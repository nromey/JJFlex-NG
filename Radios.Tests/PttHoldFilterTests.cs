using System;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The synthetic-release absorber that keeps a held PTT keyed under a
    /// screen reader that does not deliver held keys (#216).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The JAWS timeline replayed here is the MEASURED one, not an invention:
    /// Freight Fate's key probe on Noel's machine, 2026-08-24 — the first
    /// synthetic pair at the Windows repeat delay (~512 ms), later pairs
    /// roughly 250 ms apart (242-272), each pair's up a few milliseconds
    /// after its down. What the filter sees is what MainWindow forwards:
    /// alternating down/up, because a pair's down that lands while the key is
    /// already considered down is swallowed by the <c>_pttKeyDown</c> guard.
    /// </para>
    /// <para>
    /// The NVDA cases are the negative controls, and they are the contract
    /// that matters most day to day: Noel runs NVDA, and under a reader that
    /// delivers real holds the filter must change NOTHING — not a millisecond
    /// of release latency.
    /// </para>
    /// </remarks>
    public class PttHoldFilterTests
    {
        // ── NVDA: real holds pass through untouched ─────────────────────

        [Fact]
        public void A_real_hold_releases_the_instant_the_key_comes_up()
        {
            var f = new PttHoldFilter();

            Assert.Equal(PttHoldFilter.DownAction.Press, f.NoteDown(0));
            Assert.Equal(PttHoldFilter.UpAction.ReleaseNow, f.NoteUp(3000));
            Assert.False(f.SynthesisDetected);
            Assert.Equal(0, f.SyntheticReleaseCount);
        }

        [Fact]
        public void A_quick_human_tap_is_not_mistaken_for_synthesis()
        {
            // 80 ms is about as fast as a human deliberately taps a chord.
            var f = new PttHoldFilter();

            f.NoteDown(0);
            Assert.Equal(PttHoldFilter.UpAction.ReleaseNow, f.NoteUp(80));
            Assert.False(f.SynthesisDetected);
        }

        [Fact]
        public void Repeated_real_holds_never_arm_the_filter()
        {
            var f = new PttHoldFilter();
            long t = 0;
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(PttHoldFilter.DownAction.Press, f.NoteDown(t));
                Assert.Equal(PttHoldFilter.UpAction.ReleaseNow, f.NoteUp(t + 2000));
                t += 2500;
            }
            Assert.False(f.SynthesisDetected);
        }

        // ── JAWS: the measured stream ───────────────────────────────────

        [Fact]
        public void The_measured_jaws_stream_arms_on_the_second_pair()
        {
            var f = new PttHoldFilter();

            f.NoteDown(0);                                     // physical press
            // First synthetic up measures from the ORIGINAL down, so it looks
            // like a plausible half-second press — this is the one chop a
            // synthesising session pays, once, before detection is possible.
            Assert.Equal(PttHoldFilter.UpAction.ReleaseNow, f.NoteUp(517));
            Assert.Equal(PttHoldFilter.DownAction.Press, f.NoteDown(762));
            // Second pair: up five milliseconds after its down. No human.
            Assert.Equal(PttHoldFilter.UpAction.DeferRelease, f.NoteUp(767));
            Assert.True(f.SynthesisDetected);
            Assert.Equal(1, f.SyntheticReleaseCount);
        }

        [Fact]
        public void Once_armed_the_queue_of_taps_reads_as_one_continuous_hold()
        {
            var f = new PttHoldFilter();

            // First press of the session, through arming (as above).
            f.NoteDown(0);
            f.NoteUp(517);
            f.NoteDown(762);
            Assert.Equal(PttHoldFilter.UpAction.DeferRelease, f.NoteUp(767));

            // From here the pairs march at ~250 ms. Every down must CONTINUE
            // the hold (no re-key, no unkey), every up must defer.
            long t = 1012;
            for (int i = 0; i < 8; i++)
            {
                Assert.Equal(PttHoldFilter.DownAction.ContinueHold, f.NoteDown(t));
                Assert.Equal(PttHoldFilter.UpAction.DeferRelease, f.NoteUp(t + 5));
                t += 250;
            }

            // The operator lets go: no more downs. The deferral runs to
            // ground and the release really happens.
            Assert.True(f.DeferralElapsed(t + f.DeferMs));
        }

        [Fact]
        public void The_second_hold_of_an_armed_session_is_continuous_from_its_first_millisecond()
        {
            var f = ArmedFilter();

            // Fresh press. Its first synthetic up STILL looks plausible
            // (measured from the press's own down) — which is exactly why an
            // armed filter defers even plausible ups.
            Assert.Equal(PttHoldFilter.DownAction.Press, f.NoteDown(10_000));
            Assert.Equal(PttHoldFilter.UpAction.DeferRelease, f.NoteUp(10_512));
            Assert.Equal(PttHoldFilter.DownAction.ContinueHold, f.NoteDown(10_762));
            Assert.Equal(PttHoldFilter.UpAction.DeferRelease, f.NoteUp(10_767));

            // Not one release happened anywhere in that hold.
            Assert.True(f.DeferralElapsed(10_767 + f.DeferMs));
        }

        [Fact]
        public void A_down_inside_the_window_cancels_the_pending_release()
        {
            var f = ArmedFilter();

            f.NoteDown(20_000);
            f.NoteUp(20_512);                       // deferred
            f.NoteDown(20_762);                     // claimed as synthetic

            // The host timer fires anyway (it was started at the up). The
            // filter must say NO — the release was consumed.
            Assert.False(f.DeferralElapsed(20_512 + f.DeferMs));
        }

        // ── Learning ────────────────────────────────────────────────────

        [Fact]
        public void The_pair_spacing_is_learned_not_hardcoded()
        {
            var f = ArmedFilter();
            int before = f.DeferMs;

            // Slow synthesis: 400 ms between an up and its re-down.
            f.NoteDown(30_000);
            f.NoteUp(30_512);
            f.NoteDown(30_912);   // 400 ms after the up — learned
            f.NoteUp(30_917);

            Assert.True(f.DeferMs >= 600,
                $"400 ms spacing should size the deferral to at least 600 (spacing plus grace), got {f.DeferMs} (was {before})");
        }

        [Fact]
        public void The_deferral_never_exceeds_its_ceiling()
        {
            var f = ArmedFilter();

            // Pathologically slow pairs, 900 ms apart, several times over.
            long t = 40_000;
            for (int i = 0; i < 4; i++)
            {
                f.NoteDown(t);
                f.NoteUp(t + 5);       // implausible: pending release
                t += 900;              // next down 895 ms after the up
            }

            Assert.True(f.DeferMs <= PttHoldFilter.MaxDeferMs,
                $"deferral must stay bounded — a deferred release IS extra carrier. Got {f.DeferMs}");
        }

        // ── The measured regression: one window doing two jobs ──────────
        //
        // These are the tests that would have caught what shipped. The filter
        // armed, absorbed, and still let the radio key and unkey three times on
        // one held press (JJFlexRadioTrace-20260826-181039.txt, two episodes,
        // both opening at exactly 353 ms — our own window, not a JAWS number).
        // A held key produces gaps of two different sizes and the first version
        // kept one number for both, so the repeat cadence trained the window
        // down below the repeat delay and every press chopped at its start.

        [Fact]
        public void The_repeat_cadence_can_never_shorten_the_window_that_bridges_the_repeat_delay()
        {
            var f = ArmedFilter();
            f.SetKeyRepeatDelay(500);

            // A long hold, of the kind that filled the old eight-slot history
            // with ~250 ms repeat gaps and dragged the single window down.
            f.NoteDown(70_000);
            f.NoteUp(70_512);
            long t = 70_762;
            for (int i = 0; i < 12; i++)
            {
                f.NoteDown(t);
                f.NoteUp(t + 5);
                t += 250;
            }
            f.DeferralElapsed(t + f.DeferMs);   // the operator lets go

            Assert.True(f.RepeatGapDeferMs < 500,
                $"the repeat cadence really is the short number ({f.RepeatGapDeferMs} ms) — that is not the bug");
            Assert.True(f.FirstGapDeferMs >= 500,
                "and it must not have dragged the first-gap window down with it; that is the bug. "
                + $"FirstGapDeferMs is {f.FirstGapDeferMs} ms after a hold of 250 ms pairs");
        }

        [Fact]
        public void The_next_press_after_a_long_hold_is_not_chopped_at_its_start()
        {
            var f = ArmedFilter();
            f.SetKeyRepeatDelay(500);

            // Train it exactly as a real transmission does.
            f.NoteDown(80_000);
            f.NoteUp(80_512);
            long t = 80_762;
            for (int i = 0; i < 12; i++) { f.NoteDown(t); f.NoteUp(t + 5); t += 250; }
            Assert.True(f.DeferralElapsed(t + f.DeferMs));

            // Now the press that used to chop. Down, an immediate synthetic up
            // (the shape measured on Noel's machine — up 3 ms after down), and
            // the next synthetic down at the repeat delay.
            long press = 90_000;
            long up = press + 3;
            long reDown = press + 501;      // the measured gap, 312397 → 312898
            Assert.Equal(PttHoldFilter.DownAction.Press, f.NoteDown(press));
            Assert.Equal(PttHoldFilter.UpAction.DeferRelease, f.NoteUp(up));

            // The window the host arms at that up is the whole question. The
            // shipped filter armed 350 ms, the timer ran out at press+353, and
            // the radio unkeyed 148 ms before the reader's next down arrived.
            // Nothing here is asserted about when DeferralElapsed is CALLED —
            // the filter is host-clocked and would release whenever it is
            // asked. What must be true is that the host is not asked yet.
            int window = f.DeferMs;
            Assert.True(up + window > reDown,
                $"the window armed at the up ({window} ms) has to outlast the gap to the reader's "
                + $"next down ({reDown - up} ms). 350 ms did not, and that is what unkeyed a held "
                + "transmitter three times on one press.");

            Assert.Equal(PttHoldFilter.DownAction.ContinueHold, f.NoteDown(reDown));
            Assert.False(f.DeferralElapsed(reDown + 1),
                "and the pending release was consumed, so a late timer tick cannot unkey either");
        }

        [Fact]
        public void The_window_is_taken_from_this_machines_repeat_delay_not_from_a_measurement()
        {
            // The whole point: the number comes from the operator's Windows
            // setting, so a machine set to the slow end is not quietly broken.
            var slow = new PttHoldFilter();
            slow.SetKeyRepeatDelay(1000);

            var fast = new PttHoldFilter();
            fast.SetKeyRepeatDelay(250);

            Assert.True(slow.FirstGapDeferMs > fast.FirstGapDeferMs,
                "a longer repeat delay needs a longer bridge — that is the mechanism, not a preference");
            Assert.True(slow.FirstGapDeferMs >= 1000,
                $"a 1000 ms repeat delay must be bridgeable; got {slow.FirstGapDeferMs}");
            Assert.True(slow.FirstGapDeferMs <= PttHoldFilter.MaxFirstGapDeferMs);
            Assert.True(fast.FirstGapDeferMs >= PttHoldFilter.DefaultDeferMs,
                "and the floor still holds at the fast end");
        }

        [Fact]
        public void A_press_shorter_than_any_possible_repeat_releases_at_once()
        {
            // A tap that ended before this machine could have begun repeating
            // cannot have been synthesised, so the operator does not pay the
            // long window for it. Mechanism, not tuning.
            var f = ArmedFilter();
            f.SetKeyRepeatDelay(500);

            f.NoteDown(100_000);
            Assert.Equal(PttHoldFilter.UpAction.ReleaseNow, f.NoteUp(100_120));
        }

        [Fact]
        public void A_press_that_reaches_the_repeat_delay_still_defers()
        {
            // The first synthetic up of a held press measures from the press's
            // own down, so it looks like a plausible human release. It lands
            // near the repeat delay, and it must stay on the deferring side.
            var f = ArmedFilter();
            f.SetKeyRepeatDelay(500);

            f.NoteDown(110_000);
            Assert.Equal(PttHoldFilter.UpAction.DeferRelease, f.NoteUp(110_512));
        }

        [Fact]
        public void A_reader_slower_than_even_the_repeat_delay_window_degrades_but_stays_bounded()
        {
            var f = ArmedFilter();
            f.SetKeyRepeatDelay(250);   // fast repeat setting: a 375 ms bridge

            // A reader whose spacing exceeds even that. The cycle releases, and
            // the re-down that follows teaches the filter the real spacing.
            f.NoteDown(120_000);
            Assert.Equal(PttHoldFilter.UpAction.DeferRelease, f.NoteUp(120_005));
            Assert.True(f.DeferralElapsed(120_005 + f.DeferMs));

            Assert.Equal(PttHoldFilter.DownAction.Press, f.NoteDown(120_605));
            Assert.True(f.RepeatGapDeferMs >= 600,
                $"the miss should have taught the filter this reader's spacing; got {f.RepeatGapDeferMs}");
            Assert.True(f.RepeatGapDeferMs <= PttHoldFilter.MaxDeferMs,
                "a deferred release IS extra carrier and stays bounded");
        }

        // ── Corroborating the timer against the operating system ────────

        [Fact]
        public void A_key_windows_still_calls_down_extends_the_hold_instead_of_unkeying()
        {
            var f = ArmedFilter();
            f.PhysicalKeyDown = () => true;

            f.NoteDown(130_000);
            f.NoteUp(130_005);

            Assert.False(f.DeferralElapsed(130_005 + f.DeferMs),
                "the reader said released; Windows says the key is down. The operator is talking.");
            Assert.True(f.ReleasePending, "the host must re-arm and ask again, not forget the release");
            Assert.True(f.LastProbeSaidDown);
        }

        [Fact]
        public void A_probe_stuck_at_down_cannot_hold_the_transmitter_open()
        {
            // The probe can only ever EXTEND, so a probe that lies must cost a
            // bounded amount of carrier and then get out of the way. This is RF,
            // not an announcement.
            var f = ArmedFilter();
            f.PhysicalKeyDown = () => true;

            f.NoteDown(140_000);
            f.NoteUp(140_005);

            long t = 140_005 + f.DeferMs;
            for (int i = 0; i < PttHoldFilter.MaxProbeExtensions; i++)
            {
                Assert.False(f.DeferralElapsed(t));
                t += f.NextRecheckMs;
            }

            Assert.True(f.DeferralElapsed(t), "the extensions are bounded and the release happens");
            Assert.True(PttHoldFilter.MaxProbeExtensions * PttHoldFilter.ProbeRecheckMs <= 1000,
                "a lying probe must cost well under a second of carrier");
        }

        [Fact]
        public void A_key_windows_calls_up_releases_exactly_as_before()
        {
            var f = ArmedFilter();
            f.PhysicalKeyDown = () => false;

            f.NoteDown(150_000);
            f.NoteUp(150_005);

            Assert.True(f.DeferralElapsed(150_005 + f.DeferMs));
            Assert.False(f.LastProbeSaidDown);
        }

        [Fact]
        public void A_probe_that_throws_is_treated_as_no_answer_and_never_holds_the_key()
        {
            var f = ArmedFilter();
            f.PhysicalKeyDown = () => throw new InvalidOperationException("probe unavailable");

            f.NoteDown(160_000);
            f.NoteUp(160_005);

            Assert.True(f.DeferralElapsed(160_005 + f.DeferMs));
        }

        [Fact]
        public void With_no_probe_at_all_the_timer_decides_exactly_as_it_used_to()
        {
            var f = ArmedFilter();
            Assert.Null(f.PhysicalKeyDown);

            f.NoteDown(170_000);
            f.NoteUp(170_005);

            Assert.True(f.DeferralElapsed(170_005 + f.DeferMs));
            Assert.Null(f.LastProbeSaidDown);
        }

        // ── Housekeeping ────────────────────────────────────────────────

        [Fact]
        public void Reset_clears_the_hold_but_keeps_what_was_learned()
        {
            var f = ArmedFilter();
            f.NoteDown(60_000);
            f.NoteUp(60_005);          // pending

            f.Reset();                 // radio torn down mid-hold

            Assert.False(f.DeferralElapsed(61_000));   // nothing pending any more
            Assert.True(f.SynthesisDetected,
                "what was learned describes the screen reader, not the hold, and must survive a radio teardown");
        }

        [Fact]
        public void An_up_with_no_hold_in_flight_is_passed_straight_through()
        {
            var f = new PttHoldFilter();
            Assert.Equal(PttHoldFilter.UpAction.ReleaseNow, f.NoteUp(100));
        }

        /// <summary>A filter that has already seen the synthetic signature.</summary>
        private static PttHoldFilter ArmedFilter()
        {
            var f = new PttHoldFilter();
            f.NoteDown(0);
            f.NoteUp(517);
            f.NoteDown(762);
            f.NoteUp(767);             // implausible — arms
            f.DeferralElapsed(767 + f.DeferMs);
            Assert.True(f.SynthesisDetected, "fixture failed to arm");
            return f;
        }
    }
}
