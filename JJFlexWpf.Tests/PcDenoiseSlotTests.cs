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
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public PcDenoiseSlotTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _output = output;
    }
    private sealed class Engine : IDenoiseEngine
    {
        public string Id { get; }
        public float Gain, LastInput;
        public double DelayApplied;
        public bool Disposed, ThrowOnFrame, RefuseLiveDelay;
        public int Calls, Inside, DisposedInside, LateThrough;
        public ManualResetEventSlim? Entered, Release;
        public bool IsAvailable { get; set; } = true;
        public int SampleRate => 48000;
        public int FrameLength => 480;
        public int LatencySamples => 0;
        private float _amount = 1;
        public static IReadOnlyList<EngineSettingDescriptor> Descriptors { get; } = new[] {
            new EngineSettingDescriptor("amount", "label", EngineSettingKind.Number, new(1), 0, 2, 0.1),
            new EngineSettingDescriptor("delay", "label", EngineSettingKind.Number, new(0), 0, 2, 1, ChangesLatency: true)
        };
        public IReadOnlyList<EngineSettingDescriptor> Settings => Descriptors;
        public Engine(string id, float gain, IReadOnlyDictionary<string, EngineSettingValue>? initial = null)
        {
            Id = id; Gain = gain;
            if (initial != null) {
                if (initial.TryGetValue("amount", out var amount)) _amount = (float)amount.Number;
                if (initial.TryGetValue("delay", out var delay)) DelayApplied = delay.Number;
            }
        }
        public EngineSettingValue GetSetting(string name) => name == "delay" ? new(DelayApplied) : new(_amount);
        public bool TrySetSetting(string name, EngineSettingValue value)
        {
            var descriptor = Settings.FirstOrDefault(s => s.Name == name);
            if (descriptor == null || !descriptor.Accepts(value)) return false;
            if (name == "amount") _amount = (float)value.Number;
            if (name == "delay") { if (RefuseLiveDelay) throw new InvalidOperationException("latency must be set at construction"); DelayApplied = value.Number; }
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
        public Rig(Func<string, Engine>? build = null, Func<string, IReadOnlyDictionary<string, EngineSettingValue>, Engine>? buildStarting = null)
        {
            IDenoiseEngine Make(string id, float gain, IReadOnlyDictionary<string, EngineSettingValue> initial)
            { var e = buildStarting?.Invoke(id, initial) ?? build?.Invoke(id) ?? new Engine(id, gain, initial); Made.Add(e); return e; }
            var rows = new[] {
                Row("rnnoise", DenoiseFamily.Neural, initial => Make("rnnoise", 2, initial)),
                Row("spectral_capture", DenoiseFamily.Spectral, initial => Make("spectral_capture", 3, initial)) with { SettingDescriptors = SpectralCaptureEngine.SettingDescriptors },
                Row("a", DenoiseFamily.Neural, initial => Make("a", 4, initial)),
                Row("b", DenoiseFamily.Neural, initial => Make("b", 5, initial)),
                Row("c", DenoiseFamily.Neural, initial => Make("c", 6, initial)),
                Row("variant", DenoiseFamily.Neural, initial => Make("variant", 4, initial)),
                Row("spectral", DenoiseFamily.Spectral, initial => Make("spectral", 3, initial)),
                Row("transmit-only", DenoiseFamily.Neural, initial => Make("transmit-only", 1, initial)) with { Directions = new[] { DenoiseDirection.Transmit } },
                Row("future", DenoiseFamily.Neural, null),
                new DenoiseEngineEntry("radio", DenoiseSide.Radio, null, "label", new[] { DenoiseDirection.Receive })
            };
            Pipeline = new(catalog: new DenoiseEngineCatalog(rows));
        }
        public void Dispose() => Pipeline.Dispose();
    }
    private static DenoiseEngineEntry Row(string id, DenoiseFamily family, Func<IReadOnlyDictionary<string, EngineSettingValue>, IDenoiseEngine>? factory) =>
        new(id, DenoiseSide.Pc, family, "label", new[] { DenoiseDirection.Receive }, factory, () => Unavailability.None, SettingDescriptors: Engine.Descriptors);
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
        Until(() => p.NeuralSlot.BuildDelivered("a"));
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
        p.SelectPcEngine("a"); Until(() => p.NeuralSlot.BuildDelivered("a"));
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
    public void Failed_start_keeps_chosen_stops_running_and_reports_failure()
    {
        using var rig = new Rig(id => new Engine(id, 1) { IsAvailable = id != "a" });
        var p = rig.Pipeline; var events = new ConcurrentQueue<DenoiseSelectionResult>();
        p.SelectionResult += (_, e) => events.Enqueue(e);
        p.SelectPcEngine("a"); Until(() => events.Any(e => e.EngineId == "a" && !e.Processing), () => p.Process(Audio()));
        Assert.Equal("a", p.NeuralSlot.ChosenEngineId);
        Assert.Empty(p.NeuralSlot.RunningEngineId);
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
        Assert.Empty(p.NeuralSlot.RunningEngineId);
        Until(() => rig.Made.Where(e => e.Id == "a").All(e => e.Disposed));
        Assert.DoesNotContain(events, e => e.EngineId == "a" && e.Processing);
    }

    [Fact]
    public void Expected_late_startup_frames_keep_warming_without_a_failure_result()
    {
        using var rig = new Rig(id => new Engine(id, 1) { LateThrough = id == "a" ? 5 : 0 });
        var p = rig.Pipeline; p.RnnEnabled = true;
        var events = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => events.Enqueue(e);
        p.SelectPcEngine("a"); Until(() => p.NeuralSlot.BuildDelivered("a"));
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
        p.SelectPcEngine("a"); Until(() => p.NeuralSlot.BuildDelivered("a"));
        for (int i = 0; i < 20; i++) p.Process(Audio());
        Assert.Empty(events); Assert.All(rig.Made.Where(e => e.Id == "a"), e => Assert.Equal(0, e.Calls));
        p.SetCurrentMode("SSB"); p.RnnEnabled = true; RunUntil(p, "a");
        Until(() => events.Any(e => e.EngineId == "a" && e.Processing));
    }

    [Fact]
    public void No_buffers_and_turning_off_mid_warmup_leave_the_swap_pending()
    {
        using var rig = new Rig(); var p = rig.Pipeline; p.RnnEnabled = true;
        p.SelectPcEngine("a"); Until(() => p.NeuralSlot.BuildDelivered("a"));
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
        p.SelectPcEngine("a"); Until(() => p.NeuralSlot.BuildDelivered("a"));
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
    public void An_unavailable_constructor_default_is_not_running_and_the_same_saved_choice_can_retry()
    {
        bool fail = true;
        using var rig = new Rig(id => new Engine(id, 2) { IsAvailable = id != "rnnoise" || !Volatile.Read(ref fail) });
        var p = rig.Pipeline; p.RnnEnabled = true;
        Assert.Empty(p.NeuralSlot.RunningEngineId);
        var events = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => events.Enqueue(e);
        Assert.True(p.SelectPcEngine("rnnoise"));
        Until(() => events.Any(e => !e.Processing), () => p.Process(Audio()));
        Assert.Empty(p.NeuralSlot.RunningEngineId);
        Assert.True(events.Single().SlotStopped);
        Assert.Equal(4, rig.Made.Count(e => e.Id == "rnnoise"));
        var audio = Audio(); p.Process(audio); Assert.Equal(Audio(), audio);
        Volatile.Write(ref fail, false);
        Assert.True(p.SelectPcEngine("rnnoise")); RunUntil(p, "rnnoise");
        Until(() => events.Any(e => e.Processing));
        Assert.Equal(6, rig.Made.Count(e => e.Id == "rnnoise"));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Failed_saved_choice_stops_only_its_slot_preserves_settings_and_can_be_selected_again(bool enabled)
    {
        bool fail = true;
        using var rig = new Rig(id => new Engine(id, 2) { IsAvailable = id != "a" || !Volatile.Read(ref fail) });
        var p = rig.Pipeline;
        var config = new AudioOutputConfig {
            ReceiveDenoiseVersion = 1,
            ReceiveNeural = new ReceiveDenoiseSlotConfig {
                EngineId = "a", Enabled = enabled,
                Engines = { new DenoiseEngineSettings { EngineId = "a", Direction = DenoiseDirection.Receive,
                    Strength = 0.4f, Values = { new DenoiseNamedValue { Name = "amount", Number = 0.5 } } } }
            }
        };
        var events = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => events.Enqueue(e);
        ReceiveDenoiseConfig.Map(config, p, save: false);
        var before = new AudioOutputConfig(); ReceiveDenoiseConfig.Map(before, p, save: true);
        string Snapshot(ReceiveDenoiseSlotConfig value) {
            using var text = new StringWriter();
            new System.Xml.Serialization.XmlSerializer(typeof(ReceiveDenoiseSlotConfig)).Serialize(text, value);
            return text.ToString();
        }
        string saved = Snapshot(before.ReceiveNeural);
        Until(() => events.Any(e => e.EngineId == "a" && !e.Processing), () => p.Process(Audio()));
        Assert.Empty(p.NeuralSlot.RunningEngineId);
        Assert.Equal("a", p.NeuralSlot.ChosenEngineId); Assert.Equal(enabled, p.RnnEnabled);
        Assert.True(events.Single(e => e.EngineId == "a").SlotStopped);
        Assert.Equal("spectral_capture", p.SpectralSlot.RunningEngineId);
        Assert.All(rig.Made.Where(e => e.Id == "spectral_capture"), e => Assert.False(e.Disposed));
        for (int i = 0; i < 100; i++) {
            var audio = Audio(); p.Process(audio); Assert.Equal(Audio(), audio);
        }
        Assert.Equal(2, rig.Made.Count(e => e.Id == "a"));
        Assert.Single(events.Where(e => e.EngineId == "a"));
        var after = new AudioOutputConfig(); ReceiveDenoiseConfig.Map(after, p, save: true);
        Assert.Equal(saved, Snapshot(after.ReceiveNeural));
        Volatile.Write(ref fail, false); p.RnnEnabled = true;
        Assert.True(p.SelectPcEngine("a")); RunUntil(p, "a");
        Until(() => events.Any(e => e.EngineId == "a" && e.Processing));
        Assert.Equal(4, rig.Made.Count(e => e.Id == "a"));
        Assert.Single(events.Where(e => e.EngineId == "a" && e.Processing));
    }

    [Fact]
    public void Latency_settings_reach_the_factory_and_never_a_constructed_channel()
    {
        using var rig = new Rig(buildStarting: (id, initial) => new Engine(id, 1, initial) { RefuseLiveDelay = true });
        var p = rig.Pipeline; p.RnnEnabled = true;
        var events = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => events.Enqueue(e);
        Assert.True(p.NeuralSlot.TrySetSetting("a", "delay", new(1)));
        Assert.DoesNotContain(rig.Made, e => e.Id == "a");
        p.SelectPcEngine("a");
        Until(() => p.NeuralSlot.BuildDelivered("a") || events.Any(e => e.EngineId == "a" && !e.Processing));
        Assert.DoesNotContain(events, e => !e.Processing);
        RunUntil(p, "a");
        var first = rig.Made.Where(e => e.Id == "a").ToArray(); Assert.Equal(2, first.Length);
        Assert.All(first, e => Assert.Equal(1, e.DelayApplied));
        Assert.True(p.NeuralSlot.TrySetSetting("a", "delay", new(2)));
        Until(() => p.NeuralSlot.BuildDelivered("a") || events.Any(e => e.EngineId == "a" && !e.Processing));
        Assert.DoesNotContain(events, e => !e.Processing);
        p.Process(Audio()); Assert.All(first, e => Assert.Equal(1, e.DelayApplied));
        Until(() => first.All(e => e.Disposed), () => p.Process(Audio()));
        var replacement = rig.Made.Where(e => e.Id == "a" && !first.Contains(e)).ToArray();
        Assert.Equal(2, replacement.Length); Assert.All(replacement, e => Assert.Equal(2, e.DelayApplied));
    }

    [Fact]
    public void Both_active_slots_allocate_nothing_and_apply_live_changes_on_the_next_buffer()
    {
        using var rig = new Rig(); var p = rig.Pipeline;
        p.SpectralEnabled = true; p.RnnEnabled = true;
        Assert.True(p.SpectralSlot.TrySetSetting("spectral", "amount", new(1)));
        Assert.True(p.NeuralSlot.TrySetSetting("a", "amount", new(1)));
        p.SelectPcEngine("spectral"); p.SelectPcEngine("a");
        RunUntil(p, "spectral", DenoiseFamily.Spectral); RunUntil(p, "a");
        var audio = Audio();
        for (int i = 0; i < 60; i++) { Array.Fill(audio, 0.1f); p.Process(audio); }
        var active = rig.Made.Where(e => e.Id == "spectral" || e.Id == "a").ToArray();
        int beforeCalls = active.Sum(e => e.Calls);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) { Array.Fill(audio, 0.1f); p.Process(audio); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(800, active.Sum(e => e.Calls) - beforeCalls);
        Assert.True(p.SpectralSlot.TrySetSetting("spectral", "amount", new(0.5)));
        p.SpectralSlot.SetStrength("spectral", 0.5f);
        Assert.True(p.NeuralSlot.TrySetSetting("a", "amount", new(0.5)));
        p.NeuralSlot.SetStrength("a", 0.5f);
        Array.Fill(audio, 0.1f); p.Process(audio);
        Assert.All(active, e => Assert.Equal(0.5, e.GetSetting("amount").Number));
        Assert.All(audio, x => Assert.Equal(0.1875f, x, 5));
    }

    [Fact]
    public void Real_deepfilter_steady_state_allocation_is_reported()
    {
        string arch = Environment.Is64BitProcess ? "x64" : "x86";
        string lib = Path.Combine(RepoPaths.RequireRoot(), "runtimes", "win-" + arch, "native", DeepFilterNet3Engine.LibraryFileName);
        var made = new ConcurrentBag<DeepFilterNet3Engine>();
        var row = Row("deepfilternet3", DenoiseFamily.Neural, _ => {
            var engine = new DeepFilterNet3Engine(9, lib); Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
            made.Add(engine); return engine;
        }) with { SettingDescriptors = DeepFilterNet3Engine.SettingDescriptors };
        var spectral = Row("spectral", DenoiseFamily.Spectral, _ => new Engine("spectral", 1));
        using var p = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(new[] { row, spectral }));
        p.RnnEnabled = true; p.SpectralEnabled = true;
        p.SelectPcEngine("deepfilternet3"); p.SelectPcEngine("spectral");
        RunUntil(p, "spectral", DenoiseFamily.Spectral); RunUntil(p, "deepfilternet3");
        var audio = Audio(); for (int i = 0; i < 60; i++) { Array.Fill(audio, 0.01f); p.Process(audio); }
        long calls = made.Sum(e => e.FramesProcessed);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) { Array.Fill(audio, 0.01f); p.Process(audio); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine($"DeepFilterNet3: {allocated} managed bytes across 200 buffers, {arch}, both slots active.");
        Assert.Equal(400, made.Sum(e => e.FramesProcessed) - calls);
    }

    [Fact]
    public void Catalog_variant_settings_and_strength_reach_both_channels_at_build_and_live()
    {
        using var rig = new Rig(id => new Engine(id == "variant" ? "base" : id, 4));
        var p = rig.Pipeline; p.RnnEnabled = true;
        Assert.True(p.NeuralSlot.TrySetSetting("variant", "amount", new(0.5)));
        p.NeuralSlot.SetStrength("variant", 0.5f);
        p.SelectPcEngine("variant"); RunUntil(p, "variant");
        var audio = Audio(); p.Process(audio); Assert.All(audio, x => Assert.Equal(0.15f, x, 5));
        var engines = rig.Made.Where(e => e.Id == "base").ToArray(); Assert.Equal(2, engines.Length);
        Assert.All(engines, e => Assert.Equal(0.5, e.GetSetting("amount").Number));
        Assert.True(p.NeuralSlot.TrySetSetting("variant", "amount", new(1)));
        p.NeuralSlot.SetStrength("variant", 0.25f);
        audio = Audio(); p.Process(audio); Assert.All(audio, x => Assert.Equal(0.175f, x, 5));
        Assert.All(engines, e => Assert.Equal(1, e.GetSetting("amount").Number));
    }

    [Fact]
    public void Never_built_settings_come_from_the_catalog_without_constructing_an_engine()
    {
        int builds = 0;
        var made = new ConcurrentBag<DeepFilterNet3Engine>();
        string arch = Environment.Is64BitProcess ? "x64" : "x86";
        string lib = Path.Combine(RepoPaths.RequireRoot(), "runtimes", "win-" + arch, "native", DeepFilterNet3Engine.LibraryFileName);
        var row = Row("deepfilternet3", DenoiseFamily.Neural, _ => {
            Interlocked.Increment(ref builds);
            var engine = new DeepFilterNet3Engine(9, lib); made.Add(engine); return engine;
        }) with { SettingDescriptors = DeepFilterNet3Engine.SettingDescriptors };
        using var p = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(new[] { row }));
        Assert.Same(DeepFilterNet3Engine.SettingDescriptors, p.NeuralSlot.SettingsFor("deepfilternet3"));
        Assert.Equal(9, p.NeuralSlot.GetSetting("deepfilternet3", "attenuation_limit_db").Number);
        Assert.Equal(0, p.NeuralSlot.GetSetting("deepfilternet3", "post_filter_beta").Number);
        Assert.True(p.NeuralSlot.TrySetSetting("deepfilternet3", "attenuation_limit_db", new(12)));
        Assert.Equal(12, p.NeuralSlot.GetSetting("deepfilternet3", "attenuation_limit_db").Number);
        Assert.Equal(0, builds);
        Assert.Empty(p.SpectralSlot.SettingsFor("deepfilternet3"));
        Assert.Throws<ArgumentException>(() => p.SpectralSlot.GetSetting("deepfilternet3", "attenuation_limit_db"));
        Assert.False(p.SpectralSlot.TrySetSetting("deepfilternet3", "attenuation_limit_db", new(12)));
        Assert.Empty(p.SpectralSlot.ExportSettings());
        Assert.Empty(p.NeuralSlot.ExportSettings().Where(e => e.EngineId != "deepfilternet3"));
        var results = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => results.Enqueue(e);
        p.SelectPcEngine("deepfilternet3");
        Until(() => p.NeuralSlot.BuildDelivered("deepfilternet3") || results.Any(e => !e.Processing), () => p.Process(Audio()));
        Assert.Equal(2, builds); Assert.Equal(2, made.Count);
        Assert.All(made, e => Assert.Equal(12, e.GetSetting("attenuation_limit_db").Number));
    }

    [Fact]
    public void Default_catalog_descriptors_and_defaults_match_its_own_factories()
    {
        var catalog = new DenoiseEngineCatalog();
        var neural = new PcDenoiseSlot(DenoiseFamily.Neural, "absent", 48000, 2, catalog);
        var spectral = new PcDenoiseSlot(DenoiseFamily.Spectral, "absent", 48000, 2, catalog);
        foreach (var row in catalog.Entries.Where(e => e.Factory != null)) {
            using var engine = row.Factory!(row.Settings.ToDictionary(d => d.Name, d => d.Default));
            Assert.Equal(row.Settings, engine.Settings);
            var slot = row.Family == DenoiseFamily.Spectral ? spectral : neural;
            foreach (var descriptor in row.Settings) {
                var expected = engine.GetSetting(descriptor.Name);
                var actual = slot.GetSetting(row.Id, descriptor.Name);
                Assert.Equal((float)expected.Number, (float)actual.Number);
                Assert.Equal(expected.Choice, actual.Choice);
            }
        }
    }

    [Fact]
    public void Mailbox_delivery_after_a_new_choice_is_not_discarded_by_an_old_snapshot()
    {
        using var rig = new Rig(); var p = rig.Pipeline; p.RnnEnabled = true;
        var events = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => events.Enqueue(e);
        p.SelectPcEngine("a"); RunUntil(p, "a"); Until(() => events.Any(e => e.EngineId == "a"));
        while (events.TryDequeue(out _)) { }
        p.SelectPcEngine("b"); Until(() => p.NeuralSlot.BuildDelivered("b"));
        p.NeuralSlot.InstallHook = point => {
            if (point != "before-ready-take") return;
            p.NeuralSlot.InstallHook = null;
            Assert.True(p.SelectPcEngine("c"));
            Until(() => p.NeuralSlot.BuildDelivered("c"));
        };
        p.Process(Audio()); RunUntil(p, "c");
        Until(() => events.Any(e => e.EngineId == "c" && e.Processing));
        Assert.Single(events); Assert.Equal("c", p.NeuralSlot.ChosenEngineId);
        Assert.Equal(2, rig.Made.Count(e => e.Id == "c"));
        Until(() => rig.Made.Where(e => e.Id == "b").All(e => e.Disposed));
        Assert.All(rig.Made, e => Assert.Equal(0, e.DisposedInside));
    }

    [Theory]
    [InlineData("before-running-id", "a")]
    [InlineData("after-running-id", "a")]
    [InlineData("before-running-id", "c")]
    public void Install_race_converges_once_without_double_build(string point, string winner)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        bool hold = false;
        using var rig = new Rig(id => {
            if (hold && id == winner) { entered.Set(); Assert.True(release.Wait(4000)); }
            return new Engine(id, 1);
        });
        var p = rig.Pipeline; p.RnnEnabled = true;
        var events = new ConcurrentQueue<DenoiseSelectionResult>();
        p.SelectionResult += (_, e) => events.Enqueue(e);
        p.SelectPcEngine("a"); RunUntil(p, "a");
        // Flush the previous choice's notification before counting this choice.
        Until(() => events.Any(e => e.EngineId == "a"), () => p.Process(Audio()));
        while (events.TryDequeue(out _)) { }
        int before = rig.Made.Count(e => e.Id == winner);
        p.NeuralSlot.InstallHook = name => {
            if (name != point) return;
            p.NeuralSlot.InstallHook = null;
            hold = point == "after-running-id";
            Assert.True(p.SelectPcEngine(winner));
        };
        p.SelectPcEngine("b"); Until(() => p.NeuralSlot.BuildDelivered("b"));
        try {
            for (int i = 0; i < 10; i++) p.Process(Audio());
            if (hold) {
                Assert.True(entered.Wait(3000));
                for (int i = 0; i < 15; i++) p.Process(Audio());
            }
        }
        finally { release.Set(); }
        RunUntil(p, winner);
        Until(() => events.Any(e => e.EngineId == winner && e.Processing));
        for (int i = 0; i < 20; i++) p.Process(Audio());
        Assert.Equal(winner, p.NeuralSlot.ChosenEngineId);
        Assert.Single(events.Where(e => e.Processing));
        Assert.DoesNotContain(events, e => e.EngineId == "b");
        Assert.Equal(before + 2, rig.Made.Count(e => e.Id == winner));
        Until(() => rig.Made.Where(e => e.Id == "b").All(e => e.Disposed));
        Assert.All(rig.Made, e => Assert.Equal(0, e.DisposedInside));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Convergence_does_not_retry_a_failed_choice(bool firstFrame)
    {
        using var rig = new Rig(id => new Engine(id, 1) {
            IsAvailable = id != "a" || firstFrame, ThrowOnFrame = id == "a" && firstFrame });
        var p = rig.Pipeline; p.RnnEnabled = true;
        var events = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => events.Enqueue(e);
        p.SelectPcEngine("a");
        Until(() => events.Any(e => e.EngineId == "a" && !e.Processing), () => p.Process(Audio()));
        int count = rig.Made.Count(e => e.Id == "a");
        for (int i = 0; i < 100; i++) p.Process(Audio());
        Assert.Equal(count, rig.Made.Count(e => e.Id == "a"));
        Assert.Single(events.Where(e => e.EngineId == "a"));
    }

    [Fact]
    public void Real_native_engines_survive_repeated_background_swaps_and_disconnect()
    {
        string arch = Environment.Is64BitProcess ? "x64" : "x86";
        string lib = Path.Combine(RepoPaths.RequireRoot(), "runtimes", "win-" + arch, "native", DeepFilterNet3Engine.LibraryFileName);
        var made = new ConcurrentBag<DeepFilterNet3Engine>();
        using var buildEntered = new ManualResetEventSlim(); using var buildRelease = new ManualResetEventSlim();
        bool holdBuild = false;
        var defaults = new DenoiseEngineCatalog();
        var catalog = new DenoiseEngineCatalog(defaults.Entries.Select(e => e.Id == "deepfilternet3"
            ? e with { Factory = _ => {
                if (Volatile.Read(ref holdBuild)) { buildEntered.Set(); Assert.True(buildRelease.Wait(5000)); }
                var engine = new DeepFilterNet3Engine(9, lib);
                Assert.True(engine.IsAvailable, engine.UnavailabilityDetail); made.Add(engine); return engine;
            } } : e));
        using var p = new RxAudioPipeline(catalog: catalog); p.RnnEnabled = true;
        var events = new ConcurrentQueue<DenoiseSelectionResult>(); p.SelectionResult += (_, e) => events.Enqueue(e);
        using var run = new AutoResetEvent(false); using var done = new AutoResetEvent(false);
        bool stop = false; int requested = 0;
        var processing = Task.Run(() => {
            var audio = Audio();
            while (true) {
                run.WaitOne(); if (Volatile.Read(ref stop)) return;
                for (int i = 0; i < Volatile.Read(ref requested); i++) { Array.Fill(audio, 0.01f); p.Process(audio); }
                done.Set();
            }
        });
        void Step(int frames) {
            Volatile.Write(ref requested, frames); run.Set();
            Assert.True(done.WaitOne(5000), "background audio did not finish its bounded buffer batch");
        }
        try {
            Step(1); Until(() => events.Any(e => e.EngineId == "rnnoise"));
            for (int round = 0; round < 4; round++) {
                while (events.TryDequeue(out _)) { }
                var previous = made.ToArray();
                Assert.True(p.SelectPcEngine("deepfilternet3")); Until(() => p.NeuralSlot.BuildDelivered("deepfilternet3"));
                Step(3); // fewer than ten: this candidate has processed, but cannot install
                var abandoned = made.Where(e => !previous.Contains(e)).ToArray();
                Assert.Equal(2, abandoned.Length);
                Assert.All(abandoned, e => Assert.Equal(3, e.FramesProcessed));
                Assert.Equal("rnnoise", p.NeuralSlot.RunningEngineId);
                Assert.True(p.SelectPcEngine("rnnoise")); Step(1);
                Until(() => abandoned.All(e => !e.IsAvailable));
                Assert.DoesNotContain(events, e => e.EngineId == "deepfilternet3");
                Until(() => events.Any(e => e.EngineId == "rnnoise" && e.Processing));
                Assert.True(p.SelectPcEngine("deepfilternet3")); Until(() => p.NeuralSlot.BuildDelivered("deepfilternet3"));
                Step(10); Assert.Equal("deepfilternet3", p.NeuralSlot.RunningEngineId);
                Until(() => events.Any(e => e.EngineId == "deepfilternet3" && e.Processing));
                Assert.True(p.SelectPcEngine("rnnoise")); Until(() => p.NeuralSlot.BuildDelivered("rnnoise"));
                Step(10); Assert.Equal("rnnoise", p.NeuralSlot.RunningEngineId);
                // Wait for this choice's event before the next round empties the queue.
                Until(() => events.Count(e => e.EngineId == "rnnoise" && e.Processing) == 2);
            }
            int count = made.Count;
            Volatile.Write(ref holdBuild, true);
            Assert.True(p.SelectPcEngine("deepfilternet3")); Assert.True(buildEntered.Wait(3000));
            p.Dispose(); buildRelease.Set();
            Until(() => made.Count == count + 2 && made.All(e => !e.IsAvailable));
        }
        finally {
            buildRelease.Set(); Volatile.Write(ref stop, true); run.Set(); Assert.True(processing.Wait(5000));
        }
    }
}
