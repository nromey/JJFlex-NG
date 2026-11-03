using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

public sealed class DenoiseContractTests
{
    public DenoiseContractTests() => NoiseReductionPerChannelTests.AssertWindowless();

    [Fact]
    public void Catalog_is_complete_unique_directional_and_has_classified_labels()
    {
        var catalog = new DenoiseEngineCatalog();
        Assert.Equal(12, catalog.Entries.Count);
        Assert.Equal(12, catalog.Entries.Select(x => x.Id).Distinct().Count());
        foreach (var entry in catalog.Entries)
        {
            Assert.NotEqual(entry.LabelKey, Lexicon.Get(entry.LabelKey));
            Assert.NotEmpty(entry.Directions);
            if (entry.Side == DenoiseSide.Pc) Assert.NotNull(entry.Family);
            if (entry.Side == DenoiseSide.Radio || entry.Id == "spectral_capture")
                Assert.Equal(new[] { DenoiseDirection.Receive }, entry.Directions);
            else Assert.Contains(DenoiseDirection.Transmit, entry.Directions);
        }
        foreach (var reason in Enum.GetValues<Unavailability>())
            Assert.NotEqual(DenoiseEngineCatalog.UnavailabilityKey(reason), Lexicon.Get(DenoiseEngineCatalog.UnavailabilityKey(reason)));
        foreach (string id in new[] { "nr4", "nvidia_afx" }) Assert.Null(catalog.Find(id)!.Factory);
        // The helper engines (Sprint 49 Track B) are in the build; their probes start nothing.
        foreach (string id in new[] { "nnr", "nr2" }) { Assert.NotNull(catalog.Find(id)!.Factory); Assert.NotNull(catalog.Find(id)!.Probe); }
        Assert.NotNull(catalog.Find("spectral_capture")!.Factory);
        Assert.NotNull(catalog.Find("radio_nrl")!.WriteRadio);
        Assert.Throws<ArgumentException>(() => new DenoiseEngineCatalog(new[] { catalog.Entries[0], catalog.Entries[0] }));
    }

    [Fact]
    public void Descriptors_reject_unknown_range_step_and_choice_without_changing_values()
    {
        using var spectral = new SpectralCaptureEngine();
        var before = spectral.GetSetting("subtraction_amount");
        Assert.False(spectral.TrySetSetting("unknown", new(0.5)));
        Assert.False(spectral.TrySetSetting("subtraction_amount", new(-1)));
        Assert.False(spectral.TrySetSetting("subtraction_amount", new(0.705)));
        Assert.False(spectral.TrySetSetting("subtraction_amount", new(double.NaN)));
        Assert.False(spectral.TrySetSetting("subtraction_amount", new(0, "wrong")));
        Assert.Equal(before, spectral.GetSetting("subtraction_amount"));
        Assert.True(spectral.TrySetSetting("subtraction_amount", new(0.5)));
        Assert.Equal(0.5, spectral.GetSetting("subtraction_amount").Number);
        var choice = new EngineSettingDescriptor("model", "label", EngineSettingKind.Choice, new(0, "a"),
            Options: new[] { new EngineSettingOption("a", "a.label"), new EngineSettingOption("b", "b.label") });
        Assert.True(choice.Accepts(new(0, "b")));
        Assert.False(choice.Accepts(new(0, "c")));
        var stops = new EngineSettingDescriptor("limit", "label", EngineSettingKind.Number, new(9),
            0, 100, Stops: new double[] { 0, 3, 6, 9, 12, 15, 30, 100 });
        Assert.True(stops.Accepts(new(9)));
        Assert.False(stops.Accepts(new(10)));
    }

    [Fact]
    public void Settings_value_round_trips_without_a_config_or_profile()
    {
        var value = new DenoiseEngineSettings { EngineId = "nnr", Direction = DenoiseDirection.Transmit, Strength = 0.4f,
            Values = { new() { Name = "model", Choice = "premium" }, new() { Name = "limit", Number = 9 } } };
        var serializer = new XmlSerializer(typeof(DenoiseEngineSettings));
        using var text = new StringWriter(); serializer.Serialize(text, value);
        var again = (DenoiseEngineSettings)serializer.Deserialize(new StringReader(text.ToString()))!;
        Assert.Equal(value.EngineId, again.EngineId);
        Assert.Equal(value.Direction, again.Direction);
        Assert.Equal(value.Strength, again.Strength);
        Assert.Equal("premium", again.Values[0].Choice);
        Assert.Equal(9, again.Values[1].Number);
    }

    [Fact]
    public void Dfn_settings_are_pending_until_the_audio_frame_boundary()
    {
        string arch = Environment.Is64BitProcess ? "x64" : "x86";
        string lib = Path.Combine(RepoPaths.RequireRoot(), "runtimes", "win-" + arch, "native", DeepFilterNet3Engine.LibraryFileName);
        using var engine = new DeepFilterNet3Engine(9, lib);
        Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
        Assert.True(engine.TrySetSetting("attenuation_limit_db", new(12)));
        Assert.True(engine.TrySetSetting("post_filter_beta", new(0.025)));
        Assert.False(engine.TrySetSetting("attenuation_limit_db", new(101)));
        Assert.False(engine.TrySetSetting("missing", new(1)));
        var applied = typeof(DeepFilterNet3Engine).GetField("_applied", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Null(applied.GetValue(engine));
        Assert.True(engine.ProcessFrame(new float[480], new float[480]));
        Assert.NotNull(applied.GetValue(engine));
        Assert.Equal(12, engine.GetSetting("attenuation_limit_db").Number);
        for (int i = 0; i < 50; i++) {
            engine.TrySetSetting("attenuation_limit_db", new(12));
            engine.TrySetSetting("post_filter_beta", new(0.025));
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) {
            engine.TrySetSetting("attenuation_limit_db", new(12));
            engine.TrySetSetting("post_filter_beta", new(0.025));
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Receive_selection_cannot_change_the_transmit_provider()
    {
        using var tx = TxAudioConditioning.CreateNoiseReductionProvider();
        using var rx = new RxAudioPipeline();
        Assert.Equal("rnnoise", tx.EngineId);
        Assert.False(tx.Enabled);
        Assert.Equal(0.8f, tx.Strength);
        Assert.True(rx.SelectPcEngine("deepfilternet3"));
        rx.NeuralSlot.SetStrength("deepfilternet3", 0.3f);
        Assert.Equal("rnnoise", tx.EngineId);
        Assert.Equal(0.8f, tx.Strength);
        using var corrected = new RnnoiseEngine(RnnoiseScaling.Corrected);
        Assert.Equal("rnnoise", corrected.Id);
    }

    [Theory]
    [InlineData("SSB", true, true)]
    [InlineData("CW", true, false)]
    [InlineData("CWL", true, false)]
    [InlineData("DIGU", false, false)]
    [InlineData("FDM", false, false)]
    [InlineData("RTTY", false, false)]
    public void Mode_policy_uses_the_family_and_the_opt_out(string mode, bool spectral, bool neural)
    {
        Assert.Equal(spectral, RxAudioPipeline.ModePermits(DenoiseFamily.Spectral, mode, true));
        Assert.Equal(neural, RxAudioPipeline.ModePermits(DenoiseFamily.Neural, mode, true));
        Assert.True(RxAudioPipeline.ModePermits(DenoiseFamily.Spectral, mode, false));
        Assert.True(RxAudioPipeline.ModePermits(DenoiseFamily.Neural, mode, false));
    }
}
