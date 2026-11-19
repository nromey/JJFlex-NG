using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// The receive pipeline's keep-up timing (Sprint 49 Track L, #866), with fake
/// engines: a deliberately slow one in this process, and fake helper engines
/// that return false, each raising the warning, beside the fast engines that
/// do not. The audio thread allocates nothing for it, and the event is raised
/// off that thread.
/// </summary>
public sealed class EngineKeepUpPipelineTests
{
    private readonly ITestOutputHelper _out;
    public EngineKeepUpPipelineTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    /// <summary>A fake engine: copies its input, optionally spending time doing it, optionally answering late.</summary>
    internal sealed class Engine : IDenoiseEngine
    {
        private readonly Func<double> _spinMs;
        private readonly Func<bool> _late;
        public Engine(string id, Func<double>? spinMs = null, Func<bool>? late = null)
        { Id = id; _spinMs = spinMs ?? (() => 0); _late = late ?? (() => false); }
        public string Id { get; }
        public int SampleRate => 48000;
        public int FrameLength => 480;
        public bool IsAvailable => true;
        public int LatencySamples => 0;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            double ms = _spinMs();
            if (ms > 0)
            {
                long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
                while (Stopwatch.GetTimestamp() < until) { }
            }
            input.CopyTo(output);
            return !_late();
        }
        public void Reset() { }
        public void Dispose() { }
    }

    private static DenoiseEngineEntry Row(string id, DenoiseFamily family, Func<IDenoiseEngine> make) =>
        new(id, DenoiseSide.Pc, family, "audio.denoise." + id, new[] { DenoiseDirection.Receive },
            (_, _) => make(), () => Unavailability.None);

    internal sealed class Rig : IDisposable
    {
        public RxAudioPipeline Pipeline { get; }
        public ConcurrentQueue<(EngineKeepUpResult Result, int Thread)> Results { get; } = new();
        public double SlowMs = 6;          // per frame per channel: 12 ms for a 10 ms stereo buffer
        public volatile bool HelperLate;   // the fake helper's answers stop coming back in time
        private readonly float[] _audio = new float[960];

        public Rig()
        {
            var rows = new[]
            {
                Row("rnnoise", DenoiseFamily.Neural, () => new Engine("rnnoise")),
                Row("fast", DenoiseFamily.Neural, () => new Engine("fast")),
                Row("slow", DenoiseFamily.Neural, () => new Engine("slow", () => SlowMs)),
                Row("fakehelper", DenoiseFamily.Neural, () => new Engine("fakehelper", late: () => HelperLate)),
                Row("stuckhelper", DenoiseFamily.Neural, () => new Engine("stuckhelper", late: () => true)),
                Row("spectral_capture", DenoiseFamily.Spectral, () => new Engine("spectral_capture")),
                Row("fastspectral", DenoiseFamily.Spectral, () => new Engine("fastspectral")),
            };
            Pipeline = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(rows))
            {
                IsHelperEngine = id => id is "fakehelper" or "stuckhelper",
            };
            Pipeline.KeepUpResult += (_, r) => Results.Enqueue((r, Environment.CurrentManagedThreadId));
        }

        public void Pump(int buffers = 1)
        {
            for (int i = 0; i < buffers; i++) { Array.Fill(_audio, 0.1f); Pipeline.Process(_audio); }
        }

        public void Choose(string id)
        {
            Assert.True(Pipeline.SelectPcEngine(id, out var refusal), refusal);
            if (Pipeline.NeuralSlot.ChosenEngineId == id) Pipeline.RnnEnabled = true; else Pipeline.SpectralEnabled = true;
        }

        /// <summary>Feed buffers until the slot runs the chosen engine.</summary>
        public void UntilRunning(string id, DenoiseFamily family = DenoiseFamily.Neural)
        {
            var slot = family == DenoiseFamily.Neural ? Pipeline.NeuralSlot : Pipeline.SpectralSlot;
            var t = Stopwatch.StartNew();
            while (slot.ProcessingEngineId != id && t.ElapsedMilliseconds < 5000) { Pump(); Thread.Sleep(1); }
            Assert.Equal(id, slot.ProcessingEngineId);
        }

        /// <summary>Feed buffers until a change for this engine arrives, or the limit; true when it did.</summary>
        public bool PumpUntil(string id, EngineKeepUpChange change, int maxBuffers)
        {
            for (int i = 0; i < maxBuffers; i++)
            {
                Pump();
                if (Has(id, change)) return true;
            }
            var t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < 500) { if (Has(id, change)) return true; Thread.Sleep(5); }
            return false;
        }

        public bool Has(string id, EngineKeepUpChange change) => Results.Any(r => r.Result.EngineId == id && r.Result.Change == change);

        public void Dispose() => Pipeline.Dispose();
    }

    [Fact]
    public void A_deliberately_slow_engine_in_this_process_raises_fell_behind_and_a_fast_one_does_not()
    {
        // Positive control.
        using (var slow = new Rig())
        {
            slow.Choose("slow"); slow.UntilRunning("slow");
            Assert.True(slow.PumpUntil("slow", EngineKeepUpChange.FellBehind, 200), "the slow engine was never said to fall behind");
            var r = slow.Results.First(x => x.Result.Change == EngineKeepUpChange.FellBehind).Result;
            _out.WriteLine(r.Describe());
            Assert.Equal(DenoiseFamily.Neural, r.Family);
            Assert.False(r.Helper);
            Assert.True(r.AverageMs > r.BufferMs, r.Describe());
            Assert.DoesNotContain(slow.Results, x => x.Result.Change == EngineKeepUpChange.KeepingUp);
        }
        // The negative, for at least as long.
        using var fast = new Rig();
        fast.Choose("fast"); fast.UntilRunning("fast");
        Assert.True(fast.PumpUntil("fast", EngineKeepUpChange.KeepingUp, 300), "a fast engine was never said to keep up");
        fast.Pump(300);
        Thread.Sleep(100);
        Assert.DoesNotContain(fast.Results, x => x.Result.Change == EngineKeepUpChange.FellBehind);
    }

    [Fact]
    public void A_fake_helper_engine_whose_answers_come_back_late_raises_fell_behind_and_one_that_keeps_up_does_not()
    {
        using var rig = new Rig();
        rig.Choose("fakehelper"); rig.UntilRunning("fakehelper");
        // On time for longer than the grace and a whole window: keeps up, nothing else.
        Assert.True(rig.PumpUntil("fakehelper", EngineKeepUpChange.KeepingUp, 300));
        rig.Pump(200);
        Assert.False(rig.Has("fakehelper", EngineKeepUpChange.FellBehind));
        // Its answers stop coming back in time: late frames on the running bank.
        rig.HelperLate = true;
        Assert.True(rig.PumpUntil("fakehelper", EngineKeepUpChange.FellBehind, 60), "late helper answers were never said to fall behind");
        var r = rig.Results.First(x => x.Result.Change == EngineKeepUpChange.FellBehind).Result;
        Assert.True(r.Helper, r.Describe());
        Assert.Equal(EngineKeepUpRule.DefaultFallAt, r.LateBuffers);
        // It keeps running: lateness is reported, nothing is stopped or switched.
        Assert.Equal("fakehelper", rig.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.Equal("fakehelper", rig.Pipeline.NeuralSlot.RunningEngineId);
        Assert.True(rig.Pipeline.RnnEnabled);
    }

    [Fact]
    public void A_fake_helper_engine_too_slow_ever_to_finish_its_warm_up_raises_fell_behind()
    {
        using var rig = new Rig();
        rig.Choose("stuckhelper");
        Assert.True(rig.PumpUntil("stuckhelper", EngineKeepUpChange.FellBehind, EngineKeepUpRule.DefaultStuckWarmup + 100),
            "a helper engine that never warmed up was never said to fall behind");
        Assert.NotEqual("stuckhelper", rig.Pipeline.NeuralSlot.RunningEngineId);
        Assert.Equal("stuckhelper", rig.Pipeline.NeuralSlot.ChosenEngineId);
        // The same warm-up time for an engine that warms up normally says nothing of the kind.
        using var ok = new Rig();
        ok.Choose("fakehelper");
        ok.Pump(EngineKeepUpRule.DefaultStuckWarmup + 100);
        Thread.Sleep(100);
        Assert.False(ok.Has("fakehelper", EngineKeepUpChange.FellBehind));
    }

    [Fact]
    public void A_pause_in_the_stream_restarts_the_grace_so_lateness_straight_after_it_is_start_up()
    {
        // Positive control: forty late buffers with no pause, after the engine settled, fall behind.
        using (var control = new Rig())
        {
            control.Choose("fakehelper"); control.UntilRunning("fakehelper");
            control.Pump(EngineKeepUpRule.DefaultGrace + 10);
            control.HelperLate = true;
            Assert.True(control.PumpUntil("fakehelper", EngineKeepUpChange.FellBehind, 40));
        }
        using var rig = new Rig();
        rig.Choose("fakehelper"); rig.UntilRunning("fakehelper");
        rig.Pump(EngineKeepUpRule.DefaultGrace + 10);
        Thread.Sleep((int)(RxAudioPipeline.KeepUpGapTicks * 1000 / Stopwatch.Frequency) + 50);
        rig.HelperLate = true;
        rig.Pump(40);
        rig.HelperLate = false;
        rig.Pump(200);
        Thread.Sleep(100);
        Assert.False(rig.Has("fakehelper", EngineKeepUpChange.FellBehind));
    }

    [Fact]
    public void An_engine_switch_is_not_counted_and_the_new_engine_starts_its_own_count()
    {
        using var rig = new Rig();
        rig.Choose("fast"); rig.UntilRunning("fast");
        Assert.True(rig.PumpUntil("fast", EngineKeepUpChange.KeepingUp, 300));
        rig.Choose("fastspectral");
        rig.UntilRunning("fastspectral", DenoiseFamily.Spectral);
        Assert.True(rig.PumpUntil("fastspectral", EngineKeepUpChange.KeepingUp, 300));
        Assert.Equal("fast", rig.Pipeline.KeepUpRuleFor(DenoiseFamily.Neural).EngineId);
        Assert.Equal("fastspectral", rig.Pipeline.KeepUpRuleFor(DenoiseFamily.Spectral).EngineId);
        Assert.Equal(DenoiseFamily.Spectral, rig.Results.Last().Result.Family);
    }

    [Fact]
    public void The_event_is_raised_off_the_audio_thread()
    {
        using var rig = new Rig();
        rig.Choose("fast"); rig.UntilRunning("fast");
        Assert.True(rig.PumpUntil("fast", EngineKeepUpChange.KeepingUp, 300));
        Assert.All(rig.Results, r => Assert.NotEqual(Environment.CurrentManagedThreadId, r.Thread));
    }

    [Fact]
    public void Process_allocates_nothing_while_it_times_both_slots_including_a_fall_behind()
    {
        using var rig = new Rig();
        rig.Choose("fastspectral"); rig.UntilRunning("fastspectral", DenoiseFamily.Spectral);
        rig.Choose("fakehelper"); rig.UntilRunning("fakehelper");
        Assert.True(rig.PumpUntil("fakehelper", EngineKeepUpChange.KeepingUp, 300));
        rig.Pump(100);
        var audio = new float[960];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++) { Array.Fill(audio, 0.1f); rig.Pipeline.Process(audio); }
        long steady = GC.GetAllocatedBytesForCurrentThread() - before;
        rig.HelperLate = true;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { Array.Fill(audio, 0.1f); rig.Pipeline.Process(audio); }
        long falling = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"Managed bytes on the audio thread: {steady} over 300 buffers keeping up, {falling} over 100 buffers falling behind.");
        Assert.True(rig.PumpUntil("fakehelper", EngineKeepUpChange.FellBehind, 0), "the fall was not raised");
        Assert.Equal(0, steady);
        Assert.Equal(0, falling);
    }
}
