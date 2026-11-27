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

    [Theory]
    [InlineData("LSB")]
    [InlineData("DIGL")]
    [InlineData("AM")]
    [InlineData("FM")]
    [InlineData("FDVU")]
    public void Only_USB_Digital_USB_and_CW_are_allowed_on_a_channel(string mode) =>
        Assert.Equal(SixtyMeterReason.Mode, Judge(5_330_500, mode));

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
        Assert.Equal(SixtyMeterReason.Mode, Judge(5_357_000, "AM"));
    }

    [Fact]
    public void An_unreported_transmit_filter_falls_back_to_the_nominal_emission()
    {
        Assert.Equal(SixtyMeterReason.Emission, Judge(5_363_600, "USB", 0, 0));
        Assert.Equal(SixtyMeterReason.None, Judge(5_363_500, "USB", 0, 0));
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
        Assert.Equal(Lexicon.Get("audio.ptt.blocked_sixty_meter_mode"),
            new TransmitVerdict(LockoutReason.None, SixtyMeterReason.Mode).RefusalSentence());
        Assert.Equal(Lexicon.Get("audio.ptt.blocked_sixty_meter_emission"),
            new TransmitVerdict(LockoutReason.None, SixtyMeterReason.Emission).RefusalSentence());
        // Each 60 m sentence names the setting that refused it.
        foreach (var key in new[] { "audio.ptt.blocked_sixty_meter_dial", "audio.ptt.blocked_sixty_meter_mode",
                                    "audio.ptt.blocked_sixty_meter_emission", "audio.ptt.blocked_sixty_meter_outside" })
            Assert.Contains(Lexicon.Get("settings.license.enforce_tx_rules_label"), Lexicon.Get(key));
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
