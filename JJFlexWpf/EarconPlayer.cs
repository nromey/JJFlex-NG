using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JJFlexWpf
{
    /// <summary>
    /// Synthesized beep tones and .wav earcons for PTT warnings and UI feedback.
    /// Uses NAudio for playback — persistent WaveOutEvent with MixingSampleProvider
    /// allows overlapping sounds without creating/disposing players per tone.
    /// </summary>
    public static class EarconPlayer
    {
        private static WaveOutEvent? _waveOut;
        private static MixingSampleProvider? _mixer;
        private static bool _initialized;

        // Cached embedded sounds
        private static CachedSound? _clickSound;
        private static CachedSound? _confirmSound;
        private static CachedSound? _filterEdgeMoveSound;
        private static CachedSound? _modeEnterSound;
        private static CachedSound? _modeExitSound;

        private const int SampleRate = 44100;
        private const int Channels = 1;

        /// <summary>
        /// Initialize the audio engine. Call once at startup.
        /// Loads embedded .wav resources and creates the persistent mixer/output.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            try
            {
                _mixer = new MixingSampleProvider(
                    WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
                {
                    ReadFully = true // keep mixer alive when no inputs
                };

                _waveOut = new WaveOutEvent
                {
                    DesiredLatency = 100
                };
                _waveOut.Init(_mixer);
                _waveOut.Play();

                // Load embedded sounds
                _clickSound = LoadEmbeddedSound("JJFlexWpf.Sounds.click.wav");
                _confirmSound = LoadEmbeddedSound("JJFlexWpf.Sounds.confirm.wav");
                _filterEdgeMoveSound = LoadEmbeddedSound("JJFlexWpf.Sounds.filter-edge-move.wav");
                _modeEnterSound = LoadEmbeddedSound("JJFlexWpf.Sounds.mode-enter.wav");
                _modeExitSound = LoadEmbeddedSound("JJFlexWpf.Sounds.mode-exit.wav");

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
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            _mixer = null;
            _initialized = false;
        }

        /// <summary>
        /// Play a warning beep at the given frequency and duration.
        /// </summary>
        public static void Beep(int frequencyHz = 800, int durationMs = 150)
        {
            PlayTone(frequencyHz, durationMs, 0.6f);
        }

        /// <summary>
        /// Warning1 beep — moderate urgency (800 Hz, 150ms).
        /// </summary>
        public static void Warning1Beep() => Beep(800, 150);

        /// <summary>
        /// Warning2 beep — higher urgency (1000 Hz, 200ms).
        /// </summary>
        public static void Warning2Beep() => Beep(1000, 200);

        /// <summary>
        /// OhCrap beep — critical urgency (1200 Hz, 250ms).
        /// </summary>
        public static void OhCrapBeep() => Beep(1200, 250);

        /// <summary>
        /// TX start tone — two discrete tones: 400Hz then 800Hz.
        /// </summary>
        public static void TxStartTone()
        {
            PlayToneSequence(new[] { (400, 50), (0, 20), (800, 50) }, 0.5f);
        }

        /// <summary>
        /// TX stop tone — two discrete tones: 800Hz then 400Hz.
        /// </summary>
        public static void TxStopTone()
        {
            PlayToneSequence(new[] { (800, 50), (0, 20), (400, 50) }, 0.5f);
        }

        /// <summary>
        /// Hard kill tone — two rapid descending beeps.
        /// </summary>
        public static void HardKillTone()
        {
            PlayToneSequence(new[] { (1000, 100), (0, 30), (600, 200) }, 0.6f);
        }

        /// <summary>
        /// Play a frequency sweep (chirp) from startHz to endHz over durationMs.
        /// </summary>
        public static void Chirp(int startHz, int endHz, int durationMs)
        {
            PlayChirp(startHz, endHz, durationMs, 0.6f);
        }

        /// <summary>
        /// Confirmation tone — plays confirm.wav (Andre's enter key sound).
        /// Falls back to three 800Hz 25ms pips if .wav not loaded.
        /// </summary>
        public static void ConfirmTone()
        {
            if (_confirmSound != null)
            {
                PlayCachedSound(_confirmSound);
            }
            else
            {
                // Fallback: three 800Hz 25ms pips
                PlayToneSequence(new[] { (800, 25), (0, 30), (800, 25), (0, 30), (800, 25) }, 0.5f);
            }
        }

        /// <summary>
        /// Band boundary beep — distinctive double-beep when crossing band edges.
        /// 600 Hz, 50ms, pause, 600 Hz, 50ms.
        /// </summary>
        public static void BandBoundaryBeep()
        {
            PlayToneSequence(new[] { (600, 50), (0, 30), (600, 50) }, 0.6f);
        }

        /// <summary>
        /// Filter edge enter tone — plays mode-enter.wav.
        /// </summary>
        public static void FilterEdgeEnterTone()
        {
            if (_modeEnterSound != null)
                PlayCachedSound(_modeEnterSound);
            else
                PlayTone(1000, 80, 0.4f);
        }

        /// <summary>
        /// Filter edge exit tone — plays mode-exit.wav.
        /// </summary>
        public static void FilterEdgeExitTone()
        {
            if (_modeExitSound != null)
                PlayCachedSound(_modeExitSound);
            else
                PlayTone(600, 80, 0.4f);
        }

        /// <summary>
        /// Filter edge move tone — plays filter-edge-move.wav.
        /// Short click/tick sound on each filter edge adjustment.
        /// </summary>
        public static void FilterEdgeMoveTone()
        {
            if (_filterEdgeMoveSound != null)
                PlayCachedSound(_filterEdgeMoveSound);
            else
                PlayTone(800, 20, 0.3f);
        }

        #region Internal Playback

        /// <summary>
        /// Play a single sine tone through the mixer.
        /// </summary>
        private static void PlayTone(int frequencyHz, int durationMs, float volume)
        {
            if (_mixer == null) { FallbackBeep(frequencyHz, durationMs); return; }
            try
            {
                var signal = new SignalGenerator(SampleRate, Channels)
                {
                    Type = SignalGeneratorType.Sin,
                    Frequency = frequencyHz,
                    Gain = volume
                };
                var timed = signal.Take(TimeSpan.FromMilliseconds(durationMs));
                var faded = new FadeInOutSampleProvider(timed, true);
                faded.BeginFadeIn(Math.Min(durationMs / 10.0, 10));
                faded.BeginFadeOut(Math.Max(durationMs - Math.Min(durationMs / 10.0, 10), 0));
                _mixer.AddMixerInput(faded);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayTone failed: {ex.Message}");
                FallbackBeep(frequencyHz, durationMs);
            }
        }

        /// <summary>
        /// Play a sequence of (frequency, duration) tones. Frequency 0 = silence gap.
        /// </summary>
        private static void PlayToneSequence((int freq, int ms)[] tones, float volume)
        {
            if (_mixer == null) return;
            try
            {
                var providers = new ISampleProvider[tones.Length];
                for (int i = 0; i < tones.Length; i++)
                {
                    if (tones[i].freq == 0)
                    {
                        // Silence gap
                        providers[i] = new SilenceProvider(
                            WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
                            .ToSampleProvider()
                            .Take(TimeSpan.FromMilliseconds(tones[i].ms));
                    }
                    else
                    {
                        var signal = new SignalGenerator(SampleRate, Channels)
                        {
                            Type = SignalGeneratorType.Sin,
                            Frequency = tones[i].freq,
                            Gain = volume
                        };
                        providers[i] = signal.Take(TimeSpan.FromMilliseconds(tones[i].ms));
                    }
                }
                var concat = new ConcatenatingSampleProvider(providers);
                _mixer.AddMixerInput(concat);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayToneSequence failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Play a frequency sweep (chirp) through the mixer.
        /// Uses a custom sample provider for smooth linear sweep.
        /// </summary>
        private static void PlayChirp(int startHz, int endHz, int durationMs, float volume)
        {
            if (_mixer == null) { FallbackBeep((startHz + endHz) / 2, durationMs); return; }
            try
            {
                var chirp = new ChirpSampleProvider(SampleRate, startHz, endHz, durationMs, volume);
                _mixer.AddMixerInput(chirp);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayChirp failed: {ex.Message}");
                FallbackBeep((startHz + endHz) / 2, durationMs);
            }
        }

        /// <summary>
        /// Play a cached .wav sound through the mixer.
        /// </summary>
        private static void PlayCachedSound(CachedSound sound)
        {
            if (_mixer == null) return;
            try
            {
                var provider = new CachedSoundSampleProvider(sound);
                _mixer.AddMixerInput(provider);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"EarconPlayer.PlayCachedSound failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Load a .wav from embedded resources and cache as float samples.
        /// </summary>
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
        /// Pre-loaded .wav audio data stored as float samples for instant playback.
        /// </summary>
        private class CachedSound
        {
            public float[] AudioData { get; }
            public WaveFormat WaveFormat { get; }

            public CachedSound(Stream wavStream)
            {
                using var reader = new WaveFileReader(wavStream);
                var resampled = reader.ToSampleProvider();

                // Resample to match mixer format if needed
                ISampleProvider source = resampled;
                if (resampled.WaveFormat.SampleRate != SampleRate)
                {
                    source = new WdlResamplingSampleProvider(resampled, SampleRate);
                }
                if (source.WaveFormat.Channels != Channels)
                {
                    source = source.ToMono();
                }

                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

                // Read all samples
                var samples = new System.Collections.Generic.List<float>();
                var buffer = new float[SampleRate]; // 1 second buffer
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                        samples.Add(buffer[i]);
                }
                AudioData = samples.ToArray();
            }
        }

        /// <summary>
        /// Plays a CachedSound through the mixer. Each instance is a one-shot playback.
        /// </summary>
        private class CachedSoundSampleProvider : ISampleProvider
        {
            private readonly CachedSound _sound;
            private int _position;

            public CachedSoundSampleProvider(CachedSound sound)
            {
                _sound = sound;
            }

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

        /// <summary>
        /// Linear frequency sweep (chirp) sample provider.
        /// </summary>
        private class ChirpSampleProvider : ISampleProvider
        {
            private readonly int _totalSamples;
            private readonly int _startHz;
            private readonly int _endHz;
            private readonly float _volume;
            private readonly int _fadeLength;
            private int _position;
            private double _phase;

            public WaveFormat WaveFormat { get; }

            public ChirpSampleProvider(int sampleRate, int startHz, int endHz, int durationMs, float volume)
            {
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
                _totalSamples = sampleRate * durationMs / 1000;
                _startHz = startHz;
                _endHz = endHz;
                _volume = volume;
                _fadeLength = Math.Min(_totalSamples / 10, sampleRate / 100);
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int available = _totalSamples - _position;
                int toCopy = Math.Min(available, count);
                if (toCopy <= 0) return 0;

                for (int i = 0; i < toCopy; i++)
                {
                    double t = (double)_position / _totalSamples;
                    double freq = _startHz + (_endHz - _startHz) * t;
                    _phase += 2.0 * Math.PI * freq / WaveFormat.SampleRate;
                    double sample = Math.Sin(_phase);

                    double envelope = 1.0;
                    if (_position < _fadeLength)
                        envelope = (double)_position / _fadeLength;
                    else if (_position > _totalSamples - _fadeLength)
                        envelope = (double)(_totalSamples - _position) / _fadeLength;

                    buffer[offset + i] = (float)(sample * envelope * _volume);
                    _position++;
                }
                return toCopy;
            }
        }

        #endregion
    }
}
