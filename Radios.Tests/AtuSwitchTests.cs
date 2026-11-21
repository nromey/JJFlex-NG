using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// #1136, ruled by Noel 2026-11-18 (J3): the ATU switch is on for automatic
/// and off for bypassed (the manual type, for an outside tuner), and "none"
/// has left it. Until then the menu's ATU On/Off and the Fields panel's ATU box
/// read on as "not none" and wrote none for off, which the setter turns into
/// manual: after one "off" the switch still read on, and it could never turn
/// the tuner back on.
/// </summary>
/// <remarks>
/// The rule runs on the real FlexBase over the vendor radio fixture, reading
/// the commands that go out. The menu and the panel cannot be built without a
/// window, so what they call is read from their source.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class AtuSwitchTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(AtuSwitchTests));
    public void Dispose() => _scope.Dispose();

    private static int Sent(RadioCase c, string fragment) =>
        c.F.Vendor.Transport.Commands.Count(x => x.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static void Latch(RadioCase c, bool on) =>
        typeof(FlexBase).GetField("_FlexTunerOn", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(c.F.Rig, on);

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), Path.Combine(parts))).Replace("\r\n", "\n");

    [Theory]
    [InlineData(FlexBase.FlexTunerTypes.auto, true)]
    [InlineData(FlexBase.FlexTunerTypes.manual, false)]
    [InlineData(FlexBase.FlexTunerTypes.none, false)]
    public void The_switch_reads_on_only_for_automatic(FlexBase.FlexTunerTypes type, bool on) =>
        Assert.Equal(on, FlexBase.AtuSwitchReadsOn(type));

    [Fact]
    public void Off_bypasses_the_tuner_and_on_turns_it_back_on()
    {
        using var c = new RadioCase();
        Assert.Equal(FlexBase.FlexTunerTypes.manual, c.F.Rig.FlexTunerType);

        Assert.Null(c.F.Rig.SetAtuAutomatic(true));
        Assert.Equal(FlexBase.FlexTunerTypes.auto, c.F.Rig.FlexTunerType);
        Assert.True(FlexBase.AtuSwitchReadsOn(c.F.Rig.FlexTunerType));

        int bypassesBefore = Sent(c, "atu bypass");
        Assert.Null(c.F.Rig.SetAtuAutomatic(false));
        Assert.Equal(FlexBase.FlexTunerTypes.manual, c.F.Rig.FlexTunerType);
        Assert.False(FlexBase.AtuSwitchReadsOn(c.F.Rig.FlexTunerType));
        Assert.Equal(bypassesBefore + 1, Sent(c, "atu bypass"));

        // The defect: the switch must read off now, so its next press turns it ON.
        bool next = !FlexBase.AtuSwitchReadsOn(c.F.Rig.FlexTunerType);
        Assert.True(next);
        Assert.Null(c.F.Rig.SetAtuAutomatic(next));
        Assert.Equal(FlexBase.FlexTunerTypes.auto, c.F.Rig.FlexTunerType);
    }

    [Fact]
    public void A_change_the_latch_kept_back_is_refused_with_its_reason_and_nothing_is_sent()
    {
        using var c = new RadioCase();
        Assert.Null(c.F.Rig.SetAtuAutomatic(true));
        Latch(c, true);
        int bypassesBefore = Sent(c, "atu bypass");

        string? refusal = c.F.Rig.SetAtuAutomatic(false);

        Assert.Equal(Lexicon.Get("settings.atu.refused_while_tuning"), refusal);
        Assert.Equal(FlexBase.FlexTunerTypes.auto, c.F.Rig.FlexTunerType);
        Assert.Equal(bypassesBefore, Sent(c, "atu bypass"));

        // Positive control: with the latch down the same change is made.
        Latch(c, false);
        Assert.Null(c.F.Rig.SetAtuAutomatic(false));
        Assert.Equal(FlexBase.FlexTunerTypes.manual, c.F.Rig.FlexTunerType);
    }

    [Fact]
    public void Asking_for_what_is_already_true_is_not_a_refusal()
    {
        using var c = new RadioCase();
        Latch(c, true);
        Assert.Null(c.F.Rig.SetAtuAutomatic(false));   // manual already
    }

    [Fact]
    public void The_menu_and_the_fields_panel_use_the_rule_and_offer_no_none()
    {
        string menu = Source("JJFlexWpf", "NativeMenuBar.cs");
        int at = menu.IndexOf("private void BuildATUItems(IntPtr parent)", StringComparison.Ordinal);
        string items = menu.Substring(at, menu.IndexOf("AddWired(parent, \"ATU Memories\"", at, StringComparison.Ordinal) - at);
        Assert.DoesNotContain("FlexTunerTypes.none", items);
        Assert.DoesNotContain("Rig.FlexTunerType =", items);
        Assert.Equal(2, CountOf(items, "Rig.SetAtuAutomatic("));
        Assert.Contains("() => Rig != null && FlexBase.AtuSwitchReadsOn(Rig.FlexTunerType));", items);
        Assert.DoesNotContain("\"settings.atu.mode\"", items);

        string panel = Source("JJFlexWpf", "Controls", "ScreenFieldsPanel.xaml.cs");
        int build = panel.IndexOf("private void BuildAntennaControls()", StringComparison.Ordinal);
        string antenna = panel.Substring(build, panel.IndexOf("#endregion", build, StringComparison.Ordinal) - build);
        Assert.DoesNotContain("FlexTunerTypes.none", antenna);
        Assert.DoesNotContain("atu_mode_none", antenna);
        Assert.DoesNotContain("_rig.FlexTunerType =", antenna);
        Assert.Contains("_rig.SetAtuAutomatic(automatic)", antenna);
        int mode = antenna.IndexOf("_atuModeControl.SelectionChanged += (s, idx) =>", StringComparison.Ordinal);
        Assert.Contains("if (_polling || _rig == null) return;", SourceMember.CSharpAt(antenna, mode));

        int poll = panel.IndexOf("private void PollAntenna()", StringComparison.Ordinal);
        string polled = panel.Substring(poll, panel.IndexOf("#endregion", poll, StringComparison.Ordinal) - poll);
        Assert.DoesNotContain("FlexTunerTypes.none", polled);
        Assert.Contains("FlexBase.AtuSwitchReadsOn(_rig.FlexTunerType)", polled);
    }

    private static int CountOf(string text, string fragment)
    {
        int n = 0, at = 0;
        while ((at = text.IndexOf(fragment, at, StringComparison.Ordinal)) >= 0) { n++; at += fragment.Length; }
        return n;
    }
}
