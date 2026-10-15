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
            // This is also the NEGATIVE CONTROL for the three tests below
            // (#698): a hold reset while the controller is still holding polls
            // Quiet for ever. That is what Reset means, and it is exactly what
            // the fault and teardown paths used to do with a hold in flight —
            // which is why they hand the hold off instead.
            var w = new SystemWidePttWatch();
            w.NoteDown(0);
            w.Reset();
            Assert.False(w.Busy);
            Assert.Equal(SystemWidePttWatch.Verdict.Quiet, w.Poll(100_000, physicallyDown: false, controllerHolding: true));
        }

        // ── #698: a hold the hook can no longer release is handed to the watch ──

        [Fact]
        public void A_hold_handed_off_by_a_dying_hook_is_killed_when_the_release_is_not_honoured()
        {
            // The hook faulted (or is being torn down) with the operator still
            // physically holding the key, and the UI thread is blocked, so the
            // posted release is never processed and the controller keeps
            // reporting a held transmit. The watch must reach the direct kill
            // on its own — physically-down or not, because no up edge can
            // ever arrive now.
            var w = new SystemWidePttWatch();
            w.NoteDown(0);
            w.HandOffHold(500);
            Assert.True(w.Busy, "a handed-off hold is still in flight until it is honoured or killed");

            long t = 500;
            var verdict = SystemWidePttWatch.Verdict.Quiet;
            long when = -1;
            while (t < 500 + SystemWidePttWatch.ReleaseGraceMs + 1000)
            {
                verdict = w.Poll(t, physicallyDown: true, controllerHolding: true);
                if (verdict != SystemWidePttWatch.Verdict.Quiet) { when = t; break; }
                t += Poll;
            }
            Assert.Equal(SystemWidePttWatch.Verdict.KillReleaseNotHonoured, verdict);
            Assert.True(when >= 500 + SystemWidePttWatch.ReleaseGraceMs,
                "killed at " + when + ", inside the grace a legitimately-processing release is owed");
            Assert.False(w.Busy, "after the kill nothing is in flight");
        }

        [Fact]
        public void A_hold_handed_off_and_then_honoured_goes_quiet()
        {
            // The healthy case: the UI thread processed the posted release
            // within the grace. Nothing is killed and nothing lingers.
            var w = new SystemWidePttWatch();
            w.NoteDown(0);
            w.HandOffHold(500);
            Assert.Equal(SystemWidePttWatch.Verdict.Quiet, w.Poll(550, physicallyDown: true, controllerHolding: false));
            Assert.False(w.Busy);
            Assert.Equal(SystemWidePttWatch.Verdict.Quiet,
                w.Poll(550 + SystemWidePttWatch.ReleaseGraceMs + 1000, physicallyDown: true, controllerHolding: true));
        }

        [Fact]
        public void Handing_off_with_nothing_held_does_not_restamp_a_release_already_in_flight()
        {
            // Fault hands off at 500; the unhook that follows it a moment
            // later finds nothing held and must not move the clock, or every
            // teardown would buy the UI thread a fresh grace.
            var w = new SystemWidePttWatch();
            w.NoteDown(0);
            w.HandOffHold(500);
            w.HandOffHold(2500);   // the second caller: nothing held now
            Assert.Equal(SystemWidePttWatch.Verdict.KillReleaseNotHonoured,
                w.Poll(500 + SystemWidePttWatch.ReleaseGraceMs, physicallyDown: true, controllerHolding: true));

            var idle = new SystemWidePttWatch();
            idle.HandOffHold(100);
            Assert.False(idle.Busy, "handing off with no hold ever seen leaves nothing in flight");
        }
    }
}
