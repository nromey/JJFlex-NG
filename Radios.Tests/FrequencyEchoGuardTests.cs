using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;
using G = Radios.FrequencyEchoGuard;

namespace Radios.Tests
{
    /// <summary>
    /// The frequency echo guard's whole state table, cell by cell (#642).
    /// Rebuilt in the 2026-11-02 junk box's second rulings round after the 8600
    /// bench measured that the radio never reports a tune to the client that
    /// sent it (only to every other client): the reply is the receipt, and the
    /// AwaitingReport state, which waited for that report, is gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The states: Idle, Queued (asked, not written), Sent (written, not
    /// answered). The events: a new request; the send of the latest request and
    /// of an older one; a success reply for the latest and for an older one; an
    /// error reply or a local refusal for the latest, for an older one, and for
    /// the latest after a give-up; a changed report equal to the latest request
    /// and one of anything else; and running out of time. FlexLib's cache, which
    /// the owner sets from each success reply, is tested through FlexBase in
    /// TuningReceiptTests.
    /// </para>
    /// </remarks>
    public class FrequencyEchoGuardTests
    {
        private long _now;

        private readonly System.Collections.Generic.List<ulong> _gaveUp = new();

        private readonly System.Collections.Generic.List<string> _gaveUpLines = new();

        private G Guard() => new G("test", () => _now, (hz, line) => { _gaveUp.Add(hz); _gaveUpLines.Add(line); });

        private const ulong Was = 14_200_000;
        private const ulong Asked = 14_200_100;
        private const ulong Other = 14_250_000;

        // ── Getting into each state ──

        private (G g, long id) InQueued()
        {
            var g = Guard();
            long id = g.Requested(Asked);
            Assert.Equal(G.State.Queued, g.Now);
            return (g, id);
        }

        private (G g, long id) InSent()
        {
            var (g, id) = InQueued();
            g.Sending(id);
            Assert.Equal(G.State.Sent, g.Now);
            return (g, id);
        }

        private static void Reads(G g, G.Reading reading, ulong pending = Asked)
        {
            Assert.Equal(reading, g.Read(out ulong p));
            if (reading == G.Reading.Pending) Assert.Equal(pending, p);
        }

        // ── Idle ──

        [Fact]
        public void Idle_reads_the_radio_and_believes_every_report()
        {
            var g = Guard();
            Assert.Equal(G.State.Idle, g.Now);
            Reads(g, G.Reading.Idle);
            Assert.True(g.Accept(Was));
            Assert.True(g.Accept(Other));
            Assert.Equal(G.State.Idle, g.Now);
            Assert.Empty(_gaveUp);
        }

        [Fact]
        public void Idle_ignores_sends_answers_and_refusals_it_has_no_request_for()
        {
            var g = Guard();
            g.Sending(1);
            Assert.False(g.Acknowledged(1));
            Assert.False(g.Refused(1, "late"));
            Assert.Equal(G.State.Idle, g.Now);
        }

        [Fact]
        public void Idle_after_an_answer_ignores_that_requests_late_events()
        {
            var (g, id) = InSent();
            Assert.True(g.Acknowledged(id));
            g.Sending(id);
            Assert.False(g.Acknowledged(id));
            Assert.False(g.Refused(id, "late error"));
            Assert.Equal(G.State.Idle, g.Now);
        }

        [Fact]
        public void Idle_request_becomes_Queued_reading_the_request()
        {
            var g = Guard();
            g.Requested(Asked);
            Assert.Equal(G.State.Queued, g.Now);
            Reads(g, G.Reading.Pending);
        }

        // ── Queued ──

        [Fact]
        public void Queued_new_request_replaces_it()
        {
            var (g, first) = InQueued();
            long second = g.Requested(Other);
            Assert.True(second > first);
            Assert.Equal(G.State.Queued, g.Now);
            Reads(g, G.Reading.Pending, Other);
        }

        [Fact]
        public void Queued_send_of_the_latest_becomes_Sent_and_of_an_older_one_changes_nothing()
        {
            var g = Guard();
            long old = g.Requested(Was);
            long latest = g.Requested(Asked);
            g.Sending(old);
            Assert.Equal(G.State.Queued, g.Now);
            g.Sending(latest);
            Assert.Equal(G.State.Sent, g.Now);
        }

        [Fact]
        public void Queued_reports_only_its_own_value_into_the_caches()
        {
            var (g, _) = InQueued();
            Assert.True(g.Accept(Asked));
            Assert.False(g.Accept(Was));
            Assert.False(g.Accept(Other));
            Assert.Equal(G.State.Queued, g.Now);
            Reads(g, G.Reading.Pending);
        }

        [Fact]
        public void Queued_answers_and_refusals_of_an_older_request_change_nothing()
        {
            var g = Guard();
            long old = g.Requested(Was);
            g.Requested(Asked);
            Assert.False(g.Acknowledged(old));
            Assert.False(g.Refused(old, "older"));
            Assert.Equal(G.State.Queued, g.Now);
        }

        [Fact]
        public void Queued_refusal_of_the_latest_is_Idle()
        {
            var (g, id) = InQueued();
            Assert.True(g.Refused(id, "the slice is locked"));
            Assert.Equal(G.State.Idle, g.Now);
            Reads(g, G.Reading.Idle);
        }

        [Fact]
        public void Queued_success_of_the_latest_is_Idle()
        {
            var (g, id) = InQueued();
            Assert.True(g.Acknowledged(id));
            Assert.Equal(G.State.Idle, g.Now);
        }

        [Fact]
        public void Queued_is_held_past_the_radios_window_and_given_up_on_its_own()
        {
            var (g, _) = InQueued();
            _now += G.GiveUpMs * 3;
            Assert.Equal(G.State.Queued, g.Now);
            _now = G.UnsentGiveUpMs - 1;
            Assert.Equal(G.State.Queued, g.Now);
            Assert.Empty(_gaveUp);
            _now = G.UnsentGiveUpMs;
            Assert.Equal(G.State.Idle, g.Now);
            Reads(g, G.Reading.Idle);
            Assert.Equal(new[] { Asked }, _gaveUp);       // the owner is told, once
        }

        // ── Sent ──

        [Fact]
        public void Sent_new_request_is_Queued_again()
        {
            var (g, _) = InSent();
            g.Requested(Other);
            Assert.Equal(G.State.Queued, g.Now);
            Reads(g, G.Reading.Pending, Other);
        }

        [Fact]
        public void Sent_send_again_or_of_an_older_request_changes_nothing()
        {
            var g = Guard();
            long old = g.Requested(Was);
            long latest = g.Requested(Asked);
            g.Sending(latest);
            _now += 500;
            g.Sending(latest);
            g.Sending(old);
            _now += G.GiveUpMs - 500;                      // the window still counts from the first send
            Assert.Equal(G.State.Idle, g.Now);
        }

        [Fact]
        public void Sent_reports_only_its_own_value_into_the_caches_whatever_the_number()
        {
            var (g, _) = InSent();
            Assert.False(g.Accept(Was));                   // the number it replaced
            Assert.False(g.Accept(Other));                 // made at the radio before our tune ran
            Assert.True(g.Accept(Asked));
            Assert.Equal(G.State.Sent, g.Now);             // no report settles it: only the answer does
            Reads(g, G.Reading.Pending);
        }

        [Fact]
        public void Sent_success_of_the_latest_is_Idle_whatever_was_reported_before_it()
        {
            // The radio never reports our own tune to us (the 8600 bench); its
            // success reply is the receipt. A move reported before the reply was
            // made at the radio before our tune, which overrode it.
            var (g, id) = InSent();
            Assert.False(g.Accept(Other));
            Assert.True(g.Acknowledged(id));
            Assert.Equal(G.State.Idle, g.Now);
            Reads(g, G.Reading.Idle);
        }

        [Fact]
        public void Sent_answers_and_refusals_of_an_older_request_change_nothing()
        {
            var g = Guard();
            long old = g.Requested(Was);
            g.Sending(old);
            long latest = g.Requested(Asked);
            g.Sending(latest);
            Assert.False(g.Acknowledged(old));
            Assert.False(g.Refused(old, "older"));
            Assert.Equal(G.State.Sent, g.Now);
        }

        [Fact]
        public void Sent_refusal_of_the_latest_is_Idle()
        {
            var (g, id) = InSent();
            Assert.True(g.Refused(id, "the radio answered an error"));
            Assert.Equal(G.State.Idle, g.Now);
            Reads(g, G.Reading.Idle);
        }

        [Fact]
        public void Sent_is_given_up_on_exactly_the_window_after_the_send()
        {
            var g = Guard();
            long id = g.Requested(Asked);
            _now = 1190;                                   // the queue sends it late
            g.Sending(id);
            _now = 1190 + G.GiveUpMs - 1;
            Assert.Equal(G.State.Sent, g.Now);
            _now = 1190 + G.GiveUpMs;
            Assert.Equal(G.State.Idle, g.Now);
            Assert.True(g.Accept(Was));                    // and the radio is believed
            Assert.Equal(new[] { Asked }, _gaveUp);
        }

        // ── After a give-up ──

        [Fact]
        public void A_late_refusal_of_the_request_given_up_on_is_reported_once()
        {
            var (g, id) = InSent();
            _now += G.GiveUpMs;
            Assert.Equal(G.State.Idle, g.Now);
            Assert.True(g.Refused(id, "late error"));      // the owner says where the slice is
            Assert.False(g.Refused(id, "again"));
            Assert.Equal(G.State.Idle, g.Now);
        }

        [Fact]
        public void A_late_success_of_the_request_given_up_on_changes_no_state()
        {
            var (g, id) = InSent();
            _now += G.GiveUpMs;
            Assert.False(g.Acknowledged(id));               // the owner still puts it in FlexLib's cache
            Assert.Equal(G.State.Idle, g.Now);
        }

        [Fact]
        public void A_late_refusal_of_an_earlier_request_after_a_newer_one_is_not_reported()
        {
            var (g, id) = InSent();
            _now += G.GiveUpMs;
            Assert.Equal(G.State.Idle, g.Now);
            g.Requested(Other);
            Assert.False(g.Refused(id, "late error"));
        }

        // ── Sequences the old guards got wrong, on the table ──

        [Fact]
        public void A_burst_of_any_length_is_held_and_no_older_value_gets_in()
        {
            var g = Guard();
            long id = 0;
            for (ulong i = 1; i <= 1000; i++) { id = g.Requested(Was + i * 100); g.Sending(id); }
            for (ulong i = 1; i < 1000; i++) Assert.False(g.Accept(Was + i * 100));
            Reads(g, G.Reading.Pending, Was + 1000 * 100);
            Assert.True(g.Acknowledged(id));
        }

        [Fact]
        public void A_give_up_hands_its_trace_line_to_the_owner()
        {
            // Repair round 11: the owner writes the give-up's trace, off any
            // lock the reader that noticed it may hold.
            var (g, _) = InSent();
            _now += G.GiveUpMs;
            Assert.Equal(G.State.Idle, g.Now);
            Assert.Single(_gaveUpLines);
            Assert.Contains("gave up on " + Asked, _gaveUpLines[0]);
        }

        [Fact]
        public void Only_running_out_of_time_calls_the_owner_back()
        {
            var (g, id) = InSent();
            g.Acknowledged(id);
            var (h, hid) = InSent();
            h.Refused(hid, "error");
            Assert.Empty(_gaveUp);
        }

        /// <summary>
        /// The guard is wired: the one request helper arms it for every route,
        /// the send carries the id, the reply reaches it, and the Freq report
        /// asks it. Every test above passes just as well if nothing calls the
        /// guard (#264 was exactly that), so this pins the calls.
        /// </summary>
        [Fact]
        public void FlexBase_arms_the_guard_for_every_tune_and_consults_it()
        {
            string text = FlexBasePartials.JoinedText();
            Assert.Equal(1, CountOf(text, "long id = EchoGuardFor(slice).Requested(value);"));
            Assert.Equal(1, CountOf(text, "t_tuneAdmitted = RequestTune(slice, value, selectedTune: true, \"RXFreq\");"));   // integration fix: the answer is kept
            Assert.Equal(1, CountOf(text, "RequestTune(slice, value, selectedTune: false, \"TXFreq\");"));
            Assert.Equal(1, CountOf(text, "RequestTune(outSlice, ValueOf(inSlice), selectedTune: false, \"CopyVFO\");"));
            // Integration fix: the queue's own answer is used, never MainLoop read apart.
            // Integration fix 2: with its drop callback, which concedes as a refusal does.
            Assert.Equal(1, CountOf(text, "if (q == null || !q.Enqueue((FunctionDel)(() => WriteTuneTo(slice, id, mhz, what)), what,"));
            Assert.Equal(1, CountOf(text, "if (EchoGuardFor(slice).Refused(id, \"the command loop ended before it ran\"))"));
            // J28: unless a typed tune took the refusal, which then says it itself.
            Assert.Equal(1, CountOf(text, "Reconcile(slice, value, typed ? null : \"the command loop ended before it ran\");"));
            Assert.True(text.Contains("bool accepted = EchoGuardFor(s).Accept(echoed);", StringComparison.Ordinal),
                "the slice's guard is never consulted on a Freq report (#266).");
            Assert.False(text.Contains("slice.Freq = mhz", StringComparison.Ordinal),
                "a tune writes FlexLib's Freq setter again, so FlexLib's number is no longer the radio's report (repair round 5).");
            Assert.True(text.Contains("reply => TuneReplied(slice, id, hz, reply)", StringComparison.Ordinal),
                "the tune's reply no longer carries its request (repair round 5).");
            Assert.True(text.Contains("guard.Acknowledged(id);", StringComparison.Ordinal)
                        && text.Contains("if (guard.NewestReply(id)) PublishReply(slice, LongFreqToLibFreq(requestedHz));", StringComparison.Ordinal)
                        && text.Contains("bool closed = guard.Refused(id, \"the radio answered \" + reply);", StringComparison.Ordinal)
                        && text.Contains("if (TryKeptFrequency(reply.Text, out double keptMhz) && guard.NewestReply(id))", StringComparison.Ordinal)
                        && text.Contains("try { slice.AcceptTuneReply(mhz); }", StringComparison.Ordinal),
                "the tune's reply is no longer its receipt (round two of the rulings round, the 8600 bench).");
            Assert.True(text.Contains("== FrequencyEchoGuard.Reading.Pending\r\n                ? pending : LibFreqtoLong(slice.Freq);", StringComparison.Ordinal)
                        || text.Contains("== FrequencyEchoGuard.Reading.Pending\n                ? pending : LibFreqtoLong(slice.Freq);", StringComparison.Ordinal),
                "a slice's value is not the pending request or the radio's report (repair round 8).");
            Assert.False(text.Contains("slc.FrequencyReported += SliceFrequencyReported;", StringComparison.Ordinal),
                "the tuning core waits again for a report of our own tune, which the radio never sends us (the 8600 bench).");
            Assert.True(text.Contains("gaveUp: (requested, line) => q.Enqueue((FunctionDel)(() =>", StringComparison.Ordinal)
                        && text.Contains("Reconcile(s, requested, null);", StringComparison.Ordinal)
                        && text.Contains("}), \"TuneReconcile\")", StringComparison.Ordinal),
                "running out of time no longer brings what is derived with it (repair round 9).");
            Assert.True(CountOf(text, "ObservedOrPending(s, LibFreqtoLong(s.Freq))") >= 3,
                "the ActiveSlice refresh and both found-slice refreshes ask the guard (repair round 3).");
        }

        private static int CountOf(string text, string what)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
            return n;
        }
    }
}
