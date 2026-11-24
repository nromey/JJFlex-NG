using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using JJTrace;
using JJPortaudio;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// #1229: every starvation and every ceiling trim on the receive queue is
    /// placed in time, and the silence a stream plays after its feed has ended
    /// is counted at stop, not as a gap. Driven by fed callbacks, the way
    /// <see cref="ReceiveQueueRatchetTests"/> drives the queue; no clock is read.
    /// </summary>
    /// <remarks>
    /// The shipped shape: ten 10 ms buffers per callback, so one callback is
    /// 100 ms and a second is ten callbacks.
    /// </remarks>
    public class ReceiveQueueTimingTests
    {
        private const int BuffersPerCallback = 10;
        private const double BufferMs = 10.0;
        private const int FramesPerBuffer = 480;
        private const uint Rate = 48000;

        private readonly ITestOutputHelper _out;
        public ReceiveQueueTimingTests(ITestOutputHelper output) { _out = output; }

        /// <summary>The same five steps as outputCallback, against the real queue.</summary>
        private sealed class Sim
        {
            public readonly RxPlaybackQueue Queue;
            public readonly List<(long Callback, string Line)> Lines = new();
            public int Depth;

            public Sim()
            {
                Queue = new RxPlaybackQueue("receive", BuffersPerCallback, BufferMs, Rate,
                    RxPlaybackQueue.DefaultReserveMilliseconds);
                Queue.LineObserver = l => Lines.Add((Queue.Callbacks, l));
            }

            /// <summary>One callback, then the feeding side's drain (the receive loop hands it to the pool; here it is run in line).</summary>
            public void Callback(int arrivals)
            {
                CallbackOnly(arrivals);
                Queue.DrainLines();
            }

            public void CallbackOnly(int arrivals)
            {
                Depth += arrivals;
                RxCallbackPlan plan = Queue.Begin(Depth, midBuffer: false);
                Depth -= Math.Min(plan.Discard, Depth);
                if (plan.HoldForPrime)
                {
                    Queue.NotePrimingBuffer((long)BuffersPerCallback * FramesPerBuffer);
                    return;
                }
                int consumed = Math.Min(Depth, BuffersPerCallback);
                Depth -= consumed;
                Queue.NotePlayed(consumed, (long)(BuffersPerCallback - consumed) * FramesPerBuffer);
            }

            public void Steady(int callbacks) { for (int i = 0; i < callbacks; i++) Callback(BuffersPerCallback); }

            /// <summary>A starvation: the producer delivers nothing for one callback, then catches up.</summary>
            public long Starve()
            {
                Callback(0);
                long at = Queue.Callbacks;
                // Rebuild the reserve the starvation spent, as a real burst would.
                Callback(BuffersPerCallback + 6);
                return at;
            }

            public IEnumerable<string> Matching(string text) => Lines.Where(l => l.Line.Contains(text)).Select(l => l.Line);
        }

        [Fact]
        public void A_lone_starvation_after_the_first_is_written_by_the_first_callback_after_its_second_ends()
        {
            var sim = new Sim();
            Assert.Equal(10, sim.Queue.WindowCallbacks);
            sim.Steady(5);
            long first = sim.Starve();
            Assert.Single(sim.Matching("ran dry mid-stream at callback " + first));
            sim.Steady(30);
            long lone = sim.Starve();
            // Nothing yet: its second has not ended.
            int before = sim.Lines.Count;
            sim.Steady((int)(lone + sim.Queue.WindowCallbacks - 1 - sim.Queue.Callbacks));
            Assert.Equal(before, sim.Lines.Count);
            // The very next callback ends the second and places it, with no
            // later starvation needed (until #1229 it waited for one).
            sim.Steady(1);
            var line = Assert.Single(sim.Lines.Skip(before));
            _out.WriteLine(line.Line);
            Assert.Equal(lone + sim.Queue.WindowCallbacks, line.Callback);
            Assert.Contains("1 starvation(s)", line.Line);
            Assert.Contains("in the second from callback " + lone, line.Line);
            Assert.Contains("running total 2", line.Line);
            Assert.Contains("second closed at callback " + (lone + sim.Queue.WindowCallbacks), line.Line);
        }

        [Fact]
        public void Each_second_with_starvations_gets_one_line_counting_them_and_a_quiet_second_gets_none()
        {
            var sim = new Sim();
            sim.Steady(5);
            sim.Starve();                       // the first: its own long line
            sim.Steady(20);
            long a = sim.Starve();
            long b = sim.Starve();              // two in the same second
            sim.Steady(40);                     // several quiet seconds
            long c = sim.Starve();              // a later second
            sim.Steady(20);
            sim.Queue.TraceSummary();
            foreach (var l in sim.Lines) _out.WriteLine(l.Callback + ": " + l.Line);

            var seconds = sim.Matching("starvation(s) costing").Where(l => l.Contains("from callback")).ToList();
            Assert.Equal(2, seconds.Count);
            Assert.Contains("2 starvation(s)", seconds[0]);
            Assert.Contains("from callback " + a + " (the last at callback " + b + ")", seconds[0]);
            Assert.Contains("1 starvation(s)", seconds[1]);
            Assert.Contains("from callback " + c, seconds[1]);
            Assert.Contains("running total 4", seconds[1]);
            Assert.Empty(sim.Matching("final partial second"));
        }

        [Fact]
        public void Every_ceiling_trim_is_placed_the_first_at_once_and_later_ones_once_per_second()
        {
            var sim = new Sim();
            sim.Steady(5);
            sim.Callback(30);                   // a burst over the ceiling: the first trim
            long first = sim.Queue.Callbacks;
            var firstLine = Assert.Single(sim.Matching("passed the " + sim.Queue.DrainCeiling + "-buffer ceiling"));
            Assert.Contains("at callback " + first, firstLine);
            Assert.Contains("discarded", firstLine);
            sim.Steady(20);
            sim.Callback(30);                   // a second burst, a lone trim
            long second = sim.Queue.Callbacks;
            long trimmed = sim.Queue.TrimmedBuffers;
            Assert.Empty(sim.Matching("trim(s) above the"));
            sim.Steady(sim.Queue.WindowCallbacks);
            foreach (var l in sim.Lines) _out.WriteLine(l.Callback + ": " + l.Line);
            var line = Assert.Single(sim.Matching("trim(s) above the"));
            Assert.Contains("1 trim(s)", line);
            Assert.Contains("in the second from callback " + second, line);
            Assert.Contains("running total 2 trim(s), " + trimmed + " buffer(s)", line);
            Assert.Equal(2, sim.Queue.Trims);
        }

        [Fact]
        public void A_trim_or_starvation_still_open_at_close_is_written_as_the_final_partial_second()
        {
            var sim = new Sim();
            sim.Steady(5);
            sim.Starve();
            sim.Steady(20);
            long late = sim.Starve();
            sim.Queue.TraceSummary();
            Assert.Single(sim.Matching("in the final partial second from callback " + late));
        }

        // ── The stop nit ──

        [Fact]
        public void When_the_producer_stops_before_the_stream_the_queue_runs_dry_once_and_then_holds()
        {
            // The cause, reproduced. On every PC-audio-off the receive loop
            // ended first and the stream was stopped about 560 ms later, so the
            // callback kept running on a queue nobody was feeding. It drains
            // what is standing, starves ONCE (that is the one "mid-stream
            // starvation in the final callbacks" of four streams of four), and
            // then holds for a prime that never comes. Without the feed-ended
            // signal it is counted as a gap in the audio.
            var sim = new Sim();
            sim.Steady(50);
            for (int i = 0; i < 6; i++) sim.Callback(0);   // the producer has stopped; the stream plays on
            sim.Queue.TraceSummary();
            foreach (var l in sim.Lines) _out.WriteLine(l.Line);
            Assert.Equal(1, sim.Queue.Starvations);
            Assert.Contains(sim.Lines, l => l.Line.Contains("ran dry mid-stream"));
        }

        [Fact]
        public void After_the_feed_ends_an_empty_queue_is_counted_at_stop_and_never_as_a_gap()
        {
            var sim = new Sim();
            sim.Steady(50);
            sim.Queue.NoteFeedEnded();
            for (int i = 0; i < 6; i++) sim.Callback(0);
            sim.Queue.TraceSummary();
            foreach (var l in sim.Lines) _out.WriteLine(l.Line);

            Assert.Equal(0, sim.Queue.Starvations);
            Assert.Equal(0, sim.Queue.StarvationSilenceMilliseconds);
            Assert.Equal(1, sim.Queue.StopStarvations);
            Assert.Equal(6, sim.Queue.StopSilentCallbacks);
            Assert.Equal(0, sim.Queue.PrimeEpisodes);
            Assert.DoesNotContain(sim.Lines, l => l.Line.Contains("ran dry mid-stream"));
            Assert.Single(sim.Matching("0 mid-stream starvation(s)"));
            var stop = Assert.Single(sim.Matching("at stop:"));
            Assert.Contains("1 starvation(s) at stop", stop);
            // The standing-latency figure no longer carries the stop's silence.
            Assert.Equal(sim.Queue.PrimeSilenceMilliseconds, sim.Queue.InsertedSilenceMilliseconds);
        }

        [Fact]
        public void A_gap_before_the_feed_ends_is_still_a_mid_stream_starvation()
        {
            // The positive control: the at-stop count must not swallow a real gap.
            var sim = new Sim();
            sim.Steady(20);
            sim.Starve();
            sim.Steady(20);
            sim.Queue.NoteFeedEnded();
            for (int i = 0; i < 4; i++) sim.Callback(0);
            Assert.Equal(1, sim.Queue.Starvations);
            Assert.Equal(1, sim.Queue.StopStarvations);
        }

        [Fact]
        public void The_receive_loop_marks_the_feed_ended_before_anything_on_its_way_out_can_block()
        {
            // Repair 1: the mark comes before the loop's own exit line and before
            // the exception path's line and announcement, any of which can block
            // while the stream drains; and again first at remoteDone, for every
            // goto. The first starvation came 82 ms after the loop ended.
            string text = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.RemoteAudio.cs"));
            const string mark = "MarkReceiveFeedEnded(run);";
            int exitLine = text.IndexOf("Tracing.TraceLine(\"remoteAudioProc:stopping remote audio\"", StringComparison.Ordinal);
            Assert.True(exitLine > 0);
            int markBeforeExit = text.LastIndexOf(mark, exitLine, StringComparison.Ordinal);
            int loopWait = text.LastIndexOf("wake.WaitForWork(RemoteAudioWake.IdleWakeMilliseconds);", exitLine, StringComparison.Ordinal);
            Assert.True(markBeforeExit > loopWait && loopWait > 0, "the loop's exit does not mark the feed ended before its trace line");

            int catchAt = text.IndexOf("catch (Exception ex)", exitLine, StringComparison.Ordinal);
            int markInCatch = text.IndexOf(mark, catchAt, StringComparison.Ordinal);
            int catchTrace = text.IndexOf("Tracing.TraceLine", catchAt, StringComparison.Ordinal);
            int speak = text.IndexOf("ScreenReaderOutput.Speak", catchAt, StringComparison.Ordinal);
            Assert.True(markInCatch > catchAt && markInCatch < catchTrace && markInCatch < speak,
                "the exception path traces or speaks before it marks the feed ended");

            int label = text.IndexOf("remoteDone:", exitLine, StringComparison.Ordinal);
            int markAtDone = text.IndexOf(mark, label, StringComparison.Ordinal);
            int feed = text.IndexOf("CloseReceiveTallies(run);", label, StringComparison.Ordinal);
            int summary = text.IndexOf("TraceOpusRxContinuitySummary()", label, StringComparison.Ordinal);
            int monitor = text.IndexOf("CWMonDone()", label, StringComparison.Ordinal);
            int stop = text.IndexOf("stopOpusOutputChannel()", label, StringComparison.Ordinal);
            Assert.True(markAtDone > label && markAtDone < feed && markAtDone < summary && markAtDone < monitor && markAtDone < stop,
                "remoteDone does work that takes time before it marks the feed ended");
            Assert.True(feed < stop, "the pipeline hears of the end after the stream is stopped");

            // And the mark itself writes nothing on its way.
            int body = text.IndexOf("void MarkReceiveFeedEnded(long run)", StringComparison.Ordinal);
            int bodyEnd = text.IndexOf("void CloseReceiveTallies(long run)", body, StringComparison.Ordinal);
            string method = text.Substring(body, bodyEnd - body);
            int noted = method.IndexOf("NoteFeedEnded()", StringComparison.Ordinal);
            int firstTrace = method.IndexOf("Tracing.TraceLine", StringComparison.Ordinal);
            int catchInMethod = method.IndexOf("catch", StringComparison.Ordinal);
            Assert.True(noted > 0 && firstTrace > catchInMethod, "the mark traces before or instead of marking");
        }

        // ── The boundary (repair 1): a callback is classified at its entry ──

        [Fact]
        public void A_shortage_found_by_a_callback_that_began_before_the_mark_is_a_gap_even_if_the_mark_lands_before_it_is_booked()
        {
            // Codex's interleaving: Begin while live, the callback finds the
            // queue short, the producer marks the feed ended, then the shortage
            // is booked. Until repair 1 it was filed "at stop" and vanished.
            var q = new RxPlaybackQueue("receive", BuffersPerCallback, BufferMs, Rate, RxPlaybackQueue.DefaultReserveMilliseconds);
            q.Begin(16, false); q.NotePlayed(10, 0);
            q.Begin(0, false);
            q.NoteFeedEnded();
            q.NotePlayed(0, (long)BuffersPerCallback * FramesPerBuffer);
            Assert.Equal(1, q.Starvations);
            Assert.Equal(0, q.StopStarvations);
        }

        [Fact]
        public void A_shortage_in_a_callback_that_began_after_the_mark_is_at_stop()
        {
            // The other way round, the positive control for the boundary.
            var q = new RxPlaybackQueue("receive", BuffersPerCallback, BufferMs, Rate, RxPlaybackQueue.DefaultReserveMilliseconds);
            q.Begin(16, false); q.NotePlayed(10, 0);
            q.NoteFeedEnded();
            q.Begin(0, false);
            q.NotePlayed(0, (long)BuffersPerCallback * FramesPerBuffer);
            Assert.Equal(0, q.Starvations);
            Assert.Equal(1, q.StopStarvations);
            // A hold that began before the mark is still a prime, not the stop's.
            var r = new RxPlaybackQueue("receive", BuffersPerCallback, BufferMs, Rate, RxPlaybackQueue.DefaultReserveMilliseconds);
            var plan = r.Begin(0, false);
            Assert.True(plan.HoldForPrime);
            r.NoteFeedEnded();
            r.NotePrimingBuffer((long)BuffersPerCallback * FramesPerBuffer);
            Assert.Equal(1, r.PrimeCallbacks);
            Assert.Equal(0, r.StopSilentCallbacks);
        }

        // ── The callback writes no line (repair 1) ──

        /// <summary>
        /// Codex's sequence: a release, a first ceiling trim (its own record), a
        /// second trim opening a window, nine quiet callbacks; the next Begin
        /// closes that second. Returns the queue ready for that Begin.
        /// </summary>
        private static RxPlaybackQueue ReadyToCloseATrimSecond(string name)
        {
            var q = new RxPlaybackQueue(name, BuffersPerCallback, BufferMs, Rate, RxPlaybackQueue.DefaultReserveMilliseconds);
            q.Begin(16, false); q.NotePlayed(10, 0);
            q.Begin(40, false); q.NotePlayed(10, 0);
            q.Begin(40, false); q.NotePlayed(10, 0);
            for (int i = 0; i < 9; i++) { q.Begin(16, false); q.NotePlayed(10, 0); }
            q.DrainLines();
            return q;
        }

        private static (bool On, TraceSwitch Switch) TracingOn(bool on)
        {
            var saved = (Tracing.On, Tracing.TheSwitch);
            Tracing.TheSwitch = new TraceSwitch("rxtiming", "rxtiming") { Level = TraceLevel.Verbose };
            Tracing.On = on;
            return saved;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Closing_a_second_and_recording_a_trim_or_starvation_allocate_nothing_on_the_callback(bool tracingOn)
        {
            var saved = TracingOn(tracingOn);
            try
            {
                // Warm every path once, so the JIT's own work is not counted.
                var warm = ReadyToCloseATrimSecond("receive");
                warm.Begin(16, false); warm.NotePlayed(10, 0);
                warm.Begin(16, false); warm.NotePlayed(4, 6L * FramesPerBuffer);
                warm.DrainLines();

                var q = ReadyToCloseATrimSecond("receive");
                long before = GC.GetAllocatedBytesForCurrentThread();
                q.Begin(16, false);                       // closes the trim's second
                q.NotePlayed(10, 0);
                long closing = GC.GetAllocatedBytesForCurrentThread() - before;

                before = GC.GetAllocatedBytesForCurrentThread();
                q.Begin(16, false);
                q.NotePlayed(4, 6L * FramesPerBuffer);    // the stream's first starvation, its long line's record
                long starving = GC.GetAllocatedBytesForCurrentThread() - before;

                // Positive control: both really were recorded, and draining writes them.
                Assert.True(q.LinesPending, "nothing was recorded, so the zero proves nothing");
                var lines = new List<string>();
                q.LineObserver = lines.Add;
                q.DrainLines();
                foreach (var l in lines) _out.WriteLine(l);
                Assert.Contains(lines, l => l.Contains("1 trim(s) above the 26-buffer ceiling in the second from callback 3"));
                Assert.Contains(lines, l => l.Contains("ran dry mid-stream at callback 14"));

                Assert.Equal(0, closing);
                Assert.Equal(0, starving);
            }
            finally { Tracing.On = saved.On; Tracing.TheSwitch = saved.Switch; }
        }


        private sealed class SlowListener : TraceListener
        {
            public int Calls;
            public int Thread;
            public override void Write(string message) { }
            public override void WriteLine(string message)
            {
                if (message == null || !message.Contains("timing-probe")) return;
                System.Threading.Thread.Sleep(150);
                Thread = Environment.CurrentManagedThreadId;
                Interlocked.Increment(ref Calls);
            }
        }

        [Fact]
        public void A_slow_trace_listener_never_holds_up_the_callback_and_the_lines_are_written_off_it()
        {
            var saved = TracingOn(true);
            var listener = new SlowListener();
            Trace.Listeners.Add(listener);
            try
            {
                var q = ReadyToCloseATrimSecond("timing-probe");
                Volatile.Write(ref listener.Calls, 0);          // the setup's drain wrote the first trim's line
                var sw = Stopwatch.StartNew();
                q.Begin(16, false);                            // closes the trim's second: until repair 1, 155 ms here
                q.NotePlayed(10, 0);
                sw.Stop();
                _out.WriteLine($"Begin that closed a second: {sw.Elapsed.TotalMilliseconds:F2} ms; listener calls during it: {listener.Calls}");
                Assert.Equal(0, Volatile.Read(ref listener.Calls));
                Assert.True(sw.ElapsedMilliseconds < 50, $"the callback took {sw.Elapsed.TotalMilliseconds:F1} ms");

                // Positive control: the off-audio writer (Track RT: a writer of
                // this test's own, on its own thread, not the pool) writes the
                // line, and it really goes through the slow listener, on another thread.
                Assert.True(q.LinesPending);
                using var writer = new OffAudioDrainer(10, "rxtiming writer");
                writer.Register(new QueueLines(q));
                var wait = Stopwatch.StartNew();
                while (Volatile.Read(ref listener.Calls) == 0 && wait.ElapsedMilliseconds < 3000) System.Threading.Thread.Sleep(10);
                Assert.Equal(1, Volatile.Read(ref listener.Calls));
                Assert.NotEqual(Environment.CurrentManagedThreadId, listener.Thread);
                Assert.False(q.LinesPending);
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Tracing.On = saved.On; Tracing.TheSwitch = saved.Switch;
            }
        }

        /// <summary>The playback queue's lines as an off-audio work, as the stream's own work drains them (Audio.StreamLines).</summary>
        private sealed class QueueLines : OffAudioWork
        {
            private readonly RxPlaybackQueue _q;
            public QueueLines(RxPlaybackQueue q) { _q = q; }
            public override bool Pending => base.Pending || _q.LinesPending;
            protected override void DrainCore() => _q.DrainLines();
        }

        [Fact]
        public void A_full_record_ring_drops_and_counts_and_the_summary_says_so()
        {
            var q = new RxPlaybackQueue("receive", BuffersPerCallback, BufferMs, Rate, RxPlaybackQueue.DefaultReserveMilliseconds);
            var lines = new List<string>();
            q.LineObserver = lines.Add;
            q.Begin(16, false); q.NotePlayed(10, 0);
            // Starve then refill, a second apart, many times, never draining.
            for (int i = 0; i < 200; i++)
            {
                q.Begin(6, false); q.NotePlayed(6, 4L * FramesPerBuffer);
                q.Begin(16, false); q.NotePlayed(10, 0);
                for (int k = 0; k < 9; k++) { q.Begin(16, false); q.NotePlayed(10, 0); }
            }
            q.TraceSummary();
            Assert.Contains(lines, l => l.Contains("timing line(s) were lost"));
            Assert.Contains(lines, l => l.Contains("queue summary"));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
