using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// No callsign, no transmit (#335). Noel, 2026-08-28: "If the operator does
/// not give a call, they get no transmit ability, no ifs ands or buts." Ruled
/// 2026-11-08: no dummy-load exception, and every keying JJ Flexible starts is
/// refused with one sentence naming the missing callsign and where to enter
/// it; the radio's own microphone PTT and footswitch are left alone.
/// </summary>
/// <remarks>
/// <para>
/// Runs the real FlexBase against the vendor fake radio and reads the commands
/// that actually go out, one test per keying chokepoint, each with a positive
/// control once a callsign is entered. Unkeys are tested to stay open. The
/// doors in front of the chokepoints (push to talk, the lock, the sends, tune)
/// are held from source here and from <c>ZeroPowerDoors</c> in
/// <c>JJFlexWpf.Tests/CallsignDoorsTests</c>.
/// </para>
/// <para>The callsign in these tests is the conventional placeholder N0CALL.</para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class CallsignGateTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(CallsignGateTests));
    public void Dispose() => _scope.Dispose();

    private const string FakeCall = "N0CALL";

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

    private static string _call = "";
    private static RadioCase NoCallsign()
    {
        _call = "";
        var c = new RadioCase();
        c.F.Rig.Callouts.GetOperatorCallsign = () => _call;
        return c;
    }

    // ── The decision ────────────────────────────────────────────────────

    [Fact]
    public void Empty_or_blank_is_no_callsign_and_any_text_is_one()
    {
        Assert.True(FlexBase.CallsignMissing(() => ""));
        Assert.True(FlexBase.CallsignMissing(() => "   "));
        Assert.True(FlexBase.CallsignMissing(() => null!));
        Assert.False(FlexBase.CallsignMissing(() => FakeCall));
    }

    [Fact]
    public void A_provider_that_throws_refuses_and_one_never_handed_in_installs_no_gate()
    {
        Assert.True(FlexBase.CallsignMissing(() => throw new InvalidOperationException("no operator")));
        Assert.False(FlexBase.CallsignMissing(null));
    }

    [Fact]
    public void The_callsign_is_read_at_the_key_not_remembered_from_before()
    {
        using var c = NoCallsign();
        Assert.True(c.F.Rig.OperatorCallsignMissing);
        _call = FakeCall;
        Assert.False(c.F.Rig.OperatorCallsignMissing);
        _call = "";
        Assert.True(c.F.Rig.OperatorCallsignMissing);
    }

    // ── The chokepoints ─────────────────────────────────────────────────

    [Fact]
    public void Transmit_on_is_refused_and_transmit_off_is_not()
    {
        using var c = NoCallsign();
        c.F.Rig.Transmit = true;
        RunQueue(c);
        Assert.Equal(0, Sent(c, "xmit 1"));
        Assert.Equal(0, c.F.Rig.q.Count);
        c.F.Rig.Transmit = false;                 // an unkey is never refused
        Assert.Equal(1, c.F.Rig.q.Count);
        RunQueue(c);
        // Positive control: with a callsign the same press keys.
        _call = FakeCall;
        c.F.Rig.Transmit = true;
        RunQueue(c);
        Assert.Equal(1, Sent(c, "xmit 1"));
    }

    [Fact]
    public void Tune_on_is_refused_and_tune_off_is_not()
    {
        using var c = NoCallsign();
        int before = Sent(c, "tune");
        c.F.Rig.TxTune = true;
        RunQueue(c);
        Assert.Equal(before, Sent(c, "tune"));
        c.F.Rig.TxTune = false;
        RunQueue(c);
        int afterOff = Sent(c, "tune");
        _call = FakeCall;
        c.F.Rig.TxTune = true;
        RunQueue(c);
        Assert.True(Sent(c, "tune") > afterOff);
    }

    [Theory]
    [InlineData("manual", "transmit tune 1")]
    [InlineData("auto", "atu start")]
    public void The_antenna_tuner_is_not_started(string type, string command)
    {
        using var c = NoCallsign();
        typeof(FlexBase).GetField("_FlexTunerType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, type == "manual" ? FlexBase.FlexTunerTypes.manual : FlexBase.FlexTunerTypes.auto);
        int before = Sent(c, command);
        c.F.Rig.FlexTunerOn = true;
        RunQueue(c);
        Assert.Equal(before, Sent(c, command));
        Assert.False(c.F.Rig.FlexTunerOn);
        _call = FakeCall;
        c.F.Rig.FlexTunerOn = true;
        RunQueue(c);
        Assert.True(Sent(c, command) > before);
    }

    [Fact]
    public void CW_messages_and_CW_typing_are_refused()
    {
        using var c = NoCallsign();
        Assert.False(c.F.Rig.SendCW("TEST"));
        Assert.False(c.F.Rig.SendCW('E'));
        _call = FakeCall;
        Assert.True(c.F.Rig.SendCW("TEST"));
    }

    [Fact]
    public void A_recording_sent_through_VOX_is_refused_and_one_not_keyed_by_VOX_is_not()
    {
        using var c = NoCallsign();
        c.F.Rig.TxFilePlayer.Load(new float[48000], 48000, "message");
        c.F.Rig.TxFileStartForVox();             // VOX would key it: refused
        Assert.False(c.F.Rig.TxFilePlaying);
        // TxFileStart keys nothing by itself; its send keys through Transmit,
        // which is refused above. Left open on purpose.
        c.F.Rig.TxFileStart();
        Assert.True(c.F.Rig.TxFilePlaying);
        c.F.Rig.TxFileStop();
        _call = FakeCall;
        c.F.Rig.TxFileStartForVox();
        Assert.True(c.F.Rig.TxFilePlaying);
        c.F.Rig.TxFileStop();
    }

    // ── Wiring, from source ─────────────────────────────────────────────

    private static string Src(string relative) =>
        File.ReadAllText(Path.Combine(IntegrationPassTree.Root, relative)).Replace("\r\n", "\n");

    /// <summary>
    /// The application hands the radio layer the OPERATOR's callsign, read at
    /// each ask, at the one place it builds OpenParms. Without this line the
    /// gate is not installed, so it is pinned.
    /// </summary>
    [Fact]
    public void The_app_hands_the_radio_the_operators_callsign_not_the_radios()
    {
        string g = Src("globals.vb");
        Assert.Contains("OpenParms.GetOperatorCallsign = AddressOf currentOperatorCallsign", g, StringComparison.Ordinal);
        var fn = Regex.Match(g, @"Private Function currentOperatorCallsign\(\) As String(?<body>.*?)End Function", RegexOptions.Singleline);
        Assert.True(fn.Success);
        Assert.Contains("CurrentOp.callSign", fn.Groups["body"].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("RigControl", fn.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Matches(g, @"New FlexBase\.OpenParms\(\)").Count);
    }

    /// <summary>
    /// Each chokepoint asks right after the teardown admission, on the way on
    /// only. A route that reaches a chokepoint with no door of its own is
    /// refused here.
    /// </summary>
    [Fact]
    public void Every_keying_chokepoint_asks()
    {
        string flex = Src("Radios/FlexBase.cs");
        Assert.Contains("if (RefusedForCheckTeardown(\"transmit\")) return;\n            if (RefusedForNoCallsign(\"transmit\")) return;", flex, StringComparison.Ordinal);
        Assert.Contains("if (value && RefusedForNoCallsign(\"tune\")) return;", flex, StringComparison.Ordinal);
        Assert.Contains("if (value && RefusedForNoCallsign(\"antenna tuner\")) return;", flex, StringComparison.Ordinal);
        Assert.Contains("if (RefusedForNoCallsign(\"a CW message\")) return false;", flex, StringComparison.Ordinal);
        Assert.Contains("if (RefusedForNoCallsign(\"CW typing\")) return false;", flex, StringComparison.Ordinal);
        string audio = Src("Radios/FlexBase.RemoteAudio.cs");
        Assert.Contains("if (RefusedForNoCallsign(\"a recording for VOX\")) return;", audio, StringComparison.Ordinal);
    }

    /// <summary>
    /// The operator's doors ask before anything is prepared, and the lock's
    /// door does NOT exempt a check's own key, where the zero-power rule does.
    /// </summary>
    [Fact]
    public void The_push_to_talk_doors_ask_and_the_checks_are_not_exempt()
    {
        string ptt = Src("JJFlexWpf/PttSafetyController.cs");
        Assert.Contains("if (State == PttState.Idle && ZeroPowerDoors.RefuseNoCallsign(_getRigControl(), \"push to talk\")) return;", ptt, StringComparison.Ordinal);
        const string lockLine = "if (State == PttState.Idle && ZeroPowerDoors.RefuseNoCallsign(_getRigControl(), \"the lock key\")) return;";
        Assert.Contains(lockLine, ptt, StringComparison.Ordinal);

        string doors = Src("JJFlexWpf/ZeroPowerDoors.cs");
        Assert.Matches(new Regex(@"internal static bool RefuseTransmit\([^)]*\)\s*\{\s*if \(RefuseNoCallsign\(rig, door, afterMenu\)\) return true;"), doors);
        Assert.Matches(new Regex(@"internal static bool RefuseTune\([^)]*\)\s*\{\s*if \(RefuseNoCallsign\(rig, door, afterMenu\)\) return true;"), doors);
        Assert.Contains("FlexBase.NoCallsignRefusal", doors, StringComparison.Ordinal);
    }

    [Fact]
    public void One_sentence_names_the_missing_callsign_and_where_to_enter_it()
    {
        Lexicon.Load(Lexicon.Partitions);
        string said = FlexBase.NoCallsignRefusal;
        Assert.Contains("callsign", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Operators", said, StringComparison.Ordinal);
        Assert.Contains("Radio menu", said, StringComparison.Ordinal);
    }
}
