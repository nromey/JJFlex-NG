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

    /// <summary>
    /// Turning VOX on with no callsign (#335, ruled by Noel 2026-11-09 20:28,
    /// night question J8): simple VOX is refused at the setter, turning it off
    /// never is, and a callsign lets the same press through.
    /// </summary>
    [Fact]
    public void Simple_VOX_on_is_refused_and_VOX_off_is_not()
    {
        using var c = NoCallsign();
        Assert.True(c.F.Rig.VoxOnWouldTurnOnSimpleVox);   // a USB transmit slice
        c.F.Rig.Vox = FlexBase.OffOnValues.on;
        Assert.Equal(0, c.F.Rig.q.Count);                 // nothing queued: refused at the setter
        RunQueue(c);
        Assert.Equal(0, Sent(c, "vox_enable=1"));
        Assert.False(c.F.Vendor.Radio.SimpleVOXEnable);

        // Off is never refused, even with no callsign.
        c.F.Vendor.Radio.SimpleVOXEnable = true;
        c.F.Rig.Vox = FlexBase.OffOnValues.off;
        Assert.Equal(1, c.F.Rig.q.Count);
        RunQueue(c);
        Assert.False(c.F.Vendor.Radio.SimpleVOXEnable);

        // Positive control: with a callsign the same press turns VOX on.
        _call = FakeCall;
        c.F.Rig.Vox = FlexBase.OffOnValues.on;
        RunQueue(c);
        Assert.True(c.F.Vendor.Radio.SimpleVOXEnable);
    }

    /// <summary>
    /// In CW the same setter switches break-in, and turning it on with no
    /// callsign is refused too (#335, ruled by Noel 2026-11-11 11:51, night
    /// question J4): nothing queued and nothing sent. Turning it off never is,
    /// and a callsign lets the same press through. Told apart from simple VOX
    /// by the TRANSMIT slice's mode, the answer the doors share.
    /// </summary>
    /// <remarks>Until J4 this test was <c>Break_in_on_a_CW_transmit_slice_is_not_refused</c>
    /// and pinned the opposite, as the open question then required.</remarks>
    [Fact]
    public void Break_in_on_a_CW_transmit_slice_is_refused_and_off_is_not()
    {
        using var c = NoCallsign();
        Field(c.Tx, "_demodMode", "CW");
        Assert.False(c.F.Rig.VoxOnWouldTurnOnSimpleVox);
        Assert.True(c.F.Rig.VoxOnWouldTurnOnBreakIn);
        Assert.True(CallsignRefusalReached(c, () => c.F.Rig.Vox = FlexBase.OffOnValues.on));
        Assert.Equal(0, c.F.Rig.q.Count);                 // nothing queued: refused at the setter
        RunQueue(c);
        Assert.Equal(0, Sent(c, "break_in 1"));
        Assert.False(c.F.Vendor.Radio.CWBreakIn);
        Assert.False(c.F.Vendor.Radio.SimpleVOXEnable);

        // Off is never refused, even with no callsign.
        c.F.Vendor.Radio.CWBreakIn = true;
        c.F.Rig.Vox = FlexBase.OffOnValues.off;
        Assert.Equal(1, c.F.Rig.q.Count);
        RunQueue(c);
        Assert.False(c.F.Vendor.Radio.CWBreakIn);

        // Positive control: with a callsign the same press turns break-in on.
        _call = FakeCall;
        c.F.Rig.Vox = FlexBase.OffOnValues.on;
        RunQueue(c);
        Assert.True(c.F.Vendor.Radio.CWBreakIn);
        Assert.False(c.F.Vendor.Radio.SimpleVOXEnable);
    }

    /// <summary>
    /// The two predicates are siblings: at most one is true, each from the
    /// transmit slice's mode, and neither with no transmit slice.
    /// </summary>
    [Fact]
    public void Simple_VOX_and_break_in_are_told_apart_by_the_transmit_slice()
    {
        using var c = NoCallsign();
        Assert.True(c.F.Rig.VoxOnWouldTurnOnSimpleVox);
        Assert.False(c.F.Rig.VoxOnWouldTurnOnBreakIn);
        Field(c.Tx, "_demodMode", "CW");
        Assert.False(c.F.Rig.VoxOnWouldTurnOnSimpleVox);
        Assert.True(c.F.Rig.VoxOnWouldTurnOnBreakIn);
        Assert.False(FlexBase.TurnsOnBreakIn(null!));
        Field(c.F.Rig, "_TXVFO", -1);
        Assert.False(c.F.Rig.VoxOnWouldTurnOnBreakIn);
    }

    /// <summary>
    /// The callsign refusal comes first (the orchestrator's one rule for
    /// #335): with a transmit reference armed, which refuses VOX on with its
    /// own sentence, a press with no callsign hears the callsign sentence and
    /// only that, in voice and in CW. Positive control: with a callsign the
    /// reference's own refusal is still heard, so the reference really was
    /// armed.
    /// </summary>
    [Theory]
    [InlineData("USB")]
    [InlineData("CW")]
    public void The_callsign_refusal_comes_before_the_tone_and_reference_refusal(string mode)
    {
        using var c = NoCallsign();
        Field(c.Tx, "_demodMode", mode);
        var lease = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VoxPauseOwner.Lease));
        Field(c.F.Rig, "_referenceVoxLease", lease);
        try
        {
            Assert.True(c.F.Rig.TxReferenceArmed);
            // The callsign refusal is the one reached: its sentence is said
            // (the throttle's stamp is how a test sees that), and the
            // reference refusal, which returns before it, is not.
            Assert.True(CallsignRefusalReached(c, () => c.F.Rig.Vox = FlexBase.OffOnValues.on));
            Assert.Equal(0, c.F.Rig.q.Count);

            _call = FakeCall;
            // With a callsign the reference still refuses: nothing queued.
            Assert.False(CallsignRefusalReached(c, () => c.F.Rig.Vox = FlexBase.OffOnValues.on));
            Assert.Equal(0, c.F.Rig.q.Count);
        }
        finally { Field(c.F.Rig, "_referenceVoxLease", null!); }
    }

    /// <summary>
    /// The VOX hold's restore puts back the radio's OWN break-in, and it is
    /// never refused for a missing callsign: the radio's front panel is left
    /// alone (J4). Its write does not come through the <see cref="FlexBase.Vox"/>
    /// setter, which is the writer the gate stands on.
    /// </summary>
    [Fact]
    public void The_hold_puts_the_radios_own_break_in_back_with_no_callsign()
    {
        using var c = NoCallsign();
        Field(c.Tx, "_demodMode", "CW");
        Field(c.F.Rig, "_IsConnected", true);
        c.F.Vendor.Status("transmit break_in=1");
        c.F.Vendor.Status($"interlock state=READY source=SW tx_client_handle=0x{Radios.Tests.StationConnect.RigOnVendorRadio.OurHandle:X8}");
        Assert.True(c.F.Vendor.Radio.CWBreakIn);

        var lease = c.F.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.CwBreakIn);
        Assert.NotNull(lease);
        RunQueue(c);
        Assert.False(c.F.Vendor.Radio.CWBreakIn);                  // the hold switched it off

        Assert.True(lease!.Release(immediate: true));
        Assert.True(c.F.Vendor.Radio.CWBreakIn);                   // ...and put the radio's own value back
        Assert.True(Sent(c, "break_in 1") >= 1);
        Assert.True(c.F.Rig.OperatorCallsignMissing);              // still no callsign: the restore was not asked
    }

    /// <summary>
    /// Whether <paramref name="press"/> reached the no-callsign refusal and
    /// said its sentence, read from the refusal's own throttle stamp (this
    /// project's speech is silenced under test, so its history is empty).
    /// </summary>
    private static bool CallsignRefusalReached(RadioCase c, Action press)
    {
        var stamp = typeof(FlexBase).GetField("_lastNoCallsignSpokenMs",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        stamp.SetValue(c.F.Rig, 0L);
        press();
        return (long)stamp.GetValue(c.F.Rig)! != 0;
    }

    [Fact]
    public void With_no_transmit_slice_nothing_is_simple_VOX()
    {
        Assert.False(FlexBase.TurnsOnSimpleVox(null!));
        using var c = NoCallsign();
        Field(c.F.Rig, "_TXVFO", -1);
        Assert.False(c.F.Rig.VoxOnWouldTurnOnSimpleVox);
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
        // Both halves of VOX on (J8 and J4), asked before the tone refusal.
        Assert.Contains("if (val && TurnsOnSimpleVox(s) && RefusedForNoCallsign(\"VOX\")) return;\n"
            + "                if (val && TurnsOnBreakIn(s) && RefusedForNoCallsign(\"CW break-in\")) return;\n"
            + "                if (val && (TxToneEngaged || TxReferenceArmed))", flex, StringComparison.Ordinal);
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
        Assert.Contains("=> RefuseNoCallsign(rig, door, afterMenu) || RefuseTransmit(rig, door, afterMenu);", doors, StringComparison.Ordinal);
        Assert.Contains("=> RefuseNoCallsign(rig, door, afterMenu) || RefuseTune(rig, door, afterMenu);", doors, StringComparison.Ordinal);
        Assert.Contains("FlexBase.NoCallsignRefusal", doors, StringComparison.Ordinal);

        // Every operator door that keys asks the keying form; only the slice
        // recorder's door, which keys nothing, keeps the power-only question.
        var doorsAsked = new (string File, string Line)[]
        {
            ("JJFlexWpf/KeyCommands.cs", "ZeroPowerDoors.RefuseTransmitPress(rig, \"a CW message\")"),
            ("JJFlexWpf/MainWindow.xaml.cs", "ZeroPowerDoors.RefuseTransmitPress(RigControl, \"the transmit toggle\")"),
            ("JJFlexWpf/MainWindow.xaml.cs", "ZeroPowerDoors.RefuseTunePress(RigControl, \"the antenna tuner\")"),
            ("JJFlexWpf/MainWindow.xaml.cs", "ZeroPowerDoors.RefuseTunePress(RigControl, \"the tune carrier\")"),
            ("JJFlexWpf/NativeMenuBar.cs", "ZeroPowerDoors.RefuseTunePress(Rig, \"the tune menu\", afterMenu: true)"),
            ("JJFlexWpf/NativeMenuBar.cs", "ZeroPowerDoors.RefuseTunePress(Rig, \"the ATU menu\", afterMenu: true)"),
            ("JJFlexWpf/VoiceMessageSender.cs", "RefuseSendDoor(rig, \"a message key\")"),
            ("JJFlexWpf/VoiceMessageSender.cs", "RefuseSendDoor(rig, \"a recording send\")"),
            ("JJFlexWpf/VoiceMessageSender.cs", "ZeroPowerDoors.RefuseTransmitPress(rig, \"a send\")"),
            ("JJFlexWpf/VoiceMessageSender.cs", "ZeroPowerDoors.RefuseTransmitPress(rig, \"a send carrying on\")"),
        };
        foreach (var (file, line) in doorsAsked)
            Assert.Contains(line, Src(file), StringComparison.Ordinal);
        // One exception, by name (repair one): the send door asks the
        // power-only question of a send that joins a transmission the radio's
        // own push to talk holds, because that keys nothing. It is pinned here
        // whole, so the callsign half cannot quietly drop out of it.
        const string sendDoor =
            "JoinsAHeldTransmission(rig)\n            ? ZeroPowerDoors.RefuseTransmit(rig, door)\n            : ZeroPowerDoors.RefuseTransmitPress(rig, door);";
        string voice = Src("JJFlexWpf/VoiceMessageSender.cs").Replace("\r\n", "\n");
        Assert.Contains(sendDoor, voice, StringComparison.Ordinal);
        var plain = Regex.Matches(string.Concat(new[] { "JJFlexWpf/KeyCommands.cs", "JJFlexWpf/MainWindow.xaml.cs",
                "JJFlexWpf/NativeMenuBar.cs" }.Select(Src).Append(voice.Replace(sendDoor, ""))),
            @"ZeroPowerDoors\.Refuse(Transmit|Tune)\(");
        Assert.Empty(plain);
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
