using System;
using System.Collections.Generic;
using Radios.ChainChecks;
using Xunit;
using static Radios.ChainChecks.TxTuneProbe;

namespace Radios.Tests
{
    /// <summary>
    /// The transmitter, measured with the audio chain entirely out of the path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property under test is not "does it notice power." It is that
    /// <b>a failure here is never reported as an audio fault</b>, and that
    /// "we could not tell" never collapses into "we found nothing." Those two
    /// mistakes send an operator down a microphone diagnostic for a
    /// transmitter that is not transmitting, which is the specific waste this
    /// probe exists to prevent.
    /// </para>
    /// <para>
    /// The load judgement is tested against SWR WORKED OUT from forward and
    /// reflected power, never against the radio's own SWR meter — that meter
    /// read 1.008 into a completely open connector while 76 percent of the
    /// power came back (#189, bench 8600, 2026-08-22). A rule keyed to it
    /// would pass every test here and be worthless in the field.
    /// </para>
    /// </remarks>
    public class TxTuneProbeTests
    {
        private static Reading R(string name, double v, string units = "watts")
            => Reading.Got(name, v, units);

        private static Reading Absent(string name) => Reading.Missing(name);

        private static List<Reading> Meters(params Reading[] r) => new List<Reading>(r);

        // ---- forward power decides whether the transmitter did anything ----

        [Fact]
        public void Power_present_means_the_transmitter_works()
        {
            var m = Meters(R("FWDPWR", 100.0), R("REFPWR", 0.05));
            Assert.Equal(Verdict.MakesPower, Assess(m, computedSwr: 1.05));
        }

        [Fact]
        public void No_power_is_reported_and_is_not_an_audio_fault()
        {
            var m = Meters(R("FWDPWR", 0.0), R("REFPWR", 0.0));
            Assert.Equal(Verdict.NoPower, Assess(m, computedSwr: double.NaN));
        }

        [Fact]
        public void A_dead_key_trickle_still_counts_as_no_power()
        {
            // A dead key measured 0.22 W on the bench 2026-08-22. That is not
            // the transmitter working, it is the transmitter not working.
            var m = Meters(R("FWDPWR", 0.22));
            Assert.Equal(Verdict.NoPower, Assess(m, computedSwr: double.NaN));
        }

        [Fact]
        public void A_missing_forward_meter_is_not_a_reading_of_zero()
        {
            // The distinction the whole diagnostic rests on: absence of a
            // measurement must never read as a measurement of absence.
            var m = Meters(Absent("FWDPWR"), R("REFPWR", 0.0));
            Assert.Equal(Verdict.NoForwardPowerMeter, Assess(m, computedSwr: double.NaN));
        }

        [Fact]
        public void No_meters_at_all_is_also_not_a_reading_of_zero()
        {
            Assert.Equal(Verdict.NoForwardPowerMeter,
                         Assess(new List<Reading>(), computedSwr: double.NaN));
        }

        // ---- the load judgement runs on computed SWR, not the meter ----

        [Fact]
        public void A_bad_computed_swr_flags_the_load_while_still_crediting_the_transmitter()
        {
            // Power appeared, so the transmitter works. That must survive a bad
            // load — the two findings are independent and both matter.
            var m = Meters(R("FWDPWR", 17.5), R("REFPWR", 13.4));
            Verdict v = Assess(m, computedSwr: 14.0);
            Assert.Equal(Verdict.MakesPowerLoadSuspect, v);
        }

        [Fact]
        public void The_open_connector_case_from_the_bench_is_caught()
        {
            // Morning of 2026-08-22, the OPEN port: 13.4 W reflected of 17.5 W
            // forward, 76 percent, while the radio's own SWR meter said 1.008
            // throughout. Here the meter is fed that lie deliberately and must
            // not win. Port label omitted on purpose — the load was moved later
            // the same day, and the numbers are what generalise, not the jack.
            var m = Meters(R("FWDPWR", 17.5), R("REFPWR", 13.4), R("SWR", 1.008, "to 1"));
            Verdict v = Assess(m, computedSwr: 14.0);
            Assert.Equal(Verdict.MakesPowerLoadSuspect, v);
        }

        [Fact]
        public void The_dummy_load_case_from_the_bench_reads_clean()
        {
            // Same session, the LOADED port: 0.054 W of 101.2 W, 0.05 percent.
            var m = Meters(R("FWDPWR", 101.2), R("REFPWR", 0.054));
            Assert.Equal(Verdict.MakesPower, Assess(m, computedSwr: 1.05));
        }

        [Fact]
        public void Reflected_share_is_the_fallback_when_swr_cannot_be_derived()
        {
            // SwrFromPower returns NaN when reflected >= forward, or when
            // forward is too small to divide by. The fraction still works.
            var m = Meters(R("FWDPWR", 10.0), R("REFPWR", 7.6));
            Assert.Equal(Verdict.MakesPowerLoadSuspect, Assess(m, computedSwr: double.NaN));
        }

        [Fact]
        public void A_good_load_with_no_derivable_swr_is_not_flagged()
        {
            var m = Meters(R("FWDPWR", 10.0), R("REFPWR", 0.01));
            Assert.Equal(Verdict.MakesPower, Assess(m, computedSwr: double.NaN));
        }

        [Fact]
        public void A_missing_reflected_meter_does_not_manufacture_a_load_verdict()
        {
            // No reflected reading means the load is unknown, not good and not
            // bad. The transmitter verdict still stands on its own.
            var m = Meters(R("FWDPWR", 100.0), Absent("REFPWR"));
            Assert.Equal(Verdict.MakesPower, Assess(m, computedSwr: double.NaN));
        }

        // ---- the early stop ----

        [Fact]
        public void One_bad_sample_does_not_stop_the_carrier()
        {
            // The PA ramps. The first sample after key-down can read badly on a
            // perfectly good load, and stopping on it would abort healthy tests.
            Assert.False(ShouldStopEarly(computedSwr: 20.0, reflectedPercent: 80.0,
                                         consecutiveBad: 0));
        }

        [Fact]
        public void Two_bad_samples_in_a_row_stop_the_carrier()
        {
            Assert.True(ShouldStopEarly(computedSwr: 20.0, reflectedPercent: 80.0,
                                        consecutiveBad: 1));
        }

        [Fact]
        public void A_merely_suspect_load_does_not_trigger_the_early_stop()
        {
            // Suspect is something to report; abort is something to act on. A
            // 3.5 SWR is worth telling the operator about and not worth cutting
            // a two second test short for.
            Assert.False(ShouldStopEarly(computedSwr: 3.5, reflectedPercent: double.NaN,
                                         consecutiveBad: 5));
        }

        [Fact]
        public void The_stop_falls_back_to_reflected_share_when_swr_is_not_derivable()
        {
            Assert.True(ShouldStopEarly(computedSwr: double.NaN, reflectedPercent: 76.0,
                                        consecutiveBad: 1));
        }

        [Fact]
        public void Nothing_measurable_never_stops_the_carrier()
        {
            // Unknown is not bad. Aborting on absent readings would make every
            // radio that publishes no reflected meter untestable.
            Assert.False(ShouldStopEarly(computedSwr: double.NaN,
                                         reflectedPercent: double.NaN,
                                         consecutiveBad: 99));
        }

        // ---- standing: what the rest of the diagnostic is allowed to conclude ----

        [Fact]
        public void Audio_testing_has_standing_only_when_the_transmitter_proved_itself()
        {
            var ok = Result.Ran(Verdict.MakesPower, DateTime.UtcNow, Meters(R("FWDPWR", 100)),
                                100, 1.05, false, "14.200", "USB", "ANT2");
            Assert.True(ok.AudioTestingHasStanding);

            var suspect = Result.Ran(Verdict.MakesPowerLoadSuspect, DateTime.UtcNow,
                                     Meters(R("FWDPWR", 17.5)), 100, 14.0, true, "", "", "ANT1");
            Assert.True(suspect.AudioTestingHasStanding);
        }

        [Theory]
        [InlineData(Verdict.NoPower)]
        [InlineData(Verdict.NoForwardPowerMeter)]
        [InlineData(Verdict.NotRun)]
        public void Audio_testing_has_no_standing_when_the_transmitter_did_not_prove_itself(Verdict v)
        {
            // This is the gate that stops an operator being walked through a
            // microphone diagnostic for a transmitter that never keyed.
            var r = v == Verdict.NotRun
                ? Result.NotRun(SkipReason.RadioNotReachable)
                : Result.Ran(v, DateTime.UtcNow, Meters(Absent("FWDPWR")), 100,
                             double.NaN, false, "", "", "");
            Assert.False(r.AudioTestingHasStanding);
        }

        // ---- skips stay distinguishable from findings ----

        [Theory]
        [InlineData(SkipReason.RadioNotReachable)]
        [InlineData(SkipReason.LoadNotDeclared)]
        [InlineData(SkipReason.AlreadyTransmitting)]
        [InlineData(SkipReason.Cancelled)]
        public void Every_skip_reason_explains_itself_distinctly(SkipReason why)
        {
            string text = ExplainSkip(why);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(ExplainSkip(SkipReason.None), text);
        }

        [Fact]
        public void Skip_reasons_do_not_share_wording()
        {
            // Distinct remedies must read distinctly, or an operator acts on
            // the wrong one. Declaring a load and reconnecting a radio are not
            // the same job.
            var seen = new HashSet<string>();
            foreach (SkipReason why in Enum.GetValues(typeof(SkipReason)))
                Assert.True(seen.Add(ExplainSkip(why)), "duplicate wording for " + why);
        }

        [Fact]
        public void The_no_power_explanation_says_it_is_not_an_audio_problem()
        {
            // Load-bearing wording, not decoration. Without it the operator's
            // next move is to go and test their microphone.
            var r = Result.Ran(Verdict.NoPower, DateTime.UtcNow, Meters(R("FWDPWR", 0.0)),
                               100, double.NaN, false, "", "", "");
            string text = Explain(r);
            Assert.Contains("NOT AN AUDIO PROBLEM", text, StringComparison.OrdinalIgnoreCase);
        }

        // ---- evidence text ----

        [Fact]
        public void Evidence_separates_readings_from_interpretation()
        {
            // #217: a reader who distrusts our software entirely must still be
            // able to use the numbers, so what we measured and what we concluded
            // cannot be blended.
            var r = Result.Ran(Verdict.MakesPower, DateTime.UtcNow,
                               Meters(R("FWDPWR", 100.0), R("REFPWR", 0.05)),
                               100, 1.05, false, "14.200", "USB", "ANT2");
            string e = EvidenceSection(r);
            Assert.Contains("Readings:", e);
            Assert.Contains("our interpretation, not a measurement", e);
            Assert.True(e.IndexOf("Readings:", StringComparison.Ordinal) <
                        e.IndexOf("our interpretation", StringComparison.Ordinal),
                        "readings must come before interpretation");
        }

        [Fact]
        public void Evidence_states_that_no_audio_took_part()
        {
            // The single most important sentence for a FlexRadio reader: this
            // measurement does not involve our audio code at all.
            var r = Result.Ran(Verdict.MakesPower, DateTime.UtcNow, Meters(R("FWDPWR", 100.0)),
                               100, 1.05, false, "", "", "");
            Assert.Contains("No microphone", EvidenceSection(r), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Evidence_for_a_skipped_run_says_so_rather_than_showing_empty_readings()
        {
            string e = EvidenceSection(Result.NotRun(SkipReason.LoadNotDeclared));
            Assert.Contains("Not run", e);
            Assert.DoesNotContain("Readings:", e);
        }

        [Fact]
        public void Evidence_records_the_tune_power_the_verdict_was_measured_against()
        {
            // "No power" means nothing without knowing what power was asked for.
            var r = Result.Ran(Verdict.NoPower, DateTime.UtcNow, Meters(R("FWDPWR", 0.0)),
                               25, double.NaN, false, "", "", "");
            Assert.Contains("25", EvidenceSection(r));
        }
    }
}
