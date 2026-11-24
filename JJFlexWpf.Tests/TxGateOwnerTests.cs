using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using JJPortaudio;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track H, repair round 4: the transmit gate's settings have ONE
/// owner, so a later lone change wins over a whole setup even while the audio
/// side is adopting it; and the owner's real gate path is run, not read. Built
/// on Codex's fixtures from its third re-check. Windowless: MTA asserted; the
/// rig is an uninitialised FlexBase holding only a conditioner and a loudness
/// meter, never attached, never connected, its finaliser suppressed.
/// </summary>
[Collection("Transmit conditioning statics")]
public sealed class TxGateOwnerTests
{
    public TxGateOwnerTests() => NoiseReductionPerChannelTests.AssertWindowless();

    /// <summary>The owner (TxAudioConditioning) with a rig that has only the numerical parts, and every static it touches put back afterwards.</summary>
    internal sealed class Context : IDisposable
    {
        private static readonly BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
        private readonly (FieldInfo field, object? value)[] _saved;
        public readonly TxNoiseChain Chain = new(TxNoiseChainTests.Catalog());
        public readonly TxAudioConditioner Cond = new();
        public readonly LufsMeter Meter = new();
        public bool Keyed;
        public Context()
        {
            _saved = new[] { "_rig", "_chain", "_autoThreshold", "_thresholdMarginDb", "_gateIntent", "_eqIntent", "_deEsserIntent", "_orderIntent", "_learnBaseline",
                    "_compIntent", "_limiterIntent", "_namedSetup", "_namedSetupAsApplied" }
                .Select(n => typeof(TxAudioConditioning).GetField(n, Flags)!).Select(f => (f, f.GetValue(null))).ToArray();
            var rig = (FlexBase)RuntimeHelpers.GetUninitializedObject(typeof(FlexBase));
            GC.SuppressFinalize(rig); // only the injected numerical fields exist; never enter radio teardown
            typeof(FlexBase).GetField("txConditioner", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig, Cond);
            typeof(FlexBase).GetField("txLufsMeter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig, Meter);
            typeof(TxAudioConditioning).GetField("_rig", Flags)!.SetValue(null, rig);
            TxAudioConditioning.UseChainForTest(Chain);
            typeof(TxAudioConditioning).GetField("_gateIntent", Flags)!.SetValue(null, new TxNoiseGateSetup());
            // Sprint 49 Track CL: the compressor and the limiter start OFF here, as after
            // a setup saved before they existed, so every test written before them still
            // starts from a chain with nothing on. (A session that has loaded nothing
            // starts with the limiter on; TxDynamicsSurfaceTests covers that state.)
            typeof(TxAudioConditioning).GetField("_compIntent", Flags)!.SetValue(null, new TxCompressorSetup());
            typeof(TxAudioConditioning).GetField("_limiterIntent", Flags)!.SetValue(null, new TxLimiterSetup());
            Chain.KeySource = () => Volatile.Read(ref Keyed);
            Cond.KeySource = () => Volatile.Read(ref Keyed);
        }
        public void Tick() => typeof(TxAudioConditioning).GetMethod("RefreshDerivedThreshold", Flags)!.Invoke(null, null);
        public void Buffer() => Cond.Process(Array.Empty<float>(), 0, 48000);
        public void FillMeter()
        {
            var a = new float[48000 * 2 * 14];
            for (int i = 0; i < a.Length; i += 2) a[i] = a[i + 1] = (float)(0.01 * Math.Sin(2 * Math.PI * 1000 * (i / 2) / 48000));
            Meter.Process(a, a.Length, 48000);
            Assert.True(Meter.Profile.IsValid);
            // Sprint 49 Track I (#1252): the rule's floor is the room meter at the
            // expander's input, learned unkeyed, so the same audio goes there too.
            bool was = Volatile.Read(ref Keyed);
            Keyed = false;
            for (int at = 0; at < a.Length; at += 960) Cond.Process(a.AsSpan(at, Math.Min(960, a.Length - at)).ToArray(), Math.Min(960, a.Length - at), 48000);
            Keyed = was;
            Assert.True(Cond.RoomFloor.HasFloor);
        }
        public void Dispose() { foreach (var s in _saved) s.field.SetValue(null, s.value); }
    }

    private static TxNoiseSetup GateSetup(bool auto, float margin, float range = 30) => new()
    {
        Gate = new() { Enabled = true, AutoThreshold = auto, ThresholdMarginDb = margin, ThresholdDb = -40,
            AttackMs = 5, HoldMs = 500, ReleaseMs = 300, RangeDb = range },
    };

    [Fact]
    public void The_owners_whole_setup_gate_waits_for_the_unkey_and_later_lone_levers_win()
    {
        using var x = new Context();
        x.Cond.Gate.HoldMs = 150;
        TxAudioConditioning.ApplySetup(GateSetup(false, 10)); // admitted while unkeyed, no buffer yet
        Assert.True(x.Cond.HasStagedGate);
        x.Keyed = true;
        for (int i = 0; i < 4; i++) { x.Buffer(); Assert.Equal(150, x.Cond.Gate.HoldMs); Assert.True(x.Cond.AutoThresholdInForce); }
        TxAudioConditioning.GateRangeDb = 12;
        TxAudioConditioning.ThresholdMarginDb = 13;
        Assert.Equal(12, x.Cond.Gate.RangeDb);
        x.Keyed = false; x.Buffer();
        Assert.False(x.Cond.HasStagedGate);
        Assert.Equal((true, 500f, 12f, -40f, false, 13f), (x.Cond.Gate.Enabled, x.Cond.Gate.HoldMs, x.Cond.Gate.RangeDb,
            x.Cond.Gate.ThresholdDb, x.Cond.AutoThresholdInForce, x.Cond.ThresholdMarginInForceDb));
        TxAudioConditioning.ApplySetup(GateSetup(false, 8, 20));
        TxAudioConditioning.GateEnabled = false; // a later on-or-off wins too
        x.Buffer();
        Assert.False(x.Cond.Gate.Enabled);
        Assert.Equal(20, x.Cond.Gate.RangeDb);
    }

    [Fact]
    public void The_derived_threshold_tick_uses_the_policy_in_force_until_the_setup_lands()
    {
        using var x = new Context();
        x.FillMeter();
        TxAudioConditioning.AutoThreshold = true;
        TxAudioConditioning.ThresholdMarginDb = 8;
        x.Buffer();
        TxAudioConditioning.ApplySetup(GateSetup(true, 12));
        x.Keyed = true; x.Buffer(); x.Tick();
        Assert.Equal(x.Cond.RoomFloor.FloorDb + 8, x.Cond.Gate.ThresholdDb, 4);
        x.Keyed = false; x.Buffer(); x.Tick();
        Assert.Equal(x.Cond.RoomFloor.FloorDb + 12, x.Cond.Gate.ThresholdDb, 4);
        TxAudioConditioning.ApplySetup(GateSetup(false, 10));
        x.Keyed = true; x.Buffer(); x.Tick();
        Assert.Equal(x.Cond.RoomFloor.FloorDb + 12, x.Cond.Gate.ThresholdDb, 4);
        x.Keyed = false; x.Buffer(); x.Tick();
        Assert.Equal(-40f, x.Cond.Gate.ThresholdDb);
    }

    /// <summary>Each lone gate writer, its new value, and how to read what the gate (or its policy) runs with.</summary>
    public static TheoryData<string> Writers() => new() { "depth", "attack", "hold", "release", "threshold", "margin", "auto", "onoff" };

    private static void Write(string writer)
    {
        switch (writer)
        {
            case "depth": TxAudioConditioning.GateRangeDb = 12; break;
            case "attack": TxAudioConditioning.GateAttackMs = 9; break;
            case "hold": TxAudioConditioning.GateHoldMs = 700; break;
            case "release": TxAudioConditioning.GateReleaseMs = 650; break;
            case "threshold": TxAudioConditioning.GateThresholdDb = -33; break;
            case "margin": TxAudioConditioning.ThresholdMarginDb = 13; break;
            case "auto": TxAudioConditioning.AutoThreshold = true; break;
            case "onoff": TxAudioConditioning.GateEnabled = false; break;
            default: throw new ArgumentException(writer);
        }
    }

    private static object InForce(TxAudioConditioner c, string writer) => writer switch
    {
        "depth" => c.Gate.RangeDb,
        "attack" => c.Gate.AttackMs,
        "hold" => c.Gate.HoldMs,
        "release" => c.Gate.ReleaseMs,
        "threshold" => c.Gate.ThresholdDb,
        "margin" => c.ThresholdMarginInForceDb,
        "auto" => c.AutoThresholdInForce,
        "onoff" => c.Gate.Enabled,
        _ => throw new ArgumentException(writer),
    };

    private static object Expected(string writer) => writer switch
    {
        "depth" => 12f, "attack" => 9f, "hold" => 700f, "release" => 650f, "threshold" => -33f,
        "margin" => 13f, "auto" => true, "onoff" => false, _ => throw new ArgumentException(writer),
    };

    private static void SetHook(TxAudioConditioner c, string name, Action? hook) =>
        typeof(TxAudioConditioner).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(c, hook);

    /// <summary>
    /// Codex's interleaving, for every lone gate writer: the audio side has
    /// read the gate state and adopted the whole setup (manual threshold,
    /// depth 30, margin 10, on), and is paused before it writes the gate; the
    /// operator's lone change arrives then. Round 3 lost it: the audio side
    /// applied the setup it had taken, and nothing was left to correct it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writers))]
    public void A_lone_gate_change_made_while_the_audio_side_adopts_a_setup_still_wins(string writer)
    {
        using var x = new Context();
        TxAudioConditioning.AutoThreshold = false;
        TxAudioConditioning.ApplySetup(GateSetup(false, 10, 30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        SetHook(x.Cond, "GateTakenForTest", () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); });
        Exception? error = null;
        var worker = new Thread(() => { try { x.Buffer(); } catch (Exception e) { error = e; } });
        worker.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the audio side never reached the hand-off, so this proves nothing");
            Assert.False(x.Cond.HasStagedGate); // it has adopted the setup
            Write(writer);                      // the owner's real setter
            SetHook(x.Cond, "GateTakenForTest", null);
            release.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(error);
            if (writer == "onoff") x.Buffer();  // on and off land on the next unkeyed buffer, by ruling
            Assert.Equal(Expected(writer), InForce(x.Cond, writer));
            x.Buffer();                         // and it stays
            Assert.Equal(Expected(writer), InForce(x.Cond, writer));
        }
        finally
        {
            release.Set();
            if (worker.IsAlive) Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
            SetHook(x.Cond, "GateTakenForTest", null);
        }
    }

    [Fact]
    public void Track_H2s_gate_entry_points_say_their_timing_and_go_through_the_one_owner()
    {
        // SetGateLevel, SetThresholdMargin and SetAutoThreshold are Track H2's
        // shape (5bf60c11), kept here on the one owner so the branches agree.
        using var x = new Context();
        Assert.Equal(TxChangeTiming.Now, TxAudioConditioning.SetGateLevel("depth", 12).Timing);
        Assert.Equal(12f, x.Cond.GateInForce.RangeDb);
        Assert.Equal(TxChangeTiming.Refused, TxAudioConditioning.SetGateLevel("volume", 1).Timing);
        x.Keyed = true;
        TxAudioConditioning.ApplySetup(GateSetup(false, 10, 30));          // held: keyed
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetGateLevel("hold", 700).Timing);
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetThresholdMargin(13).Timing);
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetAutoThreshold(true).Timing);
        x.Keyed = false;
        x.Chain.ApplyDeferred();
        x.Buffer();
        Assert.Equal((700f, 13f, true), (x.Cond.Gate.HoldMs, x.Cond.ThresholdMarginInForceDb, x.Cond.AutoThresholdInForce));
    }

    [Fact]
    public void The_same_change_made_before_the_audio_side_reads_the_gate_rides_the_setup_in()
    {
        // The control: before the hand-off, the change lands in the waiting set.
        using var x = new Context();
        TxAudioConditioning.ApplySetup(GateSetup(false, 10, 30));
        TxAudioConditioning.GateRangeDb = 12;
        Assert.True(x.Cond.HasStagedGate);
        x.Buffer();
        Assert.Equal((12f, 500f), (x.Cond.Gate.RangeDb, x.Cond.Gate.HoldMs));
    }

    [Fact]
    public void The_new_view_adoption_and_parameter_read_allocate_nothing()
    {
        // Scoped to the methods repair round 3 added, as Codex measured them.
        // The whole transmit callback is NOT allocation-free (about 144 bytes
        // a buffer, inherited, for the register); this claims nothing about it.
        var c = new TxNoiseChain(TxNoiseChainTests.Catalog());
        var slot = c.NeuralSlot;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var adopt = (Action)typeof(PcDenoiseSlot).GetMethod("AdoptPendingView", flags)!.CreateDelegate(typeof(Action), slot);
        var read = (Func<string, object>)typeof(PcDenoiseSlot).GetMethod("ParametersForAudio", flags)!.CreateDelegate(typeof(Func<string, object>), slot);
        for (int i = 0; i < 100; i++) { adopt(); GC.KeepAlive(read("rnnoise")); }
        c.ApplySlots(null, new() { EngineId = "rnnoise" });
        Assert.True(slot.HasPendingView);
        long before = GC.GetAllocatedBytesForCurrentThread();
        adopt();
        for (int i = 0; i < 100; i++) GC.KeepAlive(read("rnnoise"));
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
        Assert.False(slot.HasPendingView);
    }

    [Fact]
    public void The_gate_adoption_on_the_audio_side_allocates_nothing()
    {
        var cond = new TxAudioConditioner();
        cond.StageGate(new TxAudioConditioner.StagedGate { Enabled = true, RangeDb = 30 });
        var update = (Action<bool>)typeof(TxAudioConditioner).GetMethod("UpdateGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Action<bool>), cond);
        update(true); // warm the delegate and JIT, keyed so nothing is adopted
        long before = GC.GetAllocatedBytesForCurrentThread();
        update(false);
        for (int i = 0; i < 100; i++) update(false);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
        Assert.Equal((true, 30f), (cond.Gate.Enabled, cond.Gate.RangeDb));
    }

    [Fact]
    public void Later_continuous_value_and_a_second_whole_replacement_are_not_lost()
    {
        var c = new TxNoiseChain(TxNoiseChainTests.Catalog());
        bool keyed = false;
        c.KeySource = () => keyed;
        c.SetStrength("rnnoise", 1);
        c.SetEnabled(DenoiseFamily.Neural, true);
        float Run() { var a = Enumerable.Repeat(1f, 960).ToArray(); c.Process(a, a.Length, 48000, keyed); return a[0]; }
        TxNoiseSlotSetup Setup(float amount, float strength = 1) => new() { EngineId = "rnnoise", Enabled = true,
            Engines = { new() { EngineId = "rnnoise", Direction = DenoiseDirection.Transmit, Strength = strength,
                Values = { new() { Name = "amount", Number = amount } } } } };
        Assert.Equal(2f, Run());
        c.ApplySlots(null, Setup(2)); keyed = true; Assert.Equal(2f, Run());
        c.SetSetting("rnnoise", "amount", new(3)); Assert.Equal(6f, Run());
        keyed = false; Assert.Equal(6f, Run()); Assert.False(c.NeuralSlot.HasPendingView);
        c.ApplySlots(null, Setup(2)); c.ApplySlots(null, Setup(4, .5f));
        keyed = true; Assert.Equal(6f, Run());
        keyed = false; Assert.Equal(4.5f, Run());
        Assert.False(c.NeuralSlot.HasPendingView);
        Assert.Equal(4.5f, Run());
    }
}
