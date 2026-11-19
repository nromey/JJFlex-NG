using System;
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
    /// <b>Each answer once</b> (repair 2, after Codex's re-check): the work is
    /// read by a <see cref="HelperServiceMeter"/>, which counts only answers
    /// that arrived since its last look. Repair 1 re-read each channel's newest
    /// figure on every buffer for a second, so one slow answer followed by a
    /// stall in delivery was counted as many slow computations.
    /// </para>
    /// </remarks>
    internal static class HelperServiceTime
    {
        /// <summary>The audio in one helper frame, in nanoseconds: 480 samples at 48 kHz, 10 ms.</summary>
        public const long FrameNanos = (long)WdspProtocol.FrameLength * 1_000_000_000L / WdspProtocol.SampleRate;

        /// <summary>
        /// How recent a channel's newest answer must be for the channel to count
        /// as served: one second, the keep-up rule's window. A channel no longer
        /// fed stops being waited for within the window. This decides only which
        /// channels a round waits for; it never makes an old answer count again.
        /// </summary>
        public static readonly long RecentTicks = Stopwatch.Frequency;

        /// <summary>The helper connection a bank's engines run on, or null when it is not a helper bank or has no channel yet.</summary>
        internal static WdspConnection? ConnectionOf(PerChannelDenoiserBank? bank)
        {
            if (bank == null) return null;
            for (int ch = 0; ch < bank.Channels; ch++)
                if (bank.EngineFor(ch) is WdspHelperEngine engine && engine.Channel is { } channel) return channel.Conn;
            return null;
        }

        /// <summary>Whether a channel is being served: open, not closed, and answered within <see cref="RecentTicks"/> of <paramref name="now"/>.</summary>
        internal static bool Served(WdspChannel? ch, long now)
        {
            if (ch == null || !ch.Opened || ch.Closed) return false;
            long at = Volatile.Read(ref ch.ProcessedAt);
            return at != 0 && now - at <= RecentTicks;
        }

        /// <summary>Whether that much work per frame takes longer than the frame's audio lasts. An unknown figure (-1) is not late.</summary>
        public static bool Late(long perFrameNanos) => perFrameNanos > FrameNanos;
    }

    /// <summary>
    /// Where a receive slot reads the helper's new work: given the slot's running
    /// bank, the slot's own meter and the time (<see cref="RxAudioPipeline.HelperService"/>).
    /// </summary>
    internal delegate HelperServiceReading HelperServiceSource(PerChannelDenoiserBank? bank, HelperServiceMeter meter, long now);

    /// <summary>
    /// What a <see cref="HelperServiceMeter"/> found: the helper's new work since
    /// its last look, as the processing time per frame of audio (every served
    /// channel added together) over <see cref="Frames"/> frames. No frames
    /// means no new work, and nothing to judge.
    /// </summary>
    internal readonly struct HelperServiceReading
    {
        /// <summary>Processing time for one frame on every served channel together, in nanoseconds; -1 when no helper is connected.</summary>
        public readonly long PerFrameNanos;
        /// <summary>How many frames of audio, on every served channel, that work covered; 0 when no new answer completed a round.</summary>
        public readonly int Frames;

        public HelperServiceReading(long perFrameNanos, int frames) { PerFrameNanos = perFrameNanos; Frames = frames; }

        /// <summary>No helper is connected.</summary>
        public static HelperServiceReading NoHelper => new(-1, 0);
        /// <summary>A helper is connected, but nothing new has been answered on every channel it serves.</summary>
        public static HelperServiceReading NoNewWork => new(0, 0);
        /// <summary>One frame's new work at this cost (tests and fakes).</summary>
        public static HelperServiceReading Fresh(long perFrameNanos) => new(perFrameNanos, 1);
    }

    /// <summary>
    /// The helper's new work since the last look, counted once per answer
    /// (Sprint 49 Track L repair 2, #866; Codex's re-check found repair 1's
    /// reading turned one slow answer and a delivery stall into a sustained
    /// fall). One per reader: each receive slot owns one, so the two slots do
    /// not take each other's answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A round.</b> The meter remembers, per served channel, the answer
    /// count and processing total it last saw. A round ends when every served
    /// channel has at least one new answer. The round's work per frame is, for
    /// each channel, its new processing time divided by its new answers, added
    /// over the channels, because the helper's one worker does every channel's
    /// frame in each 10 ms. Its frames are the fewest new answers any channel
    /// had, so a round never claims more audio than every channel was served.
    /// </para>
    /// <para>
    /// <b>Every answer reaches the rule exactly once</b> (repair 3, after
    /// Codex's second re-check). The host's writer drains one channel's queue
    /// before the next, so channels advance unequally: left twice and right
    /// once, then right twice and left once. A round advances each channel only
    /// by the frames it forwards, and its new cost only by those answers' share
    /// at the channel's average; the surplus answers and the rest of their cost
    /// wait for the next round. Repair 2 advanced every channel to its newest
    /// count and forwarded only the fewest, so a channel's extra answers were
    /// consumed without their work reaching the rule (36 ms of work became
    /// 24 ms). A channel that leaves the round takes its surplus with it.
    /// </para>
    /// <para>
    /// <b>No new answers, no new work.</b> Until every served channel answers
    /// again the meter reports no frames, so a stall in delivery adds nothing,
    /// however long; and a burst after it counts each delayed answer once, at
    /// its own processing time. A channel that stops being served (closed, or
    /// no answer for <see cref="HelperServiceTime.RecentTicks"/>) leaves the
    /// round; one that starts is first seen with its counts as they stand, so
    /// answers before the meter saw it are not counted.
    /// </para>
    /// <para>
    /// Audio thread only. No allocation, no lock, no wait: a channel caught
    /// mid-answer is read again on the next buffer.
    /// </para>
    /// </remarks>
    internal sealed class HelperServiceMeter
    {
        /// <summary>
        /// Channels one meter follows: far more than one helper serves (two
        /// receive slots in stereo and a transmit channel is six). A channel past
        /// this is not followed, so its work is under-counted, never invented.
        /// </summary>
        public const int Capacity = 16;

        private readonly WdspChannel?[] _channel = new WdspChannel?[Capacity];
        private readonly long[] _answers = new long[Capacity], _nanos = new long[Capacity];
        private readonly long[] _nowAnswers = new long[Capacity], _nowNanos = new long[Capacity];
        private readonly bool[] _seen = new bool[Capacity];
        private int _count;

        /// <summary>Channels followed now, for tests.</summary>
        internal int Following => _count;

        /// <summary>Start again: every channel is first seen afresh, so answers before now are not counted. After a restart, a switch or a gap.</summary>
        public void Forget()
        {
            Array.Clear(_channel);
            _count = 0;
        }

        /// <summary>The new work on the connection a bank's engines run on, or the app's helper's when the bank has none.</summary>
        public HelperServiceReading Read(PerChannelDenoiserBank? bank, long now)
        {
            var conn = HelperServiceTime.ConnectionOf(bank) ?? WdspHost.DefaultIfCreated?.LiveConnection;
            return conn == null ? HelperServiceReading.NoHelper : Read(conn.Lives, now);
        }

        /// <summary>The new work on these channels since the last look.</summary>
        public HelperServiceReading Read(WdspChannel[] channels, long now)
        {
            for (int i = 0; i < _count; i++) _seen[i] = false;
            foreach (var ch in channels)
            {
                if (!HelperServiceTime.Served(ch, now)) continue;
                int at = IndexOf(ch!);
                if (at >= 0) { _seen[at] = true; continue; }
                if (_count == Capacity || !ch!.TryReadServed(out long answers, out long nanos)) continue;
                _channel[_count] = ch; _answers[_count] = answers; _nanos[_count] = nanos; _seen[_count] = true;
                _count++;
            }
            // Channels no longer served leave the round.
            int kept = 0;
            for (int i = 0; i < _count; i++)
            {
                if (!_seen[i]) continue;
                if (kept != i) { _channel[kept] = _channel[i]; _answers[kept] = _answers[i]; _nanos[kept] = _nanos[i]; }
                kept++;
            }
            for (int i = kept; i < _count; i++) _channel[i] = null;
            _count = kept;
            if (_count == 0) return HelperServiceReading.NoNewWork;

            // The round ends only when every served channel has a new answer.
            long fewest = long.MaxValue;
            for (int i = 0; i < _count; i++)
            {
                if (!_channel[i]!.TryReadServed(out _nowAnswers[i], out _nowNanos[i])) return HelperServiceReading.NoNewWork;
                long fresh = _nowAnswers[i] - _answers[i];
                if (fresh <= 0) return HelperServiceReading.NoNewWork;
                if (fresh < fewest) fewest = fresh;
            }
            // Each channel gives up only the answers the round forwards, at its
            // own average; the rest, and their share of the cost (with any
            // rounding left over), wait for the next round (repair 3).
            if (fewest > int.MaxValue) fewest = int.MaxValue;
            long perFrame = 0;
            for (int i = 0; i < _count; i++)
            {
                long perAnswer = (_nowNanos[i] - _nanos[i]) / (_nowAnswers[i] - _answers[i]);
                perFrame += perAnswer;
                _answers[i] += fewest;
                _nanos[i] += perAnswer * fewest;
            }
            return new HelperServiceReading(perFrame, (int)fewest);
        }

        private int IndexOf(WdspChannel ch)
        {
            for (int i = 0; i < _count; i++) if (ReferenceEquals(_channel[i], ch)) return i;
            return -1;
        }
    }
}
