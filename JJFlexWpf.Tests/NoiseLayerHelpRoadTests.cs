using System;
using System.Linq;
using System.Windows.Forms;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The noise layer's H, down the road an operator's key takes (#741):
/// Ctrl+J, N opens the layer, H goes to the value-layer engine, the engine
/// asks the host's ListCommands, the host hands the layer's gating to
/// <see cref="KeyLayerHelp.Present(string, KeyLayerHelp.LayerGating?)"/>, and
/// Present hands its model to the window. These tests stop at that last
/// hand-off, through <see cref="KeyLayerHelp.PresentationSink"/>, so nothing
/// here builds a window.
/// </summary>
/// <remarks>
/// <para>Heard 2026-10-17: the window was titled "Noise layer, 15 keys" with
/// R, S and Ctrl+N as ordinary rows and no closing sentence, on a radio where
/// all three were refused. The spoken form's gating was unit-tested and
/// correct; nothing tested the road H takes, because the window was built
/// from the ungated rows and the gated spoken form ran only when the list
/// was refused, which on this layer is never.</para>
/// <para>The radio's answer for the three gated switches is supplied through
/// <see cref="KeyCommands.NoiseLayerGateOverride"/>, because a test cannot
/// connect one. Every other step is the shipped code.</para>
/// </remarks>
public sealed class NoiseLayerHelpRoadTests : IDisposable
{
    private readonly FlexBase _rig;
    private readonly VerbosityLevel _verbosityBefore;
    private KeyLayerHelp.HelpPresentation? _presented;
    private int _presentations;

    public NoiseLayerHelpRoadTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        KeyLayerHelp.PresentationSink = model => { _presented = model; _presentations++; };
    }

    public void Dispose()
    {
        KeyLayerHelp.PresentationSink = null;
        KeyCommands.NoiseLayerGateOverride = null;
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        _rig.Dispose();
    }

    private static void AssertTheDeskIsSafe()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    private KeyCommands EnterTheNoiseLayer()
    {
        AssertTheDeskIsSafe();
        var context = new KeyCommandContext
        {
            GetRigControl = () => _rig,
            GetPower = () => true,
        };
        var commands = new KeyCommands(context, setDefault: true);
        commands.EnterNoiseLayer();
        Assert.NotNull(commands.LiveValueLayer);
        return commands;
    }

    private KeyLayerHelp.HelpPresentation PressH(KeyCommands commands)
    {
        int before = _presentations;
        Assert.True(commands.DoCommand(Keys.H));
        Assert.Equal(before + 1, _presentations);
        Assert.NotNull(_presented);
        return _presented!;
    }

    private KeyLayerHelp.HelpPresentation PressHInTheNoiseLayer() => PressH(EnterTheNoiseLayer());

    private static CapabilityBlock ShutTheThree(RadioCapability capability) =>
        capability == RadioCapability.NoiseReductionNeural
        || capability == RadioCapability.NoiseReductionSpectral
        || capability == RadioCapability.NoiseReductionFilter
            ? CapabilityBlock.Hardware
            : CapabilityBlock.None;

    /// <summary>
    /// Repair round 1 (Codex follow-up): the gating is read at the PRESS,
    /// not cached when the layer opened. The layer opens on a radio where
    /// nothing is shut; the radio's answer changes while it is open; H then
    /// lists what is true now.
    /// </summary>
    [Fact]
    public void H_reads_the_gating_at_the_press_not_when_the_layer_opened()
    {
        KeyCommands.NoiseLayerGateOverride = _ => CapabilityBlock.None;
        var commands = EnterTheNoiseLayer();
        int all = KeyLayerHelp.Rows(KeyInventory.NoiseLayerContext).Count;
        Assert.Equal(all, PressH(commands).Rows.Count);

        KeyCommands.NoiseLayerGateOverride = ShutTheThree;
        var model = PressH(commands);

        Assert.Equal(all - 3, model.Rows.Count);
        Assert.NotNull(model.ClosingLine);
    }

    private static readonly string[] GatedKeys = { "R", "S", "Ctrl+N" };

    [Fact]
    public void H_on_a_radio_where_the_three_are_shut_lists_only_what_it_can_use_and_names_the_rest()
    {
        KeyCommands.NoiseLayerGateOverride = ShutTheThree;

        var model = PressHInTheNoiseLayer();

        int all = KeyLayerHelp.Rows(KeyInventory.NoiseLayerContext).Count;
        Assert.Equal(KeyLayerHelp.HelpSurface.List, model.Surface);
        Assert.Equal(all - 3, model.Rows.Count);
        Assert.Equal(KeyLayerHelp.ListTitle(KeyInventory.NoiseLayerContext, all - 3), model.Title);
        foreach (string gated in GatedKeys)
            Assert.DoesNotContain(model.Rows, r => r.key == gated);

        Assert.Equal(
            Lexicon.Get("audio.noise_layer.gated_keys_unavailable", ("keys", "R, S and Ctrl+N")),
            model.ClosingLine);
    }

    /// <summary>
    /// The control: the same road on a radio where nothing is shut gives
    /// every row and no sentence, so the test above is measuring the gating
    /// and not something the harness does to every list.
    /// </summary>
    [Fact]
    public void H_on_a_radio_where_nothing_is_shut_lists_every_key_and_no_sentence()
    {
        KeyCommands.NoiseLayerGateOverride = _ => CapabilityBlock.None;

        var model = PressHInTheNoiseLayer();

        int all = KeyLayerHelp.Rows(KeyInventory.NoiseLayerContext).Count;
        Assert.Equal(KeyLayerHelp.HelpSurface.List, model.Surface);
        Assert.Equal(all, model.Rows.Count);
        Assert.Equal(KeyLayerHelp.ListTitle(KeyInventory.NoiseLayerContext, all), model.Title);
        foreach (string gated in GatedKeys)
            Assert.Contains(model.Rows, r => r.key == gated);
        Assert.Null(model.ClosingLine);
    }

    /// <summary>
    /// The window and the spoken form must not disagree about which keys are
    /// shut: the spoken form is the one the engine falls back to, and its
    /// closing sentence is the window's last row.
    /// </summary>
    [Fact]
    public void The_window_and_the_spoken_form_name_the_same_shut_keys()
    {
        AssertTheDeskIsSafe();
        var gating = new KeyLayerHelp.LayerGating(
            new[] { "R", "S" }.ToHashSet(StringComparer.OrdinalIgnoreCase),
            "R and S are not available on this radio.");

        var model = KeyLayerHelp.Prepare(KeyInventory.NoiseLayerContext, gating);
        string spoken = KeyLayerHelp.SpokenList(KeyInventory.NoiseLayerContext, gating.UnavailableKeys, gating.ClosingSentence);

        Assert.Equal(gating.ClosingSentence, model.ClosingLine);
        Assert.EndsWith(gating.ClosingSentence, spoken);
        Assert.StartsWith(model.Rows.Count.ToString(), spoken);

        // Membership, not just the count (repair round 1): every row the
        // window keeps is recited, and every lifted row is recited nowhere
        // before the closing sentence.
        string recitation = spoken.Substring(0, spoken.Length - gating.ClosingSentence.Length);
        foreach (var (key, description) in model.Rows)
            Assert.Contains(Lexicon.Get("leader.help.row", ("key", key), ("description", description)), recitation);
        foreach (var (key, description) in KeyLayerHelp.Rows(KeyInventory.NoiseLayerContext))
        {
            if (!gating.UnavailableKeys.Contains(key)) continue;
            Assert.DoesNotContain(model.Rows, r => r.key == key);
            Assert.DoesNotContain(Lexicon.Get("leader.help.row", ("key", key), ("description", description)), recitation);
        }
    }

    /// <summary>
    /// Prepare's edges (repair round 1): a key that is not in the layer lifts
    /// nothing and adds no sentence; a long layer lifted to the spoken limit
    /// or below is spoken, with the filtered count.
    /// </summary>
    [Fact]
    public void Prepare_lifts_only_real_keys_and_decides_the_surface_on_the_count_left()
    {
        AssertTheDeskIsSafe();
        int all = KeyLayerHelp.Rows(KeyInventory.NoiseLayerContext).Count;

        var unmatched = KeyLayerHelp.Prepare(KeyInventory.NoiseLayerContext,
            new KeyLayerHelp.LayerGating(new[] { "Not a key" }, "Nothing is shut."));
        Assert.Equal(all, unmatched.Rows.Count);
        Assert.Null(unmatched.ClosingLine);

        var keep = KeyLayerHelp.Rows(KeyInventory.NoiseLayerContext).Take(KeyLayerHelp.SpokenLimit).Select(r => r.key).ToList();
        var liftTheRest = KeyLayerHelp.Rows(KeyInventory.NoiseLayerContext)
            .Select(r => r.key).Where(k => !keep.Contains(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shortened = KeyLayerHelp.Prepare(KeyInventory.NoiseLayerContext,
            new KeyLayerHelp.LayerGating(liftTheRest, "The rest are not available."));
        Assert.Equal(KeyLayerHelp.HelpSurface.Spoken, shortened.Surface);
        Assert.StartsWith(KeyLayerHelp.SpokenLimit.ToString(), shortened.Spoken);
        Assert.EndsWith("The rest are not available.", shortened.Spoken);
    }
}
