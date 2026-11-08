using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JJFlexWpf.Tests;

public sealed class ReceiveDenoiseMigrationTests
{
    public ReceiveDenoiseMigrationTests() => NoiseReductionPerChannelTests.AssertWindowless();

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void Legacy_four_cases_round_trip_through_real_load_save_and_both_mappings(bool neural, bool spectral)
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-49-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var old = new AudioOutputConfig { RNNoiseEnabled = neural, SpectralSubEnabled = spectral,
                RNNoiseStrength = 0.3f, SpectralSubStrength = 0.6f, SpectralSubFloor = 0.07f,
                RNNoiseAutoDisableNonVoice = false, NoiseProfileLastPath = "profile.xml" };
            old.Save(dir);
            using var pipeline = new RxAudioPipeline();
            ReceiveDenoiseConfig.Map(AudioOutputConfig.Load(dir), pipeline, false);
            Assert.Equal(neural, pipeline.RnnEnabled); Assert.Equal(spectral, pipeline.SpectralEnabled);
            Assert.Equal(0.3f, pipeline.RnnStrength); Assert.Equal(0.6f, pipeline.SpectralStrength);
            Assert.Equal(0.07f, pipeline.SpectralFloor); // VoiceModesOnly retired (Sprint 49 Track E): nothing to assert
            var next = AudioOutputConfig.Load(dir);
            ReceiveDenoiseConfig.Map(next, pipeline, true); next.Save(dir);
            var loaded = AudioOutputConfig.Load(dir);
            Assert.Equal(1, loaded.ReceiveDenoiseVersion);
            Assert.Equal(neural ? "rnnoise" : "", loaded.ReceiveNeural.EngineId);
            Assert.Equal(spectral ? "spectral_capture" : "", loaded.ReceiveSpectral.EngineId);
            Assert.Equal(neural, loaded.RNNoiseEnabled); Assert.Equal(spectral, loaded.SpectralSubEnabled);
            Assert.Equal("profile.xml", loaded.NoiseProfileLastPath);
            using var restored = new RxAudioPipeline();
            ReceiveDenoiseConfig.Map(loaded, restored, false);
            Assert.Equal(neural, restored.RnnEnabled); Assert.Equal(spectral, restored.SpectralEnabled);
            Assert.Equal(0.3f, restored.RnnStrength); Assert.Equal(0.6f, restored.SpectralStrength);
            // Simulate the old XML serializer: unknown new fields disappear on save.
            var xmlPath = Path.Combine(dir, "audioConfig.xml");
            var xml = XDocument.Load(xmlPath);
            foreach (var node in xml.Root!.Elements().Where(e => e.Name.LocalName.StartsWith("Receive", StringComparison.Ordinal)).ToArray()) node.Remove();
            xml.Save(xmlPath);
            var downgraded = AudioOutputConfig.Load(dir);
            using var afterOld = new RxAudioPipeline();
            ReceiveDenoiseConfig.Map(downgraded, afterOld, false);
            Assert.Equal(neural, afterOld.RnnEnabled); Assert.Equal(spectral, afterOld.SpectralEnabled);
            Assert.Equal(0.3f, afterOld.RnnStrength); Assert.Equal(0.6f, afterOld.SpectralStrength);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void New_block_wins_and_a_choice_is_not_saved_until_enabled()
    {
        using var pipeline = new RxAudioPipeline();
        var config = new AudioOutputConfig { ReceiveDenoiseVersion = 1, RNNoiseEnabled = true, SpectralSubEnabled = true };
        ReceiveDenoiseConfig.Map(config, pipeline, false);
        Assert.False(pipeline.RnnEnabled); Assert.False(pipeline.SpectralEnabled);
        Assert.True(pipeline.SelectPcEngine("deepfilternet3"));
        ReceiveDenoiseConfig.Map(config, pipeline, true);
        Assert.Equal("", config.ReceiveNeural.EngineId);
        pipeline.RnnEnabled = true; pipeline.RnnEnabled = false;
        ReceiveDenoiseConfig.Map(config, pipeline, true);
        Assert.Equal("deepfilternet3", config.ReceiveNeural.EngineId);
        Assert.False(config.ReceiveNeural.Enabled);
    }
}
