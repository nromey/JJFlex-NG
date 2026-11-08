using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track E, E10: presets that hold receive noise reduction, as
/// sections, safely against older builds. Windowless: an in-memory store and
/// a recording transmit chain.
/// </summary>
public sealed class NoisePresetTests
{
    public NoisePresetTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private sealed class MemoryStore : INoisePresetStore
    {
        public AudioChainPresets Store = AudioChainPresets.CreateDefaults();
        public string LastXml = "";
        public AudioChainPresets Load() => Store;
        public bool Save(AudioChainPresets store)
        {
            Store = store;
            var serializer = new XmlSerializer(typeof(AudioChainPresets));
            using var w = new StringWriter();
            serializer.Serialize(w, store);
            LastXml = w.ToString();
            return true;
        }
    }

    private sealed class Chain : ITransmitChainAccess
    {
        public List<string> Applied = new();
        public void Apply(AudioChainPreset preset) => Applied.Add(preset.Name);
        public AudioChainPreset Capture(string name) => new(name) { MicGain = 77, TxFilterLow = 300 };
    }

    /// <summary>The store as a schema 2 build reads it: only the Presets list.</summary>
    [XmlRoot("AudioChainPresets")]
    public class OldStore
    {
        public List<OldPreset> Presets { get; set; } = new();
    }

    [XmlType("AudioChainPreset")]
    public class OldPreset
    {
        public string Name { get; set; } = "";
        public int MicGain { get; set; } = 50;
    }

    private static (NoiseFixture F, MemoryStore Store, Chain Chain) Rig()
    {
        var f = new NoiseFixture();
        var store = new MemoryStore();
        var chain = new Chain();
        f.Noise.PresetStore = store;
        f.Noise.TransmitChain = chain;
        return (f, store, chain);
    }

    [Fact]
    public void Balanced_capture_is_the_one_shipped_starter_and_deleting_it_sticks()
    {
        var (f, store, _) = Rig();
        using var _f = f;
        var names = f.Noise.NoisePresets().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "Balanced capture" }, names);
        var balanced = store.Store.NoisePresets.Single();
        Assert.False(balanced.HoldsTransmitChain);
        Assert.Equal(0.45, balanced.ReceiveNoise!.Spectral.Settings!.Values.Single(v => v.Name == "subtraction_amount").Number);
        Assert.Equal(0.65f, balanced.ReceiveNoise.Neural.Settings!.Strength);
        Assert.True(f.Noise.DeletePreset("Balanced capture"));
        Assert.Empty(f.Noise.NoisePresets());
    }

    [Fact]
    public void Applying_Balanced_capture_sets_both_slots_and_never_touches_the_transmit_chain()
    {
        var (f, _, chain) = Rig();
        using var _f = f;
        f.Noise.ApplyPreset("Balanced capture", "test");
        Assert.True(f.Noise.IsOn("spectral_capture"));
        Assert.True(f.Noise.IsOn("rnnoise"));
        Assert.Equal(0.65f, f.Pipeline.NeuralSlot.GetStrength("rnnoise"), 3);
        Assert.Equal(0.04, f.Pipeline.SpectralSlot.GetSetting("spectral_capture", "spectral_floor").Number, 6);
        Assert.Empty(chain.Applied);
        Assert.Equal("Balanced capture preset: Captured noise on, RNNoise on.", f.Spoken.Last);
    }

    [Fact]
    public void A_schema_2_preset_applies_the_transmit_chain_and_leaves_noise_reduction_alone()
    {
        var (f, _, chain) = Rig();
        using var _f = f;
        f.Noise.Toggle("nr4", "test");
        f.Noise.ApplyPreset("Ragchew", "test");
        Assert.Equal(new[] { "Ragchew" }, chain.Applied);
        Assert.True(f.Noise.IsOn("nr4"));
        Assert.Null(AudioChainPresets.CreateDefaults().Presets.First().ReceiveNoise);
        Assert.True(AudioChainPresets.CreateDefaults().Presets.All(p => p.HoldsTransmitChain));
    }

    [Fact]
    public void A_noise_only_preset_round_trips_and_is_invisible_to_an_older_reader()
    {
        var (f, store, chain) = Rig();
        using var _f = f;
        f.Noise.Toggle("nr4", "test");
        f.Noise.Adjust(f.Noise.Levers("nr4", false)[1], +1, "test");
        Assert.Equal("Weak DX", f.Noise.SavePreset("Weak DX", includeNoise: true, includeChain: false, PresetCollision.Ask, "test"));
        Assert.Equal("Preset Weak DX saved, receive noise reduction only.", f.Spoken.Last);
        var saved = store.Store.NoisePresets.Single(p => p.Name == "Weak DX");
        Assert.False(saved.HoldsTransmitChain);
        Assert.Equal(3, saved.SchemaVersion);
        Assert.DoesNotContain(store.Store.Presets, p => p.Name == "Weak DX");

        // An older build reads only Presets: it never sees the noise-only preset,
        // so it can never apply it as a transmit chain of defaults.
        var old = (OldStore)new XmlSerializer(typeof(OldStore)).Deserialize(new StringReader(store.LastXml))!;
        Assert.DoesNotContain(old.Presets, p => p.Name == "Weak DX");
        Assert.Contains(old.Presets, p => p.Name == "Ragchew");

        // Round trip through XML into this build.
        var reread = (AudioChainPresets)new XmlSerializer(typeof(AudioChainPresets)).Deserialize(new StringReader(store.LastXml))!;
        var back = reread.NoisePresets.Single(p => p.Name == "Weak DX");
        Assert.Equal("nr4", back.ReceiveNoise!.Spectral.EngineId);
        Assert.True(back.ReceiveNoise.Spectral.On);

        // Change things, apply it back, and the chain is untouched.
        f.Noise.Toggle("nr4", "test");
        f.Noise.ApplyPreset("Weak DX", "test");
        Assert.True(f.Noise.IsOn("nr4"));
        Assert.Equal(21, f.Pipeline.SpectralSlot.GetSetting("nr4", "reduction_db").Number);
        Assert.Empty(chain.Applied);
    }

    [Fact]
    public void A_full_chain_preset_holds_both_and_applies_both()
    {
        var (f, store, chain) = Rig();
        using var _f = f;
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.SavePreset("Contest", includeNoise: true, includeChain: true, PresetCollision.Ask, "test");
        var saved = store.Store.Presets.Single(p => p.Name == "Contest");
        Assert.True(saved.HoldsTransmitChain);
        Assert.Equal(77, saved.MicGain);
        Assert.NotNull(saved.ReceiveNoise);
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.ApplyPreset("Contest", "test");
        Assert.True(f.Noise.IsOn("rnnoise"));
        Assert.Equal(new[] { "Contest" }, chain.Applied);
    }

    [Fact]
    public void A_collision_asks_and_then_replaces_or_keeps_both()
    {
        var (f, store, _) = Rig();
        using var _f = f;
        Assert.NotNull(f.Noise.SavePreset("Mine", true, false, PresetCollision.Ask, "test"));
        Assert.True(f.Noise.PresetExists("mine"));
        Assert.Null(f.Noise.SavePreset("Mine", true, false, PresetCollision.Ask, "test"));
        Assert.Equal("Mine 2", f.Noise.SavePreset("Mine", true, false, PresetCollision.KeepBoth, "test"));
        Assert.Equal("Mine", f.Noise.SavePreset("Mine", true, false, PresetCollision.Replace, "test"));
        Assert.Equal(2, store.Store.NoisePresets.Count(p => p.Name.StartsWith("Mine")));
    }

    [Fact]
    public void Rename_refuses_a_taken_name_and_otherwise_renames()
    {
        var (f, _, _) = Rig();
        using var _f = f;
        f.Noise.SavePreset("Weak DX", true, false, PresetCollision.Ask, "test");
        Assert.False(f.Noise.RenamePreset("Weak DX", "Ragchew"));
        Assert.True(f.Noise.RenamePreset("Weak DX", "Weak CW"));
        Assert.Equal("Preset Weak DX renamed to Weak CW.", f.Spoken.Last);
        Assert.Contains("Weak CW", f.Noise.AllPresetNames());
    }

    [Fact]
    public void A_preset_is_listed_under_its_main_engine_and_the_brackets_step_presets()
    {
        var (f, _, _) = Rig();
        using var _f = f;
        Assert.Equal("rnnoise", NoiseController.MainEngineOf(NoiseController.BalancedCapture()));
        Assert.Single(f.Noise.PresetsFor("rnnoise"));
        Assert.Empty(f.Noise.PresetsFor("spectral_capture"));
        f.Noise.StepPreset(+1, "test");
        Assert.StartsWith("Balanced capture preset", f.Spoken.Last);
    }

    [Fact]
    public void An_older_preset_file_loads_as_a_transmit_chain_with_no_noise_section()
    {
        const string v2 = "<AudioChainPreset schemaVersion=\"2\"><Name>Old</Name><MicGain>61</MicGain></AudioChainPreset>";
        var p = (AudioChainPreset)new XmlSerializer(typeof(AudioChainPreset)).Deserialize(new StringReader(v2))!;
        Assert.True(p.HoldsTransmitChain);
        Assert.Null(p.ReceiveNoise);
        Assert.Equal(61, p.MicGain);
    }
}
