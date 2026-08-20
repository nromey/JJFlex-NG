using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
        /// Deliberately NOT per-sound. Five switches an operator can hold in
        /// their head beat sixty they cannot; each public earcon method
        /// declares its family and new earcons must pick one.
        ///
        /// Outside the categories, on purpose: CW notifications (their own
        /// switch on the Audio tab), typing sounds (their own mode setting),
        /// meter tones (their own engine and toggle), and the calibration /
        /// scratchpad sounds (developer-facing).
        /// </summary>
        public enum EarconCategory
        {
            /// <summary>Connect-phase counting tones and the success double-beep.</summary>
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
                // Create alert channel (earcons, beeps, PTT tones)
                _alertChannel = new AudioChannel();
                _alertChannel.Initialize(_alertDeviceNumber);

                // Create meter channel (continuous tones from MeterToneEngine)
                // If meter device is -1 (same as alerts), use alert device
                _meterChannel = new AudioChannel();
                int meterDevice = _meterDeviceNumber == -1 ? _alertDeviceNumber : _meterDeviceNumber;
                _meterChannel.Initialize(meterDevice);

                UpdateChannelVolumes();

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
            if (!On(EarconCategory.Transmit)) return;
            PlayVoiced(EarconVoices.WarningCalm, 800, 150, VolumeSoft);
        }

        /// <summary>Second PTT warning — insistent. Brighter, pulses twice, 1000 Hz.</summary>
        [Earcon("PTT warning 2, insistent", EarconCategory.Transmit, Order = 12,
            Description = "Second long-transmission warning. Brighter, and it pulses twice.")]
        public static void Warning2Beep()
        {
            if (!On(EarconCategory.Transmit)) return;
            PlayVoiced(EarconVoices.WarningInsistent, 1000, 200, VolumeNormal);
        }

        /// <summary>Last PTT warning — harsh, metallic, hammering, 1200 Hz.</summary>
        [Earcon("PTT warning 3, last call", EarconCategory.Transmit, Order = 13,
            Description = "Final long-transmission warning. Harsh, metallic, hammering.")]
        public static void OhCrapBeep()
        {
            if (!On(EarconCategory.Transmit)) return;
            PlayVoiced(EarconVoices.WarningUrgent, 1200, 250, VolumeStrong);
        }

        /// <summary>TX start tone — two discrete tones: 400Hz then 800Hz.</summary>
        [Earcon("Transmit start", EarconCategory.Transmit, Order = 1,
            Description = "Rising pair. You are transmitting.")]
        public static void TxStartTone()
        {
            if (!On(EarconCategory.Transmit)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (400, 50), (0, 20), (800, 50) }, VolumeNormal);
        }

        /// <summary>TX stop tone — two discrete tones: 800Hz then 400Hz.</summary>
        [Earcon("Transmit stop", EarconCategory.Transmit, Order = 2,
            Description = "Falling pair. You have stopped transmitting.")]
        public static void TxStopTone()
        {
            if (!On(EarconCategory.Transmit)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (800, 50), (0, 20), (400, 50) }, VolumeNormal);
        }

        /// <summary>Hard kill tone — two rapid descending beeps.</summary>
        [Earcon("Hard kill", EarconCategory.Transmit, Order = 3,
            Description = "Transmission was cut off rather than ended.")]
        public static void HardKillTone()
        {
            if (!On(EarconCategory.Transmit)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (1000, 100), (0, 30), (600, 200) }, VolumeStrong);
        }

        // Connect-phase counting tones for the state-aware connecting modal.
        // Same pitch repeated 1 / 2 / 3 times so the user hears progress as a
        // count, not as a melody. Pitch chosen mid-band (750 Hz) and softer
        // (0.35) so it doesn't compete with concurrent speech announcements.
        private const int ConnectPhaseTonePitchHz = 750;
        private const int ConnectPhaseToneMs = 70;
        private const int ConnectPhaseToneGapMs = 60;
        private const float ConnectPhaseToneVolume = VolumeSoft;

        /// <summary>Connect phase 1 — single 750 Hz tone (TLS / SmartLink connect).</summary>
        [Earcon("Connect step 1", EarconCategory.Connection, Order = 1,
            Description = "One counting tone. The link is up and negotiating.")]
        public static void ConnectPhase1Tone()
        {
            if (!On(EarconCategory.Connection)) return;
            PlayVoiced(EarconVoices.Plain, ConnectPhaseTonePitchHz, ConnectPhaseToneMs, ConnectPhaseToneVolume);
        }

        /// <summary>Connect phase 2 — two 750 Hz tones (transport up, waiting for slice).</summary>
        [Earcon("Connect step 2", EarconCategory.Connection, Order = 2,
            Description = "Two counting tones. Transport is up, waiting for a slice.")]
        public static void ConnectPhase2Tone()
        {
            if (!On(EarconCategory.Connection)) return;
            PlayVoicedSequence(EarconVoices.Plain, new[]
            {
                (ConnectPhaseTonePitchHz, ConnectPhaseToneMs),
                (0, ConnectPhaseToneGapMs),
                (ConnectPhaseTonePitchHz, ConnectPhaseToneMs)
            }, ConnectPhaseToneVolume);
        }

        /// <summary>Connect phase 3 — three 750 Hz tones (slice acquired, station name pending).</summary>
        [Earcon("Connect step 3", EarconCategory.Connection, Order = 3,
            Description = "Three counting tones. Slice acquired, station name pending.")]
        public static void ConnectPhase3Tone()
        {
            if (!On(EarconCategory.Connection)) return;
            PlayVoicedSequence(EarconVoices.Plain, new[]
            {
                (ConnectPhaseTonePitchHz, ConnectPhaseToneMs),
                (0, ConnectPhaseToneGapMs),
                (ConnectPhaseTonePitchHz, ConnectPhaseToneMs),
                (0, ConnectPhaseToneGapMs),
                (ConnectPhaseTonePitchHz, ConnectPhaseToneMs)
            }, ConnectPhaseToneVolume);
        }

        /// <summary>
        /// Connect success — the signature double-beep (QB Track A,
        /// 2026-08-07, memory: project_connect_earcon_signature_sound.md).
        /// Same pitch and cadence as the phase-2 counting tone users already
        /// know, slightly louder because it marks ARRIVAL rather than
        /// background progress. Fired from MainWindow.PowerNowOn — the one
        /// point every successful connect path (picker local, picker remote,
        /// auto-connect, reconnect) flows through — so fast LAN connects are
        /// no longer silent (the phase tones skip any phase under 500ms).
        /// </summary>
        [Earcon("Connect success", EarconCategory.Connection, Order = 4,
            Description = "The signature double-beep. Every successful connect ends here, "
                        + "however it started.")]
        public static void ConnectSuccessTone()
        {
            if (!On(EarconCategory.Connection)) return;
            PlayVoicedSequence(EarconVoices.Plain, new[]
            {
                (ConnectPhaseTonePitchHz, ConnectPhaseToneMs),
                (0, ConnectPhaseToneGapMs),
                (ConnectPhaseTonePitchHz, ConnectPhaseToneMs)
            }, VolumeStrong);
        }

        /// <summary>Parameterized connect-phase counting tone (1..N identical tones).</summary>
        public static void ConnectPhaseTone(int count)
        {
            if (!On(EarconCategory.Connection)) return;
            if (count <= 0) return;
            var seq = new (int, int)[Math.Max(1, count * 2 - 1)];
            int idx = 0;
            for (int i = 0; i < count; i++)
            {
                if (i > 0) seq[idx++] = (0, ConnectPhaseToneGapMs);
                seq[idx++] = (ConnectPhaseTonePitchHz, ConnectPhaseToneMs);
            }
            PlayVoicedSequence(EarconVoices.Plain, seq, ConnectPhaseToneVolume);
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
            if (!On(EarconCategory.CommandsAndConfirmations)) return;
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
            if (!On(EarconCategory.TuningAndFilters)) return;
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
            if (!On(EarconCategory.TuningAndFilters)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (600, 50), (0, 30), (600, 50) }, VolumeNormal);
        }

        /// <summary>Filter edge enter tone — plays mode-enter.wav.</summary>
        [Earcon("Filter edge adjust, entering", EarconCategory.TuningAndFilters, Order = 11,
            Description = "Filter edge adjustment mode has started.")]
        public static void FilterEdgeEnterTone()
        {
            if (!On(EarconCategory.TuningAndFilters)) return;
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
            if (!On(EarconCategory.TuningAndFilters)) return;
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
            if (!On(EarconCategory.TuningAndFilters)) return;
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
            if (!On(EarconCategory.TuningAndFilters)) return;
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
            if (!On(EarconCategory.TuningAndFilters)) return;
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
            if (!On(EarconCategory.TuningAndFilters)) return;
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
            if (!On(EarconCategory.TuningAndFilters)) return;
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
            if (!EarconsEnabled || AlertMixer == null) return;
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
            if (!On(EarconCategory.CommandsAndConfirmations)) return;
            PlayChirp(400, 600, 80, VolumeNormal);
        }

        /// <summary>Double ascending beep — feature toggled ON.</summary>
        [Earcon("Feature on", EarconCategory.CommandsAndConfirmations, Order = 11,
            Description = "Rising pair. A toggle just turned on.")]
        public static void FeatureOnTone()
        {
            if (!On(EarconCategory.CommandsAndConfirmations)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (500, 60), (0, 40), (700, 60) }, VolumeNormal);
        }

        /// <summary>Double descending beep — feature toggled OFF.</summary>
        [Earcon("Feature off", EarconCategory.CommandsAndConfirmations, Order = 12,
            Description = "Falling pair. A toggle just turned off.")]
        public static void FeatureOffTone()
        {
            if (!On(EarconCategory.CommandsAndConfirmations)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (700, 60), (0, 40), (500, 60) }, VolumeNormal);
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
            if (!On(EarconCategory.CommandsAndConfirmations)) return;
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
            if (!On(EarconCategory.CommandsAndConfirmations)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (940, 55), (0, 30), (785, 55), (0, 30), (625, 55) }, VolumeNormal);
        }

        /// <summary>Double ascending ding — dialog/popup opened.</summary>
        [Earcon("Dialog opened", EarconCategory.DialogsAndPanels, Order = 1,
            Description = "Rising pair when a dialog or popup opens.")]
        public static void DialogOpenTone()
        {
            if (!On(EarconCategory.DialogsAndPanels)) return;
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (600, 50), (0, 30), (900, 50) }, VolumeSoft);
        }

        /// <summary>Double descending ding — dialog/popup closed.</summary>
        [Earcon("Dialog closed", EarconCategory.DialogsAndPanels, Order = 2,
            Description = "Falling pair when a dialog or popup closes.")]
        public static void DialogCloseTone()
        {
            if (!On(EarconCategory.DialogsAndPanels)) return;
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
            if (!On(EarconCategory.Warnings)) return;
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
            if (!On(EarconCategory.Warnings)) return;
            PlayVoiced(EarconVoices.Alarm, 800, 750, VolumeStrong);
        }

        /// <summary>Low buzz — invalid leader key.</summary>
        [Earcon("JJ key not recognised", EarconCategory.CommandsAndConfirmations, Order = 24,
            Description = "Low thunk. That key means nothing in the leader layer.")]
        public static void LeaderInvalidTone()
        {
            if (!On(EarconCategory.CommandsAndConfirmations)) return;
            PlayVoicedDecay(EarconVoices.Press, 200, 100, VolumeNormal);
        }

        /// <summary>Soft descending chirp — leader key cancelled.</summary>
        [Earcon("JJ key cancelled", EarconCategory.CommandsAndConfirmations, Order = 23,
            Description = "Soft falling chirp. The leader layer gave up waiting.")]
        public static void LeaderCancelTone()
        {
            if (!On(EarconCategory.CommandsAndConfirmations)) return;
            PlayChirp(500, 300, 150, VolumeSoft);
        }

        /// <summary>Double chime — leader key help requested.</summary>
        [Earcon("JJ key help", EarconCategory.CommandsAndConfirmations, Order = 22,
            Description = "Double chime. The leader layer is about to list what it can do.")]
        public static void LeaderHelpTone()
        {
            if (!On(EarconCategory.CommandsAndConfirmations)) return;
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
            if (!On(EarconCategory.DialogsAndPanels)) return;
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
            if (!On(EarconCategory.DialogsAndPanels)) return;
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
            if (!On(EarconCategory.DialogsAndPanels)) return;
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
            if (!On(EarconCategory.Transmit)) return;
            StopATUProgressEarcon(); // Stop any existing progress earcon
            if (AlertMixer == null) return;
            try
            {
                _atuProgressProvider = new VoicedToneSampleProvider(450f, VolumeSoft)
                {
                    Voice = MeterVoiceLibrary.Resolve(
                        MeterVoiceLibrary.FromLegacyWaveform(WaveformType.FastPulse)),
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
            var provider = _atuProgressProvider;
            if (provider == null) return;
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
            if (!On(EarconCategory.Transmit)) return;
            // C5=523, E5=659, G5=784 — rising major triad
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (523, 50), (659, 50), (784, 80) }, VolumeNormal);
        }

        /// <summary>ATU tune failed — descending minor E-C-A (~200ms total).</summary>
        [Earcon("ATU tune failed", EarconCategory.Transmit, Order = 33,
            Description = "Descending minor. The tuner gave up.")]
        public static void ATUFailTone()
        {
            if (!On(EarconCategory.Transmit)) return;
            // E5=659, C5=523, A4=440 — descending
            PlayVoicedDecaySequence(EarconVoices.Press,
                new[] { (659, 60), (523, 60), (440, 100) }, VolumeStrong);
        }

        /// <summary>Tune carrier on — short rising chirp.</summary>
        [Earcon("Tune carrier on", EarconCategory.Transmit, Order = 21,
            Description = "The tune carrier has started.")]
        public static void TuneOnTone()
        {
            if (!On(EarconCategory.Transmit)) return;
            PlayChirp(400, 700, 100, VolumeNormal);
        }

        /// <summary>Tune carrier off — short falling chirp.</summary>
        [Earcon("Tune carrier off", EarconCategory.Transmit, Order = 22,
            Description = "The tune carrier has stopped.")]
        public static void TuneOffTone()
        {
            if (!On(EarconCategory.Transmit)) return;
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
            if (!On(EarconCategory.TuningAndFilters)) return;
            PlayVoicedDecay(EarconVoices.Chime, 1000, 250, VolumeNormal);
        }

        /// <summary>
        /// Play a tone with specific parameters and panning. Used by earcon scratchpad.
        /// </summary>
        public static void PlayScratchpadTone(int freqHz, int durationMs, float volume, float pan)
        {
            PlayTonePanned(freqHz, durationMs, volume, pan);
        }

        /// <summary>
        /// Play a chirp with specific parameters and panning. Used by earcon scratchpad.
        /// </summary>
        public static void PlayScratchpadChirp(int startHz, int endHz, int durationMs, float volume, float pan)
        {
            PlayChirpPanned(startHz, endHz, durationMs, volume, pan);
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
            switch (mode)
            {
                case TypingSoundMode.Beep:
                    // Random musical note from C4-C8 (4 octaves, MIDI 60-108)
                    int midiNote = 60 + _keyRandom.Next(49); // 49 semitones = 4 octaves
                    int freq = (int)(440.0 * Math.Pow(2.0, (midiNote - 69) / 12.0));
                    PlayTone(freq, 30, 0.25f);
                    break;
                case TypingSoundMode.SingleTone:
                    PlayTone(800, 30, 0.25f);
                    break;
                case TypingSoundMode.RandomTones:
                    PlayTone(_keyRandom.Next(300, 2001), 30, 0.25f);
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

        /// <summary>
        /// Play a random mechanical keyboard sound from the loaded pool.
        /// </summary>
        private static void PlayMechanicalKey()
        {
            if (_keyboardSounds == null || _keyboardSounds.Length == 0)
            {
                // Fallback: short click
                PlayTone(800, 15, 0.3f);
                return;
            }
            int idx = _keyRandom.Next(_keyboardSounds.Length);
            var sound = _keyboardSounds[idx];
            // Keyboard sounds are low amplitude — boost 8x for audibility over radio audio
            var boosted = new CachedSound(sound, 8.0f);
            PlayCachedSound(boosted);
        }

        /// <summary>
        /// Play a DTMF dual-tone for the given digit (50ms burst).
        /// </summary>
        private static void PlayDtmfTone(char digit)
        {
            if (!DtmfFreqs.TryGetValue(digit, out var freqs))
            {
                PlayTone(800, 30, 0.25f); // fallback for non-digit chars
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
                        sounds.Add(new CachedSound(stream));
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

        /// <summary>Add a mono source to the alert channel stereo mixer (auto-converts to stereo center).</summary>
        private static void AddToMixer(ISampleProvider monoSource)
        {
            if (!EarconsEnabled || AlertMixer == null) return;
            if (monoSource.WaveFormat.Channels == 1)
                AlertMixer.AddMixerInput(new MonoToStereoSampleProvider(monoSource));
            else
                AlertMixer.AddMixerInput(monoSource);
        }

        /// <summary>Add a mono source to the alert channel stereo mixer with panning (-1 left, 0 center, +1 right).</summary>
        private static void AddToMixerPanned(ISampleProvider monoSource, float pan)
        {
            if (!EarconsEnabled || AlertMixer == null) return;
            // PanningSampleProvider takes mono → outputs stereo
            if (monoSource.WaveFormat.Channels != 1)
                monoSource = monoSource.ToMono();
            var panned = new PanningSampleProvider(monoSource) { Pan = pan };
            AlertMixer.AddMixerInput(panned);
        }

        /// <summary>Add a mono source with panning that sweeps from startPan to endPan over durationMs.</summary>
        private static void AddToMixerSweptPan(ISampleProvider monoSource, float startPan, float endPan, int durationMs)
        {
            if (!EarconsEnabled || AlertMixer == null) return;
            if (monoSource.WaveFormat.Channels != 1)
                monoSource = monoSource.ToMono();
            var swept = new SweepPanningSampleProvider(monoSource, startPan, endPan, durationMs);
            AlertMixer.AddMixerInput(swept);
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
        {
            if (sequence == null) throw new ArgumentNullException(nameof(sequence));
            if (!EarconsEnabled || AlertMixer == null) return NullCancellable.Instance;
            try
            {
                var cancellable = new CancellableCwProvider(sequence);
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
                    var mono = VoicedToneSampleProvider.RenderMono(voice, freq, ms, volume);
                    int n = Math.Min(mono.Length, buffer.Length - cursor);
                    for (int i = 0; i < n; i++)
                        buffer[cursor + i] += mono[i];
                }
                cursor += stepSamples;
            }
            return buffer;
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
                        var mono = VoicedToneSampleProvider.RenderMono(shaped, freq, ms, volume);
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
            public CachedSound(CachedSound source, float gain)
            {
                WaveFormat = source.WaveFormat;
                AudioData = new float[source.AudioData.Length];
                for (int i = 0; i < AudioData.Length; i++)
                    AudioData[i] = Math.Clamp(source.AudioData[i] * gain, -1f, 1f);
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
