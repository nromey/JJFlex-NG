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
    /// Uses NAudio for playback — persistent WaveOutEvent with stereo MixingSampleProvider
    /// allows overlapping sounds, panning, and sample reversal without creating/disposing players.
    /// </summary>
    public static class EarconPlayer
    {
        private static WaveOutEvent? _waveOut;
        private static MixingSampleProvider? _mixer;
        private static bool _initialized;

        // Cached embedded sounds (stored as mono for panning flexibility)
        private static CachedSound? _clickSound;
        private static CachedSound? _confirmSound;
        private static CachedSound? _filterEdgeMoveSound;
        private static CachedSound? _modeEnterSound;
        private static CachedSound? _modeExitSound;
        private static CachedSound? _slideSound;      // slide03.wav — filter edge drag
        private static CachedSound? _zipSound;         // zip01.wav — filter boundary hit

        private const int SampleRate = 44100;
        private const int MixerChannels = 2; // Stereo mixer for panning support

        /// <summary>
        /// Initialize the audio engine. Call once at startup.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            try
            {
                _mixer = new MixingSampleProvider(
                    WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, MixerChannels))
                {
                    ReadFully = true
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
                _slideSound = LoadEmbeddedSound("JJFlexWpf.Sounds.slide03.wav");
                _zipSound = LoadEmbeddedSound("JJFlexWpf.Sounds.zip01.wav");

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
            if (_mixer == null) return;
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
            if (_mixer == null) return;
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

        #region Internal Playback

        /// <summary>Add a mono source to the stereo mixer (auto-converts to stereo center).</summary>
        private static void AddToMixer(ISampleProvider monoSource)
        {
            if (_mixer == null) return;
            if (monoSource.WaveFormat.Channels == 1)
                _mixer.AddMixerInput(new MonoToStereoSampleProvider(monoSource));
            else
                _mixer.AddMixerInput(monoSource);
        }

        /// <summary>Add a mono source to the stereo mixer with panning (-1 left, 0 center, +1 right).</summary>
        private static void AddToMixerPanned(ISampleProvider monoSource, float pan)
        {
            if (_mixer == null) return;
            // PanningSampleProvider takes mono → outputs stereo
            if (monoSource.WaveFormat.Channels != 1)
                monoSource = monoSource.ToMono();
            var panned = new PanningSampleProvider(monoSource) { Pan = pan };
            _mixer.AddMixerInput(panned);
        }

        /// <summary>Add a mono source with panning that sweeps from startPan to endPan over durationMs.</summary>
        private static void AddToMixerSweptPan(ISampleProvider monoSource, float startPan, float endPan, int durationMs)
        {
            if (_mixer == null) return;
            if (monoSource.WaveFormat.Channels != 1)
                monoSource = monoSource.ToMono();
            var swept = new SweepPanningSampleProvider(monoSource, startPan, endPan, durationMs);
            _mixer.AddMixerInput(swept);
        }

        private static void PlayTone(int frequencyHz, int durationMs, float volume)
        {
            if (_mixer == null) { FallbackBeep(frequencyHz, durationMs); return; }
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

        private static void PlayTonePanned(int frequencyHz, int durationMs, float volume, float pan)
        {
            if (_mixer == null) { FallbackBeep(frequencyHz, durationMs); return; }
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
            if (_mixer == null) return;
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

        private static void PlayChirp(int startHz, int endHz, int durationMs, float volume)
        {
            if (_mixer == null) { FallbackBeep((startHz + endHz) / 2, durationMs); return; }
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
            if (_mixer == null) { FallbackBeep((startHz + endHz) / 2, durationMs); return; }
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
            if (_mixer == null) return;
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
            if (_mixer == null) return;
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
            if (_mixer == null) return;
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
        /// Pre-loaded .wav audio data stored as mono float samples for instant playback.
        /// Mono storage allows flexible panning at playback time.
        /// </summary>
        private class CachedSound
        {
            public float[] AudioData { get; }
            public WaveFormat WaveFormat { get; }

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
