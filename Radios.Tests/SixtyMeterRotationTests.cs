using System.Linq;
using HamBands;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The US 60 m table and its three decisions (where a stop tunes, which
    /// modes are corrected, what counts as a valid dial), written from the
    /// rule in force since 2026-02-13 (FCC 25-60), not from the code: 47 CFR
    /// 97.303(h)(3), 97.305(c), 97.307(f)(14) and 97.313(i).
    /// </summary>
    /// <remarks>
    /// <para><b>Rewritten for #1172.</b> Until then this class pinned the
    /// pre-2026 table: five USB channels at their CENTRES (a "Channel 3" at
    /// 5.3585 MHz that the rule deleted), a CW-and-data-only segment stop on
    /// its 5.3515 MHz edge, and a correction of CW on a channel to USB. Every
    /// one of those is wrong under the rule, and each assertion below states
    /// the rule's number rather than the table's.</para>
    /// <para><b>The rule's figures, retyped here on purpose</b> so a wrong
    /// table cannot agree with itself: channel centres 5332, 5348, 5373 and
    /// 5405 kHz; a CW carrier on the centre; a phone, data or RTTY carrier
    /// 1.5 kHz below it (5330.5, 5346.5, 5371.5, 5403.5); the segment 5351.5
    /// to 5366.5 kHz.</para>
    /// <para><b>What this cannot see.</b> The radio: that a Flex slice's
    /// frequency is the suppressed carrier in USB is FlexLib's (the USB low cut
    /// is clamped at 0), and that it is the carrier in CW is not established
    /// (a bench read-back settles it). That <c>MainWindow.SixtyMeterChannelNavigate</c>
    /// acts on the stop is read on the bench: Alt+Shift+Up through every stop
    /// in USB and in CW, reading frequency and mode back.</para>
    /// </remarks>
    public class SixtyMeterRotationTests
    {
        // US: indices 0 to 3 are Channels 1 to 4, index 4 is the segment.
        private const int Channel4 = 3;
        private const int SegmentStop = 4;

        private static readonly ulong[] Centres = { 5_332_000, 5_348_000, 5_373_000, 5_405_000 };
        private static readonly ulong[] PhoneDials = { 5_330_500, 5_346_500, 5_371_500, 5_403_500 };
        private const ulong SegmentLow = 5_351_500, SegmentHigh = 5_366_500;

        /// <summary>The nominal emissions the brief names, retyped from it.</summary>
        private static (long Low, long High) Emission(string mode) => mode switch
        {
            "USB" or "DIGU" => (0, 2_800),
            "LSB" or "DIGL" => (-2_800, 0),
            "CW" or "RTTY" => (-250, 250),
            _ => throw new System.ArgumentException(mode),
        };

        private static string Mhz(ulong hz) => $"{hz / 1_000_000.0:F4}";

        // ---------------- the table ----------------

        [Fact]
        public void There_are_four_channels_and_the_deleted_5358_5_is_not_one_of_them()
        {
            var alloc = SixtyMeterChannels.GetAllocation("US")!.Value;

            Assert.Equal(Centres, alloc.Channels.Select(c => c.CentreHz).ToArray());
            Assert.Equal(new[] { "Channel 1", "Channel 2", "Channel 3", "Channel 4" },
                alloc.Channels.Select(c => c.Label).ToArray());
            Assert.Equal(5, SixtyMeterChannels.GetNavigationStopCount("US"));
            Assert.DoesNotContain(alloc.Channels, c => c.CentreHz == 5_358_500);
        }

        [Fact]
        public void Power_is_100_watts_ERP_on_the_channels_and_9_15_in_the_segment()
        {
            var alloc = SixtyMeterChannels.GetAllocation("US")!.Value;

            Assert.All(alloc.Channels, c => Assert.Equal(100.0, c.MaxErpW));
            Assert.Equal(9.15, alloc.Segment!.MaxErpW);
            Assert.Equal(SegmentLow, alloc.Segment.StartHz);
            Assert.Equal(SegmentHigh, alloc.Segment.EndHz);
        }

        // ---------------- where a channel stop tunes, per arriving mode ----------------

        [Theory]
        // Kept: the dial follows the mode, and the mode is left alone.
        [InlineData("CW", "CW", false)]
        [InlineData("USB", "USB", false)]
        [InlineData("DIGU", "DIGU", false)]
        [InlineData("cw", "CW", false)]
        // Corrected: the dial follows the mode the slice ENDS in.
        [InlineData("LSB", "USB", true)]
        [InlineData("DIGL", "DIGU", true)]
        [InlineData("RTTY", "DIGU", true)]
        [InlineData("AM", "USB", true)]
        [InlineData("SAM", "USB", true)]
        [InlineData("DSB", "USB", true)]
        [InlineData("FM", "USB", true)]
        [InlineData("NFM", "USB", true)]
        [InlineData("DFM", "USB", true)]
        [InlineData("FDV", "USB", true)]
        [InlineData("SOMETHING", "USB", true)]
        public void A_channel_stop_tunes_the_dial_for_the_mode_the_slice_ends_in(
            string arriving, string final, bool sets)
        {
            for (int i = 0; i < 4; i++)
            {
                // Arrive at channel i from the stop before it.
                int from = i == 0 ? SegmentStop : i - 1;
                var stop = SixtyMeterChannels.Rotate("US", from, +1, arriving);

                Assert.NotNull(stop);
                Assert.Equal(i, stop!.Index);
                ulong dial = final == "CW" ? Centres[i] : PhoneDials[i];
                Assert.Equal(dial, stop.FrequencyHz);
                Assert.Equal(sets ? final : null, stop.ChannelMode);
                Assert.Null(stop.Correction);
                // The arrival names the dial the slice is on, and the mode it ends in.
                Assert.Equal($"Channel {i + 1}, {Mhz(dial)} megahertz, {ModeWords.Spoken(final)}", stop.Arrival);
            }
        }

        [Fact]
        public void Channel_1_in_USB_says_5_3305_and_in_CW_says_5_3320()
        {
            Assert.Equal($"Channel 1, {5.3305:F4} megahertz, USB",
                SixtyMeterChannels.Rotate("US", SegmentStop, +1, "USB")!.Arrival);
            Assert.Equal($"Channel 1, {5.3320:F4} megahertz, CW",
                SixtyMeterChannels.Rotate("US", SegmentStop, +1, "CW")!.Arrival);
            Assert.Equal($"Channel 1, {5.3305:F4} megahertz, Digital USB",
                SixtyMeterChannels.Rotate("US", SegmentStop, +1, "DIGL")!.Arrival);
        }

        [Fact]
        public void No_channel_stop_puts_a_USB_signal_above_its_channel()
        {
            // The defect this rewrite fixes: a USB dial at the centre puts the
            // upper sideband 1.5 kHz high, half of it outside the 2.8 kHz channel.
            for (int i = 0; i < 4; i++)
            {
                var stop = SixtyMeterChannels.Rotate("US", i == 0 ? SegmentStop : i - 1, +1, "USB")!;
                ulong top = stop.FrequencyHz + 2_800;
                Assert.True(top <= Centres[i] + 1_400, $"Channel {i + 1}'s USB signal ends at {top} Hz");
                Assert.NotEqual(Centres[i], stop.FrequencyHz);
            }
        }

        // ---------------- the rotation's order ----------------

        [Fact]
        public void Rotating_up_from_channel_4_lands_on_the_segment_stop()
        {
            var stop = SixtyMeterChannels.Rotate("US", Channel4, +1, "USB");

            Assert.NotNull(stop);
            Assert.Equal(SegmentStop, stop!.Index);
            Assert.Equal(5_357_000UL, stop.FrequencyHz);
            Assert.Null(stop.ChannelMode);
            Assert.Null(stop.Correction); // phone is lawful in the segment
            Assert.Equal($"60 meter segment, {5.3570:F4} megahertz", stop.Arrival);
        }

        [Fact]
        public void Rotating_down_from_channel_1_wraps_to_the_segment_and_up_from_it_to_channel_1()
        {
            Assert.Equal(SegmentStop, SixtyMeterChannels.Rotate("US", 0, -1, "USB")!.Index);
            Assert.Equal(0, SixtyMeterChannels.Rotate("US", SegmentStop, +1, "USB")!.Index);
            Assert.Equal(Channel4, SixtyMeterChannels.Rotate("US", SegmentStop, -1, "USB")!.Index);
        }

        [Fact]
        public void A_country_with_no_allocation_has_no_rotation()
        {
            Assert.Null(SixtyMeterChannels.Rotate("ZZ", 0, +1, "USB"));
        }

        // ---------------- the segment stop ----------------

        [Theory]
        [InlineData("USB")]
        [InlineData("LSB")]
        [InlineData("CW")]
        [InlineData("DIGU")]
        [InlineData("DIGL")]
        [InlineData("RTTY")]
        public void The_segment_stop_keeps_an_allowed_mode_and_its_whole_emission_inside_the_edges(string mode)
        {
            var stop = SixtyMeterChannels.Rotate("US", Channel4, +1, mode)!;

            Assert.Null(stop.Correction);
            Assert.Null(stop.ChannelMode);
            var (lo, hi) = Emission(mode);
            Assert.True((long)stop.FrequencyHz + lo >= (long)SegmentLow, mode + " starts below 5351.5");
            Assert.True((long)stop.FrequencyHz + hi <= (long)SegmentHigh, mode + " ends above 5366.5");
            Assert.True(SixtyMeterChannels.IsValidSixtyMeterFrequency("US", stop.FrequencyHz, mode));
        }

        [Fact]
        public void The_segment_stop_does_not_move_when_the_mode_changes()
        {
            var dials = new[] { "USB", "LSB", "CW", "DIGU", "DIGL", "RTTY", "AM", "FM" }
                .Select(m => SixtyMeterChannels.Rotate("US", Channel4, +1, m)!.FrequencyHz)
                .Distinct().ToArray();
            Assert.Equal(new[] { 5_357_000UL }, dials);
        }

        [Theory]
        [InlineData("AM")]
        [InlineData("SAM")]
        [InlineData("DSB")]
        [InlineData("FM")]
        [InlineData("NFM")]
        [InlineData("DFM")]
        [InlineData("FDV")]
        public void The_segment_stop_corrects_a_mode_it_does_not_keep_to_USB(string mode)
        {
            var stop = SixtyMeterChannels.Rotate("US", Channel4, +1, mode)!;

            Assert.Equal("USB", stop.Correction?.RequiredMode);
            Assert.Equal("60 meter segment", stop.Correction!.SegmentLabel);
            Assert.Null(stop.ChannelMode);
        }

        // ---------------- the correction matrix every way onto 60 m shares ----------------

        [Theory]
        // On a channel, at its centre and at its phone dial alike.
        [InlineData(5_332_000UL, "CW", null, "Channel 1")]
        [InlineData(5_330_500UL, "USB", null, "Channel 1")]
        [InlineData(5_346_500UL, "DIGU", null, "Channel 2")]
        [InlineData(5_373_000UL, "LSB", "USB", "Channel 3")]
        [InlineData(5_403_500UL, "DIGL", "DIGU", "Channel 4")]
        [InlineData(5_405_000UL, "RTTY", "DIGU", "Channel 4")]
        [InlineData(5_348_000UL, "AM", "USB", "Channel 2")]
        [InlineData(5_332_000UL, "FDV", "USB", "Channel 1")]
        // In the segment, anywhere in it.
        [InlineData(5_360_000UL, "USB", null, "60 meter segment")]
        [InlineData(5_360_000UL, "LSB", null, "60 meter segment")]
        [InlineData(5_360_000UL, "CW", null, "60 meter segment")]
        [InlineData(5_360_000UL, "DIGU", null, "60 meter segment")]
        [InlineData(5_360_000UL, "DIGL", null, "60 meter segment")]
        [InlineData(5_360_000UL, "RTTY", null, "60 meter segment")]
        [InlineData(5_358_500UL, "USB", null, "60 meter segment")] // the deleted channel is segment now
        [InlineData(5_360_000UL, "AM", "USB", "60 meter segment")]
        [InlineData(5_360_000UL, "NFM", "USB", "60 meter segment")]
        [InlineData(5_360_000UL, "FDV", "USB", "60 meter segment")]
        public void The_mode_correction_follows_the_rule(ulong hz, string mode, string? required, string label)
        {
            var advisory = SixtyMeterChannels.GetModeAdvisory("US", hz, mode);

            Assert.NotNull(advisory);
            Assert.Equal(required, advisory!.Value.RequiredMode);
            Assert.Equal(label, advisory.Value.SegmentLabel);
            var c = SixtyMeterChannels.GetModeCorrection("US", hz, mode);
            Assert.Equal(required, c?.RequiredMode);
        }

        [Fact]
        public void The_mode_correction_is_null_off_60m_between_channels_and_with_no_allocation()
        {
            Assert.Null(SixtyMeterChannels.GetModeCorrection("US", 14_200_000, "AM"));
            Assert.Null(SixtyMeterChannels.GetModeCorrection("US", 5_340_000, "AM"));
            Assert.Null(SixtyMeterChannels.GetModeCorrection("ZZ", 5_360_000, "AM"));
        }

        // ---------------- validity ----------------

        [Theory]
        [InlineData(5_330_500UL, "USB", true)]
        [InlineData(5_346_500UL, "DIGU", true)]
        [InlineData(5_373_000UL, "CW", true)]
        [InlineData(5_405_000UL, "cw", true)]
        [InlineData(5_332_000UL, "USB", false)]   // a USB dial on the centre: signal 1.5 kHz high
        [InlineData(5_348_000UL, "DIGU", false)]
        [InlineData(5_330_500UL, "CW", false)]    // a CW carrier is the centre, not the phone dial
        [InlineData(5_330_550UL, "USB", false)]   // validity is exact; 50 Hz high spills out of the channel
        [InlineData(5_330_500UL, "LSB", false)]
        [InlineData(5_371_500UL, "RTTY", false)]
        [InlineData(5_332_000UL, "AM", false)]
        public void A_channel_dial_is_valid_only_where_the_rule_puts_that_mode(ulong hz, string mode, bool valid)
            => Assert.Equal(valid, SixtyMeterChannels.IsValidSixtyMeterFrequency("US", hz, mode));

        [Fact]
        public void The_deleted_5358_5_is_not_a_channel_and_is_judged_as_segment()
        {
            // Not a channel: nothing names it one.
            Assert.Equal("60 meter segment", SixtyMeterChannels.GetModeAdvisory("US", 5_358_500, "USB")!.Value.SegmentLabel);
            // And as segment, by its emission: USB 5358.5 to 5361.3 is inside.
            Assert.True(SixtyMeterChannels.IsValidSixtyMeterFrequency("US", 5_358_500, "USB"));
            Assert.False(SixtyMeterChannels.IsValidSixtyMeterFrequency("US", 5_358_500, "AM"));
        }

        [Theory]
        [InlineData(5_363_700UL, "USB", true)]    // 5363.7 to 5366.5: fills the top exactly
        [InlineData(5_363_800UL, "USB", false)]   // 100 Hz over the top edge
        [InlineData(5_351_500UL, "DIGU", true)]   // on the bottom edge, all above it
        [InlineData(5_354_300UL, "LSB", true)]
        [InlineData(5_354_200UL, "LSB", false)]
        [InlineData(5_351_500UL, "DIGL", false)]  // the old segment stop: DIGL entirely below the band
        [InlineData(5_351_750UL, "CW", true)]
        [InlineData(5_351_500UL, "CW", false)]    // half the CW emission below the edge
        [InlineData(5_366_250UL, "RTTY", true)]
        [InlineData(5_366_500UL, "RTTY", false)]
        [InlineData(5_360_000UL, "AM", false)]
        [InlineData(5_360_000UL, "FM", false)]
        [InlineData(5_340_000UL, "USB", false)]   // between Channel 1 and Channel 2, on nothing
        [InlineData(5_310_000UL, "CW", false)]
        public void A_segment_dial_is_valid_when_its_whole_emission_is_inside_the_edges(ulong hz, string mode, bool valid)
            => Assert.Equal(valid, SixtyMeterChannels.IsValidSixtyMeterFrequency("US", hz, mode));

        [Fact]
        public void A_country_with_no_allocation_has_no_restrictions()
            => Assert.True(SixtyMeterChannels.IsValidSixtyMeterFrequency("ZZ", 5_340_000, "AM"));

        // ---------------- band memory and the band jump: the snap ----------------

        [Fact]
        public void An_old_60m_USB_memory_at_a_channel_centre_comes_back_on_the_phone_dial()
        {
            // Every operator who used the old channel stops saved USB at a
            // centre. Each now comes back 1.5 kHz lower, where the rule puts it.
            for (int i = 0; i < 4; i++)
            {
                var memory = new BandMemory();
                memory.SetFrequency(Bands.BandNames.m60, "USB", Centres[i]);
                Assert.Equal(PhoneDials[i], memory.GetFrequency(Bands.BandNames.m60, "USB"));
            }
        }

        [Fact]
        public void A_correct_60m_memory_is_not_moved()
        {
            var memory = new BandMemory();
            memory.SetFrequency(Bands.BandNames.m60, "USB", 5_330_500);
            memory.SetFrequency(Bands.BandNames.m60, "CW", 5_348_000);
            memory.SetFrequency(Bands.BandNames.m60, "DIGU", 5_357_000);

            Assert.Equal(5_330_500UL, memory.GetFrequency(Bands.BandNames.m60, "USB"));
            Assert.Equal(5_348_000UL, memory.GetFrequency(Bands.BandNames.m60, "CW"));
            Assert.Equal(5_357_000UL, memory.GetFrequency(Bands.BandNames.m60, "DIGU"));
            // And another band's memory is untouched by any of this.
            memory.SetFrequency(Bands.BandNames.m40, "USB", 7_150_000);
            Assert.Equal(7_150_000UL, memory.GetFrequency(Bands.BandNames.m40, "USB"));
        }

        [Fact]
        public void With_nothing_remembered_60m_starts_on_channel_1_s_dial_for_the_mode()
        {
            var memory = new BandMemory();
            Assert.Equal(5_330_500UL, memory.GetFrequency(Bands.BandNames.m60, "USB"));
            Assert.Equal(5_332_000UL, memory.GetFrequency(Bands.BandNames.m60, "CW"));
            Assert.Equal(5_330_500UL, memory.GetFrequency(Bands.BandNames.m60, "LSB"));
            Assert.Equal(5_330_500UL, SixtyMeterChannels.DefaultDialHz("US", "DIGU"));
            Assert.Equal(5_332_000UL, SixtyMeterChannels.DefaultDialHz("ZZ", "CW")); // no table: the US one, as before
        }

        [Theory]
        [InlineData(5_332_000UL, "USB", 5_330_500UL)]  // the old stop's dial
        [InlineData(5_330_500UL, "USB", 5_330_500UL)]  // a correct dial stays
        [InlineData(5_330_500UL, "CW", 5_332_000UL)]   // CW belongs on the centre
        [InlineData(5_373_000UL, "LSB", 5_371_500UL)]  // ends in USB
        [InlineData(5_405_000UL, "RTTY", 5_403_500UL)] // ends in DIGU
        [InlineData(5_340_000UL, "USB", 5_346_500UL)]  // between channels: the nearest, Channel 2
        [InlineData(5_366_000UL, "USB", 5_363_700UL)]  // in the segment, pulled down until the signal fits
        [InlineData(5_352_000UL, "LSB", 5_354_300UL)]  // pulled up until the signal fits
        [InlineData(5_351_500UL, "DIGL", 5_354_300UL)] // the old segment stop, in DIGL
        [InlineData(5_360_000UL, "AM", 5_360_000UL)]   // ends in USB, which fits where it is
        [InlineData(5_368_000UL, "USB", 5_363_700UL)]  // nearer the segment than Channel 3
        public void The_snap_moves_a_dial_to_the_lawful_one_for_the_final_mode(ulong hz, string mode, ulong expected)
        {
            ulong snapped = SixtyMeterChannels.SnapToDial("US", hz, mode);

            Assert.Equal(expected, snapped);
            string final = SixtyMeterChannels.GetModeCorrection("US", snapped, mode)?.RequiredMode ?? mode;
            Assert.True(SixtyMeterChannels.IsValidSixtyMeterFrequency("US", snapped, final),
                $"{snapped} is not lawful in {final}");
        }

        [Fact]
        public void The_snap_leaves_a_country_with_no_allocation_alone()
            => Assert.Equal(5_340_000UL, SixtyMeterChannels.SnapToDial("ZZ", 5_340_000, "AM"));
    }
}
