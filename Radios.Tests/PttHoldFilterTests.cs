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

        [Fact]
        public void A_reader_slower_than_the_default_window_chops_once_then_is_absorbed()
        {
            var f = ArmedFilter();

            // Spacing 600 ms — beyond the default 350 ms window. The first
            // cycle releases (the timer wins the race), and the re-down that
            // then arrives teaches the filter the real spacing.
            f.NoteDown(50_000);
            Assert.Equal(PttHoldFilter.UpAction.DeferRelease, f.NoteUp(50_512));
            Assert.True(f.DeferralElapsed(50_512 + f.DeferMs));   // released — too slow for the default window

            // The synthetic re-down lands 600 ms after the up. It is a fresh
            // press, and it is also the lesson.
            Assert.Equal(PttHoldFilter.DownAction.Press, f.NoteDown(51_112));
            Assert.True(f.DeferMs >= 600,
                $"the miss should have taught the filter this reader's spacing; DeferMs is {f.DeferMs}");
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
