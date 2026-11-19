using System;

namespace JJFlexWpf
{
    /// <summary>
    /// Plain delay after a receive bank (#873, Sprint 49 Track P): pads a faster
    /// engine's whole output (Strength's blend, or "Listen to what was removed")
    /// out to the slot's shared delay. A ring of interleaved frames, allocated
    /// once off the audio thread; <see cref="Process"/> neither allocates nor
    /// waits. The pad may change between buffers: the read point moves with it,
    /// which is how the slot absorbs a change in the bank's own delay (the frame
    /// carry) without the output moving in time.
    /// </summary>
    internal sealed class PadLine
    {
        private readonly float[] _ring;
        private readonly int _channels, _frames;
        private int _at;

        public PadLine(int maxPadFrames, int channels)
        {
            _channels = Math.Max(1, channels);
            _frames = Math.Max(0, maxPadFrames) + 1;
            _ring = new float[_frames * _channels];
        }

        /// <summary>The longest pad this line can hold, in frames.</summary>
        public int MaxPad => _frames - 1;

        /// <summary>Delay <paramref name="audio"/> in place by <paramref name="pad"/> frames (clamped to 0 to <see cref="MaxPad"/>).</summary>
        public void Process(Span<float> audio, int pad)
        {
            pad = Math.Clamp(pad, 0, MaxPad);
            int frames = audio.Length / _channels;
            for (int f = 0; f < frames; f++)
            {
                int w = _at * _channels, r = _at - pad;
                if (r < 0) r += _frames;
                r *= _channels;
                for (int c = 0; c < _channels; c++)
                {
                    int i = f * _channels + c;
                    _ring[w + c] = audio[i];
                    audio[i] = _ring[r + c];
                }
                if (++_at == _frames) _at = 0;
            }
        }

        /// <summary>Forget everything held: the next output is silence for the pad's length.</summary>
        public void Clear()
        {
            Array.Clear(_ring);
            _at = 0;
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
