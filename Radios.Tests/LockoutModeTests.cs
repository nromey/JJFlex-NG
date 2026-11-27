#nullable enable
using HamBands;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #1310, ruled by Noel 2026-11-26 (question 6 of the 41-question sheet): the
/// TX band lockout judges the mode as well as the frequency, after the US band
/// table was corrected (#1282). A signal is allowed only where its mode is
/// allowed for the class (97.305(c), 97.307(f)(9) and (10)), and CW anywhere in
/// the class's privileges (97.305(a)), so CW across a mode seam is no longer
/// refused.
/// </summary>
/// <remarks>
/// Tests <see cref="TransmitBandLockout.AllowsReason"/> and
/// <see cref="TransmitBandLockout.JudgeReason"/>, the pure judgement
/// <c>FreqOutHandlers.JudgeTransmitHere</c> runs. The frequency-only
/// <see cref="TransmitBandLockout.Allows"/> and <see cref="TransmitBandLockout.Judge"/>
/// are unchanged, and still pinned by <see cref="TransmitBandLockoutTests"/>.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class LockoutModeTests
{
    private const Bands.Licenses Novice = Bands.Licenses.novice;
    private const Bands.Licenses Technician = Bands.Licenses.technition;
    private const Bands.Licenses General = Bands.Licenses.general;
    private const Bands.Licenses Extra = Bands.Licenses.extra;

    private static LockoutReason Judge(ulong hz, string? mode, Bands.Licenses license)
    {
        var (low, high) = mode?.ToUpperInvariant() switch
        {
            "USB" or "FDVU" => (100, 2800),
            "LSB" or "FDVL" => (-2800, -100),
            "DIGU" => (0, 3000),
            "DIGL" => (-3000, 0),
            "CW" => (-250, 250),
            "AM" or "SAM" => (-3000, 3000),
            "RTTY" => (-285, 115),
            _ => (-8000, 8000),   // FM and the rest
        };
        return TransmitBandLockout.AllowsReason(hz, mode, low, high, license, out _);
    }

    // ---- the ruled refusals ----

    [Theory]
    [InlineData(3_550_000UL)]
    [InlineData(7_050_000UL)]
    [InlineData(21_100_000UL)]
    public void A_Technician_or_Novice_in_SSB_in_the_CW_segments_is_refused_for_the_mode(ulong hz)
    {
        Assert.Equal(LockoutReason.Mode, Judge(hz, "USB", Technician));
        Assert.Equal(LockoutReason.Mode, Judge(hz, "LSB", Novice));
        Assert.Equal(LockoutReason.Mode, Judge(hz, "DIGU", Technician));
        Assert.Equal(LockoutReason.None, Judge(hz, "CW", Technician));   // positive control
    }

    [Fact]
    public void AM_and_FM_in_28_3_to_28_5_are_refused_for_a_Technician_and_SSB_is_not()
    {
        Assert.Equal(LockoutReason.Mode, Judge(28_400_000, "FM", Technician));
        Assert.Equal(LockoutReason.Mode, Judge(28_400_000, "AM", Novice));
        Assert.Equal(LockoutReason.None, Judge(28_400_000, "USB", Technician));
        Assert.Equal(LockoutReason.None, Judge(28_400_000, "CW", Technician));
        Assert.Equal(LockoutReason.None, Judge(28_400_000, "FM", General));   // positive control: General phone there
    }

    [Fact]
    public void FreeDV_is_phone_and_not_single_sideband_voice()
    {
        // 97.3(c)(5): J2E is phone; 97.307(f)(10) allows Novice and Technician only J3E or R3E there.
        Assert.Equal(LockoutReason.Mode, Judge(28_400_000, "FDVU", Technician));
        Assert.Equal(LockoutReason.None, Judge(14_250_000, "FDVU", Extra));
        Assert.Equal(LockoutReason.Mode, Judge(14_100_000, "FDVU", Extra));
    }

    [Fact]
    public void Phone_in_a_data_segment_is_refused_for_every_class()
    {
        Assert.Equal(LockoutReason.Mode, Judge(14_100_000, "USB", General));
        Assert.Equal(LockoutReason.Mode, Judge(14_100_000, "USB", Extra));
        Assert.Equal(LockoutReason.Mode, Judge(10_120_000, "USB", Extra));
        Assert.Equal(LockoutReason.None, Judge(14_074_000, "DIGU", General));   // positive control: data there
    }

    [Fact]
    public void Phone_on_219_5_megahertz_is_refused_and_data_is_not()
    {
        // 219 to 220 MHz is data only (97.305(c)(4)(iv)).
        Assert.Equal(LockoutReason.Mode, Judge(219_500_000, "FM", Technician));
        Assert.Equal(LockoutReason.None, Judge(219_500_000, "DIGU", Technician));
    }

    [Fact]
    public void A_sound_card_mode_is_allowed_where_image_is_so_SSTV_on_14_230_is_not_refused()
    {
        Assert.Equal(LockoutReason.None, Judge(14_230_000, "DIGU", General));
        Assert.Equal(LockoutReason.Mode, Judge(50_050_000, "DIGU", Technician));   // CW only below 50.1 MHz
    }

    // ---- CW across a seam ----

    [Theory]
    [InlineData(3_600_000UL, "extra")]
    [InlineData(7_125_000UL, "extra")]
    [InlineData(7_125_000UL, "advanced")]
    [InlineData(14_150_000UL, "extra")]
    [InlineData(21_200_000UL, "extra")]
    [InlineData(28_300_000UL, "general")]
    [InlineData(50_100_000UL, "technition")]
    [InlineData(144_100_000UL, "technition")]
    public void CW_straddling_a_mode_seam_inside_the_privileges_is_allowed(ulong seam, string license)
    {
        var cls = System.Enum.Parse<Bands.Licenses>(license);
        Assert.Equal(LockoutReason.None, Judge(seam, "CW", cls));
        // The frequency-only arithmetic, unchanged, refused it: the reason this case exists.
        Assert.False(TransmitBandLockout.Allows(seam, "CW", -250, 250, cls));
    }

    [Fact]
    public void CW_straddling_the_edge_of_the_privileges_is_still_refused_for_the_frequency()
    {
        Assert.Equal(LockoutReason.Frequency, Judge(14_000_100, "CW", Extra));
        Assert.Equal(LockoutReason.Frequency, Judge(14_025_100, "CW", General));
    }

    // ---- frequency, and what is never a mode refusal ----

    [Fact]
    public void Outside_the_privileges_is_a_frequency_refusal_whatever_the_mode()
    {
        Assert.Equal(LockoutReason.Frequency, Judge(14_200_000, "USB", Technician));
        Assert.Equal(LockoutReason.Frequency, Judge(14_200_000, "CW", Technician));
        Assert.Equal(LockoutReason.Frequency, Judge(50_200_000, "USB", Novice));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SOMEWAVEFORM")]
    public void No_mode_or_an_unknown_one_is_judged_on_frequency_alone_never_for_its_mode(string? mode)
    {
        // SSB-like edges in the Technician CW segment: a known SSB mode is refused
        // for the mode, an unknown one is judged as the lockout always judged it.
        var reason = TransmitBandLockout.AllowsReason(7_050_000, mode, 100, 2800, Technician, out bool unknown);
        Assert.Equal(LockoutReason.None, reason);
        Assert.Equal(!string.IsNullOrEmpty(mode), unknown);
        Assert.Equal(LockoutReason.Frequency,
            TransmitBandLockout.AllowsReason(14_200_000, mode, 100, 2800, Technician, out _));
    }

    [Fact]
    public void With_no_transmit_slice_of_ours_it_is_never_a_mode_refusal()
    {
        var reason = TransmitBandLockout.JudgeReason(null, 7_050_000, 100, 2800, Technician, out bool judgedSelected, out _);
        Assert.True(judgedSelected);
        Assert.Equal(LockoutReason.None, reason);
        var usb = new TransmitSliceReading(7_050_000, "USB", 100, 2800);
        Assert.Equal(LockoutReason.Mode,
            TransmitBandLockout.JudgeReason(usb, 0, 0, 0, Technician, out _, out _));   // positive control: the transmit slice's mode is judged
    }

    [Fact]
    public void The_second_frequency_of_an_unmeasured_offset_is_judged_too()
    {
        var both = new TransmitSliceReading(146_520_000, "NFM", -8000, 8000) { AlsoJudgeHz = 151_000_000 };
        Assert.Equal(LockoutReason.Frequency, TransmitBandLockout.JudgeReason(both, 0, 0, 0, Technician, out _, out _));
    }

    [Theory]
    [InlineData("CW", TransmitBandLockout.EmissionKind.Cw)]
    [InlineData("usb", TransmitBandLockout.EmissionKind.SsbPhone)]
    [InlineData("LSB", TransmitBandLockout.EmissionKind.SsbPhone)]
    [InlineData("AM", TransmitBandLockout.EmissionKind.OtherPhone)]
    [InlineData("SAM", TransmitBandLockout.EmissionKind.OtherPhone)]
    [InlineData("FM", TransmitBandLockout.EmissionKind.OtherPhone)]
    [InlineData("NFM", TransmitBandLockout.EmissionKind.OtherPhone)]
    [InlineData("DFM", TransmitBandLockout.EmissionKind.OtherPhone)]
    [InlineData("DSTR", TransmitBandLockout.EmissionKind.OtherPhone)]
    [InlineData("FDV", TransmitBandLockout.EmissionKind.OtherPhone)]
    [InlineData("FDVU", TransmitBandLockout.EmissionKind.OtherPhone)]
    [InlineData("DIGU", TransmitBandLockout.EmissionKind.DataOrImage)]
    [InlineData("DIGL", TransmitBandLockout.EmissionKind.DataOrImage)]
    [InlineData("RTTY", TransmitBandLockout.EmissionKind.Rtty)]
    public void Each_FlexLib_mode_has_its_Part_97_kind(string mode, TransmitBandLockout.EmissionKind kind) =>
        Assert.Equal(kind, TransmitBandLockout.KindOf(mode));

    [Fact]
    public void The_verdict_speaks_the_approved_sentence_for_the_frequency_and_its_own_for_the_mode()
    {
        Lexicon.Load(Lexicon.Partitions);
        Assert.Null(TransmitVerdict.Allowed.RefusalSentence());
        Assert.True(TransmitVerdict.Allowed.IsAllowed);
        Assert.Equal(Lexicon.Get("audio.ptt.blocked_by_license"), TransmitVerdict.LockoutFrequency.RefusalSentence());
        Assert.Equal(Lexicon.Get("audio.ptt.blocked_by_license_mode"), new TransmitVerdict(LockoutReason.Mode).RefusalSentence());
        Assert.NotEqual(Lexicon.Get("audio.ptt.blocked_by_license"), Lexicon.Get("audio.ptt.blocked_by_license_mode"));
    }

    // ---- repair round 1: a sideband signal is judged by the transmit filter ----

    private static LockoutReason Transmitting(ulong hz, string mode, int sliceLow, int sliceHigh, int txLow, int txHigh,
        Bands.Licenses license) =>
        TransmitBandLockout.JudgeReason(new TransmitSliceReading(hz, mode, sliceLow, sliceHigh) { TxFilterLow = txLow, TxFilterHigh = txHigh },
            0, 0, 0, license, out _, out _);

    [Fact]
    public void A_receive_filter_wider_than_the_channel_no_longer_refuses_a_lawful_60_metre_channel_transmission()
    {
        // The Opus check's probe: General, 5330.5 USB, transmit filter 100 to 2,900,
        // slice (receive) filter 100 to 3,000. Refused for its frequency until repair 1.
        Assert.Equal(LockoutReason.None, Transmitting(5_330_500, "USB", 100, 3000, 100, 2900, General));
        Assert.Equal(LockoutReason.None, Transmitting(5_346_500, "DIGU", 0, 3200, 100, 2900, General));
        // Unreported: the app's default, 100 to 2,900.
        Assert.Equal(LockoutReason.None, Transmitting(5_330_500, "USB", 100, 3000, 0, 0, General));
    }

    [Fact]
    public void A_sideband_transmit_filter_that_crosses_an_edge_is_still_refused()
    {
        // 100 to 3,100 above 5330.5 ends 100 Hz past Channel 1 (5333.4).
        Assert.Equal(LockoutReason.Frequency, Transmitting(5_330_500, "USB", 100, 2800, 100, 3100, General));
        // LSB at 7.177 MHz with 100 to 2,900 starts at 7.1741, below General's 7.175:
        // refused, though the narrow slice filter alone would have fitted.
        Assert.Equal(LockoutReason.Frequency, Transmitting(7_177_000, "LSB", -1800, -100, 100, 2900, General));
        Assert.Equal(LockoutReason.Frequency, Transmitting(7_177_000, "DIGL", -1800, -100, 100, 2900, General));
        Assert.Equal(LockoutReason.None, Transmitting(7_178_000, "LSB", -1800, -100, 100, 2900, General));   // positive control
    }

    [Fact]
    public void FM_keeps_the_slice_filter_so_FM_beside_a_CW_only_segment_or_a_band_edge_is_still_refused()
    {
        // A transmit filter describes no FM signal: its width comes from its deviation.
        Assert.Equal(LockoutReason.Mode, Transmitting(144_104_000, "FM", -8000, 8000, 100, 2900, Technician));
        Assert.Equal(LockoutReason.Frequency, Transmitting(53_996_000, "FM", -8000, 8000, 100, 2900, Technician));
        Assert.Equal(LockoutReason.None, Transmitting(146_520_000, "FM", -8000, 8000, 100, 2900, Technician));   // positive control
    }
}
