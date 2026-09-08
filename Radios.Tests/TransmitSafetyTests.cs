using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The live warning that fires while the operator is transmitting into an
    /// antenna port with nothing on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The readings are from the bench 8600 on 2026-08-22, converted to watts:
    /// into an EMPTY ANT1 connector, 17.5 W forward and 13.4 W reflected, and
    /// into the dummy load on ANT2 minutes later, 101.2 W forward and 0.054 W
    /// reflected. The radio reported an SWR of 1.008 for the first of those.
    /// </para>
    /// <para>
    /// Every test here is a positive control before it is anything else. The
    /// failure this guards against is not a warning that says the wrong thing;
    /// it is a warning that says nothing, which looks exactly like a station
    /// with nothing wrong.
    /// </para>
    /// </remarks>
    public class TransmitSafetyTests
    {
        // Both bench pairs, in watts.
        private const float OpenForward = 17.5f;
        private const float OpenReflected = 13.4f;
        private const float LoadForward = 101.2f;
        private const float LoadReflected = 0.054f;

        private const int Settled = TransmitSafety.ReflectedWarnSeconds;

        /// <summary>
        /// One reading of both meters, taken together unless a skew is named.
        /// </summary>
        private static TransmitPowerReading Pair(
            float forwardWatts, float reflectedWatts,
            float skewMs = 0f, float ageMs = 0f, int commandedWatts = 0) =>
            new TransmitPowerReading(forwardWatts, reflectedWatts, skewMs, ageMs, commandedWatts);

        /// <summary>
        /// A transmission that has already seen this reading enough times for
        /// the persistence rule to be satisfied — the ordinary state of affairs
        /// on a station that really is mismatched, where every judgeable sample
        /// says the same thing.
        /// </summary>
        private static ReflectedPowerRun RunOf(
            TransmitPowerReading reading,
            int samples = TransmitSafety.ReflectedWarnSustainedSamples)
        {
            var run = new ReflectedPowerRun();
            // One sample a second, the PTT controller's cadence. Identical
            // readings make a SETTLED streak, so the settling rule (#453) has
            // nothing to defer and these tests exercise the level rule alone.
            for (int i = 0; i < samples; i++) run.Observe(reading, i + 1);
            return run;
        }

        [Fact]
        public void The_empty_antenna_port_warns()
        {
            // THE positive control. If this ever passes by not warning, the
            // whole feature is decorative.
            var reading = Pair(OpenForward, OpenReflected);
            Assert.True(TransmitSafety.ShouldWarnReflected(
                reading, RunOf(reading), Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void The_good_dummy_load_stays_quiet()
        {
            // The negative control, without which the test above proves nothing:
            // a function that returned true unconditionally would also pass it.
            var reading = Pair(LoadForward, LoadReflected);
            Assert.False(TransmitSafety.ShouldWarnReflected(
                reading, RunOf(reading), Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void A_running_tune_cycle_is_silent_even_into_an_open_port()
        {
            // A tuner deliberately transmits into a bad match. Warning on every
            // tune-up trains the operator to ignore the one that matters.
            var reading = Pair(OpenForward, OpenReflected);
            Assert.False(TransmitSafety.ShouldWarnReflected(
                reading, RunOf(reading), Settled, tuning: true, alreadyWarned: false));
        }

        [Fact]
        public void The_first_second_of_transmit_is_given_to_the_meters()
        {
            // Meters have not necessarily caught up with key-down, and a false
            // alarm on every single transmission would be the end of it.
            var reading = Pair(OpenForward, OpenReflected);
            Assert.False(TransmitSafety.ShouldWarnReflected(
                reading, RunOf(reading), 0, tuning: false, alreadyWarned: false));
            Assert.False(TransmitSafety.ShouldWarnReflected(
                reading, RunOf(reading), Settled - 1, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void It_speaks_once_per_transmission_not_once_per_second()
        {
            var reading = Pair(OpenForward, OpenReflected);
            Assert.False(TransmitSafety.ShouldWarnReflected(
                reading, RunOf(reading), Settled + 30, tuning: false, alreadyWarned: true));
        }

        [Fact]
        public void Almost_no_forward_power_is_not_a_fault()
        {
            // A meter wandering around zero can produce any ratio at all. The
            // operator dead-keying at a fraction of a watt has not broken
            // anything and must not be told they have.
            var reading = Pair(0.2f, 0.19f);
            Assert.False(TransmitSafety.ShouldWarnReflected(
                reading, RunOf(reading), Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void An_unreadable_meter_is_not_treated_as_a_fault_or_as_health()
        {
            Assert.True(float.IsNaN(TransmitSafety.ReflectedFractionOf(float.NaN, 1f)));
            Assert.True(float.IsNaN(TransmitSafety.ReflectedFractionOf(1f, float.NaN)));

            var reading = Pair(float.NaN, float.NaN);
            Assert.False(TransmitSafety.ShouldWarnReflected(
                reading, RunOf(reading), Settled, tuning: false, alreadyWarned: false));

            // And a radio that has never reported one of the two meters at all.
            Assert.False(TransmitSafety.ShouldWarnReflected(
                TransmitPowerReading.None, new ReflectedPowerRun(),
                Settled, tuning: false, alreadyWarned: false));
        }

        // ---- forward and reflected are ONE reading (#453) ----
        //
        // The defect Don hit on 2026-09-01, on the air, on a correctly matched
        // antenna: the two meters were read as two independent fields, so on
        // speech a small forward reading from a syllable trough was divided
        // into a larger, slightly older reflected one and the transmission was
        // ended. It never happened on a tune, because a tune is a steady
        // carrier with no envelope and therefore no skew.

        [Fact]
        public void A_good_match_with_a_skewed_pair_does_not_alarm()
        {
            // THE regression test for #453, built the way the fault actually
            // occurs. A well-matched hundred-watt station returning one percent
            // sits at 100 W forward and 1 W reflected at the envelope peak. A
            // syllable later the forward meter reads 2 W while the reflected
            // field still holds the 1 W deposited 80 ms earlier — half the
            // power apparently coming back, on an antenna that is fine.
            var skewed = Pair(2f, 1f, skewMs: 80f);

            Assert.False(skewed.IsCoherent);
            Assert.True(float.IsNaN(skewed.ReflectedShare),
                "a pair that was not sampled together has no share to report");

            var run = new ReflectedPowerRun();
            for (int i = 0; i < 20; i++) run.Observe(skewed, i + 1);

            Assert.Equal(0, run.JudgedSamples);
            Assert.Equal(20, run.IncoherentSamples);
            Assert.False(TransmitSafety.ShouldWarnReflected(
                skewed, run, Settled + 5, tuning: false, alreadyWarned: false));
            Assert.False(TransmitSafety.ShouldCutReflected(
                settingEnabled: true, alreadyWarned: true, skewed, tuning: false));
        }

        [Fact]
        public void The_empty_antenna_port_still_alarms_when_the_pair_is_taken_together()
        {
            // The other half of the control. Refusing skewed pairs is only
            // worth anything if the measured fault still gets through, so this
            // is the same 2026-08-22 reading with the skew a real dispatch
            // burst actually produces.
            var reading = Pair(OpenForward, OpenReflected, skewMs: 1f, ageMs: 120f);

            Assert.True(reading.IsCoherent);
            Assert.True(TransmitSafety.ShouldWarnReflected(
                reading, RunOf(reading), Settled, tuning: false, alreadyWarned: false));
            Assert.True(TransmitSafety.ShouldCutReflected(
                settingEnabled: true, alreadyWarned: true, reading, tuning: false));
        }

        [Fact]
        public void Meters_that_stopped_arriving_are_not_judged()
        {
            // Without this, a transmission that loses its meter feed leaves both
            // fields frozen and every consumer keeps judging a photograph.
            var stale = Pair(OpenForward, OpenReflected,
                             ageMs: TransmitPowerReading.MaxAgeMilliseconds + 1f);

            Assert.False(stale.IsCoherent);
            Assert.Contains("ms ago", stale.WhyNotCoherent);
            Assert.False(TransmitSafety.ShouldWarnReflected(
                stale, RunOf(Pair(OpenForward, OpenReflected)),
                Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void A_declining_guard_says_why_rather_than_going_quiet()
        {
            // An absence is not evidence: a guard that silently refuses to judge
            // looks exactly like one that is watching and finding nothing wrong.
            Assert.Equal("", Pair(OpenForward, OpenReflected).WhyNotCoherent);
            Assert.Contains("not one sample", Pair(2f, 1f, skewMs: 80f).WhyNotCoherent);
            Assert.Contains("never reported", TransmitPowerReading.None.WhyNotCoherent);
        }

        // ---- the floor scales with the transmission (#453) ----

        [Fact]
        public void The_floor_is_a_share_of_what_this_transmission_actually_makes()
        {
            // One watt was measured against a DEAD KEY and excludes almost
            // nothing on a voice envelope, which crosses it constantly on the
            // way down between syllables. With no commanded power known, the
            // floor is the peak term alone — the alarm's behaviour before
            // Sprint 47, exactly.
            Assert.Equal(10f, TransmitSafety.BelievableForwardFloorWatts(0, 100f), 3);
            Assert.Equal(TransmitSafety.ForwardFloorWatts,
                         TransmitSafety.BelievableForwardFloorWatts(0, 5f), 3);
            Assert.Equal(TransmitSafety.BelievableForwardFloorWatts(0, 0f),
                         TransmitSafety.ForwardFloorWatts, 3);
            Assert.Equal(TransmitSafety.BelievableForwardFloorWatts(0, float.NaN),
                         TransmitSafety.ForwardFloorWatts, 3);

            // Ten times the dead-key watt it replaces, so a voice trough no
            // longer sails over it...
            Assert.True(TransmitSafety.BelievableForwardFloorWatts(0, 100f)
                        > TransmitSafety.ForwardFloorWatts * 5f);

            // ...but not so high that most of a transmission stops being
            // judgeable, because the persistence rule then multiplies the delay
            // before a REAL fault is announced. That is the same trade the
            // register rules out for smoothing.
            Assert.True(TransmitSafety.ForwardFloorShareOfPeak <= 0.15f,
                "a floor much above a tenth of peak buys defence in depth with alarm latency");
        }

        [Fact]
        public void The_floor_follows_power_foldback_instead_of_the_power_setting()
        {
            // Why the SETTING cannot be the only reference. On 2026-08-22 the
            // same radio at the same setting made 101.2 W into a good load and
            // only 17.5 W into an empty port — that is the radio folding back
            // because of the very fault we are trying to catch.
            Assert.True(OpenForward < LoadForward / 5f);

            // A floor pinned to what the operator ASKED for does not move when
            // the radio folds back, so a bad enough mismatch climbs under it —
            // 4 W of forward power on a station set for a hundred is a worse
            // match than the one measured, and entirely possible. The
            // commanded term alone (what the display uses) sits at 5 W there.
            const float SeverelyFoldedBack = 4f;
            float settingOnly = TransmitSafety.BelievableForwardFloorWatts(100, float.NaN);
            Assert.True(settingOnly > SeverelyFoldedBack,
                "a setting-derived floor climbs above a badly folded-back transmission "
                + "and silences the alarm in the case it exists for");

            // With the measured peak in hand the floor follows it down and
            // keeps judging: a tenth of 4 W is under the absolute gate, so the
            // floor is the gate itself.
            Assert.Equal(TransmitSafety.ForwardFloorWatts,
                         TransmitSafety.BelievableForwardFloorWatts(100, SeverelyFoldedBack), 3);
            Assert.True(TransmitSafety.BelievableForwardFloorWatts(100, OpenForward) < OpenForward);

            var reading = Pair(OpenForward, OpenReflected, commandedWatts: 100);
            var run = RunOf(reading);
            Assert.True(run.ForwardPeakWatts >= OpenForward);
            Assert.Equal(100, run.CommandedWatts);
            Assert.True(TransmitSafety.ShouldWarnReflected(
                reading, run, Settled, tuning: false, alreadyWarned: false));
        }

        // ---- ONE floor, two gates (#571): checked against every measured case ----
        //
        // Two floors contradicted each other in two files until Sprint 47,
        // each arguing against the other in its own remarks, and the
        // 2026-09-07 run could not referee because both removed all seven
        // false highs on it. The three measured cases below are what the
        // brief said the unified floor MUST satisfy, and the numbers are
        // reported here so a reader can check the arithmetic rather than
        // trust a comment.

        [Fact]
        public void The_open_port_of_2026_08_22_is_still_judged_under_the_one_floor()
        {
            // 100 W commanded, radio folded back to a 17.5 W peak, 76 percent
            // back. The smaller of 5 W (commanded) and 1.75 W (peak) is 1.75 W,
            // over the absolute gate, so the floor is 1.75 W and 17.5 W is
            // judged. That is the floor the alarm used before, to the watt.
            float floor = TransmitSafety.BelievableForwardFloorWatts(100, OpenForward);

            Assert.Equal(1.75f, floor, 3);
            Assert.Equal(TransmitSafety.BelievableForwardFloorWatts(0, OpenForward), floor, 3);
            Assert.True(OpenForward >= floor, "our instrument must not go quiet on a real fault");
        }

        [Fact]
        public void The_dummy_load_run_of_2026_09_07_is_separated_by_the_one_floor()
        {
            // 100 W commanded, 107.27 W peak. The smaller of 5 W and 10.73 W is
            // 5 W. Every false high sat at or under 1.40 W forward: rejected.
            // The lowest good sample was 8.83 W: admitted. The peak-only floor
            // the alarm used before, 10.73 W, would have thrown that good
            // sample away too.
            const float Peak = 107.27f;
            const float HighestFalseHigh = 1.40f;
            const float LowestGoodSample = 8.83f;

            float floor = TransmitSafety.BelievableForwardFloorWatts(100, Peak);

            Assert.Equal(5f, floor, 3);
            Assert.True(HighestFalseHigh < floor, "the worst false high must be rejected");
            Assert.True(LowestGoodSample >= floor, "the lowest good sample must be admitted");

            float peakOnly = TransmitSafety.BelievableForwardFloorWatts(0, Peak);
            Assert.True(LowestGoodSample < peakOnly,
                "the old peak-only floor rejected a sample the bench proved good — "
                + "that is why the commanded term caps it");
        }

        [Fact]
        public void The_open_port_at_five_watts_of_2026_09_01_is_still_judged_under_the_one_floor()
        {
            // 5 W commanded, 4.1 W peak, 76 percent back. The smaller of 0.25 W
            // and 0.41 W is under the absolute gate, so the floor is the gate,
            // 1 W, and 4.1 W is judged. Again exactly the alarm's floor from
            // before.
            const float Forward0901 = 4.1f;
            float floor = TransmitSafety.BelievableForwardFloorWatts(5, Forward0901);

            Assert.Equal(TransmitSafety.ForwardFloorWatts, floor, 3);
            Assert.Equal(TransmitSafety.BelievableForwardFloorWatts(0, Forward0901), floor, 3);
            Assert.True(Forward0901 >= floor);
        }

        [Fact]
        public void The_absolute_gate_is_one_number_and_every_arithmetic_path_stands_on_it()
        {
            // Three absolute floors answered this question until Sprint 47 —
            // 1 W, 0.25 W and 0.05 W. This pins the survivors to the one: the
            // share arithmetic, the SWR arithmetic and the display's floor all
            // refuse just under it and answer at it.
            float gate = TransmitSafety.ForwardFloorWatts;
            float justUnder = gate - 0.01f;

            Assert.True(float.IsNaN(TransmitSafety.ReflectedFractionOf(justUnder, 0.1f)));
            Assert.False(float.IsNaN(TransmitSafety.ReflectedFractionOf(gate, 0.1f)));

            float underDbm = (float)(10.0 * Math.Log10(justUnder * 1000.0));
            float atDbm = (float)(10.0 * Math.Log10(gate * 1.001f * 1000.0));
            Assert.True(float.IsNaN(FlexBase.SwrFromPower(underDbm, underDbm - 20f)));
            Assert.False(float.IsNaN(FlexBase.SwrFromPower(atDbm, atDbm - 20f)));

            Assert.Equal(gate, FlexBase.MinBelievableForwardWatts(1), 3);
            Assert.Equal(gate, TransmitSafety.BelievableForwardFloorWatts(0, 0f), 3);
        }

        [Fact]
        public void The_display_asks_for_the_commanded_term_alone_and_that_is_stricter_early_in_an_over()
        {
            // The peak term is weakest while the peak is still climbing. On
            // the 2026-09-07 run a commanded-only floor rejects the 1.40 W
            // offender from the first sample; a peak-only floor admits it for
            // as long as the peak is under 14 W. The alarm can carry that
            // because it settles and persists; the display cannot, so it
            // passes no peak.
            const float HighestFalseHigh = 1.40f;
            const float EarlyPeak = 12f;

            Assert.True(HighestFalseHigh < FlexBase.MinBelievableForwardWatts(100));
            Assert.True(HighestFalseHigh >= TransmitSafety.BelievableForwardFloorWatts(0, EarlyPeak));
            Assert.True(HighestFalseHigh < TransmitSafety.BelievableForwardFloorWatts(100, 107.27f));
        }

        [Fact]
        public void A_syllable_trough_is_below_the_floor_and_is_not_judged()
        {
            // Defence in depth behind the pairing: even if a trough reading were
            // somehow coherent, it is not near the envelope peak and the
            // measurement there means nothing.
            var run = new ReflectedPowerRun();
            run.Observe(Pair(100f, 1f), 1);       // the peak this over reached
            Assert.Equal(10f, run.FloorWatts, 3);

            Assert.False(run.Observe(Pair(2f, 1f), 2),
                "2 W is nothing on a hundred-watt envelope");
            Assert.Equal(0, run.BadSamples);
        }

        // ---- persistence over judgeable samples (#453) ----

        [Fact]
        public void Two_bad_samples_are_not_enough_and_three_are()
        {
            // A voice envelope hands over two consecutive samples of anything
            // for free, which is why "the warning fired on an earlier tick and
            // the cut reads this one" was no defence here.
            var reading = Pair(OpenForward, OpenReflected);

            for (int samples = 0; samples < TransmitSafety.ReflectedWarnSustainedSamples; samples++)
            {
                Assert.False(TransmitSafety.ShouldWarnReflected(
                    reading, RunOf(reading, samples), Settled,
                    tuning: false, alreadyWarned: false),
                    samples + " bad samples should not be enough");
            }

            Assert.True(TransmitSafety.ShouldWarnReflected(
                reading, RunOf(reading, TransmitSafety.ReflectedWarnSustainedSamples),
                Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void One_good_judgeable_sample_resets_the_run()
        {
            // A judgeable good sample is evidence the antenna is fine, so it
            // clears the count. An UNJUDGEABLE sample is not evidence of
            // anything and must not clear it — otherwise, with the floor near
            // the envelope peaks, the run could never accumulate at all and the
            // alarm would never fire.
            var run = new ReflectedPowerRun();
            var bad = Pair(OpenForward, OpenReflected);
            var goodMatch = Pair(OpenForward, 0.02f);
            var belowFloor = Pair(0.5f, 0.4f);

            run.Observe(bad, 1);
            run.Observe(bad, 2);
            Assert.Equal(2, run.BadSamples);

            run.Observe(belowFloor, 3);
            Assert.Equal(2, run.BadSamples);

            run.Observe(Pair(2f, 1f, skewMs: 80f), 4);
            Assert.Equal(2, run.BadSamples);

            run.Observe(goodMatch, 5);
            Assert.Equal(0, run.BadSamples);
        }

        [Fact]
        public void A_fresh_transmission_does_not_inherit_the_last_one()
        {
            var run = RunOf(Pair(OpenForward, OpenReflected));
            Assert.True(run.Sustained);

            run.Reset();

            Assert.Equal(0, run.BadSamples);
            Assert.Equal(0, run.JudgedSamples);
            Assert.Equal(0f, run.ForwardPeakWatts);
            Assert.False(run.Sustained);
        }

        [Fact]
        public void The_two_bench_readings_are_nowhere_near_the_threshold()
        {
            // The threshold is only defensible because the measured cases sit in
            // a huge empty gap either side of it. If a future change narrows
            // that gap, 40 percent stops being a measurement and becomes a
            // guess, and this test is where that shows up.
            float open = TransmitSafety.ReflectedFractionOf(OpenForward, OpenReflected);
            float load = TransmitSafety.ReflectedFractionOf(LoadForward, LoadReflected);

            Assert.True(open > 0.70f, "open port measured 76 percent back; got " + open);
            Assert.True(load < 0.01f, "dummy load measured 0.05 percent back; got " + load);
            Assert.True(open > TransmitSafety.ReflectedWarnFraction * 1.5f);
            Assert.True(load < TransmitSafety.ReflectedWarnFraction / 10f);
        }

        [Fact]
        public void The_sentence_names_the_port_when_the_radio_knows_it()
        {
            // "Check the antenna" is advice. "Check ANT1" is an instruction —
            // and the operator cannot read the labels on the back panel.
            string named = TransmitSafety.ReflectedWarningText(0.76f, "ANT1");

            Assert.Contains("ANT1", named);
            Assert.Contains("76", named);
        }

        [Fact]
        public void A_declared_dummy_load_does_not_silence_the_warning()
        {
            // The gate that was here originally, copied from the dead-carrier
            // check where skipping IS correct, would have silenced this warning
            // in the exact scenario it was written for: on 2026-08-22 the load
            // was connected to the port that was not selected. A declared dummy
            // load makes a high reflected reading MORE diagnostic, not less,
            // because the operator has just told us to expect nothing back.
            //
            // ShouldWarnReflected deliberately takes no dummy-load parameter, so
            // there is no knob to get backwards a second time. This test stands
            // guard over the wording instead.
            string s = TransmitSafety.ReflectedWarningText(0.76f, "ANT2", dummyLoadDeclared: true);

            Assert.NotEmpty(s);
            Assert.Contains("76", s);
            Assert.Contains("ANT2", s);
            Assert.Contains("dummy load", s);
        }

        [Fact]
        public void Without_a_declared_load_the_sentence_does_not_mention_one()
        {
            // The negative control for the test above. A sentence that always
            // mentioned a dummy load would pass it while being wrong for every
            // operator on a real antenna.
            string s = TransmitSafety.ReflectedWarningText(0.76f, "ANT2");

            Assert.DoesNotContain("dummy load", s);
        }

        [Fact]
        public void The_sentence_still_works_when_the_antenna_is_unknown()
        {
            // A missing antenna name must not produce "coming back on ." or a
            // dangling placeholder read aloud as "open brace antenna".
            string plain = TransmitSafety.ReflectedWarningText(0.76f, "");

            Assert.Contains("76", plain);
            Assert.DoesNotContain("{", plain);
            Assert.DoesNotContain("  ", plain);
        }

        // ---- the reflected-power CUT (#224) ----
        //
        // The measured bad case: 13.4 of 17.5 watts coming straight back.

        [Fact]
        public void The_cut_never_fires_unless_the_operator_turned_it_on()
        {
            // The worst measured case, and still no: an app that unilaterally
            // unkeys a transmitter has taken the station away. The setting is
            // the operator's, not ours.
            Assert.False(TransmitSafety.ShouldCutReflected(
                settingEnabled: false, alreadyWarned: true,
                Pair(17.5f, 13.4f), tuning: false));
        }

        [Fact]
        public void The_cut_requires_the_warning_to_have_fired_on_an_earlier_sample()
        {
            // The two-samples rule by reuse: the PA ramps, and a single bad
            // sample at key-down is a transient, not a load. Since #453 the
            // warning behind it needs a sustained run of its own, so this is
            // four judgeable bad samples, not two.
            Assert.False(TransmitSafety.ShouldCutReflected(
                true, alreadyWarned: false, Pair(17.5f, 13.4f), tuning: false));
            Assert.True(TransmitSafety.ShouldCutReflected(
                true, alreadyWarned: true, Pair(17.5f, 13.4f), tuning: false));
        }

        [Fact]
        public void Below_the_power_floor_the_alarm_warns_but_never_cuts()
        {
            // The bench dead key measured 0.22 W into an open port — harmless,
            // and cutting there costs the operator a contact for nothing. Ten
            // watts exactly is still "telling", not "stopping".
            //
            // Ten watts is Noel's ruling of 2026-08-25 and is deliberately NOT
            // replaced by the run's scaled floor — a share of the peak would sit
            // above it on any full-power transmission, which would be quietly
            // raising a number a human set.
            Assert.False(TransmitSafety.ShouldCutReflected(true, true, Pair(0.22f, 0.17f), false));
            Assert.False(TransmitSafety.ShouldCutReflected(
                true, true, Pair(TransmitSafety.ReflectedCutMinForwardWatts, 8f), false));
            Assert.True(TransmitSafety.ShouldCutReflected(true, true, Pair(11f, 8f), false));
        }

        [Fact]
        public void A_tuner_mid_cycle_is_never_cut()
        {
            // An ATU tune transmits into a deliberately bad match and walks
            // toward a good one; high reflected power during one is the tuner
            // WORKING. A cut here would kill every tune-up.
            Assert.True(TransmitSafety.ShouldCutReflected(true, true, Pair(17.5f, 13.4f), false));
            Assert.False(TransmitSafety.ShouldCutReflected(true, true, Pair(17.5f, 13.4f), true));
        }

        [Fact]
        public void An_unreadable_meter_never_cuts()
        {
            Assert.False(TransmitSafety.ShouldCutReflected(
                true, true, Pair(float.NaN, 13.4f), false));
            Assert.False(TransmitSafety.ShouldCutReflected(
                true, true, Pair(17.5f, float.NaN), false));
            Assert.False(TransmitSafety.ShouldCutReflected(
                true, true, TransmitPowerReading.None, false));
        }

        // ---- tier 1 (#571): the WATTS rung, which did not exist ----
        //
        // #237 ruled the protective ladder in absolute reflected watts and
        // #224 described the cut as firing above ten watts, and the shipped
        // code did neither: ShouldCutReflected above is a RATIO test with a
        // forward floor, so it inherited the very defect tier 1 was meant to
        // be immune to. Reflected watts is what heats the finals and a voice
        // trough cannot fake it. Ten watts is a first number from an entry,
        // not a measurement; the empty-port bench test is what moves it.

        /// <summary>
        /// A run that has already seen this reading <paramref name="times"/>
        /// times, as the live paths would have observed it, with no share
        /// warning ever having fired.
        /// </summary>
        private static ReflectedPowerRun Hot(TransmitPowerReading reading, int times)
        {
            var run = new ReflectedPowerRun();
            for (int i = 0; i < times; i++) run.Observe(reading, i + 1);
            return run;
        }

        [Fact]
        public void Ten_watts_back_cuts_without_the_share_warning_ever_having_fired()
        {
            // THE positive control for the rung, and #237's own case: 100 W at
            // a 2.5-to-1 match sends 18 W back at 18 percent — under the
            // 40-percent warning, so the share rung can never cut here, and
            // over ten watts of heat, so this one must.
            var mediocreMatchAtFullPower = Pair(100f, 18f);
            var run = Hot(mediocreMatchAtFullPower, 2);

            Assert.True(mediocreMatchAtFullPower.ReflectedShare < TransmitSafety.ReflectedWarnFraction,
                "the case only proves independence if the share rung is silent on it");
            Assert.False(TransmitSafety.ShouldCutReflected(
                true, alreadyWarned: false, mediocreMatchAtFullPower, tuning: false));

            Assert.True(TransmitSafety.ShouldCutReflectedWatts(
                true, mediocreMatchAtFullPower, run, tuning: false));
            Assert.Equal(TransmitSafety.ReflectedCut.Watts,
                TransmitSafety.JudgeReflectedCut(
                    true, alreadyWarned: false, mediocreMatchAtFullPower, run, tuning: false));
        }

        [Fact]
        public void The_watts_rung_fires_on_the_recorded_fault_of_2026_08_22_and_not_on_2026_09_01()
        {
            // 13.4 W back is over the rung; 3.10 W back at five watts is
            // under it, correctly — three watts is not hurting anything, and
            // the share warning covers it. Watts measure heat; ratio measures
            // match (#237). Both behaviours are right.
            var open0822 = Pair(OpenForward, OpenReflected);
            Assert.True(TransmitSafety.ShouldCutReflectedWatts(true, open0822, Hot(open0822, 2), false));

            var open0901 = Pair(4.1f, 3.10f);
            Assert.False(TransmitSafety.ShouldCutReflectedWatts(true, open0901, Hot(open0901, 5), false));
            // ...while the share rung still warns on it, as ever.
            Assert.True(TransmitSafety.ShouldWarnReflected(
                open0901, Hot(open0901, 3), Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void The_watts_rung_needs_two_coherent_samples_in_a_row()
        {
            // #224's two-distinct-samples rule applies to both rungs: a single
            // hot sample at key-down is a transient, not a load.
            var hot = Pair(100f, 18f);
            Assert.False(TransmitSafety.ShouldCutReflectedWatts(true, hot, Hot(hot, 1), false));
            Assert.True(TransmitSafety.ShouldCutReflectedWatts(true, hot, Hot(hot, 2), false));
            Assert.Equal(2, TransmitSafety.ReflectedCutSustainedSamples);

            // A coherent sample under the rung ends the streak; an incoherent
            // one neither ends nor extends it.
            var run = new ReflectedPowerRun();
            run.Observe(hot, 1);
            run.Observe(Pair(100f, 2f), 2);
            Assert.Equal(0, run.HotSamples);
            run.Observe(hot, 3);
            run.Observe(Pair(100f, 18f, skewMs: 80f), 4);
            Assert.Equal(1, run.HotSamples);
            run.Observe(hot, 5);
            Assert.Equal(2, run.HotSamples);
        }

        [Fact]
        public void The_watts_rung_is_immune_to_the_voice_trough_that_faked_the_ratio()
        {
            // The 2026-09-07 run: reflected never exceeded 0.105 W across 194
            // samples while the display read 2.96. A watts-based rung stays
            // silent through every one of those false highs, because a trough
            // makes forward SMALL and a small forward cannot have ten watts of
            // itself coming back.
            var worstTrough = Pair(0.12f, 0.105f, commandedWatts: 100);
            var run = Hot(worstTrough, 10);

            Assert.Equal(0, run.HotSamples);
            Assert.False(TransmitSafety.ShouldCutReflectedWatts(true, worstTrough, run, false));
            Assert.Equal(TransmitSafety.ReflectedCut.None,
                TransmitSafety.JudgeReflectedCut(true, true, worstTrough, run, false));
        }

        [Fact]
        public void The_watts_rung_respects_the_setting_the_tuner_and_coherence()
        {
            var hot = Pair(100f, 18f);
            var run = Hot(hot, 3);

            Assert.False(TransmitSafety.ShouldCutReflectedWatts(false, hot, run, false),
                "the setting is the operator's, not ours");
            Assert.False(TransmitSafety.ShouldCutReflectedWatts(true, hot, run, tuning: true),
                "high reflected power during a tune cycle is the tuner working");
            Assert.False(TransmitSafety.ShouldCutReflectedWatts(true, Pair(100f, 18f, skewMs: 80f), run, false),
                "an incoherent pair never ends a transmission");
            Assert.False(TransmitSafety.ShouldCutReflectedWatts(true, TransmitPowerReading.None, run, false));
            Assert.False(TransmitSafety.ShouldCutReflectedWatts(true, hot, null, false));
        }

        [Fact]
        public void Nine_watts_back_at_full_power_cuts_on_neither_rung()
        {
            // The negative control for the rung's number: just under ten
            // watts back, on a share the warning would never fire on.
            var justUnder = Pair(100f, 9.9f);
            var run = Hot(justUnder, 5);

            Assert.Equal(0, run.HotSamples);
            Assert.Equal(TransmitSafety.ReflectedCut.None,
                TransmitSafety.JudgeReflectedCut(true, alreadyWarned: false, justUnder, run, false));
        }

        [Fact]
        public void When_both_rungs_would_fire_the_share_rung_names_the_cut()
        {
            // The operator has just heard "76 percent coming back"; the cut
            // sentence continues that story in the same unit.
            var open = Pair(OpenForward, OpenReflected);
            var run = Hot(open, 4);

            Assert.True(TransmitSafety.ShouldCutReflected(true, true, open, false));
            Assert.True(TransmitSafety.ShouldCutReflectedWatts(true, open, run, false));
            Assert.Equal(TransmitSafety.ReflectedCut.Share,
                TransmitSafety.JudgeReflectedCut(true, alreadyWarned: true, open, run, false));
            Assert.Equal(TransmitSafety.ReflectedCut.Watts,
                TransmitSafety.JudgeReflectedCut(true, alreadyWarned: false, open, run, false));
        }

        [Fact]
        public void Each_rung_speaks_in_its_own_unit_and_never_the_others()
        {
            // #237's standing rule: protective watts and diagnostic SWR may
            // never be stated in each other's units. Read as sentences.
            string watts = TransmitSafety.ReflectedCutWattsText(13.4f, "ANT1");
            string share = TransmitSafety.ReflectedCutText(0.76f, "ANT1");

            Assert.Contains("13 watts", watts);
            Assert.DoesNotContain("percent", watts);
            Assert.DoesNotContain("%", watts);
            Assert.Contains("no longer on the air", watts);
            Assert.Contains("ANT1", watts);
            Assert.DoesNotContain("{", watts);

            Assert.Contains("76 percent", share);
            Assert.DoesNotContain("watt", share);
            Assert.Contains("no longer on the air", share);

            string plain = TransmitSafety.ReflectedCutWattsText(13.4f, "");
            Assert.Contains("13 watts", plain);
            Assert.DoesNotContain("{", plain);
            Assert.DoesNotContain("  ", plain);
        }

        [Fact]
        public void The_cut_sentence_for_a_rung_is_that_rungs_sentence()
        {
            var open = Pair(OpenForward, OpenReflected);
            Assert.Equal(TransmitSafety.ReflectedCutText(open.ReflectedShare, "ANT1"),
                TransmitSafety.ReflectedCutTextFor(TransmitSafety.ReflectedCut.Share, open, "ANT1"));
            Assert.Equal(TransmitSafety.ReflectedCutWattsText(open.ReflectedWatts, "ANT1"),
                TransmitSafety.ReflectedCutTextFor(TransmitSafety.ReflectedCut.Watts, open, "ANT1"));
            Assert.Equal("",
                TransmitSafety.ReflectedCutTextFor(TransmitSafety.ReflectedCut.None, open, "ANT1"));
        }

        [Fact]
        public void The_two_ten_watt_numbers_are_two_rulings_and_the_test_says_so()
        {
            // ReflectedCutMinForwardWatts is the share rung's FORWARD floor
            // (#224); ReflectedCutWatts is REFLECTED heat (#237). They are
            // equal today by coincidence. This is not an equality test — it
            // records that the coincidence is known, so nobody "fixes" one by
            // deriving it from the other.
            Assert.Equal(10f, TransmitSafety.ReflectedCutWatts);
            Assert.Equal(10f, TransmitSafety.ReflectedCutMinForwardWatts);
        }

        // ---- the disarmed reminder (#224, ruled defeatable 2026-08-30) ----
        //
        // The cut has an off switch now, and the mitigation that came WITH the
        // ruling is that the alarm must not let the operator forget they used
        // it: a defeatable safety that is off and still trusted is worse than
        // no safety, because it is trusted.

        /// <summary>
        /// The reminder's own words, in ONE place, because the positive test
        /// and its negative control must move together. They did not on
        /// 2026-08-30: the control asserted the armed sentence lacked "turned
        /// off", and Noel's rewording to "disabled" would have left that
        /// assertion true of every possible sentence — a control that passes
        /// for a string it can no longer fail on is not a control. Wording is
        /// Noel's, ruled the same day; changing it here is deliberate, and it
        /// is meant to be.
        /// </summary>
        private const string DisarmedPhrase = "cutoff setting is disabled";

        [Fact]
        public void With_the_cut_disarmed_the_warning_says_no_cut_is_coming()
        {
            // The moment the cut would have acted is the one moment the
            // operator must be reminded they turned it off.
            string s = TransmitSafety.ReflectedWarningText(
                0.76f, "ANT1", dummyLoadDeclared: false, cutDisarmed: true);

            Assert.Contains(DisarmedPhrase, s);
            Assert.Contains("ANT1", s);
            Assert.Contains("76", s);
            Assert.DoesNotContain("{", s);
        }

        [Fact]
        public void With_the_cut_armed_the_warning_keeps_quiet_about_the_setting()
        {
            // The negative control. A sentence that always mentioned the
            // setting would pass the test above while burying the reminder in
            // routine noise — which is how an operator learns to stop hearing
            // it, the exact failure the reminder exists to prevent.
            Assert.DoesNotContain(DisarmedPhrase,
                TransmitSafety.ReflectedWarningText(0.76f, "ANT1"));
            Assert.DoesNotContain(DisarmedPhrase,
                TransmitSafety.ReflectedWarningText(
                    0.76f, "ANT1", dummyLoadDeclared: false, cutDisarmed: false));
        }

        [Fact]
        public void The_disarmed_reminder_composes_with_every_variant_of_the_sentence()
        {
            // Named or unnamed antenna, declared load or not — the reminder
            // rides along in all four shapes, with no dangling placeholder.
            foreach (bool named in new[] { true, false })
                foreach (bool dummy in new[] { true, false })
                {
                    string s = TransmitSafety.ReflectedWarningText(
                        0.76f, named ? "ANT2" : "", dummy, cutDisarmed: true);

                    Assert.Contains(DisarmedPhrase, s);
                    Assert.DoesNotContain("{", s);
                }
        }

        [Fact]
        public void The_cut_sentence_says_you_are_no_longer_on_the_air()
        {
            // A blind operator whose transmit was cut has no visual cue and
            // will keep talking. The one thing the words must never leave in
            // doubt is that the transmission has ENDED.
            string named = TransmitSafety.ReflectedCutText(0.76f, "ANT1");
            Assert.Contains("no longer on the air", named);
            Assert.Contains("ANT1", named);
            Assert.Contains("76", named);

            string plain = TransmitSafety.ReflectedCutText(0.76f, "");
            Assert.Contains("no longer on the air", plain);
            Assert.DoesNotContain("{", plain);
        }
    }

    /// <summary>
    /// The "check microphone" warning, which fired on a station that was
    /// audible on the air and making contacts (#459).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The readings are the Fixer run on Don's 6300 on 2026-08-31, in dBFS:
    /// his SPOKEN SC_MIC measured <b>-92.59</b>, the injected tone in the same
    /// run measured <b>-31.81</b>, and a path that delivers nothing at all
    /// reads the <b>-150</b> floor. He was heard on the air throughout. A rule
    /// that calls -92.59 silent is wrong whatever else it gets right.
    /// </para>
    /// <para>
    /// These are the real numbers on purpose. The threshold that caused the
    /// defect was itself invented from a bench measurement that did not
    /// describe live operating, and a test written from invented values would
    /// have agreed with it.
    /// </para>
    /// </remarks>
    public class MicPathVerificationTests
    {
        private const float NothingArrived = -150f;
        private const float DonSpeaking = -92.59f;
        private const float InjectedTone = -31.81f;

        /// <summary>
        /// The tests written before 2026-09-02 all describe a meter that HAS
        /// reported — which is what <c>meterReported: true</c> asserts. The
        /// tests under "a floor is not a silence" are the ones about a meter
        /// that has not (#502).
        /// </summary>
        private static TransmitSafety.MicPathVerdict Judge(float peak, double txSeconds) =>
            TransmitSafety.JudgeMicPath(peak, txSeconds, meterReported: true);

        // ---- a floor is not a silence (#502) ----

        [Fact]
        public void A_meter_that_never_reported_is_not_evidence_of_silence()
        {
            // THE regression test for #502. Don's 6300 publishes three SC_MIC
            // copies and the app was bound to one that never delivers a sample,
            // so the peak sat at the -150 floor through a transmission whose
            // audio the transmit monitor was playing back. The old rule read
            // that floor as "nothing arrived" and told a working operator his
            // microphone was dead.
            Assert.NotEqual(TransmitSafety.MicPathVerdict.NothingArrived,
                TransmitSafety.JudgeMicPath(NothingArrived,
                    TransmitSafety.MicVerifyWindowSeconds, meterReported: false));
            Assert.NotEqual(TransmitSafety.MicPathVerdict.NothingArrived,
                TransmitSafety.JudgeMicPath(NothingArrived,
                    TransmitSafety.MicVerifyWindowSeconds * 100, meterReported: false));
        }

        [Fact]
        public void Without_telemetry_the_window_ends_in_no_verdict_not_a_warning()
        {
            Assert.Equal(TransmitSafety.MicPathVerdict.KeepWatching,
                TransmitSafety.JudgeMicPath(NothingArrived, txSeconds: 5, meterReported: false));
            Assert.Equal(TransmitSafety.MicPathVerdict.NoTelemetry,
                TransmitSafety.JudgeMicPath(NothingArrived,
                    TransmitSafety.MicVerifyWindowSeconds, meterReported: false));
        }

        [Fact]
        public void A_peak_without_a_sample_behind_it_is_not_believed_either()
        {
            // A peak is a claim about samples. If the caller says none arrived,
            // the number is stale or fabricated, and it verifies nothing.
            Assert.Equal(TransmitSafety.MicPathVerdict.KeepWatching,
                TransmitSafety.JudgeMicPath(DonSpeaking, txSeconds: 1, meterReported: false));
            Assert.Equal(TransmitSafety.MicPathVerdict.NoTelemetry,
                TransmitSafety.JudgeMicPath(DonSpeaking,
                    TransmitSafety.MicVerifyWindowSeconds, meterReported: false));
        }

        [Fact]
        public void The_floor_from_a_meter_that_IS_reporting_still_warns()
        {
            // The positive control for the telemetry gate: making the warning
            // refuse to fire without a sample is only defensible if a real
            // floor — a meter streaming -150 while keyed — still gets through.
            Assert.Equal(TransmitSafety.MicPathVerdict.NothingArrived,
                TransmitSafety.JudgeMicPath(NothingArrived,
                    TransmitSafety.MicVerifyWindowSeconds, meterReported: true));
        }

        // The threshold the old warning judged by. Left here as a fact about
        // the defect, not as a rule: the presence test does not use it.
        private const float OldSilentMicDbfs = -45f;

        [Fact]
        public void The_operator_who_is_audible_on_the_air_is_never_called_silent()
        {
            // THE regression test. -92.59 dBFS sits 47 dB below the threshold
            // the old warning used, so he was told his microphone was dead on
            // every transmission while people were answering him.
            Assert.True(DonSpeaking < OldSilentMicDbfs,
                "the measurement really is below the old threshold — that is the defect");

            Assert.Equal(TransmitSafety.MicPathVerdict.Verified,
                Judge(DonSpeaking, txSeconds: 0.5));
            Assert.Equal(TransmitSafety.MicPathVerdict.Verified,
                Judge(DonSpeaking,
                    TransmitSafety.MicVerifyWindowSeconds * 100));
        }

        [Fact]
        public void A_healthy_injected_tone_verifies_too()
        {
            // The other end of the same run, on the same radio, minutes apart.
            Assert.Equal(TransmitSafety.MicPathVerdict.Verified,
                Judge(InjectedTone, txSeconds: 1));
        }

        [Fact]
        public void Nothing_arriving_at_all_is_still_reported()
        {
            // The positive control the whole change hangs on. Making the
            // warning quieter is only defensible if the fault it exists for
            // still gets through: the floor sentinel means the device, the
            // profile or the microphone is wrong, and the operator is putting
            // out a carrier with no audio on it.
            Assert.Equal(TransmitSafety.MicPathVerdict.NothingArrived,
                Judge(NothingArrived,
                    TransmitSafety.MicVerifyWindowSeconds));
            Assert.Equal(TransmitSafety.MicPathVerdict.NothingArrived,
                Judge(NothingArrived,
                    TransmitSafety.MicVerifyWindowSeconds + 30));
        }

        [Fact]
        public void Thinking_before_speaking_is_not_a_dead_microphone()
        {
            // Five seconds of gathering your thoughts with the key down is
            // ordinary operating, and it was the whole warning window. The
            // window is ten seconds now and nothing is said until it is out.
            Assert.True(TransmitSafety.MicVerifyWindowSeconds >= 10.0);
            Assert.Equal(TransmitSafety.MicPathVerdict.KeepWatching,
                Judge(NothingArrived, txSeconds: 5));
            Assert.Equal(TransmitSafety.MicPathVerdict.KeepWatching,
                Judge(NothingArrived,
                    TransmitSafety.MicVerifyWindowSeconds - 0.1));
        }

        [Fact]
        public void The_verdict_can_go_from_watching_to_verified_but_never_back()
        {
            // Latching the SUCCESS is the shape of the fix. The old code formed
            // its verdict on one tick and latched the FAILURE, so a "silent"
            // verdict at five seconds could be contradicted by the meter before
            // the sentence finished being spoken. Replayed as a sequence: quiet,
            // quiet, then he speaks.
            var readings = new[]
            {
                (peak: NothingArrived, at: 1.0),
                (peak: NothingArrived, at: 4.0),
                (peak: DonSpeaking,    at: 6.0),
                (peak: DonSpeaking,    at: 30.0),
            };

            var verdicts = new List<TransmitSafety.MicPathVerdict>();
            foreach (var r in readings)
                verdicts.Add(Judge(r.peak, r.at));

            Assert.Equal(TransmitSafety.MicPathVerdict.KeepWatching, verdicts[0]);
            Assert.Equal(TransmitSafety.MicPathVerdict.KeepWatching, verdicts[1]);
            Assert.Equal(TransmitSafety.MicPathVerdict.Verified, verdicts[2]);
            Assert.Equal(TransmitSafety.MicPathVerdict.Verified, verdicts[3]);
            Assert.DoesNotContain(TransmitSafety.MicPathVerdict.NothingArrived, verdicts);
        }

        // ---- how long a proof lasts, and what ends it ----

        private const string PathA = "SERIAL-A|MIC|radio|";
        private const string PathB = "SERIAL-A|PC|pc|";

        [Fact]
        public void A_proven_path_is_not_re_examined_on_every_over()
        {
            Assert.True(TransmitSafety.MicVerificationStillHolds(
                haveVerification: true, secondsSinceVerified: 120, PathA, PathA));
        }

        [Fact]
        public void A_proof_expires()
        {
            Assert.False(TransmitSafety.MicVerificationStillHolds(
                true, TransmitSafety.MicVerifiedForSeconds + 1, PathA, PathA));
        }

        [Fact]
        public void A_changed_audio_path_throws_the_proof_away_immediately()
        {
            // The addition that keeps the fix from becoming its own defect: a
            // clock alone would suppress the warning for up to ten minutes
            // after a microphone was unplugged or the transmit chain
            // re-pointed, which is exactly the shape of the bug being fixed.
            Assert.False(TransmitSafety.MicVerificationStillHolds(
                true, secondsSinceVerified: 1, PathA, PathB));
        }

        [Fact]
        public void Nothing_proven_holds_nothing()
        {
            Assert.False(TransmitSafety.MicVerificationStillHolds(
                haveVerification: false, 1, PathA, PathA));
        }

        [Fact]
        public void The_signature_moves_when_any_part_of_the_path_moves()
        {
            string baseline = TransmitSafety.MicPathSignature("0123-4567", "MIC", false, "");

            Assert.NotEqual(baseline,
                TransmitSafety.MicPathSignature("9999-9999", "MIC", false, ""));   // other radio
            Assert.NotEqual(baseline,
                TransmitSafety.MicPathSignature("", "MIC", false, ""));            // disconnected
            Assert.NotEqual(baseline,
                TransmitSafety.MicPathSignature("0123-4567", "PC", false, ""));    // mic source
            Assert.NotEqual(baseline,
                TransmitSafety.MicPathSignature("0123-4567", "MIC", true, ""));    // PC audio on
            Assert.NotEqual(baseline,
                TransmitSafety.MicPathSignature("0123-4567", "MIC", false, "USB Mic"));
            Assert.Equal(baseline,
                TransmitSafety.MicPathSignature("0123-4567", "MIC", false, ""));
        }

        [Fact]
        public void The_holder_forgets_a_path_it_is_told_has_changed()
        {
            MicPathVerification.ResetForTests();
            try
            {
                Assert.False(MicPathVerification.Holds(PathA));

                MicPathVerification.NoteVerified(PathA);
                Assert.True(MicPathVerification.Holds(PathA));

                // A different path is never covered by an old proof...
                Assert.False(MicPathVerification.Holds(PathB));

                MicPathVerification.NoteVerified(PathA);
                MicPathVerification.Invalidate("a microphone profile was applied");
                // ...and neither is the same one once something says so.
                Assert.False(MicPathVerification.Holds(PathA));
            }
            finally { MicPathVerification.ResetForTests(); }
        }

        // ---- present but low is a different fault (#459 part C) ----

        [Fact]
        public void A_low_but_present_level_is_advice_and_not_the_missing_path_alarm()
        {
            // One threshold was doing two jobs, which is why a low-but-working
            // station got an alarm worded for a dead one.
            Assert.True(TransmitSafety.ShouldAdviseMicLevel(DonSpeaking, OldSilentMicDbfs));
            Assert.False(TransmitSafety.ShouldAdviseMicLevel(InjectedTone, OldSilentMicDbfs));
        }

        [Fact]
        public void The_missing_path_case_never_also_produces_level_advice()
        {
            // The floor is the other fault, and telling somebody with no audio
            // path at all to adjust their gain would be worse than saying
            // nothing.
            Assert.False(TransmitSafety.ShouldAdviseMicLevel(NothingArrived, OldSilentMicDbfs));
            Assert.False(TransmitSafety.ShouldAdviseMicLevel(float.NaN, OldSilentMicDbfs));
        }
    }

    /// <summary>
    /// Both live alarm paths tell ReflectedWarningText whether the cut is
    /// disarmed (#224).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source-read, in the TransmitKillSwitchRoutingTests family, and for the
    /// same reason: the parameter is optional — it has to be, or every test of
    /// the sentence itself would be forced to answer a wiring question — so a
    /// live call site that forgets it compiles clean, reviews clean, and
    /// silently reverts the warning to trusting a cut that is off. A
    /// behavioural test cannot reach either site: both need a FlexBase.
    /// </para>
    /// <para>
    /// The sweep proves it looked (both files must yield at least one call
    /// site) before it proves anything else — a broken path constant would
    /// otherwise read as a clean bill of health.
    /// </para>
    /// </remarks>
    public sealed class ReflectedWarningWiringTests
    {
        // The two live alarm paths. TransmitSafety.cs itself is not here — it
        // is the definition, not a caller — and test files are not here
        // because a test may legitimately omit the parameter.
        private static readonly string[] LiveAlarmFiles =
        {
            "JJFlexWpf/PttSafetyController.cs",
            "Radios/TransmitKillSwitch.cs",
        };

        [Fact]
        public void Every_live_warning_call_site_passes_cutDisarmed()
        {
            string root = RepoRoot();
            foreach (string rel in LiveAlarmFiles)
            {
                string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path),
                    "The sweep cannot find " + rel + " — fix the path, do not delete the test.");

                string text = File.ReadAllText(path);
                var calls = CallArgumentSpans(text, "ReflectedWarningText(");

                // Positive control: a file with no call sites means the sweep
                // (or the code) moved, not that all is well.
                Assert.True(calls.Count > 0,
                    rel + " has no ReflectedWarningText call site; if the "
                    + "warning moved, move this sweep with it.");

                foreach (string args in calls)
                {
                    Assert.True(args.Contains("cutDisarmed:"),
                        rel + " calls ReflectedWarningText without saying "
                        + "whether the cut is disarmed. With the setting off, "
                        + "that warning would silently stop reminding the "
                        + "operator that no cut is coming (#224): "
                        + Condense(args));
                }
            }
        }

        /// <summary>
        /// The manual-tune half of #453: both live alarm paths read
        /// <c>tuning</c> from the RADIO'S live state, every tick, and neither
        /// subscribes to the tuner start/stop event.
        /// </summary>
        /// <remarks>
        /// The event carries a start for the operator's tune carrier and no
        /// stop — the stop is raised only inside <c>FlexTunerOn</c>, which the
        /// carrier toggle does not go through — so a flag latched from it
        /// would silence the alarm permanently the first time a carrier was
        /// dropped by the kill switch, the radio's own timeout or another
        /// client. A behavioural test cannot reach either site (both need a
        /// FlexBase), and the pure rule's own no-memory test cannot see how it
        /// is fed. This reads the feed.
        /// </remarks>
        [Fact]
        public void Every_live_alarm_path_reads_tuning_from_the_radio_and_never_from_the_event()
        {
            string root = RepoRoot();
            foreach (string rel in LiveAlarmFiles)
            {
                string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path),
                    "The sweep cannot find " + rel + " — fix the path, do not delete the test.");
                string text = File.ReadAllText(path);

                // Positive control: the alarm is still judged in this file.
                Assert.True(text.Contains("JudgeReflected("),
                    rel + " no longer calls JudgeReflected; if the alarm moved, move this sweep with it.");

                Assert.True(text.Contains("rig.ATUTuneInProgress"),
                    rel + " no longer reads the radio's ATU cycle state for the tuning flag.");
                // The SUBSCRIPTION form, not the bare name: both files are
                // allowed — expected, even — to say in a comment why the
                // event is not used.
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(
                        text, @"FlexAntTunerStartStop\s*\+="),
                    rel + " subscribes to FlexAntTunerStartStop. That event has no stop for the "
                    + "operator's tune carrier, so anything latched from it disables the alarm "
                    + "for good (#453). Read rig.TxTune instead — the radio's state cannot latch.");
            }

            // The operator's own tune carrier is consulted where the operator's
            // transmission is judged — and only there. The kill switch's probe
            // carrier IS a tune carrier, and standing down on it would switch
            // the check watch off for every tune probe.
            string ptt = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "PttSafetyController.cs"));
            Assert.Contains("rig.TxTune", ptt);
        }

        /// <summary>
        /// Both live paths ask BOTH rungs of the cut (#571 tier 1), through the
        /// one combined judge, and speak the rung's own sentence.
        /// </summary>
        /// <remarks>
        /// The watts rung is an addition beside the share rung. An addition a
        /// caller can forget is not a safety rung, and each of these two
        /// files compiles perfectly well calling only the share rung — which
        /// is exactly what both did until Sprint 47. Source-read for the same
        /// reason as the sibling above: neither site is reachable without a
        /// FlexBase.
        /// </remarks>
        [Fact]
        public void Every_live_alarm_path_judges_both_rungs_of_the_cut()
        {
            string root = RepoRoot();
            foreach (string rel in LiveAlarmFiles)
            {
                string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path),
                    "The sweep cannot find " + rel + " — fix the path, do not delete the test.");
                string text = File.ReadAllText(path);

                // Positive control: the cut is still decided in this file.
                Assert.True(text.Contains("JudgeReflectedCut("),
                    rel + " no longer calls JudgeReflectedCut; if the cut moved, move this sweep with it.");
                Assert.True(text.Contains("ReflectedCutTextFor("),
                    rel + " does not speak the rung's own sentence through ReflectedCutTextFor.");

                // Neither rung may be consulted on its own by a live path.
                Assert.False(text.Contains("ShouldCutReflected("),
                    rel + " calls the share rung directly, which is how the watts rung gets forgotten.");
                Assert.False(text.Contains("ShouldCutReflectedWatts("),
                    rel + " calls the watts rung directly, which is how the share rung gets forgotten.");
                Assert.False(text.Contains("ReflectedCutText("),
                    rel + " speaks the share sentence directly, so a watts cut would be announced in percent (#237).");
            }
        }

        /// <summary>The argument text of each call, to the matching close paren.</summary>
        private static List<string> CallArgumentSpans(string text, string callToken)
        {
            var spans = new List<string>();
            int at = 0;
            while ((at = text.IndexOf(callToken, at, StringComparison.Ordinal)) >= 0)
            {
                int start = at + callToken.Length;
                int depth = 1;
                int i = start;
                while (i < text.Length && depth > 0)
                {
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')') depth--;
                    i++;
                }
                spans.Add(text.Substring(start, i - start - 1));
                at = i;
            }
            return spans;
        }

        private static string Condense(string s)
        {
            return string.Join(" ",
                s.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
