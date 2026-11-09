using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

// LEXICON_SCANNER_EXEMPT: the pins below quote source lines, escaped quotes and
// all, so the scanner would read each as a call with a mangled key.

/// <summary>
/// The operator's presses that would key a rig reading 0 watts (#859, ruled by
/// Noel 2026-11-01 13:04, delivery 13:06). The rule itself and the radio's
/// reports are walked on a real status line in
/// <c>Radios.Tests.RadioPowerFloorTests</c>; this is what each door does with it.
/// </summary>
/// <remarks>
/// Windowless, proved: an MTA thread, an unconnected <see cref="FlexBase"/> whose
/// writes go nowhere, the radio's report stood in for by
/// <see cref="ZeroPowerDoors.AtZeroOverride"/>, and a window built as an
/// uninitialized object. What these cannot see is a radio: whether a press
/// really keys at zero, and what an ATU cycle does there, are bench steps.
/// </remarks>
[Collection("TuneDoors")]
public sealed class ZeroPowerDoorsTests : IDisposable
{
    private const string Transmit = "This rig is at 0 watts. Set your power first.";
    private const string Tune = "This rig's tune power is at 0 watts. Set your tune power first.";
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    private readonly FlexBase _rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
    private readonly PttSafetyController _controller;
    private readonly VerbosityLevel _verbosityWas;
    private bool _transmitAtZero = true, _tuneAtZero = true;
    private long _now = 1_000_000;
    private readonly List<string> _answers = new();

    public ZeroPowerDoorsTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        _verbosityWas = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
        ZeroPowerDoors.Forget();
        ZeroPowerDoors.AtZeroOverride = (rig, tune) => ReferenceEquals(rig, _rig) && (tune ? _tuneAtZero : _transmitAtZero);
        ZeroPowerDoors.Clock = () => _now;
        ZeroPowerDoors.Answered = _answers.Add;
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
    }

    public void Dispose()
    {
        ZeroPowerDoors.AtZeroOverride = null;
        ZeroPowerDoors.Answered = null;
        ZeroPowerDoors.Clock = () => Environment.TickCount64;
        ZeroPowerDoors.Forget();
        Dialogs.AudioWorkshopDialog.PttControllerSource = null;
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        ScreenReaderOutput.CurrentVerbosity = _verbosityWas;
        typeof(FlexBase).GetField("theRadio", Any)!.SetValue(_rig, null);
        try { _rig.Dispose(); } catch { }
    }

    /// <summary>Everything spoken during the press, oldest first.</summary>
    private static List<string> Said(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        press();
        return ScreenReaderOutput.RecentMessages.TakeWhile(m => m != marker).Reverse().ToList();
    }

    private void Keyable()
    {
        _transmitAtZero = false;
        _tuneAtZero = false;
    }

    private static string Source(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(path))).Replace("\r\n", "\n");
    }

    private static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, signature);
        int open = src.IndexOf('{', at), depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src.Substring(at, i - at + 1);
        }
        throw new InvalidOperationException(signature);
    }

    // ── Push to talk and the lock ───────────────────────────────────────

    [Fact]
    public void Push_to_talk_is_refused_aloud_and_keys_nothing()
    {
        Assert.Equal(new[] { Transmit }, Said(_controller.PttDown));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void Push_to_talk_keys_when_the_rig_is_not_at_zero()
    {
        Keyable();                       // the positive control: the same press on the same controller
        _controller.PttDown();
        Assert.Equal(PttSafetyController.PttState.PttHold, _controller.State);
    }

    [Fact]
    public void The_lock_is_refused_aloud_and_keys_nothing()
    {
        Assert.Equal(new[] { Transmit }, Said(_controller.ToggleLock));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void The_lock_keys_when_the_rig_is_not_at_zero()
    {
        Keyable();
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
    }

    [Fact]
    public void An_unlock_is_never_refused()
    {
        Keyable();
        _controller.ToggleLock();
        _transmitAtZero = true;          // a client took it to zero while keyed
        Assert.DoesNotContain(Transmit, Said(_controller.ToggleLock));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void Letting_go_of_push_to_talk_is_never_refused()
    {
        Keyable();
        _controller.PttDown();
        _transmitAtZero = true;
        _controller.PttUp();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void Escape_is_never_refused()
    {
        Keyable();
        _controller.ToggleLock();
        _transmitAtZero = true;
        _controller.EscapeUnlock();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void A_held_push_to_talk_going_on_to_the_lock_is_not_refused()
    {
        Keyable();
        _controller.PttDown();
        _transmitAtZero = true;          // already on the air: this press starts nothing
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
    }

    [Fact]
    public void A_push_to_talk_down_while_already_keyed_is_not_told_anything()
    {
        Keyable();
        _controller.ToggleLock();
        _transmitAtZero = true;
        _controller.PttDown();           // keys nothing, so it is not a press this answers
        Assert.Empty(_answers);
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
    }

    [Fact]
    public void A_rig_with_nothing_connected_keys_as_it_always_has()
    {
        ZeroPowerDoors.AtZeroOverride = null;   // the real reads, on a rig with no model and no reports
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
    }

    // ── A check's own keying is not a press ─────────────────────────────

    [Fact]
    public void A_checks_own_key_goes_through_at_zero()
    {
        Assert.Empty(Said(_controller.KeyForCheck));
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
    }

    [Fact]
    public void A_checks_way_past_the_door_closes_behind_it()
    {
        _controller.KeyForCheck();
        _controller.EscapeUnlock();
        Assert.Equal(new[] { Transmit }, Said(_controller.ToggleLock));     // the operator's press, straight after
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void A_check_still_answers_the_hold_after_a_cut()
    {
        var reading = new TransmitPowerReading(17.8f, 10.3f, skewMilliseconds: 4f, ageMilliseconds: 20f, commandedWatts: 25);
        _controller.BeginCutHold(_rig, TransmitSafety.ReflectedCut.Share, reading, "ANT2");
        _controller.KeyForCheck();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void The_check_keys_through_its_own_door_and_nothing_else_of_its_does()
    {
        string check = Source("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs");
        Assert.Contains("_ptt.KeyForCheck();", check);
        Assert.DoesNotContain("_ptt.ToggleLock", check);
        // The Fix tool, a send's own key and the checks' carrier do not pass a door.
        foreach (string file in new[] { "TransmitKillSwitch.cs", "BoxSendOwnedRig.cs", "VoiceMessageSend.cs", "FlexBase.cs" })
            Assert.DoesNotContain("ZeroPowerDoors", Source("Radios", file));
        Assert.DoesNotContain("PressRefused", Body(Source("Radios", "FlexBase.cs"), "public bool Transmit\n"));
        Assert.DoesNotContain("PressRefused", Body(Source("Radios", "FlexBase.cs"), "public bool TxTune\n"));
    }

    // ── A held key is one press ─────────────────────────────────────────

    public static IEnumerable<object[]> Doors() => new[]
    {
        new object[] { "push to talk" }, new object[] { "lock" }, new object[] { "tune" },
    };

    private void PressDoor(string door)
    {
        switch (door)
        {
            case "push to talk": _controller.PttDown(); break;
            case "lock": _controller.ToggleLock(); break;
            default: Assert.True(ZeroPowerDoors.RefuseTune(_rig, "the tune carrier")); break;
        }
    }

    [Theory]
    [MemberData(nameof(Doors))]
    public void A_held_key_under_a_reader_that_sends_pairs_is_one_tone_and_one_sentence(string door)
    {
        PressDoor(door);
        _now += 512;                     // the keyboard's repeat delay, then a pair about every 250 ms
        PressDoor(door);
        for (int i = 0; i < 12; i++)
        {
            _now += 250;
            PressDoor(door);
        }
        Assert.Single(_answers);         // counted at the answer: the speech history folds repeats
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);   // every repeat was still refused
    }

    [Theory]
    [MemberData(nameof(Doors))]
    public void A_new_press_is_told_again(string door)
    {
        PressDoor(door);
        _now += FreshPressGate.SameChordWindowMs + 1;
        PressDoor(door);
        Assert.Equal(2, _answers.Count);  // the positive control for the one above
    }

    [Fact]
    public void One_door_does_not_silence_another()
    {
        Assert.Equal(new[] { Transmit, Tune }, Said(() =>
        {
            _controller.ToggleLock();
            ZeroPowerDoors.RefuseTune(_rig, "the tune carrier");
        }));
    }

    // ── Told the usual way ──────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_sentence_is_not_Critical_so_the_operators_Off_silences_it(bool tune)
    {
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;       // what Off is
        bool refused = true;
        Assert.Empty(Said(() => refused = tune
            ? ZeroPowerDoors.RefuseTune(_rig, "a door")
            : ZeroPowerDoors.RefuseTransmit(_rig, "a door")));
        Assert.True(refused);            // silenced, and still refused

        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;          // the positive control
        _now += FreshPressGate.SameChordWindowMs + 1;
        Assert.Equal(new[] { tune ? Tune : Transmit }, Said(() =>
        {
            if (tune) ZeroPowerDoors.RefuseTune(_rig, "a door");
            else ZeroPowerDoors.RefuseTransmit(_rig, "a door");
        }));
    }

    [Fact]
    public void A_refused_press_at_Off_keys_nothing()
    {
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void The_tone_goes_through_its_own_gate_and_the_trace_comes_first_whatever_is_silenced()
    {
        string src = Source("JJFlexWpf", "ZeroPowerDoors.cs");
        string answer = Body(src, "private static void Answer(");
        int trace = answer.IndexOf("Tracing.TraceLine(", StringComparison.Ordinal);
        int tone = answer.IndexOf("EarconPlayer.ATUFailTone();", StringComparison.Ordinal);
        // "Say(sentence, subject)" since #335 shared this path with the
        // callsign refusal, which speaks under its own subject.
        int say = answer.IndexOf("Say(sentence, subject)", StringComparison.Ordinal);
        Assert.True(trace > 0 && tone > trace && say > tone);
        Assert.DoesNotContain("AdmitWarnings", src);
        Assert.DoesNotContain("VerbosityLevel.Critical", src);

        string earcon = Body(Source("JJFlexWpf", "EarconPlayer.cs"), "public static void ATUFailTone()");
        Assert.Contains("if (!Gate(EarconCategory.Transmit)) return;", earcon);
        Assert.DoesNotContain("AdmitWarnings", earcon);
    }

    // ── The tune doors ──────────────────────────────────────────────────

    private MainWindow Window()
    {
        var main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        typeof(MainWindow).GetField("_pttController", Any)!.SetValue(main, _controller);
        typeof(MainWindow).GetField("_radioPowerOn", Any)!.SetValue(main, true);
        typeof(MainWindow).GetProperty("RigControl", Any)!.SetValue(main, _rig);
        return main;
    }

    private object RadioFor()
    {
        var field = typeof(FlexBase).GetField("theRadio", Any)!;
        object radio = Activator.CreateInstance(field.FieldType, Any, null, new object[] { false }, null)!;
        field.SetValue(_rig, radio);
        return radio;
    }

    private static object? Read(MainWindow main, string field) => typeof(MainWindow).GetField(field, Any)!.GetValue(main);

    private bool TuneCarrierNoted() => (bool)typeof(PttSafetyController).GetField("_tuneCarrierOn", Any)!.GetValue(_controller)!;

    [Fact]
    public void A_refused_tune_tells_nothing_it_keyed_and_spends_no_debounce()
    {
        RadioFor();
        var main = Window();
        Assert.Equal(new[] { Tune }, Said(main.ToggleTuneCarrier));
        Assert.Equal(0L, (long)Read(main, "_lastTuneToggleTicks")!);
        Assert.False(TuneCarrierNoted());
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void A_tune_past_the_door_is_told_it_keyed()
    {
        RadioFor();
        var main = Window();
        _tuneAtZero = false;             // the positive control; the button this window lacks stops it after the note
        var said = Said(() => Assert.Throws<NullReferenceException>(main.ToggleTuneCarrier));
        Assert.DoesNotContain(Tune, said);
        Assert.True(TuneCarrierNoted());
    }

    [Fact]
    public void A_tune_turning_off_is_never_refused()
    {
        object radio = RadioFor();
        radio.GetType().GetField("_txTune", Any)!.SetValue(radio, true);
        var main = Window();
        var said = Said(() => Assert.Throws<NullReferenceException>(main.ToggleTuneCarrier));
        Assert.DoesNotContain(Tune, said);
        Assert.NotEqual(0L, (long)Read(main, "_lastTuneToggleTicks")!);
    }

    // ── Turning tune off from Check your audio (2026-11-02) ─────────────

    private bool TuneAskedOn() => (bool)typeof(FlexBase).GetField("_tuneRequested", Any)!.GetValue(_rig)!;

    private void AskForTuneJustNow()
    {
        // As FlexBase.TxTune's setter notes it, ahead of the queued write.
        typeof(FlexBase).GetField("_tuneRequested", Any)!.SetValue(_rig, true);
        typeof(FlexBase).GetField("_tuneRequestedAtMs", Any)!.SetValue(_rig, Environment.TickCount64);
        typeof(FlexBase).GetField("_tuneOnQueued", Any)!.SetValue(_rig, true);
    }

    private bool OnStillQueued() => (bool)typeof(FlexBase).GetField("_tuneOnQueued", Any)!.GetValue(_rig)!;

    [Fact]
    public void Turning_tune_off_does_nothing_when_no_tune_of_ours_is_up_or_asked()
    {
        RadioFor();
        var main = Window();
        Assert.False(main.TurnTuneCarrierOff());
        Assert.Equal(0L, (long)Read(main, "_lastTuneToggleTicks")!);
        Assert.False(TuneCarrierNoted());
        Assert.False(TuneAskedOn());
    }

    [Fact]
    public void Turning_tune_off_writes_off_even_inside_the_toggles_debounce()
    {
        object radio = RadioFor();
        radio.GetType().GetField("_txTune", Any)!.SetValue(radio, true);
        AskForTuneJustNow();
        var main = Window();
        // A toggle a moment ago: the debounce would swallow an ordinary press.
        typeof(MainWindow).GetField("_lastTuneToggleTicks", Any)!.SetValue(main, Environment.TickCount64 - 10);
        Record.Exception(() => main.TurnTuneCarrierOff());   // the feedback may reach parts this window lacks
        // The write was OFF: the setter notes its value before it queues.
        Assert.False(TuneAskedOn());
        Assert.False(TuneCarrierNoted());
    }

    [Fact]
    public void A_tune_still_in_the_command_queue_counts_and_is_written_off()
    {
        // Codex blocker 2: the radio's flag has not risen yet, but the request
        // is on its way. The getter alone would say no tune at all.
        RadioFor();
        AskForTuneJustNow();
        Assert.False(_rig.TxTune);
        Assert.True(_rig.TuneAskedOrUp);
        var main = Window();
        // The feedback may reach parts this window lacks, after the write.
        Record.Exception(() => main.TurnTuneCarrierOff());
        Assert.False(TuneAskedOn());          // the write was OFF
        Assert.False(OnStillQueued());        // and it revoked the ON ahead of it
        Assert.False(_rig.TuneAskedOrUp);
    }

    [Fact]
    public void A_flag_falling_between_reads_can_never_turn_the_off_into_an_on()
    {
        // Codex blocker 1: the first version read the flag and then toggled,
        // and the toggle read it again. The off is now a write of false and
        // nothing else, so no interleaving can reach a write of true.
        string off = Body(Source("JJFlexWpf", "MainWindow.xaml.cs"), "public bool TurnTuneCarrierOff()");
        Assert.Contains("rig.TxTune = false;", off);
        Assert.DoesNotContain("ToggleTuneCarrier", off);
        Assert.DoesNotContain("TxTune = true", off);
        Assert.DoesNotContain("newState", off);
        Assert.Contains("rig.TuneAskedOrUp", off);
    }

    [Fact]
    public void The_tune_key_in_the_dialog_stops_explicitly_and_starts_only_on_a_new_press()
    {
        // Codex blocker 4: a quick second press must stop, and a reader's
        // down/up pairs must not restart a carrier the first pair stopped.
        string takes = Body(Source("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.cs"), "private bool TakesTheTuneKey(KeyEventArgs e)");
        Assert.Contains("_tuneKeyPress.Decide(Environment.TickCount64, e.IsRepeat, carrierOurs, () => true)", takes);
        int stop = takes.IndexOf("case PressAct.Stop:", StringComparison.Ordinal);
        int start = takes.IndexOf("case PressAct.Start:", StringComparison.Ordinal);
        Assert.True(stop > 0 && start > stop);
        string stopArm = takes.Substring(stop, start - stop);
        Assert.Contains("TuneOffSource?.Invoke()", stopArm);
        Assert.DoesNotContain("ToggleTuneSource", stopArm);

        // The gate itself, in the pair shape: start, the first pair stops, no
        // pair after that starts anything.
        var gate = new FreshPressGate();
        bool up = false;
        long t = 0;
        var acts = new List<PressAct>();
        for (int pair = 0; pair < 7; pair++, t += 250)
        {
            var act = gate.Decide(t, isRepeat: false, live: up, settle: () => true);
            acts.Add(act);
            if (act == PressAct.Start) up = true;
            if (act == PressAct.Stop) up = false;
        }
        Assert.Equal(PressAct.Start, acts[0]);
        Assert.Equal(PressAct.Stop, acts[1]);
        Assert.All(acts.Skip(2), a => Assert.Equal(PressAct.Nothing, a));
    }

    [Fact]
    public void A_refused_click_unchecks_the_button_that_flipped_itself_and_plays_no_tune_tone()
    {
        string tune = Body(Source("JJFlexWpf", "MainWindow.xaml.cs"), "public void ToggleTuneCarrier()");
        int door = tune.IndexOf("ZeroPowerDoors.RefuseTune(RigControl, \"the tune carrier\")", StringComparison.Ordinal);
        int uncheck = tune.IndexOf("TuneToggleButton.IsChecked = false;", StringComparison.Ordinal);
        int leaves = tune.IndexOf("return;", uncheck, StringComparison.Ordinal);
        int debounce = tune.IndexOf("_lastTuneToggleTicks < 500", StringComparison.Ordinal);
        int write = tune.IndexOf("RigControl.TxTune = newState;", StringComparison.Ordinal);
        int tone = tune.IndexOf("EarconPlayer.TuneOnTone();", StringComparison.Ordinal);
        Assert.True(door > 0 && uncheck > door && leaves > uncheck && debounce > leaves && write > debounce && tone > write);
        Assert.DoesNotContain("CutHold", tune);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]    // the other field at zero says nothing about this one
    [InlineData(false, false, false)]
    public void The_tuner_reads_tune_power(bool tuneAtZero, bool transmitAtZero, bool refused)
    {
        _tuneAtZero = tuneAtZero;
        _transmitAtZero = transmitAtZero;
        var main = Window();
        Assert.Equal(refused, (bool)typeof(MainWindow).GetMethod("TunerStartRefused", Any)!.Invoke(main, null)!);
    }

    [Fact]
    public void Turning_the_tuner_off_is_never_refused()
    {
        typeof(FlexBase).GetField("_FlexTunerOn", Any)!.SetValue(_rig, true);
        var main = Window();
        Assert.False((bool)typeof(MainWindow).GetMethod("TunerStartRefused", Any)!.Invoke(main, null)!);
    }

    [Fact]
    public void Every_way_to_start_the_tuner_asks()
    {
        string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
        foreach (string signature in new[]
        {
            "private void AntennaTuneButton_Click(object sender, RoutedEventArgs e)",
            "public void StartATUTuneCycle()",
        })
        {
            string body = Body(main, signature);
            Assert.True(body.IndexOf("TunerStartRefused()", StringComparison.Ordinal) >= 0
                && body.IndexOf("TunerStartRefused()", StringComparison.Ordinal) < body.IndexOf("RigControl.FlexTunerOn", StringComparison.Ordinal), signature);
        }
        string toolbar = Body(main, "private void ExecuteActionToolbarItem(string item)");
        Assert.True(toolbar.IndexOf("if (TunerStartRefused()) break;", StringComparison.Ordinal)
            < toolbar.IndexOf("RigControl.FlexTunerOn = !RigControl.FlexTunerOn;", StringComparison.Ordinal));
    }

    [Fact]
    public void The_menus_ask_first_so_the_sentence_outlives_the_menu_closing()
    {
        string menu = Source("JJFlexWpf", "NativeMenuBar.cs");
        Assert.Contains("if (Rig?.TxTune == false && ZeroPowerDoors.RefuseTune(Rig, \"the tune menu\", afterMenu: true)) return;", menu);
        Assert.Contains("if (Rig?.FlexTunerOn == false && ZeroPowerDoors.RefuseTune(Rig, \"the ATU menu\", afterMenu: true)) return;", menu);
    }

    // ── The sends ───────────────────────────────────────────────────────

    [Fact]
    public void A_voice_message_key_is_refused_aloud()
    {
        var item = new CWMessageItem { Label = "CQ", Audio = "none" };
        Assert.Equal(new[] { Transmit }, Said(() => VoiceMessageSender.Send(_rig, item)));
        Assert.False(VoiceMessageSender.InFlight);
    }

    [Fact]
    public void A_voice_message_key_goes_on_when_the_rig_is_not_at_zero()
    {
        Keyable();
        var item = new CWMessageItem { Label = "CQ", Audio = "none" };
        Assert.DoesNotContain(Transmit, Said(() => VoiceMessageSender.Send(_rig, item)));
    }

    [Fact]
    public void A_slice_send_that_would_key_is_refused_aloud()
    {
        Assert.Equal(new[] { Transmit },
            Said(() => VoiceMessageSender.SendSliceRecording(_rig, SliceRecorderTarget.ActiveSlice)));
        Assert.False(VoiceMessageSender.InFlight);
    }

    [Fact]
    public void A_slice_send_goes_on_when_the_rig_is_not_at_zero()
    {
        Keyable();
        Assert.DoesNotContain(Transmit, Said(() => VoiceMessageSender.SendSliceRecording(_rig, SliceRecorderTarget.ActiveSlice)));
    }

    [Fact]
    public void A_computer_send_that_would_key_is_refused_aloud()
    {
        Assert.Equal(new[] { Transmit }, Said(() => VoiceMessageSender.SendComputerRecording(_rig, "x.flac", SendRoute.Box)));
    }

    [Fact]
    public void A_computer_send_joining_a_transmission_keys_nothing_and_is_not_refused()
    {
        Assert.DoesNotContain(Transmit, Said(() => VoiceMessageSender.SendComputerRecording(_rig, "x.flac", SendRoute.JoinTransmission)));
    }

    [Fact]
    public void Every_door_the_hold_asks_at_also_asks_here_on_the_same_condition()
    {
        string voice = Source("JJFlexWpf", "VoiceMessageSender.cs");
        foreach (string line in new[]
        {
            "if (ZeroPowerDoors.RefuseTransmit(rig, \"a message key\")) return;",
            "if (route == SendRoute.Box && ZeroPowerDoors.RefuseTransmit(rig, \"a recording send\")) return false;",
            "if (ZeroPowerDoors.RefuseTransmit(rig, \"a send\"))",
            "if (way != ResumeWay.Join && ZeroPowerDoors.RefuseTransmit(rig, \"a send carrying on\"))",
        })
            Assert.Contains(line, voice);

        string commands = Source("JJFlexWpf", "KeyCommands.cs");
        int hold = commands.IndexOf("ptt.RefuseIfHeldAfterCut(\"a CW message\")", StringComparison.Ordinal);
        int door = commands.IndexOf("if (ZeroPowerDoors.RefuseTransmit(rig, \"a CW message\")) break;", StringComparison.Ordinal);
        int send = commands.IndexOf("_context.SendCW(msg);", StringComparison.Ordinal);
        Assert.True(hold > 0 && door > hold && send > door);
    }

    [Fact]
    public void A_box_send_turned_away_late_gives_VOX_back_and_keeps_a_paused_place()
    {
        string voice = Source("JJFlexWpf", "VoiceMessageSender.cs");
        int door = voice.IndexOf("if (ZeroPowerDoors.RefuseTransmit(rig, \"a send\"))", StringComparison.Ordinal);
        int pause = voice.IndexOf("pause.Dispose();", door, StringComparison.Ordinal);
        int place = voice.IndexOf("KeepPausedPlace(info);", door, StringComparison.Ordinal);
        int hand = voice.IndexOf("HandOffNow(info);", door, StringComparison.Ordinal);
        Assert.True(door > 0 && pause > door && place > pause && hand > place);
    }

    [Fact]
    public void A_send_carrying_on_is_refused_with_nothing_changed_so_it_stays_paused()
    {
        string resume = Body(Source("JJFlexWpf", "VoiceMessageSender.cs"), "private static void ResumePaused()");
        int door = resume.IndexOf("ZeroPowerDoors.RefuseTransmit(rig, \"a send carrying on\")", StringComparison.Ordinal);
        int leaves = resume.IndexOf("_paused = null;", StringComparison.Ordinal);
        Assert.True(door > 0 && leaves > door);
    }

    // ── A record door, not a key door ───────────────────────────────────

    private static Task<RecorderOutcome> PressRecord(FlexBase rig) =>
        RecorderControls.ToggleSliceRecordAsync(rig, SliceRecorderTarget.ActiveSlice);

    [Fact]
    public void F11_in_receive_is_left_alone_at_any_power()
    {
        RadioFor();
        Assert.DoesNotContain(Transmit, Said(() => PressRecord(_rig).GetAwaiter().GetResult()));
    }

    [Fact]
    public void F11_while_a_hardware_key_holds_the_radio_at_zero_is_refused_with_the_same_sentence()
    {
        object radio = RadioFor();
        radio.GetType().GetField("_interlockState", Any)!.SetValue(radio, Enum.Parse(
            radio.GetType().GetProperty("InterlockState")!.PropertyType, "Transmitting"));
        Assert.True(_rig.RadioInterlockReportsTransmit);
        Assert.Equal(new[] { Transmit }, Said(() => PressRecord(_rig).GetAwaiter().GetResult()));
    }

    [Fact]
    public void F11_while_transmitting_goes_on_when_the_rig_is_not_at_zero()
    {
        object radio = RadioFor();
        radio.GetType().GetField("_interlockState", Any)!.SetValue(radio, Enum.Parse(
            radio.GetType().GetProperty("InterlockState")!.PropertyType, "Transmitting"));
        Keyable();
        Assert.DoesNotContain(Transmit, Said(() => PressRecord(_rig).GetAwaiter().GetResult()));
    }

    [Fact]
    public void A_recording_running_can_always_be_stopped()
    {
        string record = Body(Source("JJFlexWpf", "RecorderControls.cs"), "public static async Task<RecorderOutcome> ToggleSliceRecordAsync(");
        int asks = record.IndexOf("!rig.IsSliceRecording(target)\n            && ZeroPowerDoors.RefuseTransmit(", StringComparison.Ordinal);
        Assert.True(asks > 0);
    }
}
