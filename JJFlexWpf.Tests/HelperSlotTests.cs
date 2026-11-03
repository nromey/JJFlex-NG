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
/// The helper engine inside the real receive slot, and the one slot defect
/// helper engines make reachable (a latency setting changed while arrowing
/// back). Windowless; every stub process hidden, in a job, and gone.
/// </summary>
public sealed class HelperSlotTests
{
    public HelperSlotTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static float[] Audio(int salt) => Enumerable.Range(0, 960).Select(i => MathF.Sin((i + salt * 960) * 0.013f) * 0.3f).ToArray();

    private sealed class Pipe : IDisposable
    {
        public readonly HelperTestRig Rig;
        public readonly RxAudioPipeline Pipeline;
        public readonly ConcurrentBag<WdspHelperEngine> Made = new();
        public readonly ConcurrentQueue<DenoiseSelectionResult> Events = new();
        private int _salt;

        public Pipe(Func<int, IReadOnlyList<string>>? args = null, string? path = null)
        {
            Rig = new HelperTestRig(args, path: path);
            var host = Rig.Host;
            var rows = new[] {
                new DenoiseEngineEntry("nnr", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.nnr",
                    new[] { DenoiseDirection.Receive, DenoiseDirection.Transmit },
                    initial => { var e = new WdspHelperEngine(host, "nnr", DenoiseDirection.Receive, initial); Made.Add(e); return e; },
                    () => host.Probe("nnr")),
                // The spectral slot's own engine, so saving the settings finds its descriptors.
                new DenoiseEngineEntry("spectral_capture", DenoiseSide.Pc, DenoiseFamily.Spectral, "audio.denoise.spectral_capture",
                    new[] { DenoiseDirection.Receive }, _ => new SpectralCaptureEngine(), () => Unavailability.None,
                    SettingDescriptors: SpectralCaptureEngine.SettingDescriptors),
            };
            Pipeline = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(rows));
            Pipeline.SelectionResult += (_, e) => Events.Enqueue(e);
        }

        /// <summary>One 10 ms buffer, paced so the helper's answers are in time, as they are live.</summary>
        public float[] Step()
        {
            HelperTestRig.Pace(Made.ToArray());
            var buffer = Audio(_salt++);
            Pipeline.Process(buffer);
            return buffer;
        }

        public void Dispose() { Pipeline.Dispose(); Rig.Dispose(); }
    }

    [Fact]
    public void Selecting_nnr_warms_cross_fades_and_raises_one_processing_event()
    {
        using var p = new Pipe();
        p.Pipeline.RnnEnabled = true;
        Assert.True(p.Pipeline.SelectPcEngine("nnr"));
        HelperTestRig.Until(() => p.Pipeline.NeuralSlot.RunningEngineId == "nnr", "nnr running", step: () => p.Step());
        HelperTestRig.Until(() => p.Events.Any(e => e.Processing), "the processing event", step: () => p.Step());
        for (int i = 0; i < 30; i++) p.Step();
        var only = Assert.Single(p.Events);
        Assert.Equal(("nnr", true), (only.EngineId, only.Processing));
        Assert.Equal(2, p.Made.Count);
    }

    [Fact]
    public void A_helper_crash_during_warm_up_is_a_failed_start_keeping_the_choice_with_audio_untouched()
    {
        using var p = new Pipe(n => n == 1 ? new[] { "--crash-after", "6:9" } : Array.Empty<string>());
        p.Pipeline.RnnEnabled = true;
        Assert.True(p.Pipeline.SelectPcEngine("nnr"));
        HelperTestRig.Until(() => p.Events.Any(), "a selection result", step: () => p.Step());
        var failure = Assert.Single(p.Events);
        Assert.False(failure.Processing);
        Assert.True(failure.SlotStopped);
        Assert.Equal("nnr", p.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.Empty(p.Pipeline.NeuralSlot.RunningEngineId);
        for (int i = 0; i < 20; i++)
        {
            var expected = Audio(1000 + i);
            var buffer = (float[])expected.Clone();
            p.Pipeline.Process(buffer);
            Assert.Equal(expected, buffer);
        }
        Assert.Single(p.Events);
    }

    [Fact]
    public void A_helper_crash_after_the_swap_passes_audio_through_and_recovers_with_no_event()
    {
        using var p = new Pipe(n => n == 1 ? new[] { "--crash-after", "80:9" } : Array.Empty<string>());
        p.Pipeline.RnnEnabled = true;
        Assert.True(p.Pipeline.SelectPcEngine("nnr"));
        HelperTestRig.Until(() => p.Events.Any(e => e.Processing), "processing", step: () => p.Step());
        var engines = p.Made.ToArray();
        HelperTestRig.Until(() => engines.Any(e => !e.IsAvailable), "the crash", step: () => p.Step());
        HelperTestRig.Until(() => engines.All(e => e.IsAvailable) && p.Rig.Host.LaunchedProcessIds.Count == 2, "recovery", step: () => p.Step());
        for (int i = 0; i < 20; i++) p.Step();
        Assert.Single(p.Events);
        Assert.Equal(("nnr", "nnr"), (p.Pipeline.NeuralSlot.ChosenEngineId, p.Pipeline.NeuralSlot.RunningEngineId));
        Assert.Equal(engines.OrderBy(e => e.GetHashCode()), p.Made.OrderBy(e => e.GetHashCode())); // no engine was rebuilt
        Assert.All(engines, e => Assert.Equal(3408, e.LatencySamples));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_missing_helper_at_launch_or_mid_session_runs_nothing_reports_once_and_keeps_the_choice(bool atLaunch)
    {
        string nowhere = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jjflex-no-helper-" + Guid.NewGuid().ToString("N"), "jjflex-wdsp-helper.exe");
        using var p = new Pipe(path: nowhere);
        if (atLaunch)
        {
            var config = new AudioOutputConfig
            {
                ReceiveDenoiseVersion = 1,
                ReceiveNeural = new ReceiveDenoiseSlotConfig { EngineId = "nnr", Enabled = true },
            };
            ReceiveDenoiseConfig.Map(config, p.Pipeline, save: false);
        }
        else
        {
            p.Pipeline.RnnEnabled = true;
            Assert.True(p.Pipeline.SelectPcEngine("nnr"));
        }
        HelperTestRig.Until(() => p.Events.Any(), "the failure result", step: () => p.Pipeline.Process(Audio(0)));
        var failure = Assert.Single(p.Events);
        Assert.Equal(("nnr", false, true), (failure.EngineId, failure.Processing, failure.SlotStopped));
        Assert.Equal("nnr", p.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.Empty(p.Pipeline.NeuralSlot.RunningEngineId);
        for (int i = 0; i < 20; i++)
        {
            var expected = Audio(i);
            var buffer = (float[])expected.Clone();
            p.Pipeline.Process(buffer);
            Assert.Equal(expected, buffer);
        }
        Assert.Single(p.Events);
        Assert.Equal(Unavailability.HelperMissing, p.Rig.Host.Probe("nnr"));
        Assert.Empty(p.Rig.Host.LaunchedProcessIds);
        var saved = new AudioOutputConfig();
        ReceiveDenoiseConfig.Map(saved, p.Pipeline, save: true);
        Assert.Equal("nnr", saved.ReceiveNeural!.EngineId);
    }

    // ------------------------------------------------------------------
    // The arrow-back defect: a latency setting changed on the RUNNING engine
    // while another is chosen and warming must not leave the old instance.
    // ------------------------------------------------------------------

    private sealed class ModelEngine : IDenoiseEngine
    {
        public static readonly IReadOnlyList<EngineSettingDescriptor> Descriptors = new[] {
            new EngineSettingDescriptor("model", "label", EngineSettingKind.Choice, new EngineSettingValue(0, "standard"),
                Options: new[] { new EngineSettingOption("standard", "label"), new EngineSettingOption("premium", "label") },
                ChangesLatency: true),
        };
        public string Id { get; }
        public string BuiltWith { get; }
        public ModelEngine(string id, IReadOnlyDictionary<string, EngineSettingValue> initial)
        {
            Id = id;
            BuiltWith = initial.TryGetValue("model", out var m) ? m.Choice! : "standard";
        }
        public int SampleRate => 48000;
        public int FrameLength => 480;
        public bool IsAvailable => true;
        public int LatencySamples => BuiltWith == "premium" ? 960 : 480;
        public IReadOnlyList<EngineSettingDescriptor> Settings => Descriptors;
        public EngineSettingValue GetSetting(string name) => new(0, BuiltWith);
        public bool TrySetSetting(string name, EngineSettingValue value) => name == "model" && value.Choice == BuiltWith;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output) { input.CopyTo(output); return true; }
        public void Reset() { }
        public void Dispose() { }
    }

    [Fact]
    public void Changing_a_latency_setting_on_the_running_engine_then_arrowing_back_rebuilds_it()
    {
        var made = new ConcurrentBag<ModelEngine>();
        using var gate = new ManualResetEventSlim(true);
        IDenoiseEngine Make(string id, IReadOnlyDictionary<string, EngineSettingValue> initial)
        {
            if (id == "b") gate.Wait(5000);
            var e = new ModelEngine(id, initial); made.Add(e); return e;
        }
        DenoiseEngineEntry Row(string id) => new(id, DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { DenoiseDirection.Receive },
            initial => Make(id, initial), () => Unavailability.None, SettingDescriptors: ModelEngine.Descriptors);
        using var pipeline = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(new[] { Row("a"), Row("b") }));
        var slot = pipeline.NeuralSlot;
        pipeline.RnnEnabled = true;
        Assert.True(pipeline.SelectPcEngine("a"));
        HelperTestRig.Until(() => slot.RunningEngineId == "a", "a running", step: () => pipeline.Process(Audio(0)));
        var oldA = made.Where(e => e.Id == "a").ToArray();
        Assert.All(oldA, e => Assert.Equal("standard", e.BuiltWith));

        gate.Reset(); // b's build stays in progress: b is chosen, a is still running
        Assert.True(pipeline.SelectPcEngine("b"));
        Assert.True(slot.TrySetSetting("a", "model", new EngineSettingValue(0, "premium")));
        Assert.True(pipeline.SelectPcEngine("a")); // arrow back before b ever ran
        gate.Set();
        for (int i = 0; i < 40; i++) { pipeline.Process(Audio(i)); Thread.Sleep(2); }
        HelperTestRig.Until(() => made.Any(e => e.Id == "a" && e.BuiltWith == "premium") && slot.RunningEngineId == "a",
            "a rebuilt with the new model", step: () => pipeline.Process(Audio(0)));
        Assert.Equal("premium", slot.GetSetting("a", "model").Choice);
    }
}
