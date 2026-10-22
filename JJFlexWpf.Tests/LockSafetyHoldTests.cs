using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Radios.Tests.Fixtures;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #571 and #800 at the production edges: the real <see cref="PttSafetyController"/>
/// behind the real <c>MainWindow</c> edge methods, driven with the measured
/// JAWS key streams and with a hold begun on the controller itself. Every
/// keying assertion lands on the controller's RF command (<c>_rfCommandedOn</c>,
/// the one field <c>SetTx</c> writes before <c>rig.Transmit</c>) and its state,
/// which is the production gate; every speech assertion on what reached the
/// screen reader past the verbosity gate (<see cref="ScreenReaderOutput.RecentMessages"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Desk safety, checked against <c>Infrastructure/</c> rather than
/// assumed, the same way <c>DialogTransmitKeysTests</c> does it.</b> No window
/// is constructed anywhere in this class. The MainWindow is
/// <see cref="RuntimeHelpers.GetUninitializedObject"/>: no constructor runs,
/// no XAML loads, no Dispatcher pump, no HWND; only its private PTT fields
/// are set and its edge methods invoked. Nothing is audible: <see cref="QuietRun"/>
/// has render off before any type here loads, the controller is built with
/// chirps and speech off, and the first assertion of every test refuses to go
/// on if the run is not silent and isolated. No radio is touched: the rig is
/// an unconnected <see cref="FlexBase"/>, whose <c>Transmit</c> and <c>Vox</c>
/// setters enqueue onto a command queue that is not running or find no slice
/// to write. The hold's DispatcherTimer is created on this thread's dispatcher,
/// which never pumps; expiry is therefore driven through the controller's
/// public <c>RefuseIfHeldAfterCut</c>, which notices it on the next press, and
/// the one test that waits does so for the real five seconds.
/// </para>
/// <para>
/// <b>What this does NOT prove:</b> WPF's delivery of key events to the
/// preview handlers (the repeat flag is the event's own), the radio's VOX
/// write (the bare rig has no slice; the VOX paths are driven in
/// <c>Radios.Tests.ReflectedCutHoldTests</c>), or RF.
/// </para>
/// </remarks>
public sealed class LockSafetyHoldTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly MainWindow _main;
    private readonly VerbosityLevel _verbosityBefore;

    private static readonly TransmitPowerReading TheCut =
        new TransmitPowerReading(17.8f, 10.3f, skewMilliseconds: 4f, ageMilliseconds: 20f, commandedWatts: 25);

    public LockSafetyHoldTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;   // Off: everything here must work there
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(
            () => _rig,
            () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });

        _main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        Put("_pttController", _controller);
        Put("_radioPowerOn", true);
        Put("_pttHoldFilter", new PttHoldFilter());
        Put("_lockPairDetector", new LockKeyPairDetector());
        Dialogs.AudioWorkshopDialog.PttControllerSource = () => _controller;
    }

    public void Dispose()
    {
        Dialogs.AudioWorkshopDialog.PttControllerSource = null;
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        _rig.Dispose();
    }

    private static void AssertTheDeskIsSafe()
    {
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    // ── plumbing: by name, so a missing production member is a red test, not a compile error ──

    private void Put(string field, object value)
    {
        var f = typeof(MainWindow).GetField(field, Any);
        Assert.True(f != null, "production field MainWindow." + field + " is missing");
        f.SetValue(_main, value);
    }

    private object Call(string method, params object[] args)
    {
        var m = typeof(MainWindow).GetMethod(method, Any);
        Assert.True(m != null, "production entry MainWindow." + method + " is missing");
        return m.Invoke(_main, args);
    }

    private bool RfCommandedOn()
    {
        var f = typeof(PttSafetyController).GetField("_rfCommandedOn", Any);
        Assert.True(f != null, "PttSafetyController._rfCommandedOn is missing");
        return (bool)f.GetValue(_controller);
    }

    private void LockKeyDown(bool isRepeat) => Call("LockKeyDown", isRepeat);

    private void BeginHold() => _controller.BeginCutHold(_rig, TransmitSafety.ReflectedCut.Share, TheCut, "ANT2");

    private static string Spoken(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
        press();
        string newest = ScreenReaderOutput.RecentMessages.FirstOrDefault() ?? "";
        return newest == marker ? "" : newest;
    }

    // ──────────────────────────────────────────────────────────────────
    //  #800: a held key is one press, from the measured JAWS streams
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_measured_JAWS_hold_toggles_the_lock_once()
    {
        AssertTheDeskIsSafe();
        int toggles = 0;
        var last = _controller.State;
        foreach (var e in JawsShiftSpaceCapture20261022.HomeHold.Where(e => e.Down))
        {
            LockKeyDown(e.Repeat);
            if (_controller.State != last) { toggles++; last = _controller.State; }
        }
        Assert.Equal(31, JawsShiftSpaceCapture20261022.HoldDowns);   // what the stream delivered
        Assert.Equal(1, toggles);                                      // what the radio got
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        Assert.True(RfCommandedOn());
    }

    [Fact]
    public void The_measured_JAWS_taps_each_toggle_once_lock_unlock_lock_unlock()
    {
        AssertTheDeskIsSafe();
        var expected = new[]
        {
            PttSafetyController.PttState.Locked, PttSafetyController.PttState.Idle,
            PttSafetyController.PttState.Locked, PttSafetyController.PttState.Idle,
        };
        for (int i = 0; i < JawsShiftSpaceCapture20261022.HomeTaps.Length; i++)
        {
            LockKeyDown(JawsShiftSpaceCapture20261022.HomeTaps[i][0].Repeat);
            Assert.Equal(expected[i], _controller.State);
        }
        Assert.False(RfCommandedOn());
    }

    [Fact]
    public void A_quick_second_press_still_toggles_the_second_time()
    {
        // The control for the repeat rule: two real presses, however close,
        // are two toggles. Only the event's own repeat flag is consulted.
        AssertTheDeskIsSafe();
        LockKeyDown(false);
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        LockKeyDown(false);
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(RfCommandedOn());
    }

    // ──────────────────────────────────────────────────────────────────
    //  #571: nothing re-keys inside the window, through every door
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Inside_the_window_the_lock_key_does_not_key_and_the_explanation_is_delivered_at_Off()
    {
        AssertTheDeskIsSafe();
        BeginHold();
        Assert.True(_controller.CutHold.Holding);

        string heard = Spoken(() => LockKeyDown(false));

        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(RfCommandedOn(), "the refused press reached the rig");
        Assert.Equal(1, _controller.CutHold.RefusedPresses);
        Assert.StartsWith("Not keyed. 58 percent of your power was coming back on ANT2", heard);
        Assert.Equal(_controller.CutHold.LastRefusalSentence, heard);
    }

    [Fact]
    public void Inside_the_window_push_to_talk_does_not_key()
    {
        AssertTheDeskIsSafe();
        BeginHold();
        Call("HandleSystemWidePttEdge", true);    // the system-wide chord's down, into the shared edge
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(RfCommandedOn());
        Call("HandleSystemWidePttEdge", false);
        Call("HandleDialogPttEdge", true);        // and the dialog door's
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Call("HandleDialogPttEdge", false);
        Assert.Equal(2, _controller.CutHold.RefusedPresses);
    }

    [Fact]
    public void Inside_the_window_the_system_wide_lock_and_the_Transmit_button_do_not_key()
    {
        AssertTheDeskIsSafe();
        BeginHold();
        Call("HandleSystemWideLockToggle");
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Put("RigControl", _rig);                 // ToggleTransmit's own gate
        Call("ToggleTransmit");
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(RfCommandedOn());
        Assert.Equal(2, _controller.CutHold.RefusedPresses);
    }

    [Fact]
    public void Inside_the_window_a_voice_message_is_refused_before_its_recording_is_looked_for()
    {
        AssertTheDeskIsSafe();
        BeginHold();
        var item = new CWMessageItem { Label = "CQ", Audio = "no-such-recording" };
        string heard = Spoken(() => VoiceMessageSender.Send(_rig, item));
        Assert.False(VoiceMessageSender.InFlight);
        Assert.Equal(1, _controller.CutHold.RefusedPresses);
        Assert.StartsWith("Not keyed.", heard);
    }

    [Fact]
    public void Inside_the_window_a_transmit_checks_carrier_is_refused_with_the_same_sentence()
    {
        AssertTheDeskIsSafe();
        BeginHold();
        using (TransmitKillSwitch.Arm(_rig, "the transmitter check"))
        {
            Assert.False(TransmitKillSwitch.RaiseCarrier(_rig, TransmitKillSwitch.Carrier.Mox));
            Assert.Equal(_controller.CutHold.LastRefusalSentence, TransmitKillSwitch.LastRaiseRefusal);
            Assert.StartsWith("Not keyed.", TransmitKillSwitch.LastRaiseRefusal);
        }
        Assert.Equal(1, _controller.CutHold.RefusedPresses);
    }

    [Fact]
    public void The_tune_carrier_is_not_held()
    {
        // Noel, 2026-10-22 05:42: "you should be able to tune right away to
        // rectify the problem". ToggleTuneCarrier needs the button, which an
        // uninitialised window has not got; the pin that it consults nothing
        // is in Radios.Tests.LockSafetySourcePinTests. What CAN be shown here:
        // a tune starting lifts the hold, so the press after it keys.
        AssertTheDeskIsSafe();
        BeginHold();
        var atCut = ReflectedCutHold.Snapshot(_rig);
        var tuning = new ReflectedCutHold.TransmitSetup(atCut.TxAntenna, atCut.TxFrequencyHz, atCut.RfPower, atCut.TunePower,
                                                        tuning: true, atCut.AmplifierSignature);
        Assert.True(_controller.CutHold.Tick(tuning, Environment.TickCount64));
        Assert.False(_controller.CutHold.Holding);
        LockKeyDown(false);
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        Assert.True(RfCommandedOn());
    }

    [Fact]
    public void An_unlock_is_never_refused()
    {
        // The hold only ever stops RF. Locked when a (second) hold begins is
        // not reachable from the cut, but the rule is cheap to state: from any
        // transmitting state the lock key unlocks, hold or no hold.
        AssertTheDeskIsSafe();
        LockKeyDown(false);
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        BeginHold();
        LockKeyDown(false);
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(RfCommandedOn());
        Assert.Equal(0, _controller.CutHold.RefusedPresses);
    }

    // ──────────────────────────────────────────────────────────────────
    //  #571: the hold lifts on a change, and expires
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_operators_antenna_change_lifts_the_hold_and_the_next_press_keys_at_once()
    {
        AssertTheDeskIsSafe();
        BeginHold();
        LockKeyDown(false);
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        // The rig's hook cannot fire on a bare rig (it needs a connection), so
        // the signal is handed to the hold as the controller's subscriber does.
        _controller.CutHold.OperatorChanged("TXAntennaName", Environment.TickCount64);
        Assert.False(_controller.CutHold.Holding);

        LockKeyDown(false);
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        Assert.True(RfCommandedOn());
        Assert.Equal(1, _controller.CutHold.RefusedPresses);
    }

    [Fact]
    public void After_five_seconds_the_press_keys_as_normal()
    {
        // The real clock, once: the controller's hold runs on
        // Environment.TickCount64 and this is the one assertion that cannot be
        // made any other way on the real controller.
        AssertTheDeskIsSafe();
        BeginHold();
        LockKeyDown(false);
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        Thread.Sleep(ReflectedCutHold.HoldMs + 100);

        LockKeyDown(false);
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        Assert.True(RfCommandedOn());
        Assert.Equal(ReflectedCutHold.EndReason.Expired, _controller.CutHold.LastEnd);
    }

    [Fact]
    public void Disposing_the_controller_ends_the_hold()
    {
        AssertTheDeskIsSafe();
        BeginHold();
        _controller.Dispose();
        Assert.False(_controller.CutHold.Holding);
        Assert.Equal(ReflectedCutHold.EndReason.Abandoned, _controller.CutHold.LastEnd);
    }
}
