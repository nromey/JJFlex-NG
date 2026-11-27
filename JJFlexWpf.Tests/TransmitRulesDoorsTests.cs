using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The transmit rules at the operator's doors (#1310): push to talk and the
/// lock refuse exactly when the TX band lockout's check says no, as they always
/// have, and now speak the REASON's sentence: the approved
/// <c>audio.ptt.blocked_by_license</c> for a frequency, a sentence of its own
/// for a mode. An unkey is never refused.
/// </summary>
/// <remarks>
/// Windowless, proved: an MTA thread and an unconnected <see cref="FlexBase"/>
/// whose writes go nowhere, as <see cref="CallsignDoorsTests"/> does, in the
/// same collection because both set ZeroPowerDoors' process-wide overrides.
/// The reason is stood in for by the radio's own judge delegate
/// (<see cref="FlexBase.OpenParms.JudgeTransmitHere"/>), which is how the doors
/// ask it in the app.
/// </remarks>
[Collection("TuneDoors")]
public sealed class TransmitRulesDoorsTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    private TransmitVerdict _verdict = TransmitVerdict.Allowed;
    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly VerbosityLevel _verbosityWas;
    private readonly bool _callsignCheckWas = FlexBase.CallsignCheckOn;
    private readonly Func<PttSafetyController?>? _pttSourceWas = Dialogs.AudioWorkshopDialog.PttControllerSource;

    public TransmitRulesDoorsTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        FlexBase.CallsignCheckOn = false;   // only the transmit rules decide here
        _verbosityWas = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
        ZeroPowerDoors.Forget();
        ZeroPowerDoors.AtZeroOverride = (rig, tune) => false;   // power is fine
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests", JudgeTransmitHere = _ => _verdict });
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        // The check as MainWindow wires it: the judge's yes or no.
        _controller.CanTransmitHereCheck = () => _verdict.IsAllowed;
    }

    public void Dispose()
    {
        FlexBase.CallsignCheckOn = _callsignCheckWas;
        ZeroPowerDoors.AtZeroOverride = null;
        ZeroPowerDoors.SimpleVoxOverride = null;
        ZeroPowerDoors.BreakInOverride = null;
        ZeroPowerDoors.TransmitRulesOverride = null;
        ZeroPowerDoors.TeardownOverride = null;
        Dialogs.AudioWorkshopDialog.PttControllerSource = _pttSourceWas;
        ZeroPowerDoors.Forget();
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        ScreenReaderOutput.CurrentVerbosity = _verbosityWas;
        typeof(FlexBase).GetField("theRadio", Any)!.SetValue(_rig, null);
        try { _rig.Dispose(); } catch { }
    }

    private static List<string> Said(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        press();
        return ScreenReaderOutput.RecentMessages.TakeWhile(m => m != marker).Reverse().ToList();
    }

    private static string Frequency => Lexicon.Get("audio.ptt.blocked_by_license");
    private static string Mode => Lexicon.Get("audio.ptt.blocked_by_license_mode");

    [Fact]
    public void A_frequency_refusal_at_push_to_talk_keeps_the_approved_sentence_and_keys_nothing()
    {
        _verdict = TransmitVerdict.LockoutFrequency;
        Assert.Equal(new[] { Frequency }, Said(_controller.PttDown));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void A_mode_refusal_at_push_to_talk_says_the_mode_and_keys_nothing()
    {
        _verdict = new TransmitVerdict(LockoutReason.Mode);
        Assert.Equal(new[] { Mode }, Said(_controller.PttDown));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void The_lock_says_the_reason_too()
    {
        _verdict = new TransmitVerdict(LockoutReason.Mode);
        Assert.Equal(new[] { Mode }, Said(_controller.ToggleLock));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void A_no_from_a_check_whose_reason_cannot_be_read_is_the_frequency_refusal_as_before()
    {
        // A yes-or-no check with no radio judge behind it: what every door said until #1310.
        var bare = new PttSafetyController(() => null, () => true, new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        try
        {
            bare.CanTransmitHereCheck = () => false;
            Assert.Equal(TransmitVerdict.LockoutFrequency, bare.JudgeTransmitHere());
            Assert.Equal(Frequency, bare.TransmitRulesRefusal());
        }
        finally { bare.Dispose(); }
    }

    [Fact]
    public void Allowed_keys_and_no_check_wired_keys()
    {
        _controller.PttDown();
        Assert.Equal(PttSafetyController.PttState.PttHold, _controller.State);
        _controller.PttUp();
        _controller.CanTransmitHereCheck = null;
        _verdict = new TransmitVerdict(LockoutReason.Mode);   // the radio would say no, but nothing asks it
        Assert.Null(_controller.TransmitRulesRefusal());
    }

    [Fact]
    public void An_unkey_a_release_and_Escape_are_never_refused()
    {
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        _verdict = TransmitVerdict.LockoutFrequency;   // the frequency moved out while keyed
        Assert.DoesNotContain(Frequency, Said(_controller.ToggleLock));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        _verdict = TransmitVerdict.Allowed;
        _controller.PttDown();
        _verdict = TransmitVerdict.LockoutFrequency;
        _controller.PttUp();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        _verdict = TransmitVerdict.Allowed;
        _controller.ToggleLock();
        _verdict = new TransmitVerdict(LockoutReason.Mode);
        _controller.EscapeUnlock();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    /// <summary>
    /// #1307: every VOX door (the VOX field, the VOX box, the VOX menu) asks
    /// ZeroPowerDoors.RefuseVoxOn, which now asks the transmit rules after the
    /// callsign, so a door never announces "VOX on" for a setter that refuses.
    /// </summary>
    [Fact]
    public void Turning_VOX_or_break_in_on_is_refused_at_the_door_and_says_it_stays_off()
    {
        ZeroPowerDoors.SimpleVoxOverride = _ => true;
        ZeroPowerDoors.BreakInOverride = _ => false;
        ZeroPowerDoors.TransmitRulesOverride = (_, _) => _verdict;
        _verdict = new TransmitVerdict(LockoutReason.Mode);
        var said = Said(() => Assert.True(ZeroPowerDoors.RefuseVoxOn(_rig, "the VOX field")));
        Assert.Equal(new[] { Lexicon.Get("audio.ptt.vox_stays_off") + " " + Mode }, said);

        ZeroPowerDoors.Forget();
        ZeroPowerDoors.SimpleVoxOverride = _ => false;
        ZeroPowerDoors.BreakInOverride = _ => true;
        _verdict = TransmitVerdict.LockoutFrequency;
        said = Said(() => Assert.True(ZeroPowerDoors.RefuseVoxOn(_rig, "the VOX box")));
        Assert.Equal(new[] { Lexicon.Get("audio.ptt.break_in_stays_off") + " " + Frequency }, said);

        _verdict = TransmitVerdict.Allowed;   // positive control
        Assert.False(ZeroPowerDoors.RefuseVoxOn(_rig, "the VOX menu"));
        ZeroPowerDoors.BreakInOverride = _ => false;   // nothing would turn on: never refused
        _verdict = TransmitVerdict.LockoutFrequency;
        Assert.False(ZeroPowerDoors.RefuseVoxOn(_rig, "the VOX menu"));
    }

    // ---- repair round 1: a door says only the refusal, and never over a carrier ----

    [Fact]
    public void A_repeat_key_down_while_held_says_nothing_and_keeps_the_hold_when_the_rules_now_refuse()
    {
        _controller.PttDown();
        Assert.Equal(PttSafetyController.PttState.PttHold, _controller.State);
        _verdict = TransmitVerdict.LockoutFrequency;   // the slice moved out of privileges while held
        Assert.Empty(Said(_controller.PttDown));        // a repeat: nothing said over the carrier
        Assert.Equal(PttSafetyController.PttState.PttHold, _controller.State);
        _controller.PttUp();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.Equal(new[] { Frequency }, Said(_controller.PttDown));   // positive control: from Idle it refuses
    }

    private static bool TunerStartRefused(MainWindow main) =>
        (bool)typeof(MainWindow).GetMethod("TunerStartRefused", Any)!.Invoke(main, null)!;

    private static MainWindow MainWith(FlexBase rig)
    {
        var main = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        main.RigControl = rig;
        return main;
    }

    [Fact]
    public void The_antenna_tuner_s_door_asks_the_lockout_alone_and_says_only_the_refusal()
    {
        var asked = new List<bool>();
        ZeroPowerDoors.TransmitRulesOverride = (_, sixty) => { asked.Add(sixty); return _verdict; };
        var main = MainWith(_rig);
        _verdict = TransmitVerdict.LockoutFrequency;
        bool refused = false;
        var said = Said(() => refused = TunerStartRefused(main));
        Assert.True(refused);                              // the press stops: FlexTunerOn is never set
        Assert.Equal(new[] { Frequency }, said);           // the refusal, and no "ATU tuning"
        Assert.DoesNotContain(Lexicon.Get("audio.tune.atu_tuning"), said);
        Assert.Equal(new[] { false }, asked);              // the 60 m rule is not asked for the tuner
        Assert.False(_rig.FlexTunerOn);

        ZeroPowerDoors.Forget();
        _verdict = TransmitVerdict.Allowed;                // positive control
        Assert.False(TunerStartRefused(main));
    }

    [Fact]
    public void The_antenna_tuner_s_door_refuses_during_an_Audio_Check_teardown_before_anything_else()
    {
        ZeroPowerDoors.TeardownOverride = _ => true;
        ZeroPowerDoors.TransmitRulesOverride = (_, _) => throw new InvalidOperationException("not reached");
        var main = MainWith(_rig);
        bool refused = false;
        var said = Said(() => refused = TunerStartRefused(main));
        Assert.True(refused);
        Assert.Equal(new[] { Lexicon.Get("audio.check.teardown_refuses") }, said);
        ZeroPowerDoors.Forget();
        ZeroPowerDoors.TeardownOverride = _ => false;      // positive control
        ZeroPowerDoors.TransmitRulesOverride = (_, _) => TransmitVerdict.Allowed;
        Assert.False(TunerStartRefused(main));
    }

    [Fact]
    public void The_tune_carrier_s_doors_are_still_never_asked_the_rules()
    {
        ZeroPowerDoors.TransmitRulesOverride = (_, _) => throw new InvalidOperationException("tune is never asked");
        Assert.False(ZeroPowerDoors.RefuseTunePress(_rig, "the tune carrier"));
    }

    [Fact]
    public void A_door_with_no_PTT_controller_asks_the_radio_s_judge_itself()
    {
        var asked = new List<bool>();
        ZeroPowerDoors.TransmitRulesOverride = (_, sixty) => { asked.Add(sixty); return _verdict; };
        _verdict = new TransmitVerdict(LockoutReason.None, SixtyMeterReason.Outside);
        Assert.Equal(_verdict.RefusalSentence(), ZeroPowerDoors.TransmitRulesRefusalAt(null, _rig));
        Assert.Equal(new[] { true }, asked);
        _verdict = TransmitVerdict.Allowed;
        Assert.Null(ZeroPowerDoors.TransmitRulesRefusalAt(null, _rig));
        // With a controller, the controller's own answer.
        _verdict = TransmitVerdict.LockoutFrequency;
        Assert.Equal(Frequency, ZeroPowerDoors.TransmitRulesRefusalAt(_controller, _rig));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_CW_message_the_rules_refuse_says_only_the_refusal_and_nothing_is_sent_shown_or_said_as_sending(bool withController)
    {
        Dialogs.AudioWorkshopDialog.PttControllerSource = () => withController ? _controller : null;
        ZeroPowerDoors.TransmitRulesOverride = (_, _) => _verdict;
        typeof(FlexBase).GetField("_RXMode", Any)!.SetValue(_rig, "CW");
        int sent = 0, shown = 0;
        var context = new KeyCommandContext
        {
            GetRigControl = () => _rig,
            GetCWText = () => new[] { new CWMessageItem(System.Windows.Forms.Keys.F1, "CQ TEST", "CQ") },
            SendCW = _ => sent++,
            WriteTextX = (_, _, _, _) => shown++,
        };
        var keys = new KeyCommands(context, false) { CommandId = (CommandValues)KeyCommandConstants.FirstMessageCommandValue };
        var send = typeof(KeyCommands).GetMethod("SendCWMessage", Any)!;

        _verdict = new TransmitVerdict(LockoutReason.None, SixtyMeterReason.Outside);
        var said = Said(() => send.Invoke(keys, null));
        Assert.Equal(new[] { _verdict.RefusalSentence()! }, said);
        Assert.Equal(0, sent);
        Assert.Equal(0, shown);

        ZeroPowerDoors.Forget();
        _verdict = TransmitVerdict.Allowed;                // positive control: the same key sends
        said = Said(() => send.Invoke(keys, null));
        Assert.Equal(1, sent);
        Assert.Equal(1, shown);
        Assert.Contains(Lexicon.Get("settings.cw.sending", ("label", "CQ")), said);
    }

    [Fact]
    public void Every_voice_send_door_and_recorder_playback_ask_the_same_check()
    {
        string sender = Source("VoiceMessageSender.cs");
        // Repair round 1: through the helper that asks the radio's judge itself when the controller is null.
        Assert.Equal(3, Occurrences(sender, "ZeroPowerDoors.TransmitRulesRefusalAt(ptt, rig) is { } rulesRefusal"));
        Assert.DoesNotContain("ptt?.TransmitRulesRefusal()", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("audio.ptt.blocked_by_license", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("CanTransmitHereCheck", sender, StringComparison.Ordinal);

        string ptt = Source("PttSafetyController.cs");
        Assert.Contains("(CanTransmitHereCheck?.Invoke() ?? true);", ptt, StringComparison.Ordinal);   // recorder playback, unchanged
        Assert.Equal(1, Occurrences(ptt, "if (TransmitRulesRefusal() is { } rulesRefusal)"));   // the lock
        Assert.Equal(1, Occurrences(ptt, "if (State == PttState.Idle && TransmitRulesRefusal() is { } rulesRefusal)"));   // push to talk
        // No door names the frequency sentence's key itself any more (built, so
        // the lexicon's key scan does not read this line as a key).
        Assert.DoesNotContain("\"" + string.Join(".", "audio", "ptt", "blocked_by_license") + "\"", ptt, StringComparison.Ordinal);
    }

    private static string Source(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "JJFlexWpf", file)).Replace("\r\n", "\n");
    }

    private static int Occurrences(string text, string what)
    {
        int n = 0, at = 0;
        while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
        return n;
    }
}
