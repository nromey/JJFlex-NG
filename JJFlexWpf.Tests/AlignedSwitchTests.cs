using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using JJPortaudio;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track P, phase 1 (#873): the shared receive delay and the aligned
/// switch, proved with engines that have REAL, different delays (the slot tests'
/// fakes report zero, which is why no test saw today's fault). Ported from the
/// follow-up research's marker test (2026-11-08, section 1), with today's switch
/// run first as the positive control. Windowless and silent: fake engines in a
/// real receive pipeline, nothing played.
/// </summary>
public sealed class AlignedSwitchTests
{
    private const int F = 480;
    private readonly ITestOutputHelper _out;
    public AlignedSwitchTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    /// <summary>An engine whose output is its input, times a gain, exactly <c>delay</c> samples later.</summary>
    internal sealed class DelayGainEngine : IDenoiseEngine
    {
        private readonly float[] _ring;
        private readonly float _gain;
        private int _at;
        public DelayGainEngine(string id, int delay, float gain) { Id = id; _ring = new float[delay]; _gain = gain; LatencySamples = delay; }
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
                float x = input[i] * _gain;
                if (_ring.Length == 0) { output[i] = x; continue; }
                output[i] = _ring[_at];
                _ring[_at] = x;
                if (++_at == _ring.Length) _at = 0;
            }
            return true;
        }
        public void Reset() { Array.Clear(_ring); _at = 0; }
        public void Dispose() { }
    }

    /// <summary>A receive pipeline whose neural slot starts on "rnnoise" (delay A) and can switch to "b" (delay B).</summary>
    internal sealed class Rig : IDisposable
    {
        public RxAudioPipeline P { get; }
        public PcDenoiseSlot Slot => P.NeuralSlot;
        public long At;
        public Rig(int delayA, int delayB, float gainB, bool shared, bool? mute = null, bool shifter = true)
        {
            DenoiseEngineEntry Row(string id, int delay, float gain) =>
                new(id, DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { DenoiseDirection.Receive },
                    (_, _) => new DelayGainEngine(id, delay, gain), () => Unavailability.None);
            P = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(new[] { Row("rnnoise", delayA, 1f), Row("b", delayB, gainB) }));
            if (shared)
                Slot.SharedPlan = new SharedDelayPlan(Math.Max(delayA, delayB),
                    new[] { new ReceiveSharedDelay.Member("rnnoise", null), new ReceiveSharedDelay.Member("b", null) });
            Slot.MuteOverrideForTest = mute ?? false;
            // Phase 2 (Track P2): false runs phase 1's paths, today's code, as the positive control.
            Slot.ShifterOverrideForTest = shifter;
            Slot.SetStrength("rnnoise", 1);
            Slot.SetStrength("b", 1);
            P.RnnEnabled = true;
        }

        /// <summary>Feed buffers of <paramref name="signal"/> (by absolute sample), keeping channel 0; returns the buffer index that switched, or -1.</summary>
        public int Feed(Func<long, float> signal, int buffers, List<float> output, string? switchTo = null)
        {
            int switched = -1;
            var audio = new float[2 * F];
            for (int b = 0; b < buffers; b++)
            {
                for (int i = 0; i < F; i++) audio[2 * i] = audio[2 * i + 1] = signal(At + i);
                bool before = Slot.RunningEngineId == switchTo;
                P.Process(audio);
                if (switchTo != null && !before && Slot.RunningEngineId == switchTo && switched < 0) switched = b;
                for (int i = 0; i < F; i++) output.Add(audio[2 * i]);
                At += F;
            }
            return switched;
        }

        /// <summary>Choose "b" and wait, feeding nothing, until its bank is built, so the warm-up starts on the next buffer: the switch instant is then the same on every run.</summary>
        public void ChooseB()
        {
            Assert.True(Slot.Select("b", out _));
            var t = Stopwatch.StartNew();
            while (!Slot.BuildDelivered("b") && t.ElapsedMilliseconds < 5000) Thread.Sleep(1);
            Assert.True(Slot.BuildDelivered("b"), "b's bank was never delivered");
        }

        public void Dispose() => P.Dispose();
    }

    private const int PreBuffers = 40;

    /// <summary>
    /// One switch from A to B: 40 buffers on A, then B chosen and fed until 20
    /// buffers after the switch. Returns channel 0's output and the first sample
    /// of the switch buffer.
    /// </summary>
    private static (float[] Output, long SwitchAt, PcDenoiseSlot.SwitchNote? Note, int HeldBefore, int HeldAfter) Switch(
        int delayA, int delayB, bool shared, Func<long, float> signal, float gainB = 0.8f, bool? mute = null, bool shifter = true)
    {
        using var rig = new Rig(delayA, delayB, gainB, shared, mute, shifter);
        var output = new List<float>();
        rig.Feed(signal, PreBuffers, output);
        int heldBefore = rig.Slot.HeldSamples;
        rig.ChooseB();
        int k = rig.Feed(signal, 80, output, "b");
        Assert.True(k >= 0, "the switch never happened");
        return (output.ToArray(), (PreBuffers + k) * (long)F, rig.Slot.LastSwitch, heldBefore, rig.Slot.HeldSamples);
    }

    // ── The marker test ──

    private const int Bursts = 9;

    /// <summary>
    /// Nine 2 ms 1 kHz bursts spread evenly across the input that either engine
    /// could be playing at the switch: from the slower engine's delay plus a
    /// buffer before the switch, to a buffer after it. Today's switch to a later
    /// engine plays that span twice where it overlaps; to an earlier one it skips it.
    /// </summary>
    private static Func<long, float> BurstsAround(long switchAt, int delayA, int delayB)
    {
        long from = switchAt - Math.Max(delayA, delayB) - F, to = switchAt + F;
        var starts = Enumerable.Range(0, Bursts).Select(i => from + (to - from) * i / (Bursts - 1)).ToArray();
        return n =>
        {
            foreach (long s in starts)
                if (n >= s && n < s + 96) return 0.5f * MathF.Sin(2 * MathF.PI * 1000 * (n - s) / 48000f);
            return 0f;
        };
    }

    /// <summary>Bursts in the output: runs of 1 ms blocks whose peak is above 0.15, separated by at least one quiet block.</summary>
    private static int CountBursts(float[] output)
    {
        int count = 0; bool inBurst = false;
        for (int b = 0; b + 48 <= output.Length; b += 48)
        {
            float peak = 0;
            for (int i = 0; i < 48; i++) peak = Math.Max(peak, Math.Abs(output[b + i]));
            bool loud = peak > 0.15f;
            if (loud && !inBurst) count++;
            inBurst = loud;
        }
        return count;
    }

    internal int Markers(int delayA, int delayB, bool shared, bool shifter = true)
    {
        // First run finds the switch instant (silence); it is the same on the second.
        var probe = Switch(delayA, delayB, shared, _ => 0f, shifter: shifter);
        var signal = BurstsAround(probe.SwitchAt, delayA, delayB);
        var run = Switch(delayA, delayB, shared, signal, shifter: shifter);
        // A pre-growth (Track P2) can take a buffer more or less on the markers than
        // on silence: place them again around the instant they produced, until it holds.
        for (int tries = 0; tries < 4 && run.SwitchAt != probe.SwitchAt; tries++)
        {
            probe = run;
            signal = BurstsAround(probe.SwitchAt, delayA, delayB);
            run = Switch(delayA, delayB, shared, signal, shifter: shifter);
        }
        // Phase 1's timing and an aligned switch's do not depend on the material; a
        // pre-growth's can settle a buffer either side, and the markers then still
        // straddle it (they span the slower delay plus a buffer either side).
        bool preGrowth = shifter && !shared && delayB > delayA;
        if (!preGrowth) Assert.Equal(probe.SwitchAt, run.SwitchAt);
        else Assert.InRange(Math.Abs(probe.SwitchAt - run.SwitchAt), 0, 20 * F);
        int bursts = CountBursts(run.Output);
        _out.WriteLine($"markers {delayA} to {delayB}, {(shared ? "shared delay" : "outside the set")}, {(shifter ? "lag shifter" : "today's code")}: {Bursts} in, {bursts} out (switch at sample {run.SwitchAt})");
        return bursts;
    }

    [Fact]
    public void Positive_control_today_s_switch_repeats_markers_to_a_later_engine_and_drops_them_to_an_earlier_one()
    {
        // Today's code: phase 1's outside-set switch (Track P2 moved this control onto
        // the shifter-off path, because phase 2 makes an outside-set switch aligned too).
        Assert.True(Markers(480, 3408, shared: false, shifter: false) > Bursts);
        Assert.True(Markers(3408, 480, shared: false, shifter: false) < Bursts);
    }

    [Fact]
    public void Nine_markers_across_the_switch_come_out_nine_in_both_directions()
    {
        Assert.Equal(Bursts, Markers(480, 3408, shared: true));
        Assert.Equal(Bursts, Markers(3408, 480, shared: true));
        Assert.Equal(Bursts, Markers(960, 1440, shared: true));
    }

    // ── No time moves: every output sample is the input exactly the held delay earlier ──

    [Theory]
    [InlineData(480, 3408)]
    [InlineData(3408, 480)]
    [InlineData(960, 2454)]
    public void Inside_the_set_the_output_stays_on_one_timeline_through_the_switch(int delayA, int delayB)
    {
        // A ramp: each sample's value names its own index, exactly, in a float.
        Func<long, float> ramp = n => n / 1048576f;
        var run = Switch(delayA, delayB, shared: true, ramp, gainB: 1f);
        int held = Math.Max(delayA, delayB);
        Assert.Equal(held, run.HeldBefore);
        Assert.Equal(held, run.HeldAfter);
        Assert.True(run.Note is { Aligned: true }, "the switch was not aligned");
        // Two identical signals: the correlation is 1, so the fade sums to the signal itself.
        Assert.InRange(run.Note!.Value.R0, 0.999f, 1f);
        for (int m = held; m < run.Output.Length; m++)
            Assert.True(Math.Abs(run.Output[m] - (m - held) / 1048576f) < 1e-5f, $"sample {m}: {run.Output[m]} is not the input {held} earlier");
        _out.WriteLine($"{delayA} to {delayB}: every sample the input exactly {held} samples earlier, through the switch at {run.SwitchAt}");
    }

    // ── A steady tone through the fade (the research's 500 Hz, 30.5 periods across the 61 ms case) ──

    private static double DipDb(float[] output, long switchAt)
    {
        double Peak(long from, int length) { float p = 0; for (long i = from; i < from + length; i++) p = Math.Max(p, Math.Abs(output[i])); return p; }
        double steady = Peak(switchAt - 9600, 4800);
        double worst = double.MaxValue;
        for (long b = switchAt - 96; b < switchAt + F + 96; b += 96) worst = Math.Min(worst, Peak(b, 96));
        return 20 * Math.Log10(steady / worst);
    }

    [Fact]
    public void A_500_Hz_tone_holds_its_level_through_an_aligned_switch_and_dips_through_today_s()
    {
        Func<long, float> tone = n => 0.5f * MathF.Sin(2 * MathF.PI * 500 * n / 48000f);
        foreach (var (a, b) in new[] { (480, 3408), (3408, 480) })
        {
            var today = Switch(a, b, shared: false, tone, gainB: 1f, shifter: false);
            var aligned = Switch(a, b, shared: true, tone, gainB: 1f);
            double dToday = DipDb(today.Output, today.SwitchAt), dAligned = DipDb(aligned.Output, aligned.SwitchAt);
            _out.WriteLine($"500 Hz, {a} to {b}: today's switch dips {dToday:0.0} dB, the aligned one {dAligned:0.00} dB");
            Assert.True(dToday > 6, $"positive control: today's switch should dip, measured {dToday:0.0} dB");
            Assert.True(dAligned < 0.5, $"the aligned switch dipped {dAligned:0.00} dB");
        }
    }

    // ── Nothing on the audio thread allocates ──

    /// <summary>
    /// The least of up to <paramref name="attempts"/> measurements, stopping at the
    /// first that reads nothing. Track P2 measured why one is not enough
    /// (2026-11-21): once receive pipelines have been built on the test thread,
    /// <c>GC.GetAllocatedBytesForCurrentThread</c> moves by 1 to 8 KB at intervals
    /// even across a loop of plain arithmetic that cannot allocate, with no
    /// collection counted, so a single window fails by chance (about one run in
    /// three, on x86 and x64). A real allocation on the audio path is the same on
    /// every attempt, so the least still catches it.
    /// </summary>
    internal static long LeastAllocated(Func<long> measure, int attempts = 5)
    {
        long least = long.MaxValue;
        for (int a = 0; a < attempts && least != 0; a++) least = Math.Min(least, measure());
        return least;
    }

    [Fact]
    public void A_padded_slot_and_its_warm_up_allocate_nothing_per_buffer()
    {
        var audio = new float[2 * F];
        long steady = 0, warming = LeastAllocated(() =>
        {
            using var rig = new Rig(480, 3408, 0.8f, shared: true);
            for (int b = 0; b < 20; b++) rig.P.Process(audio);           // running, padded to 3408
            Assert.Equal(3408, rig.Slot.HeldSamples);
            steady = LeastAllocated(() =>
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int b = 0; b < 50; b++) rig.P.Process(audio);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            });
            Assert.Equal(0, steady);
            rig.ChooseB();
            rig.P.Process(audio);                                       // the warm-up's first buffer sizes its scratch once, as before
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int b = 0; b < 5; b++) rig.P.Process(audio);           // warming: two banks, two pads, the correlation
            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
        Assert.Equal(0, steady);
        Assert.Equal(0, warming);
    }

    // ── Outside the set, nothing changes ──

    [Fact]
    public void With_no_shared_set_the_receive_slot_s_output_is_exactly_today_s()
    {
        var rng = new Random(873);
        var noise = Enumerable.Range(0, 200 * F).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
        float Signal(long n) => noise[n % noise.Length];
        using var rig = new Rig(960, 2448, 1f, shared: false);
        var got = new List<float>();
        rig.Feed(Signal, 100, got);
        Assert.Equal(-1, rig.Slot.HeldSamples);
        using var bank = new PerChannelDenoiserBank(() => new DelayGainEngine("rnnoise", 960, 1f), 2);
        var audio = new float[2 * F];
        var want = new List<float>();
        for (long at = 0; at < 100 * F; at += F)
        {
            for (int i = 0; i < F; i++) audio[2 * i] = audio[2 * i + 1] = Signal(at + i);
            bank.Process(audio, 1f);
            for (int i = 0; i < F; i++) want.Add(audio[2 * i]);
        }
        Assert.Equal(want, got);
    }

    // ── Transmit is unchanged ──

    [Fact]
    public void A_transmit_slot_s_output_is_sample_for_sample_a_bare_bank_s_even_with_a_shared_plan()
    {
        var catalog = new DenoiseEngineCatalog(new[]
        {
            new DenoiseEngineEntry("rnnoise", DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { DenoiseDirection.Receive, DenoiseDirection.Transmit },
                (_, _) => new DelayGainEngine("rnnoise", 960, 0.7f), () => Unavailability.None),
        });
        var slot = new PcDenoiseSlot(DenoiseFamily.Neural, "rnnoise", 48000, 2, catalog, DenoiseDirection.Transmit);
        slot.SharedPlan = new SharedDelayPlan(4000, new[] { new ReceiveSharedDelay.Member("rnnoise", null) });
        slot.MuteOverrideForTest = true;
        slot.Enabled = true;
        float strength = slot.GetStrength("rnnoise");
        using var bank = new PerChannelDenoiserBank(() => new DelayGainEngine("rnnoise", 960, 0.7f), 2);
        var rng = new Random(49);
        for (int b = 0; b < 60; b++)
        {
            var audio = Enumerable.Range(0, 2 * F).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
            var reference = (float[])audio.Clone();
            slot.Process(audio, true, true, false, null);
            bank.Process(reference, strength);
            Assert.Equal(reference, audio);
        }
        slot.CloseAdmission();
        slot.DisposeOwned();
    }

    // ── The Mute switch ──

    [Fact]
    public void The_mute_switch_fades_the_old_engine_out_holds_100_ms_of_silence_then_fades_the_new_one_in()
    {
        Func<long, float> tone = n => 0.5f * MathF.Sin(2 * MathF.PI * 500 * n / 48000f);
        var run = Switch(480, 3408, shared: true, tone, gainB: 1f, mute: true);
        Assert.True(run.Note is { Mute: true, Aligned: false });
        long gapStart = run.SwitchAt + F;
        for (long i = gapStart; i < gapStart + PcDenoiseSlot.MuteSamples; i++) Assert.Equal(0f, run.Output[i]);
        // The switch buffer ends at nothing, so the gap starts without a step.
        Assert.True(Math.Abs(run.Output[gapStart - 1]) < 0.01f);
        // After the ramp, the new engine at the held delay, unmuted.
        long back = gapStart + PcDenoiseSlot.MuteSamples + PcDenoiseSlot.MuteRampSamples;
        for (long i = back; i < back + 960; i++) Assert.True(Math.Abs(run.Output[i] - tone(i - 3408)) < 1e-4f);
        Assert.Equal(3408, run.HeldAfter);
    }

    [Fact]
    public void With_mute_off_there_is_no_gap()
    {
        Func<long, float> tone = n => 0.5f * MathF.Sin(2 * MathF.PI * 500 * n / 48000f);
        var run = Switch(480, 3408, shared: true, tone, gainB: 1f, mute: false);
        Assert.True(run.Note is { Mute: false });
        Assert.True(DipDb(run.Output, run.SwitchAt) < 0.5);
    }

    // ── The ramp is one primitive ──

    [Fact]
    public void The_shared_ramp_at_no_correlation_is_bit_for_bit_the_sine_and_cosine_the_band_and_the_takes_used()
    {
        for (int i = 0; i <= 20000; i++)
        {
            float t = i / 20000f;
            Assert.Equal(BitConverter.SingleToInt32Bits(MathF.Sin(t * MathF.PI / 2f)), BitConverter.SingleToInt32Bits(CrossFadeCurve.In(t)));
            Assert.Equal(BitConverter.SingleToInt32Bits(MathF.Cos(t * MathF.PI / 2f)), BitConverter.SingleToInt32Bits(CrossFadeCurve.Out(t)));
        }
    }

    [Fact]
    public void The_shared_ramp_keeps_power_for_signals_of_the_measured_correlation()
    {
        foreach (float r in new[] { 0f, 0.25f, 0.5f, 0.9f, 1f })
            for (int i = 0; i <= 100; i++)
            {
                float t = i / 100f, o = CrossFadeCurve.Out(t, r), n = CrossFadeCurve.In(t, r);
                Assert.InRange(o * o + n * n + 2 * r * o * n, 0.9999f, 1.0001f);
            }
        // At r = 1 the pair sums to one: a linear fade in effect, no bump.
        Assert.InRange(CrossFadeCurve.Out(0.5f, 1f) + CrossFadeCurve.In(0.5f, 1f), 0.9999f, 1.0001f);
    }

    // ── X6: the helper's receive queue depths ──

    [Fact]
    public void The_helper_s_receive_depth_rule_takes_premium_and_standard_apart_and_clamps_both()
    {
        Assert.Equal(5, WdspPipelineDepth.ReceiveDepthFor("nnr", "premium", 1, 5));
        Assert.Equal(5, WdspPipelineDepth.ReceiveDepthFor("nnr", "premium_cw", 1, 5));
        Assert.Equal(1, WdspPipelineDepth.ReceiveDepthFor("nnr", "standard", 1, 5));
        Assert.Equal(1, WdspPipelineDepth.ReceiveDepthFor("nnr", "standard_cw", 1, 5));
        Assert.Equal(1, WdspPipelineDepth.ReceiveDepthFor("nr2", null, 1, 5));
        Assert.Equal(WdspPipelineDepth.MinReceiveDepth, WdspPipelineDepth.ReceiveDepthFor("nr2", null, 0, 5));
        Assert.Equal(WdspPipelineDepth.MaxReceiveDepth, WdspPipelineDepth.ReceiveDepthFor("nnr", "premium", 2, 99));
        // Transmit keeps the ruled constants, whatever receive is set to.
        Assert.Equal(WdspPipelineDepth.NnrPremium, WdspPipelineDepth.For("nnr", "premium", DenoiseDirection.Transmit));
        Assert.Equal(WdspPipelineDepth.Nr2, WdspPipelineDepth.For("nr2", null, DenoiseDirection.Transmit));
        // A new setup's defaults are the ruled ones (S11).
        var config = new AudioOutputConfig();
        Assert.Equal(WdspPipelineDepth.NnrStandard, config.HelperQueueDepthStandard);
        Assert.Equal(WdspPipelineDepth.NnrPremium, config.HelperQueueDepthPremium);
    }

    [Fact]
    public void The_helper_engine_takes_its_receive_depth_from_the_directional_rule()
    {
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            JJFlexWpf.Tests.Infrastructure.RepoPaths.RequireRoot(), "JJFlexWpf", "WdspHelperEngine.cs"));
        Assert.Contains("WdspPipelineDepth.For(engineId, model, direction)", source);
    }
}
