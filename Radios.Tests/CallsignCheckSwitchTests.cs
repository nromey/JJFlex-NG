using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// The callsign check is the operator's choice (#1306), and it starts OFF.
/// Ruled by Noel 2026-11-24: "A ham should be able to enter their callsign or
/// not and still be able to use their radio"; one switch for the whole
/// computer; its default off ("Transmit interlock on a radio has to be turned
/// on"); and off means "It just goes through".
/// </summary>
/// <remarks>
/// <para>The gate's own tests (<c>CallsignGateTests</c>,
/// <c>FixerNoCallsignTests</c>, two in <c>TransmitKillSwitchTests</c>) now
/// turn the check on and hold what it refuses. These hold the other half: with
/// the check off, the SAME presses on the same fake radio, with no callsign at
/// all, go out on every chokepoint, and the Fix tool's stages are not stopped.
/// Each one has the check-on refusal beside it as its positive control, so a
/// green run cannot mean the fake radio simply refuses nothing.</para>
/// <para>The callsign in these tests is the conventional placeholder N0CALL.</para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class CallsignCheckSwitchTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(CallsignCheckSwitchTests));
    private readonly CallsignCheckScope _checkOff = new(on: false);

    public void Dispose()
    {
        _checkOff.Dispose();
        _scope.Dispose();
    }

    private static void RunQueue(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0)
        {
            var item = c.F.Rig.q.Dequeue().Item;
            if (item is FlexBase.FunctionDel a) a();
        }
    }

    private static int Sent(RadioCase c, string fragment) =>
        c.F.Vendor.Transport.Commands.Count(x => x.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>A radio whose operator has no callsign at all.</summary>
    private static RadioCase NoCallsign()
    {
        var c = new RadioCase();
        c.F.Rig.Callouts.GetOperatorCallsign = () => "";
        return c;
    }

    // ── The default ─────────────────────────────────────────────────────

    /// <summary>
    /// The ruled default, read from a FRESH copy of the radio layer: this
    /// process's own copy has been set by other tests, so asking it would read
    /// whatever ran last rather than what an application starts with.
    /// </summary>
    [Fact]
    public void The_check_starts_off_in_a_freshly_loaded_radio_layer()
    {
        var context = new AssemblyLoadContext("callsign-check-default-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var fresh = context.LoadFromAssemblyPath(typeof(FlexBase).Assembly.Location);
            Assert.NotSame(typeof(FlexBase).Assembly, fresh);   // positive control: really another copy
            var property = fresh.GetType("Radios.FlexBase", throwOnError: true)!.GetProperty(nameof(FlexBase.CallsignCheckOn))!;
            Assert.False((bool)property.GetValue(null)!, "the callsign check must start off (ruled 2026-11-24 13:43-13:44)");
        }
        finally { context.Unload(); }
    }

    [Fact]
    public void Off_the_gate_reports_no_missing_callsign_and_never_reads_one()
    {
        using var c = new RadioCase();
        int reads = 0;
        c.F.Rig.Callouts.GetOperatorCallsign = () => { reads++; return ""; };
        Assert.False(c.F.Rig.OperatorCallsignMissing);
        Assert.Equal(0, reads);
        // Positive control: on, the same rig is missing its callsign.
        using (new CallsignCheckScope(on: true))
            Assert.True(c.F.Rig.OperatorCallsignMissing);
        Assert.True(reads > 0);
    }

    [Fact]
    public void Off_a_provider_that_throws_does_not_refuse()
    {
        using var c = new RadioCase();
        c.F.Rig.Callouts.GetOperatorCallsign = () => throw new InvalidOperationException("no operator");
        Assert.False(c.F.Rig.OperatorCallsignMissing);
        using (new CallsignCheckScope(on: true))
            Assert.True(c.F.Rig.OperatorCallsignMissing);   // on, unchanged: refusing is the safe failure
    }

    // ── Every chokepoint, off ───────────────────────────────────────────

    [Fact]
    public void Off_transmit_goes_out_with_no_callsign()
    {
        using var c = NoCallsign();
        c.F.Rig.Transmit = true;
        RunQueue(c);
        Assert.Equal(1, Sent(c, "xmit 1"));
        c.F.Rig.Transmit = false;
        RunQueue(c);
        // Positive control: on, the same press is refused.
        using (new CallsignCheckScope(on: true))
        {
            c.F.Rig.Transmit = true;
            RunQueue(c);
            Assert.Equal(1, Sent(c, "xmit 1"));
        }
    }

    [Fact]
    public void Off_tune_goes_out_with_no_callsign()
    {
        using var c = NoCallsign();
        int before = Sent(c, "tune");
        c.F.Rig.TxTune = true;
        RunQueue(c);
        Assert.True(Sent(c, "tune") > before);
    }

    [Theory]
    [InlineData("manual", "transmit tune 1")]
    [InlineData("auto", "atu start")]
    public void Off_the_antenna_tuner_starts_with_no_callsign(string type, string command)
    {
        using var c = NoCallsign();
        typeof(FlexBase).GetField("_FlexTunerType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, type == "manual" ? FlexBase.FlexTunerTypes.manual : FlexBase.FlexTunerTypes.auto);
        int before = Sent(c, command);
        c.F.Rig.FlexTunerOn = true;
        RunQueue(c);
        Assert.True(Sent(c, command) > before);
    }

    [Fact]
    public void Off_CW_messages_and_CW_typing_go_out_with_no_callsign()
    {
        using var c = NoCallsign();
        Assert.True(c.F.Rig.SendCW("TEST"));
        Assert.True(c.F.Rig.SendCW('E'));
        using (new CallsignCheckScope(on: true))
            Assert.False(c.F.Rig.SendCW("TEST"));   // positive control
    }

    [Fact]
    public void Off_simple_VOX_turns_on_with_no_callsign()
    {
        using var c = NoCallsign();
        Assert.True(c.F.Rig.VoxOnWouldTurnOnSimpleVox);
        c.F.Rig.Vox = FlexBase.OffOnValues.on;
        RunQueue(c);
        Assert.True(c.F.Vendor.Radio.SimpleVOXEnable);
    }

    [Fact]
    public void Off_break_in_turns_on_with_no_callsign()
    {
        using var c = NoCallsign();
        Field(c.Tx, "_demodMode", "CW");
        Assert.True(c.F.Rig.VoxOnWouldTurnOnBreakIn);
        c.F.Rig.Vox = FlexBase.OffOnValues.on;
        RunQueue(c);
        Assert.True(c.F.Vendor.Radio.CWBreakIn);
    }

    [Fact]
    public void Off_a_recording_sent_through_VOX_plays_with_no_callsign()
    {
        using var c = NoCallsign();
        c.F.Rig.TxFilePlayer.Load(new float[48000], 48000, "message");
        c.F.Rig.TxFileStartForVox();
        Assert.True(c.F.Rig.TxFilePlaying);
        c.F.Rig.TxFileStop();
    }

    [Fact]
    public void Off_the_kill_switch_raises_its_carrier_with_no_callsign()
    {
        TransmitKillSwitch.ResetForTests();
        var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests", GetOperatorCallsign = () => "" });
        try
        {
            Assert.Null(TransmitKillSwitch.RefusedForNoCallsign(rig, "a check"));
            using (TransmitKillSwitch.Arm(rig, "the transmitter check"))
            {
                Assert.True(TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox));
                Assert.Null(TransmitKillSwitch.LastRaiseRefusal);
            }
            // Positive control: on, the same question names the sentence.
            using (new CallsignCheckScope(on: true))
                Assert.Equal(FlexBase.NoCallsignRefusal, TransmitKillSwitch.RefusedForNoCallsign(rig, "a check"));
        }
        finally
        {
            TransmitKillSwitch.ResetForTests();
            try { rig.Dispose(); } catch { }
        }
    }

    [Fact]
    public void Off_the_Fix_tools_tune_stage_reaches_its_countdown_with_no_callsign()
    {
        using var c = NoCallsign();
        bool counted = false;
        var gate = new FixerTransmitGate();
        gate.BeginRun("TX-CALLSIGN-OFF");
        gate.DeclareLoad("50 ohm dummy load on ANT1", FixerLoadKind.DummyLoad);
        TxTuneProbe.Result r = FixerTransmitBoundary.ProbeTransmitter(gate, () => c.F.Rig, TransmitStageSet.TransmitterCheck,
            countdown: () => counted = true, stopRequested: () => counted,
            countdownKeyUpAtMs: 50, countdownOn: () => true)();
        Assert.True(counted, "the stage was stopped before its countdown: " + r.SkipDetail);
        Assert.NotEqual(FlexBase.NoCallsignRefusal, r.SkipDetail);
    }

    // ── Wiring, from source ─────────────────────────────────────────────

    private static string Src(string relative) =>
        File.ReadAllText(Path.Combine(IntegrationPassTree.Root, relative)).Replace("\r\n", "\n");

    /// <summary>
    /// The one control point: the radio's answer asks the switch before it
    /// reads any callsign, and the doors' answer asks it before the test-only
    /// override, so a door test cannot pass without the switch being seen.
    /// </summary>
    [Fact]
    public void The_switch_is_asked_first_at_the_one_control_point_and_at_the_doors()
    {
        string gate = Src("Radios/FlexBase.CallsignGate.cs");
        Assert.Contains("public bool OperatorCallsignMissing => CallsignCheckOn && CallsignMissing(Callouts?.GetOperatorCallsign);", gate, StringComparison.Ordinal);
        string doors = Src("JJFlexWpf/ZeroPowerDoors.cs");
        int sw = doors.IndexOf("if (!FlexBase.CallsignCheckOn) return false;", StringComparison.Ordinal);
        int over = doors.IndexOf("if (NoCallsignOverride != null) return NoCallsignOverride(rig);", StringComparison.Ordinal);
        Assert.True(sw > 0 && over > sw, "the doors must ask the switch before the override");
    }
}
