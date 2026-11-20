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
            Assert.True(r.NothingMoreSaid(3));                      // the rescued lines are held for their put-back
            r.Clock.Advance(SpeechArbiter.SalvageSettleMs + 100);   // the put-back
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
        /// </summary>
        [Fact]
        public void Codex_C2_a_final_batch_cut_from_outside_ends_after_the_stall_bound()
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
            r.Clock.Advance(SpokenSeries.StallMs + 1);
            r.Series.Pump();
            Assert.False(r.Series.Owes);
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
