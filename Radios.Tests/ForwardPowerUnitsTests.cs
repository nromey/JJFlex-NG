using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 33 Track D. The forward power meter reports dBm; the analyzer
    /// publishes watts. These lock the conversion between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is worth a test file of its own.</b> The FLEX-8600 describes
    /// FWDPWR as units dBm over a range of 0.0 to 53.0 — measured from the
    /// radio's own descriptor on 2026-08-20. Fifty-three dBm is two hundred
    /// watts. Zero dBm is one milliwatt. So a build that forgot the conversion
    /// and published the raw meter reading would announce "53 watts" while the
    /// radio made two hundred, and "0 watts" while it made a milliwatt — wrong
    /// by orders of magnitude, in units that look entirely reasonable, on the
    /// one number an operator uses to decide whether their transmitter is
    /// working.
    /// </para>
    /// <para>
    /// Nothing downstream could catch it. A rule comparing watts to a threshold
    /// has no way to know the number is in the wrong unit, and every digit of
    /// it looks plausible. That is the same defect class as reading the wrong
    /// meter, one layer down: right instrument, wrong scale.
    /// </para>
    /// <para>
    /// The conversion IS present and correct as of this sprint. These tests
    /// exist so that stays true.
    /// </para>
    /// </remarks>
    public sealed class ForwardPowerUnitsTests
    {
        /// <summary>The top of the FWDPWR meter's declared range on the bench
        /// 8600.</summary>
        private const float MeterCeilingDbm = 53.0f;

        /// <summary>The bottom of that range. Note it is NOT a floor of
        /// silence — one milliwatt is a real reading.</summary>
        private const float MeterFloorDbm = 0.0f;

        [Fact]
        public void Thirty_dBm_is_one_watt()
        {
            Assert.Equal(1.0, FlexBase.DBmToWatts(30f), 3);
        }

        [Fact]
        public void The_meters_declared_ceiling_is_about_two_hundred_watts()
        {
            float watts = FlexBase.DBmToWatts(MeterCeilingDbm);
            Assert.InRange(watts, 195.0, 205.0);
        }

        /// <summary>
        /// The bottom of the meter's range is a milliwatt of real RF, not
        /// nothing. This is why the range floor cannot be used as a
        /// has-it-reported test.
        /// </summary>
        [Fact]
        public void The_meters_declared_floor_is_one_milliwatt_of_real_power()
        {
            Assert.Equal(0.001, FlexBase.DBmToWatts(MeterFloorDbm), 6);
        }

        /// <summary>
        /// Sub-watt is the normal operating point for transverter and QRP work,
        /// so it has to survive the conversion AND the formatting. A hundred
        /// milliwatts that reads as "0 watts" is indistinguishable from a dead
        /// transmitter.
        /// </summary>
        [Fact]
        public void A_tenth_of_a_watt_survives_conversion_and_formatting()
        {
            float watts = FlexBase.DBmToWatts(20f);
            Assert.Equal(0.1, watts, 4);
            Assert.DoesNotContain("0 watts", FlexBase.FormatForwardPowerSpoken(watts));
            Assert.Contains("0.1", FlexBase.FormatForwardPowerSpoken(watts));
        }

        /// <summary>
        /// The idle sentinel, and the whole reason forward-power needed a
        /// readability gate rather than a value test.
        ///
        /// <para>FlexBase initialises its dBm field to -150, which is BELOW the
        /// meter's own declared floor of 0.0 — a value the radio can never
        /// report. Converted, it is about a millionth of a millionth of a watt
        /// and formats as "0 watts", which is exactly what a dead transmitter
        /// looks like. The gate now lives in TxChainFacts and asks whether the
        /// meter has reported at all; this test records why a value test would
        /// not have done.</para>
        /// </summary>
        [Fact]
        public void The_idle_sentinel_is_below_the_meters_own_floor_and_reads_as_zero_watts()
        {
            const float idleSentinel = -150f;

            Assert.True(idleSentinel < MeterFloorDbm,
                "the sentinel must be unreachable by the meter, or it could be mistaken for a reading");

            float watts = FlexBase.DBmToWatts(idleSentinel);
            Assert.True(watts < 0.0005f);
            Assert.Equal("0 watts", FlexBase.FormatForwardPowerSpoken(watts));
        }
    }
}
