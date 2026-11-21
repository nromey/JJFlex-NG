using System;
using System.Diagnostics;
using JJTrace;
using POpusCodec;

namespace JJPortaudio
{
    /// <summary>
    /// What happens to one decoded receive buffer between the Opus decoder and
    /// the playback queue: gain and clamp, the PC-side processing
    /// (<see cref="PostDecodeProcessor"/>, receive noise reduction), the
    /// "what was heard" tap (<see cref="PostOutputObserver"/>), the level
    /// meters, and the enqueue. Written once (#1155).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is its own class.</b> A lost receive packet used to be
    /// spliced: the loop simply decoded the next one, and the waveform step at
    /// the join was a click. A concealed or FEC-recovered frame must reach the
    /// speaker through EXACTLY the same tail as a real packet, one buffer at a
    /// time, or receive noise reduction, the recorder's heard mode and the
    /// meters see two different streams. So the tail lives here, and a real
    /// packet, a concealed frame and a recovered frame all call it.
    /// </para>
    /// <para>
    /// <b>It needs no audio device.</b> The decoder is passed in, and the
    /// enqueue is an injected action, so the gap path is testable with a bare
    /// <see cref="OpusDecoder"/>. <see cref="JJAudioStream"/> owns one and
    /// hands it the stream's decoder and queue.
    /// </para>
    /// <para>
    /// Called on the remote-audio thread only. Not thread-safe, and does not
    /// need to be.
    /// </para>
    /// </remarks>
    /// <summary>
    /// What a post-decode processor knows about the buffer it just processed,
    /// for the lag shifter at the playback queue (#873 phase 2): the class of
    /// that buffer's audio. <c>RxAudioPipeline</c> implements it; the chain finds
    /// it as the processor delegate's target, so nothing between the UI and the
    /// stream has to forward a second object.
    /// </summary>
    public interface IRxContentClassifier
    {
        /// <summary>The class of the buffer most recently processed, as it comes out.</summary>
        RxContentClass LastBufferClass { get; }
    }

    public sealed class RxOpusChain
    {
        private readonly Action<float[]> _enqueue;
        private readonly Func<uint> _sampleRate;
        private readonly Action<float[], RxContentClass, bool> _noteMeta;
        private Action<float[]> _postDecodeProcessor;
        private IRxContentClassifier _classifier;
        private bool _seamNext;

        /// <param name="noteMeta">
        /// Called with each buffer's class and seam mark just BEFORE it is
        /// enqueued (Track P2: the playback queue's catch-up keeps them beside
        /// the buffer). Optional.
        /// </param>
        public RxOpusChain(Action<float[]> enqueue, Func<uint> sampleRate, Action<float[], RxContentClass, bool> noteMeta = null)
        {
            _enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
            _sampleRate = sampleRate ?? (() => 0);
            _noteMeta = noteMeta;
        }

        /// <summary>
        /// The next buffer emitted follows lost audio the loop did not make up (a
        /// gap over the concealment cap): it carries a seam mark, so the playback
        /// queue joins across it with a matched fade instead of a splice (#1155,
        /// Track P2). Called on the remote-audio thread, just before the packet
        /// after the gap is written.
        /// </summary>
        public void MarkSeam() => _seamNext = true;

        /// <summary>Seam marks emitted (tests).</summary>
        public long SeamsMarked { get; private set; }

        /// <summary>Output gain scalar; 1.0 is unity.</summary>
        public float OutputGain { get; set; } = 1.0f;

        /// <summary>In-place PC-side processing (receive noise reduction). When its target classifies what it processed (<see cref="IRxContentClassifier"/>), each buffer's class goes with it to the queue.</summary>
        public Action<float[]>? PostDecodeProcessor
        {
            get => _postDecodeProcessor;
            set { _postDecodeProcessor = value; _classifier = value?.Target as IRxContentClassifier; }
        }

        /// <summary>Observer of the final samples; it must return without blocking.</summary>
        public Action<float[]>? PostOutputObserver { get; set; }

        /// <summary>Buffers that went through the tail, real or not. Tests read it.</summary>
        public long BuffersEmitted { get; private set; }

        /// <summary>Decode a real packet and play it.</summary>
        public void WritePacket(OpusDecoder decoder, byte[] packet)
        {
            Emit(decoder.DecodePacketFloat(packet));
        }

        /// <summary>
        /// Fill a gap of <paramref name="missingFrames"/> lost frames that
        /// ended at <paramref name="next"/>, which has NOT been played yet.
        /// Each lost frame is emitted as its own buffer of exactly the next
        /// packet's frame length: the first k-1 by Opus concealment, the last
        /// from the next packet's in-band redundancy when it carries any, else
        /// by concealment too. <paramref name="beforeEachFrame"/> is called with
        /// the frame's 1-based index just before that frame is emitted, so the
        /// caller can stamp the recorder's heard tap with a consecutive
        /// sequence. The caller then plays <paramref name="next"/> itself with
        /// <see cref="WritePacket"/>.
        /// </summary>
        /// <returns>
        /// The frames emitted, and how many of them came from FEC. Zero frames
        /// when the next packet's length cannot be read (nothing is invented
        /// then: the gap is spliced as before).
        /// </returns>
        public (int Concealed, int Recovered) FillGap(OpusDecoder decoder, byte[] next, int missingFrames,
            Action<int>? beforeEachFrame = null)
        {
            if (decoder == null || next == null || next.Length == 0 || missingFrames <= 0) return (0, 0);
            int samplesPerChannel;
            try { samplesPerChannel = decoder.SamplesPerChannel(next); }
            catch (Exception ex) { TraceOnce("could not read the next packet's frame length: " + ex.Message); return (0, 0); }
            if (samplesPerChannel <= 0) return (0, 0);

            int concealed = 0, recovered = 0;
            bool fec = OpusDecoder.HasLbrr(next);
            try
            {
                for (int i = 1; i <= missingFrames; i++)
                {
                    float[] frame = (i == missingFrames && fec)
                        ? decoder.RecoverFloatFromNext(next, samplesPerChannel)
                        : decoder.ConcealFloat(samplesPerChannel);
                    if (frame.Length == 0) break;
                    beforeEachFrame?.Invoke(i);
                    Emit(frame);
                    if (i == missingFrames && fec) recovered++;
                    else concealed++;
                }
            }
            catch (Exception ex)
            {
                // A decoder that refuses to conceal must not end receive audio:
                // the rest of the gap is spliced, as every gap was before.
                TraceOnce("concealment failed, splicing the rest of the gap: " + ex.Message);
            }
            return (concealed, recovered);
        }

        private bool _traced;
        private void TraceOnce(string what)
        {
            if (_traced) return;
            _traced = true;
            Tracing.TraceLine("RxOpusChain: " + what, TraceLevel.Error);
        }

        // ── The tail. Meters below moved here unchanged from JJAudioStream.WriteOpus. ──

        private int _peakLogCounter = 0;
        private float _peakSinceLastLog = 0f;
        private float _postPeakSinceLastLog = 0f;
        // Track B, 2026-08-18 (#17): RMS alongside the peaks. A peak says how
        // hot the loudest instant was; RMS says how loud the stream actually
        // IS, which is the number an operator's "it sounds quiet" can be
        // compared against. Accumulated across the same ~5 s window the peak
        // log uses, raw (pre-gain) and measured output (post-gain,
        // post-processing) separately, so the two RMS figures bracket the
        // gain stage exactly as the two peaks do.
        private double _rawSquaresSinceLastLog;
        private double _postSquaresSinceLastLog;
        private long _meterSamplesSinceLastLog;
        // Short-term LUFS of what is actually playing (post-gain,
        // post-processing) — the perceptual companion to the RMS figure,
        // read from the 3-second window so it is meaningful at the 5-second
        // log cadence. ShortTermLufs is a lock-free read; the gated
        // IntegratedLufs (which locks and copies) is deliberately not used
        // here — this runs on the remote-audio thread every 10 ms packet.
        private readonly LufsMeter _outputLufs = new LufsMeter();

        /// <summary>
        /// Gain, clamp, processing, the heard tap, the meters and the enqueue,
        /// for one decoded buffer. Every receive buffer passes through here.
        /// </summary>
        private void Emit(float[] buf)
        {
            BuffersEmitted++;
            // Track peak and RMS level before gain for diagnostics
            float prePeak = 0f;
            double preSquares = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                float s = buf[i];
                float abs = Math.Abs(s);
                if (abs > prePeak) prePeak = abs;
                preSquares += (double)s * s;
            }

            if (OutputGain != 1.0f)
            {
                // Hard limit at full scale. The gain used to be a bare multiply
                // with no bounds check — safe at the historical fixed 4x on
                // observed material (raw peaks ~0.02-0.10), but the gain is
                // operator-adjustable now (Audio Arc Track A, up to +24 dB),
                // and an unclamped boost on a hot source would wrap into harsh
                // digital garbage. Clamping turns overdrive into ordinary
                // clipping instead.
                for (int i = 0; i < buf.Length; i++)
                {
                    float s = buf[i] * OutputGain;
                    if (s > 1.0f) s = 1.0f;
                    else if (s < -1.0f) s = -1.0f;
                    buf[i] = s;
                }
            }

            if (prePeak > _peakSinceLastLog) _peakSinceLastLog = prePeak;

            // PC-side audio processing (NR, spectral subtraction, etc.)
            var processor = _postDecodeProcessor;
            processor?.Invoke(buf);
            var cls = processor != null && _classifier != null ? _classifier.LastBufferClass : RxContentClass.Unclassified;
            bool seam = _seamNext;
            _seamNext = false;
            if (seam) SeamsMarked++;
            PostOutputObserver?.Invoke(buf);

            // Measure what is actually going to the speakers, AFTER the gain,
            // the limiter and any noise reduction — rather than computing it
            // from the raw peak, which is what this used to do.
            //
            // Track E, 2026-08-16. This line is the instrument for the standing
            // "decoded PC audio arrives too quiet" report, so it has to be
            // trustworthy before anyone re-measures with it. Computing
            // raw x gain silently omitted PostDecodeProcessor, which is where
            // JJ Neural NR and Spectral NR run and which can move the level by
            // a great deal — so on a machine with either of those turned on,
            // the "output peak" in the trace was a number that never existed.
            // Chasing a quiet-audio report with an instrument that reports a
            // level nothing produced is worse than having no instrument.
            float postPeak = 0f;
            double postSquares = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                float s = buf[i];
                float abs = Math.Abs(s);
                if (abs > postPeak) postPeak = abs;
                postSquares += (double)s * s;
            }
            if (postPeak > _postPeakSinceLastLog) _postPeakSinceLastLog = postPeak;
            _rawSquaresSinceLastLog += preSquares;
            _postSquaresSinceLastLog += postSquares;
            _meterSamplesSinceLastLog += buf.Length;

            // Perceptual loudness of the same post-everything audio. The
            // meter wants the stream's real rate; before the stream reports
            // one (0), skip rather than feed a lie.
            uint meterRate = _sampleRate();
            if (meterRate > 0) _outputLufs.Process(buf, buf.Length, meterRate);

            // Every ~5 seconds, at ~100 packets/sec.
            if (++_peakLogCounter >= 500)
            {
                // The limiter caps at full scale, so a raw peak that would have
                // exceeded it is the clipping test — the measured peak cannot
                // report clipping on its own, because the clamp is exactly what
                // stops it going above 1.0.
                bool clipped = (_peakSinceLastLog * OutputGain) > 1.0f;
                long n = Math.Max(_meterSamplesSinceLastLog, 1);
                double rawRmsDb = 10 * Math.Log10(_rawSquaresSinceLastLog / n + 1e-20);
                double postRmsDb = 10 * Math.Log10(_postSquaresSinceLastLog / n + 1e-20);
                float lufs = _outputLufs.ShortTermLufs;
                Tracing.TraceLine($"OpusAudio: raw peak={_peakSinceLastLog:F4} "
                    + $"({20 * Math.Log10(_peakSinceLastLog + 1e-10):F1} dBFS), raw RMS={rawRmsDb:F1} dBFS, "
                    + $"gain={OutputGain:F1}x, "
                    + $"measured output peak={_postPeakSinceLastLog:F4} "
                    + $"({20 * Math.Log10(_postPeakSinceLastLog + 1e-10):F1} dBFS), "
                    + $"output RMS={postRmsDb:F1} dBFS, output loudness={lufs:F1} LUFS"
                    + $"{(PostDecodeProcessor != null ? ", post-decode processing ON" : "")}"
                    + $"{(clipped ? " CLIPPED" : "")}", TraceLevel.Info);
                _peakLogCounter = 0;
                _peakSinceLastLog = 0f;
                _postPeakSinceLastLog = 0f;
                _rawSquaresSinceLastLog = 0;
                _postSquaresSinceLastLog = 0;
                _meterSamplesSinceLastLog = 0;
            }

            _noteMeta?.Invoke(buf, cls, seam);
            _enqueue(buf);
        }
    }
}
