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
        private static string SwrTextBody()
        {
            string path = Path.Combine(RepoRoot(), "Radios", "FlexBase.cs");
            Assert.True(File.Exists(path),
                "The sweep cannot find FlexBase.cs — fix the path, do not delete the test.");

            string text = File.ReadAllText(path);
            int at = text.IndexOf("private string SWRText()", StringComparison.Ordinal);

            // POSITIVE CONTROL.
            Assert.True(at >= 0,
                "SWRText was not found in FlexBase.cs. If the SWR display moved, move this "
                + "test with it — do not let a missing method read as a passing check.");

            int open = text.IndexOf('{', at);
            int depth = 1;
            int i = open + 1;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}') depth--;
                i++;
            }
            return text.Substring(open, i - open);
        }

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
