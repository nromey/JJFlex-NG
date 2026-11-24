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
    /// <para>Since repair 2 (Codex's re-check of 03280742, C1 and C2) the
    /// reader can also hold a line IN FLIGHT and be cut part-way by something
    /// outside the app (NVDA's own Ctrl, a focus change), which is what
    /// <see cref="PacedSpeechDelivery"/> reports then: every queued line given
    /// back at zero words, not by us, and the line in flight cut at the word it
    /// reached, not by us. Nothing on that road calls the arbiter's
    /// <c>OnSilenced</c>, which only the app's own Silence does.</para>
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
            /// <summary>Lines the reader started and was cut in, part-way: heard in part, never in full.</summary>
            public readonly List<string> CutPartway = new();
            /// <summary>The line the reader is saying now, if any.</summary>
            public (long Ticket, string Text)? InFlight;
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
                    // And the line in flight is cut by us, after its first word.
                    if (InFlight is { } cut)
                    {
                        InFlight = null;
                        CutPartway.Add(cut.Text);
                        if (cut.Ticket != 0) Arbiter.OnOutcome(cut.Ticket, cut.Text, SpeechOutcome.Cancelled(1, 3, byUs: true, 0));
                    }
                }
                else Queue.Add((ticket, message));
                return Untracked ? SpeechHandoff.Untracked : SpeechHandoff.TrackedAs(ticket);
            }

            public SpokenSeries Series(Func<VerbosityLevel>? verbosity = null, Action<string, VerbosityLevel>? gated = null) =>
                new SpokenSeries(Arbiter, "series", "test", verbosity, gated, pollMs: 0, nowMs: () => (long)Clock.ElapsedMs);

            /// <summary>The reader takes the line at the head of the queue and starts saying it.</summary>
            public bool StartOne()
            {
                if (InFlight != null || Queue.Count == 0) return false;
                InFlight = Queue[0];
                Queue.RemoveAt(0);
                return true;
            }

            /// <summary>
            /// Something outside the app cuts the line in flight after
            /// <paramref name="marks"/> words (zero: before its first), and the
            /// pump gives everything it still queued back to the arbiter, as
            /// <see cref="PacedSpeechDelivery"/> does: each queued line first, at
            /// zero words and not by us, then the line that was sounding.
            /// </summary>
            public void CutFromOutside(int marks)
            {
                var cut = InFlight ?? throw new InvalidOperationException("nothing is in flight");
                InFlight = null;
                var queued = Queue.ToList();
                Queue.Clear();
                foreach (var q in queued.Where(q => q.Ticket != 0))
                    Arbiter.OnOutcome(q.Ticket, q.Text, SpeechOutcome.Cancelled(0, 1, byUs: false, 0));
                if (marks > 0) CutPartway.Add(cut.Text);
                if (cut.Ticket != 0) Arbiter.OnOutcome(cut.Ticket, cut.Text, SpeechOutcome.Cancelled(marks, 3, byUs: false, 0));
            }

            /// <summary>The reader says the line in flight to its end, or else the line at the head of the queue, if any.</summary>
            public bool SayOne()
            {
                if (InFlight is { } sounding)
                {
                    InFlight = null;
                    Said.Add(sounding.Text);
                    if (sounding.Ticket != 0) Arbiter.OnOutcome(sounding.Ticket, sounding.Text, SpeechOutcome.Completed(1, 1, 0));
                    return true;
                }
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

            /// <summary>Another part of the app queues a line (an interrupt's own follow-up, a status), on a subject of its own.</summary>
            public void OtherQueued(string text) =>
                Arbiter.Emit(text, interrupt: false, SpeechIntent.Queue, VerbosityLevel.Terse, "test", "queued " + text);

            /// <summary>Time passes with the series' timer looking every second, and nothing said.</summary>
            public void Wait(SpokenSeries series, int ms)
            {
                for (int left = ms; left > 0; left -= 1000)
                {
                    Clock.Advance(Math.Min(1000, left));
                    series.Pump();
                }
            }

            /// <summary>The reader says everything queued, the series looking after each line, and no time passes.</summary>
            public void SayQueued(SpokenSeries series)
            {
                series.Pump();
                while (SayOne()) series.Pump();
            }
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

        /// <summary>
        /// Repair 2: a lone line cut from outside, then an urgent warning. The
        /// warning discards ordinary speech, the cut line's ledger entry with
        /// it, so the series carries on behind the warning, as it does when the
        /// warning discards lines that were queued.
        /// </summary>
        [Fact]
        public void After_a_cut_from_outside_an_urgent_warning_goes_first_and_the_series_carries_on_behind_it()
        {
            var bench = new Bench();
            var series = bench.Series();
            foreach (var l in Lines(3).Append("finished")) series.Add(l, VerbosityLevel.Terse);
            Assert.True(bench.StartOne());
            bench.CutFromOutside(2);
            series.Pump();
            Assert.Empty(bench.Queue);

            bench.Arbiter.Urgent("warning", VerbosityLevel.Critical, "test", "safety");
            bench.SayAll(series, Ours);

            Assert.Equal(new[] { "warning", "line1", "line2", "finished" }, bench.Heard);
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

        /// <summary>
        /// Codex's re-check of 03280742, C1, on the stepped reader: ONE rule for
        /// a cut made outside the app, whichever of the series' lines was
        /// sounding. Before repair 2 a lone line cut part-way let the next lines
        /// go to the reader at once, re-starting speech the operator had just
        /// stopped, while the same cut inside a batch waited. Now the series
        /// hands nothing new until the app's own next interrupt (here another
        /// part of the app's "key"), and then carries on behind it: lines the
        /// pump gave back unspoken are put back by that interrupt, and the line
        /// cut part-way is not repeated, by the arbiter's own rule for a cut
        /// nobody can name. An interrupt that came BEFORE the cut is not that
        /// recovery (interruptFirst).
        /// </summary>
        [Theory]
        [InlineData(true, 2, false)]
        [InlineData(true, 2, true)]
        [InlineData(true, 0, false)]
        [InlineData(false, 2, false)]
        [InlineData(false, 2, true)]
        [InlineData(false, 0, false)]
        public void A_cut_from_outside_holds_the_series_until_our_next_interrupt_whichever_line_was_sounding(
            bool lone, int marks, bool interruptFirst)
        {
            var bench = new Bench();
            var heardFirst = new List<string>();
            if (interruptFirst)
            {
                bench.OtherInterrupt("earlier key");
                bench.SayOne();
                heardFirst.Add("earlier key");
            }
            var series = bench.Series();
            series.Add("line0", VerbosityLevel.Terse);              // handed over alone
            foreach (var l in Lines(6).Skip(1).Append("finished")) series.Add(l, VerbosityLevel.Terse);
            string sounding;
            if (lone)
            {
                Assert.Equal(new[] { "line0" }, bench.Queue.Select(q => q.Text));
                sounding = "line0";
            }
            else
            {
                bench.SayOne();                                      // line0 said; a batch is handed over
                heardFirst.Add("line0");
                series.Pump();
                Assert.Equal(Lines(5).Skip(1), bench.Queue.Select(q => q.Text));
                sounding = "line1";
            }

            Assert.True(bench.StartOne());
            bench.CutFromOutside(marks);
            for (int i = 0; i < 5; i++)
            {
                series.Pump();
                bench.Clock.Advance(1000);
            }
            Assert.Empty(bench.Queue);                               // nothing re-started after the cut
            Assert.True(series.Owes);

            bench.OtherInterrupt("key");
            bench.SayAll(series, Ours);

            // Given back unspoken (zero words): put back, a first hearing. Cut
            // part-way: heard in part and not repeated.
            var rest = Lines(6).Append("finished").SkipWhile(l => l != sounding);
            if (marks > 0) rest = rest.Skip(1);
            Assert.Equal(heardFirst.Append("key").Concat(rest), bench.Heard);
            Assert.Equal(marks > 0 ? new[] { sounding } : Array.Empty<string>(), bench.CutPartway);
            Assert.Empty(bench.Withdrawn);
        }

        /// <summary>
        /// REWRITTEN by Track SL (#1228, ruled by Noel 2026-11-20, S2). This
        /// test was <c>A_cut_the_ledger_has_since_let_go_of_no_longer_holds_the_series_whichever_line_was_sounding</c>
        /// and asserted the defect the ruling removes: once the arbiter's
        /// ledger let the cut go (its <see cref="SpeechArbiter.SalvageCeilingMs"/>
        /// lifetime, judged when the arbiter next prunes, here on another part of
        /// the app's queued line), the series carried on into the silence NVDA's
        /// Ctrl had made, and the batch's lines given back unspoken were gone.
        /// Now the list keeps its own place: the series hands nothing into the
        /// silence however long the pause, whatever the ledger let go of, and
        /// after our next key every line never said is said, in order, the line
        /// cut part-way not repeated.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_cut_the_ledger_has_since_let_go_of_still_holds_the_series_until_our_next_key_whichever_line_was_sounding(bool lone)
        {
            var bench = new Bench();
            var series = bench.Series();
            var heardFirst = new List<string>();
            series.Add("line0", VerbosityLevel.Terse);
            foreach (var l in Lines(6).Skip(1).Append("finished")) series.Add(l, VerbosityLevel.Terse);
            int firstAfter = 1;
            if (!lone)
            {
                bench.SayOne();                                      // line0 said; line1 to line4 handed over
                heardFirst.Add("line0");
                series.Pump();
                firstAfter = 2;
            }
            Assert.True(bench.StartOne());
            bench.CutFromOutside(2);
            series.Pump();
            Assert.Empty(bench.Queue);

            bench.Clock.Advance(SpeechArbiter.SalvageCeilingMs + 1000);
            bench.OtherQueued("status");                             // the arbiter prunes the cut here
            bench.SayQueued(series);
            bench.Wait(series, 5000);

            Assert.Equal(heardFirst.Append("status"), bench.Heard);   // nothing of ours into the silence
            Assert.True(series.Owes);

            bench.OtherInterrupt("key");
            bench.SayAll(series, Ours);

            Assert.Equal(heardFirst.Append("status").Append("key").Concat(Lines(6).Skip(firstAfter)).Append("finished"),
                bench.Heard);
            Assert.Equal(new[] { lone ? "line0" : "line1" }, bench.CutPartway);
        }

        /// <summary>
        /// Bench 4a as a test (Track SL, #1228): two lines said (here line0),
        /// NVDA's Ctrl cuts the sounding line part-way and the three handed over
        /// behind it at word 0, the operator waits, then presses our key
        /// (Ctrl+F1 on Start: the key's interrupt, then its own follow-up, the
        /// help text). Every remaining line is said once, in order, starting at
        /// the first line never begun, the cut line not repeated, "finished"
        /// last; lines made during the pause come after the ones taken back.
        /// <para>5 s is bench 4a2, the control, which held before Track SL and
        /// is the test that catches a double. 16.5 s is past the arbiter's
        /// 15-second salvage lifetime (the bench's 15.5 s, plus a second): before
        /// Track SL the three handed lines were lost there. 70 s and 10 minutes
        /// are past the one-minute stall bound, which no longer ends a series
        /// paused by a cut from outside (ruled S4, 2026-11-23).</para>
        /// </summary>
        [Theory]
        [InlineData(5000)]
        [InlineData(SpeechArbiter.SalvageCeilingMs + 1500)]
        [InlineData(70_000)]
        [InlineData(600_000)]
        public void Bench_4a_after_NVDAs_Ctrl_our_next_key_brings_every_remaining_line_once_in_order_however_long_the_wait(int waitMs)
        {
            var bench = new Bench();
            var series = bench.Series();
            foreach (var l in Lines(6)) series.Add(l, VerbosityLevel.Terse);
            bench.SayOne();                                          // line0 said
            series.Pump();                                           // line1 to line4 handed over
            Assert.Equal(Lines(5).Skip(1), bench.Queue.Select(q => q.Text));
            Assert.True(bench.StartOne());                           // line1 sounding
            bench.CutFromOutside(2);                                 // cut part-way; line2 to line4 given back at word 0
            series.Pump();

            bench.Wait(series, waitMs);
            series.Add("line6", VerbosityLevel.Terse);               // the test still running during the pause
            series.Add("finished", VerbosityLevel.Terse);
            bench.Wait(series, 1000);
            Assert.Empty(bench.Queue);                               // nothing into the silence
            Assert.True(series.Owes);

            bench.OtherInterrupt("key");
            bench.OtherQueued("help");                               // the key's own follow-up
            bench.SayAll(series, Ours);

            Assert.Equal(new[] { "line0", "key", "help" }.Concat(Lines(7).Skip(2)).Append("finished"), bench.Heard);
            Assert.Equal(new[] { "line1" }, bench.CutPartway);
            Assert.Equal(bench.Heard.Length, bench.Heard.Distinct().Count());   // nothing said twice
        }

        /// <summary>
        /// The second shape of a cut from outside (Track SL, #1228; found by the
        /// brief's cold reader from the code): NVDA's Ctrl lands while some
        /// OTHER utterance is sounding, so none of the series' lines is cut
        /// part-way; all of them are given back at word 0, which the arbiter
        /// records as <see cref="SpeechArbiter.Owed.NeverStarted"/>, the same
        /// state our own interrupt's flush gives. Before Track SL the series
        /// noticed only a part-way cut, so these waited in the arbiter's ledger
        /// and were lost to its 15-second lifetime exactly as in bench 4a.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Ctrl_while_another_utterance_is_sounding_still_keeps_the_lists_place(bool lone)
        {
            var bench = new Bench();
            var series = bench.Series();
            var heardFirst = new List<string>();
            int firstAfter;
            if (lone)
            {
                bench.OtherQueued("other speech");
                foreach (var l in Lines(6).Append("finished")) series.Add(l, VerbosityLevel.Terse);
                Assert.Equal(new[] { "other speech", "line0" }, bench.Queue.Select(q => q.Text));
                firstAfter = 0;
            }
            else
            {
                foreach (var l in Lines(6).Append("finished")) series.Add(l, VerbosityLevel.Terse);
                bench.SayOne();                                      // line0 said
                heardFirst.Add("line0");
                bench.OtherQueued("other speech");
                series.Pump();                                       // line1 to line4 handed over behind it
                Assert.Equal(new[] { "other speech" }.Concat(Lines(5).Skip(1)), bench.Queue.Select(q => q.Text));
                firstAfter = 1;
            }
            Assert.True(bench.StartOne());                           // the other utterance sounding
            bench.CutFromOutside(2);                                 // ours all given back at word 0
            series.Pump();
            bench.Wait(series, SpeechArbiter.SalvageCeilingMs + 1500);
            Assert.Equal(heardFirst, bench.Heard);
            Assert.True(series.Owes);

            bench.OtherInterrupt("key");
            bench.SayAll(series, Ours);

            Assert.Equal(heardFirst.Append("key").Concat(Lines(6).Skip(firstAfter)).Append("finished"), bench.Heard);
            Assert.Equal(new[] { "other speech" }, bench.CutPartway);   // not ours, and not repeated
        }

        /// <summary>
        /// The interrupt's own speech still goes first (Track SL, item 4; the
        /// settle window is #507's): the series' timer may look the moment our
        /// key's interrupt reaches the reader, before the key's own follow-up is
        /// queued, and must not re-hand its lines then. Bench 4a2 heard the help
        /// text, then the list.
        /// </summary>
        [Fact]
        public void After_the_pause_our_keys_own_follow_up_is_heard_before_the_list_resumes()
        {
            var bench = new Bench();
            var series = bench.Series();
            foreach (var l in Lines(6).Append("finished")) series.Add(l, VerbosityLevel.Terse);
            bench.SayOne();
            series.Pump();
            Assert.True(bench.StartOne());
            bench.CutFromOutside(2);
            series.Pump();
            bench.Wait(series, SpeechArbiter.SalvageCeilingMs + 1500);

            bench.OtherInterrupt("key");
            series.Pump();                                           // the timer, before the follow-up exists
            bench.Clock.Advance(SpeechArbiter.SalvageSettleMs - 100);
            series.Pump();
            Assert.Equal(new[] { "key" }, bench.Queue.Select(q => q.Text));
            bench.OtherQueued("help");
            bench.SayAll(series, Ours);

            Assert.Equal(new[] { "line0", "key", "help" }.Concat(Lines(6).Skip(2)).Append("finished"), bench.Heard);
        }

        /// <summary>
        /// The urgent rule with lines the series had already taken back (Track
        /// SL): a warning discards ordinary speech, and the series carries on
        /// behind it, but the lines it took back after a cut from outside were
        /// no longer in the reader's queue for the warning to discard, so they
        /// keep their place and follow the warning. The lone-line case, with
        /// nothing to take back, is <see cref="After_a_cut_from_outside_an_urgent_warning_goes_first_and_the_series_carries_on_behind_it"/>,
        /// unchanged.
        /// </summary>
        [Fact]
        public void After_a_cut_from_outside_inside_a_batch_an_urgent_warning_goes_first_and_the_lines_taken_back_follow_it()
        {
            var bench = new Bench();
            var series = bench.Series();
            foreach (var l in Lines(6).Append("finished")) series.Add(l, VerbosityLevel.Terse);
            bench.SayOne();
            series.Pump();
            Assert.True(bench.StartOne());
            bench.CutFromOutside(2);
            series.Pump();
            bench.Wait(series, 3000);

            bench.Arbiter.Urgent("warning", VerbosityLevel.Critical, "test", "safety");
            bench.SayAll(series, Ours);

            Assert.Equal(new[] { "line0", "warning" }.Concat(Lines(6).Skip(2)).Append("finished"), bench.Heard);
        }

        /// <summary>
        /// A paused series may wait for hours (ruled S4), so each look its timer
        /// takes while paused allocates nothing, and nothing it holds grows
        /// (Track SL, item 6, option a).
        /// </summary>
        [Fact]
        public void A_paused_series_looks_again_without_allocating()
        {
            var bench = new Bench();
            var series = bench.Series();
            foreach (var l in Lines(6).Append("finished")) series.Add(l, VerbosityLevel.Terse);
            bench.SayOne();
            series.Pump();
            Assert.True(bench.StartOne());
            bench.CutFromOutside(2);
            series.Pump();
            for (int i = 0; i < 100; i++) series.Pump();             // warm

            for (int round = 0; round < 3; round++)
            {
                bench.Clock.Advance(StallBoundPlus(round));
                Assert.Equal(0, CountedWithoutCollections(() => { for (int i = 0; i < 10_000; i++) series.Pump(); }));
            }
            Assert.True(series.Owes);
            Assert.Empty(bench.Queue);
        }

        /// <summary>
        /// Bytes this thread allocated running <paramref name="work"/>, counted
        /// inside a no-collection region. A background collection that starts
        /// mid-count, set off by another test's allocations, makes the per-thread
        /// counter report up to 8 KB this thread never allocated (measured by
        /// Sprint 49 Track P2 repair 3, <c>RxContentClassifierTests</c>, whose
        /// pattern this follows); with no collection able to start the count is
        /// exact. A region the runtime refuses or a collection forced anyway is
        /// counted again, up to three tries.
        /// </summary>
        private static long CountedWithoutCollections(Action work)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                bool region = false;
                for (long size = 16L * 1024 * 1024; size >= 1024 * 1024 && !region; size /= 2)
                {
                    try { region = GC.TryStartNoGCRegion(size); break; }
                    catch (ArgumentOutOfRangeException) { }
                }
                long before = GC.GetAllocatedBytesForCurrentThread();
                work();
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                bool held = System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion;
                if (held) GC.EndNoGCRegion();
                if (region && held) return bytes;
            }
            throw new InvalidOperationException("no count was held free of collections in three tries");
        }

        private static int StallBoundPlus(int round) => SpokenSeries.StallMs * (round + 1) + 1000;

        // ── Repair 1: NVDA's Ctrl inside the settle after one of our keys ──

        /// <summary>
        /// Bench run 3b (2026-11-23), on the stepped reader: our key cuts the
        /// sounding line (by us, after one word) and the batch behind it, the
        /// arbiter holds them behind the key's own speech, and NVDA's Ctrl cuts
        /// only that speech. Inside the 600 ms settle (100, 300, 550 ms) the
        /// arbiter used to release the held lines into the silence 600 ms after
        /// the key, and a line was heard two or three times. Now nothing is
        /// said after the Ctrl however long the wait (past the arbiter's 15 s
        /// lifetime here), and the next key brings every remaining line once,
        /// in order: the line our key cut after one word is said again in full,
        /// as the arbiter's own put-back would have said it.
        /// <para>700 ms is the control, outside the settle: the held lines
        /// were already released behind the key's speech when the Ctrl gave
        /// them back. It failed on the first Track SL too, for finding 1: the
        /// line our key had cut part-way was then never said in full.</para>
        /// <para><paramref name="seriesLooks"/> false: the series' timer does not
        /// look between the Ctrl and the end of the settle (a Ctrl at 550 ms
        /// leaves it one 40 ms poll, which a busy machine can miss). Then only the
        /// arbiter's own rule for held series lines keeps them out of the
        /// silence; with the series looking first, the series takes them back
        /// before the release.</para>
        /// </summary>
        [Theory]
        [InlineData(100, true)]
        [InlineData(300, true)]
        [InlineData(550, true)]
        [InlineData(700, true)]
        [InlineData(100, false)]
        [InlineData(550, false)]
        public void Repair1_route_3b_NVDAs_Ctrl_on_our_keys_speech_never_releases_the_list_into_the_silence(int ctrlAfterMs, bool seriesLooks)
        {
            var bench = new Bench();
            var series = bench.Series();
            foreach (var l in Lines(6).Append("finished")) series.Add(l, VerbosityLevel.Terse);
            bench.SayOne();                                          // line0 said
            series.Pump();                                           // line1 to line4 handed over
            Assert.True(bench.StartOne());                           // line1 sounding
            bench.OtherInterrupt("key");                             // our key: line1 cut by us at word 1, the rest held
            series.Pump();
            Assert.Equal(new[] { "key" }, bench.Queue.Select(q => q.Text));
            Assert.True(bench.StartOne());                           // the key's own speech sounding
            bench.Clock.Advance(ctrlAfterMs);
            if (seriesLooks) series.Pump();
            bench.CutFromOutside(2);                                 // NVDA's Ctrl, on the key's speech
            if (seriesLooks) series.Pump();
            else bench.Clock.Advance(1000);                          // the settle ends before the series looks
            bench.Wait(series, SpeechArbiter.SalvageCeilingMs + 1500);

            Assert.Equal(new[] { "line0" }, bench.Heard);            // silent after the Ctrl
            Assert.True(series.Owes);

            bench.OtherInterrupt("next key");
            bench.SayAll(series, Ours);

            Assert.Equal(new[] { "line0", "next key" }.Concat(Lines(6).Skip(1)).Append("finished"), bench.Heard);
            Assert.Equal(new[] { "line1", "key" }, bench.CutPartway);
            Assert.Equal(bench.Heard.Length, bench.Heard.Distinct().Count());
        }

        /// <summary>
        /// Bench run 3d, on the stepped reader: NVDA's own key-press cut reaches
        /// the reader before our key's interrupt, so our key's cut of the
        /// sounding line is recorded as a cut from outside and the list pauses
        /// on our own key; then NVDA's Ctrl cuts that key's speech inside its
        /// settle. The key's interrupt reached the reader BEFORE that Ctrl, so
        /// it is not the next key: nothing is said after the Ctrl, and the next
        /// key brings every remaining line once, in order (the line cut part-way
        /// from outside is not repeated). Whether or not the series looked
        /// between the two cuts.
        /// </summary>
        [Theory]
        [InlineData(100, false)]
        [InlineData(300, false)]
        [InlineData(550, false)]
        [InlineData(300, true)]
        public void Repair1_route_3d_the_key_whose_own_cut_paused_the_list_does_not_resume_it_past_NVDAs_Ctrl(int ctrlAfterMs, bool lookedBetween)
        {
            var bench = new Bench();
            var series = bench.Series();
            foreach (var l in Lines(6).Append("finished")) series.Add(l, VerbosityLevel.Terse);
            bench.SayOne();
            series.Pump();
            Assert.True(bench.StartOne());                           // line1 sounding
            bench.CutFromOutside(8);                                 // NVDA's key-press cut arrives first
            if (lookedBetween) series.Pump();
            bench.OtherInterrupt("key");                             // then our key's own interrupt
            series.Pump();
            Assert.True(bench.StartOne());                           // its speech sounding
            bench.Clock.Advance(ctrlAfterMs);
            series.Pump();
            bench.CutFromOutside(2);                                 // NVDA's Ctrl, on the key's speech
            series.Pump();
            bench.Wait(series, SpeechArbiter.SalvageCeilingMs + 1500);

            Assert.Equal(new[] { "line0" }, bench.Heard);
            Assert.True(series.Owes);

            bench.OtherInterrupt("next key");
            bench.SayAll(series, Ours);

            Assert.Equal(new[] { "line0", "next key" }.Concat(Lines(6).Skip(2)).Append("finished"), bench.Heard);
            Assert.Equal(new[] { "line1", "key" }, bench.CutPartway);
        }

        /// <summary>
        /// Why "the same key" is told from "the next key" by the arbiter's own
        /// order of events, not by guessing which keypress a cut came from
        /// (repair 1): with no Ctrl after it, one press of our key gives the
        /// operator the same thing whichever way the race between NVDA's
        /// key-press cut and our interrupt went. The key's speech, then the rest
        /// of the list, with no second key needed. (The line that was sounding
        /// differs only by the arbiter's standing rule: cut by us it is said
        /// again, cut from outside it is not.)
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Repair1_one_press_of_our_key_gives_the_same_list_whichever_way_the_race_went(bool nvdaCutFirst)
        {
            var bench = new Bench();
            var series = bench.Series();
            foreach (var l in Lines(6).Append("finished")) series.Add(l, VerbosityLevel.Terse);
            bench.SayOne();
            series.Pump();
            Assert.True(bench.StartOne());
            if (nvdaCutFirst) bench.CutFromOutside(8);
            bench.OtherInterrupt("key");
            series.Pump();
            bench.SayAll(series, Ours);

            var rest = Lines(6).Skip(nvdaCutFirst ? 2 : 1).Append("finished");
            Assert.Equal(new[] { "line0", "key" }.Concat(rest), bench.Heard);
        }

        /// <summary>
        /// The arbiter's new rule is for held SERIES lines only (repair 1's
        /// brief): ordinary held speech is released at the end of the settle
        /// exactly as before, even when NVDA's Ctrl cut the interrupt's own
        /// speech inside it.
        /// </summary>
        [Fact]
        public void Repair1_ordinary_held_speech_is_still_released_after_a_Ctrl_inside_the_settle()
        {
            var bench = new Bench();
            bench.OtherQueued("other A");
            bench.OtherQueued("other B");
            Assert.True(bench.StartOne());                           // other A sounding
            bench.OtherInterrupt("key");                             // held: A (cut at word 1), B
            Assert.True(bench.StartOne());
            bench.Clock.Advance(300);
            bench.CutFromOutside(2);                                 // NVDA's Ctrl on the key's speech
            Assert.Empty(bench.Queue);
            bench.Clock.Advance(400);                                // the settle ends

            Assert.Equal(new[] { "other A", "other B" }, bench.Queue.Select(q => q.Text));
        }

        /// <summary>
        /// A cut from outside made while the list had nothing handed over is not
        /// a cut of anything it waits behind: the list's next lines are handed
        /// over and said with no key (repair 1).
        /// </summary>
        [Fact]
        public void Repair1_a_cut_from_outside_before_the_lists_lines_were_handed_over_does_not_pause_it()
        {
            var bench = new Bench();
            var series = bench.Series();
            bench.OtherQueued("other speech");
            Assert.True(bench.StartOne());
            bench.CutFromOutside(2);
            foreach (var l in Lines(6).Append("finished")) series.Add(l, VerbosityLevel.Terse);
            bench.SayAll(series, Ours);

            Assert.Equal(Lines(6).Append("finished"), bench.Heard);
        }

        /// <summary>
        /// Codex's re-check, C2, on the stepped reader: the stall bound covers
        /// the final batch too. Before repair 2 the series stopped watching once
        /// nothing was left waiting, so a last line the reader never answered
        /// stayed owed for ever. The non-final case is
        /// <see cref="A_series_nothing_is_said_from_ends_after_the_stall_bound_and_takes_back_what_it_handed"/>.
        /// <para>CHANGED by Track SL (ruled S4, 2026-11-23): this was a theory
        /// whose second case (<c>cutFirst</c>) asserted that a final batch cut
        /// from outside also ends at the bound. After NVDA's Ctrl there is no
        /// time limit now, so that case is
        /// <see cref="After_a_cut_from_outside_a_final_batch_waits_past_the_stall_bound_for_our_next_key"/>.
        /// What stays here is the silent reader nobody pressed Ctrl on, which
        /// the bound still covers.</para>
        /// </summary>
        [Fact]
        public void The_stall_bound_ends_a_final_batch_nothing_more_is_heard_from()
        {
            var bench = new Bench();
            var series = bench.Series();
            series.Add("finished", VerbosityLevel.Terse);           // the final line, handed over
            Assert.True(bench.StartOne());                           // and the reader never answers

            bench.Clock.Advance(SpokenSeries.StallMs - 100);
            series.Pump();
            Assert.True(series.Owes);
            bench.Clock.Advance(200);
            series.Pump();

            Assert.False(series.Owes);
            Assert.Empty(bench.Queue);
        }

        /// <summary>
        /// The other half of S4 (Track SL): a final batch whose first line was
        /// cut part-way from outside, its other lines given back unspoken, is
        /// still waiting well past the one-minute bound (70 s and ten minutes),
        /// says nothing into the silence, and our next key brings every
        /// remaining line once, in order. The control, a reader that simply
        /// stops answering with no cut, still ends at the bound:
        /// <see cref="The_stall_bound_ends_a_final_batch_nothing_more_is_heard_from"/>.
        /// After resuming the stall clock starts afresh, so a reader that then
        /// goes silent is bounded again.
        /// </summary>
        [Theory]
        [InlineData(70_000)]
        [InlineData(600_000)]
        public void After_a_cut_from_outside_a_final_batch_waits_past_the_stall_bound_for_our_next_key(int waitMs)
        {
            var bench = new Bench();
            var series = bench.Series();
            series.Add("line0", VerbosityLevel.Terse);
            foreach (var l in new[] { "line1", "line2", "line3", "finished" }) series.Add(l, VerbosityLevel.Terse);
            bench.SayOne();                                          // line0 said; the final batch is handed over
            series.Pump();
            Assert.Equal(new[] { "line1", "line2", "line3", "finished" }, bench.Queue.Select(q => q.Text));
            Assert.True(bench.StartOne());
            bench.CutFromOutside(2);

            bench.Wait(series, waitMs);
            Assert.True(series.Owes);
            Assert.Empty(bench.Queue);
            Assert.Empty(bench.Withdrawn);

            bench.OtherInterrupt("key");
            bench.SayAll(series, Ours);
            Assert.Equal(new[] { "line0", "key", "line2", "line3", "finished" }, bench.Heard);
        }

        /// <summary>
        /// Resuming starts the stall clock afresh (Track SL, item 6): the
        /// reader goes silent on the first line handed after our key, with no
        /// Ctrl this time, and the series ends a minute after the resume, not at
        /// once for the ten minutes it spent paused and not never.
        /// </summary>
        [Fact]
        public void After_resuming_the_stall_clock_starts_afresh()
        {
            var bench = new Bench();
            var series = bench.Series();
            series.Add("line0", VerbosityLevel.Terse);
            foreach (var l in new[] { "line1", "line2", "line3", "finished" }) series.Add(l, VerbosityLevel.Terse);
            bench.SayOne();
            series.Pump();
            Assert.True(bench.StartOne());
            bench.CutFromOutside(2);
            bench.Wait(series, 600_000);

            bench.OtherInterrupt("key");
            Assert.True(bench.SayOne());
            bench.Wait(series, SpeechArbiter.SalvageSettleMs + 100);
            Assert.Equal(new[] { "line2", "line3", "finished" }, bench.Queue.Select(q => q.Text));
            Assert.True(bench.StartOne());                           // and the reader never answers

            bench.Wait(series, SpokenSeries.StallMs - 2000);
            Assert.True(series.Owes);
            bench.Wait(series, 3000);
            Assert.False(series.Owes);
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
