#nullable enable
using System;
using System.IO;
using System.Linq;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// #1307, ruled by Noel 2026-11-26 (question 7 of the 41-question sheet): the
/// transmit rules (the TX band lockout and the 60 m rules) are asked on every
/// route the callsign check asks on, except tune: transmit going on, CW sends
/// and CW typing, the antenna tuner, turning VOX or break-in on, and the Fix
/// tool's transmit stages. An unkey, tune off, tuner off and VOX off are never
/// refused, and a refusal never comes behind a call that already returned true.
/// </summary>
/// <remarks>
/// Walked against the vendor fake radio, as <see cref="CallsignGateTests"/>
/// walks the callsign at the same chokepoints. The application's judge is stood
/// in for by the delegate the app hands the radio,
/// <see cref="FlexBase.OpenParms.JudgeTransmitHere"/>. The callsign check is
/// turned off here, so only the rules decide.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class TransmitRulesChokepointTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(TransmitRulesChokepointTests));
    private readonly CallsignCheckScope _checkOff = new(on: false);
    public void Dispose()
    {
        _checkOff.Dispose();
        _scope.Dispose();
    }

    private static readonly TransmitVerdict Refused = TransmitVerdict.LockoutFrequency;
    private TransmitVerdict _verdict = Refused;
    private int _asked;
    private bool? _lastSixty;

    private RadioCase Judged()
    {
        var c = new RadioCase();
        c.F.Rig.Callouts.JudgeTransmitHere = sixty => { _asked++; _lastSixty = sixty; return _verdict; };
        return c;
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

    [Fact]
    public void Transmit_on_is_refused_and_transmit_off_is_not()
    {
        using var c = Judged();
        c.F.Rig.Transmit = true;
        RunQueue(c);
        Assert.Equal(0, Sent(c, "xmit 1"));
        Assert.Equal(0, c.F.Rig.q.Count);
        Assert.True(_lastSixty);                   // transmit asks the 60 m rule too
        _asked = 0;
        c.F.Rig.Transmit = false;                  // an unkey is never refused, nor even asked
        Assert.Equal(1, c.F.Rig.q.Count);
        RunQueue(c);
        Assert.Equal(0, _asked);
        _verdict = TransmitVerdict.Allowed;        // positive control: the same press keys
        c.F.Rig.Transmit = true;
        RunQueue(c);
        Assert.Equal(1, Sent(c, "xmit 1"));
    }

    [Fact]
    public void Tune_is_never_asked()
    {
        using var c = Judged();
        int before = Sent(c, "tune");
        c.F.Rig.TxTune = true;
        RunQueue(c);
        Assert.True(Sent(c, "tune") > before);
        Assert.Equal(0, _asked);
        c.F.Rig.TxTune = false;
        RunQueue(c);
        Assert.Equal(0, _asked);
    }

    [Theory]
    [InlineData("manual", "transmit tune 1")]
    [InlineData("auto", "atu start")]
    public void The_antenna_tuner_is_refused_by_the_lockout_alone_and_off_is_never_asked(string type, string command)
    {
        using var c = Judged();
        typeof(FlexBase).GetField("_FlexTunerType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, type == "manual" ? FlexBase.FlexTunerTypes.manual : FlexBase.FlexTunerTypes.auto);
        int before = Sent(c, command);
        c.F.Rig.FlexTunerOn = true;
        RunQueue(c);
        Assert.Equal(before, Sent(c, command));
        Assert.False(c.F.Rig.FlexTunerOn);
        Assert.False(_lastSixty);                  // the 60 m rule is not asked for the tuner
        _verdict = TransmitVerdict.Allowed;
        c.F.Rig.FlexTunerOn = true;
        RunQueue(c);
        Assert.True(Sent(c, command) > before);
        // Repair round 1 (the Opus checker's N5): off, really. With the rules
        // refusing again, turning the tuner off is never refused, nor asked.
        Assert.True(c.F.Rig.FlexTunerOn);
        _verdict = Refused;
        _asked = 0;
        c.F.Rig.FlexTunerOn = false;
        RunQueue(c);
        Assert.False(c.F.Rig.FlexTunerOn);
        Assert.Equal(0, _asked);
    }

    /// <summary>
    /// Repair round 1 (Codex's and the Opus checker's #1307 blocker): the
    /// automatic tuner's own queued key, <c>Transmit = true</c>, reached
    /// <c>EnqueueKeyOn</c> asking the 60 m rule, so with the lockout off and the
    /// 60 m rule refusing, our key was refused and spoken while <c>atu start</c>
    /// still went out. Judged here by the real pure judge
    /// (<see cref="TransmitRules.Judge"/>) on every US operator's defaults, lockout
    /// off and the box on, for a transmit slice on 60 m between the channels.
    /// </summary>
    [Fact]
    public void The_automatic_tuner_s_own_key_asks_the_lockout_alone_so_its_key_and_its_start_both_go_out()
    {
        using var c = new RadioCase();
        var license = new LicenseConfig { LicenseClass = HamBands.Bands.Licenses.extra, Country = "US" };
        Assert.False(license.TxLockout);
        Assert.True(license.EnforceTxRules);
        var between = new TransmitSliceReading(5_340_000, "USB", 100, 2800) { TxFilterLow = 100, TxFilterHigh = 2900 };
        var asked = new System.Collections.Generic.List<bool>();
        var markerWhenAsked = new System.Collections.Generic.List<bool>();
        c.F.Rig.Callouts.JudgeTransmitHere = sixty =>
        {
            asked.Add(sixty);
            markerWhenAsked.Add(FlexBase.TunerKeyMarkerSet);
            return TransmitRules.Judge(license, between, 0, 0, 0, sixty, out _, out _, out _);
        };
        Assert.False(c.F.Rig.TransmitRulesVerdict(sixtyMeterRule: true).IsAllowed);   // positive control: the rule refuses here
        asked.Clear(); markerWhenAsked.Clear();

        typeof(FlexBase).GetField("_FlexTunerType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, FlexBase.FlexTunerTypes.auto);
        c.F.Rig.FlexTunerOn = true;
        RunQueue(c);

        Assert.Equal(new[] { false, false }, asked);        // the setter's ask, then the queued key's
        Assert.Equal(new[] { false, true }, markerWhenAsked);
        Assert.Equal(1, Sent(c, "xmit 1"));
        Assert.Equal(1, Sent(c, "atu start"));
        Assert.True(c.F.Rig.FlexTunerOn);
        Assert.False(FlexBase.TunerKeyMarkerSet);

        // Any other key still asks the 60 m rule, and is refused here.
        asked.Clear();
        c.F.Rig.FlexTunerOn = false;
        RunQueue(c);
        c.F.Rig.Transmit = true;
        RunQueue(c);
        Assert.Equal(new[] { true }, asked);
        Assert.Equal(1, Sent(c, "xmit 1"));
    }

    [Fact]
    public void The_tuner_s_marker_is_cleared_even_when_its_key_throws()
    {
        // A radio object with nothing initialised: its Transmit setter throws.
        var rig = (FlexBase)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(FlexBase));
        var key = typeof(FlexBase).GetMethod("KeyForTheTuner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Throws<System.Reflection.TargetInvocationException>(() => key.Invoke(rig, null));
        Assert.False(FlexBase.TunerKeyMarkerSet);
    }

    [Fact]
    public void CW_messages_and_CW_typing_are_refused()
    {
        using var c = Judged();
        Assert.False(c.F.Rig.SendCW("TEST"));
        Assert.False(c.F.Rig.SendCW('E'));
        _verdict = TransmitVerdict.Allowed;
        Assert.True(c.F.Rig.SendCW("TEST"));
        Assert.True(c.F.Rig.SendCW('E'));
    }

    [Fact]
    public void Simple_VOX_on_is_refused_and_VOX_off_is_not()
    {
        using var c = Judged();
        Assert.True(c.F.Rig.VoxOnWouldTurnOnSimpleVox);
        c.F.Rig.Vox = FlexBase.OffOnValues.on;
        Assert.Equal(0, c.F.Rig.q.Count);
        Assert.False(c.F.Vendor.Radio.SimpleVOXEnable);

        c.F.Vendor.Radio.SimpleVOXEnable = true;
        _asked = 0;
        c.F.Rig.Vox = FlexBase.OffOnValues.off;    // never refused, nor asked
        RunQueue(c);
        Assert.False(c.F.Vendor.Radio.SimpleVOXEnable);
        Assert.Equal(0, _asked);

        _verdict = TransmitVerdict.Allowed;
        c.F.Rig.Vox = FlexBase.OffOnValues.on;
        RunQueue(c);
        Assert.True(c.F.Vendor.Radio.SimpleVOXEnable);
    }

    [Fact]
    public void Break_in_on_a_CW_transmit_slice_is_refused()
    {
        using var c = Judged();
        typeof(Flex.Smoothlake.FlexLib.Slice).GetField("_demodMode",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(c.Tx, "CW");
        Assert.True(c.F.Rig.VoxOnWouldTurnOnBreakIn);
        c.F.Rig.Vox = FlexBase.OffOnValues.on;
        Assert.Equal(0, c.F.Rig.q.Count);
        Assert.False(c.F.Vendor.Radio.CWBreakIn);
        _verdict = TransmitVerdict.Allowed;
        c.F.Rig.Vox = FlexBase.OffOnValues.on;
        RunQueue(c);
        Assert.True(c.F.Vendor.Radio.CWBreakIn);
        // Repair round 1 (the Opus checker's N5): off, really. Break-in off is
        // never refused, nor asked, with the rules refusing.
        _verdict = Refused;
        _asked = 0;
        c.F.Rig.Vox = FlexBase.OffOnValues.off;
        RunQueue(c);
        Assert.False(c.F.Vendor.Radio.CWBreakIn);
        Assert.Equal(0, _asked);
    }

    [Fact]
    public void Nothing_handed_in_refuses_nothing_and_a_judge_that_throws_admits()
    {
        using var c = new RadioCase();
        Assert.Null(c.F.Rig.Callouts.JudgeTransmitHere);
        c.F.Rig.Transmit = true;
        RunQueue(c);
        Assert.Equal(1, Sent(c, "xmit 1"));
        c.F.Rig.Transmit = false;
        RunQueue(c);

        c.F.Rig.Callouts.JudgeTransmitHere = _ => throw new InvalidOperationException("boom");
        Assert.True(c.F.Rig.TransmitRulesVerdict(true).IsAllowed);
        Assert.True(c.F.Rig.SendCW("TEST"));
    }

    [Fact]
    public void A_MOX_raise_is_refused_before_it_is_recorded_and_a_tune_raise_is_never_asked()
    {
        int asked = 0;
        var verdict = Refused;
        var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests", JudgeTransmitHere = _ => { asked++; return verdict; } });
        var raised = typeof(TransmitKillSwitch).GetField("_carrierRaised",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        try
        {
            using (TransmitKillSwitch.Arm(rig, "the transmitter check"))
            {
                Assert.False(TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox));
                Assert.Equal(verdict.RefusalSentence(), TransmitKillSwitch.LastRaiseRefusal);
                Assert.False((bool)raised.GetValue(null)!);
                Assert.Equal(1, asked);

                asked = 0;
                Assert.True(TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Tune));
                Assert.Equal(0, asked);
                Assert.True(TransmitKillSwitch.HoldsTuneCarrier(rig));
                TransmitKillSwitch.DropCarrier(rig, TransmitKillSwitch.Carrier.Tune);

                verdict = TransmitVerdict.Allowed;   // positive control, same arm
                Assert.True(TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox));
                Assert.Null(TransmitKillSwitch.LastRaiseRefusal);
            }
        }
        finally { try { rig.Dispose(); } catch { } }
    }

    [Fact]
    public void Every_chokepoint_asks_and_the_tune_routes_do_not()
    {
        string flex = Src("Radios/FlexBase.cs");
        // Repair round 1: the tuner's own queued key leaves the 60 m rule out; every other key asks
        // it, right after the callsign.
        int callsign = flex.IndexOf("if (RefusedForNoCallsign(\"transmit\")) return;   // #335\n", StringComparison.Ordinal);
        int rules = flex.IndexOf("if (RefusedByTransmitRules(\"transmit\", sixtyMeterRule: !t_tunerKeyLeavesSixtyMeterRule)) return;",
            Math.Max(callsign, 0), StringComparison.Ordinal);
        Assert.True(callsign > 0 && rules > callsign && rules - callsign < 400, "EnqueueKeyOn asks the rules right after the callsign");
        Assert.Contains("_atuKeyQueuedFor = atuGen; KeyForTheTuner();", flex, StringComparison.Ordinal);
        Assert.Contains("if (value && RefusedByTransmitRules(\"antenna tuner\", sixtyMeterRule: false)) return;", flex, StringComparison.Ordinal);
        Assert.Contains("if (RefusedByTransmitRules(\"a CW message\")) return false;", flex, StringComparison.Ordinal);
        Assert.Contains("if (RefusedByTransmitRules(\"CW typing\")) return false;", flex, StringComparison.Ordinal);
        Assert.Contains("&& RefusedByTransmitRules(\"VOX\", spokenPrefix:", flex, StringComparison.Ordinal);
        Assert.Contains("&& RefusedByTransmitRules(\"CW break-in\", spokenPrefix:", flex, StringComparison.Ordinal);
        Assert.DoesNotContain("RefusedByTransmitRules(\"tune\")", flex, StringComparison.Ordinal);
        Assert.DoesNotContain("RefusedByTransmitRules", Src("Radios/FlexBase.RemoteAudio.cs"), StringComparison.Ordinal);

        string fixAudio = Src("Radios/ChainChecks/FixerTransmitAudioBoundary.cs");
        Assert.Contains("TransmitKillSwitch.RefusedByTransmitRules(rig, \"the injected transmit check\")", fixAudio, StringComparison.Ordinal);
        Assert.Contains("TransmitKillSwitch.RefusedByTransmitRules(rig, \"the spoken transmit check\")", fixAudio, StringComparison.Ordinal);
        Assert.DoesNotContain("RefusedByTransmitRules", Src("Radios/ChainChecks/FixerTransmitBoundary.cs"), StringComparison.Ordinal);

        // RaiseCarrier: for MOX only, and before the lock block records the carrier.
        string kill = Src("Radios/TransmitKillSwitch.cs");
        int raise = kill.IndexOf("public static bool RaiseCarrier(FlexBase rig, Carrier carrier, object admission)", StringComparison.Ordinal);
        int ask = kill.IndexOf("if (carrier == Carrier.Mox)\n            {\n                string rules = RefusedByTransmitRules(rig,", raise, StringComparison.Ordinal);
        int gate = kill.IndexOf("lock (Gate)", raise, StringComparison.Ordinal);
        Assert.True(raise > 0 && ask > raise && gate > ask, "RaiseCarrier asks the rules for MOX before its lock block");
        string helper = kill.Substring(kill.IndexOf("public static string RefusedForNoCallsign(FlexBase rig, string what)", StringComparison.Ordinal));
        helper = helper.Substring(0, helper.IndexOf("}", StringComparison.Ordinal));
        Assert.DoesNotContain("RefusedByTransmitRules", helper, StringComparison.Ordinal);
    }

    private static string Src(string path) =>
        File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), path.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");
}
