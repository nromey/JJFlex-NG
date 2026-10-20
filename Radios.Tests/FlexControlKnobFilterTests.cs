using System;
using System.Collections.Generic;
using System.Linq;
using JJFlexControl;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #520: the Flex Control knob must not act on a burst nobody's hand made. Every stream here is replayed
    /// through <see cref="KnobInputFilter"/> exactly the way <c>Serial</c> drives it, including the
    /// release timer: <see cref="Replay"/> fires <see cref="KnobInputFilter.Release"/> at every
    /// <see cref="KnobInputFilter.NextDueMs"/> before the next arrival, so the release times the
    /// tests read are the ones the operator would get.
    /// </summary>
    /// <remarks>
    /// <para><b>The streams are RECONSTRUCTED from the measurement in #520, not captured.</b> The
    /// probe printed to the console with <c>Write-Host</c>, which cannot be redirected, so no raw
    /// recording exists. Each fixture says which sentence of the entry it is built from.</para>
    /// <para><b>What these tests cannot see:</b> the wiring in <c>Serial</c> (the port, the
    /// timestamp taken per read, the timer). That is a few lines and is read, not run, here;
    /// the bench half covers it.</para>
    /// </remarks>
    public class FlexControlKnobFilterTests
    {
        private sealed class Released
        {
            public string Token = "";
            public double At;
        }

        private sealed class Run
        {
            public readonly List<Released> Out = new List<Released>();
            public readonly List<string> Notes = new List<string>();
            public KnobInputFilter Filter = null!;
            public IEnumerable<string> Tokens => Out.Select(o => o.Token);
        }

        /// <summary>Feeds arrivals in order, firing the release timer the way Serial does.</summary>
        private static Run Replay(IEnumerable<(string token, double at)> arrivals)
        {
            var run = new Run();
            run.Filter = new KnobInputFilter(run.Notes.Add);
            double last = 0;
            foreach (var (token, at) in arrivals)
            {
                FireTimerUntil(run, at);
                foreach (string t in run.Filter.Accept(token, at)) run.Out.Add(new Released { Token = t, At = at });
                last = at;
            }
            FireTimerUntil(run, last + 10_000);
            Assert.Null(run.Filter.NextDueMs);
            return run;
        }

        private static void FireTimerUntil(Run run, double untilMs)
        {
            double? due;
            while ((due = run.Filter.NextDueMs) != null && due.Value <= untilMs)
            {
                foreach (string t in run.Filter.Release(due.Value)) run.Out.Add(new Released { Token = t, At = due.Value });
                // A deadline that Release does not meet when called at it would rearm Serial's
                // timer for the same instant forever; fail rather than hang the run.
                Assert.True(run.Filter.NextDueMs != due,
                    "Release at NextDueMs " + due.Value + " made no progress");
            }
        }

        private const double Tick = KnobInputFilter.TickMs;

        /// <summary>
        /// One controlled revolution, as measured: 84 rotation events from 17,457 ms to 19,901 ms
        /// (about 34 steps a second), every gap an integer multiple of the 15.6 ms clock from the
        /// measured set (15.6, 31.0, 47.0, 62.9, 93.6), and four sub-millisecond doubles.
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
        /// the knob while the port was closed (nine events in the run whose arithmetic closed).
        /// Spaced at what 9600 baud allows: about 1 ms a byte, so "D;" every 2 ms and "X1S;" in 4.
        /// </summary>
        private static List<(string, double)> ConnectBurst()
        {
            var burst = new List<(string, double)> { ("F0304", 0.6) };
            double t = 6.0;
            string[] backlog = { "D", "D", "U", "D", "X1S", "D", "D", "U", "D" };
            for (int i = 0; i < backlog.Length; i++)
            {
                burst.Add((backlog[i], t));
                t += backlog[i].Length > 1 ? 4.2 : 2.1;
                if (i == 1) burst.Add(("F0304", 9.3));
                if (i == 6) burst.Add(("F0304", 21.5));
            }
            return burst.OrderBy(e => e.Item2).ToList();
        }

        // ---- Ruling 1: discard any burst faster than a hand could produce ----

        [Fact]
        public void The_connect_replay_is_discarded_and_a_turn_made_after_it_passes_untouched()
        {
            var stream = ConnectBurst().Concat(MeasuredRevolution(start: 3_000)).ToList();

            Run run = Replay(stream);

            Assert.Equal(9, run.Filter.BurstEventsDiscarded);
            Assert.Equal(84, run.Out.Count(o => o.Token == "D"));
            Assert.DoesNotContain("X1S", run.Tokens);
            Assert.DoesNotContain("U", run.Tokens);
            Assert.Contains(run.Notes, n => n.Contains("burst"));
        }

        [Fact]
        public void A_genuine_fast_turn_at_the_measured_rate_passes_every_step_in_order()
        {
            var turn = MeasuredRevolution();
            double rate = 83 / ((turn[83].Item2 - turn[0].Item2) / 1000.0);
            Assert.InRange(rate, 30, 38); // the fixture really is the measured ~34 steps a second

            Run run = Replay(turn);

            Assert.Equal(84, run.Out.Count);
            Assert.All(run.Out, o => Assert.Equal("D", o.Token));
            Assert.Equal(0, run.Filter.BurstEventsDiscarded);
            // Each step is acted on half a tick after the last arrival of its tick (the smallest
            // hold the burst rule allows): within 7.8 ms, or 8.2 for the first of a 0.4 ms double.
            for (int i = 0; i < 84; i++)
            {
                Assert.InRange(run.Out[i].At - turn[i].Item2, 0, KnobInputFilter.GroupCloseMs + 0.4 + 0.001);
            }
        }

        [Fact]
        public void Two_events_in_one_tick_are_a_hand_and_three_are_not()
        {
            Run two = Replay(new[] { ("U", 100.0), ("U", 100.4) });
            Assert.Equal(new[] { "U", "U" }, two.Tokens);

            Run three = Replay(new[] { ("U", 100.0), ("U", 100.4), ("U", 101.0) });
            Assert.Empty(three.Out);
            Assert.Equal(3, three.Filter.BurstEventsDiscarded);
        }

        [Fact]
        public void A_double_followed_by_the_next_tick_is_still_a_hand()
        {
            // Jitter around one tick (measured 15.6 against 15.625) must not chain a double into
            // the next tick and make three.
            Run run = Replay(new[] { ("D", 100.0), ("D", 100.4), ("D", 100.4 + 15.3), ("D", 100.4 + 15.3 + 15.9) });
            Assert.Equal(4, run.Out.Count);
        }

        [Fact]
        public void A_reader_stall_lands_its_backlog_at_once_and_it_is_discarded()
        {
            // "The same burst arrives any time OUR READER STALLS": two seconds of turning at
            // 34 steps a second, all read in one go.
            var stall = Enumerable.Range(0, 68).Select(_ => ("D", 5_000.0));
            var after = MeasuredRevolution("U", 6_000);

            Run run = Replay(stall.Concat(after));

            Assert.Equal(68, run.Filter.BurstEventsDiscarded);
            Assert.DoesNotContain("D", run.Tokens);
            Assert.Equal(84, run.Out.Count(o => o.Token == "U"));
        }

        // ---- Ruling 2: never discard F0304 with the burst ----

        [Fact]
        public void F0304_survives_the_connect_burst_every_time_it_is_sent()
        {
            Run run = Replay(ConnectBurst());

            Assert.Equal(3, run.Out.Count(o => o.Token == "F0304"));
            Assert.Equal(new[] { "F0304", "F0304", "F0304" }, run.Tokens);
        }

        [Fact]
        public void F0304_is_passed_on_the_moment_it_arrives_and_does_not_count_toward_a_burst()
        {
            // "It also appeared ONCE mid-session ... immediately before a turn."
            var stream = new List<(string, double)> { ("F0304", 17_456.0) };
            stream.AddRange(MeasuredRevolution());

            Run run = Replay(stream);

            Assert.Equal("F0304", run.Out[0].Token);
            Assert.Equal(17_456.0, run.Out[0].At);
            Assert.Equal(84, run.Out.Count(o => o.Token == "D"));
        }

        [Fact]
        public void Three_F0304_reports_in_a_row_are_not_themselves_a_burst()
        {
            Run run = Replay(new[] { ("F0304", 0.6), ("F0304", 2.0), ("F0304", 3.4), ("D", 5.0) });
            Assert.Equal(new[] { "F0304", "F0304", "F0304", "D" }, run.Tokens);
        }

        // ---- Repair round one: the press guard is out, so nothing near a press is dropped ----

        [Fact]
        public void A_real_final_detent_followed_at_once_by_a_press_is_acted_on()
        {
            // The press-nudge guard was taken out because it could not tell this from a phantom:
            // a turn's last step, then a button press about one detent-interval later.
            Run run = Replay(new[] { ("D", 1_000.0), ("U", 1_029.0), ("X1S", 1_050.0) });

            Assert.Equal(new[] { "D", "U", "X1S" }, run.Tokens);
        }

        [Fact]
        public void A_rotation_and_a_press_in_the_same_tick_both_arrive()
        {
            Run run = Replay(new[] { ("D", 1_000.0), ("C", 1_000.5) });
            Assert.Equal(new[] { "D", "C" }, run.Tokens);
        }

        [Fact]
        public void Two_buttons_pressed_together_both_arrive()
        {
            // "Left and right pressed together ... X1S then X3S every time", 31.5 ms apart.
            Run run = Replay(new[] { ("X1S", 1_000.0), ("X3S", 1_031.5) });
            Assert.Equal(new[] { "X1S", "X3S" }, run.Tokens);
        }

        [Fact]
        public void A_press_is_acted_on_within_half_a_tick()
        {
            Run run = Replay(new[] { ("X3S", 1_000.0) });
            Assert.Single(run.Out);
            Assert.InRange(run.Out[0].At - 1_000.0, 0, KnobInputFilter.GroupCloseMs + 0.001);
        }

        // ---- Repair round one: close loses no real step ----

        [Fact]
        public void Flush_acts_on_a_real_step_still_held_and_still_discards_a_burst()
        {
            var one = new KnobInputFilter();
            Assert.Empty(one.Accept("U", 1_000.0));
            Assert.Equal(new[] { "U" }, one.Flush());
            Assert.Null(one.NextDueMs);

            var burst = new KnobInputFilter();
            foreach (string t in new[] { "D", "D", "U", "D" }) Assert.Empty(burst.Accept(t, 1_000.0));
            Assert.Empty(burst.Flush());
            Assert.Equal(4, burst.BurstEventsDiscarded);
        }

        // ---- Repair round one: the limit of the rule, stated rather than hidden ----

        [Fact]
        public void A_replay_of_one_or_two_steps_is_not_caught_and_this_is_an_open_question()
        {
            // Not a ruling: the measured ceiling of a hand is two per tick, so one or two buffered
            // steps replayed at open cannot be told from a live turn. Noel has the question
            // (2026-10-20 report, repair round one). This pins the limit so nobody claims more.
            Run run = Replay(new[] { ("F0304", 0.6), ("D", 6.0), ("D", 8.1) });
            Assert.Equal(new[] { "F0304", "D", "D" }, run.Tokens);
        }

        [Fact]
        public void An_arrival_stamped_before_the_timer_already_decided_that_instant_starts_a_new_tick()
        {
            // Serial's reader and its release timer are two threads. A read stamped just before
            // the timer ran must not join a tick whose group the timer already closed.
            var filter = new KnobInputFilter();
            var outTokens = new List<string>();
            outTokens.AddRange(filter.Accept("D", 1_000.0));
            outTokens.AddRange(filter.Accept("D", 1_000.1));
            outTokens.AddRange(filter.Release(1_008.0));
            outTokens.AddRange(filter.Accept("D", 1_000.3));
            outTokens.AddRange(filter.Release(2_000.0));

            Assert.Equal(new[] { "D", "D", "D" }, outTokens);
            Assert.Equal(0, filter.BurstEventsDiscarded);
        }

        [Fact]
        public void Ticks_are_counted_to_the_nearest_whole_tick()
        {
            Assert.Equal(0, KnobInputFilter.TicksBetween(0, 7.8));
            Assert.Equal(1, KnobInputFilter.TicksBetween(0, 15.6));
            Assert.Equal(2, KnobInputFilter.TicksBetween(0, 31.6));
            Assert.Equal(2, KnobInputFilter.TicksBetween(0, 39.0));
            Assert.Equal(3, KnobInputFilter.TicksBetween(0, 47.0));
            Assert.Equal(2, KnobInputFilter.TicksBetween(31.6, 0));
        }
    }
}
