using System;
using System.Collections.Generic;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track P, repair 1, B2 (#873): a running receive slot follows the
/// shared-delay plan it is given. Codex's check of <c>c5c8e43b</c> found that a
/// published plan never reached a running hold, that the next switch inherited
/// a hold from an old plan, and that the Advanced tab's readout described the
/// fresh plan rather than the delay in force. Its three reproductions are the
/// first three tests here, adopted as written where they could be, and each
/// fails on <c>c5c8e43b</c>. Windowless and silent: fake engines with real,
/// different delays in a real receive pipeline, nothing played.
/// </summary>
/// <remarks>
/// Every timeline is measured with a ramp whose every value names its own
/// sample, so the delay actually heard is read off the output, independently
/// of what the slot says it holds.
/// </remarks>
public sealed class SharedDelayPlanChangeTests
{
    private const int F = 480;
    private const float Scale = 1048576f;
    private readonly ITestOutputHelper _out;

    public SharedDelayPlanChangeTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    private static float Ramp(long n) => n / Scale;

    private static SharedDelayPlan Plan(int samples, params string[] ids)
    {
        var members = new ReceiveSharedDelay.Member[ids.Length];
        for (int i = 0; i < ids.Length; i++) members[i] = new ReceiveSharedDelay.Member(ids[i], null);
        return new SharedDelayPlan(samples, members);
    }

    /// <summary>The delay the last output sample was heard at: the input index it carries, against the index just fed.</summary>
    private static long Lag(AlignedSwitchTests.Rig rig, List<float> o) => (rig.At - 1) - (long)Math.Round(o[^1] * Scale);

    // ── Codex's reproductions ──

    [Fact]
    public void Codex_1_an_empty_plan_stops_the_padding_of_a_running_engine()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 3408, 1, shared: true);
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        Assert.Equal(3408, rig.Slot.HeldSamples);
        Assert.Equal(3408, Lag(rig, o));

        rig.Slot.SharedPlan = SharedDelayPlan.None;
        rig.Feed(Ramp, 30, o);
        _out.WriteLine($"empty plan: held {rig.Slot.HeldSamples}, heard at {Lag(rig, o)}");
        Assert.Equal(-1, rig.Slot.HeldSamples);
        Assert.Equal(480, Lag(rig, o)); // the engine's own delay, nothing added
    }

    [Fact]
    public void Codex_2_a_shrunken_plan_reaches_the_running_engine_and_the_next_switch_holds_it()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true);
        rig.Slot.SharedPlan = Plan(3894, "rnnoise", "b");
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        Assert.Equal(3894, rig.Slot.HeldSamples);

        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        rig.Feed(Ramp, 20, o);
        Assert.Equal(1440, rig.Slot.HeldSamples);
        Assert.Equal(1440, Lag(rig, o));

        rig.ChooseB();
        rig.Feed(Ramp, 40, o, "b");
        _out.WriteLine($"published {rig.Slot.SharedPlan.Samples}, held {rig.Slot.HeldSamples}, switch {rig.Slot.LastSwitch}");
        Assert.Equal("b", rig.Slot.RunningEngineId);
        Assert.Equal(1440, rig.Slot.HeldSamples);
        Assert.True(rig.Slot.LastSwitch is { Aligned: true, Held: 1440 }, $"the switch was {rig.Slot.LastSwitch}");
        Assert.Equal(1440, Lag(rig, o));
    }

    [Fact]
    public void Codex_3_through_the_controller_the_readout_says_what_is_held_and_the_hold_follows_the_favourites()
    {
        using var fx = new NoiseFixture(rnnoiseLatency: 960);
        fx.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "rnnoise" });
        fx.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "deepfilternet3" });
        fx.Noise.PublishSharedDelays();
        fx.Pipeline.RnnEnabled = true;
        fx.Settle();
        var slot = fx.Pipeline.NeuralSlot;
        Assert.Equal(1440, slot.HeldSamples);

        fx.Noise.ToggleFavourite("deepfilternet3", "review");
        Assert.Equal(960, slot.SharedPlan.Samples);
        // No buffer has run since: the slot still holds 1,440 (30 ms), and the
        // readout must say so, not the 960 (20 ms) the new plan asks for.
        Assert.Equal(1440, slot.HeldSamples);
        Assert.Equal(Row("rnnoise", fx, own: 20, shared: 30, more: 10), fx.Noise.SharedDelayRow("rnnoise"));

        fx.Settle();
        _out.WriteLine($"plan {slot.SharedPlan.Samples}, held {slot.HeldSamples}; row: {fx.Noise.SharedDelayRow("rnnoise")}");
        Assert.Equal(slot.SharedPlan.Samples, slot.HeldSamples);
        Assert.Equal(Row("rnnoise", fx, own: 20, shared: 20, more: 0), fx.Noise.SharedDelayRow("rnnoise"));
    }

    private static string Row(string id, NoiseFixture fx, int own, int shared, int more)
    {
        static string Ms(int ms) => Lexicon.Get("audio.noise_surface.delay_figure", ("ms", ms));
        return Lexicon.Get("audio.noise_advanced.delay_line_shared", ("engine", fx.Noise.NameOf(id)),
            ("delay", Ms(own)), ("shared", Ms(shared)), ("more", Ms(more)));
    }

    // ── The rest of the plan changes the check named ──

    [Fact]
    public void A_switch_straight_after_a_new_plan_never_inherits_the_old_hold()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true);
        rig.Slot.SharedPlan = Plan(3894, "rnnoise", "b");
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        Assert.Equal(3894, rig.Slot.HeldSamples);

        // No buffer between the new plan and the switch. Repair 1 had the
        // switch itself land the new plan, unaligned. Since repair 2 (R1-B1)
        // the running engine follows the plan while the switch warms, so it
        // retimes on the warm-up's first buffer, and the switch is then decided
        // again against that hold and is aligned. Either way time moves once,
        // by 3,894 to 1,440, and the old hold is never inherited.
        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        rig.ChooseB();
        int retimeAt = o.Count;
        rig.Feed(Ramp, 40, o, "b");
        _out.WriteLine($"held {rig.Slot.HeldSamples}, retime {rig.Slot.LastRetime}, switch {rig.Slot.LastSwitch}");
        Assert.Equal(1440, rig.Slot.HeldSamples);
        Assert.True(rig.Slot.LastRetime is { From: 3894, To: 1440, Faded: true }, $"the retime was {rig.Slot.LastRetime}");
        Assert.True(rig.Slot.LastSwitch is { Aligned: true, Held: 1440 }, $"the switch was {rig.Slot.LastSwitch}");
        // Time moved once, at the retime buffer; after it, through the switch, every sample is exactly 1,440 behind.
        for (int i = retimeAt + F; i < o.Count; i++) Assert.Equal(i - 1440, (long)Math.Round(o[i] * Scale));
    }

    [Fact]
    public void A_slower_favourite_added_lengthens_the_running_hold()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true);
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        Assert.Equal(1440, rig.Slot.HeldSamples);

        rig.Slot.SharedPlan = Plan(3408, "rnnoise", "b", "slow");
        rig.Feed(Ramp, 30, o);
        Assert.Equal(3408, rig.Slot.HeldSamples);
        Assert.Equal(3408, Lag(rig, o));
    }

    [Fact]
    public void A_changed_figure_for_the_same_members_moves_the_hold()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true);
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        // A model change or a measured figure replacing an expected one: same members, new figure.
        rig.Slot.SharedPlan = Plan(2000, "rnnoise", "b");
        rig.Feed(Ramp, 30, o);
        Assert.Equal(2000, rig.Slot.HeldSamples);
        Assert.Equal(2000, Lag(rig, o));
    }

    [Fact]
    public void A_running_engine_measured_into_an_outlier_is_no_longer_padded()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true);
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        Assert.Equal(1440, rig.Slot.HeldSamples);

        // Measured over the outlier line: the plan rule takes it out of the set.
        var figures = new Dictionary<string, int> { ["rnnoise"] = ReceiveSharedDelay.OutlierSamples + 1, ["b"] = 1440 };
        var plan = ReceiveSharedDelay.PlanFor(new[] { new ReceiveSharedDelay.Member("rnnoise", null), new ReceiveSharedDelay.Member("b", null) },
            m => figures[m.EngineId], out var outliers);
        Assert.Single(outliers);
        Assert.False(plan.Contains("rnnoise", null));
        rig.Slot.SharedPlan = plan;
        rig.Feed(Ramp, 30, o);
        Assert.Equal(-1, rig.Slot.HeldSamples);
        Assert.Equal(480, Lag(rig, o));
    }

    [Fact]
    public void A_running_engine_taken_out_of_the_favourites_is_no_longer_padded()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true);
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        rig.Slot.SharedPlan = Plan(1440, "b");
        rig.Feed(Ramp, 30, o);
        Assert.Equal(-1, rig.Slot.HeldSamples);
        Assert.Equal(480, Lag(rig, o));
    }

    [Fact]
    public void An_unpadded_engine_that_joins_a_plan_is_padded_once_its_line_holds_enough()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: false);
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        Assert.Equal(-1, rig.Slot.HeldSamples);
        Assert.Equal(480, Lag(rig, o));

        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        // The line starts empty and needs 960 samples (two buffers) of this
        // engine's output before the longer delay is real audio.
        rig.Feed(Ramp, 1, o);
        Assert.Equal(-1, rig.Slot.HeldSamples);
        Assert.Equal(480, Lag(rig, o));
        rig.Feed(Ramp, 10, o);
        Assert.Equal(1440, rig.Slot.HeldSamples);
        Assert.Equal(1440, Lag(rig, o));
    }

    [Fact]
    public void A_republished_plan_that_says_the_same_moves_nothing()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true);
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        int start = o.Count;
        for (int i = 0; i < 10; i++)
        {
            rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b"); // a new object every time, as the controller makes them
            rig.Feed(Ramp, 3, o);
        }
        Assert.Null(rig.Slot.LastRetime);
        Assert.Equal(1440, rig.Slot.HeldSamples);
        // Every sample after the republishing is exactly 1,440 behind its input.
        for (int i = start; i < o.Count; i++)
            Assert.Equal(i - 1440, (long)Math.Round(o[i] * Scale));
    }

    // ── Repair 2, R1-B1: a plan change reaches the running engine while another warms ──

    /// <summary>
    /// An available engine that is late, by the interface's own late-frame
    /// result, for its first <paramref name="lateFrames"/> frames (forever when
    /// negative), then on time: a pure delay of <paramref name="delay"/> samples.
    /// The helper engine does exactly this when its reply is not back by the
    /// receive deadline (<c>WdspHelperEngine.ProcessFrame</c>).
    /// </summary>
    private sealed class LateEngine : IDenoiseEngine
    {
        private readonly float[] _ring;
        private int _at, _lateLeft;
        private readonly bool _forever;
        public LateEngine(string id, int delay, int lateFrames)
        { Id = id; _ring = new float[delay]; LatencySamples = delay; _lateLeft = lateFrames; _forever = lateFrames < 0; }
        public string Id { get; }
        public int SampleRate => 48000;
        public int FrameLength => F;
        public int LatencySamples { get; }
        public bool IsAvailable => true;
        public IReadOnlyList<EngineSettingDescriptor> Settings => Array.Empty<EngineSettingDescriptor>();
        public EngineSettingValue GetSetting(string name) => throw new ArgumentException(name);
        public bool TrySetSetting(string name, EngineSettingValue value) => false;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            for (int i = 0; i < input.Length; i++)
            {
                output[i] = _ring[_at];
                _ring[_at] = input[i];
                if (++_at == _ring.Length) _at = 0;
            }
            if (_forever) return false;
            if (_lateLeft > 0) { _lateLeft--; return false; }
            return true;
        }
        public void Reset() { Array.Clear(_ring); _at = 0; }
        public void Dispose() { }
    }

    /// <summary>A receive pipeline running "rnnoise" (a 480-sample delay) that can switch to a late "b".</summary>
    private sealed class LateRig : IDisposable
    {
        public RxAudioPipeline P { get; }
        public PcDenoiseSlot Slot => P.NeuralSlot;
        public long At;
        public LateRig(int delayB, int lateFrames, SharedDelayPlan plan)
        {
            var catalog = new DenoiseEngineCatalog(new[]
            {
                new DenoiseEngineEntry("rnnoise", DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { DenoiseDirection.Receive },
                    (_, _) => new AlignedSwitchTests.DelayGainEngine("rnnoise", 480, 1), () => Unavailability.None),
                new DenoiseEngineEntry("b", DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { DenoiseDirection.Receive },
                    (_, _) => new LateEngine("b", delayB, lateFrames), () => Unavailability.None),
            });
            P = new RxAudioPipeline(catalog: catalog);
            Slot.SharedPlan = plan;
            Slot.MuteOverrideForTest = false;
            Slot.SetStrength("rnnoise", 1);
            Slot.SetStrength("b", 1);
            P.RnnEnabled = true;
        }

        public void Feed(int buffers, List<float> output)
        {
            var audio = new float[2 * F];
            for (int b = 0; b < buffers; b++)
            {
                for (int i = 0; i < F; i++) audio[2 * i] = audio[2 * i + 1] = Ramp(At + i);
                P.Process(audio);
                for (int i = 0; i < F; i++) output.Add(audio[2 * i]);
                At += F;
            }
        }

        public void ChooseB()
        {
            Assert.True(Slot.Select("b", out _));
            var t = System.Diagnostics.Stopwatch.StartNew();
            while (!Slot.BuildDelivered("b") && t.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(1);
            Assert.True(Slot.BuildDelivered("b"), "b's bank was never delivered");
        }

        public long Lag(List<float> o) => (At - 1) - (long)Math.Round(o[^1] * Scale);
        public void Dispose() => P.Dispose();
    }

    /// <summary>
    /// Codex's reproduction from its re-check of <c>bcdf2f70</c>, adopted: with
    /// an incoming engine that stays late, an empty plan never reached the
    /// running engine, because the warm-up never finished and the plan was
    /// applied only when nothing warmed. <c>warming: false</c> is Codex's
    /// control, the identical case with no warm-up, which already passed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Codex_R1_an_empty_plan_reaches_the_running_engine_while_the_incoming_one_stays_late(bool warming)
    {
        using var rig = new LateRig(1440, lateFrames: -1, Plan(1440, "rnnoise", "b"));
        var o = new List<float>();
        rig.Feed(40, o);
        Assert.Equal(1440, rig.Slot.HeldSamples);
        if (warming) { rig.ChooseB(); rig.Feed(1, o); }

        rig.Slot.SharedPlan = SharedDelayPlan.None;
        rig.Feed(100, o);
        _out.WriteLine($"warming {warming}: running {rig.Slot.RunningEngineId}, held {rig.Slot.HeldSamples}, heard at {rig.Lag(o)}, retime {rig.Slot.LastRetime}");
        Assert.Equal("rnnoise", rig.Slot.RunningEngineId); // b never finished warming
        Assert.Equal(-1, rig.Slot.HeldSamples);
        Assert.Equal(480, rig.Lag(o));
        Assert.True(rig.Slot.LastRetime is { From: 1440, To: 480, Faded: true }, $"the retime was {rig.Slot.LastRetime}");
    }

    /// <summary>
    /// A retime already waiting when a switch begins is not dropped (repair 2):
    /// an unpadded engine joins a plan, its line needs two buffers before the
    /// longer delay is real audio, and a late engine is chosen after one. Repair
    /// 1 dropped the waiting retime when the warm-up began, so with an incoming
    /// engine that never comes on time the plan was never applied.
    /// </summary>
    [Fact]
    public void A_retime_waiting_when_a_late_switch_begins_still_lands()
    {
        using var rig = new LateRig(1440, lateFrames: -1, SharedDelayPlan.None);
        var o = new List<float>();
        rig.Feed(40, o);
        Assert.Equal(-1, rig.Slot.HeldSamples);

        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        rig.Feed(1, o);
        Assert.Equal(-1, rig.Slot.HeldSamples); // waiting: the line holds 480 of the 960 it needs
        rig.ChooseB();
        rig.Feed(20, o);
        _out.WriteLine($"running {rig.Slot.RunningEngineId}, held {rig.Slot.HeldSamples}, heard at {rig.Lag(o)}, retime {rig.Slot.LastRetime}");
        Assert.Equal("rnnoise", rig.Slot.RunningEngineId);
        Assert.Equal(1440, rig.Slot.HeldSamples);
        Assert.Equal(1440, rig.Lag(o));
        Assert.True(rig.Slot.LastRetime is { From: 480, To: 1440, Faded: true }, $"the retime was {rig.Slot.LastRetime}");
    }

    /// <summary>
    /// The incoming engine's decision follows the running engine's retime
    /// (repair 2): a plan shrinks while "b" is late, the running engine retimes
    /// to it at once, and when "b" comes on time the switch is aligned at the
    /// new figure. Time moves once, at the retime; the switch moves none.
    /// </summary>
    [Fact]
    public void When_the_late_engine_comes_on_time_its_switch_is_aligned_at_the_retimed_hold()
    {
        using var rig = new LateRig(960, lateFrames: 30, Plan(1440, "rnnoise", "b"));
        var o = new List<float>();
        rig.Feed(40, o);
        Assert.Equal(1440, rig.Slot.HeldSamples);
        rig.ChooseB();
        rig.Feed(2, o);
        Assert.Equal("rnnoise", rig.Slot.RunningEngineId);

        rig.Slot.SharedPlan = Plan(960, "rnnoise", "b");
        rig.Feed(3, o);
        // Still warming (b is late), and the running engine already holds the new figure.
        Assert.Equal("rnnoise", rig.Slot.RunningEngineId);
        Assert.True(rig.Slot.LastRetime is { From: 1440, To: 960, Faded: true }, $"the retime was {rig.Slot.LastRetime}");
        Assert.Equal(960, rig.Slot.HeldSamples);
        Assert.Equal(960, rig.Lag(o));

        int after = o.Count;
        rig.Feed(80, o);
        _out.WriteLine($"held {rig.Slot.HeldSamples}, switch {rig.Slot.LastSwitch}, retime {rig.Slot.LastRetime}");
        Assert.Equal("b", rig.Slot.RunningEngineId);
        Assert.True(rig.Slot.LastSwitch is { Aligned: true, Held: 960 }, $"the switch was {rig.Slot.LastSwitch}");
        // Every sample from the retime on, through the switch, is exactly 960 behind its input.
        for (int i = after; i < o.Count; i++) Assert.Equal(i - 960, (long)Math.Round(o[i] * Scale));
    }

    // ── How the hold moves: a fade at one buffer, not a jump ──

    [Fact]
    public void The_retime_fades_the_old_delay_into_the_new_across_one_buffer()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true);
        rig.Slot.SharedPlan = Plan(3894, "rnnoise", "b");
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        int before = o.Count;
        rig.Feed(Ramp, 1, o);
        var note = rig.Slot.LastRetime;
        _out.WriteLine($"retime {note}");
        Assert.True(note is { From: 3894, To: 1440, Faded: true }, $"the retime was {note}");

        // The buffer before: the old delay exactly. The buffer after: the new one exactly.
        for (int i = before - F; i < before; i++) Assert.Equal(i - 3894, (long)Math.Round(o[i] * Scale));
        rig.Feed(Ramp, 2, o);
        for (int i = before + F; i < o.Count; i++) Assert.Equal(i - 1440, (long)Math.Round(o[i] * Scale));

        // The retime buffer is a blend of the two, never the new delay from its
        // first sample (that would be a jump): every frame but the last lies
        // strictly between the two taps, and the last is the new one.
        for (int f = 0; f < F; f++)
        {
            int i = before + f;
            double oldTap = (i - 3894) / Scale, newTap = (i - 1440) / Scale, v = o[i];
            if (f == F - 1) Assert.Equal(newTap, v, 6);
            else Assert.True(v > oldTap && v < newTap, $"frame {f}: {v * Scale} not between {i - 3894} and {i - 1440}");
        }
    }

    [Fact]
    public void A_retime_allocates_nothing_on_the_audio_thread()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true);
        var audio = new float[2 * F];
        for (int b = 0; b < 20; b++) rig.P.Process(audio);
        // The first retime sizes the switch buffer once, as the warm-up's first buffer does.
        rig.Slot.SharedPlan = Plan(2000, "rnnoise", "b");
        rig.P.Process(audio);
        Assert.Equal(2000, rig.Slot.HeldSamples);
        var shorter = Plan(1440, "rnnoise", "b"); // made on the control side, before the count starts
        long before = GC.GetAllocatedBytesForCurrentThread();
        rig.Slot.SharedPlan = shorter;
        for (int b = 0; b < 5; b++) rig.P.Process(audio);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before; // read before any Assert, which allocates itself
        Assert.Equal(1440, rig.Slot.HeldSamples);
        Assert.Equal(0, allocated);
    }

    // ── Repair 3, R2-B1: an equivalent republished plan keeps a pending retime's progress ──

    /// <summary>
    /// A pure delay of <paramref name="delay"/> samples that can be told, from
    /// the test, to be late (the interface's late-frame result), to fail (throw,
    /// which the bank counts as a fallback frame), or to run a callback inside
    /// its own processing (a plan published while the outgoing engine is in its
    /// bank). Codex's engine from its second re-check of <c>05d914cc</c>, adopted.
    /// </summary>
    private sealed class ControlledEngine : IDenoiseEngine
    {
        private readonly AlignedSwitchTests.DelayGainEngine _inner;
        public Action? OnFrame;
        public bool Late, Fail;
        public ControlledEngine(string id, int delay) { _inner = new AlignedSwitchTests.DelayGainEngine(id, delay, 1); }
        public string Id => _inner.Id;
        public int SampleRate => 48000;
        public int FrameLength => F;
        public int LatencySamples => _inner.LatencySamples;
        public bool IsAvailable => true;
        public IReadOnlyList<EngineSettingDescriptor> Settings => Array.Empty<EngineSettingDescriptor>();
        public EngineSettingValue GetSetting(string name) => throw new ArgumentException(name);
        public bool TrySetSetting(string name, EngineSettingValue value) => false;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            OnFrame?.Invoke();
            if (Fail) throw new InvalidOperationException("review-failed-start");
            _inner.ProcessFrame(input, output);
            return !Late;
        }
        public void Reset() => _inner.Reset();
        public void Dispose() => _inner.Dispose();
    }

    /// <summary>A receive pipeline with "rnnoise" (480 samples) and "b" (960), each engine the catalogue makes kept so the test can steer it.</summary>
    private sealed class ControlledRig : IDisposable
    {
        public RxAudioPipeline P { get; }
        public PcDenoiseSlot Slot => P.NeuralSlot;
        public readonly List<ControlledEngine> A = new(), B = new();
        public long At;
        public readonly float[] Audio = new float[2 * F];

        public ControlledRig(bool bLate = false)
        {
            DenoiseEngineEntry Row(string id, int delay, List<ControlledEngine> made) =>
                new(id, DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { DenoiseDirection.Receive },
                    (_, _) =>
                    {
                        var e = new ControlledEngine(id, delay) { Late = id == "b" && bLate };
                        lock (made) made.Add(e);
                        return e;
                    },
                    () => Unavailability.None);
            P = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(new[] { Row("rnnoise", 480, A), Row("b", 960, B) }));
            Slot.MuteOverrideForTest = false;
            Slot.SetStrength("rnnoise", 1);
            Slot.SetStrength("b", 1);
            P.RnnEnabled = true;
        }

        public void Feed(int buffers, List<float>? output = null)
        {
            for (int b = 0; b < buffers; b++)
            {
                for (int i = 0; i < F; i++) Audio[2 * i] = Audio[2 * i + 1] = Ramp(At + i);
                P.Process(Audio);
                if (output != null) for (int i = 0; i < F; i++) output.Add(Audio[2 * i]);
                At += F;
            }
        }

        public void Choose(string id)
        {
            Assert.True(Slot.Select(id, out _));
            var t = System.Diagnostics.Stopwatch.StartNew();
            while (!Slot.BuildDelivered(id) && t.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(1);
            Assert.True(Slot.BuildDelivered(id), id + "'s bank was never delivered");
        }

        /// <summary>The delay the last sample fed was heard at, read off the ramp.</summary>
        public long Lag => (At - 1) - (long)Math.Round(Audio[^1] * Scale);
        public void Dispose() => P.Dispose();
    }

    /// <summary>
    /// Codex's R2-B1 reproduction from its second re-check of <c>05d914cc</c>,
    /// adopted: a running engine with no hold, and a fresh plan asking for the
    /// same 1,440 published before every one of 100 buffers. The controller
    /// makes a new plan object on every favourite change, engine choice and
    /// selection result, so a plan saying the same thing again is ordinary. On
    /// <c>05d914cc</c> each one cleared the line the pending retime was filling,
    /// so the history it needs was never gathered and the hold never landed:
    /// held none, heard at 480, with and without a late engine warming.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Codex_R2_an_equivalent_republished_plan_does_not_restart_a_pending_growth(bool warming)
    {
        using var rig = new ControlledRig(bLate: true);
        rig.Feed(40);
        Assert.Equal(-1, rig.Slot.HeldSamples);
        if (warming) { rig.Choose("b"); rig.Feed(1); }

        for (int i = 0; i < 100; i++)
        {
            rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
            rig.Feed(1);
        }
        _out.WriteLine($"warming {warming}: running {rig.Slot.RunningEngineId}, held {rig.Slot.HeldSamples}, heard at {rig.Lag}, retime {rig.Slot.LastRetime}");
        Assert.Equal("rnnoise", rig.Slot.RunningEngineId);
        Assert.Equal(1440, rig.Slot.HeldSamples);
        Assert.Equal(1440, rig.Lag);
        Assert.True(rig.Slot.LastRetime is { From: 480, To: 1440, Faded: true }, $"the retime was {rig.Slot.LastRetime}");
    }

    /// <summary>
    /// The same fault through the real controller (Codex's case, adopted): with
    /// RNNoise and DeepFilterNet3 as favourites the controller publishes a
    /// 1,440-sample plan, and the hold must reach it whether or not the
    /// controller publishes again before every buffer. <c>republish: false</c>
    /// is Codex's positive control, which already passed: one publication lands.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Codex_R2_controller_republications_preserve_a_pending_growth(bool republish)
    {
        using var fx = new NoiseFixture(rnnoiseLatency: 480);
        fx.Pipeline.RnnEnabled = true;
        fx.Settle();
        Assert.Equal(-1, fx.Pipeline.NeuralSlot.HeldSamples);
        fx.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "rnnoise" });
        fx.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "deepfilternet3" });
        fx.Noise.PublishSharedDelays();
        Assert.Equal(1440, fx.Pipeline.NeuralSlot.SharedPlan.Samples);

        var audio = new float[2 * F];
        for (int i = 0; i < 100; i++)
        {
            if (republish) fx.Noise.PublishSharedDelays();
            Array.Fill(audio, .1f);
            fx.Pipeline.Process(audio);
        }
        _out.WriteLine($"republish {republish}: held {fx.Pipeline.NeuralSlot.HeldSamples}, retime {fx.Pipeline.NeuralSlot.LastRetime}");
        Assert.Equal(1440, fx.Pipeline.NeuralSlot.HeldSamples);
        Assert.True(fx.Pipeline.NeuralSlot.LastRetime is { From: 480, To: 1440 }, $"the retime was {fx.Pipeline.NeuralSlot.LastRetime}");
    }

    /// <summary>
    /// A genuinely different target still replaces a pending one (Codex's
    /// probe, adopted; it passed on <c>05d914cc</c> and must keep passing): a
    /// 12,000-sample request is pending, waiting for its history, when a plan
    /// asking for 1,440 replaces it while "b" stays late.
    /// </summary>
    [Fact]
    public void Codex_R2_a_new_plan_supersedes_a_pending_growth_while_an_engine_is_late()
    {
        using var rig = new ControlledRig(bLate: true);
        rig.Feed(40);
        rig.Choose("b");
        rig.Feed(1);
        rig.Slot.SharedPlan = Plan(12000, "rnnoise", "b");
        rig.Feed(1);
        Assert.Equal(-1, rig.Slot.HeldSamples);

        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        rig.Feed(4);
        Assert.Equal(1440, rig.Slot.HeldSamples);
        Assert.Equal(1440, rig.Lag);
        Assert.True(rig.Slot.LastRetime is { From: 480, To: 1440 }, $"the retime was {rig.Slot.LastRetime}");
        Assert.Equal("rnnoise", rig.Slot.RunningEngineId);
    }

    /// <summary>
    /// What the line has gathered is this engine's own output whatever target
    /// it is gathered for, so a changed pending target keeps it (repair 3's
    /// design, wider than Codex's diagnostic control): one buffer gathered
    /// toward 12,000, then 1,440 asked for, needs one more buffer, not two.
    /// Restarting on every changed target would let a run of genuinely
    /// different plans starve the retime the way equivalent ones did.
    /// </summary>
    [Fact]
    public void A_changed_pending_target_keeps_the_history_already_gathered()
    {
        using var rig = new ControlledRig();
        rig.Feed(40);
        Assert.Equal(-1, rig.Slot.HeldSamples);
        rig.Slot.SharedPlan = Plan(12000, "rnnoise", "b");
        rig.Feed(1); // 480 of the 11,520 it needs
        Assert.Equal(-1, rig.Slot.HeldSamples);

        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b"); // needs 960, and 480 are there
        rig.Feed(1); // now 960
        Assert.Equal(-1, rig.Slot.HeldSamples);
        rig.Feed(1); // the retime
        _out.WriteLine($"held {rig.Slot.HeldSamples}, retime {rig.Slot.LastRetime}");
        Assert.Equal(1440, rig.Slot.HeldSamples);
        Assert.True(rig.Slot.LastRetime is { From: 480, To: 1440, Faded: true }, $"the retime was {rig.Slot.LastRetime}");
        rig.Feed(1);
        Assert.Equal(1440, rig.Lag);
    }

    /// <summary>
    /// The other half of repair 3's rule: a line that STARTS being fed is still
    /// cleared, because it may hold another engine's history. rnnoise runs held
    /// at 1,440 and hands over to "b" (aligned), so rnnoise's line becomes the
    /// spare one with rnnoise's old output in it. "b" is then retimed out of an
    /// empty plan, and rnnoise is chosen back outside any plan, so that line
    /// comes back as the running one, unfed and uncleared. When a plan then asks
    /// for 1,440, the retime must wait for history this engine has made since,
    /// and from the buffer after it every sample is exactly 1,440 behind. Kept
    /// history would read the old engine's output, minutes of samples stale.
    /// </summary>
    [Fact]
    public void A_line_that_starts_being_fed_never_reads_another_engines_history()
    {
        using var rig = new ControlledRig();
        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        rig.Feed(40);
        Assert.Equal(1440, rig.Slot.HeldSamples);
        rig.Choose("b");
        rig.Feed(30);
        Assert.Equal("b", rig.Slot.RunningEngineId);
        Assert.True(rig.Slot.LastSwitch is { Aligned: true }, $"the switch was {rig.Slot.LastSwitch}");

        rig.Slot.SharedPlan = SharedDelayPlan.None;
        rig.Feed(10);
        Assert.Equal(-1, rig.Slot.HeldSamples);
        rig.Choose("rnnoise");
        rig.Feed(30);
        Assert.Equal("rnnoise", rig.Slot.RunningEngineId);
        Assert.Equal(-1, rig.Slot.HeldSamples);
        Assert.Equal(480, rig.Lag);

        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        var o = new List<float>();
        long start = rig.At;
        int retimedAt = -1;
        for (int b = 0; b < 8; b++)
        {
            var before = rig.Slot.LastRetime;
            rig.Feed(1, o);
            if (retimedAt < 0 && !Equals(before, rig.Slot.LastRetime)) retimedAt = b;
        }
        _out.WriteLine($"retimed on buffer {retimedAt}: {rig.Slot.LastRetime}, held {rig.Slot.HeldSamples}");
        Assert.True(rig.Slot.LastRetime is { From: 480, To: 1440, Faded: true }, $"the retime was {rig.Slot.LastRetime}");
        // The line needs 960 samples of this engine's own output: two buffers, then the retime.
        Assert.Equal(2, retimedAt);
        for (int i = (retimedAt + 1) * F; i < o.Count; i++)
            Assert.Equal(start + i - 1440, (long)Math.Round(o[i] * Scale));
    }

    /// <summary>
    /// A plan published while the outgoing engine is inside its own bank, mid
    /// warm-up (Codex's probe, adopted; it passed on <c>05d914cc</c>): the
    /// running engine retimes on that buffer, the switch waits, and the switch
    /// that follows is aligned at the new figure.
    /// </summary>
    [Fact]
    public void Codex_R2_a_plan_published_during_outgoing_processing_defers_the_switch()
    {
        using var rig = new ControlledRig();
        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        rig.Feed(40);
        rig.Choose("b");
        rig.Feed(9);
        foreach (var e in rig.A) e.OnFrame = () => rig.Slot.SharedPlan = Plan(960, "rnnoise", "b");
        rig.Feed(1);
        foreach (var e in rig.A) e.OnFrame = null;
        Assert.True(rig.Slot.LastRetime is { From: 1440, To: 960 }, $"the retime was {rig.Slot.LastRetime}");
        Assert.Equal("rnnoise", rig.Slot.RunningEngineId);

        rig.Feed(25);
        Assert.Equal("b", rig.Slot.RunningEngineId);
        Assert.True(rig.Slot.LastSwitch is { Aligned: true, Held: 960 }, $"the switch was {rig.Slot.LastSwitch}");
        Assert.Equal(960, rig.Lag);
    }

    /// <summary>
    /// A failed warm start stops the slot, and the engine chosen after it
    /// starts from the plan in force then (Codex's probe, adopted; it passed
    /// on <c>05d914cc</c>): an empty plan, so rnnoise is heard at its own 480.
    /// </summary>
    [Fact]
    public void Codex_R2_a_failed_warm_start_stops_the_slot_then_recovery_uses_the_current_plan()
    {
        using var rig = new ControlledRig();
        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        rig.Feed(40);
        rig.Choose("b");
        foreach (var e in rig.B) e.Fail = true;
        rig.Slot.SharedPlan = SharedDelayPlan.None;
        rig.Feed(1);
        Assert.Equal("", rig.Slot.ProcessingEngineId);
        Assert.Null(rig.Slot.RunningBank);
        Assert.Equal(0, rig.Lag);

        rig.Choose("rnnoise");
        rig.Feed(40);
        Assert.Equal("rnnoise", rig.Slot.RunningEngineId);
        Assert.Equal(-1, rig.Slot.HeldSamples);
        Assert.Equal(480, rig.Lag);
    }

    /// <summary>
    /// Codex's M4 regression from its second re-check, adopted: the switch
    /// never completes on a buffer on which the running engine retimed, even
    /// when the incoming engine is outside the shared set. Outgoing rnnoise is
    /// held at 1,440; "b" (960, outside the plan) has warmed nine buffers; the
    /// plan shrinks to 960 on the tenth. The incoming decision does not change
    /// (its target stays none, unaligned), so the warm-up count is NOT reset by
    /// the retime, and only the handover's own guard stops the switch landing
    /// on the retime buffer. Repair 2's report called that guard redundant;
    /// this is the path it missed.
    /// </summary>
    [Fact]
    public void Codex_R2_an_outside_set_switch_waits_past_the_retime_buffer()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 960, 1, shared: true);
        rig.Slot.SharedPlan = Plan(1440, "rnnoise");
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        rig.ChooseB();
        rig.Feed(Ramp, 9, o);
        Assert.Equal("rnnoise", rig.Slot.RunningEngineId);

        rig.Slot.SharedPlan = Plan(960, "rnnoise");
        rig.Feed(Ramp, 1, o);
        _out.WriteLine($"retime {rig.Slot.LastRetime}, running {rig.Slot.RunningEngineId}, switch {rig.Slot.LastSwitch}");
        Assert.True(rig.Slot.LastRetime is { From: 1440, To: 960 }, $"the retime was {rig.Slot.LastRetime}");
        Assert.Equal("rnnoise", rig.Slot.RunningEngineId);

        rig.Feed(Ramp, 4, o);
        Assert.Equal("b", rig.Slot.RunningEngineId);
    }
}
