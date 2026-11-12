using System;
using System.Diagnostics;
using JJTrace;
using POpusCodec.Enums;

namespace JJPortaudio
{
    /// <summary>
    /// A transmit path for a generated source that needs no microphone: its
    /// own Opus encoder, its own <see cref="TxFramePipeline"/>, and its own
    /// self-clock, for a session whose microphone could not be opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> The self-clocked source normally rides the
    /// capture stream's pipeline, and that pipeline's encoder is built when the
    /// microphone opens. So a microphone that would not open took the test tone
    /// with it — the one diagnostic an operator reaches for when the
    /// microphone is the suspect. A tone is computed from a formula; it needs
    /// no device, so there is nothing to negotiate a rate with, and the
    /// requested rate is the encoder's rate.
    /// </para>
    /// <para>
    /// <b>One encoder feeds the radio, always.</b> The caller builds this only
    /// in a session whose microphone failed to open, and the microphone is
    /// opened once per session, so the capture path and this one never both
    /// exist with a live encoder. The failed stream's own encoder, if it
    /// survived the failure, is never used.
    /// </para>
    /// <para>
    /// <b>The pipeline's parts are the caller's.</b> Source, conditioner and
    /// meter are handed in, so the rig's one source mux, one conditioning chain
    /// and one loudness meter serve both paths; this class constructs none of
    /// them.
    /// </para>
    /// <para>
    /// <b>The teardown order is the class's job, not the caller's.</b>
    /// <see cref="Dispose"/> stops the clock (joining its thread), then clears
    /// the pipeline's encode step, then disposes the encoder. A frame that
    /// arrives late finds nothing to encode rather than a disposed encoder, the
    /// same guard the capture stream's close has.
    /// </para>
    /// </remarks>
    public sealed class TxStandaloneSource : IDisposable
    {
        private readonly object _lifecycle = new object();
        private readonly TxSelfClockedSource _clock;
        private IDisposable _encoder;
        private bool _disposed;

        /// <summary>The pipeline this source drives. Its own; never the capture stream's.</summary>
        public TxFramePipeline Pipeline { get; }

        /// <summary>The rate the encoder was built for.</summary>
        public uint SampleRate { get; }

        /// <summary>Samples per channel in one Opus frame.</summary>
        public int SamplesPerFrame { get; }

        /// <summary>
        /// Build from an encode step. Production code uses <see cref="Create"/>;
        /// this constructor exists so the lifecycle and the teardown order can
        /// be tested with a fake encode, the way <see cref="TxFramePipeline"/>
        /// is, because a test host cannot construct a real encoder without
        /// libopus loaded.
        /// </summary>
        /// <param name="encode">One frame of interleaved stereo floats to bytes.</param>
        /// <param name="encoderOwner">What to dispose after the encode step is cleared. May be null.</param>
        /// <param name="sampleRate">The rate the encoder was built for.</param>
        /// <param name="samplesPerFrame">Samples per channel in one frame.</param>
        /// <param name="source">What stands in for the microphone.</param>
        /// <param name="conditioner">The transmit conditioning chain.</param>
        /// <param name="meter">The loudness meter.</param>
        /// <param name="handler">Where encoded frames go.</param>
        public TxStandaloneSource(Func<float[], byte[]> encode, IDisposable encoderOwner,
            uint sampleRate, int samplesPerFrame,
            ITxInputSource source, TxAudioProcessorCallback conditioner, LufsMeter meter,
            Audio.OpusCallback handler)
        {
            if (encode == null) throw new ArgumentNullException(nameof(encode));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            Pipeline = new TxFramePipeline
            {
                Source = source,
                Conditioner = conditioner,
                Meter = meter,
                Encode = encode,
                Handler = handler,
            };
            SampleRate = sampleRate;
            SamplesPerFrame = samplesPerFrame;
            // Throws on a nonsense geometry, before anything is owned.
            _clock = new TxSelfClockedSource(Pipeline, sampleRate, samplesPerFrame);
            _encoder = encoderOwner;
        }

        /// <summary>
        /// Build with a real Opus encoder from a rate and a profile.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The rate is not one Opus can encode. Refused rather than rounded.
        /// </exception>
        public static TxStandaloneSource Create(uint sampleRate, OpusEncodeProfile profile,
            ITxInputSource source, TxAudioProcessorCallback conditioner, LufsMeter meter,
            Audio.OpusCallback handler)
        {
            if (!OpusEncodeProfile.TryGetSamplingRate(sampleRate, out SamplingRate rate))
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate,
                    "not a rate Opus can encode");

            var opusProfile = profile ?? OpusEncodeProfile.Shipped;
            var encoder = opusProfile.CreateEncoder(rate);
            try
            {
                var built = new TxStandaloneSource(OpusEncodeProfile.BuildEncodeStep(encoder), encoder,
                    sampleRate, encoder.FrameSizePerChannel, source, conditioner, meter, handler);
                Tracing.TraceLine("TxStandaloneSource: built at " + sampleRate + " Hz, "
                    + encoder.FrameSizePerChannel + " samples per frame, " + opusProfile.Describe(),
                    TraceLevel.Info);
                return built;
            }
            catch
            {
                encoder.Dispose();
                throw;
            }
        }

        /// <summary>True while frames are being produced.</summary>
        public bool Running => _clock.Running;

        /// <summary>True once <see cref="Dispose"/> has run. A disposed source never starts again.</summary>
        public bool Disposed
        {
            get { lock (_lifecycle) return _disposed; }
        }

        /// <summary>
        /// Begin producing frames. Idempotent: true when already running.
        /// </summary>
        /// <returns>False after <see cref="Dispose"/>, or when the clock would not start.</returns>
        public bool Start()
        {
            lock (_lifecycle)
            {
                if (_disposed) return false;
                if (_clock.Running) return true;
                return _clock.Start();
            }
        }

        /// <summary>Stop producing frames. Hard and immediate. Idempotent.</summary>
        public void Stop()
        {
            _clock.Stop();
        }

        /// <summary>
        /// Stop the clock, clear the encode step, dispose the encoder — in that
        /// order. Idempotent.
        /// </summary>
        public void Dispose()
        {
            IDisposable encoder;
            lock (_lifecycle)
            {
                if (_disposed) return;
                _disposed = true;
                encoder = _encoder;
                _encoder = null;
            }

            // 1. Nothing pumps: Stop joins the thread, bounded.
            _clock.Stop();
            // 2. A frame that arrives late anyway finds nothing to encode.
            Pipeline.Encode = null;
            // 3. Only now does the encoder go.
            try { encoder?.Dispose(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxStandaloneSource: disposing the encoder failed, " + ex.Message,
                    TraceLevel.Error);
            }
            Tracing.TraceLine("TxStandaloneSource: disposed", TraceLevel.Info);
        }
    }
}
