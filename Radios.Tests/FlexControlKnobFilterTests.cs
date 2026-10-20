using System;
using System.Collections.Generic;
using System.Linq;
using JJFlexControl;
using Xunit;
using static Radios.Tests.FlexControlKnobWire;

namespace Radios.Tests
{
    /// <summary>
    /// #520, ruled by Noel 2026-10-20 at 12:25 ("wait until the knob calms down"): at open the
    /// Flex Control's input codes are ignored until the knob has sent nothing at all for
    /// <see cref="KnobInputFilter.QuietGapMs"/>, any token restarting that wait; after that every
    /// input code is acted on at once. These replay streams through <see cref="KnobInputFilter"/>
    /// with explicit times and the port opening at time zero. No timers.
    /// </summary>
    /// <remarks>
    /// <para><b>The streams are RECONSTRUCTED from the measurement in #520, not captured.</b> The
    /// probe printed with <c>Write-Host</c>, which cannot be redirected, so no recording exists.
    /// Each fixture says which sentence of the entry it is built from.</para>
    /// <para><b>Every fixture is held to 9600 baud</b> by <see cref="Replay"/>, so none can carry
    /// codes faster than the port allows.</para>
    /// </remarks>
    public class FlexControlKnobFilterTests
    {
        private const double Gap = KnobInputFilter.QuietGapMs;

        private sealed class Run
        {
            public readonly List<string> Out = new List<string>();
            public readonly List<string> Notes = new List<string>();
            public KnobInputFilter Filter = null!;
        }

        private static Run Replay(IEnumerable<(string token, double at)> arrivals, double closeAt = 600_000)
        {
            var stream = arrivals.ToList();
            AssertNoFasterThanTheWire(stream);
            var run = new Run();
            run.Filter = new KnobInputFilter(0, run.Notes.Add);
            foreach (var (token, at) in stream)
            {
                if (run.Filter.Accept(token, at)) run.Out.Add(token);
            }
            run.Filter.Close(closeAt);
            return run;
        }

        private static double End(List<(string token, double at)> stream) => stream[^1].at;

        /// <summary>
        /// One controlled revolution, as measured: 84 rotation events over about 2.4 s (about 34
        /// steps a second), gaps from the measured set. The entry's four sub-millisecond doubles
        /// are modelled as two codes in one read: two separate arrivals 0.4 ms apart are faster
        /// than 9600 baud allows.
        /// </summary>
        private static List<(string token, double at)> MeasuredRevolution(string direction = "D", double start = 17_457)
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
                t += doublesAfter.Contains(i) ? 0 : gaps[g++ % gaps.Length];
            }
            return events;
        }

        /// <summary>
        /// The connect replay: "two or three [F0304] tokens in the first 25 ms of every run", and a
        /// burst of rotations and presses matching what Noel had done to the knob while the port
        /// was closed (nine in the run whose arithmetic closed), back to back at 9600 baud. At that
        /// rate it takes about 40 ms, not 25; see <see cref="FlexControlKnobWire"/>.
        /// </summary>
        private static List<(string token, double at)> ConnectReplay() =>
            BackToBack(0, new[] { "F0304", "D", "D", "F0304", "U", "D", "X1S", "D", "D", "F0304", "U", "D" });

        /// <summary>
        /// A saved pile delivered in batches, each back to back, with <paramref name="pause"/>
        /// between the last arrival of one batch and the first arrival of the next.
        /// </summary>
        private static List<(string token, double at)> Batches(int batches, int perBatch, double pause, string code = "D")
        {
            var stream = new List<(string, double)>();
            double t = 0;
            for (int b = 0; b < batches; b++)
            {
                if (b > 0) t += pause - MsFor(code);
                var batch = BackToBack(t, code, perBatch);
                stream.AddRange(batch);
                t = End(batch);
            }
            return stream;
        }

        // ---- The wait for quiet ----

        [Fact]
        public void A_replay_in_batches_with_pauses_shorter_than_the_quiet_gap_is_wholly_ignored()
        {
            // Noel: "The knob sends these queued actions in batches." Four batches of 42 detents,
            // two revolutions in all, each pause just short of the quiet gap. Make the rule end on
            // any gap shorter than the constant and this goes red.
            var pile = Batches(4, 42, Gap - 1);
            var gaps = pile.Zip(pile.Skip(1), (x, y) => y.at - x.at).ToList();
            Assert.InRange(gaps.Max(), Gap - 1 - 1e-6, Gap - 1 + 1e-6); // the fixture's pauses really are just short
            var turn = MeasuredRevolution("U", End(pile) + Gap);

            Run run = Replay(pile.Concat(turn));

            Assert.Equal(168, run.Filter.ReplayEventsDiscarded);
            Assert.Equal(84, run.Out.Count);
            Assert.All(run.Out, t => Assert.Equal("U", t));
        }

        [Fact]
        public void A_replay_delivered_in_callbacks_with_one_time_each_is_wholly_ignored()
        {
            // Round two's counterexample, held to the wire: the saved input arrives in two
            // callbacks, each stamping its whole chunk with one time, 10 ms apart.
            var stream = new List<(string, double)>();
            stream.AddRange(Enumerable.Repeat(("D", 20.0), 9));   // 18 bytes by 20 ms
            stream.Add(("F0304", 30.0));                           // 6 bytes, then 2
            stream.Add(("D", 30.0));
            stream.AddRange(Enumerable.Repeat(("U", 300.0), 3));

            Run run = Replay(stream);

            Assert.Equal(new[] { "F0304" }, run.Out);
            Assert.Equal(13, run.Filter.ReplayEventsDiscarded);
        }

        [Fact]
        public void An_F0304_between_batches_does_not_end_the_wait()
        {
            // The pause either side of the F0304 is under the gap, but the two pauses together are
            // well over it. Let the F0304 not restart the timer and the second batch reaches the radio.
            var first = BackToBack(0, "D", 20);
            double reportAt = End(first) + 0.6 * Gap;
            var second = BackToBack(reportAt + 0.6 * Gap, "D", 20);
            var stream = first.Append(("F0304", reportAt)).Concat(second).ToList();

            Run run = Replay(stream);

            Assert.Equal(new[] { "F0304" }, run.Out);
            Assert.Equal(40, run.Filter.ReplayEventsDiscarded);
        }

        [Fact]
        public void The_first_code_after_a_full_quiet_gap_acts_at_once()
        {
            var pile = ConnectReplay();
            double last = End(pile);

            Run onTime = Replay(pile.Append(("D", last + Gap)));
            Assert.Equal("D", onTime.Out[^1]);
            Assert.False(onTime.Filter.WaitingForQuiet);

            Run early = Replay(pile.Append(("D", last + Gap - 0.1)));
            Assert.DoesNotContain("D", early.Out);
        }

        [Fact]
        public void A_quiet_open_is_ready_a_gap_after_the_open_call()
        {
            Assert.Equal(new[] { "D" }, Replay(new[] { ("D", Gap) }).Out);
            Assert.Empty(Replay(new[] { ("D", Gap - 0.1) }).Out);
        }

        [Fact]
        public void Turning_as_the_knob_connects_is_ignored_until_the_turning_pauses()
        {
            // The trade Noel accepted: a turn already going at connect never leaves a full gap,
            // so all of it is ignored; the first step after a pause acts.
            var turn = MeasuredRevolution("D", 10);
            double resume = End(turn) + Gap;
            Run run = Replay(turn.Append(("U", resume)));

            Assert.Equal(new[] { "U" }, run.Out);
            Assert.Equal(84, run.Filter.ReplayEventsDiscarded);
        }

        [Fact]
        public void F0304_while_waiting_survives_every_time_it_is_sent()
        {
            Run run = Replay(ConnectReplay());
            Assert.Equal(new[] { "F0304", "F0304", "F0304" }, run.Out);
            Assert.Equal(9, run.Filter.ReplayEventsDiscarded);
        }

        // ---- After ready, nothing real is dropped ----

        [Fact]
        public void Nothing_real_is_dropped_once_the_knob_is_ready()
        {
            var pile = ConnectReplay();
            double ready = End(pile) + Gap;
            var stream = new List<(string token, double at)>(pile);
            stream.AddRange(Enumerable.Repeat(("D", ready), 3));              // three detents in one read
            stream.AddRange(Enumerable.Repeat(("D", ready + 5_000), 68));     // a reader stall's backlog
            stream.AddRange(MeasuredRevolution("U", ready + 10_000));         // the measured fast turn
            stream.Add(("D", ready + 20_000));                                // a step, then presses at once
            stream.Add(("X1S", ready + 20_029));
            stream.Add(("C", ready + 20_034));
            stream.Add(("F0304", ready + 20_041));                            // a mid-session report

            Run run = Replay(stream);

            Assert.Equal(3 + 68 + 1, run.Out.Count(t => t == "D"));
            Assert.Equal(84, run.Out.Count(t => t == "U"));
            Assert.Equal(new[] { "D", "X1S", "C", "F0304" }, run.Out.Skip(run.Out.Count - 4).ToArray());
            Assert.Equal(9, run.Filter.ReplayEventsDiscarded);
        }

        [Fact]
        public void A_genuine_fast_turn_at_the_measured_rate_passes_every_step_in_order()
        {
            var turn = MeasuredRevolution();
            double rate = 83 / ((End(turn) - turn[0].at) / 1000.0);
            Assert.InRange(rate, 30, 38); // the fixture really is the measured ~34 steps a second

            Run run = Replay(turn);

            Assert.Equal(84, run.Out.Count);
            Assert.Equal(0, run.Filter.ReplayEventsDiscarded);
        }

        // ---- The trace line ----

        [Fact]
        public void The_trace_says_what_was_ignored_the_gaps_it_saw_and_when_the_knob_was_ready()
        {
            var stream = new List<(string token, double at)> { ("F0304", 10.0), ("D", 20.0), ("D", 20.0), ("X1S", 150.0) };
            stream.Add(("U", 150.0 + Gap + 7));
            Run run = Replay(stream);

            string line = Assert.Single(run.Notes);
            Assert.Contains("knob ready 650.0 ms after the open call, once it had sent nothing for 500.0 ms", line);
            Assert.Contains("the next code came at 657.0 ms and was acted on", line);
            Assert.Contains("ignored 3 input codes (D 2, X1S 1) and passed 1 device reports", line);
            Assert.Contains("the first at 10.0 ms after the open call, then gaps of 10.0, 130.0 ms", line);
            Assert.Contains("not the times the bytes left the knob", line);
        }

        [Fact]
        public void A_port_closed_before_the_knob_went_quiet_says_so()
        {
            var run = new Run();
            run.Filter = new KnobInputFilter(0, run.Notes.Add);
            run.Filter.Accept("D", 3);
            run.Filter.Close(200);
            Assert.Contains("the port closed at 200.0 ms after the open call with the knob never quiet", Assert.Single(run.Notes));
        }

        [Fact]
        public void A_port_closed_after_a_silent_open_reports_ready_and_nothing_arrived()
        {
            Run run = Replay(Array.Empty<(string, double)>(), closeAt: 2_000);
            string line = Assert.Single(run.Notes);
            Assert.Contains("knob ready 500.0 ms after the open call", line);
            Assert.Contains("Nothing arrived while waiting", line);
        }

        // ---- The fixtures themselves ----

        [Fact]
        public void Every_fixture_is_no_faster_than_9600_baud_allows()
        {
            Assert.InRange(MsFor("D"), 2.083, 2.084);

            // Round three's fixture: 160 codes 2.05 ms apart. The port cannot carry it.
            var roundThree = Enumerable.Range(0, 160).Select(i => ("D", 1.0 + i * 2.05)).ToList();
            Assert.Throws<InvalidOperationException>(() => AssertNoFasterThanTheWire(roundThree));

            // Nine codes in one callback at 10 ms need 18.75 ms.
            Assert.Throws<InvalidOperationException>(() => AssertNoFasterThanTheWire(Enumerable.Repeat(("D", 10.0), 9)));

            AssertNoFasterThanTheWire(BackToBack(0, "D", 160));
            AssertNoFasterThanTheWire(ConnectReplay());
            AssertNoFasterThanTheWire(MeasuredRevolution());
            AssertNoFasterThanTheWire(Batches(4, 42, Gap - 1));
        }
    }
}
