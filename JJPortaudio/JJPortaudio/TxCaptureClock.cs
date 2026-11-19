using System;
using System.Diagnostics;

namespace JJPortaudio
{
    /// <summary>
    /// When a transmit capture block was captured, on the
    /// <see cref="Stopwatch"/> clock (Sprint 49 Track H2, rulings repair 2,
    /// Codex's P1). The held unkey marks its release on that clock; this says
    /// where each capture frame falls against it, so speech said before the
    /// release but delivered after it is kept, and nothing captured after it
    /// goes on air.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why PortAudio's per-callback ADC time, and not a sample count against
    /// a time read at the release.</b> Both were open. The callback is told,
    /// in its own stream clock, when the first sample of the block reached the
    /// converter (<c>inputBufferAdcTime</c>) and when the callback began
    /// (<c>currentTime</c>). Their DIFFERENCE is the block's age, a duration in
    /// one clock, so it needs no conversion between PortAudio's stream clock
    /// and the Stopwatch: the block's capture time is the callback's own
    /// Stopwatch reading minus that age. A sample count read at the release
    /// would need a native call on the stream from the control thread (the
    /// stream may be closing; #1145's closed-stream guards exist because such
    /// calls ended the process), and it would still have to guess the device's
    /// latency, which is exactly the part of the block a release can fall in.
    /// </para>
    /// <para>
    /// <b>When the host API does not say.</b> Not every host API fills the
    /// timing in (see Audio.noteDeviceLatency). Then, or when the figure is not
    /// believable (younger than the block itself, or older than
    /// <see cref="MaxTrustedInputLatencyMs"/> beyond it), the block is taken to
    /// end at the moment its callback began: no device latency at all. That is
    /// the latest the block can have been captured, so the error only ever
    /// silences pre-release speech (at most the device's real latency), and
    /// never lets post-release audio through.
    /// </para>
    /// </remarks>
    public static class TxCaptureClock
    {
        /// <summary>
        /// The most device latency, beyond the block's own length, that a
        /// reported capture time is believed for. Past it, the report is
        /// treated as missing. A driver that over-reports could otherwise let
        /// that much post-release audio through.
        /// </summary>
        public const int MaxTrustedInputLatencyMs = 200;

        /// <summary>
        /// The Stopwatch timestamp at which the first sample of a capture block
        /// was captured.
        /// </summary>
        /// <param name="callbackTimestamp">Stopwatch timestamp taken as the callback began.</param>
        /// <param name="adcAgeSeconds">
        /// <c>currentTime - inputBufferAdcTime</c> from PortAudio's callback
        /// timing: how long before the callback the block's first sample was
        /// captured. Non-finite, zero or negative when not supplied.
        /// </param>
        /// <param name="blockFrames">Sample frames in the block.</param>
        /// <param name="sampleRate">The stream's sample rate.</param>
        public static long BlockStartTimestamp(long callbackTimestamp, double adcAgeSeconds, long blockFrames, uint sampleRate)
        {
            long blockTicks = FrameOffsetTicks(blockFrames, sampleRate);
            long freq = Stopwatch.Frequency;
            if (!double.IsNaN(adcAgeSeconds) && !double.IsInfinity(adcAgeSeconds) && adcAgeSeconds > 0)
            {
                long ageTicks = (long)(adcAgeSeconds * freq);
                long maxTicks = blockTicks + MaxTrustedInputLatencyMs * freq / 1000;
                if (ageTicks >= blockTicks && ageTicks <= maxTicks) return callbackTimestamp - ageTicks;
            }
            return callbackTimestamp - blockTicks;
        }

        /// <summary>
        /// The device latency, beyond the block's own length, that
        /// <see cref="BlockStartTimestamp"/> believes for this block, in whole
        /// milliseconds rounded up: zero when it falls back (not reported, not
        /// believable), never more than <see cref="MaxTrustedInputLatencyMs"/>.
        /// The held unkey's backstop budgets this much for the delivery of the
        /// block holding the release (Sprint 49 Track H2, join repair 3, Codex's
        /// P1), so the timing the clock accepts never runs into the backstop.
        /// </summary>
        public static int TrustedExcessLatencyMs(double adcAgeSeconds, long blockFrames, uint sampleRate)
        {
            if (double.IsNaN(adcAgeSeconds) || double.IsInfinity(adcAgeSeconds) || adcAgeSeconds <= 0) return 0;
            long freq = Stopwatch.Frequency;
            long blockTicks = FrameOffsetTicks(blockFrames, sampleRate);
            long ageTicks = (long)(adcAgeSeconds * freq);
            long maxTicks = blockTicks + MaxTrustedInputLatencyMs * freq / 1000;
            if (ageTicks < blockTicks || ageTicks > maxTicks) return 0;
            long excess = ageTicks - blockTicks;
            return (int)Math.Min(MaxTrustedInputLatencyMs, (excess * 1000 + freq - 1) / freq);
        }

        /// <summary>Stopwatch ticks spanned by <paramref name="frames"/> sample frames at <paramref name="sampleRate"/>.</summary>
        public static long FrameOffsetTicks(long frames, uint sampleRate) =>
            sampleRate == 0 || frames <= 0 ? 0 : frames * Stopwatch.Frequency / sampleRate;
    }
}
