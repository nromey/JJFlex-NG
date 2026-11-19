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
        private readonly float[] _lastWet;   // per channel: the last wet sample of the last frame, for the latch's fade
        private readonly float[] _lastDry;   // the same frame's last delayed dry sample
        private readonly int[] _fadeAt;      // per channel: samples of the latch fade already applied; -1 none running
        private readonly float[] _fadeFrom;  // per channel: the residual the fade starts from
        private readonly int[] _quiet;       // per channel: samples still to come out of the engine from before the last ClearCarry (#1169)

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
            _lastWet = new float[channels];
            _lastDry = new float[channels];
            _fadeAt = new int[channels];
            _fadeFrom = new float[channels];
            _quiet = new int[channels];
            Array.Fill(_fadeAt, -1);
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

        // ── The transmit latch (Sprint 49 Track H, item C) ──

        /// <summary>
        /// Transmit, while keyed: the first late or failed frame LATCHES the
        /// bank dry for the rest of the over, instead of falling back frame by
        /// frame. Per-frame fallback is right for receive and wrong on the air:
        /// an engine that misses intermittently would alternate processed and
        /// unprocessed 10 ms frames, which is the stutter #737 forbids. While
        /// latched the engine is not called; every frame is the dry signal at
        /// the delay the bank already held (ruling 2: no jump in the timeline,
        /// never silence), and the switch into it is faded over
        /// <see cref="LatchFadeSamples"/>. Set by the transmit slot each buffer;
        /// false (receive, or unkeyed) keeps the per-frame behaviour.
        /// </summary>
        public bool LatchFallback { get; set; }

        /// <summary>True from the first late or failed frame of a latching over until <see cref="ReleaseLatch"/>.</summary>
        public bool Latched { get; private set; }

        /// <summary>Times the bank has latched since it was built.</summary>
        public long Latches { get; private set; }

        /// <summary>Frames, summed over channels, sent dry because the bank was latched (the engine was not asked).</summary>
        public long LatchedFrames { get; private set; }

        /// <summary>
        /// How long the switch into the latched dry signal is faded, in samples
        /// per channel. Defaults to <see cref="DefaultLatchFadeSamples"/>, the
        /// shortest that measured click-free (see there).
        /// </summary>
        public int LatchFadeSamples { get; set; } = DefaultLatchFadeSamples;

        /// <summary>
        /// The latch fade: 10 samples at 48 kHz, about 0.21 ms, half a raised
        /// cosine. Ruled "the smallest fade possible" (Noel 2026-11-18 08:18),
        /// so this is the SHORTEST length that measured click-free on the test
        /// fixture, not a comfortable one, and not a universal claim: the bench
        /// checks it on real audio. <c>TxLatchFadeMeasurementTests</c> has
        /// RNNoise clean the reference voice over a fan's rumble and mains hum,
        /// latches at thirty different frames, and measures the high-band energy
        /// (second difference) in the 2 ms around each switch against the louder
        /// of the two signals unswitched, at EVERY length from 0 to 24 samples
        /// and some longer. Measured 2026-11-18, worst of thirty: a hard cut
        /// +7.31 dB; 2 samples +2.68; 6 samples +0.43; 9 samples +0.11;
        /// 10 samples +0.07; 16 samples and every longer fade +0.01, the floor.
        /// Ten is the first within 0.1 dB of the floor. (It was 12 until repair
        /// round 1: the first search tried only 0, 6, 12 and 24.)
        /// </summary>
        public const int DefaultLatchFadeSamples = 10;

        /// <summary>Clear the latch (the transmit slot calls it at the unkey). The engine is asked again from the next frame.</summary>
        public void ReleaseLatch()
        {
            Latched = false;
            Array.Fill(_fadeAt, -1);
        }

        /// <summary>
        /// Frames, summed over channels, the engine itself took: processed, or
        /// accepted and not yet answered (late). A buffer too short to complete a
        /// frame adds nothing here and nothing to <see cref="FallbackFrames"/>, so
        /// it proves nothing either way about whether the engine is working
        /// (Sprint 49 Track E follow-up 2, repair round 2: such a buffer made a
        /// dead engine read as processing).
        /// </summary>
        public long EngineFrames { get; private set; }

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
                        bool latchedBefore = Latched;
                        bool produced = !latchedBefore && RunEngine(ch, engine, inBuf, wet.AsSpan(outCount, f));
                        if (produced) AdoptDelay(ch, engine.LatencySamples);
                        DelayFrame(ch, inBuf, dry.AsSpan(outCount, f));
                        if (!produced)
                        {
                            Array.Copy(dry, outCount, wet, outCount, f);
                            if (latchedBefore) LatchedFrames++;
                            else if (LatchFallback) { Latched = true; Latches++; }
                            if (Latched && _fadeAt[ch] < 0)
                            {
                                // The switch into dry for this channel: start
                                // from what the engine was removing at the end
                                // of the last frame, and fade that to nothing.
                                _fadeAt[ch] = 0;
                                _fadeFrom[ch] = _lastWet[ch] - _lastDry[ch];
                            }
                            if (_fadeAt[ch] >= 0) Fade(ch, wet.AsSpan(outCount, f), dry.AsSpan(outCount, f));
                        }
                        if (_quiet[ch] > 0)
                        {
                            // #1169: the engine is still emitting what it was given
                            // before the stop. Its place takes the dry signal, which
                            // the stop cleared, so nothing from before plays after.
                            int n2 = Math.Min(_quiet[ch], f);
                            Array.Copy(dry, outCount, wet, outCount, n2);
                            _quiet[ch] -= n2;
                        }
                        _lastWet[ch] = wet[outCount + f - 1];
                        _lastDry[ch] = dry[outCount + f - 1];
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
        /// Drop any audio waiting in the carry and return to zero delay. Call
        /// when the stream has been interrupted (processing switched off and on
        /// again), so stale audio from before the gap is not played after it.
        /// </summary>
        /// <remarks>
        /// #1169, fixed in Sprint 49 Track P: until then this kept the dry delay
        /// lines, so a resumed bank played its whole delay of audio from before
        /// the stop (measured: 960 of 960 samples, <c>ClearCarryMarkerTests</c>),
        /// against this very comment. Now the dry lines are cleared, and for the
        /// engine's own delay after the stop its output is replaced by that
        /// cleared dry signal: the engines keep what they have LEARNED (their
        /// noise estimates), but nothing they were GIVEN before the stop is
        /// heard after it. The resumed stream starts with silence for the
        /// bank's delay, then the processed audio, on the same timeline.
        /// No allocation: called from the audio thread.
        /// </remarks>
        public void ClearCarry()
        {
            _inCount = 0;
            _outCount = 0;
            _carrying = false;
            HasProcessedSinceGap = false;
            for (int ch = 0; ch < _delay.Length; ch++)
            {
                Array.Clear(_delay[ch]);
                _delayAt[ch] = 0;
                _quiet[ch] = _delay[ch].Length;
            }
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
        /// One frame of the latch fade: wet is dry plus the residual the engine
        /// was removing when it stopped, faded out on a half raised cosine. So
        /// the output carries on from the last processed sample instead of
        /// stepping by the removed noise, and lands on the dry signal (aligned
        /// to the same delay) after <see cref="LatchFadeSamples"/>. The wet side
        /// of a true cross-fade does not exist once the engine has stopped,
        /// which is why the residual is continued rather than the engine.
        /// </summary>
        private void Fade(int ch, Span<float> wet, ReadOnlySpan<float> dry)
        {
            int length = Math.Max(0, LatchFadeSamples);
            int at = _fadeAt[ch];
            float from = _fadeFrom[ch];
            for (int i = 0; i < wet.Length && at < length; i++, at++)
                wet[i] = dry[i] + from * (float)(0.5 * (1 + Math.Cos(Math.PI * at / length)));
            _fadeAt[ch] = at;
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
                    EngineFrames++;
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
            // ClearCarry zeroes it too, since #1169.
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
