using System;
using System.IO;
using System.Xml.Serialization;
using Radios;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Sprint 49 Track I phase 2: the setup record carries the expander's mode
/// and the WDSP mode's own depth, in every home a transmit setup lives (the
/// per-radio store and a preset; the live state is the same record), and a
/// record written before phase 2, with neither, is in the JJ mode: today's
/// sound, with no version bump. Temporary roots only; no operator settings.
/// </summary>
public sealed class TxExpanderModeRecordTests : IDisposable
{
    private const string Radio = "1234-5678-9012-3456";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jjf-track-i-p2-" + Guid.NewGuid().ToString("N"));

    public TxExpanderModeRecordTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static TxNoiseSetup Wdsp() => new()
    {
        Name = "Wdsp",
        Gate = new TxNoiseGateSetup { Enabled = true, RatioToOne = 4, ExpanderMode = ExpanderModes.Wdsp, WdspDepthDb = 30 },
    };

    [Fact]
    public void A_new_record_is_in_the_JJ_mode_with_the_default_depth()
    {
        var g = new TxNoiseGateSetup();
        Assert.Equal(ExpanderModes.Jj, g.ExpanderMode);
        Assert.False(ExpanderModes.IsWdsp(g.ExpanderMode));
        Assert.Equal(ExpanderModes.DefaultWdspDepthDb, g.WdspDepthDb);
    }

    [Fact]
    public void The_mode_and_both_strengths_round_trip_through_the_store()
    {
        var store = new TxNoiseSetupStore(_root);
        Assert.True(store.Save(Radio, Wdsp()).Succeeded);
        var back = store.Find(Radio, "Wdsp")!;
        Assert.True(back.SameContentAs(Wdsp()));
        Assert.Equal(ExpanderModes.Wdsp, back.Gate.ExpanderMode);
        Assert.Equal(30f, back.Gate.WdspDepthDb);
        Assert.Equal(4f, back.Gate.RatioToOne);       // the JJ mode's own setting is kept beside it
    }

    [Fact]
    public void The_mode_round_trips_through_a_preset()
    {
        string file = Path.Combine(_root, "p.xml");
        using (var fs = File.Create(file)) new XmlSerializer(typeof(AudioChainPreset)).Serialize(fs, new AudioChainPreset { Name = "P", TransmitNoise = Wdsp() });
        Assert.True(AudioChainPreset.TryLoad(file, out var back, out _));
        Assert.True(back.TransmitNoise!.SameContentAs(Wdsp()));
    }

    [Fact]
    public void A_record_from_before_phase_two_loads_in_the_JJ_mode()
    {
        // A version 2 setup element as phase 1 wrote it: the expander's fields, no mode, no depth.
        string file = Path.Combine(_root, "v2.xml");
        File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?><AudioChainPreset><Name>V2</Name><TransmitNoise><Name>A</Name>"
            + "<Version>2</Version><Gate><Enabled>true</Enabled><RatioToOne>3</RatioToOne><GateSetting>false</GateSetting></Gate></TransmitNoise></AudioChainPreset>");
        Assert.True(AudioChainPreset.TryLoad(file, out var preset, out _));
        var g = preset.TransmitNoise!.Gate;
        Assert.Equal(ExpanderModes.Jj, g.ExpanderMode);
        Assert.Equal(ExpanderModes.DefaultWdspDepthDb, g.WdspDepthDb);
        Assert.Equal(3f, g.RatioToOne);
    }

    [Fact]
    public void A_mode_word_this_build_does_not_know_is_kept_and_runs_as_the_JJ_mode()
    {
        var s = Wdsp();
        s.Gate.ExpanderMode = "clarity_from_a_later_build";
        var store = new TxNoiseSetupStore(_root);
        Assert.True(store.Save(Radio, s).Succeeded);
        var back = store.Find(Radio, "Wdsp")!;
        Assert.Equal("clarity_from_a_later_build", back.Gate.ExpanderMode);
        Assert.False(ExpanderModes.IsWdsp(back.Gate.ExpanderMode));
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("depth")]
    public void SameContentAs_sees_the_mode_and_the_depth(string field)
    {
        var a = Wdsp(); var b = Wdsp();
        if (field == "mode") b.Gate.ExpanderMode = ExpanderModes.Jj; else b.Gate.WdspDepthDb = 15;
        Assert.False(a.SameContentAs(b));
    }
}
