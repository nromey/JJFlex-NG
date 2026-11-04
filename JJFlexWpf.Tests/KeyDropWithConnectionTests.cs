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
        Assert.Equal(Lexicon.Get("connect.drop.transmit_released"), heard);
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
        Assert.Equal(Lexicon.Get("connect.drop.transmit_released"), heard);
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
        Assert.NotEqual(Lexicon.Get("connect.drop.transmit_released"), heard);
    }

    [Fact]
    public void A_radio_keyed_by_something_else_is_still_told_it_is_off_the_air()
    {
        AssertTheDeskIsSafe();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        string heard = Spoken(() => Drop(radioWasKeyed: true));

        Assert.Equal(Lexicon.Get("connect.drop.transmit_released"), heard);
        Assert.False(Guard.Armed, "nothing of ours was held");
    }

    // ── Repair round 1 (Codex's check of #596) ─────────────────────────────

    [Fact]
    public void The_drop_sentence_never_claims_the_radio_is_off_the_air()
    {
        // B4: a lost connection cannot show that RF stopped; a foot switch or
        // VOX may still be keying the radio.
        AssertTheDeskIsSafe();
        string said = Lexicon.Get("connect.drop.transmit_released");
        Assert.DoesNotContain("no longer on the air", said);
        Assert.Contains("dropped while you were transmitting", said);
        Assert.Contains("cannot tell whether the radio has stopped", said);
    }

    [Fact]
    public void A_lock_key_held_through_the_drop_under_jaws_does_not_toggle_the_lock_when_the_radio_returns()
    {
        // B3: Home's Shift+Space held as the link falls, then down/up pairs
        // about 250 ms apart, each a fresh-looking down, after the reconnect.
        AssertTheDeskIsSafe();
        Call("LockKeyDown", false);
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        Assert.True(RfCommandedOn(), "positive control: the lock key keyed");

        Drop();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.True(Guard.Armed, "the lock key held through the drop was not remembered");

        for (int pair = 0; pair < 5; pair++)
        {
            Call("LockKeyDown", false);
            Assert.False(RfCommandedOn(), "a synthesised lock-key down keyed the radio after the reconnect, pair " + pair);
            Thread.Sleep(5);
            Call("PttHeldThroughDrop", false);   // the Space up, as Home's key-up hands it over
            Thread.Sleep(245);
        }

        Thread.Sleep(_filter.FirstGapDeferMs + 150);
        Call("LockKeyDown", false);
        Assert.True(RfCommandedOn(), "a real lock press after the release did not key");
        _controller.EscapeUnlock();
    }

    [Fact]
    public void The_system_wide_lock_toggles_nothing_while_a_key_is_held_through_the_drop()
    {
        // B3: the system-wide lock's chord and the Transmit button's Space
        // reach a door that sees one toggle per press and no separate up.
        AssertTheDeskIsSafe();
        HoldTheKey();
        Drop();
        Assert.True(Guard.Armed);

        for (int pair = 0; pair < 5; pair++)
        {
            Call("HandleSystemWideLockToggle");
            Assert.False(RfCommandedOn(), "a system-wide lock toggle keyed the radio after the reconnect, pair " + pair);
            Thread.Sleep(250);
        }

        Thread.Sleep(_filter.FirstGapDeferMs + 150);
        Call("HandleSystemWideLockToggle");
        Assert.True(RfCommandedOn(), "a real lock toggle after the release did not key");
        _controller.EscapeUnlock();
    }

    [Fact]
    public void A_teardown_releases_a_held_key_and_remembers_it()
    {
        // B1: every teardown route unkeys first, through this.
        AssertTheDeskIsSafe();
        HoldTheKey();

        Call("ReleaseKeyForTeardown", "test teardown");

        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(RfCommandedOn(), "the teardown left our command to transmit standing");
        Assert.True(Guard.Armed, "a key held through the teardown could key the next radio");
    }

    [Fact]
    public void A_teardown_releases_a_lock_with_nothing_held_and_leaves_the_next_press_alone()
    {
        AssertTheDeskIsSafe();
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);

        Call("ReleaseKeyForTeardown", "test teardown");

        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(RfCommandedOn());
        Assert.False(Guard.Armed);
    }

    [Fact]
    public void Every_teardown_route_releases_the_key_before_anything_else()
    {
        // B1, the routes themselves: CloseTheRadio reaches UnwireRadioEvents
        // (menu disconnect, radio switch, exit, connecting elsewhere); the
        // power-off notification reaches PowerNowOffInternal. Each releases
        // the key first, before the cut hold, the check teardown and the
        // handlers that would otherwise have released it.
        AssertTheDeskIsSafe();
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(Repo(), "JJFlexWpf", "MainWindow.xaml.cs"));

        string unwire = Body(src, "public void UnwireRadioEvents()");
        int release = unwire.IndexOf("ReleaseKeyForTeardown(", StringComparison.Ordinal);
        Assert.True(release > 0, "UnwireRadioEvents no longer releases the key");
        Assert.True(release < unwire.IndexOf("ReleaseRigForTeardown(", StringComparison.Ordinal));
        Assert.True(release < unwire.IndexOf("EndCheckBeforeTeardown(", StringComparison.Ordinal));
        Assert.True(release < unwire.IndexOf("RigControl.TransmitChange -= TransmitChangeHandler", StringComparison.Ordinal));

        string off = Body(src, "private void PowerNowOffInternal()");
        int offRelease = off.IndexOf("ReleaseKeyForTeardown(", StringComparison.Ordinal);
        Assert.True(offRelease > 0, "the power-off notification no longer releases the key");
        Assert.True(offRelease < off.IndexOf("RigControl.TransmitChange -= TransmitChangeHandler", StringComparison.Ordinal));
        Assert.True(offRelease < off.IndexOf("_pttController?.Dispose();", StringComparison.Ordinal));

        string globals = System.IO.File.ReadAllText(System.IO.Path.Combine(Repo(), "globals.vb"));
        string close = globals.Substring(globals.IndexOf("Friend Sub CloseTheRadio()", StringComparison.Ordinal), 400);
        Assert.True(close.IndexOf("WpfMainWindow?.UnwireRadioEvents()", StringComparison.Ordinal)
                    < close.IndexOf("RigControl.Dispose()", StringComparison.Ordinal),
            "CloseTheRadio disposes the rig before the window releases the key");
    }

    private static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, signature + " moved");
        return src.Substring(at, Math.Min(6000, src.Length - at));
    }

    private static string Repo()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
