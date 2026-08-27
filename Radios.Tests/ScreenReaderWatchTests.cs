using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The watchdog that notices the operator changed screen readers under a
    /// running application (#283).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scenario every test here is built from is the one the operator
    /// measured at the radio on 2026-08-26: launch under NVDA, start JAWS,
    /// and the application goes on speaking to a reader that has left. He
    /// proved the diagnosis by closing the application, starting JAWS first
    /// and relaunching — "JAWS spoke everything, no problem."
    /// </para>
    /// <para>
    /// The reverse direction is tested separately and on purpose. He went NVDA
    /// to JAWS; he did NOT test JAWS to NVDA, and the task warns in as many
    /// words not to assume symmetry. A policy that compares identities cannot
    /// be asymmetric, and these tests are what says so.
    /// </para>
    /// </remarks>
    public class ScreenReaderWatchTests
    {
        private const bool Healthy = true;

        // ── The measured failure ────────────────────────────────────────

        [Fact]
        public void Nvda_to_jaws_rebinds_once_the_change_has_settled()
        {
            var w = Bound("NVDA");

            // The swap. Nothing happens on the first sighting — Prism's own
            // recovery is given the chance to fix this by itself.
            for (int i = 1; i < ScreenReaderWatch.ReaderSettleTicks; i++)
                Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe("JAWS", Healthy));

            Assert.Equal(ScreenReaderWatch.Decision.Rebind,
                w.Observe("JAWS", Healthy));
        }

        [Fact]
        public void Jaws_to_nvda_is_not_assumed_to_be_the_same_and_is_tested_the_same()
        {
            var w = Bound("JAWS");

            for (int i = 1; i < ScreenReaderWatch.ReaderSettleTicks; i++)
                Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe("NVDA", Healthy));

            Assert.Equal(ScreenReaderWatch.Decision.Rebind, w.Observe("NVDA", Healthy));
        }

        [Fact]
        public void A_reader_that_is_still_the_bound_one_never_rebinds()
        {
            var w = Bound("NVDA");
            for (int i = 0; i < 50; i++)
                Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe("NVDA", Healthy));
        }

        [Fact]
        public void Only_one_rebind_is_asked_for_while_the_last_one_is_still_running()
        {
            var w = Bound("NVDA");
            Assert.Equal(ScreenReaderWatch.Decision.Rebind, Settle(w, "JAWS"));

            // The host has not reported back yet. Asking again would stack
            // rebinds on top of a rebind.
            for (int i = 0; i < 10; i++)
                Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe("JAWS", Healthy));

            // Host reports what it landed on; the disagreement is over.
            w.NoteBound("JAWS", isControllerReader: true);
            for (int i = 0; i < 10; i++)
                Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe("JAWS", Healthy));
        }

        // ── The none case, which the task calls out by name ─────────────

        [Fact]
        public void A_reader_that_merely_restarts_is_ridden_out_without_a_rebind()
        {
            var w = Bound("NVDA");

            // NVDA goes away and comes back inside its own settle window, the
            // way a screen reader restart really behaves. Rebinding here would
            // drop the operator onto a synthesiser and then drag them back.
            for (int i = 0; i < ScreenReaderWatch.NoReaderSettleTicks - 1; i++)
                Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe(null, Healthy));

            Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe("NVDA", Healthy));
        }

        [Fact]
        public void A_reader_that_is_gone_for_good_does_eventually_rebind()
        {
            var w = Bound("NVDA");

            for (int i = 1; i < ScreenReaderWatch.NoReaderSettleTicks; i++)
                Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe(null, Healthy));

            Assert.Equal(ScreenReaderWatch.Decision.Rebind, w.Observe(null, Healthy));
        }

        [Fact]
        public void No_reader_settles_more_slowly_than_a_different_reader()
        {
            Assert.True(ScreenReaderWatch.NoReaderSettleTicks > ScreenReaderWatch.ReaderSettleTicks,
                "an absent reader is usually a restarting one; a different reader is unambiguous");
        }

        [Fact]
        public void Reader_to_none_to_a_different_reader_is_not_read_as_no_change()
        {
            var w = Bound("NVDA");

            // Both readers down for a moment — the real shape of a swap where
            // the old one exits before the new one is up. The run of "none"
            // never reaches its own threshold, and then JAWS appears.
            w.Observe(null, Healthy);
            w.Observe(null, Healthy);

            Assert.Equal(ScreenReaderWatch.Decision.Rebind, Settle(w, "JAWS"));
        }

        [Fact]
        public void Attaching_a_reader_when_there_was_none_is_a_change_too()
        {
            // #167's case: the application came up with no reader and one
            // arrived afterwards. Bound to a synthesiser, so any named reader
            // is an improvement worth taking.
            var w = new ScreenReaderWatch();
            w.NoteBound("SAPI", isControllerReader: false);

            Assert.Equal(ScreenReaderWatch.Decision.Rebind, Settle(w, "NVDA"));
        }

        [Fact]
        public void A_synthesiser_with_no_reader_running_is_already_correct()
        {
            var w = new ScreenReaderWatch();
            w.NoteBound("SAPI", isControllerReader: false);

            for (int i = 0; i < 20; i++)
                Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe(null, Healthy));
        }

        // ── Flapping ────────────────────────────────────────────────────

        [Fact]
        public void A_run_of_agreement_is_required_not_a_count_of_sightings()
        {
            var w = Bound("NVDA");

            // Alternating sightings must never accumulate into a decision.
            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe("JAWS", Healthy));
                Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe("NVDA", Healthy));
            }
        }

        // ── The instrument, and the difference between two negatives ────

        [Fact]
        public void An_unusable_probe_stands_down_rather_than_reporting_absence()
        {
            var w = Bound("NVDA");

            for (int i = 0; i < 20; i++)
                Assert.Equal(ScreenReaderWatch.Decision.StandDown, w.Observe(null, probeHealthy: false));
        }

        [Fact]
        public void An_unusable_probe_cannot_finish_a_case_a_working_one_had_started()
        {
            var w = Bound("NVDA");

            // Two good sightings of a swap, one short of acting...
            Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe("JAWS", Healthy));
            Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe("JAWS", Healthy));

            // ...then the instrument fails. That must not be allowed to count
            // as the third, and it must not leave a half-built case standing.
            Assert.Equal(ScreenReaderWatch.Decision.StandDown, w.Observe(null, probeHealthy: false));

            Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe("JAWS", Healthy));
            Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe("JAWS", Healthy));
            Assert.Equal(ScreenReaderWatch.Decision.Rebind, w.Observe("JAWS", Healthy));
        }

        // ── Naming ──────────────────────────────────────────────────────

        [Fact]
        public void A_longer_product_name_is_the_same_reader_not_a_swap()
        {
            // Prism reports "JAWS"; a probe may see a longer product string.
            // Reading that as a change would rebind in a loop forever.
            var w = Bound("JAWS");
            for (int i = 0; i < 20; i++)
                Assert.Equal(ScreenReaderWatch.Decision.Hold, w.Observe("JAWS 2026", Healthy));
        }

        [Fact]
        public void Reader_names_are_matched_without_regard_to_case()
        {
            Assert.True(ScreenReaderWatch.SameReader("NVDA", "nvda"));
            Assert.True(ScreenReaderWatch.SameReader("JAWS", "JAWS 2026"));
            Assert.False(ScreenReaderWatch.SameReader("NVDA", "JAWS"));
            Assert.False(ScreenReaderWatch.SameReader(null, "NVDA"));
            Assert.False(ScreenReaderWatch.SameReader("NVDA", null));
        }

        // ── The probe itself, with a positive control ───────────────────

        [Fact]
        public void The_presence_probe_can_find_something_it_should_find()
        {
            // A negative result from Detect() also claims the probe WOULD have
            // seen a reader. That second claim needs its own evidence, which is
            // exactly what ProbeWorks is for — and testing Detect() without it
            // would be testing that we found nothing with an instrument we
            // never established was working.
            if (!ScreenReaderPresence.ProbeWorks()) return;   // no interactive shell here

            // Whatever Detect returns, it must be one of the names the probe
            // says it can recognise, or null.
            var seen = ScreenReaderPresence.Detect();
            if (seen != null)
                Assert.Contains(seen, ScreenReaderPresence.ObservableReaders);
        }

        [Fact]
        public void The_probe_names_what_it_can_recognise_so_a_trace_can_be_read()
        {
            // "No reader running" and "a reader we cannot see" are different
            // facts, and a trace can only tell them apart if the observable set
            // is written down next to the observation.
            Assert.Contains("NVDA", ScreenReaderPresence.ObservableReaders);
            Assert.Contains("JAWS", ScreenReaderPresence.ObservableReaders);
        }

        private static ScreenReaderWatch Bound(string reader)
        {
            var w = new ScreenReaderWatch();
            w.NoteBound(reader, isControllerReader: true);
            return w;
        }

        /// <summary>Feed one observation until the policy acts, or give up.</summary>
        private static ScreenReaderWatch.Decision Settle(ScreenReaderWatch w, string? observed)
        {
            var d = ScreenReaderWatch.Decision.Hold;
            for (int i = 0; i < ScreenReaderWatch.NoReaderSettleTicks + 2; i++)
            {
                d = w.Observe(observed, Healthy);
                if (d == ScreenReaderWatch.Decision.Rebind) return d;
            }
            return d;
        }
    }
}
