using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JJFlexWpf
{
    /// <summary>
    /// Synthesized beep tones and .wav earcons for PTT warnings and UI feedback.
    /// Dual-channel architecture: separate Alert (earcons, beeps, PTT tones) and
    /// Meter (continuous meter tones) channels with independent volume and device control.
    /// Each channel has its own WaveOutEvent + MixingSampleProvider for isolation.
    /// </summary>
    public static class EarconPlayer
    {
        private static AudioChannel? _alertChannel;
        private static AudioChannel? _meterChannel;
        private static bool _initialized;

        // Volume levels tracked separately for master scaling
        private static float _masterVolumeLevel = 1.0f;
        private static float _alertVolumeLevel = 1.0f;
        private static float _meterVolumeLevel = 1.0f;

        // Device numbers
        private static int _alertDeviceNumber = -1; // -1 = Windows default
        private static int _meterDeviceNumber = -1; // -1 = same as alerts

        /// <summary>
        /// Global earcon mute. When false, all alert channel sounds (earcons, beeps, tones)
        /// are suppressed. Meter tones are NOT affected (they have their own toggle).
        /// Persisted in AudioOutputConfig.
        /// </summary>
        public static bool EarconsEnabled { get; set; } = true;

        /// <summary>
        /// The earcon categories an operator can switch off one at a time
        /// (Sprint 30, #43 — the finer controls the help page promised before
        /// they existed). Under the master <see cref="EarconsEnabled"/> gate:
        /// the master off silences everything regardless of category state.
        ///
        /// Deliberately NOT per-sound. A handful of switches an operator can
        /// hold in their head beat sixty they cannot; each public earcon
        /// method declares its family and new earcons must pick one. (This
        /// sentence said "five switches" until Sprint 31 made it six; Sprint 36
        /// briefly made it seven with a ContextHelp category whose one earcon
        /// was removed by #343 — the count now lives in the enum alone.)
        ///
        /// Outside the categories, on purpose: CW notifications (their own
        /// switch on the Audio tab), typing sounds (their own mode setting),
        /// meter tones (their own engine and toggle), and the calibration /
        /// scratchpad sounds (developer-facing).
        /// </summary>
        public enum EarconCategory
        {
            /// <summary>The connect ladder — the counting tones that climb as a
            /// connect progresses, and the double-beep at the top when it
            /// lands.</summary>
            Connection = 0,
            /// <summary>TX start/stop, hard kill, tune carrier, ATU, PTT warnings.</summary>
            Transmit = 1,
            /// <summary>Dialog open/close dings and panel expand/collapse sweeps.</summary>
            DialogsAndPanels = 2,
            /// <summary>Filter-edge clicks and sweeps, band boundary, frequency-entry dings.</summary>
            TuningAndFilters = 3,
            /// <summary>JJ-layer tones, feature on/off, mute-all, mode enter/exit, confirmations.</summary>
            CommandsAndConfirmations = 4,
            /// <summary>
            /// Something is wrong. The warning alarm, and the quieter
            /// problem-recorded tone. Added Sprint 31 (#111) once there was a
            /// second member — ProblemRecordedTone sat in
            /// CommandsAndConfirmations precisely because a sixth category for
            /// one earcon would have been a switch nobody needed.
            ///
            /// This is the one category worth thinking twice about switching
            /// off. Everything else here answers a key the operator just
            /// pressed; this fires when the app has something to say that
            /// nobody asked for.
            /// </summary>
            Warnings = 5,
        }

        // One flag per EarconCategory value, all on by default. Persisted in
        // AudioOutputConfig alongside EarconsEnabled.
        private static readonly bool[] _categoryEnabled = { true, true, true, true, true, true };

        /// <summary>Whether a category is individually enabled (master gate not considered).</summary>
        public static bool GetCategoryEnabled(EarconCategory category) =>
            _categoryEnabled[(int)category];

        /// <summary>Enable or disable one earcon category. Master gate still wins.</summary>
        public static void SetCategoryEnabled(EarconCategory category, bool enabled) =>
            _categoryEnabled[(int)category] = enabled;

        /// <summary>Master gate AND the category's own switch.</summary>
        private static bool On(EarconCategory category) =>
            EarconsEnabled && _categoryEnabled[(int)category];

        // Continuous tone providers registered with the meter channel mixer
        private static readonly List<ISampleProvider> _continuousProviders = new();

        // Cached embedded sounds (stored as mono for panning flexibility)
        private static CachedSound? _clickSound;
        private static CachedSound? _confirmSound;
        private static CachedSound? _filterEdgeMoveSound;
        private static CachedSound? _modeEnterSound;
        private static CachedSound? _modeExitSound;
        private static CachedSound? _slideSound;      // slide03.wav — filter edge drag
        private static CachedSound? _zipSound;         // zip01.wav — filter boundary hit
        private static CachedSound? _typewriterBellSound; // typewriter-bell.wav — mechanical mode Enter

        private const int SampleRate = 44100;
        /// <summary>Sample rate used by the alert mixer. Exposed for CW sample providers.</summary>
        internal const int MixerSampleRate = SampleRate;
        private const int MixerChannels = 2; // Stereo mixer for panning support

        #region Loudness tiers

        // Sprint 32 Track E, #114 / the tier half of #115. Earcons had grown in
        // two eras and each era picked its own loudness by feel: the older
        // sounds sit at 0.5 to 0.7, the newer ones at 0.2 to 0.3. That is a 6 dB
        // step with nothing behind it — not urgency, not frequency content, not
        // anything an operator could learn. Playing a dialog ding straight after
        // a hard-kill tone made the app sound like two applications.
        //
        // Three tiers now, and a sound picks one for a reason that can be said
        // in words. Every value is a peak amplitude for a tone rendered through
        // VoicedToneSampleProvider, whose equal-power normalisation makes RMS
        // depend only on this number and not on how many partials the voice
        // has — so two earcons at the same tier really are the same loudness,
        // which was never true before.
        //
        // Keep them in this ratio if they move: each tier is about 2 dB above
        // the last, close enough that no sound jumps out of the set, far enough
        // that the ordering is audible.
        //
        // FOUR tiers as of 2026-08-27 (#275). Track K deliberately declined to
        // invent a fourth one and said so; this is the case that overturned
        // that, and the reason is a MEANING the three tiers could not express.
        // (The context-help cue that founded the Faint tier was itself removed
        // by #343; the tier and its meaning stay for the next genuine OFFER.)
        // Soft is the floor for "something the operator DID", however
        // incidental. An OFFER is not that: nobody asked for it, it reports no
        // action, and acting on it is optional. Noel, hearing the context-help
        // cue for the first time: "less conspicuous." Level is not the only
        // axis that fixes that — timbre and attack do more — but it is the one
        // that can be stated in words, which is the test a tier has to pass.

        /// <summary>An OFFER: something the operator may act on if they like,
        /// which reports no action of theirs and asks for none. Nothing that
        /// answers a keypress belongs here — that is <see cref="VolumeSoft"/>
        /// at the quietest.</summary>
        internal const float VolumeFaint = 0.32f;

        /// <summary>Background acknowledgement: progress the operator did not
        /// ask about, and repeat sounds that fire many times a minute.</summary>
        internal const float VolumeSoft = 0.40f;

        /// <summary>The default. An answer to a key the operator just pressed.</summary>
        internal const float VolumeNormal = 0.50f;

        /// <summary>Arrival, failure, or something wrong. Sounds that must land
        /// even if the operator is listening to something else.</summary>
        internal const float VolumeStrong = 0.65f;

        /// <summary>
        /// Input level for the tracking band-pass noise in the expand and
        /// collapse sweeps. It is NOT a loudness tier and must not be set to
        /// one: a narrow band-pass over white noise passes only about a tenth
        /// of the energy driven into it, so this is pre-filter drive and the
        /// audible result lands well under <see cref="VolumeSoft"/>. Scaled
        /// with the tone it accompanies when the tiers changed, so the
        /// tone-to-noise balance tuned by ear on 2026-04-21 survives.
        /// </summary>
        private const float ExpandNoiseLevel = 1.1f;

        #endregion

        // Convenience accessors for the channel mixers
        private static MixingSampleProvider? AlertMixer => _alertChannel?.Mixer;

        /// <summary>
        /// Whether the alert channel has a live mixer (a real audio device is
        /// open behind it). False when render is off or channel init failed.
        /// Used by MorseNotifier and MainWindow to stamp transcript events with
        /// an honest "rendered" flag.
        /// </summary>
        internal static bool AlertChannelLive => AlertMixer != null;

        /// <summary>
        /// The #171 recording gate. Same answer as <see cref="On"/>, but the
        /// question and its answer land in the output transcript first: earcon
        /// NAME (the public method, via CallerMemberName), its category, and
        /// whether the gates let it through. The gate/rendered pair is the
        /// point - an event with gate false is "fired but gated off", an
        /// absent event is "never fired". Those sound identical (like nothing)
        /// and need different fixes.
        /// </summary>
        private static bool Gate(EarconCategory category, [CallerMemberName] string earconName = "")
            => GateCore(category, earconName, null);

        /// <summary>
        /// <see cref="Gate"/> for an earcon whose method takes an argument, so
        /// its name alone cannot say which sound just fired.
        /// </summary>
        /// <remarks>
        /// #379. <see cref="ConnectPhaseTone"/> is ONE method taking a count,
        /// and the trace recorded the bare method name — so "was that two beeps
        /// or three?" was unanswerable from every instrument this project owns,
        /// and a session filled the gap with an assumption that turned out to
        /// be wrong. Noel's ear supplied what the instrument could not. The
        /// detail rides ALONGSIDE the name and never replaces it: the name is
        /// also the id a level trim and the output transcript are keyed by, and
        /// a per-count id would scatter one sound's trim across three keys.
        ///
        /// A separate name rather than an overload on purpose. <c>Gate(cat,
        /// "count=2")</c> against an overload set would bind to the
        /// CallerMemberName parameter — silently, and the "detail" would become
        /// the earcon's id.
        /// </remarks>
        private static bool GateDetailed(EarconCategory category, string detail,
            [CallerMemberName] string earconName = "")
            => GateCore(category, earconName, detail);

        private static bool GateCore(EarconCategory category, string earconName, string? detail)
        {
            // Arm this sound's per-tier trim for the providers it is about to
            // build. Set here because Gate is the one thing every gated earcon
            // does first, and the same CallerMemberName the recorder already
            // relies on is the earcon's stable id.
            _currentTrimDb = GetLevelTrimDb(earconName);

            bool on = On(category);

            // #369 — trace the PLAY, not only the failure. Until 2026-08-28 no
            // earcon left any record when it succeeded, so "the tone was
            // skipped" and "the tone played and nobody heard it" were
            // indistinguishable from every instrument this project owns — the
            // reader-side capture records speech and braille and never sees an
            // earcon. One Verbose line, at the single point every gated earcon
            // passes through: the earcon's name, its category, whether the
            // gates let it through, and whether a live mixer exists to render
            // it. gate=True mixer=False is "fired but nothing could sound it";
            // an absent line is "never fired". Those are different defects and
            // this line is what tells them apart.
            JJTrace.Tracing.TraceLine(
                $"Earcon: {earconName} category={category} gate={on} mixer={AlertMixer != null}"
                    + (detail == null ? "" : " " + detail),
                TraceLevel.Verbose);

            if (Radios.OutputChannelRecorder.RecordEnabled)
            {
                bool rendered = on && AlertMixer != null && Radios.OutputChannelRecorder.RenderEnabled;
                Radios.OutputChannelRecorder.RecordEarcon(earconName, category.ToString(), on, rendered,
                    detail: detail);
            }
            return on;
        }
        private static MixingSampleProvider? MeterMixer => _meterChannel?.Mixer;

        /// <summary>
        /// Initialize the audio engine. Call once at startup.
        /// Creates separate Alert and Meter audio channels.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            try
            {
                // #171 silent verification channel: with render off, no
                // WaveOutEvent is ever created - no audio device opened,
                // runnable headless and on machines with no sound card. Every
                // Play* method already tolerates a null mixer, so earcons flow
                // through their normal call paths and are recorded to the
                // transcript; only the device hand-off is gone. The embedded
                // sounds ARE still loaded below: several earcons choose
                // between a wav and a synthesized fallback based on load
                // success, and a silent run must take the branch production
                // takes - exercise everything, divert only the last step.
                if (Radios.OutputChannelRecorder.RenderEnabled)
                {
                    // Create alert channel (earcons, beeps, PTT tones)
                    _alertChannel = new AudioChannel();
                    _alertChannel.Initialize(_alertDeviceNumber);

                    // Create meter channel (continuous tones from MeterToneEngine)
                    // If meter device is -1 (same as alerts), use alert device
                    _meterChannel = new AudioChannel();
                    int meterDevice = _meterDeviceNumber == -1 ? _alertDeviceNumber : _meterDeviceNumber;
                    _meterChannel.Initialize(meterDevice);

                    UpdateChannelVolumes();
                }
                else
                {
                    Trace.WriteLine("EarconPlayer: render disabled - audio channels not created, earcons diverted to transcript");
                }

                // Load embedded sounds
                _clickSound = LoadEmbeddedSound("JJFlexWpf.Sounds.click.wav");
                _confirmSound = LoadEmbeddedSound("JJFlexWpf.Sounds.confirm.wav");
                _filterEdgeMoveSound = LoadEmbeddedSound("JJFlexWpf.Sounds.filter-edge-move.wav");
                _modeEnterSound = LoadEmbeddedSound("JJFlexWpf.Sounds.mode-enter.wav");
                _modeExitSound = LoadEmbeddedSound("JJFlexWpf.Sounds.mode-exit.wav");
                _slideSound = LoadEmbeddedSound("JJFlexWpf.Sounds.slide03.wav");
                _zipSound = LoadEmbeddedSound("JJFlexWpf.Sounds.zip01.wav");
                // Typewriter bell loaded from hashed asset folder (file-based, not embedded)
                try
                {
                    var bellPath = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                        "Resources", "4f89f8bc7", "d7d8480.7605032");
                    if (System.IO.File.Exists(bellPath))
                    {
                        using var bellStream = System.IO.File.OpenRead(bellPath);
                        _typewriterBellSound = new CachedSound(bellStream);
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"EarconPlayer: failed to load typewriter bell: {ex.Message}");
                }

                _initialized = true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.Initialize failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Dispose the audio engine. Call on application shutdown.
        /// </summary>
        public static void Dispose()
        {
            _continuousProviders.Clear();
            // Drop the long-lived alert-mixer inputs too. These are held in
            // their own fields rather than in _continuousProviders (which is
            // the METER mixer's list), so clearing that list does not reach
            // them. Leaving them set would survive into the next Initialize
            // and be worse than a leak: StartBenchTone and
            // StartATUProgressEarcon both re-use a non-null provider instead
            // of creating one, so they would quietly drive a provider that is
            // no longer in any mixer, and the sound would simply never arrive.
            _benchToneProvider = null;
            _atuProgressProvider = null;
            _txToneMonitorProvider = null;
            _txToneMonitorWrapper = null;
            _alertChannel?.Dispose();
            _meterChannel?.Dispose();
            _alertChannel = null;
            _meterChannel = null;
            _initialized = false;
        }

        #region Continuous Tone Support

        /// <summary>
        /// Register a ContinuousToneSampleProvider with the meter channel mixer (panned).
        /// The provider stays in the mixer permanently — it generates silence when inactive.
        /// </summary>
        public static void RegisterContinuousTone(ContinuousToneSampleProvider provider, float pan = 0f)
        {
            if (MeterMixer == null) return;
            try
            {
                ISampleProvider stereo;
                if (Math.Abs(pan) < 0.01f)
                {
                    stereo = new MonoToStereoSampleProvider(provider);
                }
                else
                {
                    stereo = new PanningSampleProvider(provider) { Pan = pan };
                }
                MeterMixer.AddMixerInput(stereo);
                _continuousProviders.Add(stereo);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.RegisterContinuousTone failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Register an already-stereo continuous provider with the meter
        /// channel mixer. Track D2: VoicedToneSampleProvider outputs stereo
        /// with live equal-power panning applied internally, so no panning
        /// wrapper is baked in here — which is what makes pan changes take
        /// effect live rather than only at registration time.
        /// </summary>
        public static void RegisterContinuousStereo(ISampleProvider stereoProvider)
        {
            if (MeterMixer == null) return;
            try
            {
                MeterMixer.AddMixerInput(stereoProvider);
                _continuousProviders.Add(stereoProvider);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.RegisterContinuousStereo failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Remove a continuous tone from the meter channel mixer. The provider wrapper
        /// is removed from the mixer's input list.
        /// </summary>
        public static void UnregisterContinuousTone(ISampleProvider stereoWrapper)
        {
            if (MeterMixer == null) return;
            try
            {
                MeterMixer.RemoveMixerInput(stereoWrapper);
                _continuousProviders.Remove(stereoWrapper);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.UnregisterContinuousTone failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Remove a continuous tone by its inner mono provider. Finds and removes
        /// the stereo wrapper that wraps this provider.
        /// </summary>
        public static void UnregisterContinuousTone(ContinuousToneSampleProvider monoProvider)
        {
            if (MeterMixer == null) return;
            ISampleProvider? found = null;
            foreach (var wrapper in _continuousProviders)
            {
                if (wrapper is MonoToStereoSampleProvider mono && GetInnerProvider(mono) == monoProvider)
                { found = wrapper; break; }
                if (wrapper is PanningSampleProvider panned && GetInnerProvider(panned) == monoProvider)
                { found = wrapper; break; }
            }
            if (found != null)
                UnregisterContinuousTone(found);
        }

        private static ISampleProvider? GetInnerProvider(MonoToStereoSampleProvider wrapper)
        {
            try
            {
                var field = typeof(MonoToStereoSampleProvider).GetField("source",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                return field?.GetValue(wrapper) as ISampleProvider;
            }
            catch { return null; }
        }

        private static ISampleProvider? GetInnerProvider(PanningSampleProvider wrapper)
        {
            try
            {
                var field = typeof(PanningSampleProvider).GetField("source",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                return field?.GetValue(wrapper) as ISampleProvider;
            }
            catch { return null; }
        }

        /// <summary>
        /// Remove all continuous tone providers from the meter channel mixer.
        /// </summary>
        public static void UnregisterAllContinuousTones()
        {
            if (MeterMixer == null) return;
            foreach (var p in _continuousProviders)
            {
                try { MeterMixer.RemoveMixerInput(p); }
                catch { }
            }
            _continuousProviders.Clear();
        }

        #endregion

        #region Device Selection & Volume

        /// <summary>
        /// Master volume multiplier across all channels (0.0 to 1.0).
        /// Scales both alert and meter channel output.
        /// </summary>
        public static float MasterVolume
        {
            get => _masterVolumeLevel;
            set
            {
                _masterVolumeLevel = Math.Clamp(value, 0f, 1f);
                UpdateChannelVolumes();
            }
        }

        /// <summary>
        /// Alert channel volume (0.0 to 1.0). Controls earcons, beeps, PTT tones.
        /// </summary>
        public static float AlertVolume
        {
            get => _alertVolumeLevel;
            set
            {
                _alertVolumeLevel = Math.Clamp(value, 0f, 1f);
                UpdateChannelVolumes();
            }
        }

        /// <summary>
        /// Meter channel volume (0.0 to 1.0). Controls continuous meter tones.
        /// </summary>
        public static float MeterVolume
        {
            get => _meterVolumeLevel;
            set
            {
                _meterVolumeLevel = Math.Clamp(value, 0f, 1f);
                UpdateChannelVolumes();
            }
        }

        private static void UpdateChannelVolumes()
        {
            if (_alertChannel != null)
                _alertChannel.Volume = _alertVolumeLevel * _masterVolumeLevel;
            if (_meterChannel != null)
                _meterChannel.Volume = _meterVolumeLevel * _masterVolumeLevel;
        }

        /// <summary>
        /// Enumerate available audio output devices. Returns (deviceNumber, name) pairs.
        /// DeviceNumber -1 is "Windows Default".
        /// </summary>
        public static List<(int deviceNumber, string name)> GetOutputDevices()
        {
            var devices = new List<(int, string)>();
            devices.Add((-1, "Windows Default"));
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                try
                {
                    var caps = WaveOut.GetCapabilities(i);
                    devices.Add((i, caps.ProductName));
                }
                catch { }
            }
            return devices;
        }

        /// <summary>
        /// Switch the alert audio output device. Also updates meter channel if
        /// meter device is set to "Same as Alerts" (-1).
        /// </summary>
        public static void SetAlertDevice(int deviceNumber)
        {
            _alertDeviceNumber = deviceNumber;
            if (!_initialized) return;
            _alertChannel?.SetDevice(deviceNumber);

            // If meter is "same as alerts", update it too
            if (_meterDeviceNumber == -1)
                _meterChannel?.SetDevice(deviceNumber);
        }

        /// <summary>
        /// Switch the meter audio output device. Use -1 for "Same as Alerts".
        /// </summary>
        public static void SetMeterDevice(int deviceNumber)
        {
            _meterDeviceNumber = deviceNumber;
            if (!_initialized) return;
            int effectiveDevice = deviceNumber == -1 ? _alertDeviceNumber : deviceNumber;
            _meterChannel?.SetDevice(effectiveDevice);
        }

        /// <summary>
        /// Switch the audio output device. Alias for SetAlertDevice (backward compatibility).
        /// </summary>
        public static void SetOutputDevice(int deviceNumber) => SetAlertDevice(deviceNumber);

        /// <summary>Get the current alert device number (-1 = Windows default).</summary>
        public static int GetAlertDeviceNumber() => _alertDeviceNumber;

        /// <summary>Get the current meter device number (-1 = same as alerts).</summary>
        public static int GetMeterDeviceNumber() => _meterDeviceNumber;

        #endregion

        #region Public Earcon Methods

        /// <summary>
        /// A plain tone at the given frequency and duration. The general-purpose
        /// primitive — an end-stop bump, a quick attention tap, anything that
        /// wants a sound without wanting to join a family. It is NOT a member of
        /// the PTT warning family, which is the confusion this call used to
        /// cause: <see cref="Warning1Beep"/> was literally <c>Beep(800, 150)</c>,
        /// byte for byte, so the first PTT warning and a generic bump were the
        /// same sound and neither had an identity.
        /// </summary>
        public static void Beep(int frequencyHz = 800, int durationMs = 150)
        {
            PlayTone(frequencyHz, durationMs, VolumeStrong);
        }

        // ------------------------------------------------------------------
        // The PTT warning family (#118). Fired by PttSafetyController as
        // transmission runs long: Warning1, then Warning2, then OhCrap.
        //
        // These were three calls to Beep at 800, 1000 and 1200 Hz. Pitch was
        // the only thing separating them, which makes the escalation legible
        // only to an operator who heard the previous rung recently enough to
        // compare — exactly the operator who does not need telling. Worse,
        // Warning1 was indistinguishable from the generic Beep used elsewhere
        // for unrelated reasons.
        //
        // Now each rung moves on three axes at once: timbre mellow → bright →
        // metallic, pattern steady → pulsing twice → hammering, and loudness
        // up one tier per step. Any single axis identifies the rung on its own,
        // so nothing depends on the operator holding a reference in memory.
        // Pitch and duration are unchanged, so anyone who already knows the
        // family by its pitches still does.
        // ------------------------------------------------------------------

        /// <summary>First PTT warning — a nudge. Mellow, steady, 800 Hz.</summary>
        [Earcon("PTT warning 1, a nudge", EarconCategory.Transmit, Order = 11,
            Description = "First long-transmission warning. Mellow and steady.")]
        public static void Warning1Beep()
        {
            if (!Gate(EarconCategory.Transmit)) return;
            PlayVoiced(EarconVoices.WarningCalm, 800, 150, VolumeSoft);
        }

        /// <summary>Second PTT warning — insistent. Brighter, pulses twice, 1000 Hz.</summary>
        [Earcon("PTT warning 2, insistent", EarconCategory.Transmit, Order = 12,
            Description = "Second long-transmission warning. Brighter, and it pulses twice.")]
        public static void Warning2Beep()
        {
            if (!Gate(EarconCategory.Transmit)) return;
            PlayVoiced(EarconVoices.WarningInsistent, 1000, 200, VolumeNormal);
        }

        /// <summary>Last PTT warning — harsh, metallic, hammering, 1200 Hz.</summary>
        [Earcon("PTT warning 3, last call", EarconCategory.Transmit, Order = 13,
            Description = "Final long-transmission warning. Harsh, metallic, hammering.")]
        public static void OhCrapBeep()
        {
            if (!Gate(EarconCategory.Transmit)) return;
            PlayVoiced(EarconVoices.WarningUrgent, 1200, 250, VolumeStrong);
        }

        /// <summary>TX start tone — two discrete tones: 400Hz then 800Hz.</summary>
        [Earcon("Transmit start", EarconCategory.Transmit, Order = 1,
            Description = "Rising pair. You are transmitting.")]
        public static void TxStartTone()
        {
            if (!Gate(EarconCategory.Transmit)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (400, 50), (0, 20), (800, 50) }, VolumeNormal);
        }

        /// <summary>TX stop tone — two discrete tones: 800Hz then 400Hz.</summary>
        [Earcon("Transmit stop", EarconCategory.Transmit, Order = 2,
            Description = "Falling pair. You have stopped transmitting.")]
        public static void TxStopTone()
        {
            if (!Gate(EarconCategory.Transmit)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (800, 50), (0, 20), (400, 50) }, VolumeNormal);
        }

        /// <summary>Hard kill tone — two rapid descending beeps.</summary>
        [Earcon("Hard kill", EarconCategory.Transmit, Order = 3,
            Description = "Transmission was cut off rather than ended.")]
        public static void HardKillTone()
        {
            if (!Gate(EarconCategory.Transmit)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (1000, 100), (0, 30), (600, 200) }, VolumeStrong);
        }

        // ------------------------------------------------------------------
        // Countdown — one clarinet figure, two destinations (#261)
        //
        // Stages of the transmit chain check where the operator has to DO
        // something get counted in. Three identical 300 Hz dits, then a
        // landing that names what is being counted down TO. A blind operator
        // otherwise has to guess when a stage has started listening, and
        // guessing wrong costs a retake — or, on stage 3, RF.
        //
        // THREE IDENTICAL TONES RATHER THAN A DESCENDING COUNT. Descending
        // needs relative-pitch tracking: miss the first tone and two is
        // indistinguishable from three. Three identical tones are
        // self-correcting, and a landing that is not 300 Hz can never be
        // mistaken for a fourth count.
        //
        // COUNTABILITY IS THE PASS CRITERION, NOT AUDIBILITY, and the two come
        // apart. A decay long relative to the step smears three tones into one
        // warble that is perfectly audible and completely uncountable — which
        // is why the steps are 300 ms rather than the 150 the first sketch
        // used, and why the ring lives in the LANDING's own length rather than
        // in a tail.
        //
        // THE RHYTHM LIVES IN THE GAP, AND UNTIL #396 THERE WAS NO GAP (Noel,
        // walking the Fixer 2026-08-29: "they need to be played with a delay of
        // 1 second — 3, 2, 1, bing, a four second countdown, not ding ding ding
        // dinnnnng"). The dits used to ABUT: 300 + 300 + 300 and straight into
        // the landing, 1.6 seconds end to end with the first dit already
        // sounding before the operator knew a stage had started.
        //
        // The trap is that the obvious repair is the wrong one. CountdownStepMs
        // is a DURATION — how long one dit lasts — so raising it to 1000 makes
        // "dinnnnng dinnnnng dinnnnng", which is the complaint stretched rather
        // than answered. A countdown is a rhythm, and a rhythm is made of the
        // silence between events. Hence CountdownIntervalMs, and hence the dits
        // staying at 300 ms: 300 is a perfectly good dit, and it was never the
        // dit that was wrong.
        //
        // WHY THE CLARINET. Hollow zeroes the even harmonics — energy at 300,
        // 900, 1500, nothing at 600 — so the record landing's octave arrives
        // as a genuinely NEW pitch rather than as the count's own second
        // partial stepping forward, which is what a Chime would have given.
        // Sharper arrival. Its odd harmonics also sit in the
        // speech-intelligibility band, which is #115's argument for anything
        // that has to cut through a shack.
        //
        // NEVER voice these with Plain or ClassicSine. Those are #115's
        // bare-sine masking problem wearing a voice name.
        //
        // AND IT IS A SAFETY CONTROL, WHICH IS WHY THE INTERVAL IS NOT TASTE.
        // On the transmitting stages this is the only cue standing between an
        // idle stage and a live transmitter, and the operator's only window to
        // stop it. A window has to be long enough to register what is
        // happening, decide, find the control and press it — which 1.6 seconds
        // starting instantly was not. Anything that SHORTENS the interval is
        // taking that window away; treat it as a safety change, not a retune.
        // ------------------------------------------------------------------

        /// <summary>
        /// The countdown's voice: the same clarinet spectrum the CW waveform
        /// set calls "Hollow", resolved rather than re-declared so there is
        /// still one definition of it in the assembly.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A property, not a cached field, so it cannot capture a stale voice
        /// at static-initialisation time — and so the fallback below stays
        /// honest whichever order the two classes initialise in.
        /// </para>
        /// <para>
        /// Falls back to <see cref="EarconVoices.Chime"/> if the id is ever
        /// retired, because a countdown that makes no sound is worse than one
        /// with the wrong timbre: silence here reads as "the stage has not
        /// started yet" and the operator waits forever.
        /// </para>
        /// <para>
        /// <b>This deliberately does not follow the #147 voice-set switch.</b>
        /// The seven named voices have a plain counterpart each; this one does
        /// not, because its spectrum is the design. A bare sine is exactly
        /// #115's masking problem, and the transmit countdown is — until #236
        /// is settled — the only thing standing between an idle stage and a
        /// live transmitter. Worth revisiting by ear, but the safe default is
        /// that a safety cue does not get quieter or plainer on a preference.
        /// </para>
        /// </remarks>
        private static MeterVoice CountdownVoice =>
            EarconVoices.ResolveCwWaveform("Hollow").Voice ?? EarconVoices.Chime;

        /// <summary>The counting pitch. Three dits at this, then a landing
        /// derived from it.</summary>
        internal const int CountdownCountHz = 300;

        /// <summary>How long each counting dit lasts. A DURATION, not a
        /// rhythm — see <see cref="CountdownIntervalMs"/>, which is the one to
        /// reach for when the countdown feels too fast.</summary>
        internal const int CountdownStepMs = 300;

        /// <summary>
        /// The BEAT: how long from the start of one countdown event to the
        /// start of the next. One second, so the figure reads as 3, 2, 1, go.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A PERIOD rather than a gap, deliberately, because the period is what
        /// the operator perceives and what the requirement is written in. The
        /// silence is derived — <c>interval − step</c>, floored at zero — so
        /// lengthening a dit shortens its own silence and the beat stays where
        /// it is. Set the interval below the step and the dits simply abut
        /// again, which is the old sound rather than a negative duration.
        /// </para>
        /// <para>
        /// The landing gets a beat of its own: three dits at 0, 1000 and 2000,
        /// landing at 3000. Four events, four seconds, which is the shape #396
        /// asked for and the shape the two Fixer waits below are derived from.
        /// </para>
        /// </remarks>
        internal const int CountdownIntervalMs = 1000;

        /// <summary>
        /// The landing's length. The record landing is exactly this; the
        /// transmit landing splits it 2:6 across its rising pair, so both
        /// destinations are the same weight of arrival.
        /// </summary>
        internal const int CountdownLandingMs = 700;

        /// <summary>The silent gap inside the transmit landing's rising pair,
        /// carried over from <see cref="TxStartTone"/> unchanged.</summary>
        private const int CountdownTransmitGapMs = 40;

        /// <summary>
        /// Build a countdown: three counting dits a beat apart, then a landing
        /// on the fourth beat that names what is being counted down to.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The shipping earcons and the audio bench both come through
        /// here</b>, so a set of timings auditioned on the bench is the set
        /// that ships — retuning by ear cannot land on numbers the real sound
        /// does not use. That property is why the bench grew an interval box in
        /// the same change that gave the sound an interval: a bench missing one
        /// of the parameters tunes numbers the shipped sound ignores.
        /// </para>
        /// <para>
        /// <b>Every pitch is derived from <paramref name="countHz"/> so the
        /// intervals survive transposition.</b> The record landing is the
        /// octave (2x). The transmit landing is the same rising pair
        /// <see cref="TxStartTone"/> uses, which at the default count sits at
        /// 400 and 800 — a fourth up, then the octave above that. Moving the
        /// count moves the whole figure and keeps it recognisable; hard-coding
        /// the landings would break the relationship the moment anyone
        /// transposed to dodge a sidetone collision.
        /// </para>
        /// </remarks>
        /// <param name="transmit">true for the transmit landing, false for the
        /// record landing.</param>
        /// <param name="countHz">The counting pitch; every other pitch in the
        /// figure is derived from it.</param>
        /// <param name="stepMs">How long one counting dit sounds.</param>
        /// <param name="landingMs">How long the landing sounds.</param>
        /// <param name="intervalMs">The beat — start of one event to start of
        /// the next. The silence between events is this minus the step.</param>
        internal static (int freq, int ms)[] CountdownSteps(
            bool transmit,
            int countHz = CountdownCountHz,
            int stepMs = CountdownStepMs,
            int landingMs = CountdownLandingMs,
            int intervalMs = CountdownIntervalMs)
        {
            countHz = Math.Max(countHz, 1);
            stepMs = Math.Max(stepMs, 1);
            landingMs = Math.Max(landingMs, 1);

            // The silence, derived. Floored at zero so an interval shorter than
            // its own dit degrades to the abutting sound this replaced rather
            // than to a negative duration the renderer would have to guess at.
            int gapMs = Math.Max(intervalMs - stepMs, 0);

            var steps = new List<(int freq, int ms)>(8);
            for (int i = 0; i < CountdownCounts; i++)
            {
                steps.Add((countHz, stepMs));
                // Every dit gets its beat, INCLUDING the third — the landing is
                // the fourth beat of the figure, not a tail on the third.
                if (gapMs > 0) steps.Add((0, gapMs));
            }

            if (!transmit)
            {
                steps.Add((countHz * 2, landingMs));
            }
            else
            {
                steps.Add((countHz * 4 / 3, landingMs * 2 / 7));
                steps.Add((0, CountdownTransmitGapMs));
                steps.Add((countHz * 8 / 3, landingMs * 6 / 7));
            }

            return steps.ToArray();
        }

        /// <summary>How many counting dits precede the landing. Three, and the
        /// two Fixer waits below are written in terms of it.</summary>
        internal const int CountdownCounts = 3;

        /// <summary>
        /// How long a countdown lasts, end to end, at the shipping timings.
        /// </summary>
        /// <remarks>
        /// <b>Ask this rather than writing the number down.</b> Two waits in the
        /// transmit checks were hand-copied from the countdown's length and both
        /// had silently gone stale by the time #396 measured them — one still
        /// describing "three 150 ms steps and a 500 ms ring" for a sound that
        /// had been 1.6 seconds for months. A derived number cannot drift.
        /// The 15 ms fade tail is excluded: it overlaps the silence that follows
        /// rather than extending the sound.
        /// </remarks>
        internal static int CountdownDurationMs(bool transmit)
        {
            int total = 0;
            foreach (var (_, ms) in CountdownSteps(transmit)) total += ms;
            return total;
        }

        /// <summary>
        /// When the LAST counting dit begins, measured from the start of the
        /// countdown — the moment the transmit checks issue their key-up, so
        /// the landing falls on the radio's MOX confirmation.
        /// </summary>
        /// <remarks>
        /// The whole count is unkeyed before this instant, and that is the
        /// operator's abort window. It is two beats wide by construction, so it
        /// grows and shrinks with the rhythm instead of having to be remembered.
        /// </remarks>
        internal static int CountdownLastDitAtMs => (CountdownCounts - 1) * CountdownIntervalMs;

        /// <summary>
        /// Countdown into a stage that RECORDS you — the microphone check.
        /// Three dits, then the octave up: start talking.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The landing is 600 Hz held for 700 ms. It rings because the step is
        /// long, NOT because of a fade tail — <c>VoicedTailMs</c> is 15 ms of
        /// click suppression and was never a ring.
        /// </para>
        /// <para>
        /// <b>Measure the noise floor BEFORE this fires, in genuine silence,
        /// and open the speech capture AFTER it.</b> A 700 ms ring landing
        /// inside a noise-floor sample makes a quiet shack read as a noisy
        /// one; a capture opened too early clips the first half-second of the
        /// operator instead. Two windows, two purposes.
        /// </para>
        /// <para>
        /// <b>Minor pitch collision, known and accepted.</b> CW sidetone
        /// defaults to 700 Hz and is settable 400–1200, so an operator running
        /// 600 hears a landing in the same place as a dit. It is a different
        /// timbre and ten times the length, and it only fires inside a check
        /// the operator started.
        /// </para>
        /// </remarks>
        [Earcon("Countdown to record", EarconCategory.Transmit, Order = 4,
            Description = "Three tones a second apart, then a higher one on the fourth beat. "
                        + "Start talking on that last tone. Four seconds end to end, so there "
                        + "is time to get ready rather than just time to be told.")]
        public static void CountdownRecordTone()
        {
            if (!Gate(EarconCategory.Transmit)) return;
            PlayVoicedDecaySequence(CountdownVoice, CountdownSteps(transmit: false), VolumeStrong);
        }

        /// <summary>
        /// Countdown into a stage that TRANSMITS. Three dits, then the transmit
        /// start figure drawn out slow: RF is about to happen.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The landing is <see cref="TxStartTone"/> stretched</b> — the same
        /// 400-then-800 rising pair, at 200 and 600 ms instead of 50 and 50.
        /// Same figure at two speeds: slow means "now beginning", and the
        /// familiar quick one means "you are on". An operator learns one shape
        /// and reads both.
        /// </para>
        /// <para>
        /// <b>Do not play this AND <see cref="TxStartTone"/>.</b> The stretched
        /// version fires on MOX confirmation and IS the confirmation. Nothing
        /// sounds twice.
        /// </para>
        /// <para>
        /// <b>It deliberately does NOT land on the PTT warning family.</b>
        /// <see cref="Warning1Beep"/>, <see cref="Warning2Beep"/> and
        /// <see cref="OhCrapBeep"/> mean "you have been keyed too long" — a
        /// STOP message. Landing there would say "stop" at the instant
        /// transmit begins.
        /// </para>
        /// <para>
        /// <b>Count UNKEYED, key on the third tone, and let the landing fall
        /// on MOX confirmation.</b> Prepending the count to an already-keyed
        /// transmit burns dead air against the thermal budget; firing the
        /// landing before confirmation would have the operator talking into a
        /// transmitter that may never key. A radio that never keys never gets a
        /// landing.
        /// </para>
        /// <para>
        /// <b>The two beats before that third dit are the abort window</b>, and
        /// they are the whole reason #396 was a safety fix rather than a
        /// retune. Nothing is keyed during them, the stop hook is polled
        /// throughout, and the operator has two full seconds to decide. The
        /// figure now also has a designed beat between the third dit and the
        /// landing, so MOX latency rides inside that beat instead of being the
        /// only thing separating them.
        /// </para>
        /// </remarks>
        [Earcon("Countdown to transmit", EarconCategory.Transmit, Order = 5,
            Description = "Three tones a second apart, then a slow rising pair on the fourth "
                        + "beat. The radio is about to transmit, and the two seconds before "
                        + "that last count are your window to stop it.")]
        public static void CountdownTransmitTone()
        {
            if (!Gate(EarconCategory.Transmit)) return;
            PlayVoicedDecaySequence(CountdownVoice, CountdownSteps(transmit: true), VolumeStrong);
        }

        // ── The connect ladder ────────────────────────────────────────────
        //
        // COUNT says how far along. PITCH says which direction. VOLUME marks
        // arrival. Noel's spec, #379, 2026-08-28.
        //
        // WHAT THIS REPLACED, because it explains every choice below. All four
        // of these sounds were 750 Hz, 70 ms, 60 ms gap, Plain, soft — and the
        // success tone was the phase-2 tone with the volume turned up. Same
        // pitch, same durations, same gap, same voice, same count. Two sounds
        // that differ on ONE axis read as a transposition of each other, and
        // volume is the one axis an operator cannot judge in isolation, because
        // there is nothing to compare it against in the moment. Noel, the first
        // time it ever played: "If it's two different tones they sure sound
        // similar."
        //
        // NOBODY COULD HAVE FOUND IT EARLIER. ConnectSuccessTone had never
        // sounded once since it was written on 2026-08-07: the guard in front of
        // it was dead on arrival, and was only fixed the night before this
        // (#369). Its first playback was also the first evidence that it was
        // wrong. This code was believed correct for three weeks while silent.
        //
        // WHAT SURVIVED. The counting scheme, and the reason the old comment
        // gave for it: one, two, three tones "so the user hears progress as a
        // COUNT, not as a melody." That was right. What is added is a second
        // axis — the count now climbs in pitch, so low means starting and high
        // means arrived — rather than a replacement for it.
        //
        // SUCCESS SHARES ITS COUNT WITH PHASE 2 ON PURPOSE. DO NOT "FIX" IT.
        // With the phases occupying one, two and three, every count is already
        // spoken for; the only escape would be leaving the counting vocabulary
        // altogether, which throws away a scheme worth keeping. Sharing a count
        // is safe now because it no longer shares anything else — phase 2 is
        // mid-ladder and soft, success is the top of the ladder and strong.
        // Two axes apart, where they used to be one.
        //
        // THE PITCH IS CONSTANT WITHIN A GROUP AND CLIMBS ONLY BETWEEN GROUPS.
        // That is what keeps the family clear of the toggle vocabulary, where a
        // rising or falling PAIR means a feature turned on or off
        // (FeatureOnTone is 500 -> 750, FeatureOffTone 750 -> 500). A connect
        // group is a repeated note; a toggle is a step.
        //
        // THE PITCHES. 750 Hz stays exactly where it is: it is the pitch the
        // operator already associates with connecting, and on a fast connect it
        // is the only progress tone that plays. Phase 1 sits a fourth below it,
        // phase 3 a fourth above, and success a fifth above that — which puts
        // success an exact OCTAVE above phase 2, the pair heard together on
        // every fast connect. An octave is the most recognisable interval there
        // is, and "the same note, higher and louder" is about as plain a way of
        // saying arrived as sound has.
        //
        // These are NOT clear of every other pitch in the alert set, and cannot
        // be — the space between 500 and 1000 Hz is too crowded for four rungs.
        // 1000 Hz is shared with the leader-help pair and the escalate pair;
        // 560 sits between the ATU triad's 523 and the band-edge 600. Identity
        // here rests on STRUCTURE first: one voice, one repeated pitch per
        // group, counts of one, two and three, and the climb. Deliberately
        // avoided: 600 Hz (BandBoundaryBeep is a double beep there), 800 Hz
        // (ConfirmTone's fallback is a triple beep there) and 1200 Hz
        // (PlayCollapseAll opens there, at the same strong tier as success).
        private const int ConnectPhase1PitchHz = 560;
        private const int ConnectPhase2PitchHz = 750;
        private const int ConnectPhase3PitchHz = 1000;
        private const int ConnectSuccessPitchHz = 1500;

        // THE LENGTH IS THE NEXT LEVER, AND IT IS DELIBERATELY NOT PULLED YET
        // (#385). The 2026-08-19 audibility work found DURATION to be the
        // dominant variable in whether a sound survives band noise: under about
        // 50 ms is heard as a click with a pitch tint rather than a tone, and
        // the sounds that actually survived were 150 to 250 ms. These are 70 —
        // above the click threshold and well below the surviving band.
        //
        // Sprint 41 Track D left it alone ON PURPOSE while it changed the
        // pitches, because moving pitch and length together makes the ear test
        // unreadable: an improvement could not be attributed to either. That
        // restraint was right and it still is. So this stays 70 until the pitch
        // ladder has been heard on a noisy evening and found wanting.
        //
        // WHEN IT IS PULLED, change this one number and nothing else. Every
        // sound in the family reads it, including the Explorer's auditions, so
        // there is no second place to remember. ConnectSeriesAtLength plays the
        // whole ladder at any length without touching it, which is how the
        // decision gets made by ear rather than by argument.
        //
        // What to set it to, and the cost, stated honestly: 150 is the bottom
        // of the surviving band and more than doubles what is there now, which
        // takes phase 3 from 330 ms to 570 and the whole worst-case ladder from
        // about 1.1 seconds to 1.8. At 250 phase 3 becomes 810 ms. A connect is
        // already talking, so every millisecond here competes with speech for
        // the same stretch — which is the argument for starting at 150 and
        // going further only if it is still buried.
        //
        // VOLUME IS NOT THE LEVER. It is already carrying the arrival's
        // emphasis, and leaning on it twice is exactly how the success tone
        // ended up being phase 2 with the gain turned up (#379).
        private const int ConnectPhaseToneMs = 70;

        /// <summary>The silence inside a count group. Not the beat of a
        /// countdown — this one is meant to be heard as a repeated note, not
        /// as separate events, so it stays short when the tone length
        /// moves.</summary>
        private const int ConnectPhaseToneGapMs = 60;
        private const float ConnectPhaseToneVolume = VolumeSoft;

        /// <summary>
        /// The rung of the ladder a phase sounds at. Clamped rather than
        /// indexed: the connecting window publishes phases 2 and 3 today, and a
        /// fourth phase added later should land on the top progress rung rather
        /// than throw on the audio path.
        /// </summary>
        private static int ConnectPhasePitchHz(int phase) => phase switch
        {
            <= 1 => ConnectPhase1PitchHz,
            2 => ConnectPhase2PitchHz,
            _ => ConnectPhase3PitchHz,
        };

        /// <summary>
        /// N tones at one pitch — the shape every rung of the ladder shares.
        /// </summary>
        /// <remarks>
        /// One definition, so the three named phase earcons the Earcon Explorer
        /// auditions cannot drift from the parameterised one the connecting
        /// window actually calls. They had not drifted, but they were four
        /// hand-written copies of the same three numbers, which is how a bench
        /// ends up auditioning a sound the application no longer makes.
        /// </remarks>
        private static void PlayConnectCount(int count, int pitchHz, float volume,
            int toneMs = ConnectPhaseToneMs)
        {
            if (count <= 0) return;
            PlayVoicedSequence(EarconVoices.Plain,
                               ConnectCountSteps(count, pitchHz, toneMs), volume);
        }

        /// <summary>
        /// The steps of one rung — N tones at one pitch. Split out so the #385
        /// length audition plays the SHIPPED figure at a different length
        /// rather than a hand-written imitation of it.
        /// </summary>
        private static (int freq, int ms)[] ConnectCountSteps(int count, int pitchHz, int toneMs)
        {
            if (count <= 0) return Array.Empty<(int, int)>();
            var seq = new (int, int)[count * 2 - 1];
            int idx = 0;
            for (int i = 0; i < count; i++)
            {
                if (i > 0) seq[idx++] = (0, ConnectPhaseToneGapMs);
                seq[idx++] = (pitchHz, toneMs);
            }
            return seq;
        }

        /// <summary>
        /// Connect phase 1 — one tone at the bottom of the ladder.
        /// </summary>
        /// <remarks>
        /// Audition-only in practice. <c>ConnectNarration</c> starts at phase 1
        /// and only announces TRANSITIONS, so nothing on a live connect calls
        /// this; the first tone an operator hears is phase 2. Kept because the
        /// bottom rung is what makes the ladder legible on the bench, and
        /// because the day the connecting window does announce its opening
        /// phase, the sound for it should already exist and already fit.
        /// </remarks>
        [Earcon("Connect step 1", EarconCategory.Connection, Order = 1,
            Description = "One tone at 560 hertz, the bottom of the connect ladder. The opening "
                        + "stage — which the connecting window treats as where it starts rather "
                        + "than something to announce, so you hear this rung here more than on "
                        + "a real connect.")]
        public static void ConnectPhase1Tone()
        {
            if (!Gate(EarconCategory.Connection)) return;
            PlayConnectCount(1, ConnectPhase1PitchHz, ConnectPhaseToneVolume);
        }

        /// <summary>Connect phase 2 — two tones at 750 Hz (transport up, waiting for slice).</summary>
        [Earcon("Connect step 2", EarconCategory.Connection, Order = 2,
            Description = "Two tones at 750 hertz, a fourth up from step 1. Transport is up, "
                        + "waiting for a slice. On a fast connect this is the only progress "
                        + "tone you get.")]
        public static void ConnectPhase2Tone()
        {
            if (!Gate(EarconCategory.Connection)) return;
            PlayConnectCount(2, ConnectPhase2PitchHz, ConnectPhaseToneVolume);
        }

        /// <summary>Connect phase 3 — three tones at 1000 Hz (slice acquired, station name pending).</summary>
        [Earcon("Connect step 3", EarconCategory.Connection, Order = 3,
            Description = "Three tones at 1000 hertz, a fourth up again. Slice acquired, "
                        + "station name pending.")]
        public static void ConnectPhase3Tone()
        {
            if (!Gate(EarconCategory.Connection)) return;
            PlayConnectCount(3, ConnectPhase3PitchHz, ConnectPhaseToneVolume);
        }

        /// <summary>
        /// Connect success — the signature double-beep (QB Track A,
        /// 2026-08-07, memory: project_connect_earcon_signature_sound.md).
        /// The top of the ladder: two tones at 1500 Hz, an exact octave above
        /// the phase-2 pair, and the only one of the four at a strong tier.
        /// Fired from MainWindow.PowerNowOn — the one point every successful
        /// connect path (picker local, picker remote, auto-connect, reconnect)
        /// flows through — so fast LAN connects are not silent (the phase tones
        /// skip any phase under 500 ms).
        ///
        /// Two tones, the same count as phase 2, deliberately. The block
        /// comment above the pitch constants says why; read it before changing
        /// this to three.
        /// </summary>
        [Earcon("Connect success", EarconCategory.Connection, Order = 4,
            Description = "Two tones at 1500 hertz, an octave above step 2 and the only loud one "
                        + "of the four. Every successful connect ends here, however it started.")]
        public static void ConnectSuccessTone()
        {
            if (!Gate(EarconCategory.Connection)) return;
            PlayConnectCount(2, ConnectSuccessPitchHz, VolumeStrong);
        }

        /// <summary>
        /// The connect rung for one phase — the method the connecting window
        /// actually calls. The argument is the phase number, and it decides
        /// BOTH how many tones sound and which rung they sound at.
        /// </summary>
        public static void ConnectPhaseTone(int count)
        {
            int pitch = ConnectPhasePitchHz(count);
            // The count goes in the trace. Until #379 this recorded the bare
            // method name, so "two beeps or three?" could not be answered from
            // any instrument this project owns — and a session answered it with
            // an assumption instead. The name stays the method's, because it is
            // also the level-trim and transcript id.
            if (!GateDetailed(EarconCategory.Connection,
                    $"count={count} pitchHz={pitch}")) return;
            if (count <= 0) return;
            PlayConnectCount(count, pitch, ConnectPhaseToneVolume);
        }

        // ------------------------------------------------------------------
        // #144 — the connect series still sounds like the old sounds
        //
        // Everything else in the alert path changed instrument in Sprint 32.
        // The connect series did not, and there is a reason it feels like an
        // oversight rather than a decision: it IS the plain near-sine it always
        // was, because EarconVoices.Plain was authored specifically as a
        // like-for-like replacement for the bare SignalGenerator sine. The
        // series was ported and never re-voiced.
        //
        // These are AUDITION CANDIDATES, not a change. They reach the operator
        // only through the Earcon Explorer, and they exist so a decision can be
        // made by ear instead of by description. Every one of them plays the
        // SHIPPED STRUCTURE — one tone, two, three, then the success pair, on
        // the pitch ladder — because that structure is the decided part. What
        // varies is the instrument.
        //
        // Candidate D is GONE as of #379, and its absence is the point. D
        // existed because "connect success" was the phase-2 tone at a louder
        // tier — the sound for ARRIVED was the sound for STILL WORKING played
        // harder — and D gave arrival its own shape by rising on the second
        // note. The shipped series now gives arrival its own rung, so D was a
        // candidate advertising a fix for something already fixed, which is
        // exactly the kind of label this project keeps getting caught by.
        //
        // Delete this method and its catalog entries once an instrument is
        // chosen. The timbre question is still open; only D closed.
        // ------------------------------------------------------------------

        /// <summary>
        /// The whole connect ladder at a chosen tone length — the #385
        /// audition. Not wired to any connect path; the Earcon Explorer is the
        /// only caller, and nothing here changes what ships.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A separate question from the #144 candidates, so it gets a
        /// separate method.</b> Those vary the INSTRUMENT and hold everything
        /// else at the shipped values; this varies the LENGTH and holds the
        /// instrument. Folding one into the other would put two questions in
        /// one vocabulary and neither answer would mean anything.
        /// </para>
        /// <para>
        /// It plays through <see cref="ConnectCountSteps"/> — the same builder
        /// the connect path uses — so what is auditioned is the shipped figure
        /// at a different length and not an imitation of it. That property is
        /// the whole reason #379's collapse of four hand-written copies into
        /// one definition was worth doing.
        /// </para>
        /// <para>
        /// Judge it on the FAST-CONNECT case, which is the case operators
        /// actually get: phase 2 alone, then the arrival. The full ladder only
        /// sounds when a connect is slow enough for the 500 ms threshold to let
        /// the other rungs through.
        /// </para>
        /// </remarks>
        /// <param name="toneMs">How long each tone lasts. The shipped value is
        /// 70; the field work's surviving band is 150 to 250.</param>
        public static void ConnectSeriesAtLength(int toneMs)
        {
            NoTrim();
            if (!EarconsEnabled) return;

            toneMs = Math.Clamp(toneMs, 20, 600);

            // The same spacing the #144 candidates use, so the two auditions
            // are comparable to each other as well as to the shipped sound.
            const int groupGap = 400;

            var v = EarconVoices.Plain;
            int at = 0;

            void Rung(int count, int pitchHz, float volume)
            {
                var steps = ConnectCountSteps(count, pitchHz, toneMs);
                if (at == 0) PlayVoicedSequence(v, steps, volume);
                else PlayLaterVoiced(v, steps, volume, at);

                int len = 0;
                foreach (var (_, ms) in steps) len += ms;
                at += len + groupGap;
            }

            // One, two, three, then the arrival at its own louder tier — the
            // arrival is scheduled separately rather than folded in because one
            // render carries one level.
            Rung(1, ConnectPhase1PitchHz, ConnectPhaseToneVolume);
            Rung(2, ConnectPhase2PitchHz, ConnectPhaseToneVolume);
            Rung(3, ConnectPhase3PitchHz, ConnectPhaseToneVolume);
            Rung(2, ConnectSuccessPitchHz, VolumeStrong);
        }

        /// <summary>
        /// Play one connect-series audition candidate (#144). Not wired to any
        /// connect path — the Earcon Explorer is the only caller.
        /// </summary>
        /// <param name="candidate">'A' through 'C'; anything else is ignored.</param>
        public static void ConnectSeriesCandidate(char candidate)
        {
            // A parameterised audition, not a named earcon, so there is no id
            // to look a trim up by — and it must not inherit the trim of
            // whatever played before it on this thread.
            NoTrim();
            if (!EarconsEnabled) return;

            // The ladder, so a candidate auditions the instrument and nothing
            // else. Reading these from the same constants the shipped earcons
            // use is the whole reason a candidate stays honest when the pitches
            // move again.
            int p1 = ConnectPhase1PitchHz;
            int p2 = ConnectPhase2PitchHz;
            int p3 = ConnectPhase3PitchHz;
            int ps = ConnectSuccessPitchHz;
            int ms = ConnectPhaseToneMs;
            int gap = ConnectPhaseToneGapMs;

            // The whole series back to back — one, two, three, then success —
            // with a longer gap between the groups so the count stays legible.
            // Judging a connect sound one tone at a time is judging the wrong
            // thing; what matters is whether the four read as a family.
            //
            // Only A reproduces the arrival's louder tier, because one render
            // carries one level and A pays for a second render to get it. In B
            // and C the arrival is at the phase tier — they answer "what does
            // this instrument sound like", not "is the arrival loud enough".
            const int groupGap = 400;

            switch (char.ToUpperInvariant(candidate))
            {
                case 'A': // Chime — struck, with a ringing tail. Arrival as a bell.
                    PlayVoicedSequence(EarconVoices.Chime, new[]
                    {
                        (p1, ms), (0, groupGap),
                        (p2, ms), (0, gap), (p2, ms), (0, groupGap),
                        (p3, ms), (0, gap), (p3, ms), (0, gap), (p3, ms),
                    }, ConnectPhaseToneVolume);
                    // The six phase tones above run 6·ms with three inter-tone
                    // gaps and two group gaps; one more group gap puts the
                    // success pair where the other candidates put theirs.
                    PlayLaterVoiced(EarconVoices.Chime, new[] { (ps, ms), (0, gap), (ps, ms) },
                        VolumeStrong, 6 * ms + 3 * gap + 3 * groupGap);
                    break;

                case 'B': // Press — struck and dry. A mechanical handshake.
                    PlayVoicedDecaySequence(EarconVoices.Press, new[]
                    {
                        (p1, ms), (0, groupGap),
                        (p2, ms), (0, gap), (p2, ms), (0, groupGap),
                        (p3, ms), (0, gap), (p3, ms), (0, gap), (p3, ms), (0, groupGap),
                        (ps, ms), (0, gap), (ps, ms),
                    }, ConnectPhaseToneVolume);
                    break;

                case 'C': // Hollow — odd harmonics, woody. Nothing on the band
                          // sounds like a clarinet, which is the entire point.
                    PlayVoicedSequence(ConnectCandidateHollow, new[]
                    {
                        (p1, ms), (0, groupGap),
                        (p2, ms), (0, gap), (p2, ms), (0, groupGap),
                        (p3, ms), (0, gap), (p3, ms), (0, gap), (p3, ms), (0, groupGap),
                        (ps, ms), (0, gap), (ps, ms),
                    }, ConnectPhaseToneVolume);
                    break;
            }
        }

        /// <summary>
        /// Odd harmonics with a steep rolloff — the clarinet-ish spectrum the
        /// meter alphabet calls Hollow. Local to the audition because it is a
        /// candidate, not yet a word in the alert vocabulary; if C wins it
        /// moves into EarconVoices under a name.
        /// </summary>
        private static readonly MeterVoice ConnectCandidateHollow = new MeterVoice
        {
            Name = "Connect Candidate Hollow",
            Partials = new[] { 1f, 0f, 0.45f, 0f, 0.28f, 0f, 0.18f, 0f, 0.12f },
            AttackMs = 6f,
            SustainLevel = 1f,
        };

        /// <summary>
        /// Play a voiced cadence after a delay, on a timer rather than by
        /// padding the sequence with silence.
        ///
        /// Candidate A needs its success pair at a LOUDER tier than the phase
        /// tones that precede it, and RenderVoiced applies one volume to a whole
        /// sequence — so the two halves cannot be one render. Used by the
        /// audition only; nothing on a real connect path needs it, because
        /// there the four events are genuinely separate in time.
        /// </summary>
        private static void PlayLaterVoiced(MeterVoice voice, (int freq, int ms)[] steps,
            float volume, int delayMs)
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(delayMs).ConfigureAwait(false);
                    PlayVoicedSequence(voice, steps, volume);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"EarconPlayer.PlayLaterVoiced failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// A press, a ding and a warning nudge back to back — the quickest way
        /// to hear what the alert tone set setting (#147) actually changes.
        ///
        /// Outside the six family switches on purpose, like the other bench
        /// sounds: it is an instrument for judging the sounds, not one of them,
        /// and it would be perverse for a preview of the warning voice to go
        /// silent because the operator has warnings switched off.
        /// </summary>
        [Earcon("Alert tone set sampler",
            Description = "A press, a ding and a warning nudge back to back, in whichever "
                        + "alert tone set is currently selected.")]
        public static void VoiceSetSampler()
        {
            if (!BenchGate()) return;
            PlayVoicedDecay(EarconVoices.Press, 800, 60, VolumeNormal);
            PlayLaterVoiced(EarconVoices.Chime, new[] { (1000, 160) }, VolumeNormal, 220);
            PlayLaterVoiced(EarconVoices.WarningCalm, new[] { (800, 150) }, VolumeSoft, 560);
        }

        /// <summary>Play a frequency sweep (chirp) from startHz to endHz.</summary>
        public static void Chirp(int startHz, int endHz, int durationMs)
        {
            PlayChirp(startHz, endHz, durationMs, 0.6f);
        }

        /// <summary>Confirmation tone — plays confirm.wav.</summary>
        [Earcon("Confirmation", EarconCategory.CommandsAndConfirmations, Order = 1,
            Description = "The action you asked for went through.")]
        public static void ConfirmTone()
        {
            if (!Gate(EarconCategory.CommandsAndConfirmations)) return;
            if (_confirmSound != null)
                PlayCachedSound(_confirmSound);
            else
                PlayVoicedDecaySequence(EarconVoices.Press,
                    new[] { (800, 25), (0, 30), (800, 25), (0, 30), (800, 25) }, VolumeNormal);
        }

        /// <summary>Typewriter bell — plays at end of frequency entry in mechanical keyboard mode.</summary>
        [Earcon("Typewriter bell", EarconCategory.TuningAndFilters, Order = 2,
            Description = "End of frequency entry, in mechanical keyboard mode.")]
        public static void TypewriterBellTone()
        {
            if (!Gate(EarconCategory.TuningAndFilters)) return;
            if (_typewriterBellSound != null)
                PlayCachedSound(_typewriterBellSound);
            else
                DingTone(); // fallback
        }

        /// <summary>Band boundary beep — 600 Hz double-beep.</summary>
        [Earcon("Band edge", EarconCategory.TuningAndFilters, Order = 3,
            Description = "Tuning has reached the edge of the band.")]
        public static void BandBoundaryBeep()
        {
            if (!Gate(EarconCategory.TuningAndFilters)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (600, 50), (0, 30), (600, 50) }, VolumeNormal);
        }

        /// <summary>Filter edge enter tone — plays mode-enter.wav.</summary>
        [Earcon("Filter edge adjust, entering", EarconCategory.TuningAndFilters, Order = 11,
            Description = "Filter edge adjustment mode has started.")]
        public static void FilterEdgeEnterTone()
        {
            if (!Gate(EarconCategory.TuningAndFilters)) return;
            if (_modeEnterSound != null)
                PlayCachedSound(_modeEnterSound);
            else
                PlayVoicedDecay(EarconVoices.Chime, 1000, 80, VolumeNormal);
        }

        /// <summary>Filter edge exit tone — plays mode-exit.wav.</summary>
        [Earcon("Filter edge adjust, leaving", EarconCategory.TuningAndFilters, Order = 12,
            Description = "Filter edge adjustment mode has ended.")]
        public static void FilterEdgeExitTone()
        {
            if (!Gate(EarconCategory.TuningAndFilters)) return;
            if (_modeExitSound != null)
                PlayCachedSound(_modeExitSound);
            else
                PlayVoicedDecay(EarconVoices.Chime, 600, 80, VolumeNormal);
        }

        /// <summary>
        /// Filter edge move tone — panned click on each filter edge adjustment.
        /// Left edge pans left, right edge pans right.
        /// </summary>
        /// <param name="isLowEdge">True for low/left edge, false for high/right edge.</param>
        public static void FilterEdgeMoveTone(bool isLowEdge)
        {
            if (!Gate(EarconCategory.TuningAndFilters)) return;
            float pan = isLowEdge ? -0.7f : 0.7f;
            if (_slideSound != null)
                PlayCachedSoundPanned(_slideSound, pan);
            else if (_filterEdgeMoveSound != null)
                PlayCachedSoundPanned(_filterEdgeMoveSound, pan);
            else
                PlayVoicedDecay(EarconVoices.Press, 800, 20, VolumeSoft, pan);
        }

        /// <summary>
        /// Filter edge move tone — unpanned (for when edge isn't known).
        /// </summary>
        [Earcon("Filter edge move, edge unknown", EarconCategory.TuningAndFilters, Order = 33,
            Description = "The unpanned fallback, used when the code does not know which edge moved.")]
        public static void FilterEdgeMoveTone()
        {
            if (!Gate(EarconCategory.TuningAndFilters)) return;
            if (_slideSound != null)
                PlayCachedSound(_slideSound);
            else if (_filterEdgeMoveSound != null)
                PlayCachedSound(_filterEdgeMoveSound);
            else
                PlayVoicedDecay(EarconVoices.Press, 800, 20, VolumeSoft);
        }

        /// <summary>
        /// Filter boundary hit — zip sound panned to the edge that hit the boundary.
        /// Right boundary: zip01 forward (ascending) panned right.
        /// Left boundary: zip01 reversed (descending) panned left.
        /// </summary>
        /// <param name="isLowEdge">True for low/left boundary, false for high/right boundary.</param>
        public static void FilterBoundaryHitTone(bool isLowEdge)
        {
            if (!Gate(EarconCategory.TuningAndFilters)) return;
            float pan = isLowEdge ? -0.8f : 0.8f;
            if (_zipSound != null)
            {
                if (isLowEdge)
                    PlayCachedSoundReversedPanned(_zipSound, pan);
                else
                    PlayCachedSoundPanned(_zipSound, pan);
            }
            else
            {
                PlayVoicedDecay(EarconVoices.Press, isLowEdge ? 400 : 800, 80, VolumeNormal, pan);
            }
        }

        /// <summary>
        /// Filter squeeze tone — edges closing in. Single descending sweep 800→200Hz, 300ms.
        /// </summary>
        [Earcon("Filter squeeze", EarconCategory.TuningAndFilters, Order = 51,
            Description = "The passband is closing in. One descending sweep.")]
        public static void FilterSqueezeTone()
        {
            if (!Gate(EarconCategory.TuningAndFilters)) return;
            if (AlertMixer == null) return;
            try
            {
                const int durationMs = 300;
                var down = new ChirpSampleProvider(SampleRate, 800, 200, durationMs, VolumeNormal);
                AddToMixer(down);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.FilterSqueezeTone failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Filter stretch/pull tone — edges opening up. Ascending sweep 200→800Hz
        /// with a second tone 100Hz above, 300ms. The interval gives a "spreading" feel.
        /// </summary>
        [Earcon("Filter stretch", EarconCategory.TuningAndFilters, Order = 52,
            Description = "The passband is opening up. Two ascending sweeps a note apart.")]
        public static void FilterStretchTone()
        {
            if (!Gate(EarconCategory.TuningAndFilters)) return;
            if (AlertMixer == null) return;
            try
            {
                const int durationMs = 300;
                // Primary ascending sweep
                var primary = new ChirpSampleProvider(SampleRate, 200, 800, durationMs, VolumeNormal);
                AddToMixer(primary);
                // Secondary tone 100Hz above — same sweep shifted up
                var secondary = new ChirpSampleProvider(SampleRate, 300, 900, durationMs, VolumeSoft);
                AddToMixer(secondary);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.FilterStretchTone failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reverse boom — ascending sweep with layered harmonics.
        /// Sounds like a rewind/implosion. Used for calibration reset.
        /// </summary>
        [Earcon("Reverse boom",
            Description = "Calibration reset. Outside the family switches on purpose, along with "
                        + "the other calibration and bench sounds.")]
        public static void ReverseBoomTone()
        {
            if (!BenchGate() || AlertMixer == null) return;
            try
            {
                // Low sweep: 80Hz → 800Hz over 400ms (the "whoosh")
                var low = new ChirpSampleProvider(SampleRate, 80, 800, 400, 0.5f);
                AddToMixer(low);
                // Mid sweep: 200Hz → 1200Hz over 300ms (harmonic layer, slightly shorter)
                var mid = new ChirpSampleProvider(SampleRate, 200, 1200, 300, 0.3f);
                AddToMixer(mid);
                // High click at the end: short 1500Hz burst (the "snap")
                var click = new SignalGenerator(SampleRate, 1)
                {
                    Type = SignalGeneratorType.Sin,
                    Frequency = 1500,
                    Gain = 0.4f
                };
                // Delay the click by 350ms then play for 50ms
                var silence = new SilenceProvider(new WaveFormat(SampleRate, 1)).ToSampleProvider().Take(TimeSpan.FromMilliseconds(350));
                var clickTimed = click.Take(TimeSpan.FromMilliseconds(50));
                var clickDelayed = new OffsetSampleProvider(clickTimed) { DelayBySamples = (int)(SampleRate * 0.35) };
                AddToMixer(clickDelayed);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.ReverseBoomTone failed: {ex.Message}");
            }
        }

        /// <summary>Rising chirp — entering leader key mode.</summary>
        [Earcon("JJ key pressed", EarconCategory.CommandsAndConfirmations, Order = 21,
            Description = "The JJ leader layer is listening for the next key.")]
        public static void LeaderEnterTone()
        {
            if (!Gate(EarconCategory.CommandsAndConfirmations)) return;
            PlayChirp(400, 600, 80, VolumeNormal);
        }

        // ------------------------------------------------------------------
        // The confirmation pair (#114) — and why it felt unresolved
        //
        // These were 500 Hz then 700 Hz. 700/500 is 1.4, which is 583 cents; a
        // TRITONE is 600. So the confirmation tone was already a tritone,
        // seventeen cents flat of one — close enough that the ear hears one.
        //
        // That is very likely the real diagnosis of "bland", and it is not a
        // lack of richness. The tritone is the most unstable interval in
        // Western music: it does not resolve, it hangs. Psychoacoustically
        // that is exactly wrong for "this succeeded", where the operator wants
        // a settled arrival and instead got a suspension. It would make an
        // excellent WARNING interval, which is plausibly why the alarm has
        // character and this did not.
        //
        // 500 -> 750 is a perfect fifth (702 cents) and lands. It is a
        // one-number change: same length, same cadence, same loudness tier, on
        // the sound the application plays more than any other — after the #128
        // sweep these fire from roughly two dozen more places than they used
        // to. An extra hundred milliseconds on THIS sound is not free, and a
        // sound with more character can tire faster than a plain one.
        //
        // NOEL'S THREE-NOTE PROPOSAL IS BUILT AND NOT SHIPPING, pending ears —
        // see FeatureOnToneThreeNoteCandidate below. Three notes give the
        // tension somewhere to resolve TO, which is a real argument. Two
        // arguments run the other way and both were found while building it:
        // the duration cost above, and the count collision below.
        //
        // THE COUNT COLLISION, which is the finding worth Noel's attention.
        // MuteAllOnTone is ALREADY a three-note triad, and counting is the
        // whole of what separates "this slice" from "all my slices" — 625/785/
        // 940 against 500/700. Count survives masking in a way timbre does
        // not: "how many beeps was that" stays readable through band noise,
        // cheap speakers and poor signal-to-noise, because counting is a
        // temporal judgement rather than a spectral one. Making the
        // single-slice confirmation three notes spends the most robust axis in
        // the vocabulary to fix an interval, and the interval can be fixed for
        // free instead. Distinguishing by NUMBER and CONTOUR rather than by
        // TIMBRE deserves to be a principle for the whole set.
        //
        // Both candidates are auditionable side by side in the Earcon
        // Explorer. Judge against real band noise, not a quiet room. When it
        // is decided, delete the loser.
        // ------------------------------------------------------------------

        /// <summary>Rising pair, a perfect fifth — feature toggled ON.</summary>
        [Earcon("Feature on", EarconCategory.CommandsAndConfirmations, Order = 11,
            Description = "Rising pair. A toggle just turned on.")]
        public static void FeatureOnTone()
        {
            if (!Gate(EarconCategory.CommandsAndConfirmations)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (500, 60), (0, 40), (750, 60) }, VolumeNormal);
        }

        /// <summary>Falling pair, the mirror — feature toggled OFF.</summary>
        [Earcon("Feature off", EarconCategory.CommandsAndConfirmations, Order = 12,
            Description = "Falling pair. A toggle just turned off.")]
        public static void FeatureOffTone()
        {
            if (!Gate(EarconCategory.CommandsAndConfirmations)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (750, 60), (0, 40), (500, 60) }, VolumeNormal);
        }

        /// <summary>
        /// CANDIDATE, not shipping: Noel's three-note confirmation. States the
        /// tension and then settles it — 500 up to the tritone at 700, then a
        /// semitone up to 750 to land.
        /// </summary>
        /// <remarks>
        /// Outside the family switches, along with the other bench sounds, so
        /// auditioning it cannot be silenced by a category an operator turned
        /// off. Roughly 260 ms against the shipping pair's 160. <b>Delete this
        /// and its mirror once the comparison is made</b> — a candidate that
        /// outlives its decision is just clutter in the Explorer.
        /// </remarks>
        [Earcon("Feature on, three-note candidate",
            Description = "Candidate for the confirmation tone (#114): three notes that state "
                        + "a tension and resolve it. Compare against Feature on.")]
        public static void FeatureOnToneThreeNoteCandidate()
        {
            if (!BenchGate()) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (500, 60), (0, 40), (700, 60), (0, 40), (750, 80) }, VolumeNormal);
        }

        /// <summary>CANDIDATE, not shipping: the mirror of the three-note
        /// confirmation. Delete with it.</summary>
        [Earcon("Feature off, three-note candidate",
            Description = "Candidate mirror for the confirmation tone (#114). Compare against "
                        + "Feature off.")]
        public static void FeatureOffToneThreeNoteCandidate()
        {
            if (!BenchGate()) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (750, 60), (0, 40), (700, 60), (0, 40), (500, 80) }, VolumeNormal);
        }

        /// <summary>
        /// A toggle just changed state: rising for on, falling for off. The one
        /// call every operator-facing toggle should make.
        ///
        /// Sprint 32 Track E, #128. FeatureOnTone and FeatureOffTone existed and
        /// were called from fifty-odd places, always as the same if-else written
        /// out again. Writing it out again is how a road gets missed: a toggle
        /// reachable by a hotkey, a menu item and a settings checkbox needs the
        /// tone on all three, and the ones that got forgotten were the roads
        /// nobody was thinking about while adding the other two. PC audio on and
        /// off made no sound at all, by any road, which is what surfaced this.
        ///
        /// Pass the state the toggle ENDED UP IN, not the state it was in — and
        /// read it back from wherever the truth lives rather than assuming the
        /// flip succeeded. Several toggles in this application can decline: PC
        /// audio refuses to come on when no audio devices are configured, and
        /// a tone claiming otherwise is worse than silence.
        /// </summary>
        public static void ToggleTone(bool isOn)
        {
            if (isOn) FeatureOnTone(); else FeatureOffTone();
        }

        /// <summary>
        /// The same, for an action that affects every slice at once. Pitched a
        /// third above the single-slice tones so "all of them" and "this one"
        /// are separable by ear.
        /// </summary>
        public static void ToggleAllTone(bool isOn)
        {
            if (isOn) MuteAllOnTone(); else MuteAllOffTone();
        }

        /// <summary>
        /// Tri-tone ascending — multi-slice mute-all or release-all (any
        /// "affects all my slices at once" action). Pitched roughly a major
        /// third above the single-slice FeatureOnTone so the user can tell
        /// "affects all slices" from "affects one slice" by ear.
        /// </summary>
        [Earcon("All slices on", EarconCategory.CommandsAndConfirmations, Order = 13,
            Description = "Rising triad, a third above the single-slice tone. Affects every slice.")]
        public static void MuteAllOnTone()
        {
            if (!Gate(EarconCategory.CommandsAndConfirmations)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (625, 55), (0, 30), (785, 55), (0, 30), (940, 55) }, VolumeNormal);
        }

        /// <summary>
        /// Tri-tone descending — multi-slice unmute-all. Mirror of MuteAllOnTone.
        /// </summary>
        [Earcon("All slices off", EarconCategory.CommandsAndConfirmations, Order = 14,
            Description = "Falling triad. The mirror of all-slices-on.")]
        public static void MuteAllOffTone()
        {
            if (!Gate(EarconCategory.CommandsAndConfirmations)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (940, 55), (0, 30), (785, 55), (0, 30), (625, 55) }, VolumeNormal);
        }

        /// <summary>Double ascending ding — dialog/popup opened.</summary>
        [Earcon("Dialog opened", EarconCategory.DialogsAndPanels, Order = 1,
            Description = "Rising pair when a dialog or popup opens.")]
        public static void DialogOpenTone()
        {
            if (!Gate(EarconCategory.DialogsAndPanels)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (600, 50), (0, 30), (900, 50) }, VolumeSoft);
        }

        /// <summary>Double descending ding — dialog/popup closed.</summary>
        [Earcon("Dialog closed", EarconCategory.DialogsAndPanels, Order = 2,
            Description = "Falling pair when a dialog or popup closes.")]
        public static void DialogCloseTone()
        {
            if (!Gate(EarconCategory.DialogsAndPanels)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (900, 50), (0, 30), (600, 50) }, VolumeSoft);
        }

        /// <summary>
        /// Two notes falling a minor third — a problem was recorded and can be
        /// read with Ctrl+J, Ctrl+R (Sprint 31, #100).
        ///
        /// Family: Warnings, alongside WarningAlarmTone. It lived in
        /// CommandsAndConfirmations until Sprint 31 #111, for the honest reason
        /// that a sixth category holding exactly one earcon is a switch nobody
        /// needs. The alarm gave it a sibling, so the category now earns its
        /// place and both "something is wrong" sounds answer to one switch.
        ///
        /// Deliberately calm rather than alarming, and that contrast is the
        /// point now that the two share a family. Nothing here is urgent — the
        /// failure has already happened, the log already has it, and the list
        /// will still be there in an hour. WarningAlarmTone is for the other
        /// kind: something is wrong *now* and the next thing you do depends on
        /// knowing it.
        /// </summary>
        [Earcon("Problem recorded", EarconCategory.Warnings, Order = 2,
            Description = "Deliberately calm. Something failed, it is already logged, and the "
                        + "list will still be there in an hour.")]
        public static void ProblemRecordedTone()
        {
            if (!Gate(EarconCategory.Warnings)) return;
            // 90 + 50 + 130. #116: warnings duck the band audio for their own
            // length, and the duration is stated here rather than guessed at,
            // because the request is a deadline and a wrong one is audible.
            RxDuck.RequestFor(270);
            PlayVoicedSequence(EarconVoices.Plain,
                new[] { (440, 90), (0, 50), (370, 130) }, VolumeNormal);
        }

        /// <summary>
        /// The warning alarm: 800 Hz for 750 ms with its 2nd and 3rd harmonics
        /// stacked underneath at falling gain — Noel's specification, 2026-08-19.
        ///
        /// It is deliberately unlike every other earcon in the app, and the
        /// difference is structural rather than a rearrangement of the same
        /// parts. Everything else is one or more pure sines of 50 to 200 ms,
        /// usually two of them, usually a third apart; put any two of those
        /// side by side and an operator has to work to tell them apart. This is
        /// a single sustained note, three to twelve times longer than anything
        /// else here, and it has harmonics, so it has a timbre the rest of the
        /// set does not. Nothing about it can be mistaken for a toggle
        /// answering back.
        ///
        /// Long enough to be unmissable, short enough not to step on the speech
        /// that follows it — which is the actual payload. The alarm's whole job
        /// is to make sure the sentence after it gets listened to.
        ///
        /// Sprint 32 Track E: the spectrum is unchanged, but it is now the
        /// <see cref="EarconVoices.Alarm"/> voice rendered by the same engine
        /// as everything else, so it has a real envelope instead of two
        /// symmetric linear ramps. It also moved to the Strong tier. The old
        /// hand-summed version worked out at an RMS just above the quietest
        /// earcons in the app — a warning that was, measurably, softer than a
        /// dialog closing. That was never a decision anyone made; it fell out
        /// of three sines being summed without normalisation. If Noel's ear
        /// says the new level is too much, <see cref="VolumeStrong"/> is the
        /// one number to move.
        /// </summary>
        [Earcon("Warning alarm", EarconCategory.Warnings, Order = 1,
            Description = "Something is wrong now, and what happens next depends on hearing it. "
                        + "Long and harmonic, so it cannot be mistaken for a toggle.")]
        public static void WarningAlarmTone()
        {
            if (!Gate(EarconCategory.Warnings)) return;
            RxDuck.RequestFor(750);
            PlayVoiced(EarconVoices.Alarm, 800, 750, VolumeStrong);
        }

        /// <summary>Low buzz — invalid leader key.</summary>
        [Earcon("JJ key not recognised", EarconCategory.CommandsAndConfirmations, Order = 24,
            Description = "Low thunk. That key means nothing in the leader layer.")]
        public static void LeaderInvalidTone()
        {
            if (!Gate(EarconCategory.CommandsAndConfirmations)) return;
            PlayVoicedDecay(EarconVoices.Press, 200, 100, VolumeNormal);
        }

        /// <summary>Soft descending chirp — leader key cancelled.</summary>
        [Earcon("JJ key cancelled", EarconCategory.CommandsAndConfirmations, Order = 23,
            Description = "Soft falling chirp. The leader layer gave up waiting.")]
        public static void LeaderCancelTone()
        {
            if (!Gate(EarconCategory.CommandsAndConfirmations)) return;
            PlayChirp(500, 300, 150, VolumeSoft);
        }

        /// <summary>Double chime — leader key help requested.</summary>
        [Earcon("JJ key help", EarconCategory.CommandsAndConfirmations, Order = 22,
            Description = "Double chime. The leader layer is about to list what it can do.")]
        public static void LeaderHelpTone()
        {
            if (!Gate(EarconCategory.CommandsAndConfirmations)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (800, 80), (0, 40), (1000, 80) }, VolumeNormal);
        }

        #region Sprint 28 Phase 3 — Expand / Collapse / Collapse-All earcons

        /// <summary>
        /// Sprint 28 Phase 3 — ascending chirp with tracking band-pass noise sweep.
        /// Fires when a ScreenFields group re-expands. 400 → 1200 Hz over 350 ms.
        /// The band-pass noise sweep (tracking center freq, ~1/3 octave bandwidth)
        /// gives cut-through against ambient RF noise that pure tones would get
        /// masked by — the noise texture's envelope doesn't match broadband hiss.
        /// </summary>
        [Earcon("Group expanded", EarconCategory.DialogsAndPanels, Order = 11,
            Description = "Rising sweep with a tracking band of noise, when a group re-expands.")]
        public static void PlayExpand()
        {
            if (!Gate(EarconCategory.DialogsAndPanels)) return;
            if (AlertMixer == null) { FallbackBeep(800, 100); return; }
            try
            {
                var chirp = new ChirpSampleProvider(SampleRate, 400, 1200, 350, VolumeSoft);
                AddToMixer(chirp);
                // Noise volume 0.7 (was 0.12) compensates for biquad band-pass
                // inherent attenuation — narrow band-pass filtering of white noise
                // produces an RMS output of roughly sqrt(bandwidth/nyquist) * input RMS.
                // At Q=2.0 bandwidth is ~fc/2 across the sweep, so output RMS ≈
                // 0.1 * input RMS. Input volume 0.7 yields ~0.07 RMS, audibly present
                // but quieter than the 0.25-volume tone. Tuned 2026-04-21 after user
                // feedback ("sounds just like a tone sweep, no noise").
                var noise = new BandPassNoiseSweepSampleProvider(SampleRate, 400, 1200, 350, ExpandNoiseLevel);
                AddToMixer(noise);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayExpand failed: {ex.Message}");
                FallbackBeep(800, 100);
            }
        }

        /// <summary>
        /// Sprint 28 Phase 3 — descending chirp with tracking band-pass noise sweep.
        /// Fires on single-Escape group collapse. 1200 → 400 Hz over 350 ms.
        /// Mirror of PlayExpand; the symmetry lets users learn "up = grow, down =
        /// shrink" from one pairing and have the pattern hold.
        /// </summary>
        [Earcon("Group collapsed", EarconCategory.DialogsAndPanels, Order = 12,
            Description = "Falling sweep, the mirror of expand, on a single-Escape collapse.")]
        public static void PlayCollapse()
        {
            if (!Gate(EarconCategory.DialogsAndPanels)) return;
            if (AlertMixer == null) { FallbackBeep(500, 100); return; }
            try
            {
                var chirp = new ChirpSampleProvider(SampleRate, 1200, 400, 350, VolumeSoft);
                AddToMixer(chirp);
                var noise = new BandPassNoiseSweepSampleProvider(SampleRate, 1200, 400, 350, ExpandNoiseLevel);
                AddToMixer(noise);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayCollapse failed: {ex.Message}");
                FallbackBeep(500, 100);
            }
        }

        /// <summary>
        /// Double-Escape collapse-all: two descending struck notes at the top
        /// and bottom of the collapse chirp's sweep range, 1200 Hz then 400 Hz.
        /// Same endpoints as the single-group collapse, stamped rather than
        /// slid, so the pair reads as "and everything else too."
        ///
        /// This shape replaced a synthesized gavel in Sprint 28 Phase 3.8
        /// (2026-04-21) after the operator could hardly hear the bong — a low
        /// fundamental was the right idea acoustically and the wrong one on
        /// laptop speakers. The gavel class itself was left behind unwired as
        /// "a reference for future percussive work" and sat unread for four
        /// months; Sprint 32 Track E deleted it, because the thing it was a
        /// reference FOR is now expressible as a voice — fast attack, decay to
        /// silence, inharmonic partials — and a synthesiser nobody can find is
        /// not a reference, it is a fourth vocabulary lying in wait.
        /// </summary>
        [Earcon("Everything collapsed", EarconCategory.DialogsAndPanels, Order = 13,
            Description = "Two struck notes falling, on a double-Escape collapse-all.")]
        public static void PlayCollapseAll()
        {
            if (!Gate(EarconCategory.DialogsAndPanels)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (1200, 220), (0, 30), (400, 250) }, VolumeStrong);
        }

        #endregion

        #region ATU Tune Earcons

        // Dedicated provider for ATU progress — short-lived, added/removed per
        // tune cycle. Sprint 32 Track E moved it from ContinuousToneSampleProvider
        // to VoicedToneSampleProvider, the successor engine: the old provider's
        // FastPulse waveform is the "Urgent" voice's 50/50 gate, and
        // MeterVoiceLibrary.FromLegacyWaveform exists precisely to say so. The
        // voiced provider is already stereo with live equal-power panning, so
        // the MonoToStereo wrapper goes away too.
        // Logical "the app believes this continuous earcon is sounding" state,
        // tracked separately from the provider objects because in render-off
        // mode no provider ever exists. The first silent verification run
        // (2026-08-21) keyed the stop event on the provider and produced an
        // unmatched earcon-start - a false positive of the exact bug class the
        // start/stop pairing exists to catch.
        private static bool _atuProgressStarted;
        private static bool _txToneMonitorStarted;

        private static VoicedToneSampleProvider? _atuProgressProvider;

        /// <summary>
        /// Start the ATU progress earcon — a fast pulsing tone at 450 Hz that
        /// runs until <see cref="StopATUProgressEarcon"/> is called. One of the
        /// two continuous earcons in the app; it needs a Start/Stop pair rather
        /// than a fire-and-forget Play, and anything auditioning it has to know
        /// that (see <see cref="EarconCatalog"/>).
        /// </summary>
        [Earcon("ATU tuning in progress", EarconCategory.Transmit, Order = 31,
            StopMethod = nameof(StopATUProgressEarcon),
            RunningProperty = nameof(IsATUProgressEarconRunning),
            Description = "Continuous. Runs for as long as the tuner is working, so it needs "
                        + "Start and Stop rather than a single Play.")]
        public static void StartATUProgressEarcon()
        {
            // Continuous earcon: start and stop are SEPARATE transcript
            // events, because a tone that outlives its stop is a real bug this
            // project has had and an unmatched earcon-start is exactly how a
            // test sees it. A gated-off fire records as a plain earcon event
            // (no start), so the start/stop pairing stays clean.
            bool atuGateOn = On(EarconCategory.Transmit);
            if (!atuGateOn)
            {
                if (Radios.OutputChannelRecorder.RecordEnabled)
                    Radios.OutputChannelRecorder.RecordEarcon(
                        "ATUProgress", nameof(EarconCategory.Transmit), false, false);
                return;
            }
            StopATUProgressEarcon(); // Stop any existing progress earcon
            _atuProgressStarted = true;
            if (Radios.OutputChannelRecorder.RecordEnabled)
                Radios.OutputChannelRecorder.RecordEarconStart(
                    "ATUProgress", nameof(EarconCategory.Transmit), true,
                    AlertMixer != null && Radios.OutputChannelRecorder.RenderEnabled, 450);
            if (AlertMixer == null) return;
            try
            {
                JJTrace.Tracing.TraceLine("EarconPlayer: ATU progress earcon started");
                _atuProgressProvider = new VoicedToneSampleProvider(450f,
                    VolumeSoft * (_previewActive ? _previewGain : 1f))
                {
                    Voice = MeterVoiceLibrary.Resolve(
                        MeterVoiceLibrary.FromLegacyWaveform(WaveformType.FastPulse)),
                    Pan = _previewActive ? _previewPan : 0f,
                    Active = true
                };
                AlertMixer.AddMixerInput(_atuProgressProvider);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.StartATUProgressEarcon failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop the ATU progress earcon. Deactivates (10 ms fade) then removes
        /// from the mixer.
        /// </summary>
        public static void StopATUProgressEarcon()
        {
            // Real stops only, keyed on the logical flag rather than the
            // provider - in render-off mode the provider never exists, and the
            // provider-keyed version left every silent transcript with an
            // unmatched start.
            if (_atuProgressStarted)
            {
                _atuProgressStarted = false;
                if (Radios.OutputChannelRecorder.RecordEnabled)
                    Radios.OutputChannelRecorder.RecordEarconStop(
                        "ATUProgress", nameof(EarconCategory.Transmit));
            }
            var provider = _atuProgressProvider;
            if (provider == null) return;
            JJTrace.Tracing.TraceLine("EarconPlayer: ATU progress earcon stopped");
            provider.Active = false;
            _atuProgressProvider = null;
            if (AlertMixer == null) return;
            // Brief delay for fade-out, then remove from mixer
            System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
            {
                try { AlertMixer?.RemoveMixerInput(provider); }
                catch { }
            });
        }

        /// <summary>True while the ATU progress earcon is running — lets a
        /// bench surface show Start and Stop honestly rather than guessing.</summary>
        public static bool IsATUProgressEarconRunning => _atuProgressProvider != null;

        /// <summary>
        /// Local monitor for the TX test tone (Audio Track C). A continuous
        /// tone in the alert mixer so the operator can confirm by ear that the
        /// tone is running and hear its pitch. Presence indicator at a fixed
        /// comfortable volume — deliberately NOT scaled by the TX level, so a
        /// quiet reference tone is still audible locally. Same add/remove
        /// lifecycle as the ATU progress earcon.
        /// </summary>
        private static ContinuousToneSampleProvider? _txToneMonitorProvider;
        private static ISampleProvider? _txToneMonitorWrapper;

        /// <summary>
        /// Start the TX test-tone local monitor at the given frequency.
        /// Returns the provider so the caller can retune it live (volatile
        /// Frequency), or null if the mixer is unavailable.
        /// </summary>
        public static ContinuousToneSampleProvider? StartTxToneMonitor(float frequencyHz)
        {
            StopTxToneMonitor();
            // Same start/stop pairing contract as the ATU progress earcon.
            // Deliberately outside the category gates (TX confirmation).
            _txToneMonitorStarted = true;
            if (Radios.OutputChannelRecorder.RecordEnabled)
                Radios.OutputChannelRecorder.RecordEarconStart(
                    "TxToneMonitor", "ungated", true,
                    AlertMixer != null && Radios.OutputChannelRecorder.RenderEnabled, frequencyHz);
            if (AlertMixer == null) return null;
            try
            {
                _txToneMonitorProvider = new ContinuousToneSampleProvider(frequencyHz, 0.35f)
                {
                    Active = true
                };
                _txToneMonitorWrapper = new MonoToStereoSampleProvider(_txToneMonitorProvider);
                AlertMixer.AddMixerInput(_txToneMonitorWrapper);
                return _txToneMonitorProvider;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.StartTxToneMonitor failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Stop the TX test-tone local monitor (10 ms fade, then remove).</summary>
        public static void StopTxToneMonitor()
        {
            // Real stops only, keyed on the logical flag - see StopATUProgressEarcon.
            if (_txToneMonitorStarted)
            {
                _txToneMonitorStarted = false;
                if (Radios.OutputChannelRecorder.RecordEnabled)
                    Radios.OutputChannelRecorder.RecordEarconStop("TxToneMonitor", "ungated");
            }
            if (_txToneMonitorProvider != null)
            {
                _txToneMonitorProvider.Active = false;
            }
            if (_txToneMonitorWrapper != null && AlertMixer != null)
            {
                var wrapper = _txToneMonitorWrapper;
                _txToneMonitorWrapper = null;
                _txToneMonitorProvider = null;
                // Brief delay for fade-out, then remove from mixer
                System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
                {
                    try { AlertMixer?.RemoveMixerInput(wrapper); }
                    catch { }
                });
            }
        }

        /// <summary>ATU tune successful — rising major arpeggio C-E-G (~150ms total).</summary>
        [Earcon("ATU tune succeeded", EarconCategory.Transmit, Order = 32,
            Description = "Rising major triad. The tuner found a match.")]
        public static void ATUSuccessTone()
        {
            if (!Gate(EarconCategory.Transmit)) return;
            // C5=523, E5=659, G5=784 — rising major triad
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (523, 50), (659, 50), (784, 80) }, VolumeNormal);
        }

        /// <summary>ATU tune failed — descending minor E-C-A (~200ms total).</summary>
        [Earcon("ATU tune failed", EarconCategory.Transmit, Order = 33,
            Description = "Descending minor. The tuner gave up.")]
        public static void ATUFailTone()
        {
            if (!Gate(EarconCategory.Transmit)) return;
            // E5=659, C5=523, A4=440 — descending
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (659, 60), (523, 60), (440, 100) }, VolumeStrong);
        }

        /// <summary>Tune carrier on — short rising chirp.</summary>
        [Earcon("Tune carrier on", EarconCategory.Transmit, Order = 21,
            Description = "The tune carrier has started.")]
        public static void TuneOnTone()
        {
            if (!Gate(EarconCategory.Transmit)) return;
            PlayChirp(400, 700, 100, VolumeNormal);
        }

        /// <summary>Tune carrier off — short falling chirp.</summary>
        [Earcon("Tune carrier off", EarconCategory.Transmit, Order = 22,
            Description = "The tune carrier has stopped.")]
        public static void TuneOffTone()
        {
            if (!Gate(EarconCategory.Transmit)) return;
            PlayChirp(700, 400, 100, VolumeNormal);
        }

        #endregion

        /// <summary>
        /// Confirmation ding with decay — a clear, pleasant tone that cuts
        /// through radio audio. 1000 Hz with a soft octave above it, struck and
        /// then falling away over 250 ms. Used for frequency-entry confirmation
        /// and similar.
        ///
        /// This used to be DingToneSampleProvider, a private class whose entire
        /// job was "sine plus octave with an exponential decay" — which is one
        /// voice and three envelope numbers in the vocabulary, so it is now
        /// those instead. Sprint 32 Track E, #112.
        /// </summary>
        [Earcon("Frequency entry ding", EarconCategory.TuningAndFilters, Order = 1,
            Description = "A struck chime confirming a typed frequency.")]
        public static void DingTone()
        {
            if (!Gate(EarconCategory.TuningAndFilters)) return;
            PlayVoicedDecay(EarconVoices.Chime, 1000, 250, VolumeNormal);
        }

        #region Bench tone — a held note the operator drives

        // Sprint 32 Track E, #119 and #120. Every earcon in the app is a
        // one-shot, which is the right shape for an earcon and the wrong shape
        // for judging one. A sound you cannot hold still cannot be compared
        // against band noise: by the time you have decided what you heard it
        // has stopped, and the noise floor has moved. So the bench gets a note
        // it can hold, and tune and pan and re-voice while it is sounding.
        //
        // Distinct from StartTxToneMonitor, which monitors an actual transmit
        // test tone and is scaled and labelled for that job. This one is local,
        // says nothing to the radio, and exists purely to be listened to.

        private static VoicedToneSampleProvider? _benchToneProvider;

        /// <summary>
        /// Start, or re-voice, the held bench tone. Returns the live provider so
        /// the caller can move Frequency, Volume, Pan and Voice while it sounds
        /// — which is the whole point — or null if the mixer is unavailable.
        /// </summary>
        public static VoicedToneSampleProvider? StartBenchTone(
            MeterVoice? voice, float frequencyHz, float volume, float pan = 0f)
        {
            if (!EarconsEnabled || AlertMixer == null) return null;
            try
            {
                var existing = _benchToneProvider;
                if (existing != null)
                {
                    existing.Voice = voice ?? MeterVoiceLibrary.Resolve(null);
                    existing.Frequency = frequencyHz;
                    existing.Volume = volume;
                    existing.Pan = pan;
                    existing.Active = true;
                    return existing;
                }

                var provider = new VoicedToneSampleProvider(frequencyHz, volume)
                {
                    Voice = voice ?? MeterVoiceLibrary.Resolve(null),
                    Pan = pan,
                    Active = true,
                };
                _benchToneProvider = provider;
                AlertMixer.AddMixerInput(provider);
                return provider;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.StartBenchTone failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Stop the held bench tone (10 ms fade, then remove).</summary>
        public static void StopBenchTone()
        {
            var provider = _benchToneProvider;
            if (provider == null) return;
            provider.Active = false;
            _benchToneProvider = null;
            if (AlertMixer == null) return;
            System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
            {
                try { AlertMixer?.RemoveMixerInput(provider); }
                catch { }
            });
        }

        /// <summary>True while the held bench tone is sounding.</summary>
        public static bool IsBenchToneRunning => _benchToneProvider != null;

        #endregion

        /// <summary>
        /// Play a tone with specific parameters and panning. Used by earcon scratchpad.
        /// </summary>
        public static void PlayScratchpadTone(int freqHz, int durationMs, float volume, float pan)
        {
            PlayTonePanned(freqHz, durationMs, volume, pan);
        }

        /// <summary>
        /// Play a one-shot voiced note from the bench, optionally with a decay
        /// that fills its own duration.
        /// </summary>
        public static void PlayScratchpadVoiced(MeterVoice? voice, int freqHz, int durationMs,
            float volume, float pan, bool decay)
        {
            NoTrim();
            var v = voice ?? MeterVoiceLibrary.Resolve(null);
            if (decay) PlayVoicedDecay(v, freqHz, durationMs, volume, pan);
            else PlayVoiced(v, freqHz, durationMs, volume, pan);
        }

        /// <summary>
        /// Walk a scale from <paramref name="startHz"/> to <paramref name="endHz"/>
        /// in equal-tempered semitones, one note per step.
        ///
        /// Judging a voice on one pitch tells you almost nothing. A timbre that
        /// reads clearly at 800 Hz can vanish at 300 or turn shrill at 2000, and
        /// pitch is the axis that carries meter values, so the whole working
        /// range has to be listenable before a voice is worth keeping.
        /// </summary>
        public static void PlayScratchpadScale(MeterVoice? voice, int startHz, int endHz,
            int noteMs, float volume, float pan, bool decay)
        {
            NoTrim();
            if (startHz <= 0 || endHz <= 0) return;
            var v = voice ?? MeterVoiceLibrary.Resolve(null);
            int perNote = Math.Clamp(noteMs, 40, 1000);

            double ratio = (double)endHz / startHz;
            int semitones = (int)Math.Round(Math.Abs(Math.Log(ratio, 2.0) * 12.0));
            semitones = Math.Clamp(semitones, 1, 48);
            int direction = endHz >= startHz ? 1 : -1;

            var steps = new (int freq, int ms)[semitones + 1];
            for (int i = 0; i <= semitones; i++)
            {
                double f = startHz * Math.Pow(2.0, direction * i / 12.0);
                steps[i] = ((int)Math.Round(f), perNote);
            }

            if (decay) PlayVoicedDecaySequence(v, steps, volume, pan);
            else PlayVoicedSequence(v, steps, volume, pan);
        }

        /// <summary>
        /// Play the harmonic series over a fundamental: one times, two times,
        /// three times and so on, each in turn.
        ///
        /// This is the ear-training half of the bench. Voices are built out of
        /// exactly these partials, so hearing them one at a time is how a
        /// partial list stops being a row of numbers. Steps past about 5 kHz
        /// are dropped rather than played shrill.
        /// </summary>
        public static void PlayScratchpadHarmonics(MeterVoice? voice, int fundamentalHz,
            int count, int noteMs, float volume, float pan, bool decay)
        {
            NoTrim();
            if (fundamentalHz <= 0) return;
            var v = voice ?? MeterVoiceLibrary.Resolve(null);
            int perNote = Math.Clamp(noteMs, 40, 1000);
            int n = Math.Clamp(count, 1, 16);

            var steps = new List<(int freq, int ms)>();
            for (int i = 1; i <= n; i++)
            {
                int f = fundamentalHz * i;
                if (f > 5000) break;
                steps.Add((f, perNote));
            }
            if (steps.Count == 0) return;

            if (decay) PlayVoicedDecaySequence(v, steps.ToArray(), volume, pan);
            else PlayVoicedSequence(v, steps.ToArray(), volume, pan);
        }

        /// <summary>
        /// Play a chirp with specific parameters and panning. Used by earcon scratchpad.
        /// </summary>
        public static void PlayScratchpadChirp(int startHz, int endHz, int durationMs, float volume, float pan)
        {
            NoTrim();
            PlayChirpPanned(startHz, endHz, durationMs, volume, pan);
        }

        /// <summary>
        /// Play a countdown at bench timings and report how long it lasts, so
        /// the caller can schedule what follows it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Goes through <see cref="CountdownSteps"/> — the same builder the
        /// shipping earcons use — so a set of timings settled here is a set
        /// that can ship without being re-derived. That is the whole reason
        /// this exists rather than the dialog assembling its own step list.
        /// </para>
        /// <para>
        /// The returned duration is the sum of the steps and does NOT include
        /// the 15 ms fade tail, which overlaps the following silence rather
        /// than extending the sound.
        /// </para>
        /// </remarks>
        /// <returns>Total length in milliseconds.</returns>
        public static int PlayScratchpadCountdown(MeterVoice? voice, bool transmit,
            int countHz, int stepMs, int landingMs, float volume, float pan,
            int intervalMs = CountdownIntervalMs)
        {
            NoTrim();
            var v = voice ?? CountdownVoice;
            var steps = CountdownSteps(transmit, countHz,
                Math.Clamp(stepMs, 20, 2000), Math.Clamp(landingMs, 20, 4000),
                Math.Clamp(intervalMs, 20, 4000));

            PlayVoicedDecaySequence(v, steps, volume, pan);

            int total = 0;
            foreach (var (_, ms) in steps) total += ms;
            return total;
        }

        #endregion

        #region Typing Sounds (Phase 7)

        private static CachedSound[]? _keyboardSounds;
        private static readonly Random _keyRandom = new();

        // DTMF frequency pairs per ITU-T Q.23
        private static readonly Dictionary<char, (int low, int high)> DtmfFreqs = new()
        {
            ['1'] = (697, 1209), ['2'] = (697, 1336), ['3'] = (697, 1477),
            ['4'] = (770, 1209), ['5'] = (770, 1336), ['6'] = (770, 1477),
            ['7'] = (852, 1209), ['8'] = (852, 1336), ['9'] = (852, 1477),
            ['*'] = (941, 1209), ['0'] = (941, 1336), ['#'] = (941, 1477),
        };

        /// <summary>
        /// Play a typing sound for a digit keystroke based on current mode.
        /// </summary>
        public static void PlayTypingSound(char digit, TypingSoundMode mode)
        {
            // Typing sounds are not catalog entries — they have their own mode
            // setting rather than an earcon id — so there is nothing to look a
            // per-sound trim up by, and they must not inherit one from the
            // sound that played before them. If they ever want their own
            // trim, they need an id first.
            NoTrim();

            // Outside the EarconCategory gates on purpose (typing sounds have
            // their own mode setting), so recorded here: mode Off is this
            // family's gate. One event per keystroke sound - human-paced.
            if (Radios.OutputChannelRecorder.RecordEnabled)
            {
                bool typingOn = mode != TypingSoundMode.Off;
                Radios.OutputChannelRecorder.RecordEarcon(
                    "TypingSound", "typing", typingOn,
                    typingOn && AlertMixer != null && Radios.OutputChannelRecorder.RenderEnabled,
                    detail: $"{mode} '{digit}'");
            }
            switch (mode)
            {
                case TypingSoundMode.Beep:
                    // Random musical note from C4-C8 (4 octaves, MIDI 60-108)
                    int midiNote = 60 + _keyRandom.Next(49); // 49 semitones = 4 octaves
                    int freq = (int)(440.0 * Math.Pow(2.0, (midiNote - 69) / 12.0));
                    PlayTypingTone(freq);
                    break;
                case TypingSoundMode.SingleTone:
                    PlayTypingTone(TypingToneHz);
                    break;
                case TypingSoundMode.RandomTones:
                    PlayTypingTone(_keyRandom.Next(300, 2001));
                    break;
                case TypingSoundMode.Mechanical:
                    PlayMechanicalKey();
                    break;
                case TypingSoundMode.TouchTone:
                    PlayDtmfTone(digit);
                    break;
                case TypingSoundMode.Off:
                    break;
            }
        }

        // ------------------------------------------------------------------
        // The typing tone modes (#115) — camouflage, not loudness
        //
        // Beep, SingleTone and RandomTones were all PlayTone(freq, 30, 0.25f):
        // a bare sine, thirty milliseconds, straight out of a SignalGenerator.
        //
        // The complaint was that they get buried, and the diagnosis is NOT
        // that they are too quiet. They RESEMBLE THE MASKER. A short
        // broadband transient in the voice band IS what a static crash is, and
        // there is no amplitude at which it stops being one — turning these up
        // makes them louder pieces of the noise. RandomTones is the clearest
        // case: a random frequency between 300 and 2000 Hz for 30 ms is a
        // synthetic static crash by construction, and you could not design
        // better camouflage deliberately.
        //
        // Duration is the mechanism. Gating a tone into a very short window
        // smears its spectrum, and below roughly 50 ms the ear resolves the
        // onset transient rather than the pitch. "800 Hz for 15 ms" is not a
        // tone, it is a click with an 800 Hz tint.
        //
        // DTMF IS THE POSITIVE CONTROL AND IS DELIBERATELY NOT TOUCHED. It
        // sits in the same family, is heard as clearly present, and differs in
        // exactly the two variables that decide masking: it puts energy at two
        // frequencies at once instead of one, and it runs long enough to read
        // as a pitch. Harmonicity is one of the strongest auditory grouping
        // cues — partials at integer ratios fuse into one perceived object and
        // segregate from aperiodic noise, which is what band noise is.
        //
        // So the tone modes copy those two variables and nothing else: a voice
        // with harmonics instead of a bare sine, and DTMF's duration instead
        // of 30 ms. Press is the right voice — struck, then out of the way —
        // and because it is one of the seven named voices these follow the
        // #147 set switch, so an operator on Simple gets the plain sines back.
        //
        // MUST BE JUDGED AGAINST A LIVE BAND. The original assessment was made
        // in a quiet room with no radio connected, which is the FLOOR and not
        // the worst case. Evening QRN on 40 or 80 is the benchmark. Judging in
        // a quiet room is exactly how this shipped the first time.
        // ------------------------------------------------------------------

        /// <summary>The fixed pitch for SingleTone, and the fallback pitch
        /// wherever a typing sound has nothing better to play.</summary>
        private const int TypingToneHz = 800;

        /// <summary>
        /// How long a typing tone lasts. <see cref="PlayDtmfTone"/>'s duration,
        /// deliberately and to the millisecond: DTMF is the one member of this
        /// family that is not buried, and length is half of why.
        /// </summary>
        /// <remarks>
        /// Checked against the code rather than taken from the write-up, which
        /// said 50 — as did DTMF's own summary line, which had drifted from
        /// the constant beneath it. Both say 60 now.
        /// </remarks>
        private const int TypingToneMs = 60;

        /// <summary>
        /// The typing family's level. <see cref="VolumeSoft"/> by name rather
        /// than by coincidence — its whole definition is "repeat sounds that
        /// fire many times a minute", which is what a keystroke is.
        /// </summary>
        /// <remarks>
        /// This is a rise from the 0.25 these used, and it is only defensible
        /// for the TONE modes. The mechanical WAV modes are broadband
        /// transients and gain does not rescue those — see
        /// <see cref="PlayMechanicalKey"/>, which is fixed by levelling the
        /// pool rather than by pushing it harder.
        /// </remarks>
        private const float VolumeTyping = VolumeSoft;

        /// <summary>
        /// One keystroke tone: harmonic, and long enough to read as a pitch.
        /// Every tone-mode keystroke goes through here so the three modes
        /// cannot drift apart in anything except the pitch they choose.
        /// </summary>
        private static void PlayTypingTone(int frequencyHz)
        {
            PlayVoicedDecay(EarconVoices.Press, frequencyHz, TypingToneMs, VolumeTyping);
        }

        /// <summary>
        /// The peak every mechanical keyboard sample is levelled to.
        /// </summary>
        /// <remarks>
        /// Comfortably below full scale, and it sits in the middle of the
        /// range the old blanket 8x actually produced (0.35 to 1.30), so the
        /// pool as a whole is about as loud as it was. What changes is that
        /// the samples now agree with each other.
        /// </remarks>
        private const float MechanicalKeyTargetPeak = 0.8f;

        /// <summary>
        /// Play a random mechanical keyboard sound from the loaded pool,
        /// levelled so every sample in it lands at the same peak.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This used to apply a blanket 8x to whichever sample came up.</b>
        /// Someone had already reached for gain here, with a comment saying
        /// why, and the sounds were still buried — which is #115's whole
        /// point: these are broadband transients, they resemble band noise,
        /// and no amount of gain makes a click stop being click-shaped. Do not
        /// spend DSP trying to make one beat the noise.
        /// </para>
        /// <para>
        /// <b>Measured 2026-08-26, because "the boost may be clipping" was a
        /// hypothesis and not a fact.</b> All thirteen samples are 24-bit
        /// stereo, 48 kHz, 200 ms. Peaks run 0.0435 to 0.1630. At 8x the
        /// loudest reaches 1.304 — 2.3 dB past full scale — and
        /// <see cref="CachedSound"/>'s gain constructor hard-clamps, so it
        /// clips. Four of the thirteen clip at all, and across the pool it is
        /// <b>42 samples out of 124,800, or 0.03%</b>.
        /// </para>
        /// <para>
        /// <b>So the clipping is real, and it is not the problem.</b> 0.03% of
        /// samples cannot flatten an envelope; the worry that the existing fix
        /// was degrading what it meant to rescue does not survive the
        /// measurement. It is confined to the attack transient of four
        /// samples, which is at least the part that carries a click's
        /// identity, so it is worth not doing.
        /// </para>
        /// <para>
        /// <b>The measurement found something louder, which nobody was looking
        /// for: the pool's peaks span 11.5 dB.</b> One blanket gain therefore
        /// produced keystrokes whose loudness jumped by more than 11 dB at
        /// random, keypress to keypress. That is far more audible than 0.03%
        /// clipping and reads as the application being inconsistent rather
        /// than as variety.
        /// </para>
        /// <para>
        /// Per-sample levelling fixes both at once, and needs no DSP: every
        /// sample lands at the same peak, nothing clips, and the randomness
        /// that remains is the character of the samples rather than their
        /// volume. Ear-gated, because the quietest sample is now noticeably
        /// louder than it was.
        /// </para>
        /// </remarks>
        private static void PlayMechanicalKey()
        {
            if (_keyboardSounds == null || _keyboardSounds.Length == 0)
            {
                // No pool loaded. A 15 ms sine was the worst offender in the
                // whole typing family — comfortably below the roughly 50 ms
                // where the ear starts hearing pitch instead of onset — so the
                // fallback is the same tone the other modes now play.
                PlayTypingTone(TypingToneHz);
                return;
            }
            // The pool is levelled once, at load. Normalising per keystroke
            // would allocate a fresh 200 ms buffer on every keypress for a
            // result that never changes.
            int idx = _keyRandom.Next(_keyboardSounds.Length);
            PlayCachedSound(_keyboardSounds[idx]);
        }

        /// <summary>
        /// Play a DTMF dual-tone for the given digit (60 ms burst).
        /// </summary>
        private static void PlayDtmfTone(char digit)
        {
            if (!DtmfFreqs.TryGetValue(digit, out var freqs))
            {
                // Not a DTMF key. Falls back to the tone the other modes play
                // rather than to the bare 30 ms sine it used, which was one of
                // the offenders #115 was written about.
                PlayTypingTone(TypingToneHz);
                return;
            }

            if (!EarconsEnabled || AlertMixer == null) return;
            try
            {
                const int durationMs = 60;
                // Two simultaneous sine waves at standard DTMF frequencies
                var low = new SignalGenerator(SampleRate, 1)
                {
                    Type = SignalGeneratorType.Sin,
                    Frequency = freqs.low,
                    Gain = 0.25f
                };
                var high = new SignalGenerator(SampleRate, 1)
                {
                    Type = SignalGeneratorType.Sin,
                    Frequency = freqs.high,
                    Gain = 0.25f
                };
                var lowTimed = low.Take(TimeSpan.FromMilliseconds(durationMs));
                var highTimed = high.Take(TimeSpan.FromMilliseconds(durationMs));
                AddToMixer(lowTimed);
                AddToMixer(highTimed);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayDtmfTone failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Load keyboard sounds from the hashed resource directory.
        /// Called by CalibrationEngine when mechanical keyboard mode is unlocked.
        /// </summary>
        public static void LoadKeyboardSoundsFromDirectory(string relativeDir)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                string baseDir = Path.GetDirectoryName(assembly.Location) ?? "";
                string fullDir = Path.Combine(baseDir, relativeDir);

                if (!Directory.Exists(fullDir))
                {
                    Trace.WriteLine($"EarconPlayer: keyboard sound directory not found: {fullDir}");
                    return;
                }

                var files = Directory.GetFiles(fullDir);
                var sounds = new List<CachedSound>();
                foreach (var file in files)
                {
                    try
                    {
                        using var stream = File.OpenRead(file);
                        // Levelled here, once, rather than boosted at every
                        // keystroke. The pool's own peaks span 11.5 dB
                        // (measured 2026-08-26), so a single blanket gain made
                        // keystroke loudness jump at random — see
                        // PlayMechanicalKey for the measurement.
                        sounds.Add(new CachedSound(stream).Normalized(MechanicalKeyTargetPeak));
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"EarconPlayer: failed to load keyboard sound '{file}': {ex.Message}");
                    }
                }

                Trace.WriteLine($"EarconPlayer: loaded {sounds.Count} keyboard sounds");
                if (sounds.Count > 0)
                {
                    _keyboardSounds = sounds.ToArray();
                    Trace.WriteLine($"EarconPlayer: loaded {sounds.Count} keyboard sounds");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.LoadKeyboardSoundsFromDirectory failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Play a WAV stream through the alert channel. Used by CalibrationEngine for
        /// verification tones.
        /// </summary>
        public static void PlayStreamAsWav(Stream wavStream)
        {
            if (!EarconsEnabled || AlertMixer == null) return;
            try
            {
                var sound = new CachedSound(wavStream);
                PlayCachedSound(sound);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayStreamAsWav failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if mechanical keyboard sounds are loaded and available.
        /// </summary>
        public static bool HasKeyboardSounds => _keyboardSounds != null && _keyboardSounds.Length > 0;

        #endregion

        #region Internal Playback

        #region Bench preview scope

        // Sprint 32 Track E, #119. The bench needs to audition a real earcon at
        // a chosen level and stereo position without every earcon in the app
        // permanently acquiring a level and a position. So the override is
        // SCOPED to one Play call rather than being a setting.
        //
        // Every earcon's public method builds its providers synchronously and
        // hands them to the mixer before returning, so a [ThreadStatic] flag
        // held across that one call reaches exactly the sound being auditioned
        // and nothing else. It cannot leak: the finally clears it, and a value
        // left set on the UI thread by an exception would still be cleared
        // before any other earcon could run on that thread.
        //
        // This deliberately does NOT touch the operator's alert volume. The
        // bench is for comparing sounds against each other and against band
        // noise; changing a saved setting to do it would be a side effect
        // nobody asked for and a support call later.

        [ThreadStatic] private static bool _previewActive;
        [ThreadStatic] private static float _previewGain;
        [ThreadStatic] private static float _previewPan;

        /// <summary>
        /// Run one earcon with a bench gain and stereo position applied.
        /// <paramref name="gain"/> multiplies the sound's own tier;
        /// <paramref name="pan"/> is added to whatever panning the sound
        /// already does for itself, so a left-panned filter edge auditioned at
        /// pan right lands in the middle rather than jumping.
        /// </summary>
        public static void PlayWithBenchSettings(Action play, float gain, float pan)
        {
            if (play == null) return;
            _previewActive = true;
            _previewGain = Math.Clamp(gain, 0f, 4f);
            _previewPan = Math.Clamp(pan, -1f, 1f);
            try { play(); }
            finally
            {
                _previewActive = false;
                _previewGain = 1f;
                _previewPan = 0f;
            }
        }

        // ------------------------------------------------------------------
        // Per-sound relative level — a DESIGN control, not a listening one
        //
        // The bench gain above is a listening control: scoped to one Play
        // call, deliberately not saved, there so two sounds can be compared
        // against each other and against band noise. It answers "how loud is
        // this right now."
        //
        // This answers a different question — "how loud should this sound be,
        // relative to its tier, from now on" — and so it must PERSIST. It is
        // what "the whole modern vocabulary sits a tier below the legacy one"
        // actually needs: 0.30 against 0.60 is 6 dB, and the sounds heard most
        // often were the hardest to hear. The three tiers fixed most of that
        // by giving every sound a level it picks for a reason that can be said
        // in words; what they cannot do is let one individual sound be trimmed
        // when the ear says the tier is right and that one sound still is not.
        //
        // In dB, not as a multiplier, because that is how the judgement is
        // actually made ("this wants to come down three") and because equal
        // steps in dB are equal steps to the ear. Zero means untouched, which
        // is the default for every sound and what ships.
        //
        // BOUNDED, AND ASYMMETRICALLY, because the two directions carry
        // different risks.
        //
        // Cutting is safe at any depth, so cuts go to -12 dB. Boosting is not:
        // the loudest tier is a peak amplitude of 0.65, so even +4 dB puts a
        // sound past full scale, and there is no headroom left at the device
        // to absorb it. A persisted boost that distorts is worse than a bench
        // gain that does, because the bench gain evaporates when the Play call
        // returns and this one does not.
        //
        // So boosts stop at +3 dB, which is roughly the headroom the top tier
        // has left. A sound wanting more than that does not want a trim, it
        // wants a different TIER — and the tiers are chosen for reasons that
        // can be said in words, which is the better conversation to be having.
        // The quiet-vocabulary problem this was written for is a 6 dB gap, and
        // the answer to it is to raise the quiet sounds a tier rather than to
        // boost them individually.
        //
        // A trim can never silence a sound outright: -12 dB is a quarter of
        // the amplitude, not zero. The family switches exist for turning
        // things off, and losing a warning by accident should not be one
        // slider away.
        // ------------------------------------------------------------------

        /// <summary>The deepest cut a single sound may be given.</summary>
        public const float MinLevelTrimDb = -12f;

        /// <summary>
        /// The largest boost a single sound may be given — roughly the
        /// headroom the loudest tier has left before full scale.
        /// </summary>
        public const float MaxLevelTrimDb = 3f;

        private static readonly Dictionary<string, float> _levelTrimDb = new(StringComparer.Ordinal);

        /// <summary>
        /// The earcon currently being built, so the gain applied at the mixer
        /// can find its trim.
        /// </summary>
        /// <remarks>
        /// Same reasoning as the bench flags above, and the same guarantee:
        /// an earcon's public method builds its providers synchronously and
        /// hands them to the mixer before returning, so a thread-local set on
        /// the way in reaches exactly that sound. It is SET rather than
        /// cleared at the end because a sound may add more than one provider —
        /// the filter stretch adds two — and consuming it on first use would
        /// trim half a sound. Every path that reaches the mixer sets it first:
        /// <see cref="Gate"/> for the gated families,
        /// <see cref="BenchGate"/> for the handful outside them, and
        /// <see cref="NoTrim"/> for the scratchpad, which plays voices rather
        /// than named earcons and has nothing to look up.
        /// </remarks>
        [ThreadStatic] private static float _currentTrimDb;

        /// <summary>
        /// The trim on one earcon, in dB. Zero when it has never been given
        /// one, which is every sound as shipped.
        /// </summary>
        /// <param name="earconId">the method name, which is the same stable id
        /// <see cref="EarconCatalog"/> uses</param>
        public static float GetLevelTrimDb(string earconId)
        {
            if (string.IsNullOrEmpty(earconId)) return 0f;
            lock (_levelTrimDb)
                return _levelTrimDb.TryGetValue(earconId, out float db) ? db : 0f;
        }

        /// <summary>
        /// Trim one earcon relative to its tier. Zero removes the trim rather
        /// than storing a zero, so a config only ever carries real decisions.
        /// </summary>
        public static void SetLevelTrimDb(string earconId, float db)
        {
            if (string.IsNullOrEmpty(earconId)) return;
            db = Math.Clamp(db, MinLevelTrimDb, MaxLevelTrimDb);
            lock (_levelTrimDb)
            {
                if (Math.Abs(db) < 0.05f) _levelTrimDb.Remove(earconId);
                else _levelTrimDb[earconId] = db;
            }
        }

        /// <summary>Every trim that has been set, for persistence.</summary>
        public static IReadOnlyDictionary<string, float> GetAllLevelTrimsDb()
        {
            lock (_levelTrimDb)
                return new Dictionary<string, float>(_levelTrimDb, StringComparer.Ordinal);
        }

        /// <summary>Replace every trim at once — the restore side of persistence.</summary>
        public static void SetAllLevelTrimsDb(IEnumerable<KeyValuePair<string, float>>? trims)
        {
            lock (_levelTrimDb)
            {
                _levelTrimDb.Clear();
                if (trims == null) return;
                foreach (var kv in trims)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    float db = Math.Clamp(kv.Value, MinLevelTrimDb, MaxLevelTrimDb);
                    if (Math.Abs(db) >= 0.05f) _levelTrimDb[kv.Key] = db;
                }
            }
        }

        /// <summary>
        /// Arm the trim for a sound outside the six family switches — the
        /// calibration and bench sounds, which answer to the master gate only.
        /// </summary>
        /// <remarks>
        /// These would otherwise reach the mixer without setting the
        /// thread-local and inherit whatever the previous sound on this thread
        /// was trimmed by. Returns the master gate so it reads as a drop-in
        /// for the <c>if (!EarconsEnabled) return;</c> it replaces.
        /// </remarks>
        private static bool BenchGate([CallerMemberName] string earconName = "")
        {
            _currentTrimDb = GetLevelTrimDb(earconName);
            return EarconsEnabled;
        }

        /// <summary>
        /// Declare that what follows is not a named earcon and must not be
        /// trimmed — the scratchpad's raw tones and voice auditions.
        /// </summary>
        private static void NoTrim() => _currentTrimDb = 0f;

        /// <summary>
        /// Bench gain and per-sound trim for a provider going into the alert
        /// mixer. One wrapper for both: they multiply, because one is "louder
        /// while I listen" and the other is "quieter from now on", and an
        /// operator auditioning a trimmed sound wants to hear the trim.
        /// </summary>
        private static ISampleProvider ApplyPreviewGain(ISampleProvider source)
        {
            float gain = _previewActive ? _previewGain : 1f;
            float trimDb = _currentTrimDb;
            if (Math.Abs(trimDb) >= 0.05f)
                gain *= (float)Math.Pow(10.0, trimDb / 20.0);

            if (Math.Abs(gain - 1f) < 0.001f) return source;
            return new VolumeSampleProvider(source) { Volume = gain };
        }

        #endregion

        /// <summary>Add a mono source to the alert channel stereo mixer (auto-converts to stereo center).</summary>
        private static void AddToMixer(ISampleProvider monoSource)
        {
            if (!EarconsEnabled || AlertMixer == null) return;
            // A bench pan on an otherwise centred sound has to become a real
            // pan, which needs the mono source before it is widened.
            if (_previewActive && Math.Abs(_previewPan) >= 0.01f
                && monoSource.WaveFormat.Channels == 1)
            {
                AddToMixerPanned(monoSource, 0f);
                return;
            }
            if (monoSource.WaveFormat.Channels == 1)
                AlertMixer.AddMixerInput(ApplyPreviewGain(new MonoToStereoSampleProvider(monoSource)));
            else
                AlertMixer.AddMixerInput(ApplyPreviewGain(monoSource));
        }

        /// <summary>Add a mono source to the alert channel stereo mixer with panning (-1 left, 0 center, +1 right).</summary>
        private static void AddToMixerPanned(ISampleProvider monoSource, float pan)
        {
            if (!EarconsEnabled || AlertMixer == null) return;
            // PanningSampleProvider takes mono → outputs stereo
            if (monoSource.WaveFormat.Channels != 1)
                monoSource = monoSource.ToMono();
            if (_previewActive) pan = Math.Clamp(pan + _previewPan, -1f, 1f);
            var panned = new PanningSampleProvider(monoSource) { Pan = pan };
            AlertMixer.AddMixerInput(ApplyPreviewGain(panned));
        }

        /// <summary>Add a mono source with panning that sweeps from startPan to endPan over durationMs.</summary>
        private static void AddToMixerSweptPan(ISampleProvider monoSource, float startPan, float endPan, int durationMs)
        {
            if (!EarconsEnabled || AlertMixer == null) return;
            if (monoSource.WaveFormat.Channels != 1)
                monoSource = monoSource.ToMono();
            var swept = new SweepPanningSampleProvider(monoSource, startPan, endPan, durationMs);
            // Through the same gain wrapper as the other two, so a trim is
            // uniform across every one-shot. This also closes a pre-existing
            // gap: the bench gain never reached the swept-pan sounds either,
            // so auditioning an expand or collapse sweep at a bench gain moved
            // nothing.
            AlertMixer.AddMixerInput(ApplyPreviewGain(swept));
        }

        internal static void PlayTone(int frequencyHz, int durationMs, float volume)
        {
            if (!EarconsEnabled) return;
            if (AlertMixer == null) { FallbackBeep(frequencyHz, durationMs); return; }
            try
            {
                var signal = new SignalGenerator(SampleRate, 1) // mono
                {
                    Type = SignalGeneratorType.Sin,
                    Frequency = frequencyHz,
                    Gain = volume
                };
                var timed = signal.Take(TimeSpan.FromMilliseconds(durationMs));
                var faded = new FadeInOutSampleProvider(timed, true);
                faded.BeginFadeIn(Math.Min(durationMs / 10.0, 10));
                faded.BeginFadeOut(Math.Max(durationMs - Math.Min(durationMs / 10.0, 10), 0));
                AddToMixer(faded);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayTone failed: {ex.Message}");
                FallbackBeep(frequencyHz, durationMs);
            }
        }

        /// <summary>
        /// Submit a pre-composed CW element sequence to the alert mixer.
        /// The caller constructs the full sequence as a ConcatenatingSampleProvider
        /// of shaped CwToneSampleProviders and silences so the audio engine drives
        /// inter-element timing at sample-accurate resolution — no Task.Delay
        /// jitter. Returns an IDisposable whose Dispose() cancels the sequence
        /// mid-stream (used by MorseNotifier to interrupt a long string if a
        /// newer one fires before it finishes).
        /// </summary>
        internal static IDisposable SubmitCwSequence(ISampleProvider sequence)
            => SubmitCwSequence(sequence, null);

        /// <summary>
        /// As above, with the sample positions of the sequence's character
        /// boundaries (#182) so a supersede can close the stream at the end
        /// of the character in progress rather than mid-symbol. Null means no
        /// boundaries — a single character, which is atomic.
        /// </summary>
        internal static IDisposable SubmitCwSequence(
            ISampleProvider sequence, IReadOnlyList<long>? boundarySamplePositions)
        {
            if (sequence == null) throw new ArgumentNullException(nameof(sequence));
            if (!EarconsEnabled || AlertMixer == null) return NullCancellable.Instance;
            try
            {
                var cancellable = new CancellableCwProvider(sequence, boundarySamplePositions);
                AddToMixer(cancellable);
                return cancellable;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.SubmitCwSequence failed: {ex.Message}");
                return NullCancellable.Instance;
            }
        }

        private sealed class NullCancellable : IDisposable
        {
            public static readonly NullCancellable Instance = new();
            public void Dispose() { }
        }

        // ── Drain observation for the alert channel (Sprint 32 Track H) ──
        //
        // Purely additive read-only accessors. They exist because
        // EarconCwOutput has to know whether audio it submitted has actually
        // REACHED THE SPEAKER before it resolves a completion — and until this
        // sprint nothing could ask, so it resolved on a computed duration and
        // the app-exit farewell was cut off mid-string by the teardown that
        // followed. See the long comment on EarconCwOutput.WaitForDrain.
        //
        // Nothing here mutates the channel; the audio engine's ownership is
        // unchanged.

        /// <summary>
        /// Bytes the alert output device reports as actually PLAYED, or -1 when
        /// there is no channel or the driver will not answer. Rises continuously
        /// while the app runs, because the mixer always has something (usually
        /// silence) to hand it — so this is only meaningful as a DIFFERENCE
        /// measured from a known instant, never as an absolute.
        /// </summary>
        internal static long AlertPlayedBytes => _alertChannel?.PlayedBytes ?? -1;

        /// <summary>
        /// How much audio the alert output device can be holding past the mixer,
        /// in milliseconds — its buffer size multiplied by its buffer count. This
        /// is the interval between "the mixer read the last sample" and "the last
        /// sample was heard", and it is roughly four times the 50 ms tail that
        /// used to stand in for it.
        /// </summary>
        internal static int AlertOutputLatencyMs => _alertChannel?.OutputLatencyMs ?? 200;

        /// <summary>
        /// Byte rate of the alert mixer's format, for converting a latency in
        /// milliseconds into an advance in <see cref="AlertPlayedBytes"/>.
        /// IEEE float samples, so four bytes per sample per channel.
        /// </summary>
        internal static int AlertBytesPerSecond => SampleRate * MixerChannels * sizeof(float);

        private static void PlayTonePanned(int frequencyHz, int durationMs, float volume, float pan)
        {
            if (AlertMixer == null) { FallbackBeep(frequencyHz, durationMs); return; }
            try
            {
                var signal = new SignalGenerator(SampleRate, 1)
                {
                    Type = SignalGeneratorType.Sin,
                    Frequency = frequencyHz,
                    Gain = volume
                };
                var timed = signal.Take(TimeSpan.FromMilliseconds(durationMs));
                var faded = new FadeInOutSampleProvider(timed, true);
                faded.BeginFadeIn(Math.Min(durationMs / 10.0, 10));
                faded.BeginFadeOut(Math.Max(durationMs - Math.Min(durationMs / 10.0, 10), 0));
                AddToMixerPanned(faded, pan);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayTonePanned failed: {ex.Message}");
                FallbackBeep(frequencyHz, durationMs);
            }
        }

        #region Voiced rendering — the one synthesis vocabulary (#112)

        /// <summary>
        /// Fade tail appended to every voiced render, matching the engine's own
        /// 10 ms activation fade with 5 ms of headroom. In a sequence the tail
        /// overlaps the following gap rather than lengthening the earcon, which
        /// is why steps are summed into one buffer instead of concatenated.
        /// </summary>
        private const int VoicedTailMs = 15;

        /// <summary>
        /// Render a voiced earcon step by step into one mono buffer.
        ///
        /// Each step is a frequency in Hz and a duration in ms; a frequency of
        /// zero is silence, which is how gaps inside a cadence are expressed —
        /// the same convention the old PlayToneSequence used, so porting
        /// an earcon from sines to a voice is a change of instrument and
        /// nothing else. Steps are placed at their own start offset and SUMMED,
        /// so a step's decay tail rings on into the gap after it instead of
        /// being chopped. That is what stops a fast cadence sounding stapled
        /// together.
        ///
        /// This is the only place in the alert path that turns parameters into
        /// samples. Everything above it chooses a voice, a pitch and a tier.
        /// </summary>
        internal static float[] RenderVoiced(MeterVoice voice, (int freq, int ms)[] steps, float volume)
        {
            int totalMs = 0;
            foreach (var s in steps) totalMs += Math.Max(s.ms, 0);

            int totalSamples = SampleRate * (totalMs + VoicedTailMs) / 1000 + 1;
            var buffer = new float[Math.Max(totalSamples, 1)];

            int cursor = 0;
            foreach (var (freq, ms) in steps)
            {
                int stepSamples = SampleRate * Math.Max(ms, 0) / 1000;
                if (freq > 0 && ms > 0)
                {
                    var mono = RenderStep(voice, freq, ms, volume);
                    int n = Math.Min(mono.Length, buffer.Length - cursor);
                    for (int i = 0; i < n; i++)
                        buffer[cursor + i] += mono[i];
                }
                cursor += stepSamples;
            }
            return buffer;
        }

        /// <summary>
        /// Render one note, then restore the level the caller asked for if the
        /// engine's own activation fade ate into it.
        ///
        /// VoicedToneSampleProvider ramps from silence over a fixed 10 ms every
        /// time it activates. On a meter tone that is invisible; on a 20 ms
        /// filter-edge click it is half the sound, and the note never reaches
        /// the amplitude its tier asked for. Measured, that cost the shortest
        /// earcons around 5 dB — which is the wrong direction entirely for the
        /// sounds already hardest to pick out of band noise.
        ///
        /// So: measure the peak, and scale UP to the requested amplitude if it
        /// fell short. Never down. A note long enough to reach full level peaks
        /// at the requested volume already, so the factor is 1 and nothing
        /// changes; a noisy voice can peak above it, and is left alone. The
        /// boost is capped, because a nearly-silent render is a bug and
        /// amplifying it four hundred times would only make the bug loud.
        ///
        /// The 10 ms fade lives in the shared engine and the meters depend on
        /// it. Correcting here rather than there is deliberate.
        /// </summary>
        private static float[] RenderStep(MeterVoice voice, int freq, int ms, float volume)
        {
            var mono = VoicedToneSampleProvider.RenderMono(voice, freq, ms, volume);
            if (volume <= 0f || mono.Length == 0) return mono;

            float peak = 0f;
            foreach (var sample in mono)
            {
                float a = Math.Abs(sample);
                if (a > peak) peak = a;
            }
            if (peak <= 0.0001f || peak >= volume) return mono;

            float gain = Math.Min(volume / peak, 4f);
            for (int i = 0; i < mono.Length; i++) mono[i] *= gain;
            return mono;
        }

        /// <summary>
        /// Play a single voiced note. The workhorse of the alert path: pick a
        /// voice from <see cref="EarconVoices"/>, a pitch, a length and a
        /// loudness tier.
        /// </summary>
        internal static void PlayVoiced(MeterVoice voice, int frequencyHz, int durationMs,
            float volume, float pan = 0f)
        {
            PlayVoicedSequence(voice, new[] { (frequencyHz, durationMs) }, volume, pan);
        }

        /// <summary>
        /// Play a voiced cadence — several notes and gaps as one earcon, one
        /// mixer input, no inter-note timing jitter.
        /// </summary>
        internal static void PlayVoicedSequence(MeterVoice voice, (int freq, int ms)[] steps,
            float volume, float pan = 0f)
        {
            if (!EarconsEnabled) return;
            if (AlertMixer == null)
            {
                int first = 800, ms = 150;
                foreach (var s in steps) { if (s.freq > 0) { first = s.freq; ms = s.ms; break; } }
                FallbackBeep(first, ms);
                return;
            }
            try
            {
                var rendered = RenderVoiced(voice, steps, volume);
                var provider = new RenderedSampleProvider(rendered);
                if (Math.Abs(pan) < 0.01f)
                    AddToMixer(provider);
                else
                    AddToMixerPanned(provider, pan);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayVoicedSequence failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Play a single note that decays away inside its own duration — the
        /// button-press shape. Noel, 2026-08-19: "for some tones I'd also
        /// consider adding more of a fade out (decay)... you might use it for a
        /// button press."
        /// </summary>
        internal static void PlayVoicedDecay(MeterVoice voice, int frequencyHz, int durationMs,
            float volume, float pan = 0f)
        {
            PlayVoiced(EarconVoices.DecayingOver(voice, durationMs), frequencyHz, durationMs, volume, pan);
        }

        /// <summary>
        /// Play a cadence where every note decays away inside its own step —
        /// the same shape as <see cref="PlayVoicedDecay"/>, applied to a
        /// sequence whose notes may differ in length.
        /// </summary>
        internal static void PlayVoicedDecaySequence(MeterVoice voice, (int freq, int ms)[] steps,
            float volume, float pan = 0f)
        {
            if (!EarconsEnabled) return;
            if (AlertMixer == null) { PlayVoicedSequence(voice, steps, volume, pan); return; }
            try
            {
                int totalMs = 0;
                foreach (var s in steps) totalMs += Math.Max(s.ms, 0);
                int totalSamples = SampleRate * (totalMs + VoicedTailMs) / 1000 + 1;
                var buffer = new float[Math.Max(totalSamples, 1)];

                int cursor = 0;
                foreach (var (freq, ms) in steps)
                {
                    int stepSamples = SampleRate * Math.Max(ms, 0) / 1000;
                    if (freq > 0 && ms > 0)
                    {
                        var shaped = EarconVoices.DecayingOver(voice, ms);
                        var mono = RenderStep(shaped, freq, ms, volume);
                        int n = Math.Min(mono.Length, buffer.Length - cursor);
                        for (int i = 0; i < n; i++)
                            buffer[cursor + i] += mono[i];
                    }
                    cursor += stepSamples;
                }

                var provider = new RenderedSampleProvider(buffer);
                if (Math.Abs(pan) < 0.01f)
                    AddToMixer(provider);
                else
                    AddToMixerPanned(provider, pan);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayVoicedDecaySequence failed: {ex.Message}");
            }
        }

        #endregion

        // PlayToneSequence — the bare-sine cadence primitive every earcon used
        // to go through — was deleted in Sprint 32 Track E once the last caller
        // moved to the voiced path. Its convention survives verbatim in
        // RenderVoiced: a step is (frequency, milliseconds) and frequency zero
        // means silence, so an earcon reads the same as it always did. Keeping
        // the old primitive around "in case" is exactly how the app ended up
        // with three additive synthesisers; a single unbroken sine is still one
        // call away as PlayTone.

        internal static void PlayChirp(int startHz, int endHz, int durationMs, float volume)
        {
            if (AlertMixer == null) { FallbackBeep((startHz + endHz) / 2, durationMs); return; }
            try
            {
                var chirp = new ChirpSampleProvider(SampleRate, startHz, endHz, durationMs, volume);
                AddToMixer(chirp);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayChirp failed: {ex.Message}");
                FallbackBeep((startHz + endHz) / 2, durationMs);
            }
        }

        private static void PlayChirpPanned(int startHz, int endHz, int durationMs, float volume, float pan)
        {
            if (AlertMixer == null) { FallbackBeep((startHz + endHz) / 2, durationMs); return; }
            try
            {
                var chirp = new ChirpSampleProvider(SampleRate, startHz, endHz, durationMs, volume);
                AddToMixerPanned(chirp, pan);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayChirpPanned failed: {ex.Message}");
            }
        }

        private static void PlayCachedSound(CachedSound sound)
        {
            if (AlertMixer == null) return;
            try
            {
                var provider = new CachedSoundSampleProvider(sound);
                AddToMixer(provider);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayCachedSound failed: {ex.Message}");
            }
        }

        private static void PlayCachedSoundPanned(CachedSound sound, float pan)
        {
            if (AlertMixer == null) return;
            try
            {
                var provider = new CachedSoundSampleProvider(sound);
                AddToMixerPanned(provider, pan);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayCachedSoundPanned failed: {ex.Message}");
            }
        }

        private static void PlayCachedSoundReversedPanned(CachedSound sound, float pan)
        {
            if (AlertMixer == null) return;
            try
            {
                var provider = new ReversedCachedSoundSampleProvider(sound);
                AddToMixerPanned(provider, pan);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayCachedSoundReversedPanned failed: {ex.Message}");
            }
        }

        private static CachedSound? LoadEmbeddedSound(string resourceName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    Trace.WriteLine($"EarconPlayer: embedded resource '{resourceName}' not found");
                    return null;
                }
                return new CachedSound(stream);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer: failed to load '{resourceName}': {ex.Message}");
                return null;
            }
        }

        private static void FallbackBeep(int frequencyHz, int durationMs)
        {
            // Render-off means NO audio path at all - Console.Beep is still an
            // audio device as far as a blind operator's ears are concerned.
            if (!Radios.OutputChannelRecorder.RenderEnabled) return;
            try { Console.Beep(frequencyHz, durationMs); }
            catch { }
        }

        #endregion

        #region Internal Types

        /// <summary>
        /// An independent audio output channel with its own WaveOutEvent, mixer, and volume.
        /// Each channel can target a different audio device.
        /// </summary>
        private class AudioChannel : IDisposable
        {
            private WaveOutEvent? _waveOut;
            private VolumeSampleProvider? _volume;
            private int _deviceNumber = -1;

            public MixingSampleProvider? Mixer { get; private set; }

            public float Volume
            {
                get => _volume?.Volume ?? 1.0f;
                set { if (_volume != null) _volume.Volume = Math.Clamp(value, 0f, 1f); }
            }

            public int DeviceNumber => _deviceNumber;

            /// <summary>
            /// The device's own report of how many bytes it has played, or -1
            /// when there is no device or it refuses. Sprint 32 Track H — see
            /// EarconPlayer.AlertPlayedBytes.
            /// </summary>
            public long PlayedBytes
            {
                get
                {
                    try { return _waveOut?.GetPosition() ?? -1; }
                    catch { return -1; }
                }
            }

            /// <summary>
            /// Depth of this device's buffer chain in milliseconds. Read from the
            /// device rather than restated as a constant, so changing
            /// BufferMilliseconds above cannot silently invalidate the drain wait
            /// that depends on it.
            /// </summary>
            public int OutputLatencyMs
            {
                get
                {
                    var wo = _waveOut;
                    if (wo == null) return 200;
                    int per = wo.BufferMilliseconds > 0 ? wo.BufferMilliseconds : 100;
                    int count = wo.NumberOfBuffers > 0 ? wo.NumberOfBuffers : 2;
                    return per * count;
                }
            }

            public bool Initialize(int deviceNumber)
            {
                _deviceNumber = deviceNumber;
                Mixer = new MixingSampleProvider(
                    WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, MixerChannels))
                {
                    ReadFully = true
                };
                _volume = new VolumeSampleProvider(Mixer);

                try
                {
                    _waveOut = new WaveOutEvent
                    {
                        DeviceNumber = deviceNumber,
                        BufferMilliseconds = 100
                    };
                    _waveOut.Init(_volume);
                    _waveOut.Play();
                    return true;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"AudioChannel.Initialize failed on device {deviceNumber}: {ex.Message}");
                    // Fall back to default device
                    try
                    {
                        _waveOut = new WaveOutEvent { BufferMilliseconds = 100 };
                        _waveOut.Init(_volume);
                        _waveOut.Play();
                        _deviceNumber = -1;
                        return true;
                    }
                    catch (Exception ex2)
                    {
                        Trace.WriteLine($"AudioChannel.Initialize fallback failed: {ex2.Message}");
                        return false;
                    }
                }
            }

            public void SetDevice(int deviceNumber)
            {
                _deviceNumber = deviceNumber;
                if (Mixer == null || _volume == null) return;
                try
                {
                    _waveOut?.Stop();
                    _waveOut?.Dispose();

                    _waveOut = new WaveOutEvent
                    {
                        DeviceNumber = deviceNumber,
                        BufferMilliseconds = 100
                    };
                    _waveOut.Init(_volume);
                    _waveOut.Play();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"AudioChannel.SetDevice failed: {ex.Message}");
                    // Fall back to default device
                    try
                    {
                        _waveOut = new WaveOutEvent { BufferMilliseconds = 100 };
                        _waveOut.Init(_volume);
                        _waveOut.Play();
                        _deviceNumber = -1;
                    }
                    catch { }
                }
            }

            public void Dispose()
            {
                _waveOut?.Stop();
                _waveOut?.Dispose();
                _waveOut = null;
                _volume = null;
                Mixer = null;
            }
        }

        /// <summary>
        /// Pre-loaded .wav audio data stored as mono float samples for instant playback.
        /// Mono storage allows flexible panning at playback time.
        /// </summary>
        private class CachedSound
        {
            public float[] AudioData { get; }
            public WaveFormat WaveFormat { get; }

            /// <summary>Create a gain-boosted copy of an existing CachedSound.</summary>
            /// <remarks>
            /// Hard-clamps, so a gain that overshoots destroys peaks rather
            /// than wrapping. Prefer <see cref="Normalized"/> where the point
            /// is to reach a level rather than to apply a specific gain — it
            /// works out the gain from the audio instead of being told one.
            /// </remarks>
            public CachedSound(CachedSound source, float gain)
            {
                WaveFormat = source.WaveFormat;
                AudioData = new float[source.AudioData.Length];
                for (int i = 0; i < AudioData.Length; i++)
                    AudioData[i] = Math.Clamp(source.AudioData[i] * gain, -1f, 1f);
            }

            /// <summary>
            /// A copy scaled so its loudest sample sits exactly at
            /// <paramref name="targetPeak"/>. Nothing clips, by construction.
            /// </summary>
            /// <remarks>
            /// Returns this instance unchanged when there is no peak to scale
            /// — digital silence has no level to normalise TO, and dividing by
            /// it would turn a silent file into full-scale noise.
            /// </remarks>
            public CachedSound Normalized(float targetPeak)
            {
                float peak = 0f;
                foreach (float s in AudioData)
                {
                    float a = Math.Abs(s);
                    if (a > peak) peak = a;
                }
                if (peak <= 0.0001f) return this;
                return new CachedSound(this, targetPeak / peak);
            }

            public CachedSound(Stream wavStream)
            {
                using var reader = new WaveFileReader(wavStream);
                var resampled = reader.ToSampleProvider();

                ISampleProvider source = resampled;
                if (resampled.WaveFormat.SampleRate != SampleRate)
                    source = new WdlResamplingSampleProvider(resampled, SampleRate);
                // Always store as mono for panning flexibility
                if (source.WaveFormat.Channels != 1)
                    source = source.ToMono();

                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

                var samples = new System.Collections.Generic.List<float>();
                var buffer = new float[SampleRate];
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    for (int i = 0; i < read; i++)
                        samples.Add(buffer[i]);
                }
                AudioData = samples.ToArray();
            }
        }

        /// <summary>Plays a CachedSound forward (one-shot).</summary>
        private class CachedSoundSampleProvider : ISampleProvider
        {
            private readonly CachedSound _sound;
            private int _position;

            public CachedSoundSampleProvider(CachedSound sound) { _sound = sound; }
            public WaveFormat WaveFormat => _sound.WaveFormat;

            // NAudio 3.0: ISampleProvider.Read takes a Span<float>. offset/count
            // are re-declared here so the body's index arithmetic is unchanged -
            // buffer[offset + n] indexes a Span exactly as it did an array.
            public int Read(Span<float> buffer)
            {
                int offset = 0;
                int count = buffer.Length;
                int available = _sound.AudioData.Length - _position;
                int toCopy = Math.Min(available, count);
                if (toCopy <= 0) return 0;
                _sound.AudioData.AsSpan(_position, toCopy).CopyTo(buffer.Slice(offset, toCopy));
                _position += toCopy;
                return toCopy;
            }
        }

        /// <summary>
        /// Plays a mono float buffer once. The output end of the voiced path:
        /// <see cref="RenderVoiced"/> composes the whole earcon offline, this
        /// hands it to the mixer. Rendering ahead of time rather than
        /// streaming is deliberate — an earcon is at most a second long, the
        /// cost is microseconds, and it means a cadence's timing is decided by
        /// array arithmetic instead of by how the audio callback happens to
        /// carve up buffers.
        /// </summary>
        private class RenderedSampleProvider : ISampleProvider
        {
            private readonly float[] _data;
            private int _position;

            public RenderedSampleProvider(float[] data) { _data = data; }
            public WaveFormat WaveFormat { get; } =
                WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

            public int Read(Span<float> buffer)
            {
                int available = _data.Length - _position;
                int toCopy = Math.Min(available, buffer.Length);
                if (toCopy <= 0) return 0;
                _data.AsSpan(_position, toCopy).CopyTo(buffer.Slice(0, toCopy));
                _position += toCopy;
                return toCopy;
            }
        }

        /// <summary>Plays a CachedSound in reverse (one-shot).</summary>
        private class ReversedCachedSoundSampleProvider : ISampleProvider
        {
            private readonly CachedSound _sound;
            private int _position;

            public ReversedCachedSoundSampleProvider(CachedSound sound) { _sound = sound; }
            public WaveFormat WaveFormat => _sound.WaveFormat;

            // NAudio 3.0: ISampleProvider.Read takes a Span<float>. offset/count
            // are re-declared here so the body's index arithmetic is unchanged -
            // buffer[offset + n] indexes a Span exactly as it did an array.
            public int Read(Span<float> buffer)
            {
                int offset = 0;
                int count = buffer.Length;
                int available = _sound.AudioData.Length - _position;
                int toCopy = Math.Min(available, count);
                if (toCopy <= 0) return 0;

                int sourceStart = _sound.AudioData.Length - 1 - _position;
                for (int i = 0; i < toCopy; i++)
                    buffer[offset + i] = _sound.AudioData[sourceStart - i];
                _position += toCopy;
                return toCopy;
            }
        }

        /// <summary>
        /// Wraps a mono source and outputs stereo with panning that sweeps linearly
        /// from startPan to endPan over the lifetime of the source.
        /// Pan range: -1 (full left) to +1 (full right).
        /// </summary>
        private class SweepPanningSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly float _startPan;
            private readonly float _endPan;
            private readonly int _totalSamples;
            private int _position;

            public WaveFormat WaveFormat { get; }

            public SweepPanningSampleProvider(ISampleProvider monoSource, float startPan, float endPan, int durationMs)
            {
                _source = monoSource;
                _startPan = startPan;
                _endPan = endPan;
                _totalSamples = monoSource.WaveFormat.SampleRate * durationMs / 1000;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(monoSource.WaveFormat.SampleRate, 2);
            }

            // NAudio 3.0: ISampleProvider.Read takes a Span<float>. offset/count
            // are re-declared here so the body's index arithmetic is unchanged -
            // buffer[offset + n] indexes a Span exactly as it did an array.
            public int Read(Span<float> buffer)
            {
                int offset = 0;
                int count = buffer.Length;
                int monoCount = count / 2;
                var monoBuffer = new float[monoCount];
                int monoRead = _source.Read(monoBuffer);
                if (monoRead == 0) return 0;

                for (int i = 0; i < monoRead; i++)
                {
                    float t = _totalSamples > 0
                        ? Math.Min((float)_position / _totalSamples, 1f)
                        : 0f;
                    float pan = _startPan + (_endPan - _startPan) * t;

                    // Linear panning: -1 = full left, +1 = full right
                    float right = (pan + 1f) / 2f;
                    float left = 1f - right;

                    buffer[offset + i * 2] = monoBuffer[i] * left;
                    buffer[offset + i * 2 + 1] = monoBuffer[i] * right;
                    _position++;
                }
                return monoRead * 2;
            }
        }

        /// <summary>
        /// Linear frequency sweep (chirp) sample provider (mono).
        /// Supports square wave and roughen (stutter gate) for harsh textures.
        /// </summary>
        private class ChirpSampleProvider : ISampleProvider
        {
            private readonly int _totalSamples;
            private readonly int _startHz;
            private readonly int _endHz;
            private readonly float _volume;
            private readonly int _fadeLength;
            private readonly bool _square;
            private readonly int _roughenOnSamples;
            private readonly int _roughenOffSamples;
            private int _position;
            private double _phase;

            public WaveFormat WaveFormat { get; }

            public ChirpSampleProvider(int sampleRate, int startHz, int endHz, int durationMs, float volume,
                bool square = false, int roughenOnMs = 0, int roughenOffMs = 0)
            {
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
                _totalSamples = sampleRate * durationMs / 1000;
                _startHz = startHz;
                _endHz = endHz;
                _volume = volume;
                _fadeLength = Math.Min(_totalSamples / 10, sampleRate / 100);
                _square = square;
                _roughenOnSamples = roughenOnMs > 0 ? sampleRate * roughenOnMs / 1000 : 0;
                _roughenOffSamples = roughenOffMs > 0 ? sampleRate * roughenOffMs / 1000 : 0;
            }

            // NAudio 3.0: ISampleProvider.Read takes a Span<float>. offset/count
            // are re-declared here so the body's index arithmetic is unchanged -
            // buffer[offset + n] indexes a Span exactly as it did an array.
            public int Read(Span<float> buffer)
            {
                int offset = 0;
                int count = buffer.Length;
                int available = _totalSamples - _position;
                int toCopy = Math.Min(available, count);
                if (toCopy <= 0) return 0;

                int roughenCycle = _roughenOnSamples + _roughenOffSamples;

                for (int i = 0; i < toCopy; i++)
                {
                    double t = (double)_position / _totalSamples;
                    double freq = _startHz + (_endHz - _startHz) * t;
                    _phase += 2.0 * Math.PI * freq / WaveFormat.SampleRate;

                    double sample = _square
                        ? (Math.Sin(_phase) >= 0 ? 1.0 : -1.0)
                        : Math.Sin(_phase);

                    double envelope = 1.0;
                    if (_position < _fadeLength)
                        envelope = (double)_position / _fadeLength;
                    else if (_position > _totalSamples - _fadeLength)
                        envelope = (double)(_totalSamples - _position) / _fadeLength;

                    // Roughen: stutter gate
                    if (roughenCycle > 0 && (_position % roughenCycle) >= _roughenOnSamples)
                        envelope = 0;

                    buffer[offset + i] = (float)(sample * envelope * _volume);
                    _position++;
                }
                return toCopy;
            }
        }

        /// <summary>
        /// Sprint 28 Phase 3 — white noise filtered through a time-varying band-pass
        /// biquad filter whose center frequency sweeps from startHz to endHz across
        /// the duration. Used to give expand/collapse chirps a distinctive noise
        /// texture that cuts through ambient RF hash where pure tones alone would
        /// get masked.
        ///
        /// Biquad band-pass coefficients recomputed per sample (cheap — a few math
        /// ops in a 350 ms window at 48 kHz = ~16.8k recomputations, trivial on
        /// modern hardware). Q fixed at ~4.3 (~1/3 octave bandwidth).
        /// </summary>
        private class BandPassNoiseSweepSampleProvider : ISampleProvider
        {
            private readonly int _totalSamples;
            private readonly int _startHz;
            private readonly int _endHz;
            private readonly float _volume;
            private readonly int _fadeLength;
            private readonly Random _rand = new();
            private int _position;
            // Biquad state
            private double _x1, _x2, _y1, _y2;
            // Q=2.0 gives ~1/2 octave bandwidth — wide enough that sufficient noise
            // energy passes through to be audibly present alongside the tone chirp.
            // Tuned down from 4.3 (~1/3 octave) on 2026-04-21 after the narrower
            // filter was inaudible even at 5x the nominal volume.
            private const double Q = 2.0;

            public WaveFormat WaveFormat { get; }

            public BandPassNoiseSweepSampleProvider(int sampleRate, int startHz, int endHz, int durationMs, float volume)
            {
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
                _totalSamples = sampleRate * durationMs / 1000;
                _startHz = startHz;
                _endHz = endHz;
                _volume = volume;
                _fadeLength = Math.Min(_totalSamples / 10, sampleRate / 100);
            }

            // NAudio 3.0: ISampleProvider.Read takes a Span<float>. offset/count
            // are re-declared here so the body's index arithmetic is unchanged -
            // buffer[offset + n] indexes a Span exactly as it did an array.
            public int Read(Span<float> buffer)
            {
                int offset = 0;
                int count = buffer.Length;
                int available = _totalSamples - _position;
                int toCopy = Math.Min(available, count);
                if (toCopy <= 0) return 0;

                int sampleRate = WaveFormat.SampleRate;

                for (int i = 0; i < toCopy; i++)
                {
                    // Generate white noise sample in [-1, 1]
                    double x0 = (_rand.NextDouble() * 2.0) - 1.0;

                    // Sweep center frequency linearly across duration
                    double t = (double)_position / _totalSamples;
                    double fc = _startHz + (_endHz - _startHz) * t;

                    // Compute biquad band-pass coefficients for this sample's fc
                    double w0 = 2.0 * Math.PI * fc / sampleRate;
                    double cosw0 = Math.Cos(w0);
                    double sinw0 = Math.Sin(w0);
                    double alpha = sinw0 / (2.0 * Q);
                    double a0 = 1.0 + alpha;
                    double b0 = alpha / a0;
                    double b2 = -alpha / a0;
                    double a1 = -2.0 * cosw0 / a0;
                    double a2 = (1.0 - alpha) / a0;

                    // Direct Form I
                    double y0 = b0 * x0 + b2 * _x2 - a1 * _y1 - a2 * _y2;
                    _x2 = _x1; _x1 = x0;
                    _y2 = _y1; _y1 = y0;

                    // Envelope — match chirp's fade shape for cohesion
                    double envelope = 1.0;
                    if (_position < _fadeLength)
                        envelope = (double)_position / _fadeLength;
                    else if (_position > _totalSamples - _fadeLength)
                        envelope = (double)(_totalSamples - _position) / _fadeLength;

                    buffer[offset + i] = (float)(y0 * envelope * _volume);
                    _position++;
                }
                return toCopy;
            }
        }


        #endregion
    }
}
