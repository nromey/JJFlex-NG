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

        [Fact]
        public void After_giving_up_the_radio_wins()
        {
            var g = Guard();
            g.Requested(Was, Asked);
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
            g.Requested(Was, Asked);
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
            g.Requested(Was, Asked);

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

            // One guard per slice since repair round 3 (EchoGuardFor). Both
            // tune setters arm it, and the Freq report and every refresh of a
            // cache from a slice's own number consult it.
            Assert.Equal(2, CountOf(text, "EchoGuardFor(slice).Requested(replaced, value);"));
            Assert.True(text.Contains("bool accepted = EchoGuardFor(s).Accept(echoed);", StringComparison.Ordinal),
                "the slice's guard is never consulted on a Freq report, so every echo goes straight "
                + "into the cache and the guard is decoration. See task #266.");
            Assert.True(text.Contains("if (guard.Accept(reported)) return reported;", StringComparison.Ordinal),
                "a cache refreshed from a slice's own number does not ask its guard (repair round 3).");
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
            g.Requested(Was, Asked);
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
    }
}
