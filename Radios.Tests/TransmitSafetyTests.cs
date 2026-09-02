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
            float skewMs = 0f, float ageMs = 0f) =>
            new TransmitPowerReading(forwardWatts, reflectedWatts, skewMs, ageMs);

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
            for (int i = 0; i < samples; i++) run.Observe(reading);
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
            for (int i = 0; i < 20; i++) run.Observe(skewed);

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
            // way down between syllables.
            Assert.Equal(10f, TransmitSafety.ReflectedWarnFloorWatts(100f), 3);
            Assert.Equal(TransmitSafety.ReflectedWarnMinWatts,
                         TransmitSafety.ReflectedWarnFloorWatts(5f), 3);
            Assert.Equal(TransmitSafety.ReflectedWarnFloorWatts(0f),
                         TransmitSafety.ReflectedWarnMinWatts, 3);

            // Ten times the dead-key watt it replaces, so a voice trough no
            // longer sails over it...
            Assert.True(TransmitSafety.ReflectedWarnFloorWatts(100f)
                        > TransmitSafety.ReflectedWarnMinWatts * 5f);

            // ...but not so high that most of a transmission stops being
            // judgeable, because the persistence rule then multiplies the delay
            // before a REAL fault is announced. That is the same trade the
            // register rules out for smoothing.
            Assert.True(TransmitSafety.ReflectedWarnFloorShareOfPeak <= 0.15f,
                "a floor much above a tenth of peak buys defence in depth with alarm latency");
        }

        [Fact]
        public void The_floor_follows_power_foldback_instead_of_the_power_setting()
        {
            // Why the reference is the MEASURED peak and not the operator's set
            // power. On 2026-08-22 the same radio at the same setting made
            // 101.2 W into a good load and only 17.5 W into an empty port —
            // that is the radio folding back because of the very fault we are
            // trying to catch. A floor derived from a hundred-watt SETTING
            // would sit above everything a badly mismatched station can produce
            // and the alarm would go quiet in exactly the case it exists for.
            // The measured foldback establishes that the mechanism is real: the
            // same radio, the same setting, 101.2 W into a load and 17.5 W into
            // an empty port.
            Assert.True(OpenForward < LoadForward / 5f);

            // A floor pinned to what the operator ASKED for does not move when
            // the radio folds back, so a bad enough mismatch climbs under it —
            // 8 W of forward power on a station set for a hundred is a worse
            // match than the one measured, and entirely possible.
            const float SeverelyFoldedBack = 8f;
            Assert.True(TransmitSafety.ReflectedWarnFloorWatts(LoadForward) > SeverelyFoldedBack,
                "a setting-derived floor climbs above a badly folded-back transmission "
                + "and silences the alarm in the case it exists for");

            // A floor pinned to what the radio is actually MAKING follows it
            // down and keeps judging.
            Assert.True(TransmitSafety.ReflectedWarnFloorWatts(SeverelyFoldedBack)
                        < SeverelyFoldedBack);
            Assert.True(TransmitSafety.ReflectedWarnFloorWatts(OpenForward) < OpenForward);

            var reading = Pair(OpenForward, OpenReflected);
            var run = RunOf(reading);
            Assert.True(run.ForwardPeakWatts >= OpenForward);
            Assert.True(TransmitSafety.ShouldWarnReflected(
                reading, run, Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void A_syllable_trough_is_below_the_floor_and_is_not_judged()
        {
            // Defence in depth behind the pairing: even if a trough reading were
            // somehow coherent, it is not near the envelope peak and the
            // measurement there means nothing.
            var run = new ReflectedPowerRun();
            run.Observe(Pair(100f, 1f));          // the peak this over reached
            Assert.Equal(10f, run.FloorWatts, 3);

            Assert.False(run.Observe(Pair(2f, 1f)),
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

            run.Observe(bad);
            run.Observe(bad);
            Assert.Equal(2, run.BadSamples);

            run.Observe(belowFloor);
            Assert.Equal(2, run.BadSamples);

            run.Observe(Pair(2f, 1f, skewMs: 80f));
            Assert.Equal(2, run.BadSamples);

            run.Observe(goodMatch);
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
