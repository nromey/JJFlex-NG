using System;
using System.Collections.Generic;
using System.Linq;
using JJFlexControl;
using Xunit;
using static Radios.Tests.FlexControlKnobWire;

namespace Radios.Tests
{
    /// <summary>
    /// #520, ruled by Noel 2026-10-20 at 12:25 ("wait until the knob calms down"), with the
    /// buffer check he approved at 12:52: at open the Flex Control's input codes are ignored until
    /// a check of the port's buffer finds it empty a full <see cref="KnobInputFilter.QuietGapMs"/>
    /// after the last read; after that every input code is acted on at once. These drive
    /// <see cref="KnobInputFilter"/> with explicit times and the port opening at time zero. No timers.
    /// </summary>
    /// <remarks>
    /// <para><b>The model.</b> A fixture says when each code's bytes LAND in the port's buffer
    /// (held to 9600 baud), and when the reader gets round to reading them, which can be much
    /// later. A buffer check runs every <see cref="KnobInputFilter.QuietCheckMs"/> and sees how
    /// many landed bytes are still unread. That is the distinction Codex's round-four review said
    /// the filter could not draw: text waiting to be read is not a quiet knob.</para>
    /// <para><b>The streams are RECONSTRUCTED from the measurement in #520, not captured.</b> The
    /// probe printed with <c>Write-Host</c>, which cannot be redirected, so no recording exists.
    /// Each fixture says which sentence of the entry it is built from.</para>
    /// </remarks>
    public class FlexControlKnobFilterTests
    {
        private const double Gap = KnobInputFilter.QuietGapMs;
        private const double Every = KnobInputFilter.QuietCheckMs;

        private sealed class Run
        {
            public readonly List<string> Out = new List<string>();
            public readonly List<string> Notes = new List<string>();
            public KnobInputFilter Filter = null!;
        }

        /// <summary>
        /// Land each code in the buffer at its time, read it at <paramref name="readAt"/> of that
        /// time (at once by default), and check the buffer every <see cref="Every"/> ms, until well
        /// after the last event. Also check just before every landing: the real timer can fire at
        /// any moment, and the moment just before more bytes land is the worst one, so the gap's
        /// boundary is tested exactly. Ties: that check, bytes land, then a read, then a timed check.
        /// </summary>
        private static Run Simulate(IEnumerable<(string token, double at)> landings, Func<double, double>? readAt = null, double? closeAt = null)
        {
            var landed = landings.ToList();
            AssertNoFasterThanTheWire(landed);
            readAt ??= t => t;
            var reads = landed.Select(l => readAt(l.at)).ToList();
            Assert.All(landed.Zip(reads), p => Assert.True(p.Second >= p.First.at, "a read before its bytes landed"));

            double end = closeAt ?? (landed.Count == 0 ? 0 : Math.Max(landed.Max(l => l.at), reads.Max())) + 2 * Gap + Every;
            var events = new List<(double at, int order, int index)>();
            for (int i = 0; i < landed.Count; i++)
            {
                events.Add((landed[i].at, -1, -1));
                events.Add((landed[i].at, 0, i));
                events.Add((reads[i], 1, i));
            }
            for (double c = Every; c <= end; c += Every) events.Add((c, 2, -1));
            events = events.OrderBy(e => e.at).ThenBy(e => e.order).ThenBy(e => e.index).ToList();

            var run = new Run();
            run.Filter = new KnobInputFilter(0, run.Notes.Add);
            int unread = 0;
            foreach (var (at, order, index) in events)
            {
                if (order == 0) unread += landed[index].token.Length + 1;
                else if (order == 1)
                {
                    unread -= landed[index].token.Length + 1;
                    if (run.Filter.Accept(landed[index].token, at)) run.Out.Add(landed[index].token);
                }
                else run.Filter.CheckQuiet(at, unread);
            }
            run.Filter.Close(end);
            return run;
        }

        private static double End(List<(string token, double at)> stream) => stream[^1].at;

        /// <summary>
        /// One controlled revolution, as measured: 84 rotation events over about 2.4 s (about 34
        /// steps a second), gaps from the measured set. The entry's four sub-millisecond doubles
        /// are modelled as two codes landing together: two arrivals 0.4 ms apart are faster than
        /// 9600 baud allows.
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
        /// A saved pile in batches, each back to back, with <paramref name="pause"/> between the
        /// last landing of one batch and the first landing of the next.
        /// </summary>
        private static List<(string token, double at)> Batches(int batches, int perBatch, double pause, string code = "D", double start = 0)
        {
            var stream = new List<(string, double)>();
            double t = start;
            for (int b = 0; b < batches; b++)
            {
                if (b > 0) t += pause - MsFor(code);
                var batch = BackToBack(t, code, perBatch);
                stream.AddRange(batch);
                t = End(batch);
            }
            return stream;
        }

        /// <summary>Comfortably after any wait: a full gap after <paramref name="lastRead"/> plus a check.</summary>
        private static double After(double lastRead) => lastRead + Gap + 2 * Every;

        // ---- Codex's round-four sequences: text waiting to be read is not quiet ----

        [Fact]
        public void A_delayed_first_callback_cannot_pass_the_saved_pile_off_as_live()
        {
            // The whole replay lands in the first 40 ms, but Windows does not deliver it until
            // 990 ms, nearly a second later, so no read happens for far longer than the gap. The
            // buffer checks see it waiting. Drop the buffer check and this goes red.
            var pile = ConnectReplay();
            var turn = MeasuredRevolution("U", 3_000);

            Run run = Simulate(pile.Concat(turn), readAt: t => t < 1_000 ? 990 : t);

            Assert.Equal(9, run.Filter.ReplayEventsDiscarded);
            Assert.Equal(new[] { "F0304", "F0304", "F0304" }, run.Out.Take(3).ToArray());
            Assert.Equal(84, run.Out.Count(t => t == "U"));
            Assert.DoesNotContain("D", run.Out);
            Assert.DoesNotContain("X1S", run.Out);
            Assert.Contains(run.Notes, n => n.StartsWith("knob ready at 1500.0 ms after the open call"));
        }

        [Fact]
        public void A_later_saved_batch_held_up_by_a_stalled_reader_cannot_pass_as_live()
        {
            // One batch is read at once; the second lands at 300 ms while the reader is stalled
            // and is not read until 920 ms. Without the buffer check, a gap of over 500 ms since
            // the first batch's last read would end the wait before the second batch is read.
            var first = BackToBack(0, "D", 42);
            var second = BackToBack(300, "U", 42);
            var turn = MeasuredRevolution("D", 3_000);

            Run run = Simulate(first.Concat(second).Concat(turn), readAt: t => t >= 300 && t < 1_000 ? 920 : t);

            Assert.Equal(84, run.Filter.ReplayEventsDiscarded);
            Assert.Equal(84, run.Out.Count);
            Assert.All(run.Out, t => Assert.Equal("D", t));
        }

        [Fact]
        public void A_buffer_holding_bytes_is_never_quiet_however_long_since_the_last_read()
        {
            var run = new Run();
            run.Filter = new KnobInputFilter(0, run.Notes.Add);
            run.Filter.Accept("D", 10);
            run.Filter.CheckQuiet(60_000, 2);
            Assert.True(run.Filter.WaitingForQuiet);
            run.Filter.CheckQuiet(60_050, 0);
            Assert.False(run.Filter.WaitingForQuiet);
        }

        [Fact]
        public void A_read_never_ends_the_wait_by_itself()
        {
            // Only a buffer check can find the knob quiet. Round four let a read a full gap after
            // the last one end the wait, which is exactly how a late callback passed as live.
            var filter = new KnobInputFilter(0);
            Assert.False(filter.Accept("D", 10));
            Assert.False(filter.Accept("D", 60_000));
            Assert.True(filter.WaitingForQuiet);
        }

        // ---- The wait for quiet ----

        [Fact]
        public void A_genuinely_quiet_knob_becomes_ready_and_acts_on_the_next_step()
        {
            Run run = Simulate(new[] { ("D", 600.0) });
            Assert.Equal(new[] { "D" }, run.Out);
            Assert.Contains(run.Notes, n => n.StartsWith("knob ready at 500.0 ms after the open call"));

            Assert.Empty(Simulate(new[] { ("D", 490.0) }).Out); // before any check has found it quiet
        }

        [Fact]
        public void A_replay_in_batches_with_pauses_shorter_than_the_quiet_gap_is_wholly_ignored()
        {
            // Noel: "The knob sends these queued actions in batches." Four batches of 42 detents,
            // two revolutions in all, each pause 1 ms short of the gap, read as they land. Make
            // the rule end on any gap shorter than the constant and this goes red.
            var pile = Batches(4, 42, Gap - 1);
            var gaps = pile.Zip(pile.Skip(1), (x, y) => y.at - x.at).ToList();
            Assert.InRange(gaps.Max(), Gap - 1 - 1e-6, Gap - 1 + 1e-6); // the fixture's pauses really are just short
            var turn = MeasuredRevolution("U", After(End(pile)));

            Run run = Simulate(pile.Concat(turn));

            Assert.Equal(168, run.Filter.ReplayEventsDiscarded);
            Assert.Equal(84, run.Out.Count);
            Assert.All(run.Out, t => Assert.Equal("U", t));
        }

        [Fact]
        public void An_F0304_between_batches_does_not_end_the_wait()
        {
            // The pause either side of the F0304 is under the gap, but the two together are well
            // over it. Let the F0304 not restart the wait and the second batch reaches the radio.
            var first = BackToBack(0, "D", 20);
            double reportAt = End(first) + 0.6 * Gap;
            var second = BackToBack(reportAt + 0.6 * Gap, "D", 20);

            Run run = Simulate(first.Append(("F0304", reportAt)).Concat(second));

            Assert.Equal(new[] { "F0304" }, run.Out);
            Assert.Equal(40, run.Filter.ReplayEventsDiscarded);
        }

        [Fact]
        public void The_first_code_after_the_knob_is_found_quiet_acts_at_once()
        {
            // The simulator checks the buffer just before each landing, so this is the boundary
            // exactly: a full gap after the last read, found empty, and the code acts.
            var pile = ConnectReplay();
            double last = End(pile);

            Run onTime = Simulate(pile.Append(("D", last + Gap)));
            Assert.Equal("D", onTime.Out[^1]);

            // A tenth of a millisecond short, the check does not find it quiet, and the code
            // restarts the wait instead.
            Run early = Simulate(pile.Append(("D", last + Gap - 0.1)));
            Assert.DoesNotContain("D", early.Out);
        }

        [Fact]
        public void Turning_as_the_knob_connects_is_ignored_until_the_turning_pauses()
        {
            // The trade Noel accepted: a turn already going at connect never leaves a full gap,
            // so all of it is ignored; the first step after a pause acts.
            var turn = MeasuredRevolution("D", 10);
            Run run = Simulate(turn.Append(("U", After(End(turn)))));

            Assert.Equal(new[] { "U" }, run.Out);
            Assert.Equal(84, run.Filter.ReplayEventsDiscarded);
        }

        [Fact]
        public void F0304_while_waiting_survives_every_time_it_is_sent()
        {
            Run run = Simulate(ConnectReplay());
            Assert.Equal(new[] { "F0304", "F0304", "F0304" }, run.Out);
            Assert.Equal(9, run.Filter.ReplayEventsDiscarded);
        }

        // ---- After ready, nothing real is dropped ----

        [Fact]
        public void Nothing_real_is_dropped_once_the_knob_is_ready()
        {
            var pile = ConnectReplay();
            double ready = After(End(pile));
            var stream = new List<(string token, double at)>(pile);
            stream.AddRange(BackToBack(ready, "D", 3));                       // three detents...
            var stall = BackToBack(ready + 5_000, "D", 68);                   // a reader stall's backlog...
            stream.AddRange(stall);
            stream.AddRange(MeasuredRevolution("U", ready + 10_000));         // the measured fast turn
            stream.Add(("D", ready + 20_000));                                // a step, then presses at once
            stream.Add(("X1S", ready + 20_029));
            stream.Add(("C", ready + 20_034));
            stream.Add(("F0304", ready + 20_041));                            // a mid-session report
            double threeAt = ready + 10, stallAt = End(stall) + 300;

            // ...read in one go, and after a long stall.
            Run run = Simulate(stream, readAt: t => t >= ready && t < ready + 10 ? threeAt : t >= ready + 5_000 && t < ready + 6_000 ? stallAt : t);

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

            Run run = Simulate(turn);

            Assert.Equal(84, run.Out.Count);
            Assert.Equal(0, run.Filter.ReplayEventsDiscarded);
        }

        // ---- The trace line says only what was observed ----

        [Fact]
        public void The_trace_says_what_was_ignored_the_gaps_it_saw_and_when_the_knob_was_found_quiet()
        {
            var filter = new List<string>();
            var f = new KnobInputFilter(0, filter.Add);
            f.Accept("F0304", 10);
            f.Accept("D", 20);
            f.Accept("D", 20);
            f.Accept("X1S", 150);
            f.CheckQuiet(600, 4);
            f.CheckQuiet(650, 0);
            f.Accept("U", 657);

            string line = Assert.Single(filter);
            Assert.Equal(
                "knob ready at 650.0 ms after the open call: no text had been read from the port for 500.0 ms, and the port's buffer was empty when checked."
                + " While waiting it ignored 3 input codes (D 2, X1S 1) and passed 1 device reports, and 1 buffer checks found bytes waiting unread."
                + " Reads: the first at 10.0 ms after the open call, then gaps of 10.0, 130.0 ms."
                + " These are the times the text was read from the port, one read often carrying many codes, not the times the bytes left the knob.",
                line);
            Assert.DoesNotContain("sent nothing", line);
        }

        [Fact]
        public void A_port_closed_before_the_knob_was_found_quiet_says_so()
        {
            var notes = new List<string>();
            var f = new KnobInputFilter(0, notes.Add);
            f.Accept("D", 3);
            f.Close(200);
            Assert.StartsWith("the port closed at 200.0 ms after the open call before a buffer check had found the knob quiet", Assert.Single(notes));
        }

        [Fact]
        public void A_silent_open_reports_ready_and_nothing_read()
        {
            Run run = Simulate(Array.Empty<(string, double)>(), closeAt: 2_000);
            string line = Assert.Single(run.Notes);
            Assert.StartsWith("knob ready at 500.0 ms after the open call", line);
            Assert.Contains("Nothing was read while waiting", line);
        }

        // ---- The fixtures themselves ----

        [Fact]
        public void Every_fixture_is_no_faster_than_9600_baud_allows()
        {
            Assert.InRange(MsFor("D"), 2.083, 2.084);

            // Round three's fixture: 160 codes 2.05 ms apart. The port cannot carry it.
            var roundThree = Enumerable.Range(0, 160).Select(i => ("D", 1.0 + i * 2.05)).ToList();
            Assert.Throws<InvalidOperationException>(() => AssertNoFasterThanTheWire(roundThree));

            // Nine codes landing together at 10 ms need 18.75 ms.
            Assert.Throws<InvalidOperationException>(() => AssertNoFasterThanTheWire(Enumerable.Repeat(("D", 10.0), 9)));

            AssertNoFasterThanTheWire(BackToBack(0, "D", 160));
            AssertNoFasterThanTheWire(ConnectReplay());
            AssertNoFasterThanTheWire(MeasuredRevolution());
            AssertNoFasterThanTheWire(Batches(4, 42, Gap - 1));
        }
    }
}
