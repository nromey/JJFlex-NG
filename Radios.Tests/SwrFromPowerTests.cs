using System;
using System.Globalization;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// SWR derived from forward and reflected power, checked against readings
    /// taken at the bench on 2026-08-22.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are not invented numbers. Every pair below came off the 8600's
    /// meter stream during a dummy-load session, and the session produced the
    /// one thing a test like this needs: a KNOWN-GOOD case and a KNOWN-BAD one,
    /// measured minutes apart on the same radio.
    /// </para>
    /// <para>
    /// The known-bad case is the reason the whole calculation exists. The
    /// dummy load was on ANT2; ANT1 was selected and empty. Transmitting into
    /// that open connector, forward power was 17.5 W and reflected was 13.4 W —
    /// 76% of it coming straight back — and the radio's own SWR meter reported
    /// <b>1.008</b>. Two full sessions of measurements were taken through that
    /// reassuring number before anyone noticed the load was never getting warm.
    /// </para>
    /// <para>
    /// The known-good case is what the same radio reported minutes later with
    /// the load actually in circuit: forward 101.2 W, reflected 0.054 W,
    /// meter 1.047. The calculation agrees to three decimals there — so it is
    /// not merely different from the meter, it is right where the meter is
    /// right and right where the meter is wrong.
    /// </para>
    /// </remarks>
    public class SwrFromPowerTests
    {
        // Measured pairs, in dBm, exactly as the meter stream carried them.
        private const float GoodForward = 50.05f;   // 101.2 W into the dummy load
        private const float GoodReflected = 17.33f; //   0.054 W back
        private const float GoodMeterSaid = 1.047f;

        private const float OpenForward = 42.43f;   //  17.5 W into an empty port
        private const float OpenReflected = 41.27f; //  13.4 W back
        private const float OpenMeterSaid = 1.008f; // ← the lie

        [Fact]
        public void AGoodLoadAgreesWithTheRadioToThreeDecimals()
        {
            // The positive control. If the arithmetic disagreed here, it would
            // be the calculation at fault rather than the meter, and nothing
            // below could be trusted.
            float computed = FlexBase.SwrFromPower(GoodForward, GoodReflected);

            Assert.InRange(computed, GoodMeterSaid - 0.01f, GoodMeterSaid + 0.01f);
        }

        [Fact]
        public void AnOpenPortIsCaughtWhereTheMeterReported1008()
        {
            // The case this exists for. A true SWR near 15, reported by the
            // radio as essentially perfect.
            float computed = FlexBase.SwrFromPower(OpenForward, OpenReflected);

            Assert.True(computed > 10f,
                "an open antenna port must compute a high SWR; got " + computed);
            Assert.True(computed > OpenMeterSaid * 5f,
                "the computed value must be nowhere near the 1.008 the meter reported");
        }

        [Fact]
        public void TheTwoMeasuredCasesAreSeparatedByAnEnormousMargin()
        {
            // No threshold-tuning needed: good and bad are three orders of
            // magnitude apart in reflected fraction and an order apart in SWR.
            float good = FlexBase.SwrFromPower(GoodForward, GoodReflected);
            float open = FlexBase.SwrFromPower(OpenForward, OpenReflected);

            Assert.True(open > good * 10f,
                "good " + good + " vs open " + open + " — expected a wide separation");
        }

        [Fact]
        public void TheCurveMatchesTextbookValues()
        {
            // Reflected fraction for a given SWR: |Γ|² where Γ = (SWR-1)/(SWR+1).
            foreach (float swr in new[] { 1.5f, 2.0f, 3.0f, 5.0f })
            {
                double gamma = (swr - 1.0) / (swr + 1.0);
                double fraction = gamma * gamma;
                float forwardDBm = 50f;                     // 100 W
                float reflectedDBm = (float)(10.0 * Math.Log10(100.0 * fraction * 1000.0));

                float computed = FlexBase.SwrFromPower(forwardDBm, reflectedDBm);

                Assert.InRange(computed, swr - 0.02f, swr + 0.02f);
            }
        }

        [Fact]
        public void NoForwardPowerReturnsUnknownRatherThanAPlausibleOne()
        {
            // The whole point. Returning 1.0 when there is nothing to measure
            // would recreate the defect being fixed — a comfortable number
            // nobody asked the provenance of.
            Assert.True(float.IsNaN(FlexBase.SwrFromPower(-150f, -150f)));
            Assert.True(float.IsNaN(FlexBase.SwrFromPower(10f, 5f)));   // 0.01 W, below the floor
        }

        [Fact]
        public void ReflectedAboveForwardIsUnknownNotInfinite()
        {
            // Not physical. It means the two meters were sampled at different
            // instants, or one is wrong. Either way the honest answer is "no
            // reading", not a number.
            Assert.True(float.IsNaN(FlexBase.SwrFromPower(40f, 45f)));
            Assert.True(float.IsNaN(FlexBase.SwrFromPower(40f, 40f)));
        }

        [Fact]
        public void APerfectMatchIsOne()
        {
            // Reflected 60 dB down is a better match than any real antenna.
            float computed = FlexBase.SwrFromPower(50f, -10f);
            Assert.InRange(computed, 1.0f, 1.01f);
        }

        [Fact]
        public void TheSentinelIsNamedSoNobodyReadsItAsALowSwr()
        {
            // The radio reports -25 when it has no reading, including during a
            // transmit that is plainly happening. Any rule consuming SWR has to
            // tell "no reading" from "a good reading", and -25 sorts below 1.0
            // on every numeric comparison anyone would naively write.
            Assert.Equal(-25f, FlexBase.SWRNoReading);
            Assert.True(FlexBase.SWRNoReading < 1.0f,
                "which is exactly why a bare 'swr < 1.5 means fine' test is unsafe");
        }

        [Fact]
        public void The_calculation_can_never_produce_a_number_below_one()
        {
            // The property that makes "below 1 is not a measurement" a safe
            // rule for the display to apply. Reflection coefficient is a
            // square root of a non-negative ratio, so the result is 1 or more
            // or it is NaN — there is no third answer, and anything below 1
            // reaching an operator therefore came from the raw meter.
            foreach (float fwd in new[] { 20f, 35f, 42.43f, 50.05f, 60f })
                foreach (float refl in new[] { -40f, 0f, 17.33f, 30f, 41f, 45f, 61f })
                {
                    float swr = FlexBase.SwrFromPower(fwd, refl);
                    Assert.True(float.IsNaN(swr) || swr >= 1.0f,
                        $"SwrFromPower({fwd}, {refl}) produced {swr}");
                }
        }
    }

    /// <summary>
    /// The SWR the operator is actually shown, on the manual-tuner button
    /// (#454).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Source-read, because the thing under test is private and needs a
    /// live FlexBase.</b> That is the whole reason the defect survived:
    /// <c>SWRText</c> was <c>_SWR.ToString("f1")</c> — the raw radio meter
    /// straight to a string with no test for the sentinel — so the radio's
    /// −25 "I have no reading" was displayed as <b>"-25.0"</b>, and the same
    /// meter's reassuring 1.008 was displayed while 76 percent of the power was
    /// coming back off an empty port. Nothing could reach it to prove
    /// otherwise, so nothing did.
    /// </para>
    /// <para>
    /// The sweep proves it found the method before it proves anything about
    /// the method — a path or a rename that empties it must fail loudly rather
    /// than report a clean bill of health.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class SwrDisplayTests
    {
        /// <summary>
        /// The full body of a member of <c>FlexBase</c>, matched by braces.
        /// </summary>
        /// <remarks>
        /// <b>Brace-matched, never a fixed character window.</b> Two of these
        /// sweeps read a 2,600-character slice instead, and on 2026-09-07 a
        /// comment added ahead of a <c>return</c> pushed that return outside
        /// the window — so the scan was reading half a method. It failed
        /// loudly only because a positive control happened to sit on the part
        /// that fell off the end. A window has a cliff and the cliff moves
        /// every time somebody writes a sentence.
        /// </remarks>
        private static string FlexBaseMemberBody(string signature)
        {
            string path = Path.Combine(RepoRoot(), "Radios", "FlexBase.cs");
            Assert.True(File.Exists(path),
                "The sweep cannot find FlexBase.cs — fix the path, do not delete the test.");

            string text = File.ReadAllText(path);
            int at = text.IndexOf(signature, StringComparison.Ordinal);

            // POSITIVE CONTROL.
            Assert.True(at >= 0,
                $"'{signature}' was not found in FlexBase.cs. If it moved, move this test "
                + "with it — do not let a missing member read as a passing check.");

            int open = text.IndexOf('{', at);
            int depth = 1;
            int i = open + 1;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}') depth--;
                i++;
            }
            Assert.True(depth == 0,
                $"braces under '{signature}' never closed — the file is truncated or the "
                + "signature matched inside a comment");
            return text.Substring(open, i - open);
        }

        private static string SwrTextBody() =>
            FlexBaseMemberBody("private string SWRText()");

        [Fact]
        public void The_display_does_not_read_the_raw_radio_meter()
        {
            string body = SwrTextBody();

            Assert.DoesNotContain("_SWR", body);
            Assert.Contains("ComputedSWR", body);
        }

        [Fact]
        public void The_display_refuses_to_render_anything_below_one_as_a_number()
        {
            // A standing wave ratio cannot be negative, and cannot be under 1.
            // Anything below 1 is by construction not a measurement, so the
            // guard is written against 1 rather than against the −25 sentinel
            // specifically: a future sentinel, or a meter glitch, lands in the
            // same net.
            string body = SwrTextBody();

            Assert.Contains("IsNaN", body);
            Assert.Contains("< 1f", body);
            Assert.Contains("audio.tune.swr_no_reading", body);
        }

        [Fact]
        public void The_no_reading_wording_is_words_and_not_a_number()
        {
            string words = Lexicon.Get("audio.tune.swr_no_reading");

            Assert.False(string.IsNullOrWhiteSpace(words));
            Assert.DoesNotContain("{", words);
            Assert.False(float.TryParse(words, NumberStyles.Float, CultureInfo.InvariantCulture,
                                        out _),
                "a no-reading label that parses as a number is the defect again");
            Assert.DoesNotContain("-25", words);
        }

        // ════════════════════════════════════════════════════════════
        //  #453: the two meters must come from the SAME MOMENT
        // ════════════════════════════════════════════════════════════
        //
        // Measured 2026-09-07, 100 W into the 400 W dummy load. The radio's own
        // meter held 1.01-1.22 throughout; ours ranged 1.04 to 4.84. These two
        // consecutive samples are the whole mechanism:
        //
        //   fwd 44.67 W, refl 0.031 W  ->  1.05   correct
        //   fwd  0.48 W, refl 0.083 W  ->  2.43   wrong
        //
        // Reflected ROSE while forward fell ninetyfold. It is not a reading of
        // that instant; it is the peak's reading, still sitting there.

        // The same trough sample in dBm, as the meter stream carried it.
        private const float TroughForwardDbm = 26.81f;    // 0.48 W
        private const float TroughReflectedDbm = 19.20f;  // 0.083 W

        [Fact]
        public void ThePairFromOneMomentIsCoherent()
        {
            // Positive control: a well-formed pair must PASS, or the gate below
            // proves nothing except that everything fails.
            var fresh = new TransmitPowerReading(44.67f, 0.031f, skewMilliseconds: 4f,
                                                 ageMilliseconds: 120f);

            Assert.True(fresh.IsCoherent);
            Assert.Equal("", fresh.WhyNotCoherent);
        }

        [Fact]
        public void APairSampledAMeterPeriodApartIsNotCoherent()
        {
            // The meter stream ran at roughly one update per second on
            // 2026-09-07, so a stale partner is ~1000 ms old against a 60 ms
            // budget. This is the trough sample with the peak's reflected.
            var stale = new TransmitPowerReading(0.48f, 0.083f,
                                                 skewMilliseconds: 1030f,
                                                 ageMilliseconds: 200f);

            Assert.False(stale.IsCoherent);
            Assert.Contains("apart", stale.WhyNotCoherent);

            // And the arithmetic on that pair is exactly the number the
            // operator should never have been shown.
            float wrong = FlexBase.SwrFromPower(TroughForwardDbm, TroughReflectedDbm);
            Assert.True(wrong > 2.0f,
                "the measured trough pair produces a false high; if this stops being "
                + "true the sample is wrong, not the gate");
        }

        [Fact]
        public void TheOperatorFacingSwrJudgesThePairTogether()
        {
            // ComputedSWR needs a live radio, so the RULE is what is pinned.
            //
            // FlexBase says it one screen above the fields: "anything JUDGING
            // the two together takes them from here instead". The kill switch
            // and the PTT safety controller obey it. Until 2026-09-07 the
            // property an OPERATOR sees did not — it read the two fields raw,
            // which is how a stale partner reached the display.
            string body = FlexBaseMemberBody("public float ComputedSWR");
            Assert.Contains("ReadTransmitPower()", body);
            Assert.Contains("IsCoherent", body);

            // Positive control on the scan itself: a phrase that IS in the body.
            Assert.Contains("SwrFromPower", body);
        }

        [Fact]
        public void TheSpokenAfterTuneSwrIsNotTheRadiosRawMeter()
        {
            // #570. Until 2026-09-07 this spoke RigControl.SWRValue — the meter
            // that read 1.008 with 76 percent coming back off an open port — at
            // the exact moment an operator asks whether their antenna is all
            // right.
            //
            // It now speaks the coherent COMPUTED value latched during the
            // carrier. The latch matters: by the time the announcement runs,
            // forward power has collapsed and a live read would be NaN every
            // time, so a naive swap would have silenced the announcement
            // instead of correcting it.
            string src = File.ReadAllText(
                Path.Combine(RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs"));

            int at = src.IndexOf("SpeakSwrAfterTune(", StringComparison.Ordinal);
            Assert.True(at > 0, "SpeakSwrAfterTune was renamed; rewrite this guard, do not delete it");

            // Every call site, not just the first: there are two paths in and
            // they have been corrected one at a time before.
            foreach (int idx in AllIndexesOf(src, "SpeakSwrAfterTune("))
            {
                string call = src.Substring(idx, Math.Min(120, src.Length - idx));
                Assert.DoesNotContain("SWRValue", call);
            }

            // Positive control: the phrase we DO expect is present.
            Assert.Contains("TuneCycleSettledComputedSwr", src);
        }

        // ════════════════════════════════════════════════════════════
        //  #453: the POWER FLOOR, which is what was actually wrong
        // ════════════════════════════════════════════════════════════
        //
        // The coherence gate above was the first hypothesis and it is not the
        // mechanism. Across the 194 samples of the 2026-09-07 bench run —
        // 100 W commanded into the 400 W dummy load, Jim Dale reading aloud,
        // speech processor and compander ON — the gate rejected exactly ONE.
        // Every one of the seven false highs came from a sample whose FORWARD
        // power had collapsed into the trough between words.
        //
        // The two numbers that bound the floor, both from that run:
        //
        //   1.40 W forward — the worst false high, our arithmetic said 4.84
        //                    while the radio's own meter held 1.01
        //   8.83 W forward — the LOWEST sample that produced a good reading
        //
        // Anything between those two separates the run perfectly. Five percent
        // of commanded lands at 5 W, near the middle on a log scale, which is
        // the right place to sit when the true boundary is unknown.

        private const float WorstFalseHighForwardWatts = 1.40f;
        private const float LowestGoodForwardWatts = 8.83f;
        private const int RunCommandedWatts = 100;

        [Fact]
        public void TheFloorSeparatesTheMeasuredRun()
        {
            float floor = FlexBase.MinBelievableForwardWatts(RunCommandedWatts);

            Assert.True(floor > WorstFalseHighForwardWatts,
                $"the floor ({floor} W) must exclude the worst false high at "
                + $"{WorstFalseHighForwardWatts} W, or #453 is not fixed");

            Assert.True(floor < LowestGoodForwardWatts,
                $"the floor ({floor} W) must keep the lowest GOOD sample at "
                + $"{LowestGoodForwardWatts} W — a floor that silences correct "
                + "readings trades one wrong answer for no answer");
        }

        [Fact]
        public void TheFloorKeepsRoomOnBothSides()
        {
            // Sitting just above the worst false high would fit this one run
            // and nothing else. #238 asks where the floor really is across
            // models and powers; until it answers, margin is the honest
            // substitute for evidence we do not have.
            float floor = FlexBase.MinBelievableForwardWatts(RunCommandedWatts);

            Assert.True(floor >= WorstFalseHighForwardWatts * 2f,
                "less than a factor of two above the worst false high is fitted "
                + "to one day's data, not chosen");
            Assert.True(floor <= LowestGoodForwardWatts / 1.5f,
                "the floor is creeping up on real readings");
        }

        [Fact]
        public void TheFloorScalesWithCommandedPowerRatherThanBeingAFixedWattage()
        {
            // The reason this is a FRACTION. A fixed 5 W floor tuned to the
            // 100 W run would silence SWR completely at tune power — and a
            // tune is precisely when an operator asks the question. Don tunes
            // at 10 W.
            float atHundred = FlexBase.MinBelievableForwardWatts(100);
            float atTen = FlexBase.MinBelievableForwardWatts(10);

            Assert.True(atTen < atHundred,
                "a floor that does not scale is a floor tuned to one power level");
            Assert.True(atTen < 5f,
                "at 10 W commanded the floor must not swallow the whole carrier");

            // Commanded power is a SETTING, so unlike forward power it does not
            // chase the operator's voice. That is the property being relied on.
            Assert.Equal(atHundred, FlexBase.MinBelievableForwardWatts(100));
        }

        [Fact]
        public void AVeryLowCommandedPowerStillHasAnAbsoluteFloor()
        {
            // Five percent of 1 W is 0.05 W, which is coupler noise. The
            // absolute bound is what stops the percentage collapsing into it.
            float atOne = FlexBase.MinBelievableForwardWatts(1);

            Assert.Equal(FlexBase.MinForwardWattsAbsolute, atOne);
            Assert.True(atOne > 1f * FlexBase.MinForwardFractionOfCommanded,
                "the absolute bound is not being applied");
        }

        [Fact]
        public void TheOperatorFacingSwrAppliesTheFloor()
        {
            // ComputedSWR needs a live radio, so as with the coherence gate the
            // RULE is what gets pinned: the property an operator sees must go
            // through the floor, not merely have one available nearby.
            string body = FlexBaseMemberBody("public float ComputedSWR");
            Assert.Contains("MinBelievableForwardWatts", body);
            Assert.Contains("ForwardWatts", body);

            // Positive control on the scan.
            Assert.Contains("SwrFromPower", body);
        }

        private static System.Collections.Generic.IEnumerable<int> AllIndexesOf(string haystack, string needle)
        {
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
                yield return i;
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
