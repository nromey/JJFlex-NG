using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using JJPortaudio;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track L2: S15 made observable (the 2026-11-19 integration-day
/// bench's finding 4, "no trace or readout of the derived gate threshold while
/// automatic"). While the automatic gate threshold is held during an over, one
/// trace line when the hold starts and one when it lets go, each with the
/// threshold in force. Windowless: MTA asserted; the owner's rig is
/// <see cref="TxGateOwnerTests.Context"/>'s uninitialised FlexBase, never
/// attached or connected.
/// </summary>
[Collection("Transmit conditioning statics")]
public sealed class GateThresholdHoldTraceTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private const string Prefix = "TxAudioConditioning: automatic gate threshold";
    private static readonly FieldInfo Held = typeof(TxAudioConditioning).GetField("_thresholdHeld", Any)!;

    public GateThresholdHoldTraceTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Held.SetValue(null, 0);
    }

    public void Dispose() => Held.SetValue(null, 0);

    private static void SetKeyed(TxGateOwnerTests.Context x, bool on)
    {
        var rig = (FlexBase)typeof(TxAudioConditioning).GetField("_rig", Any)!.GetValue(null)!;
        typeof(FlexBase).GetField("_Transmit", Any)!.SetValue(rig, on);   // the tick's key state (our Mox)
        x.Keyed = on;                                                    // the conditioner's
    }

    private static TxNoiseSetup Gate(bool auto, float margin) => new()
    {
        Gate = new() { Enabled = true, AutoThreshold = auto, ThresholdMarginDb = margin, ThresholdDb = -40,
            AttackMs = 5, HoldMs = 500, ReleaseMs = 300, RangeDb = 30 },
    };

    private static string Db(float v) => v.ToString("F1", CultureInfo.InvariantCulture);

    private static string[] Mine(HelperTestRig.TraceCapture capture) =>
        capture.Lines.Where(l => l.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();

    [Fact]
    public void An_over_with_the_automatic_gate_writes_one_line_when_the_hold_starts_and_one_when_it_lets_go_each_with_the_threshold()
    {
        using var x = new TxGateOwnerTests.Context();
        using var capture = new HelperTestRig.TraceCapture();
        x.FillMeter();
        TxAudioConditioning.ApplySetup(Gate(auto: true, margin: 8));
        x.Buffer(); x.Tick();                               // adopted unkeyed; the threshold derived
        float floor = x.Cond.RoomFloor.FloorDb; // Sprint 49 Track I (#1252): the rule reads the room meter at the expander's input
        Assert.True(x.Cond.Gate.Enabled && x.Cond.AutoThresholdInForce);
        Assert.Equal(floor + 8, x.Cond.Gate.ThresholdDb, 4);
        Assert.Empty(Mine(capture));                        // nothing held yet, nothing written

        SetKeyed(x, true); x.Buffer();
        x.Tick();
        TxAudioConditioning.ThresholdMarginDb = 13;         // a lone margin change mid-over: the threshold still holds
        x.Buffer(); x.Tick(); x.Tick();
        Assert.Equal(floor + 8, x.Cond.Gate.ThresholdDb, 4);
        var during = Mine(capture);
        Assert.Single(during);
        Assert.Contains("held for the over at " + Db(floor + 8) + " dB", during[0]);

        SetKeyed(x, false); x.Buffer(); x.Tick(); x.Tick();
        var all = Mine(capture);
        Assert.Equal(2, all.Length);
        Assert.Contains("released", all[1]);
        Assert.Contains("held at " + Db(floor + 8) + " dB, now " + Db(floor + 13) + " dB", all[1]);
    }

    [Fact]
    public void An_over_with_a_threshold_by_hand_or_the_gate_off_holds_nothing_and_writes_nothing()
    {
        using var x = new TxGateOwnerTests.Context();
        using var capture = new HelperTestRig.TraceCapture();
        x.FillMeter();

        TxAudioConditioning.ApplySetup(Gate(auto: false, margin: 8));   // a threshold by hand
        x.Buffer(); x.Tick();
        SetKeyed(x, true); x.Buffer(); x.Tick(); x.Tick();
        SetKeyed(x, false); x.Buffer(); x.Tick();
        Assert.Empty(Mine(capture));

        var off = Gate(auto: true, margin: 8);                          // automatic, but the gate off
        off.Gate.Enabled = false;
        TxAudioConditioning.ApplySetup(off);
        x.Buffer(); x.Tick();
        Assert.False(x.Cond.Gate.Enabled);
        SetKeyed(x, true); x.Buffer(); x.Tick();
        SetKeyed(x, false); x.Buffer(); x.Tick();
        Assert.Empty(Mine(capture));

        // Positive control, same rig and capture: the automatic gate on writes both lines.
        TxAudioConditioning.ApplySetup(Gate(auto: true, margin: 8));
        x.Buffer(); x.Tick();
        SetKeyed(x, true); x.Buffer(); x.Tick();
        SetKeyed(x, false); x.Buffer(); x.Tick();
        Assert.Equal(2, Mine(capture).Length);
    }
}
