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

        // No buffer between the new plan and the switch: the running engine
        // never got to retime, so the switch itself must land the new plan.
        rig.Slot.SharedPlan = Plan(1440, "rnnoise", "b");
        rig.ChooseB();
        rig.Feed(Ramp, 40, o, "b");
        _out.WriteLine($"held {rig.Slot.HeldSamples}, switch {rig.Slot.LastSwitch}");
        Assert.Equal(1440, rig.Slot.HeldSamples);
        // Time moved once (3,894 to 1,440) at the switch, so it is not called aligned.
        Assert.True(rig.Slot.LastSwitch is { Aligned: false, Held: 1440 }, $"the switch was {rig.Slot.LastSwitch}");
        Assert.Equal(1440, Lag(rig, o));
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
}
