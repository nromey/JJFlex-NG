#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// <see cref="SpokenSeries"/> over the REAL <see cref="PacedSpeechDelivery"/>
    /// and the REAL <see cref="SpeechArbiter"/>, with
    /// <see cref="FakeCompletionChannel"/> standing in for the reader: no
    /// reader, no sound, no window. Codex's re-check of 03280742 (Sprint 49
    /// Track L2, repair 2, #866), whose six probes are adopted here with their
    /// names kept recognisable, and the controls repair 2 adds: after a cut
    /// from outside, the app's next interrupt lets the series carry on, and the
    /// series' own timer watches the final batch without being pumped by hand.
    /// </summary>
    public sealed class SpokenSeriesRealPumpTests
    {
        private sealed class Rig : IDisposable
        {
            public readonly FakeSpeechClock Clock = new();
            public readonly FakeCompletionChannel Channel = new();
            public readonly PacedSpeechDelivery Pump;
            public readonly SpeechArbiter Arbiter;
            public readonly SpokenSeries Series;
            public readonly ConcurrentDictionary<long, SpeechOutcome> Outcomes = new();

            public Rig(int pollMs = 0)
            {
                SpeechArbiter? a = null;
                Pump = new PacedSpeechDelivery((_, _) => SpeechDelivery.Accepted,
                    (ticket, text, outcome) => { a!.OnOutcome(ticket, text, outcome); Outcomes[ticket] = outcome; },
                    _ => 30000);
                Arbiter = a = new SpeechArbiter(Clock, () => VerbosityLevel.Chatty,
                    (text, interrupt, intent, level, origin, salvaged) => SpeechHandoff.TrackedAs(
                        interrupt ? Pump.Interrupt(Channel, text) : Pump.Enqueue(Channel, text)),
                    () => Pump.Discard("silence"), (_, _, _, _) => { }, new SpeechRateModel(), () => null,
                    (ticket, reason) => Pump.WithdrawIfQueued(ticket, reason));
                Series = new SpokenSeries(Arbiter, "diagnostic", "real pump test", pollMs: pollMs,
                    nowMs: () => (long)Clock.ElapsedMs);
            }

            /// <summary>Something outside the app cuts the call after its first word; wait for the pump's report.</summary>
            public void CancelFirstPartway(FakeCompletionChannel.Call call)
            {
                Channel.Mark(call);
                Channel.CancelFromOutside(call, marksReached: 1);
                Assert.True(SpinWait.SpinUntil(() => Outcomes.ContainsKey(call.Ticket), 5000));
            }

            public void Complete(FakeCompletionChannel.Call call)
            {
                Channel.Complete(call);
                Assert.True(SpinWait.SpinUntil(() => Outcomes.ContainsKey(call.Ticket), 5000));
            }

            /// <summary>Another part of the app interrupts, and the reader says it to the end.</summary>
            public void OurInterrupt(string text)
            {
                Arbiter.Emit(text, true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "real pump test", "other");
                var call = Channel.WaitForCall();
                Assert.Equal(text, call.Text);
                Complete(call);
            }

            /// <summary>No new call reaches the reader within a few hundred milliseconds.</summary>
            public bool NothingMoreSaid(int seen) => !SpinWait.SpinUntil(() => Channel.Seen.Count > seen, 300);

            public void Dispose()
            {
                Series.Withdraw("test done");
                Arbiter.DiscardAll();
                Pump.Dispose();
                Channel.ReleaseAll();
            }
        }

        /// <summary>
        /// Codex's C1 reproduction: the series' first line is handed over alone,
        /// two more wait above the pump, and something outside the app cuts the
        /// first part-way. Before repair 2 the series took the cut as done and
        /// handed the next line over at once, re-starting speech the reader had
        /// just been silenced from.
        /// </summary>
        [Fact]
        public void Codex_C1_a_cut_from_outside_part_way_through_a_lone_line_does_not_restart_the_list()
        {
            using var r = new Rig();
            r.Series.Add("first result with several words", VerbosityLevel.Terse);
            var first = r.Channel.WaitForCall();
            r.Series.Add("second result should wait", VerbosityLevel.Terse);
            r.Series.Add("finished should wait", VerbosityLevel.Terse);
            Assert.Equal(0, r.Pump.QueuedCount);

            r.CancelFirstPartway(first);
            r.Series.Pump();

            Assert.True(r.NothingMoreSaid(1),
                "The next result started without a later app interrupt after the foreign partial cut.");
            Assert.True(r.Series.Owes);
        }

        /// <summary>
        /// Codex's passing control for C1: the same cut on the first line of a
        /// four-line batch, its three queued companions given back unspoken. The
        /// series waits; since repair 2 it waits by the same rule as the lone line.
        /// </summary>
        [Fact]
        public void Codex_C1_control_a_cut_from_outside_inside_a_batch_waits()
        {
            using var r = new Rig();
            r.Series.Add("seed said before the batch", VerbosityLevel.Terse);
            var seed = r.Channel.WaitForCall();
            r.Series.Add("first result in the next batch", VerbosityLevel.Terse);
            r.Series.Add("second result in the next batch", VerbosityLevel.Terse);
            r.Series.Add("third result in the next batch", VerbosityLevel.Terse);
            r.Series.Add("fourth result in the next batch", VerbosityLevel.Terse);
            r.Series.Add("later result should wait", VerbosityLevel.Terse);
            r.Complete(seed);
            r.Series.Pump();
            var first = r.Channel.WaitForCall();
            Assert.Equal(3, r.Pump.QueuedCount);

            r.CancelFirstPartway(first);
            r.Series.Pump();

            Assert.True(r.NothingMoreSaid(2));
            Assert.True(r.Series.Owes);
        }

        /// <summary>
        /// Repair 2's recovery for C1, the lone line: the app's next interrupt is
        /// what lets the series carry on, behind it. The line cut part-way is not
        /// said again (the arbiter's rule for a cut nobody can name); the next
        /// one is.
        /// </summary>
        [Fact]
        public void After_a_cut_from_outside_of_a_lone_line_our_next_interrupt_lets_the_series_carry_on()
        {
            using var r = new Rig();
            r.Series.Add("first result with several words", VerbosityLevel.Terse);
            var first = r.Channel.WaitForCall();
            r.Series.Add("second result", VerbosityLevel.Terse);
            r.Series.Add("finished", VerbosityLevel.Terse);
            r.CancelFirstPartway(first);
            r.Series.Pump();
            Assert.True(r.NothingMoreSaid(1));

            r.OurInterrupt("a key of ours");
            // Track SL: the series waits out the interrupt's settle window
            // itself, as the arbiter's hold does, so the key's own follow-ups
            // go first; its timer does this in the app.
            r.Series.Pump();
            Assert.True(r.NothingMoreSaid(2));
            r.Clock.Advance(SpeechArbiter.SalvageSettleMs + 100);
            r.Series.Pump();

            var next = r.Channel.WaitForCall();
            Assert.Equal("second result", next.Text);
            r.Complete(next);
            var last = r.Channel.WaitForCall();
            Assert.Equal("finished", last.Text);
            r.Complete(last);
            r.Series.Pump();
            Assert.False(r.Series.Owes);
            Assert.Equal(new[] { "first result with several words", "a key of ours", "second result", "finished" },
                r.Channel.Seen.ConvertAll(s => s.Text));
        }

        /// <summary>
        /// The same recovery inside a batch: the interrupt puts back the lines
        /// the pump gave back unspoken, in order, and only then does the series
        /// hand over its next line.
        /// </summary>
        [Fact]
        public void After_a_cut_from_outside_inside_a_batch_our_next_interrupt_puts_the_rest_back_then_the_series_carries_on()
        {
            using var r = new Rig();
            r.Series.Add("seed", VerbosityLevel.Terse);
            var seed = r.Channel.WaitForCall();
            foreach (var l in new[] { "one", "two", "three", "four", "later" }) r.Series.Add(l, VerbosityLevel.Terse);
            r.Complete(seed);
            r.Series.Pump();
            var one = r.Channel.WaitForCall();
            r.CancelFirstPartway(one);
            r.Series.Pump();
            Assert.True(r.NothingMoreSaid(2));

            r.OurInterrupt("a key of ours");
            r.Series.Pump();
            Assert.True(r.NothingMoreSaid(3));                      // the lines taken back wait out the settle
            r.Clock.Advance(SpeechArbiter.SalvageSettleMs + 100);
            // Track SL: the lines given back are the series' own now, taken
            // back at the cut, and the series hands them over again itself
            // after the settle (its timer does this in the app); before, the
            // arbiter's hold put them back.
            r.Series.Pump();
            foreach (var expected in new[] { "two", "three", "four" })
            {
                var call = r.Channel.WaitForCall();
                Assert.Equal(expected, call.Text);
                r.Complete(call);
                r.Series.Pump();
            }
            var later = r.Channel.WaitForCall();
            Assert.Equal("later", later.Text);
            r.Complete(later);
            r.Series.Pump();
            Assert.False(r.Series.Owes);
        }

        /// <summary>Codex's first C2 case: one final tracked line that never answers.</summary>
        [Fact]
        public void Codex_C2_a_final_line_that_never_answers_ends_after_the_stall_bound()
        {
            using var r = new Rig();
            r.Series.Add("a final line that never answers", VerbosityLevel.Terse);
            r.Channel.WaitForCall();
            r.Clock.Advance(SpokenSeries.StallMs + 1);
            r.Series.Pump();
            Assert.False(r.Series.Owes);
        }

        /// <summary>
        /// Codex's second C2 case: a final three-line batch whose first line is
        /// cut part-way from outside, its two queued companions given back
        /// unspoken by the real pump, with no pump deadline left to retire them.
        /// <para>REWRITTEN by Track SL (ruled S4, 2026-11-23). This was
        /// <c>Codex_C2_a_final_batch_cut_from_outside_ends_after_the_stall_bound</c>
        /// and asserted that the series ended at the one-minute bound. After
        /// NVDA's Ctrl there is no time limit now: the series is still waiting
        /// at 70 s and at ten minutes, says nothing into the silence, and our
        /// next key brings every remaining line once, in order. A reader that
        /// stops answering with no cut still ends at the bound:
        /// <see cref="Codex_C2_a_final_line_that_never_answers_ends_after_the_stall_bound"/>.</para>
        /// </summary>
        [Fact]
        public void Codex_C2_a_final_batch_cut_from_outside_waits_past_the_stall_bound_for_our_next_key()
        {
            using var r = new Rig();
            r.Series.Add("seed completes before final batch", VerbosityLevel.Terse);
            var seed = r.Channel.WaitForCall();
            r.Series.Add("final batch first result", VerbosityLevel.Terse);
            r.Series.Add("final batch second result", VerbosityLevel.Terse);
            r.Series.Add("finished final batch last", VerbosityLevel.Terse);
            r.Complete(seed);
            r.Series.Pump();
            var first = r.Channel.WaitForCall();
            Assert.Equal(2, r.Pump.QueuedCount);
            r.CancelFirstPartway(first);
            r.Series.Pump();
            Assert.True(r.Series.Owes);
            r.Clock.Advance(70_000);
            r.Series.Pump();
            Assert.True(r.Series.Owes);
            r.Clock.Advance(600_000 - 70_000);
            r.Series.Pump();
            Assert.True(r.Series.Owes);
            Assert.True(r.NothingMoreSaid(2));

            r.OurInterrupt("a key of ours");
            r.Clock.Advance(SpeechArbiter.SalvageSettleMs + 100);
            r.Series.Pump();
            foreach (var expected in new[] { "final batch second result", "finished final batch last" })
            {
                var call = r.Channel.WaitForCall();
                Assert.Equal(expected, call.Text);
                r.Complete(call);
                r.Series.Pump();
            }
            Assert.False(r.Series.Owes);
            Assert.Equal(new[] { "seed completes before final batch", "final batch first result", "a key of ours",
                "final batch second result", "finished final batch last" }, r.Channel.Seen.ConvertAll(c => c.Text));
        }

        /// <summary>Codex's C2 control: a stalled batch with more lines waiting ends after the bound.</summary>
        [Fact]
        public void Codex_C2_control_a_nonfinal_batch_ends_after_the_stall_bound()
        {
            using var r = new Rig();
            r.Series.Add("first line that never answers", VerbosityLevel.Terse);
            r.Channel.WaitForCall();
            r.Series.Add("another line waiting upstairs", VerbosityLevel.Terse);
            r.Clock.Advance(SpokenSeries.StallMs + 1);
            r.Series.Pump();
            Assert.False(r.Series.Owes);
        }

        /// <summary>
        /// C2 on the series' own timer: nobody pumps it by hand, nothing is left
        /// waiting, and the final line never answers. The timer must still be
        /// watching, so the series ends once the bound has passed.
        /// </summary>
        [Fact]
        public void The_series_own_timer_watches_the_final_batch_until_it_is_retired()
        {
            using var r = new Rig(pollMs: 10);
            r.Series.Add("a final line that never answers", VerbosityLevel.Terse);
            r.Channel.WaitForCall();
            Thread.Sleep(50);
            Assert.True(r.Series.Owes);
            r.Clock.Advance(SpokenSeries.StallMs + 1);
            Assert.True(SpinWait.SpinUntil(() => !r.Series.Owes, 3000), "nothing watched the final line after it was handed over");
        }

        /// <summary>
        /// Bench 4a on the real pump (Track SL, #1228): NVDA's Ctrl cuts the
        /// sounding line part-way and the pump gives the three queued behind it
        /// back unspoken; the operator waits; then our key, and its own
        /// follow-up (the help text). Every remaining line is said exactly once,
        /// in order, after the help text, the cut line not repeated. 5 s is
        /// bench 4a2, the control that held before Track SL and catches a
        /// double (a line taken back AND put back by the arbiter); 16.5 s is past
        /// the arbiter's 15-second salvage lifetime, where the three were lost.
        /// </summary>
        [Theory]
        [InlineData(5000)]
        [InlineData(SpeechArbiter.SalvageCeilingMs + 1500)]
        public void Bench_4a_on_the_real_pump_every_remaining_line_is_said_once_in_order(int waitMs)
        {
            using var r = new Rig();
            r.Series.Add("seed", VerbosityLevel.Terse);
            var seed = r.Channel.WaitForCall();
            foreach (var l in new[] { "one", "two", "three", "four", "later" }) r.Series.Add(l, VerbosityLevel.Terse);
            r.Complete(seed);
            r.Series.Pump();
            var one = r.Channel.WaitForCall();
            Assert.Equal(3, r.Pump.QueuedCount);
            r.CancelFirstPartway(one);
            r.Series.Pump();
            Assert.True(r.NothingMoreSaid(2));

            r.Clock.Advance(waitMs);
            r.Series.Add("finished", VerbosityLevel.Terse);
            r.Series.Pump();
            Assert.True(r.NothingMoreSaid(2));

            r.OurInterrupt("a key of ours");
            r.Series.Pump();
            r.Arbiter.Emit("help text", false, SpeechIntent.Queue, VerbosityLevel.Terse, "real pump test", "help");
            var help = r.Channel.WaitForCall();
            Assert.Equal("help text", help.Text);
            r.Complete(help);
            r.Clock.Advance(SpeechArbiter.SalvageSettleMs + 100);   // the arbiter's hold, if any, and the series' settle
            r.Series.Pump();
            foreach (var expected in new[] { "two", "three", "four", "later", "finished" })
            {
                var call = r.Channel.WaitForCall();
                Assert.Equal(expected, call.Text);
                r.Complete(call);
                r.Series.Pump();
            }
            Assert.False(r.Series.Owes);
            Assert.True(r.NothingMoreSaid(10));
            Assert.Equal(new[] { "seed", "one", "a key of ours", "help text", "two", "three", "four", "later", "finished" },
                r.Channel.Seen.ConvertAll(c => c.Text));
        }

        /// <summary>
        /// The second shape on the real pump (Track SL): NVDA's Ctrl lands while
        /// another part of the app is sounding, so the series' queued line is
        /// only given back at word 0, never cut part-way. Past the salvage
        /// lifetime it was lost before Track SL; now it is said after our key.
        /// </summary>
        [Fact]
        public void Ctrl_while_another_utterance_is_sounding_on_the_real_pump_keeps_the_lists_place()
        {
            using var r = new Rig();
            r.Arbiter.Emit("other speech in flight", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "real pump test", "other");
            var other = r.Channel.WaitForCall();
            r.Series.Add("first result", VerbosityLevel.Terse);
            r.Series.Add("second result", VerbosityLevel.Terse);
            r.Series.Add("finished", VerbosityLevel.Terse);
            Assert.Equal(1, r.Pump.QueuedCount);
            r.CancelFirstPartway(other);
            r.Series.Pump();
            r.Clock.Advance(SpeechArbiter.SalvageCeilingMs + 1500);
            r.Series.Pump();
            Assert.True(r.NothingMoreSaid(1));

            r.OurInterrupt("a key of ours");
            r.Clock.Advance(SpeechArbiter.SalvageSettleMs + 100);
            r.Series.Pump();
            foreach (var expected in new[] { "first result", "second result", "finished" })
            {
                var call = r.Channel.WaitForCall();
                Assert.Equal(expected, call.Text);
                r.Complete(call);
                r.Series.Pump();
            }
            Assert.False(r.Series.Owes);
            Assert.Equal(new[] { "other speech in flight", "a key of ours", "first result", "second result", "finished" },
                r.Channel.Seen.ConvertAll(c => c.Text));
        }

        /// <summary>
        /// The series' own timer, paused (Track SL): nobody pumps by hand. After
        /// NVDA's Ctrl the timer keeps looking and hands nothing, however far
        /// the clock moves, and after our key it resumes the list by itself.
        /// </summary>
        [Fact]
        public void The_series_own_timer_keeps_a_paused_series_waiting_and_resumes_it_after_our_key()
        {
            using var r = new Rig(pollMs: 10);
            r.Series.Add("seed", VerbosityLevel.Terse);
            var seed = r.Channel.WaitForCall();
            foreach (var l in new[] { "one", "two", "three" }) r.Series.Add(l, VerbosityLevel.Terse);
            r.Complete(seed);
            var one = r.Channel.WaitForCall();
            r.CancelFirstPartway(one);
            Thread.Sleep(100);
            r.Clock.Advance(600_000);
            Assert.True(r.NothingMoreSaid(2));
            Assert.True(r.Series.Owes);

            r.OurInterrupt("a key of ours");
            r.Clock.Advance(SpeechArbiter.SalvageSettleMs + 100);
            foreach (var expected in new[] { "two", "three" })
            {
                var call = r.Channel.WaitForCall();
                Assert.Equal(expected, call.Text);
                r.Complete(call);
            }
            Assert.True(SpinWait.SpinUntil(() => !r.Series.Owes, 3000));
        }

        /// <summary>Codex's isolation probe: withdrawing one owner leaves another series on the same subject, and a safety warning in flight, alone.</summary>
        [Fact]
        public void Codex_withdrawing_one_owner_leaves_another_series_and_a_safety_warning_alone()
        {
            using var r = new Rig();
            // Hold the reader at an independent line, so every series line is unsent.
            r.Arbiter.Emit("independent in flight", true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "probe", "other");
            var independent = r.Channel.WaitForCall();
            r.Series.Add("owned by first series", VerbosityLevel.Terse);
            var second = new SpokenSeries(r.Arbiter, "diagnostic", "second owner", pollMs: 0);
            second.Add("owned by second series", VerbosityLevel.Terse);
            r.Series.Withdraw("close first owner");
            Assert.Equal(1, r.Pump.QueuedCount);
            Assert.True(second.Owes);
            r.Complete(independent);
            var survivor = r.Channel.WaitForCall();
            Assert.Equal("owned by second series", survivor.Text);
            r.Complete(survivor);
            r.Arbiter.Urgent("protected safety warning", VerbosityLevel.Critical, "probe", "safety");
            var warning = r.Channel.WaitForCall();
            second.Withdraw("close second owner");
            Assert.False(warning.Done.IsSet);
            r.Complete(warning);
        }
    }
}
