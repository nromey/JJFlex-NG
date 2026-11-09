using System;
using System.IO;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #895, ruled by Noel 2026-11-08 ~16:23: connecting to a DIFFERENT radio
/// closes every Home field group; reconnecting to the SAME radio after a drop
/// keeps them as they were.
/// </summary>
/// <remarks>
/// <b>Windowless.</b> A static decision on <see cref="MainWindow"/> and source
/// reads; no window or panel is built. Asserts an MTA thread. Example serials
/// only. Connecting to two radios in turn is the harness's.
/// </remarks>
public sealed class FieldGroupsNewRadioTests
{
    private const string RadioA = "1111-2222-3333-4444";
    private const string RadioB = "5555-6666-7777-8888";

    [Fact]
    public void A_different_radio_closes_the_groups()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(MainWindow.ClosesFieldGroupsOnConnect(RadioA, RadioB));
    }

    [Fact]
    public void The_same_radio_the_first_connect_and_an_unknown_serial_keep_them()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.False(MainWindow.ClosesFieldGroupsOnConnect(RadioA, RadioA));
        Assert.False(MainWindow.ClosesFieldGroupsOnConnect(RadioA, RadioA.ToLowerInvariant()));
        Assert.False(MainWindow.ClosesFieldGroupsOnConnect("", RadioA));
        Assert.False(MainWindow.ClosesFieldGroupsOnConnect(null, RadioA));
        Assert.False(MainWindow.ClosesFieldGroupsOnConnect(RadioA, ""));
    }

    private static string Source(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(path))).Replace("\r\n", "\n");
    }

    /// <summary>
    /// The groups close BEFORE the panel is wired to the new radio, and the
    /// serial is remembered only when known, so an unknown one cannot wipe
    /// the memory of the last real radio.
    /// </summary>
    [Fact]
    public void The_close_comes_before_the_panel_meets_the_new_radio()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
        int decide = main.IndexOf("if (ClosesFieldGroupsOnConnect(_lastConnectedSerial, connectedSerial))", StringComparison.Ordinal);
        int close = main.IndexOf("FieldsPanel.CloseAllGroupsForNewRadio();", StringComparison.Ordinal);
        int remember = main.IndexOf("if (connectedSerial.Length > 0) _lastConnectedSerial = connectedSerial;", StringComparison.Ordinal);
        int init = main.IndexOf("FieldsPanel.Initialize(RigControl);", StringComparison.Ordinal);
        Assert.True(decide > 0 && decide < close && close < remember && remember < init);

        string panel = Source("JJFlexWpf", "Controls", "ScreenFieldsPanel.xaml.cs");
        int at = panel.IndexOf("public void CloseAllGroupsForNewRadio()", StringComparison.Ordinal);
        Assert.True(at > 0);
        string body = panel.Substring(at);
        // Quiet and focus untouched: no gavel, no sentence, no trip home.
        Assert.Contains("_suppressCollapseEarcons = true;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayCollapseAll", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Speak(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("EscapePressed", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Focus", body, StringComparison.Ordinal);
    }
}
