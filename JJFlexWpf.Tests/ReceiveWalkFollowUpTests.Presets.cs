using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>A noise preset saved, renamed or deleted from any surface: the menus that list presets follow (Sprint 49 Track N2).</summary>
public sealed partial class ReceiveWalkFollowUpTests
{
    private sealed class MemoryStore : INoisePresetStore
    {
        public AudioChainPresets Store = AudioChainPresets.CreateDefaults();
        public bool Refuse;
        public AudioChainPresets Load() => Store;
        public bool Save(AudioChainPresets store) { if (Refuse) return false; Store = store; return true; }
    }

    // ── A noise preset saved, renamed or deleted: the menus follow ──

    [Fact]
    public void A_noise_preset_saved_renamed_or_deleted_tells_the_lists_built_from_the_store()
    {
        using var f = new NoiseFixture();
        var store = new MemoryStore();
        f.Noise.PresetStore = store;
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        int changed = 0;
        f.Noise.PresetsChanged += () => changed++;

        // Reading the list offers the starter once and writes it: not a change
        // anyone made, and a rebuild that read the list would loop on it.
        Assert.NotEmpty(f.Noise.NoisePresets());
        Assert.Equal(0, changed);

        Assert.Equal("bench dfn", f.Noise.SavePreset("bench dfn", true, false, PresetCollision.Ask, "test"));
        Assert.Equal(1, changed);
        Assert.True(f.Noise.RenamePreset("bench dfn", "bench two"));
        Assert.Equal(2, changed);
        Assert.True(f.Noise.DeletePreset("bench two"));
        Assert.Equal(3, changed);

        // Nothing changed, nothing rebuilt: a refused name, a missing preset,
        // an empty name, a collision left to ask, and a store that would not write.
        Assert.Null(f.Noise.SavePreset("", true, false, PresetCollision.Ask, "test"));
        Assert.Null(f.Noise.SavePreset("Balanced capture", true, false, PresetCollision.Ask, "test"));
        Assert.False(f.Noise.RenamePreset("missing", "other"));
        Assert.False(f.Noise.DeletePreset("missing"));
        store.Refuse = true;
        Assert.Null(f.Noise.SavePreset("refused", true, false, PresetCollision.Ask, "test"));
        Assert.Equal(3, changed);

        // The Workshop's own writes say so through the controller.
        f.Noise.PresetsChangedElsewhere();
        Assert.Equal(4, changed);
    }

    [Fact]
    public void The_receive_and_transmit_noise_menus_rebuild_on_a_preset_change_and_the_Workshop_reports_its_writes()
    {
        // The menu bar is a window's: read the wiring from source. One hook for
        // both stores, reached from both menus, so whichever is built first
        // follows (the transmit setups' mechanism, not a second one).
        string tx = Source("JJFlexWpf", "NativeMenuBar.TransmitNoise.cs");
        string hook = Body(tx, "private void FollowNoiseStores(NoiseController noise)", "\n    }\n");
        Assert.Contains("if (ReferenceEquals(_txSetupsHookedOn, noise)) return;", hook);
        Assert.Contains("noise.TransmitSetupsChanged += () => QueueMenuRebuild(", hook);
        Assert.Contains("noise.PresetsChanged += () => QueueMenuRebuild(", hook);
        Assert.Contains("FollowNoiseStores(noise);", Body(tx, "private void BuildTransmitNoiseItems(IntPtr parent)", "BuildTransmitPresetItems("));
        string menu = Source("JJFlexWpf", "NativeMenuBar.cs");
        Assert.Contains("FollowNoiseStores(noise);", Body(menu, "private void BuildNoiseItems(IntPtr parent, DenoiseGroup group)", "foreach (var e in noise.Engines(group))"));
        // The Workshop writes the store through MainWindow's callback; only a write that landed says so.
        string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
        string save = Body(main, "Dialogs.AudioWorkshopDialog.SavePresetsCallback = presets =>", "};");
        Assert.Contains("if (saved) Noise.PresetsChangedElsewhere();", save);
    }
}
