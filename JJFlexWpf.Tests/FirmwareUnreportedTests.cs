using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Radio Setup's firmware line with a connected radio whose version is not
/// reported (#1071 rider, from Codex's check): the version is unknown, so the
/// line claims neither a match nor a difference. It used to go on to say the
/// firmware matched what the library expects.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> A pure static decision only: no window or element is
/// built, and every test asserts it runs on an MTA thread, where WPF cannot
/// build one.
/// </remarks>
public sealed class FirmwareUnreportedTests
{
    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    public FirmwareUnreportedTests() => Lexicon.Load(Lexicon.Partitions);

    private static string Matches => Lexicon.Get("settings.radio.firmware.matches");
    private static string Unreported => Lexicon.Get("settings.radio.firmware.version_unreported");

    [Fact]
    public void An_unreported_version_claims_no_match_and_no_difference()
    {
        AssertWindowless();
        var (parts, showSuppress) = SettingsDialog.FirmwareStatusSentences("", inRecovery: false, bypassed: false,
            differs: false, local: true);
        Assert.Equal(new[] { Unreported }, parts);
        Assert.False(showSuppress);
    }

    [Fact]
    public void An_unreported_version_over_SmartLink_says_only_that_and_the_transport()
    {
        AssertWindowless();
        var (parts, _) = SettingsDialog.FirmwareStatusSentences("", false, false, false, local: false);
        Assert.Equal(new[] { Unreported, Lexicon.Get("settings.radio.firmware.smartlink_cannot_carry") }, parts);
        Assert.DoesNotContain(Matches, parts);
    }

    /// <summary>The controls: a reported version still says it matches, or differs with the button.</summary>
    [Fact]
    public void A_reported_version_still_matches_or_differs()
    {
        AssertWindowless();
        var same = SettingsDialog.FirmwareStatusSentences("3.3.32.8203", false, false, differs: false, local: true);
        Assert.Equal(Matches, same.Parts[^1]);
        Assert.False(same.ShowSuppress);

        var other = SettingsDialog.FirmwareStatusSentences("4.1.5.39794", false, false, differs: true, local: true);
        Assert.Equal(Lexicon.Get("settings.radio.firmware.differs_from_library",
            ("expected", Radios.FlexBase.LibraryExpectedFirmwareVersion)), other.Parts[^1]);
        Assert.True(other.ShowSuppress);
    }

    [Fact]
    public void The_bypass_is_said_whether_or_not_the_version_is_reported()
    {
        AssertWindowless();
        string off = Lexicon.Get("settings.radio.firmware.check_switched_off");
        Assert.Equal(new[] { Unreported, off }, SettingsDialog.FirmwareStatusSentences("", false, bypassed: true, false, true).Parts);
        Assert.False(SettingsDialog.FirmwareStatusSentences("4.1.5.39794", false, bypassed: true, differs: true, true).ShowSuppress);
    }
}
