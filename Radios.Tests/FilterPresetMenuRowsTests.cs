using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #960, batch 14 repair round 2 (Codex's F1): the Filter menu's preset rows
/// are built for one mode's list, and their native check read the live edges
/// in that BUILD mode. After USB to LSB no row was checked (LSB's negative
/// edges read as USB match nothing), and after USB to DIGU the rows were the
/// SSB list while the filter was a DIGI preset. Now the menu rebuilds on a
/// mode change (the rows follow the mode) and a row's check reads the live
/// edges in the live mode, and only while the live mode uses the rows' list
/// (<see cref="FilterPresets.RowIsActive"/>).
/// </summary>
/// <remarks>
/// The rows and checks are computed here as the menu computes them: the rows
/// are <see cref="FilterPresets.GetPresetsForMode"/> of the mode the menu was
/// built in, and row i is checked by RowIsActive(built mode, live mode, live
/// edges, i). The menu's own wiring is read from source.
/// </remarks>
public sealed class FilterPresetMenuRowsTests
{
    private static readonly FilterPresets Presets = new();

    /// <summary>The rows a menu built in <paramref name="builtMode"/> shows, and which are checked for the live filter.</summary>
    private static (string Label, bool Checked)[] Menu(string builtMode, string liveMode, int liveLow, int liveHigh) =>
        Presets.GetPresetsForMode(builtMode)
            .Select((p, i) => (p.FormatForDisplay(), Presets.RowIsActive(builtMode, liveMode, liveLow, liveHigh, i)))
            .ToArray();

    private static int OnlyChecked((string Label, bool Checked)[] rows)
    {
        var on = rows.Select((r, i) => (r, i)).Where(x => x.r.Checked).Select(x => x.i).ToArray();
        Assert.True(on.Length == 1, "expected exactly one checked row, got " + on.Length);
        return on[0];
    }

    [Fact]
    public void In_USB_the_matching_preset_is_the_one_checked_row()
    {
        var ssb = Presets.GetPresetsForMode("USB");
        int wide = ssb.Count - 1;
        Assert.Equal(wide, OnlyChecked(Menu("USB", "USB", ssb[wide].Low, ssb[wide].High)));
    }

    [Fact]
    public void After_a_settled_USB_to_LSB_change_the_same_rows_show_and_the_matching_preset_is_checked()
    {
        var ssb = Presets.GetPresetsForMode("USB");
        int wide = ssb.Count - 1;
        // LSB reports the mirrored edges: -2800 to -100 for a 100 to 2800 preset.
        var rows = Menu("USB", "LSB", -ssb[wide].High, -ssb[wide].Low);
        Assert.Equal(Menu("LSB", "LSB", -ssb[wide].High, -ssb[wide].Low).Select(r => r.Label), rows.Select(r => r.Label));
        Assert.Equal(wide, OnlyChecked(rows));

        // The defect, as a control: reading LSB's edges in the build mode finds nothing.
        Assert.Equal(-1, Presets.FindActivePreset("USB", -ssb[wide].High, -ssb[wide].Low));
    }

    [Fact]
    public void After_USB_to_DIGU_the_rebuilt_rows_are_the_DIGI_list_with_the_DIGI_preset_checked()
    {
        var ssb = Presets.GetPresetsForMode("USB");
        var digi = Presets.GetPresetsForMode("DIGU");
        Assert.NotEqual(ssb.Select(p => p.FormatForDisplay()), digi.Select(p => p.FormatForDisplay()));
        int pick = digi.Count / 2;
        var (low, high) = (digi[pick].Low, digi[pick].High);

        // Before the rebuild runs, the SSB rows check nothing: they are not this mode's list.
        Assert.DoesNotContain(Menu("USB", "DIGU", low, high), r => r.Checked);

        // Rebuilt in DIGU: the DIGI rows, and the matching DIGI preset checked.
        var rebuilt = Menu("DIGU", "DIGU", low, high);
        Assert.Equal(digi.Select(p => p.FormatForDisplay()), rebuilt.Select(r => r.Label));
        Assert.Equal(pick, OnlyChecked(rebuilt));
    }

    [Fact]
    public void A_filter_that_matches_no_preset_checks_no_row()
    {
        Assert.DoesNotContain(Menu("USB", "USB", 137, 1913), r => r.Checked);
    }

    [Fact]
    public void The_menu_rebuilds_on_a_mode_change_and_checks_rows_in_the_live_mode()
    {
        string menu = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "NativeMenuBar.cs"));
        Assert.Contains("rig.ModeChanged += OnModeChanged;", menu);
        Assert.Equal(2, menu.Split("_subscribedRig.ModeChanged -= OnModeChanged;").Length - 1);   // resubscribe and Dispose
        int at = menu.IndexOf("private void OnModeChanged(string mode)", StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.Contains("QueueMenuRebuild(", SourceMember.CSharpAt(menu, at));
        Assert.Contains("FilterPresets.RowIsActive(mode, Rig.Mode, Rig.FilterLow, Rig.FilterHigh, index)", menu);
    }
}
