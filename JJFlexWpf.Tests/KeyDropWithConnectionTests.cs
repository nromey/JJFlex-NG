using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #596 at the production edges: when the radio's connection falls, the real
/// <see cref="PttSafetyController"/> behind the real <c>MainWindow</c> drops
/// the key, held or locked, says why, and a key still held keys nothing when
/// the radio comes back until it has been let go.
/// </summary>
/// <remarks>
/// <para>
/// <b>Desk safety, the same way <c>LockSafetyHoldTests</c> does it.</b> No
/// window is constructed: the MainWindow is
/// <see cref="RuntimeHelpers.GetUninitializedObject"/>, only its PTT fields are
/// set and its edge methods invoked, and every test asserts it runs on an MTA
/// thread, where WPF refuses to build any element. Nothing is audible: the
/// controller has chirps and speech off and <see cref="QuietRun"/> has render
/// off. The rig is an unconnected <see cref="FlexBase"/> whose transmit writes
/// enqueue onto a queue that is not running.
/// </para>
/// <para>
/// <b>Driven through <c>ReleaseKeyForDroppedConnection</c>, not the event
/// handler</b>, because that handler's first act is to marshal to the window's
/// dispatcher, which an uninitialized window does not have. The ordering that
/// makes the handler run before <c>TransmitChangeHandler</c> is pinned on the
/// rig side, in <c>Radios.Tests.ConnectionFallArchiveTests</c>.
/// </para>
/// <para>
/// <b>What this does NOT prove:</b> that a real connection's fall reaches the
/// handler on a running application, or what a screen reader does with the
/// sentence if a window opens at the same moment. Those are bench steps.
/// </para>
/// </remarks>
public sealed class KeyDropWithConnectionTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly MainWindow _main;
    private readonly PttHoldFilter _filter = new PttHoldFilter();
    private readonly VerbosityLevel _verbosityBefore;

    public KeyDropWithConnectionTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(
            () => _rig,
            () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });

        _main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        Put("_pttController", _controller);
        Put("_radioPowerOn", true);
        Put("_pttHoldFilter", _filter);
        Put("_pttHeldThroughDrop", new PttHeldThroughDrop());
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
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    private void Put(string field, object value)
    {
        var f = typeof(MainWindow).GetField(field, Any);
        Assert.True(f != null, "production field MainWindow." + field + " is missing");
        f.SetValue(_main, value);
    }

    private object Get(string field)
    {
        var f = typeof(MainWindow).GetField(field, Any);
        Assert.True(f != null, "production field MainWindow." + field + " is missing");
        return f.GetValue(_main);
    }

    private void Call(string method, params object[] args)
    {
        var m = typeof(MainWindow).GetMethod(method, Any);
        Assert.True(m != null, "production entry MainWindow." + method + " is missing");
        m.Invoke(_main, args);
    }

    private bool RfCommandedOn()
    {
        var f = typeof(PttSafetyController).GetField("_rfCommandedOn", Any);
        Assert.True(f != null, "PttSafetyController._rfCommandedOn is missing");
        return (bool)f.GetValue(_controller);
    }

    private PttHeldThroughDrop Guard => (PttHeldThroughDrop)Get("_pttHeldThroughDrop");

    private void Drop(bool radioWasKeyed = true, bool deliberate = false) =>
        Call("ReleaseKeyForDroppedConnection", radioWasKeyed, deliberate);

    private static string Spoken(Action act)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
        act();
        string newest = ScreenReaderOutput.RecentMessages.FirstOrDefault() ?? "";
        return newest == marker ? "" : newest;
    }

    /// <summary>A held push to talk through the system-wide door, the radio keyed.</summary>
    private void HoldTheKey()
    {
        Call("HandleSystemWidePttEdge", true);
        Assert.Equal(PttSafetyController.PttState.PttHold, _controller.State);
        Assert.True(RfCommandedOn(), "positive control: the held key keyed");
    }

    [Fact]
    public void A_drop_under_a_held_key_drops_the_key_and_says_why()
    {
        AssertTheDeskIsSafe();
        HoldTheKey();

        string heard = Spoken(() => Drop());

        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(RfCommandedOn(), "the radio dropped and our side still says transmit");
        Assert.Equal(Lexicon.Get("connect.drop.transmit_ended"), heard);
        Assert.True(Guard.Armed, "the key held through the drop was not remembered");
    }

    [Fact]
    public void A_drop_before_the_radio_echoed_our_key_still_says_why()
    {
        // Our push to talk keyed a moment ago and the radio had not yet said
        // so when the link fell: the rig reports "not keyed", and the
        // controller's own release is what must carry the sentence.
        AssertTheDeskIsSafe();
        HoldTheKey();

        string heard = Spoken(() => Drop(radioWasKeyed: false));

        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.Equal(Lexicon.Get("connect.drop.transmit_ended"), heard);
    }

    [Fact]
    public void A_drop_under_a_lock_releases_the_lock_and_leaves_the_next_press_alone()
    {
        AssertTheDeskIsSafe();
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);

        Drop();

        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(RfCommandedOn());
        // No key was held, so nothing is remembered: the first deliberate
        // press after the reconnect keys at once.
        Assert.False(Guard.Armed);
        Call("HandleDialogPttEdge", true);
        Assert.True(RfCommandedOn(), "the first press after a lock was dropped was swallowed");
        Call("HandleDialogPttEdge", false);
    }

    [Fact]
    public void A_key_held_through_the_drop_auto_repeating_does_not_key_when_the_radio_returns()
    {
        AssertTheDeskIsSafe();
        HoldTheKey();
        Drop();
        Put("_radioPowerOn", false);   // the radio is gone...
        Put("_radioPowerOn", true);    // ...and back, the key still down

        for (int i = 0; i < 20; i++)
        {
            Call("HandleDialogPttEdge", true);   // a down every ~30 ms, no up
            Assert.False(RfCommandedOn(), "auto-repeat keyed the radio after the reconnect, repeat " + i);
            Thread.Sleep(30);
        }

        // Positive control: let go, wait past the gap, press. That keys.
        Call("HandleDialogPttEdge", false);
        Thread.Sleep(_filter.FirstGapDeferMs + 150);
        Call("HandleDialogPttEdge", true);
        Assert.True(RfCommandedOn(), "a real press after the release did not key");
        Call("HandleDialogPttEdge", false);
    }

    [Fact]
    public void A_key_held_through_the_drop_under_jaws_does_not_key_when_the_radio_returns()
    {
        AssertTheDeskIsSafe();
        HoldTheKey();
        Drop();
        Put("_radioPowerOn", false);
        Put("_radioPowerOn", true);

        // JAWS: down/up pairs about 250 ms apart, each up a few ms after its down.
        for (int pair = 0; pair < 5; pair++)
        {
            Call("HandleDialogPttEdge", true);
            Assert.False(RfCommandedOn(), "a synthesised down keyed the radio after the reconnect, pair " + pair);
            Thread.Sleep(5);
            Call("HandleDialogPttEdge", false);
            Thread.Sleep(245);
        }
        Assert.True(Guard.Armed);

        Thread.Sleep(_filter.FirstGapDeferMs + 150);
        Call("HandleDialogPttEdge", true);
        Assert.True(RfCommandedOn(), "a real press after the release did not key");
        Call("HandleDialogPttEdge", false);
    }

    [Fact]
    public void Our_own_disconnect_releases_the_key_without_calling_it_a_drop()
    {
        AssertTheDeskIsSafe();
        HoldTheKey();

        string heard = Spoken(() => Drop(radioWasKeyed: true, deliberate: true));

        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(RfCommandedOn());
        Assert.NotEqual(Lexicon.Get("connect.drop.transmit_ended"), heard);
    }

    [Fact]
    public void A_radio_keyed_by_something_else_is_still_told_it_is_off_the_air()
    {
        AssertTheDeskIsSafe();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        string heard = Spoken(() => Drop(radioWasKeyed: true));

        Assert.Equal(Lexicon.Get("connect.drop.transmit_ended"), heard);
        Assert.False(Guard.Armed, "nothing of ours was held");
    }
}
