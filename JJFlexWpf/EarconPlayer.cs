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

        /// <summary>Play a warning beep at the given frequency and duration.</summary>
        public static void Beep(int frequencyHz = 800, int durationMs = 150)
        {
            PlayTone(frequencyHz, durationMs, 0.6f);
        }

        public static void Warning1Beep() => Beep(800, 150);
        public static void Warning2Beep() => Beep(1000, 200);
        public static void OhCrapBeep() => Beep(1200, 250);

        /// <summary>TX start tone — two discrete tones: 400Hz then 800Hz.</summary>
        public static void TxStartTone()
        {
            PlayToneSequence(new[] { (400, 50), (0, 20), (800, 50) }, 0.5f);
        }

        /// <summary>TX stop tone — two discrete tones: 800Hz then 400Hz.</summary>
        public static void TxStopTone()
        {
            PlayToneSequence(new[] { (800, 50), (0, 20), (400, 50) }, 0.5f);
        }

        /// <summary>Hard kill tone — two rapid descending beeps.</summary>
        public static void HardKillTone()
        {
            PlayToneSequence(new[] { (1000, 100), (0, 30), (600, 200) }, 0.6f);
        }

        /// <summary>Play a frequency sweep (chirp) from startHz to endHz.</summary>
        public static void Chirp(int startHz, int endHz, int durationMs)
        {
            PlayChirp(startHz, endHz, durationMs, 0.6f);
        }

        /// <summary>Confirmation tone — plays confirm.wav.</summary>
        public static void ConfirmTone()
        {
            if (_confirmSound != null)
                PlayCachedSound(_confirmSound);
            else
                PlayToneSequence(new[] { (800, 25), (0, 30), (800, 25), (0, 30), (800, 25) }, 0.5f);
        }

        /// <summary>Typewriter bell — plays at end of frequency entry in mechanical keyboard mode.</summary>
        public static void TypewriterBellTone()
        {
            if (_typewriterBellSound != null)
                PlayCachedSound(_typewriterBellSound);
            else
                DingTone(); // fallback
        }

        /// <summary>Band boundary beep — 600 Hz double-beep.</summary>
        public static void BandBoundaryBeep()
        {
            PlayToneSequence(new[] { (600, 50), (0, 30), (600, 50) }, 0.6f);
        }

        /// <summary>Filter edge enter tone — plays mode-enter.wav.</summary>
        public static void FilterEdgeEnterTone()
        {
            if (_modeEnterSound != null)
                PlayCachedSound(_modeEnterSound);
            else
                PlayTone(1000, 80, 0.4f);
        }

        /// <summary>Filter edge exit tone — plays mode-exit.wav.</summary>
        public static void FilterEdgeExitTone()
        {
            if (_modeExitSound != null)
                PlayCachedSound(_modeExitSound);
            else
                PlayTone(600, 80, 0.4f);
        }

        /// <summary>
        /// Filter edge move tone — panned click on each filter edge adjustment.
        /// Left edge pans left, right edge pans right.
        /// </summary>
        /// <param name="isLowEdge">True for low/left edge, false for high/right edge.</param>
        public static void FilterEdgeMoveTone(bool isLowEdge)
        {
            float pan = isLowEdge ? -0.7f : 0.7f;
            if (_slideSound != null)
                PlayCachedSoundPanned(_slideSound, pan);
            else if (_filterEdgeMoveSound != null)
                PlayCachedSoundPanned(_filterEdgeMoveSound, pan);
            else
                PlayTonePanned(800, 20, 0.3f, pan);
        }

        /// <summary>
        /// Filter edge move tone — unpanned (for when edge isn't known).
        /// </summary>
        public static void FilterEdgeMoveTone()
        {
            if (_slideSound != null)
                PlayCachedSound(_slideSound);
            else if (_filterEdgeMoveSound != null)
                PlayCachedSound(_filterEdgeMoveSound);
            else
                PlayTone(800, 20, 0.3f);
        }

        /// <summary>
        /// Filter boundary hit — zip sound panned to the edge that hit the boundary.
        /// Right boundary: zip01 forward (ascending) panned right.
        /// Left boundary: zip01 reversed (descending) panned left.
        /// </summary>
        /// <param name="isLowEdge">True for low/left boundary, false for high/right boundary.</param>
        public static void FilterBoundaryHitTone(bool isLowEdge)
        {
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
                PlayTonePanned(isLowEdge ? 400 : 800, 80, 0.5f, pan);
            }
        }

        /// <summary>
        /// Filter squeeze tone — edges closing in. Single descending sweep 800→200Hz, 300ms.
        /// </summary>
        public static void FilterSqueezeTone()
        {
            if (AlertMixer == null) return;
            try
            {
                const int durationMs = 300;
                var down = new ChirpSampleProvider(SampleRate, 800, 200, durationMs, 0.4f);
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
        public static void FilterStretchTone()
        {
            if (AlertMixer == null) return;
            try
            {
                const int durationMs = 300;
                // Primary ascending sweep
                var primary = new ChirpSampleProvider(SampleRate, 200, 800, durationMs, 0.4f);
                AddToMixer(primary);
                // Secondary tone 100Hz above — same sweep shifted up
                var secondary = new ChirpSampleProvider(SampleRate, 300, 900, durationMs, 0.3f);
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
        public static void LeaderEnterTone()
        {
            PlayChirp(400, 600, 80, 0.3f);
        }

        /// <summary>Double ascending beep — feature toggled ON.</summary>
        public static void FeatureOnTone()
        {
            PlayToneSequence(new[] { (500, 60), (0, 40), (700, 60) }, 0.3f);
        }

        /// <summary>Double descending beep — feature toggled OFF.</summary>
        public static void FeatureOffTone()
        {
            PlayToneSequence(new[] { (700, 60), (0, 40), (500, 60) }, 0.3f);
        }

        /// <summary>Double ascending ding — dialog/popup opened.</summary>
        public static void DialogOpenTone()
        {
            PlayToneSequence(new[] { (600, 50), (0, 30), (900, 50) }, 0.25f);
        }

        /// <summary>Double descending ding — dialog/popup closed.</summary>
        public static void DialogCloseTone()
        {
            PlayToneSequence(new[] { (900, 50), (0, 30), (600, 50) }, 0.25f);
        }

        /// <summary>Low buzz — invalid leader key.</summary>
        public static void LeaderInvalidTone()
        {
            PlayTone(200, 100, 0.4f);
        }

        /// <summary>Soft descending chirp — leader key cancelled.</summary>
        public static void LeaderCancelTone()
        {
            PlayChirp(500, 300, 150, 0.2f);
        }

        /// <summary>Double chime — leader key help requested.</summary>
        public static void LeaderHelpTone()
        {
            PlayToneSequence(new[] { (800, 80), (0, 40), (1000, 80) }, 0.25f);
        }

        #region ATU Tune Earcons

        // Dedicated provider for ATU progress — short-lived, added/removed per tune cycle
        private static ContinuousToneSampleProvider? _atuProgressProvider;
        private static ISampleProvider? _atuProgressStereoWrapper;

        /// <summary>
        /// Start the ATU progress earcon — FastPulse at 450Hz, moderate volume.
        /// Loops until StopATUProgressEarcon() is called.
        /// </summary>
        public static void StartATUProgressEarcon()
        {
            StopATUProgressEarcon(); // Stop any existing progress earcon
            if (AlertMixer == null) return;
            try
            {
                _atuProgressProvider = new ContinuousToneSampleProvider(450f, 0.25f)
                {
                    Waveform = WaveformType.FastPulse,
                    Active = true
                };
                _atuProgressStereoWrapper = new MonoToStereoSampleProvider(_atuProgressProvider);
                AlertMixer.AddMixerInput(_atuProgressStereoWrapper);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.StartATUProgressEarcon failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop the ATU progress earcon. Deactivates (10ms fade) then removes from mixer.
        /// </summary>
        public static void StopATUProgressEarcon()
        {
            if (_atuProgressProvider != null)
            {
                _atuProgressProvider.Active = false;
            }
            if (_atuProgressStereoWrapper != null && AlertMixer != null)
            {
                var wrapper = _atuProgressStereoWrapper;
                _atuProgressStereoWrapper = null;
                _atuProgressProvider = null;
                // Brief delay for fade-out, then remove from mixer
                System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
                {
                    try { AlertMixer?.RemoveMixerInput(wrapper); }
                    catch { }
                });
            }
        }

        /// <summary>ATU tune successful — rising major arpeggio C-E-G (~150ms total).</summary>
        public static void ATUSuccessTone()
        {
            // C5=523, E5=659, G5=784 — rising major triad
            PlayToneSequence(new[] { (523, 50), (659, 50), (784, 80) }, 0.4f);
        }

        /// <summary>ATU tune failed — descending minor E-C-A (~200ms total).</summary>
        public static void ATUFailTone()
        {
            // E5=659, C5=523, A4=440 — descending
            PlayToneSequence(new[] { (659, 60), (523, 60), (440, 100) }, 0.4f);
        }

        /// <summary>Tune carrier on — short rising chirp.</summary>
        public static void TuneOnTone()
        {
            PlayChirp(400, 700, 100, 0.3f);
        }

        /// <summary>Tune carrier off — short falling chirp.</summary>
        public static void TuneOffTone()
        {
            PlayChirp(700, 400, 100, 0.3f);
        }

        #endregion

        /// <summary>
        /// Confirmation ding with decay — a clear, pleasant tone that cuts through radio audio.
        /// 1000 Hz fundamental + soft octave harmonic, exponential decay over 250ms.
        /// Use for frequency entry confirmation and similar confirmations.
        /// </summary>
        public static void DingTone()
        {
            if (AlertMixer == null) return;
            try
            {
                var ding = new DingToneSampleProvider(SampleRate, 1000, 250, 0.4f);
                AddToMixer(ding);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.DingTone failed: {ex.Message}");
            }
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

        private static void PlayToneSequence((int freq, int ms)[] tones, float volume)
        {
            if (AlertMixer == null) return;
            try
            {
                var monoFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);
                var providers = new ISampleProvider[tones.Length];
                for (int i = 0; i < tones.Length; i++)
                {
                    if (tones[i].freq == 0)
                    {
                        providers[i] = new SilenceProvider(monoFormat)
                            .ToSampleProvider()
                            .Take(TimeSpan.FromMilliseconds(tones[i].ms));
                    }
                    else
                    {
                        var signal = new SignalGenerator(SampleRate, 1)
                        {
                            Type = SignalGeneratorType.Sin,
                            Frequency = tones[i].freq,
                            Gain = volume
                        };
                        providers[i] = signal.Take(TimeSpan.FromMilliseconds(tones[i].ms));
                    }
                }
                var concat = new ConcatenatingSampleProvider(providers);
                AddToMixer(concat);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayToneSequence failed: {ex.Message}");
            }
        }

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
                        DesiredLatency = 100
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
                        _waveOut = new WaveOutEvent { DesiredLatency = 100 };
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
                        DesiredLatency = 100
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
                        _waveOut = new WaveOutEvent { DesiredLatency = 100 };
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
        /// Sine tone with exponential decay — produces a clear "ding" that fades naturally.
        /// Includes a soft octave harmonic for warmth.
        /// </summary>
        private class DingToneSampleProvider : ISampleProvider
        {
            private readonly int _totalSamples;
            private readonly float _frequency;
            private readonly float _volume;
            private readonly float _decayRate;
            private int _position;
            private double _phase;
            private double _phase2; // octave harmonic

            public WaveFormat WaveFormat { get; }

            public DingToneSampleProvider(int sampleRate, float frequency, int durationMs, float volume)
            {
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
                _totalSamples = sampleRate * durationMs / 1000;
                _frequency = frequency;
                _volume = volume;
                // Decay rate: envelope reaches ~1% at end of duration
                _decayRate = -MathF.Log(0.01f) / _totalSamples;
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int available = _totalSamples - _position;
                int toCopy = Math.Min(available, count);
                if (toCopy <= 0) return 0;

                double phaseInc = 2.0 * Math.PI * _frequency / WaveFormat.SampleRate;
                double phaseInc2 = 2.0 * Math.PI * (_frequency * 2) / WaveFormat.SampleRate;

                for (int i = 0; i < toCopy; i++)
                {
                    double envelope = Math.Exp(-_decayRate * _position);

                    // Fundamental (80%) + soft octave harmonic (20%)
                    double sample = Math.Sin(_phase) * 0.8 + Math.Sin(_phase2) * 0.2;

                    buffer[offset + i] = (float)(sample * envelope * _volume);
                    _phase += phaseInc;
                    _phase2 += phaseInc2;
                    _position++;
                }
                return toCopy;
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
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
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

            public int Read(float[] buffer, int offset, int count)
            {
                int available = _sound.AudioData.Length - _position;
                int toCopy = Math.Min(available, count);
                if (toCopy <= 0) return 0;
                Array.Copy(_sound.AudioData, _position, buffer, offset, toCopy);
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

            public int Read(float[] buffer, int offset, int count)
            {
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

            public int Read(float[] buffer, int offset, int count)
            {
                int monoCount = count / 2;
                var monoBuffer = new float[monoCount];
                int monoRead = _source.Read(monoBuffer, 0, monoCount);
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

            public int Read(float[] buffer, int offset, int count)
            {
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

        #endregion
    }
}
