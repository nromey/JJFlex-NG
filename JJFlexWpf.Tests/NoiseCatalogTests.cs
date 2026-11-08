using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track E, E1: the engine catalog as the one list every noise
/// surface reads. Windowless: catalog rows, descriptors and a receive
/// pipeline built over fake engines.
/// </summary>
public sealed class NoiseCatalogTests
{
    public NoiseCatalogTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static readonly DenoiseEngineCatalog Catalog = new();

    [Fact]
    public void Every_engine_has_a_group_and_the_two_subjects_hold_the_ruled_engines_in_order()
    {
        var reducers = Catalog.InSurfaceOrder().Where(e => e.Group == DenoiseGroup.NoiseReduction).Select(e => e.Id).ToArray();
        var blankers = Catalog.InSurfaceOrder().Where(e => e.Group == DenoiseGroup.BlankersAndNotches).Select(e => e.Id).ToArray();
        Assert.Equal(new[] { "radio_nr", "radio_nrl", "radio_rnn", "radio_nrs", "radio_nrf",
            "nr2", "nr4", "spectral_capture", "rnnoise", "deepfilternet3", "nnr", "nvidia_afx" }, reducers);
        Assert.Equal(new[] { "radio_nb", "radio_wnb", "radio_anf", "radio_anfl", "radio_anft", "radio_apf" }, blankers);
        // Held: no Blanker family and no computer blanker row (ruled 2026-11-07).
        Assert.DoesNotContain(Catalog.Entries, e => e.Side == DenoiseSide.Pc && e.Group == DenoiseGroup.BlankersAndNotches);
        Assert.Equal(2, Enum.GetValues<DenoiseFamily>().Length);
    }

    [Theory]
    [InlineData("radio_nr", 0, 100, true, null)]
    [InlineData("radio_nrl", 1, 100, false, null)]
    [InlineData("radio_nrs", 0, 100, false, RadioCapability.NoiseReductionSpectral)]
    [InlineData("radio_nrf", 0, 100, false, RadioCapability.NoiseReductionFilter)]
    [InlineData("radio_nb", 0, 100, true, null)]
    [InlineData("radio_wnb", 0, 100, true, null)]
    [InlineData("radio_anf", 0, 100, true, null)]
    [InlineData("radio_anfl", 0, 100, false, null)]
    [InlineData("radio_apf", 0, 100, true, null)]
    public void Every_radio_level_has_its_range_whether_the_radio_reports_it_and_its_gate(
        string id, int min, int max, bool reported, RadioCapability? gate)
    {
        var row = Catalog.Find(id)!;
        Assert.Equal(DenoiseSide.Radio, row.Side);
        Assert.NotNull(row.Level);
        Assert.Equal(min, row.Level!.Min);
        Assert.Equal(max, row.Level.Max);
        Assert.Equal(reported, row.Level.ReportedBack);
        Assert.Equal(gate, row.Capability);
        Assert.NotNull(row.ReadRadio);
        Assert.NotNull(row.WriteRadio);
        Assert.False(row.IsSwitchOnly);
    }

    [Theory]
    [InlineData("radio_rnn", RadioCapability.NoiseReductionNeural)]
    [InlineData("radio_anft", RadioCapability.AutoNotchFft)]
    public void The_switch_only_radio_engines_have_no_level_and_keep_their_gate(string id, RadioCapability gate)
    {
        var row = Catalog.Find(id)!;
        Assert.True(row.IsSwitchOnly);
        Assert.Null(row.Level);
        Assert.Equal(gate, row.Capability);
    }

    [Fact]
    public void Only_the_Audio_Peak_Filter_is_CW_only()
    {
        Assert.Equal(new[] { "radio_apf" }, Catalog.Entries.Where(e => e.CwOnly).Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Legacy_NR_takes_its_range_from_its_one_constant()
    {
        var level = Catalog.Find("radio_nrl")!.Level!;
        Assert.Equal(DenoiseEngineCatalog.LegacyNrLevelMin, level.Min);
        Assert.Equal(DenoiseEngineCatalog.LegacyNrLevelMax, level.Max);
        Assert.Equal(100, DenoiseEngineCatalog.LegacyNrLevelMax);
    }

    [Fact]
    public void A_radio_level_steps_by_one_up_to_fifteen_and_by_five_above()
    {
        var level = Catalog.Find("radio_nrl")!.Level!;
        Assert.Equal(2, level.Step(1, +1));
        Assert.Equal(1, level.Step(1, -1)); // the end holds
        Assert.Equal(15, level.Step(14, +1));
        Assert.Equal(20, level.Step(15, +1));
        Assert.Equal(15, level.Step(20, -1));
        Assert.Equal(14, level.Step(15, -1));
        Assert.Equal(20, level.Step(17, +1)); // off the grid, onto it
        Assert.Equal(15, level.Step(17, -1));
        Assert.Equal(100, level.Step(100, +1));
        Assert.Equal(95, level.Step(100, -1));
    }

    [Fact]
    public void The_helper_rows_carry_descriptors_equal_to_Track_As_documented_offer()
    {
        // Written out here from Track A's report (2026-11-05, "The descriptors
        // and every label key the helper sends"), not copied from the mirror,
        // so a change to either side is a red test.
        var nnr = Catalog.Find("nnr")!.Settings;
        Assert.Equal(new[] { "model", "mask_floor_db" }, nnr.Select(d => d.Name));
        var model = nnr[0];
        Assert.Equal(EngineSettingKind.Choice, model.Kind);
        Assert.True(model.ChangesLatency);
        Assert.Equal("standard", model.Default.Choice);
        Assert.Equal(new[] { "standard", "premium", "standard_cw", "premium_cw" }, model.Options!.Select(o => o.Name));
        var floor = nnr[1];
        Assert.Equal((-50.0, -10.0, 1.0, -25.0), (floor.Min, floor.Max, floor.Step, floor.Default.Number));
        Assert.False(floor.ChangesLatency);
        Assert.Equal("audio.unit.decibels", floor.UnitKey);

        var nr2 = Catalog.Find("nr2")!.Settings;
        Assert.Equal(new[] { "gain_method", "noise_estimator", "artefact_elimination", "trained_t1", "trained_t2" }, nr2.Select(d => d.Name));
        Assert.Equal(new[] { "linear", "log", "gamma", "trained" }, nr2[0].Options!.Select(o => o.Name));
        Assert.Equal("gamma", nr2[0].Default.Choice);
        Assert.Equal(new[] { "osms", "mmse", "nstat" }, nr2[1].Options!.Select(o => o.Name));
        Assert.Equal("osms", nr2[1].Default.Choice);
        Assert.Equal(new[] { "off", "on" }, nr2[2].Options!.Select(o => o.Name));
        Assert.Equal("on", nr2[2].Default.Choice);
        Assert.Equal((-5.0, 5.0, 0.1, -2.0), (nr2[3].Min, nr2[3].Max, nr2[3].Step, nr2[3].Default.Number));
        Assert.Equal((0.02, 0.3, 0.01, 0.2), (nr2[4].Min, nr2[4].Max, nr2[4].Step, nr2[4].Default.Number));
        Assert.All(nr2, d => Assert.False(d.ChangesLatency));
        Assert.All(nr2, d => Assert.True(d.Advanced));
        Assert.All(nnr, d => Assert.False(d.Advanced));
    }

    [Fact]
    public void The_mirror_check_is_silent_on_agreement_and_names_a_disagreement()
    {
        Assert.Null(DenoiseEngineCatalog.MirrorDisagreement("nnr", DenoiseEngineCatalog.NnrMirror));
        Assert.Null(DenoiseEngineCatalog.MirrorDisagreement("nr2", DenoiseEngineCatalog.Nr2Mirror));
        // Positive control: one default moved, one lever new.
        var drifted = DenoiseEngineCatalog.NnrMirror.Select(d => d.Name == "mask_floor_db" ? d with { Default = new(-30) } : d)
            .Append(new EngineSettingDescriptor("alpha", "x", EngineSettingKind.Number, new(0))).ToList();
        string? problem = DenoiseEngineCatalog.MirrorDisagreement("nnr", drifted);
        Assert.Contains("mask_floor_db differs", problem);
        Assert.Contains("alpha is new", problem);
    }

    [Fact]
    public void NNRs_five_test_only_levers_are_not_offered()
    {
        var names = Catalog.Find("nnr")!.Settings.Select(d => d.Name).ToArray();
        foreach (string hidden in new[] { "alpha", "alpha_knee", "tau", "max_gain", "smooth" })
            Assert.DoesNotContain(hidden, names);
    }

    [Fact]
    public void Every_label_and_option_key_in_the_catalog_has_lexicon_text()
    {
        foreach (var row in Catalog.Entries)
        {
            Assert.NotEqual(row.LabelKey, Lexicon.Get(row.LabelKey));
            foreach (var d in row.Settings)
            {
                Assert.NotEqual(d.LabelKey, Lexicon.Get(d.LabelKey));
                if (d.UnitKey != null) Assert.NotEqual(d.UnitKey, Lexicon.Get(d.UnitKey));
                foreach (var o in d.Options ?? Array.Empty<EngineSettingOption>())
                    Assert.NotEqual(o.LabelKey, Lexicon.Get(o.LabelKey));
            }
        }
    }

    // ── DeepFilterNet3's stops ──

    [Fact]
    public void Every_DeepFilterNet3_stop_is_accepted_and_nothing_between_them()
    {
        var limit = DeepFilterNet3Engine.SettingDescriptors.Single(d => d.Name == "attenuation_limit_db");
        foreach (double stop in new double[] { 0, 3, 6, 9, 12, 15, 18, 24, 30, 40, 60, 100 })
            Assert.True(limit.Accepts(new(stop)), stop.ToString());
        foreach (double between in new double[] { 1, 10, 20, 50, 99 })
            Assert.False(limit.Accepts(new(between)), between.ToString());
        Assert.Equal(9, limit.Default.Number);
        Assert.Equal(new EngineSettingValue(12), limit.StepFrom(new(9), +1));
        Assert.Equal(new EngineSettingValue(6), limit.StepFrom(new(9), -1));
        Assert.Null(limit.StepFrom(new(100), +1));
        Assert.Null(limit.StepFrom(new(0), -1));
    }

    [Theory]
    [InlineData(10, 9)]
    [InlineData(11, 12)]
    [InlineData(20, 18)]
    [InlineData(21, 18)]  // equidistant from 18 and 24: the lower stop, the gentler one
    [InlineData(50, 40)]
    [InlineData(80, 60)]
    [InlineData(90, 100)]
    [InlineData(150, 100)]
    [InlineData(-4, 0)]
    public void A_stored_limit_that_is_not_a_stop_snaps_to_the_nearest_stop_on_load(double stored, double expected)
    {
        using var p = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(new[]
        {
            new DenoiseEngineEntry("deepfilternet3", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.deepfilternet3",
                new[] { DenoiseDirection.Receive }, null, () => Unavailability.None,
                SettingDescriptors: DeepFilterNet3Engine.SettingDescriptors),
        }));
        p.NeuralSlot.RestoreSettings(new[] { new DenoiseEngineSettings {
            EngineId = "deepfilternet3", Direction = DenoiseDirection.Receive, Strength = 1,
            Values = new() { new() { Name = "attenuation_limit_db", Number = stored } } } });
        // Never silently the default, 9: the nearest stop.
        Assert.Equal(expected, p.NeuralSlot.GetSetting("deepfilternet3", "attenuation_limit_db").Number);
    }

    [Fact]
    public void Snapping_never_reaches_the_default_for_a_value_it_could_place()
    {
        // Positive control for the test above: without the snap, every
        // off-stop value would read back as the default 9.
        var limit = DeepFilterNet3Engine.SettingDescriptors.Single(d => d.Name == "attenuation_limit_db");
        Assert.Equal(60, limit.Snap(new(61)).Number);
        Assert.Equal(9, limit.Snap(new(double.NaN)).Number);
        Assert.Equal(9, limit.Snap(new(0, "a choice")).Number);
    }

    // ── NR4's new levers ──

    [Fact]
    public void NR4s_three_new_levers_are_described_and_only_the_estimator_rebuilds()
    {
        var d = SpecbleachEngine.SettingDescriptors;
        var estimator = d.Single(x => x.Name == "noise_estimator");
        Assert.True(estimator.Rebuilds);
        Assert.False(estimator.ChangesLatency);
        Assert.True(estimator.NeedsRebuild);
        Assert.Equal(new[] { "spp_mmse", "brandt", "martin" }, estimator.Options!.Select(o => o.Name));
        Assert.Equal("spp_mmse", estimator.Default.Choice);
        Assert.Equal("off", d.Single(x => x.Name == "transient_protection").Default.Choice);
        Assert.Equal(20, d.Single(x => x.Name == "tonal_reduction_db").Default.Number);
        Assert.Equal(new[] { "reduction_db" }, d.Where(x => !x.Advanced).Select(x => x.Name));
        Assert.All(d.Where(x => x.Name != "noise_estimator"), x => Assert.False(x.NeedsRebuild, x.Name));
        Assert.Equal(SpecbleachNoiseEstimator.Martin,
            SpecbleachEngine.EstimatorFrom(new Dictionary<string, EngineSettingValue> { ["noise_estimator"] = new(0, "martin") }));
        Assert.Equal(SpecbleachNoiseEstimator.SppMmse, SpecbleachEngine.EstimatorFrom(null));
    }

    [Fact]
    public void NR4s_new_levers_reach_the_librarys_parameter_block()
    {
        using var e = new SpecbleachEngine(new Dictionary<string, EngineSettingValue>
        {
            ["transient_protection"] = new(0, "on"),
            ["tonal_reduction_db"] = new(30),
        }, SpecbleachEngineTests.LibraryPath(), SpecbleachNoiseEstimator.Brandt);
        Assert.True(e.IsAvailable, e.UnavailabilityDetail);
        Assert.Equal(1, e.AppliedParameters.TransientProtectionEnable);
        Assert.Equal((float)Math.Pow(10, -30 / 20.0), e.AppliedParameters.TonalReductionGain, 6);
        Assert.Equal((int)SpecbleachNoiseEstimator.Brandt, e.AppliedParameters.NoiseEstimationMethod);
        Assert.Equal("brandt", e.GetSetting("noise_estimator").Choice);
        Assert.Equal("on", e.GetSetting("transient_protection").Choice);

        // A live change of the switch and the tone reduction reaches the block at the next frame.
        Assert.True(e.TrySetSetting("transient_protection", new(0, "off")));
        Assert.True(e.TrySetSetting("tonal_reduction_db", new(10)));
        e.ProcessFrame(new float[480], new float[480]);
        Assert.Equal(0, e.AppliedParameters.TransientProtectionEnable);
        Assert.Equal((float)Math.Pow(10, -10 / 20.0), e.AppliedParameters.TonalReductionGain, 6);

        // The estimator cannot change on a live instance.
        Assert.False(e.TrySetSetting("noise_estimator", new(0, "martin")));
        Assert.True(e.TrySetSetting("noise_estimator", new(0, "brandt")));
    }

    [Fact]
    public void Changing_NR4s_estimator_rebuilds_the_engine_through_the_slot()
    {
        var built = new ConcurrentQueue<SpecbleachNoiseEstimator>();
        using var p = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(new[]
        {
            new DenoiseEngineEntry("nr4", DenoiseSide.Pc, DenoiseFamily.Spectral, "audio.denoise.nr4",
                new[] { DenoiseDirection.Receive },
                initial => { var est = SpecbleachEngine.EstimatorFrom(initial); built.Enqueue(est); return new PassEngine(); },
                () => Unavailability.None, SettingDescriptors: SpecbleachEngine.SettingDescriptors),
        }));
        Assert.True(p.SpectralSlot.Select("nr4", out _));
        p.SpectralEnabled = true;
        Until(() => p.SpectralSlot.RunningEngineId == "nr4", () => p.Process(new float[960]));
        // One engine per channel: the pipeline is stereo, so a build makes two.
        Assert.Equal(new[] { SpecbleachNoiseEstimator.SppMmse, SpecbleachNoiseEstimator.SppMmse }, built.ToArray());

        // A non-rebuilding lever builds nothing.
        Assert.True(p.SpectralSlot.TrySetSetting("nr4", "tonal_reduction_db", new(10)));
        for (int i = 0; i < 30; i++) p.Process(new float[960]);
        Assert.Equal(2, built.Count);

        Assert.True(p.SpectralSlot.TrySetSetting("nr4", "noise_estimator", new(0, "martin")));
        Until(() => built.Count == 4, () => p.Process(new float[960]));
        Assert.Equal(SpecbleachNoiseEstimator.Martin, built.Last());
    }

    private sealed class PassEngine : IDenoiseEngine
    {
        public string Id => "nr4";
        public int SampleRate => 48000;
        public int FrameLength => 480;
        public bool IsAvailable => true;
        public int LatencySamples => 0;
        public IReadOnlyList<EngineSettingDescriptor> Settings => SpecbleachEngine.SettingDescriptors;
        public EngineSettingValue GetSetting(string name) => SpecbleachEngine.SettingDescriptors.Single(d => d.Name == name).Default;
        public bool TrySetSetting(string name, EngineSettingValue value) => true;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output) { input.CopyTo(output); return true; }
        public void Reset() { }
        public void Dispose() { }
    }

    private static void Until(Func<bool> predicate, Action? step = null)
    {
        var time = Stopwatch.StartNew();
        while (!predicate() && time.ElapsedMilliseconds < 5000) { step?.Invoke(); Thread.Sleep(1); }
        Assert.True(predicate(), "condition did not become true");
    }
}
