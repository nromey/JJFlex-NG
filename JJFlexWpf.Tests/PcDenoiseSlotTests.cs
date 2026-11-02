using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

public sealed class PcDenoiseSlotTests
{
    public PcDenoiseSlotTests() => NoiseReductionPerChannelTests.AssertWindowless();
    private sealed class Engine : IDenoiseEngine
    {
        public string Id { get; }
        public float Gain, LastInput;
        public double DelayApplied;
        public bool Disposed, ThrowOnFrame;
        public int Calls, Inside, DisposedInside, LateThrough;
        public ManualResetEventSlim? Entered, Release;
        public bool IsAvailable { get; set; } = true;
        public int SampleRate => 48000;
        public int FrameLength => 480;
        public int LatencySamples => 0;
        private float _amount = 1;
        public IReadOnlyList<EngineSettingDescriptor> Settings { get; } = new[] {
            new EngineSettingDescriptor("amount", "label", EngineSettingKind.Number, new(1), 0, 2, 0.1),
            new EngineSettingDescriptor("delay", "label", EngineSettingKind.Number, new(0), 0, 2, 1, ChangesLatency: true)
        };
        public Engine(string id, float gain) { Id = id; Gain = gain; }
        public EngineSettingValue GetSetting(string name) => new(_amount);
        public bool TrySetSetting(string name, EngineSettingValue value)
        {
            var descriptor = Settings.FirstOrDefault(s => s.Name == name);
            if (descriptor == null || !descriptor.Accepts(value)) return false;
            if (name == "amount") _amount = (float)value.Number;
            if (name == "delay") DelayApplied = value.Number;
            return true;
        }
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            Interlocked.Increment(ref Inside);
            try
            {
                if (Disposed) throw new InvalidOperationException("use after dispose");
                if (ThrowOnFrame) throw new InvalidOperationException("failed first frame");
                Entered?.Set(); Release?.Wait(); Calls++; LastInput = input[0];
                if (Calls <= LateThrough) return false;
                for (int i = 0; i < input.Length; i++) output[i] = input[i] * Gain * _amount;
                Assert.False(Disposed);
                return true;
            }
            finally { Interlocked.Decrement(ref Inside); }
        }
        public void Reset() { }
        public void Dispose() { if (Volatile.Read(ref Inside) != 0) Interlocked.Increment(ref DisposedInside); Disposed = true; }
    }

    private sealed class Rig : IDisposable
    {
        public readonly ConcurrentBag<Engine> Made = new();
        public RxAudioPipeline Pipeline { get; }
        public Rig(Func<string, Engine>? build = null)
        {
            IDenoiseEngine Make(string id, float gain)
            { var e = build?.Invoke(id) ?? new Engine(id, gain); Made.Add(e); return e; }
            var rows = new[] {
                Row("rnnoise", DenoiseFamily.Neural, () => Make("rnnoise", 2)),
                Row("spectral_capture", DenoiseFamily.Spectral, () => Make("spectral_capture", 3)),
                Row("a", DenoiseFamily.Neural, () => Make("a", 4)),
                Row("b", DenoiseFamily.Neural, () => Make("b", 5)),
                Row("spectral", DenoiseFamily.Spectral, () => Make("spectral", 3)),
                Row("transmit-only", DenoiseFamily.Neural, () => Make("transmit-only", 1)) with { Directions = new[] { DenoiseDirection.Transmit } },
                Row("future", DenoiseFamily.Neural, null),
                new DenoiseEngineEntry("radio", DenoiseSide.Radio, null, "label", new[] { DenoiseDirection.Receive })
            };
            Pipeline = new(catalog: new DenoiseEngineCatalog(rows));
        }
        public void Dispose() => Pipeline.Dispose();
    }
    private static DenoiseEngineEntry Row(string id, DenoiseFamily family, Func<IDenoiseEngine>? factory) =>
        new(id, DenoiseSide.Pc, family, "label", new[] { DenoiseDirection.Receive }, factory, () => Unavailability.None);
    private static float[] Audio() => Enumerable.Repeat(0.1f, 960).ToArray();
    private static void Until(Func<bool> predicate, Action? step = null)
    {
        var time = Stopwatch.StartNew();
        while (!predicate() && time.ElapsedMilliseconds < 5000) { step?.Invoke(); Thread.Sleep(1); }
        Assert.True(predicate(), "condition did not become true");
    }
    private static void RunUntil(RxAudioPipeline p, string id, DenoiseFamily family = DenoiseFamily.Neural) =>
        Until(() => (family == DenoiseFamily.Neural ? p.NeuralSlot : p.SpectralSlot).RunningEngineId == id,
            () => p.Process(Audio()));

    [Theory]
    [InlineData("missing", "unknown-engine")]
    [InlineData("radio", "radio-engine")]
    [InlineData("future", "not-in-build")]
    [InlineData("transmit-only", "wrong-direction")]
    public void Bad_choices_are_refused_with_a_reason_and_leave_intent_alone(string id, string reason)
    {
        using var rig = new Rig();
        Assert.False(rig.Pipeline.SelectPcEngine(id, out var refusal));
        Assert.Equal(reason, refusal);
        Assert.Equal("rnnoise", rig.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.False(rig.Pipeline.NeuralSlot.Select("spectral", out var wrong));
        Assert.Equal("wrong-family", wrong);
    }

    [Theory]
    [InlineData(false, false, 0.1f)] [InlineData(true, false, 0.3f)]
    [InlineData(false, true, 0.2f)] [InlineData(true, true, 0.6f)]
    public void Two_slots_run_in_order_or_alone_or_pass_through(bool spectral, bool neural, float expected)
    {
        using var rig = new Rig(); var p = rig.Pipeline;
        p.SelectPcEngine("spectral"); p.SpectralEnabled = spectral; p.RnnEnabled = neural;
        p.NeuralSlot.SetStrength("rnnoise", 1);
        if (spectral) RunUntil(p, "spectral", DenoiseFamily.Spectral);
        var audio = Audio(); p.Process(audio);
        Assert.All(audio, value => Assert.Equal(expected, value, 5));
        if (spectral && neural) Assert.All(rig.Made.Where(e => e.Id == "rnnoise"), e => Assert.Equal(0.3f, e.LastInput, 5));
        if (!spectral && !neural) Assert.All(rig.Made, e => Assert.Equal(0, e.Calls));
    }

    [Fact]
    public void Second_switch_during_warmup_keeps_last_choice_and_only_announces_the_winner_off_audio()
    {
        using var rig = new Rig(); var p = rig.Pipeline;
        p.RnnEnabled = true;
        p.Process(Audio()); Thread.Sleep(20);
        var events = new ConcurrentQueue<DenoiseSelectionResult>();
        int audioThread = Environment.CurrentManagedThreadId, eventThread = audioThread;
        p.SelectionResult += (_, e) => { eventThread = Environment.CurrentManagedThreadId; events.Enqueue(e); };
        p.SelectPcEngine("a");
        Until(() => p.NeuralSlot.SettingsFor("a").Count > 0); Thread.Sleep(10);
        for (int i = 0; i < 3; i++) p.Process(Audio());
        Assert.Equal("rnnoise", p.NeuralSlot.RunningEngineId);
        p.SelectPcEngine("b"); RunUntil(p, "b");
        Until(() => events.Any(e => e.EngineId == "b"));
        Assert.DoesNotContain(events, e => e.EngineId == "a");
        Assert.Single(events.Where(e => e.EngineId == "b"));
        Assert.NotEqual(audioThread, eventThread);
        Until(() => rig.Made.Where(e => e.Id == "a").All(e => e.Disposed));
        Assert.All(rig.Made, e => Assert.Equal(0, e.DisposedInside));
    }

    [Fact]
    public void Arrow_away_and_back_keeps_running_state_and_abandons_the_warmer()
    {
        using var rig = new Rig(); var p = rig.Pipeline; p.RnnEnabled = true;
        p.Process(Audio()); Thread.Sleep(20);
        var initial = rig.Made.Where(e => e.Id == "rnnoise").ToArray();
        p.SelectPcEngine("a"); Until(() => p.NeuralSlot.SettingsFor("a").Count > 0); Thread.Sleep(10);
        p.Process(Audio());
        var events = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => events.Enqueue(e);
        p.SelectPcEngine("rnnoise"); p.Process(Audio());
        Until(() => events.Count == 1);
        Assert.Equal("rnnoise", events.Single().EngineId);
        Assert.All(initial, e => Assert.False(e.Disposed));
        Until(() => rig.Made.Where(e => e.Id == "a").All(e => e.Disposed));
    }

    [Fact]
    public void Overtaken_and_disconnected_builds_dispose_when_their_factories_finish()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var rig = new Rig(id => { if (id == "a") { entered.Set(); release.Wait(); } return new Engine(id, 1); });
        var p = rig.Pipeline; p.RnnEnabled = true; p.SelectPcEngine("a"); Assert.True(entered.Wait(3000));
        p.SelectPcEngine("b"); RunUntil(p, "b"); release.Set();
        Until(() => rig.Made.Count(e => e.Id == "a") == 2);
        Until(() => rig.Made.Where(e => e.Id == "a").All(e => e.Disposed));
        p.Dispose();
        Assert.All(rig.Made, e => Assert.Equal(0, e.DisposedInside));
    }

    [Fact]
    public void Failed_start_keeps_chosen_and_running_separate_and_reports_failure()
    {
        using var rig = new Rig(id => new Engine(id, 1) { IsAvailable = id != "a" });
        var p = rig.Pipeline; var events = new ConcurrentQueue<DenoiseSelectionResult>();
        p.SelectionResult += (_, e) => events.Enqueue(e);
        p.SelectPcEngine("a"); Until(() => events.Any(e => e.EngineId == "a" && !e.Processing));
        Assert.Equal("a", p.NeuralSlot.ChosenEngineId);
        Assert.Equal("rnnoise", p.NeuralSlot.RunningEngineId);
        p.RnnEnabled = true; p.Process(Audio());
        Assert.DoesNotContain(events, e => e.EngineId == "a" && e.Processing);
    }

    [Fact]
    public void Failed_first_frame_reports_start_failure_and_retires_the_candidate()
    {
        using var rig = new Rig(id => new Engine(id, 1) { ThrowOnFrame = id == "a" });
        var p = rig.Pipeline; p.RnnEnabled = true;
        var events = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => events.Enqueue(e);
        p.SelectPcEngine("a");
        Until(() => events.Any(e => e.EngineId == "a" && !e.Processing), () => p.Process(Audio()));
        Assert.Equal("a", p.NeuralSlot.ChosenEngineId);
        Assert.Equal("rnnoise", p.NeuralSlot.RunningEngineId);
        Until(() => rig.Made.Where(e => e.Id == "a").All(e => e.Disposed));
        Assert.DoesNotContain(events, e => e.EngineId == "a" && e.Processing);
    }

    [Fact]
    public void Expected_late_startup_frames_keep_warming_without_a_failure_result()
    {
        using var rig = new Rig(id => new Engine(id, 1) { LateThrough = id == "a" ? 5 : 0 });
        var p = rig.Pipeline; p.RnnEnabled = true;
        var events = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => events.Enqueue(e);
        p.SelectPcEngine("a"); Until(() => p.NeuralSlot.SettingsFor("a").Count > 0); Thread.Sleep(10);
        for (int i = 0; i < 3; i++) p.Process(Audio());
        Assert.Equal("rnnoise", p.NeuralSlot.RunningEngineId);
        Assert.DoesNotContain(events, e => e.EngineId == "a");
        RunUntil(p, "a"); Until(() => events.Any(e => e.EngineId == "a" && e.Processing));
        Assert.DoesNotContain(events, e => e.EngineId == "a" && !e.Processing);
    }

    [Fact]
    public void An_external_callback_cannot_hold_the_disconnect_lock()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var rig = new Rig(); var p = rig.Pipeline; p.RnnEnabled = true;
        p.SelectionResult += (_, _) => { entered.Set(); release.Wait(); };
        p.Process(Audio()); Assert.True(entered.Wait(3000));
        var disposing = Task.Run(() => p.Dispose());
        try { Assert.True(disposing.Wait(200), "disconnect waited for a callback that may itself be waiting for the UI"); }
        finally { release.Set(); Assert.True(disposing.Wait(3000)); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Disabled_or_mode_held_slot_waits_for_real_audio_before_warming_or_announcing(bool heldByMode)
    {
        using var rig = new Rig(); var p = rig.Pipeline;
        p.RnnEnabled = heldByMode; if (heldByMode) p.SetCurrentMode("DIGU");
        var events = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => events.Enqueue(e);
        p.SelectPcEngine("a"); Until(() => p.NeuralSlot.SettingsFor("a").Count > 0); Thread.Sleep(10);
        for (int i = 0; i < 20; i++) p.Process(Audio());
        Assert.Empty(events); Assert.All(rig.Made.Where(e => e.Id == "a"), e => Assert.Equal(0, e.Calls));
        p.SetCurrentMode("SSB"); p.RnnEnabled = true; RunUntil(p, "a");
        Until(() => events.Any(e => e.EngineId == "a" && e.Processing));
    }

    [Fact]
    public void No_buffers_and_turning_off_mid_warmup_leave_the_swap_pending()
    {
        using var rig = new Rig(); var p = rig.Pipeline; p.RnnEnabled = true;
        p.SelectPcEngine("a"); Until(() => p.NeuralSlot.SettingsFor("a").Count > 0); Thread.Sleep(10);
        Assert.All(rig.Made.Where(e => e.Id == "a"), e => Assert.Equal(0, e.Calls));
        p.Process(Audio()); p.RnnEnabled = false;
        for (int i = 0; i < 20; i++) p.Process(Audio());
        Assert.Equal("rnnoise", p.NeuralSlot.RunningEngineId);
        p.RnnEnabled = true; RunUntil(p, "a");
    }

    [Fact]
    public void Strength_and_named_values_follow_each_engine_and_changes_during_warmup_reach_both_channels()
    {
        using var rig = new Rig(); var p = rig.Pipeline; p.RnnEnabled = true;
        p.SelectPcEngine("a"); Until(() => p.NeuralSlot.SettingsFor("a").Count > 0); Thread.Sleep(10);
        p.Process(Audio());
        Assert.True(p.NeuralSlot.TrySetSetting("a", "amount", new(0.5)));
        p.NeuralSlot.SetStrength("a", 0.5f);
        RunUntil(p, "a");
        var audio = Audio(); p.Process(audio); Assert.All(audio, x => Assert.Equal(0.15f, x, 5));
        Assert.All(rig.Made.Where(e => e.Id == "a"), e => Assert.Equal(0.5, e.GetSetting("amount").Number));
        p.SelectPcEngine("b"); RunUntil(p, "b"); p.SelectPcEngine("a"); RunUntil(p, "a");
        Assert.Equal(0.5f, p.NeuralSlot.GetStrength("a"));
        audio = Audio(); p.Process(audio); Assert.All(audio, x => Assert.Equal(0.15f, x, 5));
    }

    [Fact]
    public void Switching_one_family_preserves_the_other_and_latency_changes_build_a_replacement()
    {
        using var rig = new Rig(); var p = rig.Pipeline;
        p.RnnEnabled = true; p.SpectralEnabled = true;
        p.SelectPcEngine("spectral"); RunUntil(p, "spectral", DenoiseFamily.Spectral);
        var spectral = rig.Made.Where(e => e.Id == "spectral").ToArray();
        p.SelectPcEngine("a"); RunUntil(p, "a");
        Assert.All(spectral, e => { Assert.False(e.Disposed); Assert.Equal(0.1f, e.LastInput, 5); });
        var previous = rig.Made.Where(e => e.Id == "a").ToArray();
        Assert.True(p.NeuralSlot.TrySetSetting("a", "delay", new(1)));
        Until(() => rig.Made.Count(e => e.Id == "a") == 4);
        p.Process(Audio()); // new bank warms; the old bank must retain its old delay
        Assert.All(previous, e => Assert.Equal(0, e.DelayApplied));
        Until(() => previous.All(e => e.Disposed), () => p.Process(Audio()));
        Assert.All(rig.Made.Where(e => e.Id == "a" && !previous.Contains(e)), e => Assert.Equal(1, e.DelayApplied));
        Assert.All(spectral, e => Assert.False(e.Disposed));
    }

    [Fact]
    public void Pipeline_dispose_during_a_build_frees_the_completed_build_without_an_event()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var rig = new Rig(id => { if (id == "a") { entered.Set(); release.Wait(); } return new Engine(id, 1); });
        var p = rig.Pipeline;
        var events = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => events.Enqueue(e);
        p.SelectPcEngine("a"); Assert.True(entered.Wait(3000));
        p.Dispose(); release.Set();
        Until(() => rig.Made.Count(e => e.Id == "a") == 2);
        Until(() => rig.Made.Where(e => e.Id == "a").All(e => e.Disposed));
        Assert.Empty(events);
    }

    [Fact]
    public void A_bank_disposes_already_built_channels_when_a_later_factory_throws()
    {
        var first = new Engine("first", 1); int count = 0;
        Assert.Throws<InvalidOperationException>(() => new PerChannelDenoiserBank(() =>
            ++count == 1 ? first : throw new InvalidOperationException("failed second channel"), 2));
        Assert.True(first.Disposed);
    }

    [Fact]
    public void Dispose_waits_for_audio_and_process_after_dispose_is_untouched()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var rig = new Rig(); var p = rig.Pipeline; p.RnnEnabled = true;
        var first = rig.Made.First(e => e.Id == "rnnoise"); first.Entered = entered; first.Release = release;
        var audio = Task.Run(() => p.Process(Audio())); Assert.True(entered.Wait(3000));
        var unblock = Task.Run(() => { Thread.Sleep(40); release.Set(); });
        p.Dispose(); Assert.True(audio.Wait(3000)); unblock.Wait();
        Assert.All(rig.Made, e => { Assert.True(e.Disposed); Assert.Equal(0, e.DisposedInside); });
        var untouched = Audio(); p.Process(untouched); Assert.Equal(Audio(), untouched);
    }

    [Fact]
    public void Dispose_timeout_leaks_instead_of_freeing_a_live_native_state()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var rig = new Rig(); var p = rig.Pipeline; p.RnnEnabled = true;
        var first = rig.Made.First(e => e.Id == "rnnoise"); first.Entered = entered; first.Release = release;
        var audio = Task.Run(() => p.Process(Audio())); Assert.True(entered.Wait(3000));
        try { p.Dispose(); Assert.False(first.Disposed); }
        finally { release.Set(); Assert.True(audio.Wait(3000)); }
        Assert.Equal(0, first.DisposedInside);
    }

    [Fact]
    public void Real_native_engines_survive_repeated_background_swaps_and_disconnect()
    {
        string lib = Path.Combine(RepoPaths.RequireRoot(), "runtimes/win-x64/native/deepfilter.dll");
        var defaults = new DenoiseEngineCatalog();
        var catalog = new DenoiseEngineCatalog(defaults.Entries.Select(e => e.Id == "deepfilternet3"
            ? e with { Factory = () => new DeepFilterNet3Engine(9, lib) } : e));
        using var p = new RxAudioPipeline(catalog: catalog); p.RnnEnabled = true;
        using var stop = new CancellationTokenSource();
        var processing = Task.Run(() => { while (!stop.IsCancellationRequested) { p.Process(Audio()); Thread.Sleep(1); } });
        try
        {
            for (int i = 0; i < 10; i++)
            {
                string id = i % 2 == 0 ? "deepfilternet3" : "rnnoise";
                Assert.True(p.SelectPcEngine(id)); Until(() => p.NeuralSlot.RunningEngineId == id);
            }
            p.SelectPcEngine("deepfilternet3"); p.Dispose();
        }
        finally { stop.Cancel(); Assert.True(processing.Wait(5000)); }
    }
}
