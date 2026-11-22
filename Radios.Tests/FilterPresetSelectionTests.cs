using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #960, batch 14 repair round 3 (Codex's F1 on c543d024): selecting another
/// EXISTING slice in another preset group raised no ModeChanged, so the Filter
/// menu kept the SSB rows over a DIGU slice, checked nothing, and a click on a
/// stale row applied the SSB preset in DIGU. Now the rig raises
/// <see cref="FlexBase.ActiveSliceChanged"/> on our slice's selection, the menu
/// rebuilds on it, and a stale row refuses.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class FilterPresetSelectionTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(FilterPresetSelectionTests));
    public void Dispose() => _scope.Dispose();

    private static void WireHandler(ListenbackRoundTwoTests.RadioCase c)
    {
        var handler = typeof(FlexBase).GetMethod("radioPropertyChangedHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
        c.F.Vendor.Radio.PropertyChanged += (s, e) => handler.Invoke(c.F.Rig, new object[] { s!, e, c.F.Binding });
    }

    /// <summary>
    /// Two existing slices of ours, USB (slice 1, selected) and DIGU (slice
    /// 2), each with its panadapter, in the radio's own list. The selection
    /// moves through the radio's own status lines, as the radio reports a
    /// selection from Slice, Selection or JumpToSlice: the rig raises
    /// ActiveSliceChanged for it (what the Filter menu rebuilds on), and none
    /// of it is a ModeChanged, which is why the mode event alone was not enough.
    /// </summary>
    [Fact]
    public void Selecting_an_existing_slice_in_another_group_raises_the_selection_event_and_no_mode_change()
    {
        using var c = new ListenbackRoundTwoTests.RadioCase();
        var digu = c.Add(2, 14.074);
        typeof(Flex.Smoothlake.FlexLib.Slice).GetField("_demodMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(digu, "DIGU");
        // The filter adapter a connect builds; the ActiveSlice handler tells it the new slice.
        typeof(FlexBase).GetField("FilterObj", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(c.F.Rig, new WpfFilterAdapter(c.F.Rig));
        WireHandler(c);
        int selections = 0, modeChanges = 0;
        c.F.Rig.ActiveSliceChanged += () => selections++;
        c.F.Rig.ModeChanged += _ => modeChanges++;

        c.F.Vendor.Status("slice 1 active=0");
        c.F.Vendor.Status("slice 2 active=1");
        Assert.True(selections >= 1, "selecting the DIGU slice raises the selection event the menu rebuilds on");
        Assert.Equal("DIGU", c.F.Rig.Mode);
        Assert.Equal(0, modeChanges);
        Assert.False(FilterPresets.SameList("USB", c.F.Rig.Mode), "the USB rows are not the DIGU slice's list");

        // And back: another selection, another event.
        int before = selections;
        c.F.Vendor.Status("slice 2 active=0");
        c.F.Vendor.Status("slice 1 active=1");
        Assert.True(selections > before);
        Assert.Equal("USB", c.F.Rig.Mode);
    }

    [Theory]
    [InlineData("USB", "LSB", true)]
    [InlineData("DIGU", "DIGL", true)]
    [InlineData("USB", "DIGU", false)]
    [InlineData("CW", "USB", false)]
    public void Two_modes_share_a_preset_list_only_within_a_group(string built, string live, bool same) =>
        Assert.Equal(same, FilterPresets.SameList(built, live));

    [Fact]
    public void The_menu_rebuilds_on_a_selection_and_a_stale_row_refuses_before_it_applies()
    {
        string menu = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "NativeMenuBar.cs")).Replace("\r\n", "\n");
        Assert.Contains("rig.ActiveSliceChanged += OnActiveSliceChanged;", menu);
        Assert.Equal(2, menu.Split("_subscribedRig.ActiveSliceChanged -= OnActiveSliceChanged;").Length - 1);
        int handler = menu.IndexOf("private void OnActiveSliceChanged()", StringComparison.Ordinal);
        Assert.True(handler > 0);
        Assert.Contains("QueueMenuRebuild(", SourceMember.CSharpAt(menu, handler));

        int refuse = menu.IndexOf("if (!Radios.FilterPresets.SameList(mode, currentMode))", StringComparison.Ordinal);
        int apply = menu.IndexOf("rig.ApplyFilterPreset(preset, currentMode,", StringComparison.Ordinal);
        Assert.True(refuse > 0 && refuse < apply, "a stale row refuses before it can apply");
        string branch = menu.Substring(refuse, apply - refuse);
        Assert.Contains("\"audio.filter.preset_list_changed\"", branch);
        Assert.Contains("return;", branch);
        Assert.False(string.IsNullOrWhiteSpace(Lexicon.Get("audio.filter.preset_list_changed")));

        string flex = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "Radios", "FlexBase.cs"));
        int active = flex.IndexOf("case \"ActiveSlice\":", StringComparison.Ordinal);
        Assert.True(flex.IndexOf("RaiseActiveSliceChanged();", active, StringComparison.Ordinal) < flex.IndexOf("case \"", active + 20, StringComparison.Ordinal));
    }
}
