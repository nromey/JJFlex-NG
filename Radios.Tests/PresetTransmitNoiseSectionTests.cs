using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Sprint 49 Track H2: a preset's transmit noise reduction section (design
/// part five) survives the operator's preset file, and a file without one
/// still reads as having none.
/// </summary>
public sealed class PresetTransmitNoiseSectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjflex-h2-presets-" + Guid.NewGuid().ToString("N"));

    public PresetTransmitNoiseSectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void The_transmit_section_round_trips_through_the_preset_file()
    {
        var store = new AudioChainPresets();
        var preset = new AudioChainPreset("Mic quiet")
        {
            SchemaVersion = AudioChainPreset.CurrentSchemaVersion,
            HoldsTransmitChain = false,
            TransmitNoise = new TxNoiseSetup
            {
                Name = "Mic quiet",
                Neural = new TxNoiseSlotSetup
                {
                    Enabled = true, EngineId = "nnr",
                    Engines = { new DenoiseEngineSettings { EngineId = "nnr", Direction = DenoiseDirection.Transmit, Strength = 0.7f } },
                },
                Gate = new TxNoiseGateSetup { Enabled = true, ThresholdMarginDb = 11 },
            },
        };
        store.NoisePresets.Add(preset);
        Assert.True(store.Save(_dir, "K5NER"));

        var back = AudioChainPresets.Load(_dir, "K5NER").NoisePresets.Single(p => p.Name == "Mic quiet");
        Assert.NotNull(back.TransmitNoise);
        Assert.True(back.TransmitNoise!.SameContentAs(preset.TransmitNoise));
        Assert.Null(back.ReceiveNoise);
        Assert.False(back.HoldsTransmitChain);
    }

    [Fact]
    public void A_preset_saved_without_the_section_reads_as_holding_none()
    {
        var store = new AudioChainPresets();
        store.Presets.Add(new AudioChainPreset("Ragchew") { SchemaVersion = AudioChainPreset.CurrentSchemaVersion });
        Assert.True(store.Save(_dir, "K5NER"));
        Assert.Null(AudioChainPresets.Load(_dir, "K5NER").Presets.Single(p => p.Name == "Ragchew").TransmitNoise);
    }
}
