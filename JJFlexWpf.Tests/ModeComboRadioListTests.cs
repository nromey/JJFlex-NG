using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #1017: the classic Mode combo offers the radio's own mode list, as every
/// other mode surface does, re-reads it, and refuses a mode the radio has not
/// listed. It read our fixed <c>RigCaps.ModeTable</c>, which names modes a
/// radio may not have (the 8600 lists no DSB).
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> Static methods of <see cref="MainWindow"/> and a read
/// of its source only: no window or element is built, and every test asserts
/// it runs on an MTA thread, where WPF cannot build one.
/// </remarks>
public sealed class ModeComboRadioListTests
{
    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    private static readonly string[] Radio8600 = { "LSB", "USB", "AM", "CW", "DIGL", "DIGU", "SAM", "FM", "NFM", "DFM", "RTTY" };

    [Fact]
    public void The_combo_offers_the_radios_own_modes_in_its_order()
    {
        AssertWindowless();
        var items = MainWindow.ModeComboItems(Radio8600);
        Assert.Equal(Radio8600, items.Cast<string>());
        Assert.DoesNotContain("DSB", items.Cast<string>());
    }

    [Fact]
    public void Before_the_radio_reports_a_list_it_offers_flexlibs_documented_set()
    {
        AssertWindowless();
        Assert.Equal(ModeCycle.KnownModes(System.Array.Empty<string>()), MainWindow.ModeComboItems(System.Array.Empty<string>()).Cast<string>());
    }

    [Fact]
    public void The_list_is_replaced_only_when_the_radios_list_differs()
    {
        AssertWindowless();
        var shown = MainWindow.ModeComboItems(System.Array.Empty<string>());   // the fallback, filled at connect
        var fresh = MainWindow.ModeComboRefresh(shown, Radio8600);           // the radio's list arrived
        Assert.NotNull(fresh);
        Assert.Equal(Radio8600, fresh!.Cast<string>());
        Assert.Null(MainWindow.ModeComboRefresh(fresh, Radio8600));            // unchanged: left alone
        var reordered = new[] { "USB", "LSB", "AM", "CW", "DIGL", "DIGU", "SAM", "FM", "NFM", "DFM", "RTTY" };
        Assert.NotNull(MainWindow.ModeComboRefresh(fresh, reordered));         // a new order is a new list
        Assert.NotNull(MainWindow.ModeComboRefresh(null, Radio8600));
    }

    [Fact]
    public void A_mode_the_radio_does_not_list_is_refused()
    {
        AssertWindowless();
        Assert.False(MainWindow.ModeComboWriteAllowed("DSB", Radio8600));
        Assert.False(MainWindow.ModeComboWriteAllowed(null, Radio8600));
        Assert.True(MainWindow.ModeComboWriteAllowed("USB", Radio8600));
        Assert.True(MainWindow.ModeComboWriteAllowed("usb", Radio8600));
    }

    [Fact]
    public void SetupBoxes_fills_from_the_radio_and_guards_the_write()
    {
        AssertWindowless();
        string src = File.ReadAllText(Path.Combine(Root(), "JJFlexWpf", "MainWindow.xaml.cs")).Replace("\r\n", "\n");
        var m = Regex.Match(src, @"private void SetupBoxes\(\)\n    \{(?<body>[\s\S]*?)\n    \}\n");
        Assert.True(m.Success, "SetupBoxes is not where this test reads it");
        string body = m.Groups["body"].Value;
        Assert.DoesNotContain("RigCaps.ModeTable", body);
        Assert.Contains("ModeControl.TheList = ModeComboItems(RigControl.RadioModeList);", body);
        Assert.Contains("RefreshModeComboList();", body);
        // The guard is set after the write it wraps.
        Assert.True(body.IndexOf("GuardModeComboWrites();", System.StringComparison.Ordinal)
                    > body.IndexOf("ModeControl.UpdateRigFunction =", System.StringComparison.Ordinal),
            "GuardModeComboWrites must run after SetupBoxes sets the write it wraps");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "repository root not found");
        return dir!.FullName;
    }
}
