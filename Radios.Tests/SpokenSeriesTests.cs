#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// <see cref="SpokenSeries"/>, the owner of the speed test's spoken lines
    /// (Sprint 49 Track L2 repair 1, #866; Codex's check of 979bfa1f, B1 to
    /// B3), through the REAL <see cref="SpeechArbiter"/> on a fake clock, with
    /// a delivery pump modelled on <see cref="PacedSpeechDelivery"/>: queued
    /// lines wait in order, an interrupt clears the queue and reports every
    /// cleared line unspoken and cut by us, a withdrawal removes one line by
    /// ticket only while it is still queued, and the reader says the head of
    /// the queue when the test tells it to. What the operator would hear is
    /// what the reader said plus what is still queued, in order.
    /// </summary>
    public sealed class SpokenSeriesTests
    {
        private sealed class Bench
        {
            public readonly FakeSpeechClock Clock = new();
            public readonly SpeechArbiter Arbiter;
            public bool Untracked;
            private long _next;
            public readonly List<(long Ticket, string Text)> Queue = new();
            public readonly List<string> Said = new();
            public readonly List<string> Withdrawn = new();
            public int MostOfOursQueued;

            public Bench()
            {
                Arbiter = new SpeechArbiter(Clock, () => VerbosityLevel.Chatty,
                    (message, interrupt, intent, level, origin, salvaged) => Sink(message, interrupt),
                    () => { }, (m, l, i, o) => { }, new SpeechRateModel(), () => null,
                    (ticket, reason) =>
                    {
                        int at = Queue.FindIndex(q => q.Ticket == ticket && ticket != 0);
                        if (at < 0) return false;
                        Withdrawn.Add(Queue[at].Text);
                        Queue.RemoveAt(at);
                        return true;
                    });
            }

            private SpeechHandoff Sink(string message, bool interrupt)
            {
                long ticket = Untracked ? 0 : ++_next;
                if (interrupt)
                {
                    var cleared = Queue.ToList();
                    Queue.Clear();
                    Queue.Insert(0, (ticket, message));
                    // As PacedSpeechDelivery.Interrupt does: each cleared line
                    // is reported withdrawn unspoken, by us.
                    foreach (var c in cleared.Where(c => c.Ticket != 0))
                        Arbiter.OnOutcome(c.Ticket, c.Text, SpeechOutcome.Cancelled(0, 1, byUs: true, 0));
                }
                else Queue.Add((ticket, message));
                return Untracked ? SpeechHandoff.Untracked : SpeechHandoff.TrackedAs(ticket);
            }

            public SpokenSeries Series(Func<VerbosityLevel>? verbosity = null, Action<string, VerbosityLevel>? gated = null) =>
                new SpokenSeries(Arbiter, "series", "test", verbosity, gated, pollMs: 0, nowMs: () => (long)Clock.ElapsedMs);

            /// <summary>The reader says the line at the head of the queue, if any.</summary>
            public bool SayOne()
            {
                if (Queue.Count == 0) return false;
                var head = Queue[0];
                Queue.RemoveAt(0);
                Said.Add(head.Text);
                if (head.Ticket != 0) Arbiter.OnOutcome(head.Ticket, head.Text, SpeechOutcome.Completed(1, 1, 0));
                return true;
            }

            /// <summary>Let the reader run until the series owes nothing and the queue is empty.</summary>
            public void SayAll(SpokenSeries series, Func<string, bool> ours)
            {
                for (int guard = 0; guard < 2000; guard++)
                {
                    series.Pump();
                    MostOfOursQueued = Math.Max(MostOfOursQueued, Queue.Count(q => ours(q.Text)));
                    if (SayOne()) continue;
                    if (!series.Owes) return;
                    Clock.Advance(100);
                }
                throw new InvalidOperationException("the series never finished");
            }

            /// <summary>What the operator hears from now on: said, then still queued, in order.</summary>
            public string[] Heard => Said.Concat(Queue.Select(q => q.Text)).ToArray();

            public void OtherInterrupt(string text) =>
                Arbiter.Emit(text, interrupt: true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "test", "other");
        }

        private static string[] Lines(int n) => Enumerable.Range(0, n).Select(i => "line" + i).ToArray();
        private static bool Ours(string text) => text.StartsWith("line", StringComparison.Ordinal) || text == "finished";

        [Fact]
        public void Every_line_is_said_once_in_order_and_no_more_than_a_batch_waits_in_the_reader()
        {
            var bench = new Bench();
            var series = bench.Series();
            var lines = Lines(18).Append("finished").ToArray();
            foreach (var l in lines) series.Add(l, VerbosityLevel.Terse);

            bench.SayAll(series, Ours);

            Assert.Equal(lines, bench.Said);
            Assert.Empty(bench.Withdrawn);
            // The shared ledger keeps 16; the series never has more than a
            // batch of its own in it (B2).
            Assert.InRange(bench.MostOfOursQueued, 1, SpokenSeries.BatchSize);
        }

        [Fact]
        public void Withdrawing_takes_back_a_line_the_ledger_let_go_of_at_its_cap()
        {
            var bench = new Bench();
            var series = bench.Series();
            series.Add("line0", VerbosityLevel.Terse);
            // Sixteen other queued lines: the ledger forgets line0, its oldest
            // ordinary entry, and leaves it in the pump.
            for (int i = 0; i < 16; i++)
                bench.Arbiter.Emit("other" + i, interrupt: false, SpeechIntent.Queue, VerbosityLevel.Terse, "test", "other" + i);

            series.Withdraw("the window closing");

            Assert.Contains("line0", bench.Withdrawn);
            Assert.DoesNotContain("line0", bench.Heard);
            Assert.False(series.Owes);

            // Positive control: the same arrangement, taken back by subject
            // alone, leaves line0 to be said. This is B2's loss.
            var control = new Bench();
            control.Arbiter.Emit("line0", interrupt: false, SpeechIntent.Queue, VerbosityLevel.Terse, "test", "series", additive: true);
            for (int i = 0; i < 16; i++)
                control.Arbiter.Emit("other" + i, interrupt: false, SpeechIntent.Queue, VerbosityLevel.Terse, "test", "other" + i);
            control.Arbiter.Supersede("series", "the window closing", "test");
            Assert.Contains("line0", control.Heard);
        }

        [Fact]
        public void After_an_interrupt_nothing_new_is_handed_over_until_the_rescued_lines_are_back_in_order()
        {
            var bench = new Bench();
            var series = bench.Series();
            series.Add("line0", VerbosityLevel.Terse);
            series.Add("line1", VerbosityLevel.Terse);
            series.Add("line2", VerbosityLevel.Terse);
            bench.SayOne();                       // line0 said; line1 and line2 handed over together
            series.Pump();
            Assert.Equal(new[] { "line1", "line2" }, bench.Queue.Select(q => q.Text));

            bench.OtherInterrupt("key");          // clears the queue; line1 and line2 held for their put-back
            foreach (var l in Lines(8).Skip(3).Append("finished")) series.Add(l, VerbosityLevel.Terse);
            series.Pump();
            Assert.Equal(new[] { "key" }, bench.Queue.Select(q => q.Text));   // nothing jumped ahead of the rescue
            Assert.True(series.Owes);

            bench.SayAll(series, Ours);

            Assert.Equal(new[] { "line0", "key" }.Concat(Lines(8).Skip(1)).Append("finished"), bench.Heard);
        }

        [Fact]
        public void On_the_estimate_path_a_held_line_whose_old_estimate_ran_out_still_holds_the_next_back()
        {
            var bench = new Bench { Untracked = true };
            var series = bench.Series();
            series.Add("line0", VerbosityLevel.Terse);   // estimated to finish at 800 ms at the earliest
            bench.Clock.Advance(700);
            bench.OtherInterrupt("key");                  // line0 not yet finished by its estimate: held
            series.Add("line1", VerbosityLevel.Terse);
            series.Add("finished", VerbosityLevel.Terse);
            bench.Clock.Advance(300);                     // past line0's old estimate, inside the 600 ms hold
            series.Pump();
            Assert.Equal(new[] { "key" }, bench.Queue.Select(q => q.Text));

            bench.SayAll(series, Ours);

            Assert.Equal(new[] { "key", "line0", "line1", "finished" }, bench.Heard);
        }

        [Fact]
        public void An_urgent_warning_goes_first_and_the_series_carries_on_behind_it()
        {
            var bench = new Bench();
            var series = bench.Series();
            foreach (var l in Lines(3)) series.Add(l, VerbosityLevel.Terse);
            bench.SayOne();                       // line0 said; line1 and line2 handed over
            series.Pump();

            bench.Arbiter.Urgent("warning", VerbosityLevel.Critical, "test", "safety");
            Assert.Equal("warning", bench.Queue[0].Text);
            series.Add("line3", VerbosityLevel.Terse);
            series.Add("finished", VerbosityLevel.Terse);

            bench.SayAll(series, Ours);

            // The warning discarded what was queued, ours included; nothing of
            // ours went ahead of it, and the series carried on behind it.
            Assert.Equal(new[] { "line0", "warning", "line3", "finished" }, bench.Heard);
        }

        [Fact]
        public void The_operators_silence_ends_the_series()
        {
            var bench = new Bench();
            var series = bench.Series();
            foreach (var l in Lines(3)) series.Add(l, VerbosityLevel.Terse);
            bench.Arbiter.OnSilenced();
            bench.Queue.Clear();                  // the pump discards on Silence
            series.Add("finished", VerbosityLevel.Terse);
            series.Pump();

            Assert.False(series.Owes);
            Assert.Empty(bench.Queue);
            bench.SayAll(series, Ours);
            Assert.Empty(bench.Heard);
        }

        [Fact]
        public void A_series_nothing_is_said_from_ends_after_the_stall_bound_and_takes_back_what_it_handed()
        {
            var bench = new Bench();
            var series = bench.Series();
            foreach (var l in Lines(3)) series.Add(l, VerbosityLevel.Terse);
            Assert.Equal(new[] { "line0" }, bench.Queue.Select(q => q.Text));

            bench.Clock.Advance(SpokenSeries.StallMs - 100);
            series.Pump();
            Assert.True(series.Owes);
            bench.Clock.Advance(200);
            series.Pump();

            Assert.False(series.Owes);
            Assert.Equal(new[] { "line0" }, bench.Withdrawn);
            Assert.Empty(bench.Queue);
        }

        [Fact]
        public void A_line_above_the_operators_verbosity_is_not_said_and_is_recorded_as_gated()
        {
            var bench = new Bench();
            var gated = new List<string>();
            var series = bench.Series(() => VerbosityLevel.Terse, (text, level) => gated.Add(text));
            series.Add("line0", VerbosityLevel.Chatty);
            series.Add("line1", VerbosityLevel.Terse);
            bench.SayAll(series, Ours);

            Assert.Equal(new[] { "line1" }, bench.Said);
            Assert.Equal(new[] { "line0" }, gated);
        }
    }
}
