using System;
using JJPortaudio;

namespace JJFlexWpf
{
    /// <summary>
    /// Plain delay after a receive bank (#873, Sprint 49 Track P): pads a faster
    /// engine's whole output (Strength's blend, or "Listen to what was removed")
    /// out to the slot's shared delay. Since phase 2 (Track P2) it is a client of
    /// the lag shifter's core (<see cref="RxLagShifter"/>): one ring, so a fixed
    /// pad and a drained or grown one read the same history. <see cref="Process"/>
    /// and <see cref="ProcessTwo"/> are phase 1's line exactly (write, then read a
    /// fixed pad back; silence for what was never written). <see cref="Steer"/>
    /// moves the delay toward a target by whole splices instead of jumping. All
    /// allocated once off the audio thread; nothing here allocates or waits.
    /// </summary>
    internal sealed class PadLine
    {
        private readonly RxLagShifter _line;
        private readonly int _maxPad;

        public PadLine(int maxPadFrames, int channels, int sampleRate = 48000, RxLagBudget? budget = null)
        {
            _maxPad = Math.Max(0, maxPadFrames);
            // Seams are carried through a pad line, never joined in it: the join is the
            // playback queue's, at the place the seam is actually heard (B1).
            _line = new RxLagShifter(Math.Max(1, channels), _maxPad + 8192, sampleRate, budget) { CarrySeams = true };
        }

        private long _outAtCall;

        /// <summary>Each call starts a fresh report of the seams that come out of it.</summary>
        private void BeginCall()
        {
            _line.ClearEmerged();
            _outAtCall = _line.OutputPosition;
        }

        /// <summary>
        /// Mark the seams <paramref name="trail"/> holds where this call's write will put
        /// them, for audio <paramref name="latency"/> frames behind the trail's input
        /// (the engine's own delay; 0 for the dry signal). Call just before the write.
        /// </summary>
        public void MarkSeams(SeamTrail trail, int latency) => trail.MarkInto(_line, latency);

        /// <summary>Seams that came out of the last call.</summary>
        public int SeamCount => _line.EmergedCount;

        /// <summary>Where the <paramref name="index"/>-th seam came out, in frames into the last call's audio.</summary>
        public int SeamOffset(int index) => (int)(_line.EmergedAt(index) - _outAtCall);

        /// <summary>The longest pad this line can hold, in frames.</summary>
        public int MaxPad => _maxPad;

        /// <summary>The core, for its events and counters.</summary>
        public RxLagShifter Shifter => _line;

        /// <summary>
        /// How many frames have gone in since the line was made or cleared, up
        /// to <see cref="MaxPad"/>: a read at a pad no longer than this is real
        /// audio from the start of the next buffer, never left-over history.
        /// </summary>
        public int Filled => (int)Math.Min(_maxPad, _line.Filled);

        /// <summary>The pad the last buffer came out at, in frames.</summary>
        public int Delay => (int)_line.Available;

        /// <summary>Content in the line is classified this many frames earlier (its engine's own delay).</summary>
        public int ClassLag { get => _line.ClassLagFrames; set => _line.ClassLagFrames = value; }

        /// <summary>Delay <paramref name="audio"/> in place by <paramref name="pad"/> frames (clamped to 0 to <see cref="MaxPad"/>).</summary>
        public void Process(Span<float> audio, int pad, RxContentClass cls = RxContentClass.Unclassified)
        {
            BeginCall();
            _line.ProcessFixed(audio, Math.Clamp(pad, 0, _maxPad), cls);
        }

        /// <summary>One line, two taps (repair 1, B2, phase 1's retime).</summary>
        public void ProcessTwo(Span<float> audio, int padA, Span<float> second, int padB)
        {
            BeginCall();
            _line.ProcessFixedTwo(audio, Math.Clamp(padA, 0, _maxPad), second, Math.Clamp(padB, 0, _maxPad));
        }

        /// <summary>
        /// Phase 2: write <paramref name="audio"/> and render it back with the pad
        /// moving toward <paramref name="targetPad"/> by whole splices, on
        /// <paramref name="policy"/>. Asked afresh every buffer, from the pad the
        /// line actually holds, so nothing drifts.
        /// </summary>
        public void Steer(Span<float> audio, int targetPad, in LagPolicy policy, LagCause cause, RxContentClass cls)
        {
            targetPad = Math.Clamp(targetPad, 0, _maxPad);
            BeginCall();
            _line.Request(_line.Available - targetPad, policy, cause, exact: true);
            _line.ProcessSteered(audio, cls);
        }

        /// <summary>True when the pad is within one smallest splice of <paramref name="targetPad"/> and no splice is part-way.</summary>
        public bool SettledAt(int targetPad) => !_line.Fading && Math.Abs(_line.Available - Math.Clamp(targetPad, 0, _maxPad)) < _line.MinLagFrames;

        /// <summary>Keep the line's history current at a pad of zero, with no output: so a later read at a pad has audio behind it.</summary>
        public void Feed(ReadOnlySpan<float> audio, RxContentClass cls = RxContentClass.Unclassified)
        {
            BeginCall();
            _line.Write(audio, cls);
            _line.SetDelay(0);
        }

        /// <summary>Put the pad at <paramref name="pad"/> at once, with no output: phase 1's jump, for a line nobody has heard yet.</summary>
        public void SetDelay(int pad) => _line.SetDelay(Math.Clamp(pad, 0, _maxPad));

        /// <summary>Forget everything held: the next output is silence for the pad's length.</summary>
        public void Clear() => _line.Clear();
    }

    /// <summary>
    /// The seams in one receive slot's INPUT, kept by absolute input frame, so each
    /// can be put where it lands in whatever the slot plays: a pad line written
    /// this buffer (<see cref="MarkInto"/>, at that line's own engine delay), or
    /// audio played straight through at a fixed delay (<see cref="Direct"/>).
    /// Codex's check of 2567a596 (B1): a seam mark rode the buffer that came out
    /// of the slots, which still held audio from before the lost packets; the
    /// queue then joined 40 ms (or more) too early. No allocation after
    /// construction.
    /// </summary>
    internal sealed class SeamTrail
    {
        private readonly long[] _in = new long[16];
        private int _count;
        private long _next;
        private readonly long _keep;

        public SeamTrail(int sampleRate) => _keep = Math.Max(1, sampleRate) * 4L;

        /// <summary>The absolute input frame of this buffer's first frame.</summary>
        public long InputStart { get; private set; }

        /// <summary>This buffer's frames.</summary>
        public int Frames { get; private set; }

        /// <summary>A new input buffer of <paramref name="frames"/> frames begins.</summary>
        public void Begin(int frames)
        {
            InputStart = _next;
            Frames = frames;
            _next += frames;
            // Long after any delay a slot can hold, a seam has come out or been cut away.
            int drop = 0;
            while (drop < _count && _in[drop] < InputStart - _keep) drop++;
            if (drop > 0) { Array.Copy(_in, drop, _in, 0, _count - drop); _count -= drop; }
        }

        /// <summary>A seam <paramref name="offset"/> frames into this buffer's input.</summary>
        public void Add(int offset)
        {
            if (_count == _in.Length) { Array.Copy(_in, 1, _in, 0, _in.Length - 1); _count--; }
            _in[_count++] = InputStart + Math.Max(0, offset);
        }

        /// <summary>
        /// Mark <paramref name="line"/>'s seams for the write about to happen: audio
        /// <paramref name="latency"/> frames behind this buffer's input, so a seam at
        /// input frame p is in this write when p + latency falls in this buffer.
        /// </summary>
        public void MarkInto(RxLagShifter line, int latency)
        {
            long w = line.WritePosition;
            for (int i = 0; i < _count; i++)
            {
                long q = _in[i] + latency;
                if (q >= InputStart && q < InputStart + Frames) line.MarkSeamAt(w + (q - InputStart));
            }
        }

        /// <summary>
        /// The seams that come out of this buffer when what is played is the input
        /// <paramref name="latency"/> frames late, with no line between: their
        /// offsets into this buffer, written into <paramref name="offsets"/> from
        /// <paramref name="count"/> on; returns the new count.
        /// </summary>
        public int Direct(int latency, int[] offsets, int count)
        {
            for (int i = 0; i < _count && count < offsets.Length; i++)
            {
                long off = _in[i] + latency - InputStart;
                if (off >= 0 && off < Frames) offsets[count++] = (int)off;
            }
            return count;
        }
    }

    /// <summary>
    /// The fade across a switch between two engines whose outputs are lined up
    /// to the sample (#873; the follow-up research, 2026-11-08, section 1): the
    /// correlation-matched pair from <see cref="CrossFadeCurve"/>, r measured per
    /// channel over the buffer before the switch. No allocation.
    /// </summary>
    internal static class AlignedSwitch
    {
        /// <summary>
        /// Per channel: the normalised correlation of <paramref name="a"/> and
        /// <paramref name="b"/> at lag zero, clamped to 0 to 1, written into
        /// <paramref name="r"/>. Silence on either side reads 0 (equal power,
        /// which is right for two unrelated signals and harmless for silence).
        /// </summary>
        public static void Correlation(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int channels, Span<float> r)
        {
            int frames = Math.Min(a.Length, b.Length) / channels;
            for (int c = 0; c < channels; c++)
            {
                double xy = 0, xx = 0, yy = 0;
                for (int f = 0; f < frames; f++)
                {
                    double x = a[f * channels + c], y = b[f * channels + c];
                    xy += x * y; xx += x * x; yy += y * y;
                }
                r[c] = xx < 1e-12 || yy < 1e-12 ? 0f : (float)Math.Clamp(xy / Math.Sqrt(xx * yy), 0, 1);
            }
        }

        /// <summary>
        /// Fade <paramref name="oldAudio"/> into <paramref name="newAudio"/> across
        /// the buffer, in place in <paramref name="oldAudio"/>, on the shared ramp
        /// at each channel's correlation. The last frame is all new, so the next
        /// buffer (the new engine alone) follows without a step.
        /// </summary>
        public static void Fade(Span<float> oldAudio, ReadOnlySpan<float> newAudio, int channels, ReadOnlySpan<float> r)
        {
            int frames = oldAudio.Length / channels;
            for (int f = 0; f < frames; f++)
            {
                float t = (f + 1f) / frames;
                for (int c = 0; c < channels; c++)
                {
                    int i = f * channels + c;
                    oldAudio[i] = oldAudio[i] * CrossFadeCurve.Out(t, r[c]) + newAudio[i] * CrossFadeCurve.In(t, r[c]);
                }
            }
        }
    }
}
