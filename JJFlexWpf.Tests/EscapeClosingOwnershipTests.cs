using System;
using System.IO;
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
