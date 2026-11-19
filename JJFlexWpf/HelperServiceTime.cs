using System.Diagnostics;
using System.Threading;

namespace JJFlexWpf
{
    /// <summary>
    /// Whether the WDSP helper's one worker keeps up, in its own measure
    /// (Sprint 49 Track L repair 1, #866). Noel's X2 ruling: an engine falls
    /// behind when it takes longer to process audio than the audio lasts. For
    /// a helper engine that is the helper's own processing time per frame
    /// (FrameOut's <c>ProcessingNanos</c>, the engine's work alone, without the
    /// pipe), not whether its answers arrived in time: a fast engine whose
    /// answers are delayed on the way back is a delivery problem, which the
    /// pipeline traces separately and never warns about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every live channel together.</b> The helper processes frames one at
    /// a time on a single worker, so the work due in each 10 ms of audio is the
    /// sum over every channel it serves: both stereo channels, both receive
    /// slots, and a transmit channel too while one is fed. One channel's figure
    /// against 10 ms would miss a helper that is overloaded only in total.
    /// </para>
    /// <para>
    /// A channel counts while it is open and its newest answer arrived within
    /// <see cref="RecentTicks"/>; a channel nobody feeds drops out of the sum.
    /// No allocation, no lock, no wait: the receive audio thread calls it per
    /// buffer.
    /// </para>
    /// </remarks>
    internal static class HelperServiceTime
    {
        /// <summary>The audio in one helper frame, in nanoseconds: 480 samples at 48 kHz, 10 ms.</summary>
        public const long FrameNanos = (long)WdspProtocol.FrameLength * 1_000_000_000L / WdspProtocol.SampleRate;

        /// <summary>
        /// How recent a channel's newest answer must be for its work to count:
        /// one second, the keep-up rule's window. Long enough that a badly
        /// overloaded helper, whose answers come seldom, still counts every
        /// channel it is serving; short enough that a channel no longer fed
        /// stops counting within the window.
        /// </summary>
        public static readonly long RecentTicks = Stopwatch.Frequency;

        /// <summary>
        /// The helper's processing time for one frame on every channel it is
        /// serving now, in nanoseconds; -1 when no helper is connected. The
        /// connection is the running bank's when it is a helper bank, otherwise
        /// the app's helper's (a warming helper bank is the slot's own, so it is
        /// reached through the host).
        /// </summary>
        public static long PerFrameNanos(PerChannelDenoiserBank? bank, long now)
        {
            var conn = ConnectionOf(bank) ?? WdspHost.DefaultIfCreated?.LiveConnection;
            return conn == null ? -1 : Sum(conn.Lives, now);
        }

        /// <summary>The helper connection a bank's engines run on, or null when it is not a helper bank or has no channel yet.</summary>
        internal static WdspConnection? ConnectionOf(PerChannelDenoiserBank? bank)
        {
            if (bank == null) return null;
            for (int ch = 0; ch < bank.Channels; ch++)
                if (bank.EngineFor(ch) is WdspHelperEngine engine && engine.Channel is { } channel) return channel.Conn;
            return null;
        }

        /// <summary>The newest per-frame processing time of every open channel answered within <see cref="RecentTicks"/> of <paramref name="now"/>, added together.</summary>
        internal static long Sum(WdspChannel[] channels, long now)
        {
            long sum = 0;
            foreach (var ch in channels)
            {
                if (ch == null || !ch.Opened || ch.Closed) continue;
                long at = Volatile.Read(ref ch.ProcessedAt);
                if (at == 0 || now - at > RecentTicks) continue;
                sum += Volatile.Read(ref ch.ProcessingNanos);
            }
            return sum;
        }

        /// <summary>Whether that much work per frame takes longer than the frame's audio lasts. An unknown figure (-1) is not late.</summary>
        public static bool Late(long perFrameNanos) => perFrameNanos > FrameNanos;
    }
}
