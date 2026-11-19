using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track N, repair 1, F4: the noise layer's closing sentence after
/// Escape follows the ownership facts (#1150). It said "All changes reverted"
/// with a change made elsewhere still standing, and "No noise changes made"
/// when changes made elsewhere stood and the layer had none of its own. The
/// two true cases have their own keys; their words are Codex's.
/// </summary>
/// <remarks>Windowless: the noise controller over <see cref="NoiseFixture"/>'s fakes, on an MTA thread.</remarks>
public sealed class EscapeClosingOwnershipTests
{
    public EscapeClosingOwnershipTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private const string Layer = NoiseController.LayerSource;

    private static string Close(NoiseFixture f, NoiseSnapshot snap) => f.Noise.RestoreClosingKey(f.Noise.RestoreSnapshot(snap, Layer));

    [Fact]
    public void The_layers_change_reverted_and_a_change_made_elsewhere_kept_says_both()
    {
        // Codex's mixed case: Escape_keeps_an_outside_change_made_after_the_layers_own_change_to_the_same_slot.
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        var snap = f.Noise.TakeSnapshot();
        f.Noise.Toggle("deepfilternet3", Layer);
        f.Noise.Toggle("nnr", "Home");
        f.Noise.Toggle("nr4", Layer);
        Assert.Equal("audio.noise_layer.restored_kept_elsewhere", Close(f, snap));
        Assert.True(f.Noise.LastRestoreKeptOutsideChanges);
    }

    [Fact]
    public void Nothing_of_the_layers_to_revert_while_changes_made_elsewhere_stand_says_so()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        var snap = f.Noise.TakeSnapshot();
        f.Noise.Toggle("deepfilternet3", "Home");
        Assert.Equal("audio.noise_layer.restored_nothing_kept_elsewhere", Close(f, snap));
    }

    [Fact]
    public void A_transmit_change_made_elsewhere_counts_as_kept()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        var snap = f.Noise.TakeSnapshot();
        f.Noise.TransmitToggle("nnr", "Home");
        Assert.Equal("audio.noise_layer.restored_nothing_kept_elsewhere", Close(f, snap));
    }

    [Fact]
    public void Only_the_layers_own_changes_keep_the_old_sentences()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        var snap = f.Noise.TakeSnapshot();
        f.Noise.Toggle("deepfilternet3", Layer);
        Assert.Equal("audio.noise_layer.restored", Close(f, snap));
        Assert.False(f.Noise.LastRestoreKeptOutsideChanges);

        var again = f.Noise.TakeSnapshot();
        Assert.Equal("audio.noise_layer.restored_nothing", Close(f, again));
    }

    // ── Repair 2, R1 (Codex's re-check): a rebased picture must not erase the
    // evidence of an outside change. Home changed a slot before the layer's
    // next edit, so the restore target was rebased onto Home's change; Escape
    // correctly puts Home's change back, and the closing key must say a
    // change was kept rather than "All changes reverted".

    [Fact]
    public void A_kept_receive_change_rebased_before_the_layers_edit_is_still_reported_as_kept()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        var snap = f.Noise.TakeSnapshot();
        f.Noise.Toggle("deepfilternet3", "Home");
        var lever = f.Noise.Levers("deepfilternet3", false).Single(l => l.Key == "attenuation_limit_db");
        var original = f.Noise.NumberOf(lever);
        f.Noise.Adjust(lever, +1, Layer);
        Assert.True(f.Noise.RestoreSnapshot(snap, Layer));
        Assert.Equal(original, f.Noise.NumberOf(lever));
        Assert.True(f.Pipeline.NeuralSlot.Enabled);
        Assert.Equal("deepfilternet3", f.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.Equal("audio.noise_layer.restored_kept_elsewhere", f.Noise.RestoreClosingKey(true));
    }

    [Fact]
    public void A_kept_transmit_change_rebased_before_the_layers_edit_is_still_reported_as_kept()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        var snap = f.Noise.TakeSnapshot();
        f.Noise.TransmitToggle("nnr", "Home");
        f.Noise.TransmitToggle("rnnoise", Layer);
        Assert.True(f.Noise.RestoreSnapshot(snap, Layer));
        Assert.True(f.Transmit.WantsEnabled(DenoiseFamily.Neural));
        Assert.Equal("nnr", f.Transmit.ChosenEngineId(DenoiseFamily.Neural));
        Assert.Equal("audio.noise_layer.restored_kept_elsewhere", f.Noise.RestoreClosingKey(true));
    }

    [Fact]
    public void A_rebased_slot_whose_target_is_the_entry_picture_again_is_not_reported_as_kept()
    {
        // The control for R1: the layer turns the engine on, Home turns it off
        // again (back to the entry), and the layer turns it on once more. That
        // second edit rebases the target onto Home's state, which IS the entry
        // picture, so nothing that differs from the entry is left standing.
        // DeepFilterNet3 is the chosen engine before the picture, so the
        // chosen engine (part of the picture) never differs from it.
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("deepfilternet3", "test");
        f.Noise.Toggle("deepfilternet3", "test");
        Assert.False(f.Pipeline.NeuralSlot.Enabled);
        Assert.Equal("deepfilternet3", f.Pipeline.NeuralSlot.ChosenEngineId);
        var snap = f.Noise.TakeSnapshot();
        f.Noise.Toggle("deepfilternet3", Layer);
        f.Noise.Toggle("deepfilternet3", "Home");
        Assert.False(f.Pipeline.NeuralSlot.Enabled);
        f.Noise.Toggle("deepfilternet3", Layer);
        Assert.True(f.Pipeline.NeuralSlot.Enabled);
        Assert.True(f.Noise.RestoreSnapshot(snap, Layer));
        Assert.False(f.Pipeline.NeuralSlot.Enabled);
        Assert.Equal("audio.noise_layer.restored", f.Noise.RestoreClosingKey(true));
    }

    // ── Repair 2, R2 (Codex's re-check): a radio switch or level that differs
    // from the layer's picture and that Escape leaves standing, whatever moved
    // it (another client, or the radio itself as a sibling effect), means the
    // sentence must claim neither "No noise changes made" nor "All changes
    // reverted". The cause is not established, so it is a separate fact from
    // a change made elsewhere.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_radio_change_kept_by_Escape_is_not_described_as_no_changes_or_all_reverted(bool layerChange)
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        var snap = f.Noise.TakeSnapshot();
        if (layerChange) f.Noise.Toggle("nr4", Layer);
        f.Radio.Switches["radio_nr"] = true;
        bool restored = f.Noise.RestoreSnapshot(snap, Layer);
        Assert.Equal(layerChange, restored);
        Assert.True(f.Noise.IsOn("radio_nr"));
        Assert.True(f.Noise.LastRestoreLeftRadioDifferences);
        Assert.False(f.Noise.LastRestoreKeptOutsideChanges);
        Assert.Equal(layerChange ? "audio.noise_layer.restored_kept_elsewhere" : "audio.noise_layer.restored_nothing_kept_elsewhere",
            f.Noise.RestoreClosingKey(restored));
    }

    [Fact]
    public void A_radio_switch_the_layer_changed_and_Escape_put_back_leaves_no_radio_difference()
    {
        // The control for R2: the layer's own radio write is put back, so
        // nothing on the radio differs from the picture after Escape.
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        var snap = f.Noise.TakeSnapshot();
        f.Noise.Toggle("radio_nr", Layer);
        Assert.True(f.Noise.RestoreSnapshot(snap, Layer));
        Assert.False(f.Noise.IsOn("radio_nr"));
        Assert.False(f.Noise.LastRestoreLeftRadioDifferences);
        Assert.Equal("audio.noise_layer.restored", f.Noise.RestoreClosingKey(true));
    }

    [Fact]
    public void Both_new_sentences_are_in_the_lexicon_and_the_layer_chooses_through_the_controller()
    {
        Assert.NotEqual("audio.noise_layer.restored_kept_elsewhere", Lexicon.Get("audio.noise_layer.restored_kept_elsewhere"));
        Assert.NotEqual("audio.noise_layer.restored_nothing_kept_elsewhere", Lexicon.Get("audio.noise_layer.restored_nothing_kept_elsewhere"));
        string keys = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "KeyCommands.cs"));
        Assert.Contains("noise.RestoreClosingKey(restoredSomething)", keys);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
