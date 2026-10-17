using System;
using System.Diagnostics;

namespace JJFlexWpf
{
    /// <summary>
    /// Runs one mono <see cref="INeuralDenoiseEngine"/> per channel over an
    /// interleaved buffer: de-interleave, denoise each channel on its own,
    /// blend wet and dry, re-interleave.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why per channel (#732).</b> The radio applies each slice's pan before
    /// the audio leaves it, so the two channels carry different things: two
    /// slices split left and right, or one slice panned off centre. The old
    /// provider cleaned channel 0 and wrote it into both, which replaced a
    /// right-panned slice with the left channel. One engine per channel, never
    /// shared, is the whole fix; engines keep state between frames, so an
    /// instance fed two channels in turn would also learn neither.
    /// </para>
    /// <para>
    /// <b>Partial frames are carried, never passed through.</b> Engines take
    /// fixed frames (480 samples for both RNNoise and DeepFilterNet3). The old
    /// provider left the tail of any buffer that was not a whole number of
    /// frames untouched, so on such a stream some audio went out unprocessed
    /// with a seam at every buffer boundary.
    /// </para>
    /// <para>
    /// <b>Latency: none while the input is frame-aligned, and that is the live
    /// path.</b> The receive decoder hands over one Opus packet per call, and
    /// the radio sends 10 ms packets, so every buffer is exactly 480 frames
    /// (see the comment on <c>OpenOpus</c> in <c>FlexBase</c>). While every
    /// buffer is a whole number of frames, each frame is processed and emitted
    /// in the same call, with no added delay at all, exactly as before.
    /// </para>
    /// <para>
    /// The first time a buffer leaves a partial frame behind, the bank moves to
    /// a fixed delay of <c>FrameLength - 1</c> samples (479, just under 10 ms at
    /// 48 kHz) and stays there until <see cref="ClearCarry"/>. That is the
    /// smallest delay that can serve ANY buffer size, proved in
    /// <c>PerChannelDenoiserBankTests</c>. The switch costs one gap of that
    /// many silent samples, once, at the moment it happens; when the very
    /// first buffer is misaligned the gap falls before any audio and is just
    /// start-up delay. Changing delay again later would cost another gap, so it
    /// never shrinks on its own.
    /// </para>
    /// <para>
    /// <b>Strength</b> is the same wet/dry mix the provider always applied,
    /// computed with the same expression; the dry signal is delayed with the
    /// wet so the two stay sample-aligned.
    /// </para>
    /// <para>
    /// <b>A failing engine costs that channel its processing, never its audio.</b>
    /// Availability is read per frame, because an engine can stop working
    /// mid-stream (one running in another process can die). A frame whose
    /// engine is unavailable, or throws, goes out exactly as it came in, for
    /// that channel only, and the bank asks again on the next frame. Ruled by
    /// Noel 2026-10-17 for the out-of-process engine (#737): never silence,
    /// never stall the audio thread, never pass the exception on. Note that a
    /// passed-through frame does not carry the engine's own delay, so the
    /// moment of failure, and of recovery, is a jump of
    /// <see cref="EngineLatencySamples"/> in the timeline.
    /// </para>
    /// <para>
    /// Not thread-safe: <see cref="Process"/> belongs to one audio thread.
    /// </para>
    /// </remarks>
    public sealed class PerChannelDenoiserBank : IDisposable
    {
        private readonly INeuralDenoiseEngine[] _engines;
        private readonly float[][] _in;   // pending partial frame, per channel
        private float[][] _wet;            // processed, waiting to go out
        private float[][] _dry;            // the same samples unprocessed, for the blend
        private int _inCount;              // identical across channels by construction
        private int _outCount;
        private bool _carrying;
        private readonly bool[] _failing; // per channel: currently in a fallback stretch

        /// <param name="engineFactory">Called once per channel. Every instance must report the same frame length.</param>
        /// <param name="channels">Interleaved channel count, at least 1.</param>
        public PerChannelDenoiserBank(Func<INeuralDenoiseEngine> engineFactory, int channels)
        {
            if (engineFactory == null) throw new ArgumentNullException(nameof(engineFactory));
            if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));

            _engines = new INeuralDenoiseEngine[channels];
            for (int c = 0; c < channels; c++)
                _engines[c] = engineFactory();

            FrameLength = _engines[0].FrameLength;
            for (int c = 1; c < channels; c++)
                if (_engines[c].FrameLength != FrameLength)
                    throw new InvalidOperationException("Every engine in a bank must use the same frame length.");
            if (FrameLength < 1)
                throw new InvalidOperationException("An engine reported a frame length below one sample.");

            _failing = new bool[channels];
            _in = new float[channels][];
            _wet = new float[channels][];
            _dry = new float[channels][];
            for (int c = 0; c < channels; c++)
            {
                _in[c] = new float[FrameLength];
                _wet[c] = new float[FrameLength * 2];
                _dry[c] = new float[FrameLength * 2];
            }
        }

        /// <summary>Interleaved channel count this bank was built for.</summary>
        public int Channels => _engines.Length;

        /// <summary>The engines' frame length, in samples per channel.</summary>
        public int FrameLength { get; }

        /// <summary>True only when every channel's engine is available right now.</summary>
        public bool IsAvailable
        {
            get
            {
                foreach (var e in _engines)
                    if (!e.IsAvailable) return false;
                return true;
            }
        }

        /// <summary>True when at least one channel's engine is available right now.</summary>
        public bool AnyAvailable
        {
            get
            {
                foreach (var e in _engines)
                    if (e.IsAvailable) return true;
                return false;
            }
        }

        /// <summary>
        /// Delay the frame carry currently adds, in samples per channel: 0
        /// while the input has been frame-aligned, <c>FrameLength - 1</c> once
        /// it has not.
        /// </summary>
        public int CarryLatencySamples => _carrying ? FrameLength - 1 : 0;

        /// <summary>The engines' own delay (the largest across channels), in samples.</summary>
        public int EngineLatencySamples
        {
            get
            {
                int max = 0;
                foreach (var e in _engines) max = Math.Max(max, e.LatencySamples);
                return max;
            }
        }

        /// <summary>Everything the processed signal lags its input by: carry plus engine.</summary>
        public int LatencySamples => CarryLatencySamples + EngineLatencySamples;

        /// <summary>
        /// Frames, summed over channels, that went out unprocessed because the
        /// engine was unavailable or threw. Zero in health.
        /// </summary>
        public long FallbackFrames { get; private set; }

        /// <summary>The engine serving one channel, for the offline bench's per-engine figures.</summary>
        public INeuralDenoiseEngine EngineFor(int channel) => _engines[channel];

        /// <summary>
        /// Denoise an interleaved buffer in place. Whole sample frames only:
        /// any trailing samples that do not make a complete frame across all
        /// channels (a malformed buffer) are left as they are.
        /// </summary>
        /// <param name="interleaved">The buffer, rewritten in place.</param>
        /// <param name="strength">Wet/dry mix: 0 = original, 1 = fully processed.</param>
        public void Process(Span<float> interleaved, float strength)
        {
            int channels = _engines.Length;
            int n = interleaved.Length / channels;
            if (n == 0) return;
            int f = FrameLength;

            // In aligned mode the queues are empty between calls, so the only
            // question is whether this buffer is a whole number of frames.
            bool switchNow = !_carrying && (n % f) != 0;
            int lead = switchNow ? f - 1 : 0;

            int framesOut = (_inCount + n) / f;
            EnsureOutCapacity(_outCount + lead + framesOut * f);

            float dryMix = 1f - strength;
            int inEnd = 0, outEnd = 0;

            for (int ch = 0; ch < channels; ch++)
            {
                var engine = _engines[ch];
                float[] inBuf = _in[ch];
                float[] wet = _wet[ch];
                float[] dry = _dry[ch];
                int inCount = _inCount;
                int outCount = _outCount;

                if (lead > 0)
                {
                    // outCount is 0 here (aligned mode keeps nothing queued),
                    // so the silent lead sits ahead of everything.
                    Array.Clear(wet, outCount, lead);
                    Array.Clear(dry, outCount, lead);
                    outCount += lead;
                }

                int idx = 0;
                while (idx < n)
                {
                    int take = Math.Min(f - inCount, n - idx);
                    for (int k = 0; k < take; k++)
                        inBuf[inCount + k] = interleaved[(idx + k) * channels + ch];
                    inCount += take;
                    idx += take;

                    if (inCount == f)
                    {
                        if (!RunEngine(ch, engine, inBuf, wet.AsSpan(outCount, f)))
                        {
                            // Unavailable or threw: this frame goes out as it
                            // came in, for this channel only. Never silence,
                            // never a stall, never the exception.
                            Array.Copy(inBuf, 0, wet, outCount, f);
                            FallbackFrames++;
                        }
                        Array.Copy(inBuf, 0, dry, outCount, f);
                        outCount += f;
                        inCount = 0;
                    }
                }

                // Proved in the class remarks: there are always n ready here.
                for (int i = 0; i < n; i++)
                    interleaved[i * channels + ch] = dry[i] * dryMix + wet[i] * strength;

                int remaining = outCount - n;
                if (remaining > 0)
                {
                    Array.Copy(wet, n, wet, 0, remaining);
                    Array.Copy(dry, n, dry, 0, remaining);
                }

                inEnd = inCount;
                outEnd = remaining;
            }

            _inCount = inEnd;
            _outCount = outEnd;
            if (switchNow) _carrying = true;
        }

        /// <summary>
        /// Drop any audio waiting in the carry and return to zero delay. The
        /// engines keep what they have learned. Call when the stream has been
        /// interrupted (processing switched off and on again), so stale audio
        /// from before the gap is not played after it.
        /// </summary>
        public void ClearCarry()
        {
            _inCount = 0;
            _outCount = 0;
            _carrying = false;
        }

        /// <summary><see cref="ClearCarry"/>, and reset every engine as if new.</summary>
        public void Reset()
        {
            ClearCarry();
            foreach (var e in _engines) e.Reset();
        }

        public void Dispose()
        {
            foreach (var e in _engines) e.Dispose();
        }

        /// <summary>
        /// True when the engine produced this frame. Checked per frame,
        /// because availability can change mid-stream (an engine in another
        /// process can die). Each channel's failure is traced once per
        /// stretch, not once per frame, so a dead engine cannot flood the
        /// trace from the audio thread.
        /// </summary>
        private bool RunEngine(int ch, INeuralDenoiseEngine engine, float[] input, Span<float> output)
        {
            string? failure = null;
            if (engine.IsAvailable)
            {
                try
                {
                    engine.ProcessFrame(input, output);
                    _failing[ch] = false;
                    return true;
                }
                catch (Exception ex)
                {
                    failure = ex.GetType().Name + ": " + ex.Message;
                }
            }
            else
            {
                failure = "not available";
            }

            if (!_failing[ch])
            {
                _failing[ch] = true;
                Trace.WriteLine($"PerChannelDenoiserBank: {engine.Id} on channel {ch} failed ({failure}); " +
                                "passing that channel through unprocessed until it recovers.");
            }
            return false;
        }

        private void EnsureOutCapacity(int needed)
        {
            if (_wet[0].Length >= needed) return;
            int size = Math.Max(needed, _wet[0].Length * 2);
            for (int c = 0; c < _engines.Length; c++)
            {
                Array.Resize(ref _wet[c], size);
                Array.Resize(ref _dry[c], size);
            }
        }
    }
}
