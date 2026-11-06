using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;
using G = Radios.FrequencyEchoGuard;

namespace Radios.Tests
{
    /// <summary>
    /// The frequency echo guard's whole state table, cell by cell (#642,
    /// junk box repair round 8). Rounds 3 to 7 tested the guard by the
    /// schedules each re-check found, and every re-check found a state they
    /// had not reached. So the guard was rebuilt as four states and these tests
    /// visit every state against every event, with an injected clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The states: Idle, Queued (asked, not written), Sent (written, not
    /// answered), AwaitingReport (answered with success while the radio's
    /// report is not our value and nothing has been reported since the write).
    /// </para>
    /// <para>
    /// Repair round 9 split "a report" in two, because FlexLib raises its Freq
    /// notification only for a CHANGED number: <c>Accept</c> (a changed value,
    /// may it enter the caches?) changes no state, and <c>Reported</c> (a
    /// report ARRIVED, changed or not, from FlexLib's patched
    /// <c>Slice.FrequencyReported</c>) is the event the state machine moves on.
    /// Running out of time now also calls the owner back.
    /// </para>
    /// <para>
    /// The events: a new request; the send of the latest request and of an
    /// older one; a success reply for the latest (the radio reporting our
    /// value; another value after a report since the write; another value
    /// with none) and for an older one; an error reply or a local refusal for
    /// the latest and for an older one; a report equal to the latest request
    /// and one of anything else; and running out of time. A slice's removal
    /// takes its guard with it, and the send of a removed slice is a local
    /// refusal; those are tested through FlexBase in TuningReceiptTests.
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

        private (G g, long id) InAwaitingReport()
        {
            var (g, id) = InSent();
            Assert.False(g.Acknowledged(id, Was));         // the radio still reports the old number, nothing since
            Assert.Equal(G.State.AwaitingReport, g.Now);
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
            Assert.False(g.Reported(Other, out _));
            Assert.Equal(G.State.Idle, g.Now);
            Assert.Empty(_gaveUp);
        }

        [Fact]
        public void Idle_ignores_sends_answers_and_refusals_it_has_no_request_for()
        {
            var g = Guard();
            g.Sending(1);
            Assert.False(g.Acknowledged(1, Asked));
            Assert.False(g.Refused(1, "late"));
            Assert.Equal(G.State.Idle, g.Now);
        }

        [Fact]
        public void Idle_after_an_answer_ignores_that_request_s_late_events()
        {
            var (g, id) = InSent();
            Assert.True(g.Acknowledged(id, Asked));
            g.Sending(id);
            Assert.False(g.Acknowledged(id, Asked));
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
            Assert.False(g.Reported(Other, out _));        // arrived, but before the write: no change
            Assert.Equal(G.State.Queued, g.Now);
            Reads(g, G.Reading.Pending);
        }

        [Fact]
        public void Queued_answers_and_refusals_of_an_older_request_change_nothing()
        {
            var g = Guard();
            long old = g.Requested(Was);
            g.Requested(Asked);
            Assert.False(g.Acknowledged(old, Asked));
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
        public void Queued_success_of_the_latest_is_judged_as_if_sent()
        {
            var (g, id) = InQueued();
            Assert.True(g.Acknowledged(id, Asked));
            Assert.Equal(G.State.Idle, g.Now);

            var (h, id2) = InQueued();
            Assert.False(h.Acknowledged(id2, Was));
            Assert.Equal(G.State.AwaitingReport, h.Now);
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
            Assert.False(g.Accept(Other));                 // a clamp, the knob, another client
            Assert.True(g.Accept(Asked));                  // its own value
            Assert.False(g.Reported(Asked, out _));        // arrived: noted, nothing ends
            Assert.Equal(G.State.Sent, g.Now);             // no report settles it: only the answer does
            Reads(g, G.Reading.Pending);
        }

        [Fact]
        public void Sent_success_with_the_radio_reporting_our_value_is_Idle()
        {
            var (g, id) = InSent();
            Assert.True(g.Acknowledged(id, Asked));
            Assert.Equal(G.State.Idle, g.Now);
        }

        [Fact]
        public void Sent_success_after_a_report_since_the_write_is_Idle_whatever_the_number()
        {
            var (g, id) = InSent();
            Assert.False(g.Accept(Other));                 // kept out of the reading,
            Assert.False(g.Reported(Other, out _));        // but it came
            Assert.True(g.Acknowledged(id, Other));
            Assert.Equal(G.State.Idle, g.Now);
            Reads(g, G.Reading.Idle);
        }

        [Fact]
        public void Sent_success_with_nothing_reported_and_another_number_awaits_the_report()
        {
            var (g, id) = InSent();
            Assert.False(g.Acknowledged(id, Was));
            Assert.Equal(G.State.AwaitingReport, g.Now);
            Reads(g, G.Reading.Pending);
        }

        [Fact]
        public void Sent_a_report_before_the_send_does_not_count_as_one_since()
        {
            var (g, id) = InQueued();
            Assert.False(g.Accept(Other));                 // while still queued
            Assert.False(g.Reported(Other, out _));
            g.Sending(id);
            Assert.False(g.Acknowledged(id, Other));
            Assert.Equal(G.State.AwaitingReport, g.Now);
        }

        [Fact]
        public void Sent_answers_and_refusals_of_an_older_request_change_nothing()
        {
            var g = Guard();
            long old = g.Requested(Was);
            g.Sending(old);
            long latest = g.Requested(Asked);
            g.Sending(latest);
            Assert.False(g.Acknowledged(old, Asked));
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

        // ── AwaitingReport ──

        [Fact]
        public void AwaitingReport_any_report_ends_it_and_is_believed()
        {
            var (g, _) = InAwaitingReport();
            Assert.True(g.Accept(Other));                  // a changed value may enter the caches,
            Assert.Equal(G.State.AwaitingReport, g.Now);   // but only its arrival ends the wait
            Assert.True(g.Reported(Other, out ulong asked));
            Assert.Equal(Asked, asked);
            Assert.Equal(G.State.Idle, g.Now);

            var (h, _) = InAwaitingReport();
            Assert.True(h.Reported(Was, out _));           // an UNCHANGED report: no Accept at all
            Assert.Equal(G.State.Idle, h.Now);
            Assert.False(h.Reported(Was, out _));          // once
        }

        [Fact]
        public void AwaitingReport_new_request_is_Queued_again()
        {
            var (g, _) = InAwaitingReport();
            g.Requested(Other);
            Assert.Equal(G.State.Queued, g.Now);
        }

        [Fact]
        public void AwaitingReport_sends_and_answers_change_nothing()
        {
            var (g, id) = InAwaitingReport();
            g.Sending(id);
            Assert.False(g.Acknowledged(id, Asked));
            Assert.Equal(G.State.AwaitingReport, g.Now);
        }

        [Fact]
        public void AwaitingReport_refusal_of_the_latest_is_Idle_and_of_an_older_one_nothing()
        {
            var (g, id) = InAwaitingReport();
            Assert.False(g.Refused(id - 1, "older"));
            Assert.Equal(G.State.AwaitingReport, g.Now);
            Assert.True(g.Refused(id, "late error"));
            Assert.Equal(G.State.Idle, g.Now);
        }

        [Fact]
        public void AwaitingReport_is_given_up_on_the_window_after_the_send()
        {
            var (g, _) = InAwaitingReport();
            _now += G.GiveUpMs - 1;
            Assert.Equal(G.State.AwaitingReport, g.Now);
            _now += 1;
            Assert.Equal(G.State.Idle, g.Now);
            Assert.Equal(new[] { Asked }, _gaveUp);
        }

        // ── Sequences the old guard got wrong, on the table ──

        [Fact]
        public void A_burst_of_any_length_is_held_and_no_older_value_gets_in()
        {
            var g = Guard();
            long id = 0;
            for (ulong i = 1; i <= 1000; i++) { id = g.Requested(Was + i * 100); g.Sending(id); }
            for (ulong i = 1; i < 1000; i++) Assert.False(g.Accept(Was + i * 100));
            Reads(g, G.Reading.Pending, Was + 1000 * 100);
            Assert.True(g.Acknowledged(id, Was + 1000 * 100));
        }

        [Fact]
        public void A_genuine_move_before_the_answer_is_read_from_the_radio_after_it()
        {
            var g = Guard();
            long id = g.Requested(Asked);
            g.Sending(id);
            Assert.True(g.Accept(Asked));                  // our own report
            g.Reported(Asked, out _);
            Assert.False(g.Accept(Other));                 // a genuine move, kept out of the caches for now
            g.Reported(Other, out _);
            Assert.True(g.Acknowledged(id, Other));        // the radio's report is Other
            Reads(g, G.Reading.Idle);                      // and the reader reads the radio
        }

        [Fact]
        public void An_unchanged_report_after_the_write_counts_as_one()
        {
            // Codex's eighth re-check, blocker 1, on the table: the radio
            // reports a number equal to FlexLib's cache, so no Accept happens,
            // only Reported. The answer then closes the window.
            var (g, id) = InSent();
            Assert.False(g.Reported(Was, out _));
            Assert.True(g.Acknowledged(id, Was));
            Assert.Equal(G.State.Idle, g.Now);
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
            g.Acknowledged(id, Asked);
            var (h, hid) = InSent();
            h.Refused(hid, "error");
            var (k, _) = InAwaitingReport();
            k.Reported(Was, out _);
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
            Assert.Equal(1, CountOf(text, "RequestTune(slice, value, selectedTune: true, \"RXFreq\");"));
            Assert.Equal(1, CountOf(text, "RequestTune(slice, value, selectedTune: false, \"TXFreq\");"));
            Assert.Equal(1, CountOf(text, "RequestTune(outSlice, ValueOf(inSlice), selectedTune: false, \"CopyVFO\");"));
            Assert.Equal(1, CountOf(text, "q.Enqueue((FunctionDel)(() => WriteTuneTo(slice, id, mhz, what)), what);"));
            Assert.True(text.Contains("bool accepted = EchoGuardFor(s).Accept(echoed);", StringComparison.Ordinal),
                "the slice's guard is never consulted on a Freq report (#266).");
            Assert.False(text.Contains("slice.Freq = mhz", StringComparison.Ordinal),
                "a tune writes FlexLib's Freq setter again, so FlexLib's number is no longer the radio's report (repair round 5).");
            Assert.True(text.Contains("reply => TuneReplied(slice, id, hz, reply)", StringComparison.Ordinal),
                "the tune's reply no longer carries its request (repair round 5).");
            Assert.True(text.Contains("closed = guard.Acknowledged(id, LibFreqtoLong(slice.Freq));", StringComparison.Ordinal)
                        && text.Contains("closed = guard.Refused(id, \"the radio answered \" + reply);", StringComparison.Ordinal),
                "the tune's reply does not reach its own request's guard.");
            Assert.True(text.Contains("== FrequencyEchoGuard.Reading.Pending\r\n                ? pending : LibFreqtoLong(slice.Freq);", StringComparison.Ordinal)
                        || text.Contains("== FrequencyEchoGuard.Reading.Pending\n                ? pending : LibFreqtoLong(slice.Freq);", StringComparison.Ordinal),
                "a slice's value is not the pending request or the radio's report (repair round 8).");
            Assert.True(text.Contains("slc.FrequencyReported += SliceFrequencyReported;", StringComparison.Ordinal)
                        && text.Contains("if (EchoGuardFor(slice).Reported(LibFreqtoLong(mhz), out ulong requested) && !changed)", StringComparison.Ordinal),
                "every report's arrival no longer reaches the guard (repair round 9).");
            Assert.True(text.Contains("gaveUp: (requested, line) => q.Enqueue((FunctionDel)(() =>", StringComparison.Ordinal)
                        && text.Contains("Reconcile(s, requested);\r\n                }), \"TuneReconcile\")", StringComparison.Ordinal)
                           | text.Contains("Reconcile(s, requested);\n                }), \"TuneReconcile\")", StringComparison.Ordinal),
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
