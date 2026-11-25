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
[Collection(RadioConfigStaticsCollection.Name)]
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
        // Repair round 1: the catch also covers Home's own display work, so it
        // establishes only that the status is unavailable, not that the radio
        // stopped answering.
        Assert.DoesNotContain("not responding", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Repair round 1: the cause across one connection's notices ──

    private sealed class Rig { }

    /// <summary>
    /// A confirmed LOCAL restart raises two power-off notices on one
    /// connection: the restart route's own, then the command loop's end when
    /// the restart's disconnect stops it. Both must keep the power-off line;
    /// the one-shot cause lost it at the second, leaving "Radio disconnected".
    /// </summary>
    [Fact]
    public void A_local_restart_keeps_the_power_off_line_through_its_second_notice()
    {
        var latch = new PowerOffStatus.CauseLatch();
        var rig = new Rig();
        latch.RestartConfirmed(rig);
        var first = latch.Take(rig, importing: false);
        var second = latch.Take(rig, importing: false);
        Assert.Equal(PowerOffStatus.Cause.Restart, first);
        Assert.Equal(PowerOffStatus.Cause.Restart, second);
        Assert.Equal(PowerOff, PowerOffStatus.StatusText(second));
        Assert.Equal("Off", PowerOffStatus.PowerField(second));
    }

    /// <summary>
    /// The restart is held for its own connection only: after that teardown
    /// completes (or the radio comes back on), and on any other connection, a
    /// later disconnect says disconnected. Goes red if the boundary stops
    /// clearing the restart, or the rig check is dropped.
    /// </summary>
    [Fact]
    public void A_later_unrelated_disconnect_after_a_restart_says_disconnected()
    {
        var latch = new PowerOffStatus.CauseLatch();
        var rig = new Rig();
        latch.RestartConfirmed(rig);
        latch.Take(rig, importing: false);
        latch.Take(rig, importing: false);
        latch.Boundary();
        var later = latch.Take(rig, importing: false);
        Assert.Equal(PowerOffStatus.Cause.Disconnected, later);
        Assert.Equal(Lexicon.Get("connect.home.status_disconnected"), PowerOffStatus.StatusText(later));

        // A different connection never inherits a restart, even before any boundary.
        var restarted = new Rig();
        latch.RestartConfirmed(restarted);
        Assert.Equal(PowerOffStatus.Cause.Disconnected, latch.Take(new Rig(), importing: false));
        Assert.Equal(PowerOffStatus.Cause.Disconnected, latch.Take(null, importing: false));
        Assert.Equal(PowerOffStatus.Cause.Restart, latch.Take(restarted, importing: false));
    }

    /// <summary>
    /// A cause the status update sets is used once: the next notice from the
    /// same connection (the command loop ending on a later disconnect) says
    /// disconnected, not "status unavailable" again. Goes red if the one-shot
    /// is not reset when taken.
    /// </summary>
    [Fact]
    public void A_failed_status_update_labels_only_its_own_notice()
    {
        var latch = new PowerOffStatus.CauseLatch();
        var rig = new Rig();
        latch.Once(PowerOffStatus.Cause.StatusUnreadable);
        Assert.Equal(PowerOffStatus.Cause.StatusUnreadable, latch.Take(rig, importing: false));
        Assert.Equal(PowerOffStatus.Cause.Disconnected, latch.Take(rig, importing: false));
    }

    /// <summary>A profile import's notice says so whatever is pending, and uses
    /// up a pending one-shot, as the field did before the latch.</summary>
    [Fact]
    public void A_profile_import_says_so_and_uses_up_a_pending_cause()
    {
        var latch = new PowerOffStatus.CauseLatch();
        var rig = new Rig();
        latch.Once(PowerOffStatus.Cause.StatusUnreadable);
        Assert.Equal(PowerOffStatus.Cause.ProfileImport, latch.Take(rig, importing: true));
        Assert.Equal(PowerOffStatus.Cause.Disconnected, latch.Take(rig, importing: false));
    }

    /// <summary>SOURCE PINS: the window cannot be built here.</summary>
    [Fact]
    public void Home_reads_the_cause_from_the_route_that_knows_it()
    {
        string src = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs"))
            .Replace("\r\n", "\n");
        // The status lines: no literal, the import still recognised as one.
        Assert.Contains("var powerOffCause = _powerOffCauses.Take(RigControl, importing);", src);
        Assert.Contains("WriteStatus(\"Power\", Radios.PowerOffStatus.PowerField(powerOffCause));", src);
        Assert.Contains("StatusText.Text = Radios.PowerOffStatus.StatusText(powerOffCause);", src);
        Assert.DoesNotContain("WriteStatus(\"Power\", \"Off\");", src);
        // Each caller sets its cause before the power-off runs.
        Assert.Contains("_powerOffCauses.Once(Radios.PowerOffStatus.Cause.StatusUnreadable);   // #564\n                PowerNowOffInternal();", src);
        Assert.Contains("_powerOffCauses.RestartConfirmed(RigControl);\n        PowerNowOffInternal();", src);
        // The two boundaries that end a held restart: the radio back on, and
        // the connection's teardown complete (after PowerStatus is unwired).
        Assert.Contains("_radioPowerOn = true;\n        _powerOffCauses.Boundary();", src);
        int unwire = src.IndexOf("public void UnwireRadioEvents()", StringComparison.Ordinal);
        int unsub = src.IndexOf("RigControl.PowerStatus -= PowerStatusHandler;", unwire, StringComparison.Ordinal);
        int boundary = src.IndexOf("_powerOffCauses.Boundary();", unwire, StringComparison.Ordinal);
        int end = src.IndexOf("StatusText.Text = Radios.Lexicon.Get(\"connect.home.status_ready_no_radio\");", unwire, StringComparison.Ordinal);
        Assert.True(unwire >= 0 && unsub > unwire && boundary > unsub && boundary < end,
            "UnwireRadioEvents must end a held restart once it has stopped listening for power notices");
    }
}
