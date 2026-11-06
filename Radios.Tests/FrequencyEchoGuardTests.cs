using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The abort rule for #266, tested without a radio.
    /// </summary>
    /// <remarks>
    /// The bug this guards against is remote-only and timing-dependent, which
    /// means the bench cannot reliably produce it and cannot prove its absence
    /// either. So the rule is a pure function over (replaced, requested, echoed,
    /// elapsed), and these are the cases that matter. The give-up path in
    /// particular has no other way of being exercised on purpose — waiting 1.2
    /// seconds for it by hand is exactly the kind of check that quietly stops
    /// being run.
    /// </remarks>
    public class FrequencyEchoGuardTests
    {
        private long _now;

        private FrequencyEchoGuard Guard() => new FrequencyEchoGuard("test", () => _now);

        private const ulong Was = 14_200_000;
        private const ulong Asked = 14_200_100;

        [Fact]
        public void An_unarmed_guard_accepts_everything()
        {
            var g = Guard();
            Assert.True(g.Accept(Was));
            Assert.True(g.Accept(Asked));
            Assert.True(g.Accept(99_999_999));
        }

        [Fact]
        public void The_stale_echo_of_the_value_we_replaced_is_rejected()
        {
            var g = Guard();
            g.Requested(Was, Asked);

            Assert.False(g.Accept(Was));
        }

        [Fact]
        public void Our_own_value_coming_back_is_accepted_and_disarms()
        {
            var g = Guard();
            g.Requested(Was, Asked);

            Assert.True(g.Accept(Asked));

            // Settled. A later report of the old frequency is now real news —
            // somebody tuned back — and must not be swallowed.
            Assert.True(g.Accept(Was));
        }

        [Fact]
        public void A_value_we_never_asked_for_is_accepted_immediately()
        {
            // A band-edge clamp, another MultiFlex client, the front-panel knob.
            // None of these are the stale echo, and the radio is the authority.
            var g = Guard();
            g.Requested(Was, Asked);

            Assert.True(g.Accept(14_350_000));
        }

        [Fact]
        public void A_repeated_press_re_arms_against_the_new_previous_value()
        {
            var g = Guard();
            g.Requested(Was, Asked);
            Assert.False(g.Accept(Was));

            // Second press: what we now stand to lose is Asked, not Was.
            const ulong asked2 = 14_200_200;
            g.Requested(Asked, asked2);

            Assert.False(g.Accept(Asked));
            Assert.True(g.Accept(asked2));
        }

        // The give-up window counts from the SEND since repair round 5 (#642):
        // the four tests below mark the send where they used to start the
        // clock at the request, which is the same claim about the radio's
        // window measured from the moment that window can actually open.

        [Fact]
        public void After_giving_up_the_radio_wins()
        {
            var g = Guard();
            g.Sending(g.Requested(Was, Asked));
            Assert.False(g.Accept(Was));

            _now += FrequencyEchoGuard.GiveUpMs;

            // Our write never arrived. Holding a frequency the radio is not on
            // would tell a blind operator they are somewhere they are not.
            Assert.True(g.Accept(Was));
        }

        [Fact]
        public void Giving_up_disarms_rather_than_expiring_once_per_echo()
        {
            var g = Guard();
            g.Sending(g.Requested(Was, Asked));
            _now += FrequencyEchoGuard.GiveUpMs;
            Assert.True(g.Accept(Was));

            // Back to the clock we started from. If give-up merely returned true
            // without disarming, the guard would rearm itself here and start
            // rejecting a value it has already conceded.
            _now = 0;
            Assert.True(g.Accept(Was));
        }

        [Fact]
        public void One_millisecond_before_giving_up_it_still_holds()
        {
            // The boundary, so that a change to GiveUpMs cannot silently become a
            // change to whether the guard works at all.
            var g = Guard();
            g.Sending(g.Requested(Was, Asked));

            _now += FrequencyEchoGuard.GiveUpMs - 1;
            Assert.False(g.Accept(Was));
        }

        /// <summary>
        /// The guard is actually CALLED, on both sides, for both frequencies.
        /// </summary>
        /// <remarks>
        /// Every test above passes just as happily if nothing in the app ever
        /// constructs this class. That is not hypothetical: #264 in the backlog
        /// is a guard that was built, documented, tested and never wired by any
        /// caller, and it read as finished for weeks. A guard needs both halves —
        /// something must ARM it where we write ahead of the radio, and something
        /// must ASK it before accepting an echo. Either one alone is inert, and
        /// silent about being inert.
        /// </remarks>
        [Fact]
        public void FlexBase_both_arms_the_guard_and_consults_it()
        {
            // Every FlexBase partial, not FlexBase.cs alone.
            string text = FlexBasePartials.JoinedText();

            // One guard per slice since repair round 3 (EchoGuardFor). Since
            // repair round 5 ONE helper owns the whole request, and every
            // route that tunes a slice goes through it: both setters and the
            // slice copy (the copy bypassed it in round 4, Codex's finding 2).
            Assert.Equal(1, CountOf(text, "long id = EchoGuardFor(slice).Requested(replaced, value);"));
            Assert.Equal(1, CountOf(text, "RequestTune(slice, value, selectedTune: true, \"RXFreq\");"));
            Assert.Equal(1, CountOf(text, "RequestTune(slice, value, selectedTune: false, \"TXFreq\");"));
            Assert.Equal(1, CountOf(text, "RequestTune(outSlice, ValueOf(inSlice, _rxCache, _txCache), selectedTune: false, \"CopyVFO\");"));
            Assert.Equal(1, CountOf(text, "q.Enqueue((FunctionDel)(() => WriteTuneTo(slice, id, mhz, what)), what);"));
            // The radio's Freq report asks the guard, and nothing else
            // decides whether it reaches the caches.
            Assert.True(text.Contains("bool accepted = EchoGuardFor(s).Accept(echoed);", StringComparison.Ordinal),
                "the slice's guard is never consulted on a Freq report, so every echo goes straight "
                + "into the cache and the guard is decoration. See task #266.");
            Assert.True(text.Contains("return EchoGuardFor(slice).TryPending(out ulong pending) ? pending : reported;", StringComparison.Ordinal),
                "a cache refreshed from a slice's own number does not ask its guard (repair round 3).");
            Assert.False(text.Contains("guard.Accept(reported)", StringComparison.Ordinal),
                "a refresh from the vendor's number settles the guard (repair round 4).");
            // Repair round 5: the tune goes out through the reply-bearing path
            // with its request's id, never through FlexLib's Freq setter, which
            // would put our value in FlexLib's cache and so make the radio's
            // report of an ordinary success invisible.
            Assert.False(text.Contains("slice.Freq = mhz", StringComparison.Ordinal),
                "a tune writes FlexLib's Freq setter again, so an ordinary success is never confirmed (repair round 5).");
            Assert.True(text.Contains("SendRadioCommandWithReply(slice.Radio, command, reply => TuneReplied(slice, id, reply));", StringComparison.Ordinal),
                "the tune no longer goes through the reply-bearing path with its id (repair round 5).");
            Assert.True(text.Contains("guard.Acknowledged(id, LibFreqtoLong(slice.Freq));", StringComparison.Ordinal)
                        && text.Contains("guard.Refused(id, \"the radio answered \" + reply);", StringComparison.Ordinal),
                "the tune's reply does not reach its own request's guard (repair round 5).");
            Assert.True(CountOf(text, "ObservedOrPending(s, LibFreqtoLong(s.Freq))") >= 3,
                "the ActiveSlice refresh and both found-slice refreshes ask the guard (repair round 3).");
        }

        private static int CountOf(string text, string what)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
            return n;
        }

        /// <summary>
        /// Repair round 3: a held key's chain. Each queued write's own report
        /// of ITS value arrives while a newer one is pending; every one of them
        /// is stale, not only the last write's predecessor.
        /// </summary>
        [Fact]
        public void Every_value_a_run_of_steps_superseded_is_rejected_until_the_last_arrives()
        {
            var g = Guard();
            g.Requested(Was, Asked);
            g.Requested(Asked, 14_200_200);
            g.Requested(14_200_200, 14_200_300);

            Assert.False(g.Accept(Was));
            Assert.False(g.Accept(Asked));
            Assert.False(g.Accept(14_200_200));
            Assert.True(g.TryPending(out ulong pending));
            Assert.Equal(14_200_300UL, pending);
            Assert.True(g.Accept(14_200_300));
            Assert.False(g.TryPending(out _));
            Assert.True(g.Accept(Was));        // settled: the old number is news again
        }

        [Fact]
        public void A_value_outside_the_chain_is_still_believed_at_once()
        {
            var g = Guard();
            g.Requested(Was, Asked);
            g.Requested(Asked, 14_200_200);
            Assert.True(g.Accept(14_350_000));
            Assert.True(g.Accept(Asked));      // disarmed by the foreign report
        }

        [Fact]
        public void A_chain_started_after_giving_up_forgets_the_old_one()
        {
            var g = Guard();
            g.Sending(g.Requested(Was, Asked));
            _now += FrequencyEchoGuard.GiveUpMs;
            g.Requested(14_300_000, 14_300_100);
            Assert.True(g.Accept(Was));
        }

        [Fact]
        public void Stepping_back_onto_an_earlier_value_settles_on_it()
        {
            var g = Guard();
            g.Requested(Was, Asked);
            g.Requested(Asked, Was);           // back down
            Assert.False(g.Accept(Asked));
            Assert.True(g.Accept(Was));
        }

        [Fact]
        public void Writing_the_frequency_it_is_already_on_guards_nothing()
        {
            // replaced == requested, so there is no stale value to distinguish
            // and the echo confirming it must land.
            var g = Guard();
            g.Requested(Was, Was);

            Assert.True(g.Accept(Was));
        }
        // ── Repair round 5 (#642): requests carry ids; replies and refusals ──

        [Fact]
        public void A_request_still_in_our_queue_is_held_past_the_radios_window()
        {
            var g = Guard();
            g.Requested(Was, Asked);
            _now += FrequencyEchoGuard.GiveUpMs * 3;
            Assert.False(g.Accept(Was));
            Assert.Equal(FrequencyEchoGuard.Reading.Pending, g.Read(out ulong p));
            Assert.Equal(Asked, p);
        }

        [Fact]
        public void A_request_never_sent_is_given_up_on_in_the_end()
        {
            var g = Guard();
            g.Requested(Was, Asked);
            _now += FrequencyEchoGuard.UnsentGiveUpMs;
            Assert.Equal(FrequencyEchoGuard.Reading.Conceded, g.Read(out _));
            Assert.True(g.Accept(Was));
            Assert.Equal(FrequencyEchoGuard.Reading.Idle, g.Read(out _));
        }

        [Fact]
        public void The_window_counts_from_the_send_not_the_request()
        {
            var g = Guard();
            long id = g.Requested(Was, Asked);
            _now += 1190;
            g.Sending(id);
            _now += 10;                                    // 1200 after the request, 10 after the send
            Assert.Equal(FrequencyEchoGuard.Reading.Pending, g.Read(out _));
            Assert.False(g.Accept(Was));
            _now += FrequencyEchoGuard.GiveUpMs - 10;
            Assert.Equal(FrequencyEchoGuard.Reading.Conceded, g.Read(out _));
        }

        [Fact]
        public void A_refused_request_concedes_at_once_and_a_newer_one_arms_again()
        {
            var g = Guard();
            long id = g.Requested(Was, Asked);
            Assert.True(g.Refused(id, "locked"));
            Assert.Equal(FrequencyEchoGuard.Reading.Conceded, g.Read(out _));
            Assert.True(g.Accept(Was));                    // the radio's report is believed

            long id2 = g.Requested(Was, 14_200_200);
            Assert.Equal(FrequencyEchoGuard.Reading.Pending, g.Read(out ulong p));
            Assert.Equal(14_200_200UL, p);
            Assert.False(g.Refused(id, "late"));           // the old refusal says nothing about the new request
            Assert.Equal(FrequencyEchoGuard.Reading.Pending, g.Read(out _));
            Assert.True(g.Refused(id2, "gone"));
        }

        [Fact]
        public void A_refused_request_does_not_settle_on_its_own_value()
        {
            var g = Guard();
            long id = g.Requested(Was, Asked);
            g.Refused(id, "the radio answered an error");
            Assert.True(g.Accept(Asked));                  // believed, as any report is once conceded
            Assert.Equal(FrequencyEchoGuard.Reading.Idle, g.Read(out _));
        }

        [Fact]
        public void The_reply_confirms_only_when_there_is_nothing_left_to_report()
        {
            var g = Guard();
            long id = g.Requested(Was, Asked);
            g.Sending(id);
            g.Acknowledged(id, Was);                       // the radio's report of the change is still to come
            Assert.Equal(FrequencyEchoGuard.Reading.Pending, g.Read(out _));
            Assert.False(g.Accept(Was));
            g.Acknowledged(id, Asked);                     // the radio already reports what we asked for
            Assert.Equal(FrequencyEchoGuard.Reading.Idle, g.Read(out _));
            Assert.True(g.Accept(Was));                    // so an old number is news again
        }

        [Fact]
        public void A_reply_to_an_older_request_settles_nothing()
        {
            var g = Guard();
            long first = g.Requested(Was, Asked);
            g.Sending(first);
            long second = g.Requested(Asked, Was);         // back to where the radio still is
            g.Acknowledged(first, Was);                    // equals the newer request, but answers the older one
            Assert.Equal(FrequencyEchoGuard.Reading.Pending, g.Read(out _));
            Assert.False(g.Accept(Asked));                 // the older one's report is still stale
            g.Sending(second);
            g.Acknowledged(second, Was);
            Assert.Equal(FrequencyEchoGuard.Reading.Idle, g.Read(out _));
        }

        [Fact]
        public void An_older_request_being_sent_does_not_start_the_newer_ones_window()
        {
            var g = Guard();
            long first = g.Requested(Was, Asked);
            g.Requested(Asked, 14_200_200);
            g.Sending(first);
            _now += FrequencyEchoGuard.GiveUpMs;           // well past the older send
            Assert.Equal(FrequencyEchoGuard.Reading.Pending, g.Read(out _));
        }
    }
}
