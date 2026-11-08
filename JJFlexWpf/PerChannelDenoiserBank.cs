using System;
using System.Diagnostics;

namespace JJFlexWpf
{
    /// <summary>
    /// Runs one mono <see cref="IDenoiseEngine"/> per channel over an
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
    /// <b>Frame carry adds no delay while input is frame-aligned, the live
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
    /// <b>Strength</b> blends wet and dry with the same arithmetic as before.
    /// Dry is delayed by each channel's engine latency, to the sample, so
    /// mixing it with wet does not create a comb filter.
    /// </para>
    /// <para>
    /// A failed or late frame uses that same delayed dry frame. The last
    /// adopted delay is held until the engine produces again: ruling 2
    /// (2026-10-18), no timeline jump during failure or recovery.
    /// </para>
    /// <para>
    /// Not thread-safe: <see cref="Process"/> belongs to one audio thread.
    /// </para>
    /// </remarks>
    public sealed class PerChannelDenoiserBank : IDisposable
    {
        private readonly IDenoiseEngine[] _engines;
        private readonly float[][] _in;   // pending partial frame, per channel
        private float[][] _wet;            // processed, waiting to go out
        private float[][] _dry;            // the same samples unprocessed, for the blend
        private int _inCount;              // identical across channels by construction
        private int _outCount;
        private bool _carrying;
        private readonly float[][] _delay;
        private readonly int[] _delayAt;
        internal bool HasProcessedSinceGap { get; private set; }
        private readonly bool[] _failing; // per channel: currently in a fallback stretch

        /// <param name="engineFactory">Called once per channel. Every instance must report the same frame length.</param>
        /// <param name="channels">Interleaved channel count, at least 1.</param>
        public PerChannelDenoiserBank(Func<IDenoiseEngine> engineFactory, int channels)
        {
            if (engineFactory == null) throw new ArgumentNullException(nameof(engineFactory));
            if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));

            _engines = new IDenoiseEngine[channels];
            try
            {
                for (int c = 0; c < channels; c++) _engines[c] = engineFactory();
            }
            catch
            {
                foreach (var engine in _engines) engine?.Dispose();
                throw;
            }

            FrameLength = _engines[0].FrameLength;
            for (int c = 1; c < channels; c++)
                if (_engines[c].FrameLength != FrameLength)
                    throw new InvalidOperationException("Every engine in a bank must use the same frame length.");
            if (FrameLength < 1)
                throw new InvalidOperationException("An engine reported a frame length below one sample.");

            _failing = new bool[channels];
            _delay = new float[channels][];
            _delayAt = new int[channels];
            _in = new float[channels][];
            _wet = new float[channels][];
            _dry = new float[channels][];
            for (int c = 0; c < channels; c++)
            {
                _delay[c] = new float[Math.Max(0, _engines[c].LatencySamples)];
                _in[c] = new float[FrameLength];
                _wet[c] = new float[FrameLength * 2];
                _dry[c] = new float[FrameLength * 2];
            }
        }

        /// <summary>
        /// When true, <see cref="Process"/> writes what the engine REMOVED —
        /// the dry signal, already delayed by the engine's own latency to the
        /// sample, minus the wet one — instead of the blend (Sprint 49 Track
        /// E, E13). One mechanism for every engine, computed where the
        /// aligned dry history already exists for the Strength blend, rather
        /// than one engine's native residual and something else for the rest.
        /// A frame that fell back to the dry signal has removed nothing, so
        /// it is silence here. Set by the slot from the audio thread; no
        /// allocation, no lock.
        /// </summary>
        public bool ListenToRemoved { get; set; }

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

        /// <summary>Last adopted engine delay (the largest across channels), in samples.</summary>
        public int EngineLatencySamples
        {
            get
            {
                int max = 0;
                for (int ch = 0; ch < _delay.Length; ch++) max = Math.Max(max, _delay[ch].Length);
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

        public long LateFrames { get; private set; }

        /// <summary>The engine serving one channel, for the offline bench's per-engine figures.</summary>
        public IDenoiseEngine EngineFor(int channel) => _engines[channel];

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
                        bool produced = RunEngine(ch, engine, inBuf, wet.AsSpan(outCount, f));
                        if (produced) AdoptDelay(ch, engine.LatencySamples);
                        DelayFrame(ch, inBuf, dry.AsSpan(outCount, f));
                        if (!produced) Array.Copy(dry, outCount, wet, outCount, f);
                        outCount += f;
                        inCount = 0;
                    }
                }

                // Proved in the class remarks: there are always n ready here.
                if (ListenToRemoved)
                    for (int i = 0; i < n; i++)
                        interleaved[i * channels + ch] = dry[i] - wet[i];
                else
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
            HasProcessedSinceGap = true;
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
            HasProcessedSinceGap = false;
        }

        /// <summary><see cref="ClearCarry"/>, and reset every engine as if new.</summary>
        public void Reset()
        {
            ClearCarry();
            for (int ch = 0; ch < _engines.Length; ch++)
            {
                _engines[ch].Reset();
                AdoptDelay(ch, _engines[ch].LatencySamples);
                Array.Clear(_delay[ch]);
                _delayAt[ch] = 0;
            }
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
        private bool RunEngine(int ch, IDenoiseEngine engine, float[] input, Span<float> output)
        {
            string? failure = null;
            if (engine.IsAvailable)
            {
                try
                {
                    bool processed = engine.ProcessFrame(input, output);
                    _failing[ch] = false;
                    if (!processed) LateFrames++;
                    return processed;
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

            FallbackFrames++;
            if (!_failing[ch])
            {
                _failing[ch] = true;
                Trace.WriteLine($"PerChannelDenoiserBank: {engine.Id} on channel {ch} failed ({failure}); " +
                                "passing that channel through unprocessed until it recovers.");
            }
            return false;
        }

        private void AdoptDelay(int channel, int samples)
        {
            samples = Math.Max(0, samples);
            if (_delay[channel].Length == samples) return;
            int before = _delay[channel].Length;
            // A deliberate latency change starts zeroed, like a new engine.
            // ClearCarry alone keeps this history, just as it keeps the engine's.
            _delay[channel] = new float[samples];
            _delayAt[channel] = 0;
            Trace.WriteLine($"PerChannelDenoiserBank: channel {channel} delay changed from {before} to {samples} samples.");
        }

        private void DelayFrame(int channel, float[] input, Span<float> output)
        {
            float[] line = _delay[channel];
            if (line.Length == 0) { input.AsSpan().CopyTo(output); return; }
            int at = _delayAt[channel];
            for (int i = 0; i < input.Length; i++)
            {
                output[i] = line[at];
                line[at] = input[i];
                if (++at == line.Length) at = 0;
            }
            _delayAt[channel] = at;
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
