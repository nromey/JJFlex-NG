using System;
using System.Collections.Generic;
using System.Linq;
using JJFlexControl;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #520, ruled by Noel 2026-10-20 ("I'd do A"): the Flex Control ignores what it replays when
    /// the port opens, and after that every input code is acted on at once. These replay streams
    /// through <see cref="KnobInputFilter"/>, with the port opening at time zero.
    /// </summary>
    /// <remarks>
    /// <para><b>The streams are RECONSTRUCTED from the measurement in #520, not captured.</b> The
    /// probe printed with <c>Write-Host</c>, which cannot be redirected, so no recording exists.
    /// Each fixture says which sentence of the entry it is built from.</para>
    /// </remarks>
    public class FlexControlKnobFilterTests
    {
        private sealed class Run
        {
            public readonly List<string> Out = new List<string>();
            public readonly List<string> Notes = new List<string>();
            public KnobInputFilter Filter = null!;
        }

        private static Run Replay(IEnumerable<(string token, double at)> arrivals, double closeAt = 60_000)
        {
            var run = new Run();
            run.Filter = new KnobInputFilter(0, run.Notes.Add);
            foreach (var (token, at) in arrivals)
            {
                if (run.Filter.Accept(token, at)) run.Out.Add(token);
            }
            run.Filter.Close(closeAt);
            return run;
        }

        /// <summary>
        /// One controlled revolution, as measured: 84 rotation events from 17,457 ms to 19,901 ms
        /// (about 34 steps a second), gaps from the measured set, and four sub-millisecond doubles.
        /// </summary>
        private static List<(string, double)> MeasuredRevolution(string direction = "D", double start = 17_457)
        {
            double[] gaps = { 31.0, 15.6, 31.0, 47.0, 15.6, 31.0, 31.0, 15.6, 62.9, 15.6, 31.0, 15.6 };
            int[] doublesAfter = { 10, 30, 50, 70 };
            var events = new List<(string, double)>();
            double t = start;
            int g = 0;
            for (int i = 0; i < 84; i++)
            {
                events.Add((direction, t));
                if (i == 83) break;
                t += doublesAfter.Contains(i) ? 0.4 : gaps[g++ % gaps.Length];
            }
            return events;
        }

        /// <summary>
        /// The connect replay: "two or three [F0304] tokens in the first 25 ms of every run", and a
        /// burst of rotations and presses in the same first 25 ms matching what Noel had done to
        /// the knob while the port was closed (nine in the run whose arithmetic closed), at 9600
        /// baud: about 1 ms a byte, so "D;" every 2 ms and "X1S;" in 4.
        /// </summary>
        private static List<(string, double)> ConnectReplay()
        {
            var replay = new List<(string, double)> { ("F0304", 0.6) };
            double t = 6.0;
            string[] backlog = { "D", "D", "U", "D", "X1S", "D", "D", "U", "D" };
            for (int i = 0; i < backlog.Length; i++)
            {
                replay.Add((backlog[i], t));
                t += backlog[i].Length > 1 ? 4.2 : 2.1;
                if (i == 1) replay.Add(("F0304", 9.3));
                if (i == 6) replay.Add(("F0304", 21.5));
            }
            return replay.OrderBy(e => e.Item2).ToList();
        }

        [Fact]
        public void The_connect_replay_is_ignored_and_a_turn_made_after_it_passes_every_step()
        {
            Run run = Replay(ConnectReplay().Concat(MeasuredRevolution(start: 3_000)));

            Assert.Equal(9, run.Filter.ReplayEventsDiscarded);
            Assert.Equal(84, run.Out.Count(t => t == "D"));
            Assert.DoesNotContain("X1S", run.Out);
            Assert.DoesNotContain("U", run.Out);
            Assert.Contains(run.Notes, n => n.Contains("opening window ignored 9"));
        }

        [Fact]
        public void F0304_inside_the_opening_window_survives_every_time_it_is_sent()
        {
            Run run = Replay(ConnectReplay());
            Assert.Equal(new[] { "F0304", "F0304", "F0304" }, run.Out);
        }

        [Fact]
        public void Three_detents_in_one_read_after_the_window_all_arrive()
        {
            // Codex's case: one serial read carries three real detents, so all three share one
            // arrival time. The burst rule this replaced dropped all three.
            Run run = Replay(new[] { ("D", 1_000.0), ("D", 1_000.0), ("D", 1_000.0) });
            Assert.Equal(new[] { "D", "D", "D" }, run.Out);
        }

        [Fact]
        public void A_reader_stall_after_the_window_loses_nothing()
        {
            // Option A: nothing real is dropped once connected, a stalled read included.
            var stall = Enumerable.Range(0, 68).Select(_ => ("D", 5_000.0));
            Run run = Replay(stall);
            Assert.Equal(68, run.Out.Count);
        }

        [Fact]
        public void A_genuine_fast_turn_at_the_measured_rate_passes_every_step_in_order()
        {
            var turn = MeasuredRevolution();
            double rate = 83 / ((turn[83].Item2 - turn[0].Item2) / 1000.0);
            Assert.InRange(rate, 30, 38); // the fixture really is the measured ~34 steps a second

            Run run = Replay(turn);

            Assert.Equal(84, run.Out.Count);
            Assert.Equal(0, run.Filter.ReplayEventsDiscarded);
        }

        [Fact]
        public void A_backlog_larger_than_the_bench_saw_keeps_the_window_open_until_it_has_drained()
        {
            // 120 rotation codes draining at 9600 baud, about 2.08 ms apart, run past 25 ms.
            var drain = Enumerable.Range(0, 120).Select(i => ("D", 1.0 + i * 2.08)).ToList();
            double drained = drain[^1].Item2;
            Assert.True(drained > KnobInputFilter.OpeningMinimumMs && drained < KnobInputFilter.OpeningCapMs);

            Run run = Replay(drain.Concat(new[] { ("U", drained + 40), ("U", drained + 70) }));

            Assert.Equal(120, run.Filter.ReplayEventsDiscarded);
            Assert.Equal(new[] { "U", "U" }, run.Out);
        }

        [Fact]
        public void The_window_never_outlasts_its_cap()
        {
            // A stream with no quiet gap at all: everything from the cap on is acted on.
            var stream = Enumerable.Range(0, 400).Select(i => ("D", 1.0 + i * 2.0)).ToList();
            Run run = Replay(stream);

            int expectedKept = stream.Count(e => e.Item2 >= KnobInputFilter.OpeningCapMs);
            Assert.Equal(expectedKept, run.Out.Count);
            Assert.InRange(KnobInputFilter.OpeningCapMs, 331.0, 331.5); // 25 + 147 x 2 bytes x 10 bits / 9600
        }

        [Fact]
        public void Input_inside_the_first_25_ms_is_ignored_even_alone()
        {
            Run run = Replay(new[] { ("D", 20.0), ("U", 400.0) });
            Assert.Equal(new[] { "U" }, run.Out);
            Assert.Equal(1, run.Filter.ReplayEventsDiscarded);
        }

        [Fact]
        public void A_first_step_after_a_quiet_opening_is_acted_on()
        {
            Run run = Replay(new[] { ("F0304", 0.6), ("F0304", 3.0), ("D", 40.0) });
            Assert.Equal(new[] { "F0304", "F0304", "D" }, run.Out);
            Assert.Equal(0, run.Filter.ReplayEventsDiscarded);
        }

        [Fact]
        public void A_device_report_counts_as_activity_and_still_passes()
        {
            // The window ends at half a tick of silence of ANY kind, F0304 included.
            Run run = Replay(new[] { ("F0304", 20.0), ("D", 24.0), ("F0304", 28.0), ("D", 30.0), ("D", 60.0) });
            Assert.Equal(new[] { "F0304", "F0304", "D" }, run.Out);
            Assert.Equal(2, run.Filter.ReplayEventsDiscarded);
        }

        [Fact]
        public void A_press_is_never_dropped_after_the_window_and_a_step_beside_it_arrives()
        {
            // The press-nudge rule is not built: a real last step before a press arrives.
            Run run = Replay(new[] { ("D", 1_000.0), ("U", 1_029.0), ("X1S", 1_029.5), ("C", 1_031.0) });
            Assert.Equal(new[] { "D", "U", "X1S", "C" }, run.Out);
        }
    }
}
