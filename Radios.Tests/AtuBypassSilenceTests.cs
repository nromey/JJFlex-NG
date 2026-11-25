using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// #1221, ruled by Noel 2026-11-23 17:10: a frequency the ATU has no memory
/// for, where the radio bypasses the ATU on its own, says nothing. A memory
/// hit whose stored solution is bypass is silent too, for now (#1223's
/// "Tuned from memory" will cover it). A tune press whose cycle ends in a
/// bypass is still said (#1188, J8), and the operator's own bypass is said by
/// the route that asked for it.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class AtuBypassSilenceTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(AtuBypassSilenceTests));
    public void Dispose() => _scope.Dispose();

    private static string Bypassed => Lexicon.Get("audio.tune.atu_bypassed");

    [Theory]
    // The radio's own bypass on a frequency change: a memory miss
    // (TUNE_MANUAL_BYPASS, using_mem=0) and a memory hit whose solution is
    // bypass (TUNE_BYPASS, using_mem=1). No cycle of ours: silent.
    [InlineData("ManualBypass", true, false, false)]
    [InlineData("Bypass", true, false, false)]
    // A tune press whose ATU cycle ends in a bypass: said, as a result.
    [InlineData("Bypass", true, true, true)]
    [InlineData("ManualBypass", true, true, true)]
    // Out of automatic (the operator's own bypass arrives here): never said
    // here; its own route says it.
    [InlineData("ManualBypass", false, false, false)]
    [InlineData("ManualBypass", false, true, false)]
    [InlineData("Bypass", false, true, false)]
    // Not a bypass status: not this rule's to say.
    [InlineData("OK", true, true, false)]
    [InlineData("Fail", true, true, false)]
    [InlineData("InProgress", true, false, false)]
    public void Only_a_bypass_that_ends_a_tune_cycle_of_ours_is_said(string status, bool auto, bool cycle, bool said)
    {
        string? sentence = FlexBase.AtuBypassStatusSentence(status, auto, cycle);
        if (said) Assert.Equal(Bypassed, sentence);
        else Assert.Null(sentence);
    }

    /// <summary>
    /// The operator's own bypass, through the ATU switch: the setter moves the
    /// type out of automatic BEFORE it sends the bypass, so the radio's
    /// TUNE_MANUAL_BYPASS reaches the handler out of automatic and this rule
    /// stays out of it. Its confirmation is its own route's (next test).
    /// </summary>
    [Fact]
    public void The_operators_own_bypass_reaches_the_handler_out_of_automatic()
    {
        using var c = new RadioCase();
        typeof(FlexBase).GetField("_FlexTunerType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, FlexBase.FlexTunerTypes.auto);
        var radio = c.F.Vendor.Radio;
        var binding = new StationConnect.ObservationBindingFor(radio).Binding;
        var handler = typeof(FlexBase).GetMethod("radioPropertyChangedHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
        radio.PropertyChanged += (s, e) => handler.Invoke(c.F.Rig, new object[] { s!, e, binding });
        var raised = new List<FlexBase.FlexAntTunerArg>();
        c.F.Rig.FlexAntTunerStartStop += a => raised.Add(a);

        // Positive control: in automatic, the radio's own bypass arrives as automatic.
        c.F.Vendor.Status("atu status=TUNE_MANUAL_BYPASS");
        Assert.Contains(raised, a => a.Status == "ManualBypass" && a.Type == "auto");
        raised.Clear();
        c.F.Vendor.Status("atu status=TUNE_SUCCESSFUL");
        raised.Clear();

        Assert.Null(c.F.Rig.SetAtuAutomatic(false));
        c.F.Vendor.Status("atu status=TUNE_MANUAL_BYPASS");

        var arg = Assert.Single(raised, a => a.Status == "ManualBypass");
        Assert.Equal(nameof(FlexBase.FlexTunerTypes.manual), arg.Type);
    }

    [Fact]
    public void The_operators_own_bypass_is_said_by_the_route_that_asked()
    {
        // SOURCE PINS: every route to SetAtuAutomatic says what it did.
        string menu = Source("JJFlexWpf", "NativeMenuBar.cs");
        Assert.Contains("\"settings.atu.mode_bypassed\"", menu);
        Assert.Contains("Radios.Lexicon.Get(\"settings.atu.turned_off\")", menu);
        string panel = Source("JJFlexWpf", "Controls", "ScreenFieldsPanel.xaml.cs");
        Assert.Contains("if (SetAtu(true) == null) ToggleFeedback.Announce(Lexicon.Get(\"audio.fields.atu\"), true);", panel);
        Assert.Contains("if (SetAtu(false) == null) ToggleFeedback.Announce(Lexicon.Get(\"audio.fields.atu\"), false);", panel);
    }

    [Fact]
    public void The_handler_reads_its_cycle_before_ending_it_and_asks_the_rule()
    {
        string src = Source("JJFlexWpf", "MainWindow.xaml.cs");
        int read = src.IndexOf("bool ourCycleRunning = _atuTuneTimer != null;", StringComparison.Ordinal);
        int ask = src.IndexOf("FlexBase.AtuBypassStatusSentence(e.Status, isAutoATU, ourCycleRunning)", StringComparison.Ordinal);
        Assert.True(read > 0, "the handler reads whether a cycle of ours is running");
        Assert.True(ask > read, "and asks the rule after");
        int end = src.IndexOf("EndAtuCycle(AtuCycleEnd.Result);", read, StringComparison.Ordinal);
        Assert.True(end > read && end < ask, "the read comes before EndAtuCycle stops the cycle's timer");
        Assert.DoesNotContain("if (isAutoATU)\n                    Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get(\"audio.tune.atu_bypassed\")", src);
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), Path.Combine(parts))).Replace("\r\n", "\n");
}
