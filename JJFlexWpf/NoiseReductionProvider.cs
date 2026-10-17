using System;
using System.Diagnostics;
using NAudio.Wave;

namespace JJFlexWpf
{
    /// <summary>
    /// ISampleProvider wrapper that applies neural noise reduction, one engine
    /// per channel. RNNoise is the engine the app uses; 480-sample frames
    /// (10 ms at 48 kHz). Includes wet/dry mix control via Strength property.
    /// Sprint 25 Phase 10.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sprint 48 Track G (#731, #732):</b> this is now a thin owner of a
    /// <see cref="PerChannelDenoiserBank"/>. It used to denoise channel 0 and
    /// write the result into every channel, which replaced a right-panned slice
    /// with the left channel's audio, and it passed the tail of any buffer that
    /// was not a whole number of frames through unprocessed. Both are gone; the
    /// public surface is unchanged.
    /// </para>
    /// <para>
    /// <b>RNNoise remains the only engine the app uses.</b> The engine factory
    /// constructor exists so code and the offline bench (<c>tools/nrbench</c>)
    /// can run DeepFilterNet3 through exactly this class; nothing an operator
    /// can reach selects it.
    /// </para>
    /// <para>
    /// The transmit chain uses this class too (<c>TxAudioConditioning</c>). Its
    /// stream carries the mono microphone duplicated on both channels, and a
    /// deterministic engine fed identical channels produces identical output,
    /// so transmit audio is unchanged; it now costs two engine instances
    /// instead of one.
    /// </para>
    /// </remarks>
    public class NoiseReductionProvider : ISampleProvider, IDisposable
    {
        private readonly ISampleProvider? _source;
        private readonly WaveFormat _waveFormat;
        private readonly Func<INeuralDenoiseEngine> _engineFactory;
        private PerChannelDenoiserBank? _bank;

        /// <summary>
        /// Set whenever a buffer goes by unprocessed (disabled, or a non-voice
        /// mode), so the next processed buffer drops any audio the carry held
        /// from before the gap rather than playing it late.
        /// </summary>
        private bool _carryStale;

        private bool _disposed;

        /// <summary>Whether noise reduction is active.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Wet/dry mix: 0.0 = original signal only, 1.0 = fully processed.
        /// Default 0.8 to preserve some signal character.
        /// </summary>
        public float Strength { get; set; } = 0.8f;

        /// <summary>
        /// When true, auto-disables NR in CW/digital modes (RNNoise is speech-trained).
        /// </summary>
        public bool AutoDisableNonVoice { get; set; } = true;

        /// <summary>Current mode from radio — set externally to control auto-disable.</summary>
        public string CurrentMode { get; set; } = "";

        public WaveFormat WaveFormat => _waveFormat;

        /// <summary>
        /// ISampleProvider constructor — wraps an existing audio source for NAudio chain use.
        /// </summary>
        public NoiseReductionProvider(ISampleProvider source)
        {
            _source = source;
            _waveFormat = source.WaveFormat;
            _engineFactory = DefaultEngine;
            _bank = CreateBank(_waveFormat.Channels);
        }

        /// <summary>
        /// Standalone constructor for live audio pipeline use (no ISampleProvider source).
        /// Call ProcessInPlace() to process buffers directly.
        /// </summary>
        public NoiseReductionProvider(int sampleRate = 48000, int channels = 2)
            : this(sampleRate, channels, DefaultEngine)
        {
        }

        /// <summary>
        /// Standalone constructor with a chosen engine, for code and the
        /// offline bench. Not reachable from anything an operator can select.
        /// </summary>
        /// <param name="engineFactory">Called once per channel; each call must return a new instance.</param>
        public NoiseReductionProvider(int sampleRate, int channels, Func<INeuralDenoiseEngine> engineFactory)
        {
            _source = null;
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
            _bank = CreateBank(channels);
        }

        /// <summary>Id of the engine this provider runs ("rnnoise" in the app).</summary>
        public string EngineId => _bank?.EngineFor(0).Id ?? "";

        /// <summary>Delay currently added, in samples per channel (0 on frame-aligned input).</summary>
        public int LatencySamples => _bank?.LatencySamples ?? 0;

        /// <summary>The bank, for tests and the bench.</summary>
        internal PerChannelDenoiserBank? Bank => _bank;

        // NAudio 3.0: ISampleProvider.Read takes a Span<float>.
        public int Read(Span<float> buffer)
        {
            int totalRead = _source?.Read(buffer) ?? 0;

            // Pass through if disabled, no engine, or auto-disabled for non-voice modes
            if (!Enabled || totalRead == 0 || (AutoDisableNonVoice && IsNonVoiceMode(CurrentMode)))
            {
                _carryStale = true;
                return totalRead;
            }

            ProcessSamples(buffer.Slice(0, totalRead), WaveFormat.Channels);
            return totalRead;
        }

        /// <summary>
        /// Process audio buffer in-place without ISampleProvider chain.
        /// Used by the live RX audio pipeline where decoded Opus frames
        /// are pushed through processing before PortAudio enqueue.
        /// </summary>
        public void ProcessInPlace(float[] buffer, int offset, int count, int channels)
        {
            if (!Enabled || count == 0 || (AutoDisableNonVoice && IsNonVoiceMode(CurrentMode)))
            {
                _carryStale = true;
                return;
            }
            ProcessSamples(buffer.AsSpan(offset, count), channels);
        }

        private void ProcessSamples(Span<float> samples, int channels)
        {
            if (channels < 1 || _disposed) return;

            var bank = _bank;
            if (bank == null || bank.Channels != channels)
            {
                // The stream shape changed under us. A fresh bank per shape,
                // never one engine stretched over a different channel count.
                bank?.Dispose();
                bank = _bank = CreateBank(channels);
                _carryStale = false;
            }
            if (bank == null || !bank.IsAvailable) return;

            if (_carryStale)
            {
                bank.ClearCarry();
                _carryStale = false;
            }

            bank.Process(samples, Strength);
        }

        private PerChannelDenoiserBank? CreateBank(int channels)
        {
            if (channels < 1) return null;
            try
            {
                var bank = new PerChannelDenoiserBank(_engineFactory, channels);
                if (!bank.IsAvailable)
                    Trace.WriteLine($"NoiseReductionProvider: {bank.EngineFor(0).Id} is not available; audio will pass through unprocessed.");
                return bank;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"NoiseReductionProvider: engine init failed: {ex.Message}");
                return null;
            }
        }

        private static INeuralDenoiseEngine DefaultEngine() => new RnnoiseEngine();

        private static bool IsNonVoiceMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return false;
            return mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase) ||
                   mode.StartsWith("DIG", StringComparison.OrdinalIgnoreCase) ||
                   mode.StartsWith("FDM", StringComparison.OrdinalIgnoreCase) ||
                   mode.Equals("RTTY", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            _disposed = true;
            _bank?.Dispose();
            _bank = null;
        }
    }
}
