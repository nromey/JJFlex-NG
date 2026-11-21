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
            _line = new RxLagShifter(Math.Max(1, channels), _maxPad + 8192, sampleRate, budget);
        }

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
        public void Process(Span<float> audio, int pad, RxContentClass cls = RxContentClass.Unclassified) =>
            _line.ProcessFixed(audio, Math.Clamp(pad, 0, _maxPad), cls);

        /// <summary>One line, two taps (repair 1, B2, phase 1's retime).</summary>
        public void ProcessTwo(Span<float> audio, int padA, Span<float> second, int padB) =>
            _line.ProcessFixedTwo(audio, Math.Clamp(padA, 0, _maxPad), second, Math.Clamp(padB, 0, _maxPad));

        /// <summary>
        /// Phase 2: write <paramref name="audio"/> and render it back with the pad
        /// moving toward <paramref name="targetPad"/> by whole splices, on
        /// <paramref name="policy"/>. Asked afresh every buffer, from the pad the
        /// line actually holds, so nothing drifts.
        /// </summary>
        public void Steer(Span<float> audio, int targetPad, in LagPolicy policy, LagCause cause, RxContentClass cls)
        {
            targetPad = Math.Clamp(targetPad, 0, _maxPad);
            _line.Request(_line.Available - targetPad, policy, cause, exact: true);
            _line.ProcessSteered(audio, cls);
        }

        /// <summary>True when the pad is within one smallest splice of <paramref name="targetPad"/> and no splice is part-way.</summary>
        public bool SettledAt(int targetPad) => !_line.Fading && Math.Abs(_line.Available - Math.Clamp(targetPad, 0, _maxPad)) < _line.MinLagFrames;

        /// <summary>Keep the line's history current at a pad of zero, with no output: so a later read at a pad has audio behind it.</summary>
        public void Feed(ReadOnlySpan<float> audio, RxContentClass cls = RxContentClass.Unclassified)
        {
            _line.Write(audio, cls);
            _line.SetDelay(0);
        }

        /// <summary>Put the pad at <paramref name="pad"/> at once, with no output: phase 1's jump, for a line nobody has heard yet.</summary>
        public void SetDelay(int pad) => _line.SetDelay(Math.Clamp(pad, 0, _maxPad));

        /// <summary>Forget everything held: the next output is silence for the pad's length.</summary>
        public void Clear() => _line.Clear();
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
