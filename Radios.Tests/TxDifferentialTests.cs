using System;
using System.Collections.Generic;
using System.Linq;
using Radios.ChainChecks;
using Xunit;
using static Radios.ChainChecks.TxDifferential;

namespace Radios.Tests
{
    /// <summary>
    /// Two transmit runs down one chain, differing in exactly one stage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property under test is not "does it spot a difference." It is that a
    /// difference is only ever reported when it CANNOT be a difference in the
    /// instrument. Two runs that measured different things prove nothing about
    /// the chain, and the whole value of handing this to a tester is that the
    /// answer needs no interpretation — so a wrong answer would be believed.
    /// </para>
    /// <para>
    /// The other property is that a SKIPPED run never reads as a passed one.
    /// "Step one succeeded" naturally sounds like "transmit works"; it means
    /// transmit works from the injection point onward, with the microphone
    /// never in the path.
    /// </para>
    /// </remarks>
    public class TxDifferentialTests
    {
        private static MeterSample M(string name, double v, bool reported = true)
            => new MeterSample(name, v, "dBFS", reported);

        private static TxRunSample Run(RunKind kind, params MeterSample[] meters)
            => TxRunSample.Measured(kind, DateTime.UtcNow, meters, "14.100 MHz", "USB", "ANT1");

        // ── The comparison only counts when both sides measured ───────────

        [Fact]
        public void A_meter_missing_from_one_run_is_incomparable_never_a_difference()
        {
            // THE test. A meter the radio reported in one run and not the other
            // differs by an unknown amount for an unknown reason, and calling
            // that a finding would put a fabricated difference in front of a
            // support engineer.
            var injected = Run(RunKind.Injected, M("SC_MIC", -10));
            var spoken = Run(RunKind.Spoken, M("SC_MIC", 0, reported: false));

            var c = CompareMeter("SC_MIC", injected, spoken);

            Assert.Equal(Verdict.Incomparable, c.Verdict);
            Assert.Contains("not comparable", c.Line());
        }

        [Fact]
        public void A_meter_absent_from_one_run_entirely_is_also_incomparable()
        {
            // Not merely unreported — not in the set at all. Same answer, and
            // it must not throw looking for it.
            var injected = Run(RunKind.Injected, M("SC_MIC", -10));
            var spoken = Run(RunKind.Spoken, M("ALC", -3));

            Assert.Equal(Verdict.Incomparable, CompareMeter("SC_MIC", injected, spoken).Verdict);
        }

        [Fact]
        public void Two_real_readings_far_apart_are_reported_with_a_direction()
        {
            // The positive control for the two tests above: it must actually be
            // able to find a difference, or "incomparable" everywhere would
            // pass them both and mean nothing.
            var injected = Run(RunKind.Injected, M("SC_MIC", -10));
            var spoken = Run(RunKind.Spoken, M("SC_MIC", -60));

            var c = CompareMeter("SC_MIC", injected, spoken);

            Assert.Equal(Verdict.LowerWhenSpoken, c.Verdict);
            Assert.Contains("lower", c.Line());
        }

        [Fact]
        public void A_difference_inside_the_threshold_is_called_the_same()
        {
            var injected = Run(RunKind.Injected, M("SC_MIC", -10));
            var spoken = Run(RunKind.Spoken, M("SC_MIC", -12));

            Assert.Equal(Verdict.Same, CompareMeter("SC_MIC", injected, spoken).Verdict);
        }

        [Fact]
        public void The_threshold_is_honoured_from_both_sides()
        {
            // Boundary pair. Without both, the threshold could be off by a
            // decibel in either direction unnoticed.
            var inj = Run(RunKind.Injected, M("SC_MIC", -10));

            Assert.Equal(Verdict.Same,
                CompareMeter("SC_MIC", inj, Run(RunKind.Spoken, M("SC_MIC", -10 - (SignificantDelta - 0.1)))).Verdict);
            Assert.Equal(Verdict.LowerWhenSpoken,
                CompareMeter("SC_MIC", inj, Run(RunKind.Spoken, M("SC_MIC", -10 - SignificantDelta))).Verdict);
        }

        [Fact]
        public void A_run_that_did_not_happen_makes_every_meter_incomparable()
        {
            var injected = Run(RunKind.Injected, M("SC_MIC", -10), M("ALC", -3));
            var spoken = TxRunSample.NotRun(RunKind.Spoken, SkipReason.NoMicrophone);

            Assert.All(Compare(injected, spoken), c => Assert.Equal(Verdict.Incomparable, c.Verdict));
        }

        [Fact]
        public void Every_watched_meter_is_compared_every_time()
        {
            // The set is fixed on purpose: both runs are asked for all of it, so
            // a meter that quietly stopped being captured shows up as
            // incomparable rather than vanishing from the report.
            var injected = Run(RunKind.Injected, M("SC_MIC", -10));
            var spoken = Run(RunKind.Spoken, M("SC_MIC", -11));

            var all = Compare(injected, spoken);

            Assert.Equal(Watched.Length, all.Count);
            foreach (string name in Watched)
                Assert.Contains(all, c => c.Name == name);
        }

        [Fact]
        public void The_watched_set_includes_the_meter_that_makes_the_comparison_legitimate()
        {
            // SC_MIC sits downstream of the mic selection, so it reads the
            // transmit chain whichever source feeds it. Without it there is no
            // injected-versus-spoken comparison at all.
            Assert.Contains("SC_MIC", Watched);

            // And MIC/MICPEAK, deliberately: they read the physical jack and sit
            // at -120 under PC audio BY DESIGN. That -120 cost a full day on
            // 2026-08-23 when it was read as "transmit is broken". In a
            // differential it stops being a trap and becomes a discriminator.
            Assert.Contains("MIC", Watched);
            Assert.Contains("MICPEAK", Watched);
        }

        // ── A skipped run is not a passed run ─────────────────────────────

        [Fact]
        public void Injected_alone_never_reads_as_transmit_works()
        {
            // The misreading this exists to prevent. Step one succeeding proves
            // the chain works FROM THE INJECTION POINT ONWARD — the microphone
            // was never in the path, so nothing has been learned about it.
            var injected = Run(RunKind.Injected, M("SC_MIC", -10));
            var spoken = TxRunSample.NotRun(RunKind.Spoken, SkipReason.NoMicrophone);

            string s = OperatorSummary(injected, spoken);

            Assert.Contains("injection point onward", s);
            Assert.Contains("NOT", s);
            Assert.DoesNotContain("Transmit audio is working", s);
        }

        [Fact]
        public void The_two_skip_reasons_say_different_things()
        {
            // Noel gave two buttons on purpose and they narrow the fault domain
            // differently. Collapsing them into one "skipped" would claim less
            // than we know in one case and more in the other.
            var injected = Run(RunKind.Injected, M("SC_MIC", -10));

            string remote = OperatorSummary(injected,
                TxRunSample.NotRun(RunKind.Spoken, SkipReason.RadioNotReachable));
            string noMic = OperatorSummary(injected,
                TxRunSample.NotRun(RunKind.Spoken, SkipReason.NoMicrophone));

            Assert.NotEqual(remote, noMic);
            // Remote rig: a PC microphone may still exist, so the comparison
            // may still be possible and we should say so.
            Assert.Contains("computer", remote);
            // No microphone anywhere: the comparison is closed.
            Assert.Contains("could not be tested at all", noMic);
        }

        [Fact]
        public void With_no_injected_run_there_is_nothing_to_compare_and_it_says_so()
        {
            string s = OperatorSummary(
                TxRunSample.NotRun(RunKind.Injected, SkipReason.None),
                Run(RunKind.Spoken, M("SC_MIC", -10)));

            Assert.Contains("nothing to compare", s);
            Assert.Contains("needs no microphone", s);
        }

        // ── The four outcomes an operator can actually get ────────────────

        [Fact]
        public void Injected_good_and_spoken_dead_points_at_the_microphone_side()
        {
            string s = OperatorSummary(
                Run(RunKind.Injected, M("SC_MIC", -10)),
                Run(RunKind.Spoken, M("SC_MIC", -120)));

            Assert.Contains("microphone side", s);
            // Named remedies, not a verdict the operator can do nothing with.
            Assert.Contains("muted", s);
            Assert.Contains("privacy", s);
        }

        [Fact]
        public void Both_dead_clears_the_microphone_explicitly()
        {
            // Saying "downstream" without saying "your microphone is NOT
            // implicated" leaves the operator still suspecting the thing this
            // test just exonerated.
            string s = OperatorSummary(
                Run(RunKind.Injected, M("SC_MIC", -120)),
                Run(RunKind.Spoken, M("SC_MIC", -118)));

            Assert.Contains("downstream", s);
            Assert.Contains("not implicated", s);
        }

        [Fact]
        public void Both_good_says_so_without_claiming_nothing_is_wrong()
        {
            string s = OperatorSummary(
                Run(RunKind.Injected, M("SC_MIC", -10)),
                Run(RunKind.Spoken, M("SC_MIC", -12)));

            Assert.Contains("working on this path", s);
            Assert.Contains("intermittent", s);
        }

        [Fact]
        public void Spoken_good_and_injected_dead_is_flagged_as_odd_rather_than_diagnosed()
        {
            // This should not happen. When a measurement produces a shape the
            // model does not explain, saying so is the honest answer — inventing
            // a cause would be worse than admitting the surprise.
            string s = OperatorSummary(
                Run(RunKind.Injected, M("SC_MIC", -120)),
                Run(RunKind.Spoken, M("SC_MIC", -10)));

            Assert.Contains("unusual", s);
            Assert.Contains("rather than acting on", s);
        }

        // ── Conditions travel with the measurement ────────────────────────

        [Fact]
        public void A_run_records_the_conditions_it_was_taken_under()
        {
            // #217: give a reader enough to reproduce the conditions rather
            // than take our word. #188: a meter reading with no recorded
            // antenna port is a number a support engineer cannot use.
            var r = Run(RunKind.Injected, M("SC_MIC", -10));

            Assert.Equal("14.100 MHz", r.Frequency);
            Assert.Equal("USB", r.Mode);
            Assert.Equal("ANT1", r.Antenna);
        }

        [Fact]
        public void A_meter_line_reports_what_was_read_not_what_it_means()
        {
            // #217, the grammar rule: observations, not diagnoses. A line that
            // declares a stage broken invites an argument about standing; a
            // line that says what a meter read can only be checked or refuted.
            string line = CompareMeter("SC_MIC",
                Run(RunKind.Injected, M("SC_MIC", -10)),
                Run(RunKind.Spoken, M("SC_MIC", -60))).Line();

            Assert.Contains("-10", line);
            Assert.Contains("-60", line);
            foreach (string forbidden in new[] { "broken", "fault", "failed", "problem" })
                Assert.DoesNotContain(forbidden, line, StringComparison.OrdinalIgnoreCase);
        }
    }
}
