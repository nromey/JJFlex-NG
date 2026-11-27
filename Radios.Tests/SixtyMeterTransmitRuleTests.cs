#nullable enable
using HamBands;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// #1161, ruled by Noel 2026-11-19 and 2026-11-26 (question 4 of the
/// 41-question sheet): "Enforce transmit rules" governs something real. On
/// 60 m a transmission is refused unless it is lawful under #1172's table: on a
/// channel at the exact dial for its mode, or in the segment, with the whole
/// emission inside and no wider than 2.8 kHz. On by default for a US operator,
/// independent of the TX band lockout; the box really turns it off.
/// </summary>
/// <remarks>
/// <para>Every figure is retyped from 97.303(h)(3) here (centres 5332, 5348,
/// 5373 and 5405 kHz; phone dial 1.5 kHz below; segment 5351.5 to 5366.5 kHz;
/// 2.8 kHz), never read back from the table.</para>
/// <para>The decisive guarantee for the configuration every US operator runs,
/// lockout OFF and this box ON, is that NOTHING off 60 m is refused: pinned by
/// <see cref="With_the_lockout_off_and_the_60_metre_rules_on_nothing_off_60_metres_is_refused"/>.</para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class SixtyMeterTransmitRuleTests
{
    /// <summary>The app's default transmit filter, 100 to 2,900 Hz.</summary>
    private static SixtyMeterReason Judge(ulong hz, string? mode, int txLow = 100, int txHigh = 2900) =>
        SixtyMeterChannels.JudgeTransmission("US", hz, mode, txLow, txHigh, out _);

    // ---- on a channel ----

    [Fact]
    public void USB_on_the_channel_dial_is_lawful_and_at_the_centre_is_the_wrong_dial()
    {
        Assert.Equal(SixtyMeterReason.None, Judge(5_330_500, "USB"));
        Assert.Equal(SixtyMeterReason.None, Judge(5_346_500, "DIGU"));
        Assert.Equal(SixtyMeterReason.Dial, Judge(5_332_000, "USB"));
        Assert.Equal(SixtyMeterReason.Dial, Judge(5_331_000, "USB"));   // inside the channel, off the dial
    }

    [Fact]
    public void CW_goes_on_the_centre_and_nowhere_else_on_a_channel()
    {
        Assert.Equal(SixtyMeterReason.None, Judge(5_332_000, "CW"));
        Assert.Equal(SixtyMeterReason.Dial, Judge(5_330_500, "CW"));
    }

    /// <summary>
    /// Repair round 1: these five rows pinned <c>Mode</c>, "this mode is not
    /// allowed here on 60 meters", which is a claim about the law the code
    /// cannot back. On a channel JJ Flexible allows only USB, Digital USB, RTTY
    /// and CW (the conservative reading of 97.303(h)(3)'s designators, the
    /// orchestrator's decision; night question J8), and says exactly that.
    /// </summary>
    [Theory]
    [InlineData("LSB")]
    [InlineData("DIGL")]
    [InlineData("AM")]
    [InlineData("FM")]
    [InlineData("FDVU")]
    public void Only_USB_Digital_USB_RTTY_and_CW_are_allowed_on_a_channel(string mode) =>
        Assert.Equal(SixtyMeterReason.ChannelMode, Judge(5_330_500, mode));

    [Fact]
    public void A_transmit_filter_wider_than_2_8_kHz_or_past_the_channel_edge_is_refused()
    {
        Assert.Equal(SixtyMeterReason.Emission, Judge(5_330_500, "USB", 100, 3400));   // 3.3 kHz wide
        Assert.Equal(SixtyMeterReason.Emission, Judge(5_330_500, "USB", 0, 2800));     // 100 Hz under the channel
        Assert.Equal(SixtyMeterReason.None, Judge(5_330_500, "USB", 100, 2900));       // fills it exactly
    }

    // ---- in the segment ----

    [Fact]
    public void The_segment_takes_its_modes_with_the_real_filter_inside_the_edges()
    {
        Assert.Equal(SixtyMeterReason.None, Judge(5_357_000, "USB"));
        Assert.Equal(SixtyMeterReason.None, Judge(5_357_000, "LSB"));
        Assert.Equal(SixtyMeterReason.None, Judge(5_357_000, "CW"));
        // 5363.6 USB with the default filter ends at 5366.5: lawful, though the
        // nominal 3 kHz sideband would have refused it. The real filter is judged.
        Assert.Equal(SixtyMeterReason.None, Judge(5_363_600, "USB"));
        Assert.Equal(SixtyMeterReason.Emission, Judge(5_366_000, "USB"));
        Assert.Equal(SixtyMeterReason.Emission, Judge(5_352_000, "LSB"));
        Assert.Equal(SixtyMeterReason.Emission, Judge(5_357_000, "USB", 100, 3500));
        // AM with the default filter: the carrier plus and minus 2.9 kHz, 5.8 kHz wide.
        Assert.Equal(SixtyMeterReason.Emission, Judge(5_357_000, "AM"));
    }

    /// <summary>
    /// Repair round 1: an unreported filter (both edges 0) is taken as the app's
    /// default transmit filter, 100 to 2,900 Hz, not the 3 kHz snapping margin,
    /// which refused a correct channel dial and blamed a filter nobody measured.
    /// The orchestrator's decision (night question J4). This test pinned the old
    /// margin (5363.6 USB unreported as Emission) and now pins the new reading.
    /// </summary>
    [Fact]
    public void An_unreported_transmit_filter_is_taken_as_the_app_s_default_and_said_to_be_assumed()
    {
        Assert.Equal(SixtyMeterReason.None, Judge(5_363_600, "USB", 0, 0));
        Assert.Equal(SixtyMeterReason.None, Judge(5_330_500, "USB", 0, 0));    // a correct channel dial
        Assert.Equal(SixtyMeterReason.None, Judge(5_346_500, "DIGU", 0, 0));
        Assert.Equal(SixtyMeterReason.EmissionFilterAssumed, Judge(5_366_000, "USB", 0, 0));
        Assert.Equal(SixtyMeterReason.EmissionFilterAssumed, Judge(5_357_000, "AM", 0, 0));   // 5.8 kHz wide
        Assert.Equal(SixtyMeterReason.Emission, Judge(5_366_000, "USB", 100, 2900));         // reported: the filter's
    }

    // ---- repair round 1: a refusal never blames what was not measured ----

    [Theory]
    [InlineData(5_351_600UL)]
    [InlineData(5_366_400UL)]
    public void CW_is_150_hertz_wide_so_CW_just_inside_the_segment_is_admitted(ulong hz)
    {
        Assert.Equal(SixtyMeterReason.None, Judge(hz, "CW"));
        Assert.Equal(SixtyMeterReason.None, Judge(hz, "CW", 0, 0));   // CW reads no filter
    }

    [Fact]
    public void CW_whose_150_hertz_cross_the_segment_edge_is_refused_with_the_estimated_sentence()
    {
        Lexicon.Load(Lexicon.Partitions);
        Assert.Equal(SixtyMeterReason.EmissionCwEstimated, Judge(5_351_550, "CW"));
        Assert.Equal(SixtyMeterReason.EmissionCwEstimated, Judge(5_366_450, "CW"));
        string said = new TransmitVerdict(LockoutReason.None, SixtyMeterReason.EmissionCwEstimated).RefusalSentence()!;
        Assert.Equal(Lexicon.Get("audio.ptt.blocked_sixty_meter_cw_edge"), said);
        Assert.DoesNotContain("transmit filter", said, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_a_reported_filter_is_blamed_and_an_assumed_one_says_it_was_assumed()
    {
        Lexicon.Load(Lexicon.Partitions);
        // Reported, past Channel 1's lower edge (5330.5 to 5333.3): the filter sentence.
        Assert.Equal(SixtyMeterReason.Emission, Judge(5_330_500, "USB", 0, 2800));
        Assert.Equal(Lexicon.Get("audio.ptt.blocked_sixty_meter_emission"),
            new TransmitVerdict(LockoutReason.None, SixtyMeterReason.Emission).RefusalSentence());
        string assumed = new TransmitVerdict(LockoutReason.None, SixtyMeterReason.EmissionFilterAssumed).RefusalSentence()!;
        Assert.Equal(Lexicon.Get("audio.ptt.blocked_sixty_meter_filter_assumed"), assumed);
        Assert.DoesNotContain("your transmit filter", assumed, System.StringComparison.OrdinalIgnoreCase);
        foreach (var key in new[] { "audio.ptt.blocked_sixty_meter_cw_edge", "audio.ptt.blocked_sixty_meter_filter_assumed" })
            Assert.Contains(Lexicon.Get("settings.license.enforce_tx_rules_label"), Lexicon.Get(key));
    }

    // ---- elsewhere on 60 m, and off it ----

    [Theory]
    [InlineData(5_340_000UL, "USB")]
    [InlineData(5_450_000UL, "CW")]
    [InlineData(5_299_500UL, "USB")]   // straddles the band's lower edge
    [InlineData(5_499_000UL, "USB")]   // straddles the upper
    public void On_60_metres_but_on_no_channel_or_segment_is_refused_as_outside(ulong hz, string mode) =>
        Assert.Equal(SixtyMeterReason.Outside, Judge(hz, mode));

    [Theory]
    [InlineData(14_200_000UL, "USB")]
    [InlineData(7_050_000UL, "CW")]
    [InlineData(28_400_000UL, "FM")]
    [InlineData(146_520_000UL, "FM")]
    [InlineData(5_250_000UL, "USB")]   // just below the band
    [InlineData(5_296_000UL, "USB")]   // its signal ends before 5.300
    public void Nothing_off_60_metres_is_judged(ulong hz, string mode) =>
        Assert.Equal(SixtyMeterReason.None, Judge(hz, mode));

    [Fact]
    public void RTTY_is_let_through_on_a_channel_and_in_the_segment_and_said_so()
    {
        Assert.Equal(SixtyMeterReason.None, SixtyMeterChannels.JudgeTransmission("US", 5_332_000, "RTTY", 100, 2900, out bool onChannel));
        Assert.True(onChannel);
        Assert.Equal(SixtyMeterReason.None, SixtyMeterChannels.JudgeTransmission("US", 5_357_000, "RTTY", 100, 2900, out bool inSegment));
        Assert.True(inSegment);
        SixtyMeterChannels.JudgeTransmission("US", 14_080_000, "RTTY", 100, 2900, out bool offSixty);
        Assert.False(offSixty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("SOMEWAVEFORM")]
    public void No_mode_or_an_unknown_one_is_judged_on_its_dial_alone(string? mode)
    {
        Assert.Equal(SixtyMeterReason.None, Judge(5_332_000, mode));
        Assert.Equal(SixtyMeterReason.None, Judge(5_357_000, mode));
        Assert.Equal(SixtyMeterReason.Outside, Judge(5_340_000, mode));
    }

    [Fact]
    public void A_country_with_no_60_metre_table_is_never_judged() =>
        Assert.Equal(SixtyMeterReason.None, SixtyMeterChannels.JudgeTransmission("CA", 5_340_000, "USB", 100, 2900, out _));

    // ---- the two switches together ----

    private static LicenseConfig Settings(bool lockout, bool sixty, Bands.Licenses cls = Bands.Licenses.extra) =>
        new() { LicenseClass = cls, TxLockout = lockout, EnforceTxRules = sixty, Country = "US" };

    private static TransmitVerdict Verdict(LicenseConfig license, ulong hz, string mode, bool sixtyMeterRule = true)
    {
        var (low, high) = mode switch { "CW" => (-250, 250), "FM" => (-8000, 8000), "LSB" => (-2800, -100), _ => (100, 2800) };
        var reading = new TransmitSliceReading(hz, mode, low, high) { TxFilterLow = 100, TxFilterHigh = 2900 };
        return TransmitRules.Judge(license, reading, 0, 0, 0, sixtyMeterRule, out _, out _, out _);
    }

    [Theory]
    [InlineData(14_200_000UL, "USB")]
    [InlineData(7_050_000UL, "CW")]
    [InlineData(28_400_000UL, "FM")]
    [InlineData(146_520_000UL, "FM")]
    [InlineData(5_250_000UL, "USB")]
    public void With_the_lockout_off_and_the_60_metre_rules_on_nothing_off_60_metres_is_refused(ulong hz, string mode)
    {
        // Every US operator's defaults (LicenseConfig's own), and a Technician too.
        var defaults = new LicenseConfig();
        Assert.False(defaults.TxLockout);
        Assert.True(defaults.EnforceTxRules);
        Assert.True(Verdict(defaults, hz, mode).IsAllowed);
        Assert.True(Verdict(Settings(lockout: false, sixty: true, Bands.Licenses.technition), hz, mode).IsAllowed);
    }

    [Fact]
    public void The_60_metre_rules_refuse_with_the_lockout_off_and_the_box_really_turns_them_off()
    {
        var on = Verdict(Settings(lockout: false, sixty: true), 5_332_000, "USB");
        Assert.Equal(new TransmitVerdict(LockoutReason.None, SixtyMeterReason.Dial), on);
        Assert.True(Verdict(Settings(lockout: false, sixty: false), 5_332_000, "USB").IsAllowed);
        Assert.True(Verdict(Settings(lockout: false, sixty: true), 5_330_500, "USB").IsAllowed, "positive control: the dial");
    }

    [Fact]
    public void The_antenna_tuner_is_not_judged_by_the_60_metre_rules()
    {
        Assert.True(Verdict(Settings(lockout: false, sixty: true), 5_340_000, "USB", sixtyMeterRule: false).IsAllowed);
        Assert.False(Verdict(Settings(lockout: false, sixty: true), 5_340_000, "USB").IsAllowed);
    }

    [Fact]
    public void With_both_off_nothing_is_ever_refused()
    {
        var off = Settings(lockout: false, sixty: false, Bands.Licenses.novice);
        Assert.True(Verdict(off, 5_340_000, "AM").IsAllowed);
        Assert.True(Verdict(off, 14_200_000, "USB").IsAllowed);
        Assert.True(Verdict(off, 146_520_000, "FM").IsAllowed);
    }

    [Fact]
    public void When_both_refuse_the_sentence_names_both()
    {
        Lexicon.Load(Lexicon.Partitions);
        var both = Verdict(Settings(lockout: true, sixty: true, Bands.Licenses.technition), 5_332_000, "USB");
        Assert.Equal(new TransmitVerdict(LockoutReason.Frequency, SixtyMeterReason.Dial), both);
        string said = both.RefusalSentence()!;
        Assert.Contains(Lexicon.Get("audio.ptt.blocked_by_license"), said);
        Assert.Contains(Lexicon.Get("audio.ptt.blocked_sixty_meter_dial"), said);
        Assert.Equal(Lexicon.Get("audio.ptt.blocked_sixty_meter_outside"),
            new TransmitVerdict(LockoutReason.None, SixtyMeterReason.Outside).RefusalSentence());
        Assert.Equal(Lexicon.Get("audio.ptt.blocked_sixty_meter_channel_mode"),
            new TransmitVerdict(LockoutReason.None, SixtyMeterReason.ChannelMode).RefusalSentence());
        Assert.Equal(Lexicon.Get("audio.ptt.blocked_sixty_meter_too_wide_mode"),
            new TransmitVerdict(LockoutReason.None, SixtyMeterReason.TooWideMode).RefusalSentence());
        Assert.Equal(Lexicon.Get("audio.ptt.blocked_sixty_meter_emission"),
            new TransmitVerdict(LockoutReason.None, SixtyMeterReason.Emission).RefusalSentence());
        // Each 60 m sentence names the setting that refused it.
        foreach (var key in new[] { "audio.ptt.blocked_sixty_meter_dial", "audio.ptt.blocked_sixty_meter_channel_mode",
                                    "audio.ptt.blocked_sixty_meter_too_wide_mode",
                                    "audio.ptt.blocked_sixty_meter_emission", "audio.ptt.blocked_sixty_meter_outside" })
            Assert.Contains(Lexicon.Get("settings.license.enforce_tx_rules_label"), Lexicon.Get(key));
        // Repair round 1: never a claim that a mode is unlawful.
        foreach (var key in new[] { "audio.ptt.blocked_sixty_meter_channel_mode", "audio.ptt.blocked_sixty_meter_too_wide_mode" })
            Assert.DoesNotContain("not allowed here", Lexicon.Get(key), System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void With_no_transmit_slice_of_ours_the_selected_slice_is_judged_on_its_dial_alone()
    {
        var settings = Settings(lockout: false, sixty: true);
        Assert.Equal(SixtyMeterReason.Outside,
            TransmitRules.Judge(settings, null, 5_340_000, 100, 2800, true, out bool judgedSelected, out _, out _).SixtyMeter);
        Assert.True(judgedSelected);
        Assert.True(TransmitRules.Judge(settings, null, 5_332_000, 100, 2800, true, out _, out _, out _).IsAllowed);
    }

    [Fact]
    public void The_checkbox_no_longer_promises_power_limits_and_has_its_words_from_the_Lexicon()
    {
        Lexicon.Load(Lexicon.Partitions);
        string root = FieldKeyMapScan.RepoRoot();
        string xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "JJFlexWpf", "Dialogs", "SettingsDialog.xaml"));
        string code = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"));
        Assert.DoesNotContain("Enforce transmit rules (60m channels, power limits)", xaml, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Enforce transmit parameters based on published", xaml, System.StringComparison.Ordinal);
        // Built, so the Lexicon's key scan does not read these lines as keys.
        string label = string.Join(".", "settings", "license", "enforce_tx_rules_label");
        string help = string.Join(".", "settings", "license", "enforce_tx_rules_help");
        var lines = code.Split('\n');
        Assert.Contains(lines, l => l.Contains("EnforceTxRulesCheckbox.Content = ") && l.Contains(label));
        Assert.Contains(lines, l => l.Contains("JJFlexHelp.SetText(EnforceTxRulesCheckbox, ") && l.Contains(help));
        Assert.DoesNotContain("power limit", Lexicon.Get("settings.license.enforce_tx_rules_label"), System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not limit your power", Lexicon.Get("settings.license.enforce_tx_rules_help"));
    }

    // ---- repair round 1: which check owns what ----
    //
    // The lockout judges the class's privileges and keeps the 60 m allocation
    // edges (each channel's centre plus and minus 1.4 kHz, and the segment); the
    // 60 m rule owns the rest: which modes go on a channel, the dial per mode,
    // and the 2.8 kHz width where the edges do not already settle it. So with
    // the lockout on and the box off, a signal that fits the allocation is
    // admitted even when the 60 m rule would refuse it: off means off (the
    // orchestrator's decision, repair round 1).

    private static TransmitVerdict Judged(LicenseConfig license, ulong hz, string mode, int sliceLow, int sliceHigh,
        int txLow = 100, int txHigh = 2900)
    {
        var reading = new TransmitSliceReading(hz, mode, sliceLow, sliceHigh) { TxFilterLow = txLow, TxFilterHigh = txHigh };
        return TransmitRules.Judge(license, reading, 0, 0, 0, true, out _, out _, out _);
    }

    [Fact]
    public void Narrow_AM_in_the_segment_is_admitted_by_both_checks()
    {
        Assert.Equal(SixtyMeterReason.None, Judge(5_357_000, "AM", 100, 1200));
        Assert.True(Judged(Settings(lockout: true, sixty: true, Bands.Licenses.general), 5_357_000, "AM", -1200, 1200, 100, 1200).IsAllowed);
        Assert.True(Judged(Settings(lockout: false, sixty: true), 5_357_000, "AM", -1200, 1200, 100, 1200).IsAllowed);
    }

    [Fact]
    public void AM_with_the_default_filter_is_refused_for_width_by_the_60_metre_rule_and_admitted_by_the_lockout()
    {
        Assert.Equal(new TransmitVerdict(LockoutReason.None, SixtyMeterReason.Emission),
            Judged(Settings(lockout: true, sixty: true, Bands.Licenses.general), 5_357_000, "AM", -2900, 2900));
        Assert.Equal(new TransmitVerdict(LockoutReason.None, SixtyMeterReason.Emission),
            Judged(Settings(lockout: false, sixty: true), 5_357_000, "AM", -2900, 2900));   // box on, lockout off
        Assert.True(Judged(Settings(lockout: true, sixty: false, Bands.Licenses.general), 5_357_000, "AM", -2900, 2900).IsAllowed);
    }

    [Theory]
    [InlineData("FDVU")]
    [InlineData("FDV")]
    public void FreeDV_upper_in_the_segment_is_placed_like_USB_and_admitted(string mode)
    {
        Assert.Equal(SixtyMeterReason.None, Judge(5_357_000, mode));
        Assert.True(Judged(Settings(lockout: true, sixty: true, Bands.Licenses.general), 5_357_000, mode, 100, 2900).IsAllowed);
        Assert.True(Judged(Settings(lockout: false, sixty: true), 5_357_000, mode, 100, 2900).IsAllowed);
        // Placed like USB: past the segment's top it spills, as USB does.
        Assert.Equal(SixtyMeterReason.Emission, Judge(5_366_000, mode));
        // FreeDV lower is placed like LSB: at 5352.0 it reaches below the segment, at 5354.5 it fits.
        Assert.Equal(SixtyMeterReason.Emission, Judge(5_352_000, "FDVL"));
        Assert.Equal(SixtyMeterReason.None, Judge(5_354_500, "FDVL"));
        Assert.Equal(SixtyMeterReason.None, Judge(5_352_000, "FDVU"));
    }

    [Theory]
    [InlineData("FM")]
    [InlineData("NFM")]
    [InlineData("DFM")]
    [InlineData("DSTR")]
    public void FM_and_D_STAR_in_the_segment_are_refused_as_wider_than_60_metres_allows(string mode)
    {
        Assert.Equal(SixtyMeterReason.TooWideMode, Judge(5_357_000, mode));
        Assert.Equal(SixtyMeterReason.TooWideMode,
            Judged(Settings(lockout: false, sixty: true), 5_357_000, mode, -5500, 5500).SixtyMeter);
    }

    [Fact]
    public void Narrow_AM_on_a_channel_fits_the_allocation_but_the_60_metre_rule_refuses_it_on_a_channel()
    {
        var general = Settings(lockout: true, sixty: false, Bands.Licenses.general);
        Assert.True(Judged(general, 5_332_000, "AM", -1200, 1200).IsAllowed);   // the lockout alone
        Assert.Equal(new TransmitVerdict(LockoutReason.None, SixtyMeterReason.ChannelMode),
            Judged(Settings(lockout: true, sixty: true, Bands.Licenses.general), 5_332_000, "AM", -1200, 1200));
        Assert.Equal(new TransmitVerdict(LockoutReason.None, SixtyMeterReason.ChannelMode),
            Judged(Settings(lockout: false, sixty: true), 5_332_000, "AM", -1200, 1200));   // box on, lockout off
    }

    [Fact]
    public void LSB_at_the_mirrored_dial_fits_the_channel_but_the_60_metre_rule_refuses_it_on_a_channel()
    {
        // 5333.5 LSB with the default 100 to 2,900 filter fills 5330.6 to 5333.4, Channel 1 exactly.
        Assert.True(Judged(Settings(lockout: true, sixty: false, Bands.Licenses.general), 5_333_500, "LSB", -2900, -100).IsAllowed);
        Assert.Equal(new TransmitVerdict(LockoutReason.None, SixtyMeterReason.ChannelMode),
            Judged(Settings(lockout: true, sixty: true, Bands.Licenses.general), 5_333_500, "LSB", -2900, -100));
        Assert.Equal(SixtyMeterReason.ChannelMode, Judge(5_333_500, "LSB"));
    }

    [Fact]
    public void AM_wider_than_a_channel_is_refused_by_the_lockout_for_its_frequency()
    {
        // The channel edge is the allocation edge: never widened to make a test pass.
        Assert.Equal(new TransmitVerdict(LockoutReason.Frequency),
            Judged(Settings(lockout: true, sixty: false, Bands.Licenses.general), 5_332_000, "AM", -3000, 3000));
    }

    [Fact]
    public void With_the_lockout_on_and_the_box_off_narrow_FM_that_fits_the_segment_is_admitted_and_wide_FM_is_not()
    {
        var lockoutOnly = Settings(lockout: true, sixty: false, Bands.Licenses.general);
        Assert.True(Judged(lockoutOnly, 5_359_000, "NFM", -5500, 5500).IsAllowed);
        // The 16 kHz FM fixture runs from 5351.0 to 5367.0, past both segment edges.
        Assert.Equal(new TransmitVerdict(LockoutReason.Frequency), Judged(lockoutOnly, 5_359_000, "FM", -8000, 8000));
        // Positive control: the box on refuses the narrow one for its mode's width.
        Assert.Equal(SixtyMeterReason.TooWideMode,
            Judged(Settings(lockout: true, sixty: true, Bands.Licenses.general), 5_359_000, "NFM", -5500, 5500).SixtyMeter);
    }

    // With a 100 to 1,200 Hz filter these rows lie wholly in the gap (5,349,800 to
    // 5,350,900 Hz) and do not reach Channel 2's top (5,349,400): repair round 2
    // corrected this comment, which said they reached into Channel 2. The
    // reaching case is LSB_in_the_gap_with_the_default_filter_is_outside.
    [Theory]
    [InlineData(5_351_000UL, "LSB")]   // wholly in the gap below the segment
    [InlineData(5_351_000UL, "DIGL")]
    public void A_signal_in_the_gap_between_a_channel_and_the_segment_is_outside_never_a_mode_refusal(ulong hz, string mode) =>
        Assert.Equal(SixtyMeterReason.Outside, Judge(hz, mode, 100, 1200));

    // ---- repair round 2: a dial inside a channel gets the channel sentence ----

    [Theory]
    [InlineData(5_332_500UL, "AM", 100, 1200)]    // 500 Hz above Channel 1's centre
    [InlineData(5_331_500UL, "LSB", 100, 2900)]
    [InlineData(5_347_200UL, "DIGL", 100, 2900)]
    [InlineData(5_373_600UL, "FDVU", 100, 2900)]
    [InlineData(5_404_300UL, "FM", 100, 2900)]
    public void A_mode_the_channels_do_not_take_dialled_inside_a_channel_is_told_what_a_channel_allows(ulong hz, string mode, int low, int high) =>
        Assert.Equal(SixtyMeterReason.ChannelMode, Judge(hz, mode, low, high));

    [Fact]
    public void The_channel_sentence_changes_no_verdict()
    {
        // FreeDV upper dialled just inside Channel 2's top with a 2,200 to 2,900 filter lies
        // wholly in the segment: admitted before repair round 2, and still.
        Assert.Equal(SixtyMeterReason.None, Judge(5_349_300, "FDVU", 2200, 2900));
        // FM dialled there reaches the segment: still too wide, never the channel sentence.
        Assert.Equal(SixtyMeterReason.TooWideMode, Judge(5_349_300, "FM", 2200, 2900));
        // USB and Digital USB keep their own reasons (the Opus re-check's smaller relatives, left as they are).
        Assert.Equal(SixtyMeterReason.Outside, Judge(5_333_350, "USB", 100, 2900));
        // Off every channel and the segment: still outside.
        Assert.Equal(SixtyMeterReason.Outside, Judge(5_340_000, "AM", 100, 1200));
    }

    [Fact]
    public void AME_is_judged_like_AM_by_the_60_metre_rule()
    {
        // Repair round 2: AME was unknown, so judged on its dial alone, and admitted at a
        // channel centre and anywhere in the segment whatever its width.
        Assert.Equal(SixtyMeterReason.ChannelMode, Judge(5_332_000, "AME"));
        Assert.Equal(SixtyMeterReason.Emission, Judge(5_357_000, "AME"));          // 5.8 kHz with the default filter
        Assert.Equal(SixtyMeterReason.None, Judge(5_357_000, "AME", 100, 1200));   // narrow, like narrow AM
        foreach (var (hz, low, high) in new[] { (5_332_000UL, 100, 2900), (5_357_000UL, 100, 2900), (5_357_000UL, 100, 1200) })
            Assert.Equal(Judge(hz, "AM", low, high), Judge(hz, "AME", low, high));
    }

    [Fact]
    public void LSB_in_the_gap_with_the_default_filter_is_outside()
    {
        Assert.Equal(SixtyMeterReason.Outside, Judge(5_351_000, "LSB"));
        Assert.Equal(SixtyMeterReason.None, Judge(5_357_000, "LSB"));   // positive control: in the segment
    }

    [Fact]
    public void The_transmit_slice_reading_carries_the_radio_s_transmit_filter()
    {
        using var c = new RadioCase();
        c.F.Vendor.Status("transmit lo=150 hi=2750");
        var reading = c.F.Rig.TransmitSliceSignal();
        Assert.NotNull(reading);
        Assert.Equal(150, reading!.Value.TxFilterLow);
        Assert.Equal(2750, reading.Value.TxFilterHigh);
    }
}
