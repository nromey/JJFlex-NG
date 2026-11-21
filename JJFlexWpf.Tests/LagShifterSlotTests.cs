using System;
using System.Collections.Generic;
using System.Linq;
using JJPortaudio;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track P2 (#873 phase 2): the lag shifter inside the receive noise
/// reduction slot. An engine switch outside the shared set, turning computer
/// noise reduction on and off, and a retime are content-aligned AND
/// time-continuous: nothing is heard twice, nothing is lost, and the delay moves
/// only by whole splices. Engines with REAL, different delays (the zero-delay
/// fakes prove nothing). Every case runs today's code too (phase 1's path,
/// <c>shifter: false</c>) as its positive control. Windowless and silent.
/// </summary>
public sealed class LagShifterSlotTests
{
    private const int F = 480;
    private const float Scale = 1048576f;
    private readonly ITestOutputHelper _out;
    public LagShifterSlotTests(ITestOutputHelper output) { NoiseReductionPerChannelTests.AssertWindowless(); _out = output; }

    private static float Ramp(long n) => n / Scale;

    private static SharedDelayPlan Plan(int samples, params string[] ids) =>
        new(samples, ids.Select(id => new ReceiveSharedDelay.Member(id, null)).ToArray());

    /// <summary>The input index each output sample names (a ramp): not whole inside a fade.</summary>
    private static double Index(float v) => v * (double)Scale;

    /// <summary>
    /// Time continuity on a ramp: from <paramref name="from"/> on, the index the
    /// output names never steps forward by more than one splice at a time and,
    /// when <paramref name="growthAllowed"/> is false, never goes back. Returns
    /// the largest forward step and the largest back step, in samples.
    /// </summary>
    private static (double Forward, double Back) Steps(List<float> o, int from)
    {
        // Forward: samples skipped between two neighbours beyond the one expected.
        // Back: how far the named index went backwards (a repeat reads 1).
        double fwd = 0, back = 0;
        for (int i = Math.Max(1, from); i < o.Count; i++)
        {
            double diff = Index(o[i]) - Index(o[i - 1]);
            if (diff - 1 > fwd) fwd = diff - 1;
            if (1 - diff > back && diff < 1 - 1e-6) back = 1 - diff;
        }
        return (fwd, back);
    }

    /// <summary>
    /// The most times any input sample was played at full weight: a sample counts
    /// only inside an exact run of three (its neighbours one less and one more),
    /// because inside a fade the blend of a ramp can land on a whole number by chance.
    /// </summary>
    private static int MostPlays(List<float> o, int from)
    {
        var counts = new Dictionary<long, int>();
        for (int i = Math.Max(1, from); i + 1 < o.Count; i++)
        {
            double x = Index(o[i]), p = Index(o[i - 1]), n = Index(o[i + 1]);
            if (Math.Abs(x - Math.Round(x)) > 1e-3 || Math.Abs(x - p - 1) > 1e-3 || Math.Abs(n - x - 1) > 1e-3) continue;
            long k = (long)Math.Round(x);
            counts[k] = counts.TryGetValue(k, out int c) ? c + 1 : 1;
        }
        return counts.Count == 0 ? 0 : counts.Values.Max();
    }

    private static void FeedUntil(AlignedSwitchTests.Rig rig, List<float> o, Func<bool> done, int max = 1200)
    {
        for (int i = 0; i < max && !done(); i++) rig.Feed(Ramp, 1, o);
        Assert.True(done(), "it never got there");
    }

    // ── A retime is a drain, never a jump ──

    [Fact]
    public void A_retime_is_a_drain_the_output_never_jumps_and_lands_exactly_on_the_new_delay()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true);
        rig.Slot.SharedPlan = Plan(3894, "rnnoise", "b");
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        Assert.Equal(3894, rig.Slot.HeldSamples);
        int start = o.Count;
        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        FeedUntil(rig, o, () => !rig.Slot.DrainingForTest && rig.Slot.HeldSamples == 1440);
        rig.Feed(Ramp, 5, o);
        var run = rig.Slot.LinesForTest!.Value.Run;
        var (fwd, back) = Steps(o, start);
        _out.WriteLine($"retime {rig.Slot.LastRetime}; {run.TotalPeriodSplices} period and {run.TotalGapSplices} gap splices, removed {run.TotalRemoved}; largest forward step {fwd:0}, back {back:0}; {(o.Count - start) / F} buffers");
        Assert.True(rig.Slot.LastRetime is { From: 3894, To: 1440, Drained: true }, $"the retime was {rig.Slot.LastRetime}");
        Assert.Equal(2454, run.TotalRemoved);
        Assert.True(back < 1, $"the output went back {back:0} samples during a drain");
        Assert.True(fwd <= RxLagShifter.MaxLagFramesAt(48000), $"a step of {fwd:0} samples, more than one splice");
        long last = rig.At - 1;
        Assert.Equal(last - 1440, (long)Math.Round(Index(o[^1])));
    }

    [Fact]
    public void Positive_control_today_s_retime_moves_the_whole_difference_inside_one_buffer()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true, shifter: false);
        rig.Slot.SharedPlan = Plan(3894, "rnnoise", "b");
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        int start = o.Count;
        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        rig.Feed(Ramp, 3, o);
        // Inside the one faded buffer the named index runs far ahead of time: the two taps blend.
        double spread = Index(o[start + F - 1]) - Index(o[start]) - (F - 1);
        _out.WriteLine($"today's retime: the fade buffer covers {spread:0} samples more than it lasts");
        Assert.True(spread > 2000);
    }

    [Fact]
    public void An_unpadded_engine_that_joins_a_plan_grows_to_it_and_never_repeats_a_region_twice()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: false);
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        Assert.Equal(-1, rig.Slot.HeldSamples);
        int start = o.Count;
        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        FeedUntil(rig, o, () => !rig.Slot.DrainingForTest && rig.Slot.HeldSamples == 1440);
        rig.Feed(Ramp, 5, o);
        var run = rig.Slot.LinesForTest!.Value.Run;
        int most = MostPlays(o, start);
        _out.WriteLine($"grown by {run.TotalAdded} in {run.TotalPeriodSplices + run.TotalGapSplices} splices; most plays of one sample {most}; retime {rig.Slot.LastRetime}");
        Assert.Equal(960, run.TotalAdded);
        Assert.True(most <= 2, $"a region was played {most} times");
        Assert.True(rig.Slot.LastRetime is { From: 480, To: 1440, Drained: true });
        Assert.Equal(rig.At - 1 - 1440, (long)Math.Round(Index(o[^1])));
    }

    // ── The repairs' invariants, on the drain ──

    [Fact]
    public void A_plan_republished_every_buffer_does_not_slow_the_drain_R2_B1()
    {
        int Buffers(bool republish)
        {
            using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: false);
            var o = new List<float>();
            rig.Feed(Ramp, 40, o);
            rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
            int n = 0;
            for (; n < 1200 && (n < 2 || rig.Slot.DrainingForTest); n++)
            {
                if (republish) rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
                rig.Feed(Ramp, 1, o);
            }
            Assert.Equal(1440, rig.Slot.HeldSamples);
            return n;
        }
        int once = Buffers(false), every = Buffers(true);
        _out.WriteLine($"landed after {once} buffers published once, {every} republished every buffer");
        Assert.True(every <= once + 2, $"republishing slowed it from {once} to {every} buffers");
    }

    [Fact]
    public void The_running_engine_follows_a_new_plan_while_an_incoming_engine_stays_late_R1_B1()
    {
        // The late "b" (240) is faster than anything rnnoise is heard at, so no pre-growth
        // competes with the plan. (A slower incoming engine pre-grows the running one,
        // and the bound on that is what lets the plan through: see the report.)
        using var rig = new LateRigLag(240, Plan(1440, "rnnoise", "b"));
        rig.Feed(40);
        Assert.Equal(1440, rig.Slot.HeldSamples);
        rig.ChooseB();
        rig.Feed(2);
        rig.Slot.SharedPlan = SharedDelayPlan.None;
        for (int i = 0; i < 1200 && (i < 5 || rig.Slot.DrainingForTest); i++) rig.Feed(1);
        rig.Feed(3);
        _out.WriteLine($"running {rig.Slot.RunningEngineId}, held {rig.Slot.HeldSamples}, heard at {rig.Lag}, retime {rig.Slot.LastRetime}");
        Assert.Equal("rnnoise", rig.Slot.RunningEngineId);
        Assert.Equal(-1, rig.Slot.HeldSamples);
        Assert.Equal(480, rig.Lag);
    }

    [Fact]
    public void A_switch_straight_after_a_new_plan_lands_the_new_plan_and_time_never_jumps_B2_and_M4()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true);
        rig.Slot.SharedPlan = Plan(3894, "rnnoise", "b");
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        int start = o.Count;
        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        rig.ChooseB();
        FeedUntil(rig, o, () => rig.Slot.RunningEngineId == "b" && !rig.Slot.DrainingForTest && rig.Slot.HeldSamples == 1440);
        rig.Feed(Ramp, 5, o);
        var (fwd, back) = Steps(o, start);
        _out.WriteLine($"switch {rig.Slot.LastSwitch}, held {rig.Slot.HeldSamples}; largest forward step {fwd:0}, back {back:0}");
        Assert.True(rig.Slot.LastSwitch is { Aligned: true });
        Assert.True(back < 1, "time went back");
        Assert.True(fwd <= RxLagShifter.MaxLagFramesAt(48000), $"time jumped {fwd:0} samples: a switch landed on a splice, or a stale hold was inherited");
        Assert.Equal(rig.At - 1 - 1440, (long)Math.Round(Index(o[^1])));
    }

    [Fact]
    public void Nine_markers_across_a_retime_come_out_nine_and_today_s_fade_does_not()
    {
        int Run(bool shifter)
        {
            using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true, shifter: shifter);
            rig.Slot.SharedPlan = Plan(3894, "rnnoise", "b");
            // Bursts every 30 ms from a second before the plan changes to six seconds after,
            // in silence: a drain that skipped or a fade that blended one away shows.
            long change = 40L * F;
            Func<long, float> signal = n =>
            {
                long k = (n - 300) % 1440;
                return n >= change - 4800 && n < change + 288000 && n >= 300 && k < 96 ? 0.5f * MathF.Sin(2 * MathF.PI * 1000 * (k + 0.5f) / 48000f) : 0f;
            };
            var o = new List<float>();
            rig.Feed(signal, 40, o);
            rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
            rig.Feed(signal, 700, o);
            int c = 0; bool inB = false;
            for (int i = 0; i + 48 <= o.Count; i += 48) { float p = 0; for (int j = 0; j < 48; j++) p = Math.Max(p, Math.Abs(o[i + j])); bool l = p > 0.15f; if (l && !inB) c++; inB = l; }
            Assert.Equal(1440, rig.Slot.HeldSamples);
            return c;
        }
        int expected = Enumerable.Range(0, 740 * F).Count(n => n >= 300 && (n - 300) % 1440 == 0 && n >= 40L * F - 4800 && n < 40L * F + 288000 && n < 740L * F - 1440);
        int shifted = Run(true), today = Run(false);
        _out.WriteLine($"retime: {expected} bursts in (to the end of what is heard), lag shifter {shifted} out, today's fade {today} out");
        Assert.Equal(expected, shifted);
        Assert.NotEqual(expected, today);
    }

    /// <summary>An engine that colours differently from its partner (so the two outputs' correlation varies buffer to buffer), then a pure delay.</summary>
    private sealed class ColourEngine : IDenoiseEngine
    {
        private readonly float[] _ring; private int _at; private float _prev; private readonly float _a, _b;
        public ColourEngine(string id, int delay, float a, float b) { Id = id; _ring = new float[delay]; LatencySamples = delay; _a = a; _b = b; }
        public string Id { get; }
        public int SampleRate => 48000; public int FrameLength => F; public int LatencySamples { get; }
        public bool IsAvailable => true;
        public IReadOnlyList<EngineSettingDescriptor> Settings => Array.Empty<EngineSettingDescriptor>();
        public EngineSettingValue GetSetting(string name) => throw new ArgumentException(name);
        public bool TrySetSetting(string name, EngineSettingValue value) => false;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            for (int i = 0; i < input.Length; i++) { float x = _a * input[i] + _b * _prev; _prev = input[i]; output[i] = _ring[_at]; _ring[_at] = x; if (++_at == _ring.Length) _at = 0; }
            return true;
        }
        public void Reset() { } public void Dispose() { }
    }

    [Fact]
    public void A_switch_inside_the_set_is_phase_1_s_sample_for_sample()
    {
        float[] Run(bool shifter)
        {
            DenoiseEngineEntry Row(string id, int d, float a, float b) => new(id, DenoiseSide.Pc, DenoiseFamily.Neural, "label",
                new[] { DenoiseDirection.Receive }, (_, _) => new ColourEngine(id, d, a, b), () => Unavailability.None);
            using var p = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(new[] { Row("rnnoise", 480, 0.85f, 0.15f), Row("b", 3408, 1.1f, -0.2f) }));
            var slot = p.NeuralSlot;
            slot.SharedPlan = Plan(3408, "rnnoise", "b");
            slot.ShifterOverrideForTest = shifter; slot.MuteOverrideForTest = false;
            slot.SetStrength("rnnoise", 1); slot.SetStrength("b", 1);
            p.RnnEnabled = true;
            var rng = new Random(49);
            var o = new List<float>();
            var audio = new float[2 * F];
            for (int k = 0; k < 120; k++)
            {
                if (k == 40)
                {
                    Assert.True(slot.Select("b", out _));
                    var t = System.Diagnostics.Stopwatch.StartNew();
                    while (!slot.BuildDelivered("b") && t.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(1);
                }
                // Voice-like: noise bursts at a syllable rate, so each buffer's correlation differs.
                for (int i = 0; i < F; i++) { long n = (long)k * F + i; float env = (n / 4800) % 2 == 0 ? 1f : 0.1f; audio[2 * i] = audio[2 * i + 1] = (float)(rng.NextDouble() - 0.5) * 0.3f * env; }
                p.Process(audio);
                for (int i = 0; i < F; i++) o.Add(audio[2 * i]);
            }
            Assert.Equal("b", slot.RunningEngineId);
            Assert.True(slot.LastSwitch is { Aligned: true });
            return o.ToArray();
        }
        Assert.Equal(Run(false), Run(true));
    }

    // ── An engine switch outside the set ──

    [Theory]
    [InlineData(480, 3408)]
    [InlineData(3408, 480)]
    [InlineData(480, 5024)]
    [InlineData(5024, 480)]
    public void Outside_the_set_nine_markers_come_out_nine_and_today_s_code_does_not(int a, int b)
    {
        var markers = new AlignedSwitchTests(_out);
        int shifted = markers.Markers(a, b, shared: false);
        int today = markers.Markers(a, b, shared: false, shifter: false);
        Assert.Equal(9, shifted);
        Assert.NotEqual(9, today);
    }

    [Theory]
    [InlineData(3408, 480)]
    [InlineData(480, 3408)]
    public void Outside_the_set_time_is_continuous_through_the_switch_and_the_new_engine_ends_at_its_own_delay(int a, int b)
    {
        using var rig = new AlignedSwitchTests.Rig(a, b, 1, shared: false);
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        int start = o.Count;
        rig.ChooseB();
        FeedUntil(rig, o, () => rig.Slot.RunningEngineId == "b" && !rig.Slot.DrainingForTest);
        rig.Feed(Ramp, 5, o);
        var (fwd, back) = Steps(o, start);
        int most = MostPlays(o, start);
        _out.WriteLine($"{a} to {b}: switch {rig.Slot.LastSwitch}; largest forward step {fwd:0}, back {back:0}; most plays {most}; lag {rig.At - 1 - Math.Round(Index(o[^1]))}");
        Assert.True(rig.Slot.LastSwitch is { Aligned: true });
        Assert.True(fwd <= RxLagShifter.MaxLagFramesAt(48000) && back <= RxLagShifter.MaxGapFramesAt(48000) + RxLagShifter.MaxLagFramesAt(48000));
        if (b < a) Assert.True(back < 1, "a drain went back");
        Assert.True(most <= 2);
        Assert.Equal(rig.At - 1 - b, (long)Math.Round(Index(o[^1])));
    }

    // ── Turning computer noise reduction on and off ──

    /// <summary>A neural slot with "rnnoise" at <paramref name="delay"/>, off, fed <paramref name="signal"/>; turns on at buffer 40 and off at <paramref name="offAt"/>.</summary>
    private static List<float> OnOff(int delay, Func<long, float> signal, bool shifter, int offAt, int total, out PcDenoiseSlot.TurnNote? on, out PcDenoiseSlot.TurnNote? off)
    {
        using var rig = new AlignedSwitchTests.Rig(delay, 960, 1, shared: false, shifter: shifter);
        rig.P.RnnEnabled = false;
        var o = new List<float>();
        rig.Feed(signal, 40, o);
        rig.P.RnnEnabled = true;
        on = null; off = null;
        for (int b = 40; b < total; b++)
        {
            if (b == offAt) { on = rig.Slot.LastTurn; rig.P.RnnEnabled = false; }
            rig.Feed(signal, 1, o);
        }
        off = rig.Slot.LastTurn;
        return o;
    }

    [Theory]
    [InlineData(960)]
    [InlineData(3408)]
    public void Turning_on_and_off_is_continuous_on_a_ramp_and_today_s_jumps(int delay)
    {
        var o = OnOff(delay, Ramp, shifter: true, offAt: 600, total: 1300, out var on, out var off);
        var (fwd, back) = Steps(o, 40 * F);
        int most = MostPlays(o, 40 * F);
        _out.WriteLine($"{delay}: on {on}, off {off}; largest forward step {fwd:0}, back {back:0}; most plays {most}; ends heard at {(1300 * F - 1) - Math.Round(Index(o[^1]))}");
        Assert.True(on is { On: true, FellBack: false, Delay: var d } && d == delay, $"turned on {on}");
        Assert.True(off is { On: false }, $"turned off {off}");
        Assert.True(fwd <= RxLagShifter.MaxLagFramesAt(48000) + 1, $"a jump of {fwd:0} samples");
        Assert.True(most <= 2, $"a region was played {most} times");
        Assert.Equal(1300L * F - 1, (long)Math.Round(Index(o[^1]))); // drained back to no delay at all
        // No silence at the turn-on: phase 1's pad started silent for its length.
        for (int i = 40 * F; i < 60 * F; i++) Assert.True(o[i] > 0, $"sample {i} is silent");

        var today = OnOff(delay, Ramp, shifter: false, offAt: 600, total: 1300, out _, out _);
        var (tf, tb) = Steps(today, 40 * F);
        _out.WriteLine($"today's code: largest forward step {tf:0}, back {tb:0}");
        Assert.True(tb >= delay - 1 || tf >= delay - 1, "the positive control did not jump");
    }

    [Theory]
    [InlineData(960)]
    [InlineData(3408)]
    public void Nine_markers_across_a_turn_on_and_a_turn_off_come_out_nine(int delay)
    {
        // Bursts every 30 ms around both events, in silence: anything replayed or skipped shows.
        Func<long, float> bursts = n =>
        {
            long k = (n - 500) % 1440;
            bool near = (n >= 36 * F && n < 46 * F) || (n >= 596 * F && n < 606 * F);
            return near && n >= 500 && k < 96 ? 0.5f * MathF.Sin(2 * MathF.PI * 1000 * (k + 0.5f) / 48000f) : 0f;
        };
        int Count(List<float> y) { int c = 0; bool inB = false; for (int i = 0; i + 48 <= y.Count; i += 48) { float p = 0; for (int j = 0; j < 48; j++) p = Math.Max(p, Math.Abs(y[i + j])); bool l = p > 0.15f; if (l && !inB) c++; inB = l; } return c; }
        int expected = Enumerable.Range(0, 1300 * F).Count(n => n >= 500 && (n - 500) % 1440 == 0 && ((n >= 36 * F && n < 46 * F) || (n >= 596 * F && n < 606 * F)));
        var y = OnOff(delay, bursts, shifter: true, offAt: 600, total: 1300, out _, out _);
        var t = OnOff(delay, bursts, shifter: false, offAt: 600, total: 1300, out _, out _);
        _out.WriteLine($"{delay}: {expected} bursts in; lag shifter {Count(y)} out; today's code {Count(t)} out");
        Assert.Equal(expected, Count(y));
        Assert.NotEqual(expected, Count(t));
    }

    // ── The cross-fade switch governs the new path (S11) ──

    [Fact]
    public void With_the_cross_fade_switch_off_an_outside_set_switch_is_today_s_cut()
    {
        int Run(bool crossFade)
        {
            using var rig = new AlignedSwitchTests.Rig(480, 3408, 1, shared: false);
            rig.Slot.CrossFadeOverrideForTest = crossFade;
            var o = new List<float>();
            rig.Feed(Ramp, 40, o);
            rig.ChooseB();
            FeedUntil(rig, o, () => rig.Slot.RunningEngineId == "b");
            rig.Feed(Ramp, 3, o);
            var (_, back) = Steps(o, 40 * F);
            _out.WriteLine($"cross-fade {(crossFade ? "on" : "off")}: switch {rig.Slot.LastSwitch}, largest back step {back:0}, pre-grown {rig.Slot.LinesForTest!.Value.Warm.TotalAdded + rig.Slot.LinesForTest!.Value.Run.TotalAdded}");
            return (int)back;
        }
        Assert.True(Run(true) < 1000, "with the switch on, time went back by more than the growth's splices");
        Assert.True(Run(false) >= 2900, "with the switch off, the switch was not today's cut");
    }

    // ── Mute absorbs the difference inside its silence ──

    [Fact]
    public void With_mute_on_the_difference_moves_inside_the_silence_and_nothing_drains_after()
    {
        using var rig = new AlignedSwitchTests.Rig(480, 3408, 1, shared: false, mute: true);
        var o = new List<float>();
        rig.Feed(Ramp, 40, o);
        rig.ChooseB();
        FeedUntil(rig, o, () => rig.Slot.RunningEngineId == "b");
        rig.Feed(Ramp, 20, o);
        var lines = rig.Slot.LinesForTest!.Value;
        _out.WriteLine($"switch {rig.Slot.LastSwitch}; draining {rig.Slot.DrainingForTest}; splices {lines.Run.TotalPeriodSplices + lines.Run.TotalGapSplices + lines.Warm.TotalPeriodSplices + lines.Warm.TotalGapSplices}");
        Assert.True(rig.Slot.LastSwitch is { Mute: true });
        Assert.False(rig.Slot.DrainingForTest);
        Assert.Equal(0, lines.Run.TotalPeriodSplices + lines.Run.TotalGapSplices + lines.Warm.TotalPeriodSplices + lines.Warm.TotalGapSplices);
        Assert.Equal(rig.At - 1 - 3408, (long)Math.Round(Index(o[^1])));
    }

    // ── The two slots share one budget; nothing allocates ──

    [Fact]
    public void Both_slots_spend_from_the_pipeline_s_one_budget()
    {
        using var p = new RxAudioPipeline();
        Assert.Same(p.SwitchBudget, p.SpectralSlot.LinesForTest!.Value.Run.Budget);
        Assert.Same(p.SwitchBudget, p.NeuralSlot.LinesForTest!.Value.Run.Budget);
        Assert.Same(p.SwitchBudget, p.NeuralSlot.LinesForTest!.Value.Dry.Budget);
    }

    [Fact]
    public void A_drain_a_turn_on_and_a_turn_off_allocate_nothing_per_buffer()
    {
        // The least of up to five fresh runs: see AlignedSwitchTests.LeastAllocated
        // for why one window can read a few KB that nothing allocated.
        long allocated = AlignedSwitchTests.LeastAllocated(() =>
        {
            using var rig = new AlignedSwitchTests.Rig(480, 1440, 1, shared: true);
            var audio = new float[2 * F];
            var rng = new Random(5);
            void Noise() { for (int i = 0; i < F; i++) audio[2 * i] = audio[2 * i + 1] = (float)(rng.NextDouble() - 0.5) * 0.2f; }
            for (int b = 0; b < 40; b++) { Noise(); rig.P.Process(audio); }
            var shorter = Plan(480, "rnnoise", "b");
            rig.P.RnnEnabled = false; Noise(); rig.P.Process(audio);   // sizes the switch buffer once, as phase 1's first switch does
            rig.P.RnnEnabled = true;
            for (int b = 0; b < 5; b++) { Noise(); rig.P.Process(audio); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            rig.Slot.SharedPlan = shorter;
            for (int b = 0; b < 200; b++) { Noise(); rig.P.Process(audio); }
            rig.P.RnnEnabled = false;
            for (int b = 0; b < 100; b++) { Noise(); rig.P.Process(audio); }
            rig.P.RnnEnabled = true;
            for (int b = 0; b < 200; b++) { Noise(); rig.P.Process(audio); }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            var lines = rig.Slot.LinesForTest!.Value;
            long splices = lines.Run.TotalPeriodSplices + lines.Run.TotalGapSplices + lines.Dry.TotalPeriodSplices + lines.Dry.TotalGapSplices;
            Assert.True(splices > 0, "nothing was drained while counting");
            return bytes;
        });
        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// Codex's check of 2567a596 (B4): the first smooth turn-off of a stream that
    /// had never switched engines allocated its scratch (3,864 bytes) on the audio
    /// thread, and the next buffer 24 more; the warmed loop above never saw either,
    /// because it does its first turn-off before it counts. Here every call is
    /// counted on its own from the first turn-off on: the turn-off, the drain after
    /// it, a turn-on and its growth, and the fade back. A fresh slot each time.
    /// </summary>
    [Theory]
    [InlineData(480)]
    [InlineData(960)]   // not the 10 ms buffer the slot sizes for when it is made
    public void The_first_turn_off_and_the_first_turn_on_allocate_nothing(int frames)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        DenoiseEngineEntry Row() => new("rnnoise", DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { DenoiseDirection.Receive },
            (_, _) => new AlignedSwitchTests.DelayGainEngine("rnnoise", 480, 1), () => Unavailability.None);
        var slot = new PcDenoiseSlot(DenoiseFamily.Neural, "rnnoise", 48000, 2, new DenoiseEngineCatalog(new[] { Row() }));
        try
        {
            slot.CrossFadeOverrideForTest = true; slot.ShifterOverrideForTest = true; slot.MuteOverrideForTest = false;
            slot.SetStrength("rnnoise", 1);
            slot.Enabled = true;
            var x = new float[2 * frames];
            var rng = new Random(4);
            void Next() { for (int i = 0; i < frames; i++) x[2 * i] = x[2 * i + 1] = (float)(rng.NextDouble() - 0.5) * 0.2f; }
            for (int i = 0; i < 80; i++) { Next(); slot.Process(x, true, true, false, null, RxContentClass.Classified | RxContentClass.NotVoiceRun); }
            Assert.Equal("rnnoise", slot.RunningEngineId);
            // A new length is handed to a pool thread on its first buffer; it is long ready by now.
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!slot.ScratchReadyForTest(2 * frames) && wait.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(1);
            Assert.True(slot.ScratchReadyForTest(2 * frames));
            var hits = new List<string>();
            var poolGrew = new List<string>();
            void Count(string what, int buffers)
            {
                for (int i = 0; i < buffers; i++)
                {
                    Next();
                    int threads = System.Threading.ThreadPool.ThreadCount;
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    slot.Process(x, true, true, false, null, RxContentClass.Classified | RxContentClass.NotVoiceRun);
                    long d = GC.GetAllocatedBytesForCurrentThread() - before;
                    // The slot's trace lines are queued to the thread pool (the brief's
                    // pattern). On a busy pool, a queue can make the pool add a worker on
                    // the queuing thread, which allocates (measured: 2,304 bytes as the pool
                    // grew 24 to 26 threads; an idle pool, nothing). That is reported, not
                    // hidden: it shows here as a call during which the pool grew.
                    if (d != 0) (System.Threading.ThreadPool.ThreadCount != threads ? poolGrew : hits).Add($"{what} buffer {i}: {d} bytes");
                }
            }
            slot.Enabled = false;
            Count("turn-off and its drain", 150 * F / frames);    // 1.5 s either way
            Assert.False(slot.TurningForTest, "the drain never ended");
            slot.Enabled = true;
            Count("turn-on, its growth and the fade", 150 * F / frames);
            Assert.Equal("rnnoise", slot.ProcessingEngineId);
            Assert.True(slot.LastTurn is { On: true, FellBack: false }, $"the turn-on was not the smooth one: {slot.LastTurn}");
            _out.WriteLine(hits.Count == 0 ? "nothing allocated" : string.Join("; ", hits));
            if (poolGrew.Count > 0) _out.WriteLine("while the thread pool added a worker: " + string.Join("; ", poolGrew));
            Assert.Empty(hits);
        }
        finally { slot.CloseAdmission(); slot.DisposeOwned(); }
    }

    /// <summary>
    /// The constructor's scratch covers a turn-off that comes before the pool thread
    /// has made one (Codex's re-check of 5a0a8c93, R4a): with the scratch maker held,
    /// as behind a busy pool, a turn-off on the stream's second buffer allocates
    /// nothing. Without the constructor's scratch it allocated 3,864 bytes here.
    /// </summary>
    [Fact]
    public void A_turn_off_before_the_pool_has_made_any_scratch_allocates_nothing()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        DenoiseEngineEntry Row() => new("rnnoise", DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { DenoiseDirection.Receive },
            (_, _) => new AlignedSwitchTests.DelayGainEngine("rnnoise", 480, 1), () => Unavailability.None);
        var slot = new PcDenoiseSlot(DenoiseFamily.Neural, "rnnoise", 48000, 2, new DenoiseEngineCatalog(new[] { Row() }));
        using var gate = new System.Threading.ManualResetEventSlim(false);
        try
        {
            slot.ScratchMakerGateForTest = gate;
            slot.CrossFadeOverrideForTest = true; slot.ShifterOverrideForTest = true; slot.MuteOverrideForTest = false;
            slot.SetStrength("rnnoise", 1);
            slot.Enabled = true;
            var x = new float[2 * F];
            for (int i = 0; i < F; i++) x[2 * i] = x[2 * i + 1] = (float)Math.Sin(i * 0.04) * 0.2f;
            for (int b = 0; b < 2; b++) slot.Process(x, true, true, false, null, RxContentClass.Classified | RxContentClass.NotVoiceRun);
            Assert.Equal("rnnoise", slot.RunningEngineId);
            slot.Enabled = false;
            long before = GC.GetAllocatedBytesForCurrentThread();
            slot.Process(x, true, true, false, null, RxContentClass.Classified | RxContentClass.NotVoiceRun);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            _out.WriteLine($"the first turn-off, the maker held: {bytes} bytes");
            Assert.True(slot.TurningForTest, "it did not take the smooth turn-off, so nothing was tested");
            Assert.Equal(0, bytes);
        }
        finally { gate.Set(); slot.ScratchMakerGateForTest = null; slot.CloseAdmission(); slot.DisposeOwned(); }
    }

    /// <summary>
    /// The slot's trace lines follow Track T's record-and-drain (Codex's re-check of
    /// 5a0a8c93): the buffer in which a turn-off happens only records it; the next
    /// buffer asks for the ring to be written, and a pool thread writes the lines in
    /// order (the turn-off, then the dry line's drain when it ends). Nothing is lost.
    /// </summary>
    [Fact]
    public void Trace_lines_are_recorded_by_the_buffer_and_written_after_it_in_order()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        DenoiseEngineEntry Row() => new("rnnoise", DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { DenoiseDirection.Receive },
            (_, _) => new AlignedSwitchTests.DelayGainEngine("rnnoise", 480, 1), () => Unavailability.None);
        var slot = new PcDenoiseSlot(DenoiseFamily.Neural, "rnnoise", 48000, 2, new DenoiseEngineCatalog(new[] { Row() }));
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            slot.TraceObserverForTest = lines.Enqueue;
            slot.CrossFadeOverrideForTest = true; slot.ShifterOverrideForTest = true; slot.MuteOverrideForTest = false;
            slot.SetStrength("rnnoise", 1);
            slot.Enabled = true;
            var x = new float[2 * F];
            var rng = new Random(8);
            void Next() { for (int i = 0; i < F; i++) x[2 * i] = x[2 * i + 1] = (float)(rng.NextDouble() - 0.5) * 0.2f; slot.Process(x, true, true, false, null, RxContentClass.Classified | RxContentClass.NotVoiceRun); }
            for (int b = 0; b < 40; b++) Next();
            slot.Enabled = false;
            Next();                                   // the turn-off: recorded only
            Assert.True(slot.TracePendingForTest, "the turn-off was not recorded");
            System.Threading.Thread.Sleep(100);
            Assert.Empty(lines);                      // nothing was handed to a writer from its own buffer
            for (int b = 0; b < 200 && slot.TurningForTest; b++) Next();
            Next();                                   // asks for the drain's line too
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (lines.Count < 2 && wait.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(5);
            var got = lines.ToArray();
            _out.WriteLine(string.Join(" | ", got));
            Assert.Equal(2, got.Length);
            Assert.Contains("turned off", got[0]);
            Assert.Contains("TurnOff", got[1]);
            Assert.DoesNotContain(got, l => l.Contains("lost"));
        }
        finally { slot.CloseAdmission(); slot.DisposeOwned(); }
    }

    /// <summary>A pipeline whose neural slot runs "rnnoise" (480) and can choose a forever-late "b".</summary>
    private sealed class LateRigLag : IDisposable
    {
        public RxAudioPipeline P { get; }
        public PcDenoiseSlot Slot => P.NeuralSlot;
        public long At;
        private readonly float[] _audio = new float[2 * F];
        public LateRigLag(int delayB, SharedDelayPlan plan)
        {
            var catalog = new DenoiseEngineCatalog(new[]
            {
                new DenoiseEngineEntry("rnnoise", DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { DenoiseDirection.Receive },
                    (_, _) => new AlignedSwitchTests.DelayGainEngine("rnnoise", 480, 1), () => Unavailability.None),
                new DenoiseEngineEntry("b", DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { DenoiseDirection.Receive },
                    (_, _) => new ForeverLate("b", delayB), () => Unavailability.None),
            });
            P = new RxAudioPipeline(catalog: catalog);
            Slot.SharedPlan = plan;
            Slot.MuteOverrideForTest = false;
            Slot.SetStrength("rnnoise", 1); Slot.SetStrength("b", 1);
            P.RnnEnabled = true;
        }
        public void Feed(int buffers)
        {
            for (int b = 0; b < buffers; b++)
            {
                for (int i = 0; i < F; i++) _audio[2 * i] = _audio[2 * i + 1] = Ramp(At + i);
                P.Process(_audio);
                At += F;
            }
        }
        public void ChooseB()
        {
            Assert.True(Slot.Select("b", out _));
            var t = System.Diagnostics.Stopwatch.StartNew();
            while (!Slot.BuildDelivered("b") && t.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(1);
        }
        public long Lag => (At - 1) - (long)Math.Round(_audio[^2] * Scale);
        public void Dispose() => P.Dispose();
    }

    private sealed class ForeverLate : IDenoiseEngine
    {
        private readonly float[] _ring; private int _at;
        public ForeverLate(string id, int delay) { Id = id; _ring = new float[delay]; LatencySamples = delay; }
        public string Id { get; }
        public int SampleRate => 48000; public int FrameLength => F; public int LatencySamples { get; }
        public bool IsAvailable => true;
        public IReadOnlyList<EngineSettingDescriptor> Settings => Array.Empty<EngineSettingDescriptor>();
        public EngineSettingValue GetSetting(string name) => throw new ArgumentException(name);
        public bool TrySetSetting(string name, EngineSettingValue value) => false;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            for (int i = 0; i < input.Length; i++) { output[i] = _ring[_at]; _ring[_at] = input[i]; if (++_at == _ring.Length) _at = 0; }
            return false;
        }
        public void Reset() { } public void Dispose() { }
    }
}
