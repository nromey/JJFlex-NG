using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track S, A4 (#1019, ruled S3 2026-11-09 15:41): at the first start
/// of this build a saved receive RNNoise Strength of exactly 0.8 (floor 20)
/// moves to 0.9 (floor 10), once. Windowless: real Load and Save in a temporary
/// folder, and the real receive mapping.
/// </summary>
public sealed class NoiseFloorMoveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjflex-49s-move-" + Guid.NewGuid().ToString("N"));

    public NoiseFloorMoveTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
    private string XmlPath => Path.Combine(_dir, "audioConfig.xml");

    /// <summary>Write a config the way a build from before this one left it: no marker beside it.</summary>
    private void WriteAsOlderBuild(AudioOutputConfig config)
    {
        config.Save(_dir);
        File.Delete(Path.Combine(_dir, ReceiveNoiseFloorMove.MarkerFileName));
    }

    private static float LoadedReceiveRnnoise(AudioOutputConfig config)
    {
        using var p = new RxAudioPipeline();
        ReceiveDenoiseConfig.Map(config, p, save: false);
        return p.RnnStrength;
    }

    [Fact]
    public void A_version_zero_file_at_point_eight_loads_as_floor_ten_and_saves_point_nine()
    {
        WriteAsOlderBuild(new AudioOutputConfig { RNNoiseStrength = 0.8f });
        var loaded = AudioOutputConfig.Load(_dir);
        Assert.Equal(Bits(0.9f), Bits(LoadedReceiveRnnoise(loaded)));
        Assert.Equal(10, NoiseFloor.FromStrength(LoadedReceiveRnnoise(loaded)));
        // The move was saved, and its marker written after it.
        Assert.True(ReceiveNoiseFloorMove.HasRun(_dir));
        Assert.Contains("<RNNoiseStrength>0.9</RNNoiseStrength>", File.ReadAllText(XmlPath));
    }

    [Fact]
    public void A_version_one_entry_at_point_eight_loads_as_floor_ten_and_saves_point_nine()
    {
        var old = new AudioOutputConfig { ReceiveDenoiseVersion = 1, RNNoiseStrength = 0.8f };
        old.ReceiveNeural.Engines.Add(new DenoiseEngineSettings { EngineId = "rnnoise", Direction = DenoiseDirection.Receive, Strength = 0.8f });
        old.ReceiveNeural.Engines.Add(new DenoiseEngineSettings { EngineId = "deepfilternet3", Direction = DenoiseDirection.Receive, Strength = 0.8f });
        WriteAsOlderBuild(old);
        var loaded = AudioOutputConfig.Load(_dir);
        Assert.Equal(Bits(0.9f), Bits(LoadedReceiveRnnoise(loaded)));
        Assert.Equal(Bits(0.9f), Bits(loaded.ReceiveNeural.Engines.Single(e => e.EngineId == "rnnoise").Strength));
        // Another engine's 0.8 is that engine's own choice: untouched.
        Assert.Equal(Bits(0.8f), Bits(loaded.ReceiveNeural.Engines.Single(e => e.EngineId == "deepfilternet3").Strength));
        var reread = AudioOutputConfig.Load(_dir);
        Assert.Equal(Bits(0.9f), Bits(reread.ReceiveNeural.Engines.Single(e => e.EngineId == "rnnoise").Strength));
        Assert.Equal(1, reread.ReceiveDenoiseVersion);
    }

    [Fact]
    public void A_floor_twenty_set_after_the_move_is_left_alone()
    {
        WriteAsOlderBuild(new AudioOutputConfig { RNNoiseStrength = 0.8f });
        AudioOutputConfig.Load(_dir);                       // the move runs
        var chosen = AudioOutputConfig.Load(_dir);
        chosen.RNNoiseStrength = 0.8f;                      // the operator sets floor 20 again
        chosen.Save(_dir);
        Assert.Equal(Bits(0.8f), Bits(LoadedReceiveRnnoise(AudioOutputConfig.Load(_dir))));
        Assert.Equal(Bits(0.8f), Bits(AudioOutputConfig.Load(_dir).RNNoiseStrength));
    }

    [Fact]
    public void An_older_builds_save_in_between_does_not_re_arm_the_move()
    {
        WriteAsOlderBuild(new AudioOutputConfig { RNNoiseStrength = 0.8f });
        AudioOutputConfig.Load(_dir);                       // the move runs: 0.9 on disk
        // An older build loads, the operator sets Strength 80 there, and it saves
        // the whole file with XmlSerializer, dropping every element it does not know.
        var xml = XDocument.Load(XmlPath);
        xml.Root!.Element("RNNoiseStrength")!.Value = "0.8";
        foreach (var node in xml.Root.Elements().Where(e => e.Name.LocalName.StartsWith("Receive", StringComparison.Ordinal)).ToArray()) node.Remove();
        xml.Save(XmlPath);
        Assert.True(File.Exists(Path.Combine(_dir, ReceiveNoiseFloorMove.MarkerFileName)));
        Assert.Equal(Bits(0.8f), Bits(LoadedReceiveRnnoise(AudioOutputConfig.Load(_dir))));
    }

    [Fact]
    public void Any_other_saved_value_is_untouched_and_nothing_is_saved_for_it()
    {
        var old = new AudioOutputConfig { RNNoiseStrength = 0.7f };
        WriteAsOlderBuild(old);
        DateTime before = File.GetLastWriteTimeUtc(XmlPath);
        var loaded = AudioOutputConfig.Load(_dir);
        Assert.Equal(Bits(0.7f), Bits(LoadedReceiveRnnoise(loaded)));
        Assert.Equal(before, File.GetLastWriteTimeUtc(XmlPath));
        Assert.True(ReceiveNoiseFloorMove.HasRun(_dir));   // a later 0.8 is the operator's
    }

    [Fact]
    public void The_move_is_journaled_as_done_by_JJ_Flexible()
    {
        var entries = new List<NoiseJournalEntry>();
        void Take(NoiseJournalEntry e) => entries.Add(e);
        WriteAsOlderBuild(new AudioOutputConfig { RNNoiseStrength = 0.8f });
        NoiseSettingsJournal.Recorded += Take;
        try { AudioOutputConfig.Load(_dir); AudioOutputConfig.Load(_dir); }
        finally { NoiseSettingsJournal.Recorded -= Take; }
        var moved = Assert.Single(entries, e => e.Kind == NoiseJournalKind.DefaultMoved);
        Assert.True(moved.ByJjFlexible);
        Assert.Equal(new NoiseJournalChange("rnnoise", "floor", "20", "10"), moved.Changes.Single());
    }

    [Fact]
    public void A_brand_new_setup_has_the_new_default_and_moves_nothing()
    {
        var loaded = AudioOutputConfig.Load(_dir);          // no file at all
        Assert.Equal(Bits(0.9f), Bits(LoadedReceiveRnnoise(loaded)));
        Assert.False(File.Exists(XmlPath));
    }
}
