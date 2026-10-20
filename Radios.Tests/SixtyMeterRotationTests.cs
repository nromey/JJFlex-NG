using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The 60m channel rotation (Alt+Shift+Up / Alt+Shift+Down) and the mode
    /// correction every 60m arrival path shares (#312).
    /// </summary>
    /// <remarks>
    /// <para><b>What this sees and what it cannot.</b> It asserts the stop the
    /// rotation lands on, the correction it owes, and the arrival sentence
    /// assembled through the real lexicon, word for word. The mode clause is
    /// NOT here since repair round one: whether "mode set to CW" is true
    /// depends on the radio taking the write, and SixtyMeterModeDeliveryTests
    /// owns that, past the speech gate. It cannot see that
    /// <c>MainWindow.SixtyMeterChannelNavigate</c> acts on the stop. The bench
    /// half is Alt+Shift+Up through the stops into the digital segment from a
    /// USB channel, listening for the mode.</para>
    /// </remarks>
    public class SixtyMeterRotationTests
    {
        // US allocation: indices 0-4 are Channels 1-5 (USB), index 5 is the
        // digital segment starting at 5.3515 MHz.
        private const int Channel5 = 4;
        private const int DigitalSegment = 5;

        [Fact]
        public void Rotating_up_from_channel_5_on_USB_lands_in_the_digital_segment_on_CW_and_says_so()
        {
            var stop = SixtyMeterChannels.Rotate("US", Channel5, +1, "USB");

            Assert.NotNull(stop);
            Assert.Equal(DigitalSegment, stop!.Index);
            Assert.Equal(5_351_500UL, stop.FrequencyHz);
            Assert.Null(stop.ChannelMode);
            Assert.Equal("CW", stop.Correction?.RequiredMode);
            Assert.Equal("digital segment", stop.Correction!.SegmentLabel);
            Assert.Equal($"60 meter digital and CW segment, {5.3515:F4} megahertz", stop.Arrival);
        }

        [Fact]
        public void Rotating_down_from_channel_1_on_USB_wraps_into_the_digital_segment_on_CW()
        {
            var stop = SixtyMeterChannels.Rotate("US", 0, -1, "USB");

            Assert.NotNull(stop);
            Assert.Equal(DigitalSegment, stop!.Index);
            Assert.Equal("CW", stop.Correction?.RequiredMode);
        }

        [Theory]
        [InlineData("CW")]
        [InlineData("DIGU")]
        [InlineData("DIGL")]
        [InlineData("digu")]
        public void Arriving_in_the_digital_segment_on_a_permitted_mode_leaves_the_mode_alone(string mode)
        {
            var stop = SixtyMeterChannels.Rotate("US", Channel5, +1, mode);

            Assert.NotNull(stop);
            Assert.Null(stop!.ChannelMode);
            Assert.Null(stop.Correction);
            Assert.Equal($"60 meter digital and CW segment, {5.3515:F4} megahertz", stop.Arrival);
        }

        [Theory]
        [InlineData(0, +1, 1, "Channel 2", 5.3480)]
        [InlineData(1, +1, 2, "Channel 3", 5.3585)]
        [InlineData(3, -1, 2, "Channel 3", 5.3585)]
        [InlineData(DigitalSegment, +1, 0, "Channel 1", 5.3320)]
        [InlineData(DigitalSegment, -1, Channel5, "Channel 5", 5.4050)]
        public void A_channel_stop_is_unchanged_it_always_sets_the_channel_mode(
            int from, int direction, int expectedIndex, string label, double mhz)
        {
            // Whatever mode the operator was on, a channel sets its own mode -
            // including leaving the digital segment on CW. This was the
            // behaviour before #312 and #312 does not touch it.
            foreach (var mode in new[] { "USB", "CW", "DIGU", "LSB" })
            {
                var stop = SixtyMeterChannels.Rotate("US", from, direction, mode);

                Assert.NotNull(stop);
                Assert.Equal(expectedIndex, stop!.Index);
                Assert.Equal((ulong)(mhz * 1_000_000.0 + 0.5), stop.FrequencyHz);
                Assert.Equal("USB", stop.ChannelMode);
                Assert.Null(stop.Correction);
                Assert.Equal($"{label}, {mhz:F4} megahertz, USB", stop.Arrival);
            }
        }

        [Fact]
        public void A_country_with_no_allocation_has_no_rotation()
        {
            Assert.Null(SixtyMeterChannels.Rotate("ZZ", 0, +1, "USB"));
        }

        [Fact]
        public void The_mode_correction_for_a_typed_frequency_in_the_segment_is_CW()
        {
            // 5.360 MHz: inside the digital segment, on no channel.
            var c = SixtyMeterChannels.GetModeCorrection("US", 5_360_000, "USB");

            Assert.NotNull(c);
            Assert.Equal("CW", c!.RequiredMode);
            Assert.Equal("digital segment", c.SegmentLabel);
        }

        [Fact]
        public void The_mode_correction_is_null_off_60m_and_on_a_permitted_mode()
        {
            Assert.Null(SixtyMeterChannels.GetModeCorrection("US", 14_200_000, "USB"));
            Assert.Null(SixtyMeterChannels.GetModeCorrection("US", 5_360_000, "DIGU"));
            Assert.Null(SixtyMeterChannels.GetModeCorrection("US", 5_332_000, "USB"));
            Assert.Null(SixtyMeterChannels.GetModeCorrection("ZZ", 5_360_000, "USB"));
        }

        [Fact]
        public void The_mode_correction_on_a_channel_frequency_is_the_channel_mode()
        {
            // The band jump's existing behaviour, which every path now shares.
            var c = SixtyMeterChannels.GetModeCorrection("US", 5_332_000, "CW");

            Assert.NotNull(c);
            Assert.Equal("USB", c!.RequiredMode);
            Assert.Equal("Channel 1", c.SegmentLabel);
        }
    }
}
