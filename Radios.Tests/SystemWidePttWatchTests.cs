using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The fail-safe under the system-wide push to talk (#307): a held
    /// transmit nobody is holding must end on its own, and a JAWS user's
    /// genuinely held transmit must not.
    /// </summary>
    public class SystemWidePttWatchTests
    {
        private const int Poll = SystemWidePttWatch.PollMs;

        [Fact]
        public void A_hold_the_operating_system_agrees_with_is_quiet()
        {
            var w = new SystemWidePttWatch();
            w.NoteDown(0);
            for (long t = Poll; t < 10_000; t += Poll)
                Assert.Equal(SystemWidePttWatch.Verdict.Quiet, w.Poll(t, physicallyDown: true, controllerHolding: true));
            Assert.True(w.Busy);
        }

        [Fact]
        public void A_missed_release_is_unkeyed_once_the_key_has_read_up_long_enough()
        {
            // The hook never saw the up edge; Windows says the key is up.
            var w = new SystemWidePttWatch();
            w.NoteDown(0);
            long t = 1000;
            var verdict = SystemWidePttWatch.Verdict.Quiet;
            long when = -1;
            while (t < 1000 + SystemWidePttWatch.MissedReleaseMs + 500)
            {
                verdict = w.Poll(t, physicallyDown: false, controllerHolding: true);
                if (verdict != SystemWidePttWatch.Verdict.Quiet) { when = t; break; }
                t += Poll;
            }
            Assert.Equal(SystemWidePttWatch.Verdict.UnkeyMissedRelease, verdict);
            Assert.True(when >= 1000 + SystemWidePttWatch.MissedReleaseMs,
                "unkeyed at " + when + ", before the key had read up for " + SystemWidePttWatch.MissedReleaseMs + " ms");
        }

        [Fact]
        public void The_jaws_gap_between_synthetic_pairs_does_not_read_as_a_missed_release()
        {
            // #216: under JAWS a held key arrives as pairs ~250 ms apart with
            // the first gap at the Windows repeat delay (~512 ms). Between
            // pairs the operating system may well say "up". That is a hold,
            // not a release, and the absorber bridges it; the watchdog must
            // not undercut the absorber. Twenty seconds of it.
            var w = new SystemWidePttWatch();
            long t = 0;
            w.NoteDown(t);
            long nextDown = 512;
            while (t < 20_000)
            {
                t += Poll;
                if (t >= nextDown)
                {
                    w.NoteDown(t);          // the synthetic re-down
                    nextDown = t + 250;
                }
                bool physicallyDown = t < 5 || (t - (nextDown - 250)) < 10; // down for a blink after each pair
                Assert.Equal(SystemWidePttWatch.Verdict.Quiet, w.Poll(t, physicallyDown, controllerHolding: true));
            }
        }

        [Fact]
        public void A_release_that_was_posted_and_honoured_goes_quiet()
        {
            var w = new SystemWidePttWatch();
            w.NoteDown(0);
            w.NoteUp(2000);
            Assert.True(w.Busy);
            Assert.Equal(SystemWidePttWatch.Verdict.Quiet, w.Poll(2050, physicallyDown: false, controllerHolding: false));
            Assert.False(w.Busy);
        }

        [Fact]
        public void A_release_the_ui_thread_never_honours_kills_the_carrier_after_the_grace()
        {
            var w = new SystemWidePttWatch();
            w.NoteDown(0);
            w.NoteUp(2000);
            long t = 2000;
            var verdict = SystemWidePttWatch.Verdict.Quiet;
            long when = -1;
            while (t < 2000 + SystemWidePttWatch.ReleaseGraceMs + 500)
            {
                t += Poll;
                verdict = w.Poll(t, physicallyDown: false, controllerHolding: true);
                if (verdict != SystemWidePttWatch.Verdict.Quiet) { when = t; break; }
            }
            Assert.Equal(SystemWidePttWatch.Verdict.KillReleaseNotHonoured, verdict);
            Assert.True(when >= 2000 + SystemWidePttWatch.ReleaseGraceMs);
            Assert.False(w.Busy);
        }

        [Fact]
        public void The_grace_outlasts_the_absorbers_longest_deferral_so_a_jaws_release_is_never_killed_mid_corroboration()
        {
            // The absorber may defer a release for its first-gap window and
            // then extend it by every probe recheck it is allowed. The kill
            // must sit beyond all of that, or it would end a JAWS user's
            // transmit while the absorber was still legitimately holding it.
            int longestDeferral = PttHoldFilter.MaxFirstGapDeferMs
                                  + PttHoldFilter.MaxProbeExtensions * PttHoldFilter.ProbeRecheckMs;
            Assert.True(SystemWidePttWatch.ReleaseGraceMs > longestDeferral,
                "grace " + SystemWidePttWatch.ReleaseGraceMs + " ms must exceed the absorber's " + longestDeferral + " ms");
            Assert.Equal(PttHoldFilter.MaxFirstGapDeferMs, SystemWidePttWatch.MissedReleaseMs);
        }

        [Fact]
        public void A_hold_past_the_hard_kill_ceiling_is_killed_whatever_the_key_says()
        {
            var w = new SystemWidePttWatch();
            w.NoteDown(0);
            long justUnder = SystemWidePttWatch.MaxHoldMs - 1;
            Assert.Equal(SystemWidePttWatch.Verdict.Quiet, w.Poll(justUnder, physicallyDown: true, controllerHolding: true));
            Assert.Equal(SystemWidePttWatch.Verdict.KillHoldTooLong,
                w.Poll(SystemWidePttWatch.MaxHoldMs, physicallyDown: true, controllerHolding: true));
            Assert.Equal(PttConfig.HardKillSeconds * 1000, SystemWidePttWatch.MaxHoldMs);
        }

        [Fact]
        public void A_fresh_down_cancels_a_pending_release_check()
        {
            var w = new SystemWidePttWatch();
            w.NoteDown(0);
            w.NoteUp(1000);
            w.NoteDown(1100);   // operator keyed again before the grace ran out
            Assert.Equal(SystemWidePttWatch.Verdict.Quiet, w.Poll(1100 + SystemWidePttWatch.ReleaseGraceMs + 100,
                physicallyDown: true, controllerHolding: true));
        }

        [Fact]
        public void Reset_leaves_nothing_in_flight()
        {
            var w = new SystemWidePttWatch();
            w.NoteDown(0);
            w.Reset();
            Assert.False(w.Busy);
            Assert.Equal(SystemWidePttWatch.Verdict.Quiet, w.Poll(100_000, physicallyDown: false, controllerHolding: true));
        }
    }
}
