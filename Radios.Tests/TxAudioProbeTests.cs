using System;
using System.Collections.Generic;
using Radios.ChainChecks;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The decisions behind the injected and spoken transmit stages, tested
    /// without a radio, a microphone or a transmitter — the same split as
    /// <see cref="TxTuneProbe"/> against its runner. What the operator is TOLD
    /// about these results belongs to <c>TransmitStages</c> and is tested
    /// there; this covers what a raw reading is judged to be.
    /// </summary>
    public class TxAudioProbeTests
    {
        // ---- the one threshold ----

        [Fact]
        public void The_reached_line_is_the_differentials_line_and_nobody_elses()
        {
            // One threshold for every stage. Two would let the same SC_MIC
            // reading arrive in one stage and fail in the next.
            Assert.True(TxAudioProbe.Reached(TxDifferential.ReachedRadioDbfs + 0.1));
            Assert.False(TxAudioProbe.Reached(TxDifferential.ReachedRadioDbfs));
            Assert.False(TxAudioProbe.Reached(TxDifferential.ReachedRadioDbfs - 0.1));
        }

        [Fact]
        public void A_reading_that_never_happened_never_reaches()
        {
            // NaN is "no reading", and no reading must never read as arrival.
            Assert.False(TxAudioProbe.Reached(double.NaN));
        }

        // ---- judging one probe ----

        [Fact]
        public void A_probe_whose_meter_never_updated_casts_no_vote()
        {
            TxProbeSet.ProbeResult r = TxAudioProbe.Judge(
                TxProbeSet.Probe.SingleTone, meterUpdated: false, scMicDb: -12.0,
                "a steady tone");

            // NotAttempted keeps it out of TxProbeSet.Judge's vote — a probe
            // whose instrument was silent must not vote either way, however
            // healthy the stale number looks.
            Assert.Equal(TxProbeSet.Outcome.NotAttempted, r.Outcome);
            Assert.False(r.Counted);
            // But the detail must say RF went out, or "not attempted" reads
            // as "nothing happened".
            Assert.Contains("transmitted", r.Detail);
            Assert.Contains("SC_MIC", r.Detail);
        }

        [Fact]
        public void A_healthy_reading_reaches_and_carries_its_number()
        {
            TxProbeSet.ProbeResult r = TxAudioProbe.Judge(
                TxProbeSet.Probe.Voice, meterUpdated: true, scMicDb: -18.4, "the recording");

            Assert.Equal(TxProbeSet.Outcome.ReachedRadio, r.Outcome);
            Assert.Contains("-18.4", r.Detail);
            Assert.Contains("the recording", r.Detail);
        }

        [Fact]
        public void A_silent_reading_does_not_reach_and_names_the_line_it_missed()
        {
            TxProbeSet.ProbeResult r = TxAudioProbe.Judge(
                TxProbeSet.Probe.SingleTone, meterUpdated: true, scMicDb: -120.0, null);

            Assert.Equal(TxProbeSet.Outcome.DidNotReach, r.Outcome);
            // The threshold travels in the words, so a reader can check the
            // judgement instead of taking it.
            Assert.Contains("-45", r.Detail);
        }

        // ---- judging the ladder as one probe ----

        private static readonly TxToneLadder.Passband Band = TxToneLadder.Passband.Read(300, 2700);

        private static TxToneLadder.RungReading Rung(int hz, TxToneLadder.Placement place,
                                                     double db, bool reported = true)
            => new TxToneLadder.RungReading(
                new TxToneLadder.Rung(hz, place, "test"), db, reported);

        [Fact]
        public void A_ladder_with_no_reference_measured_casts_no_vote()
        {
            TxProbeSet.ProbeResult r = TxAudioProbe.LadderProbe(
                referenceReported: false, referenceDb: double.NaN,
                new[] { Rung(1000, TxToneLadder.Placement.InPassband, -12) }, Band);

            Assert.Equal(TxProbeSet.Outcome.NotAttempted, r.Outcome);
            Assert.False(r.Counted);
        }

        [Fact]
        public void A_ladder_whose_in_band_rungs_all_went_silent_casts_no_vote()
        {
            // Out-of-band controls reporting is not a measurement of arrival —
            // they are the rungs DESIGNED to come back attenuated.
            TxProbeSet.ProbeResult r = TxAudioProbe.LadderProbe(
                referenceReported: true, referenceDb: -12,
                new[]
                {
                    Rung(50, TxToneLadder.Placement.BelowPassband, -40),
                    Rung(1000, TxToneLadder.Placement.InPassband, 0, reported: false),
                    Rung(2950, TxToneLadder.Placement.AbovePassband, -41),
                }, Band);

            Assert.Equal(TxProbeSet.Outcome.NotAttempted, r.Outcome);
        }

        [Fact]
        public void The_ladders_vote_comes_from_its_best_in_band_rung()
        {
            TxProbeSet.ProbeResult r = TxAudioProbe.LadderProbe(
                referenceReported: true, referenceDb: -12,
                new[]
                {
                    Rung(50, TxToneLadder.Placement.BelowPassband, -80),
                    Rung(700, TxToneLadder.Placement.InPassband, -60),
                    Rung(1400, TxToneLadder.Placement.InPassband, -13),
                    Rung(2950, TxToneLadder.Placement.AbovePassband, -80),
                }, Band);

            Assert.Equal(TxProbeSet.Outcome.ReachedRadio, r.Outcome);
            // The deliberate out-of-band attenuation must not fail the probe
            // for doing what it was placed to do.
            Assert.Contains("-13", r.Detail);
        }

        [Fact]
        public void A_ladder_nothing_arrived_from_says_so_and_still_tells_its_story()
        {
            TxProbeSet.ProbeResult r = TxAudioProbe.LadderProbe(
                referenceReported: true, referenceDb: -100,
                new[]
                {
                    Rung(700, TxToneLadder.Placement.InPassband, -100),
                    Rung(1400, TxToneLadder.Placement.InPassband, -102),
                }, Band);

            Assert.Equal(TxProbeSet.Outcome.DidNotReach, r.Outcome);
            // The rung-by-rung story rides along for the evidence — it is
            // TxToneLadder's judgement, not re-decided by the probe.
            Assert.Contains("hertz", r.Detail);
        }

        // ---- the spoken stage's path check ----

        [Theory]
        [InlineData("CW")]
        [InlineData("CWL")]
        [InlineData("cw")]
        public void CW_has_no_transmit_audio_path_for_a_voice_either(string mode)
        {
            string trouble = TxAudioProbe.SpokenPathTrouble(mode, "PC", pcAudioOn: true);
            Assert.NotEqual("", trouble);
            Assert.Contains("CW", trouble);
        }

        [Fact]
        public void A_pc_microphone_with_pc_audio_off_has_no_path()
        {
            Assert.NotEqual("", TxAudioProbe.SpokenPathTrouble("USB", "PC", pcAudioOn: false));
        }

        [Fact]
        public void A_pc_microphone_with_pc_audio_on_is_a_path()
        {
            Assert.Equal("", TxAudioProbe.SpokenPathTrouble("USB", "PC", pcAudioOn: true));
        }

        [Fact]
        public void The_radios_own_jack_needs_no_pc_audio_at_all()
        {
            // Deliberately narrower than the injected stage's check: SC_MIC
            // sits downstream of the mic selection, so a voice into the
            // radio's own jack measures honestly with PC audio off.
            Assert.Equal("", TxAudioProbe.SpokenPathTrouble("LSB", "MIC", pcAudioOn: false));
        }

        [Fact]
        public void An_unknown_mode_is_not_refused()
        {
            // The measurement itself will say whether anything arrived; only
            // the states with provably NO path refuse.
            Assert.Equal("", TxAudioProbe.SpokenPathTrouble("", "MIC", pcAudioOn: false));
        }

        // ---- evidence composition ----

        private static TxDifferential.TxRunSample Sample(TxDifferential.RunKind kind,
                                                         double scMic)
            => TxDifferential.TxRunSample.Measured(kind, DateTime.UtcNow,
                new[]
                {
                    new TxDifferential.MeterSample("SC_MIC", scMic, "dBFS", true),
                    new TxDifferential.MeterSample("SWR", 1.2, "", true),
                },
                "14.200000 MHz", "USB", "ANT1");

        [Fact]
        public void A_capture_that_never_happened_says_so()
        {
            Assert.Equal("No meter capture was taken.", TxAudioProbe.DescribeSample(null));
            Assert.Equal("No meter capture was taken.", TxAudioProbe.DescribeSample(
                TxDifferential.TxRunSample.NotRun(TxDifferential.RunKind.Injected,
                                                  TxDifferential.SkipReason.NoMicrophone)));
        }

        [Fact]
        public void A_capture_travels_with_its_conditions()
        {
            string s = TxAudioProbe.DescribeSample(
                Sample(TxDifferential.RunKind.Injected, -20));

            // A reading with no recorded conditions cannot be reproduced by
            // anyone (#217), and one with no antenna cannot be used (#188).
            Assert.Contains("14.200000 MHz", s);
            Assert.Contains("USB", s);
            Assert.Contains("ANT1", s);
            Assert.Contains("SC_MIC", s);
        }

        [Fact]
        public void The_spoken_comparison_without_an_injected_run_says_what_is_missing()
        {
            string s = TxAudioProbe.SpokenComparison(null,
                Sample(TxDifferential.RunKind.Spoken, -20));

            Assert.Contains("injected", s);
            // And it must not pretend a comparison happened.
            Assert.DoesNotContain("meter by meter", s);
        }

        [Fact]
        public void The_spoken_comparison_reads_the_differentials_own_verdicts()
        {
            string s = TxAudioProbe.SpokenComparison(
                Sample(TxDifferential.RunKind.Injected, -20),
                Sample(TxDifferential.RunKind.Spoken, -110));

            // The verdict vocabulary is TxDifferential's, reused not restated:
            // injected arrived, spoken did not, so the fault is mic-side.
            Assert.Contains("SC_MIC", s);
            Assert.Contains("microphone", s);
        }

        // ---- timing constants keep their promises ----

        [Fact]
        public void A_rungs_settle_and_window_add_up_to_the_airtime_the_ladder_promises()
        {
            // TxToneLadder.TotalMsFor tells the operator how long their radio
            // will transmit, per rung, before it starts. The measurement must
            // spend exactly that, not roughly that.
            Assert.Equal(TxToneLadder.RungMs,
                         TxAudioProbe.RungSettleMs + TxAudioProbe.RungWindowMs);
        }
    }
}
