using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            public void Callback(int arrivals)
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
            Assert.Contains("written at callback " + (lone + sim.Queue.WindowCallbacks), line.Line);
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
        public void The_receive_loop_says_the_feed_ended_before_it_stops_any_stream()
        {
            // The signal has to come first: the starvation happened 82 ms after
            // the loop ended and about 480 ms before the receive stream was
            // stopped, while the CW monitor's stream was being stopped.
            string text = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.RemoteAudio.cs"));
            int label = text.IndexOf("remoteDone:", StringComparison.Ordinal);
            Assert.True(label > 0);
            int noted = text.IndexOf("NoteFeedEnded()", label, StringComparison.Ordinal);
            int feed = text.IndexOf("AudioFeedEnded?.Invoke()", label, StringComparison.Ordinal);
            int monitor = text.IndexOf("CWMonDone()", label, StringComparison.Ordinal);
            int stop = text.IndexOf("stopOpusOutputChannel()", label, StringComparison.Ordinal);
            int summary = text.IndexOf("TraceOpusRxContinuitySummary()", label, StringComparison.Ordinal);
            Assert.True(noted > label && feed > label, "the feed-ended signal is not after remoteDone");
            Assert.True(noted < summary && noted < monitor && noted < stop, "the feed-ended signal comes after work that takes time");
            Assert.True(feed < stop, "the pipeline hears of the end after the stream is stopped");
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
