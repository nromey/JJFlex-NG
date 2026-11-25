using System;
using System.IO;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #564: Home's status line said "Radio connected — power off", with "Off" in
/// the power field, for every cause, including ones where the app knows only
/// that it stopped hearing from the radio. The causes below are the ones that
/// really reach that line (read from the code at batch 29).
/// </summary>
public sealed class PowerOffStatusTests
{
    private static string PowerOff => Lexicon.Get("connect.home.status_power_off");

    [Fact]
    public void A_profile_import_and_a_confirmed_restart_keep_the_power_off_line()
    {
        // Deliberate: the import says nothing about a dropped link.
        Assert.Equal(PowerOff, PowerOffStatus.StatusText(PowerOffStatus.Cause.ProfileImport));
        Assert.Equal(PowerOff, PowerOffStatus.StatusText(PowerOffStatus.Cause.Restart));
        Assert.Equal("Off", PowerOffStatus.PowerField(PowerOffStatus.Cause.ProfileImport));
        Assert.Equal("Off", PowerOffStatus.PowerField(PowerOffStatus.Cause.Restart));
        Assert.DoesNotContain("dropped", PowerOffStatus.StatusText(PowerOffStatus.Cause.ProfileImport));
    }

    [Theory]
    [InlineData(PowerOffStatus.Cause.Disconnected)]
    [InlineData(PowerOffStatus.Cause.StatusUnreadable)]
    public void A_cause_the_app_cannot_vouch_for_claims_neither_connected_nor_power_off(PowerOffStatus.Cause cause)
    {
        string text = PowerOffStatus.StatusText(cause);
        Assert.NotEqual(PowerOff, text);
        Assert.DoesNotContain("power off", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connected —", text, StringComparison.Ordinal);
        Assert.NotEqual("Off", PowerOffStatus.PowerField(cause));
    }

    [Fact]
    public void A_status_poll_that_failed_hedges_as_the_connect_failures_do()
    {
        string text = PowerOffStatus.StatusText(PowerOffStatus.Cause.StatusUnreadable);
        Assert.Contains("may be powered off", text, StringComparison.Ordinal);
        Assert.Contains("connection", text, StringComparison.Ordinal);
        Assert.DoesNotContain("another client", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>SOURCE PINS: the window cannot be built here.</summary>
    [Fact]
    public void Home_reads_the_cause_from_the_route_that_knows_it()
    {
        string src = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs"))
            .Replace("\r\n", "\n");
        // The status lines: no literal, the import still recognised as one.
        Assert.Contains("var powerOffCause = importing ? Radios.PowerOffStatus.Cause.ProfileImport : _powerOffCause;", src);
        Assert.Contains("WriteStatus(\"Power\", Radios.PowerOffStatus.PowerField(powerOffCause));", src);
        Assert.Contains("StatusText.Text = Radios.PowerOffStatus.StatusText(powerOffCause);", src);
        Assert.DoesNotContain("WriteStatus(\"Power\", \"Off\");", src);
        // Each caller sets its cause before the power-off runs.
        Assert.Contains("_powerOffCause = Radios.PowerOffStatus.Cause.StatusUnreadable;   // #564\n                PowerNowOffInternal();", src);
        Assert.Contains("_powerOffCause = Radios.PowerOffStatus.Cause.Restart;\n        PowerNowOffInternal();", src);
        // A field never set by a caller is the command loop's end.
        Assert.Equal(0, (int)PowerOffStatus.Cause.Disconnected);
    }
}
