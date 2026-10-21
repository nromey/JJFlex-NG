using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;
using Keys = System.Windows.Forms.Keys;

namespace JJFlexWpf.Tests;

/// <summary>
/// #778c: Ctrl+Space and Shift+Space pressed in any WPF dialog are push to
/// talk and the transmit lock (Noel, 2026-10-21 06:30); Escape while
/// transmitting unkeys first; a held Shift+Space toggles once; an Audio Check
/// owns the transmitter; window loss releases at once. Driven through the
/// PRODUCTION entry: the door's <c>KeyDown</c>/<c>KeyUp</c>, wired to the
/// real <c>MainWindow</c> edge methods on an uninitialised MainWindow, so the
/// repeat guard, both <see cref="PttHoldFilter"/>s and the real
/// <see cref="PttSafetyController"/> are all on the route. Every release
/// assertion lands on the controller's state, the production gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Desk safety, checked against <c>Infrastructure/</c> rather than
/// assumed.</b> No window is constructed anywhere in this class. The
/// MainWindow is <see cref="RuntimeHelpers.GetUninitializedObject"/>: no
/// constructor runs, no XAML loads, no Dispatcher pump, no HWND; only its
/// private PTT fields are set and its edge methods invoked, which is how
/// Codex's verification probe reached them. The hold window is a fake that
/// raises two events. The editable-text tests build controls on a throwaway
/// STA thread with no PresentationSource; a FrameworkElement outside a Window
/// is never shown. Nothing is audible: <see cref="QuietRun"/> has render off
/// before any type here loads, the controller is built with chirps and speech
/// off, and the first assertion of every test refuses to go on if the run is
/// not silent and isolated. No radio is touched: the rig is an unconnected
/// <see cref="FlexBase"/>, whose <c>Transmit</c> setter enqueues onto a command
/// queue that is not running (<c>q_t.Enqueue</c> traces "outside main loop"
/// and runs nothing).
/// </para>
/// <para>
/// <b>Why reflection for the new members.</b> Everything this round added
/// (<c>HandleDialogEscapeUnkey</c>, <c>HandleDialogHoldLost</c>, the lock
/// edges' filter and clock, the door's three new delegates) is reached by
/// name, so this file compiles against <c>b53b2a05</c> and every test that
/// depends on the fix fails there with "production entry X is missing"
/// rather than failing to compile. That is the red run.
/// </para>
/// <para>
/// <b>What this does NOT prove:</b> WPF's delivery of the preview events to
/// the door, the Window class handler's order against the leader/layer check,
/// the Audio Workshop's real session state, or RF. Those are pinned from
/// source in <c>Radios.Tests.ValueSubLayerTests</c> and pressed at the bench.
/// </para>
/// </remarks>
public sealed class DialogTransmitKeysTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly PttHoldFilter _pttFilter = new();
    private readonly LockPressClassifier _lockPresses = new();
    private readonly MainWindow _main;
    private readonly VerbosityLevel _verbosityBefore;
    private long _clock = 100_000;
    private bool _sessionActive;
    private int _downs, _ups, _locks, _escapes, _releases;

    public DialogTransmitKeysTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(
            () => _rig,
            () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });

        _main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        Put("_pttController", _controller);
        Put("_radioPowerOn", true);
        Put("_pttHoldFilter", _pttFilter);
    }

    /// <summary>
    /// The rest of the wiring, done lazily so a test of the OLD behaviour can
    /// still construct the fixture on the base: the new fields and delegates
    /// are demanded by name only when a test needs them.
    /// </summary>
    private void WireDoor()
    {
        _lockPresses.SetKeyRepeatDelay(500);   // the measured machine's repeat delay (~512 ms)
        Put("_lockPresses", _lockPresses);
        Put("PttClock", (Func<long>)(() => _clock));
        Door("PttEdge", (Action<bool>)(down =>
        {
            if (down) _downs++; else _ups++;
            Call("HandleDialogPttEdge", down);
        }));
        Door("ToggleLock", (Action)(() => { _locks++; Call("HandleDialogLockToggle"); }));
        Door("HoldInFlight", (Func<bool>)(() => _controller.State == PttSafetyController.PttState.PttHold));
        Door("EscapeUnkey", (Func<bool>)(() => { _escapes++; return (bool)Call("HandleDialogEscapeUnkey")!; }));
        Door("ReleaseNow", (Action)(() => { _releases++; Call("HandleDialogHoldLost"); }));
        Door("SessionOwnsTransmitter", (Func<bool>)(() => _sessionActive));
    }

    public void Dispose()
    {
        DialogTransmitKeys.HoldWindowLost("test teardown");
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        foreach (var name in new[] { "PttEdge", "ToggleLock", "HoldInFlight", "EscapeUnkey", "ReleaseNow", "SessionOwnsTransmitter" })
            typeof(DialogTransmitKeys).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
        ((System.Windows.Threading.DispatcherTimer?)typeof(MainWindow).GetField("_pttDeferTimer", Any)?.GetValue(_main))?.Stop();
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        _controller.Dispose();
        _rig.Dispose();
    }

    // ── plumbing: by name, so a missing production member is a red test, not a compile error ──

    private void Put(string field, object? value)
    {
        var f = typeof(MainWindow).GetField(field, Any);
        Assert.True(f != null, "production field MainWindow." + field + " is missing");
        f!.SetValue(_main, value);
    }

    private object? Call(string method, params object[] args)
    {
        var m = typeof(MainWindow).GetMethod(method, Any);
        Assert.True(m != null, "production entry MainWindow." + method + " is missing");
        return m!.Invoke(_main, args);
    }

    private object? Get(string field)
    {
        var f = typeof(MainWindow).GetField(field, Any);
        Assert.True(f != null, "production field MainWindow." + field + " is missing");
        return f!.GetValue(_main);
    }

    /// <summary>
    /// The deferred-release timer ran out. Invokes the production Tick
    /// delegate directly, as Codex's probe did, so no dispatcher is pumped
    /// and no wall clock is waited on: this represents expiry, not timing.
    /// </summary>
    private void DeferTimerExpires()
    {
        var timer = (System.Windows.Threading.DispatcherTimer?)Get("_pttDeferTimer");
        Assert.True(timer != null, "no deferral timer was armed");
        timer!.Stop();
        var tick = (EventHandler?)typeof(System.Windows.Threading.DispatcherTimer).GetField("Tick", Any)?.GetValue(timer);
        Assert.True(tick != null, "the production timer has no Tick handler");
        tick!(timer, EventArgs.Empty);
    }

    private static void Door(string property, Delegate value)
    {
        var p = typeof(DialogTransmitKeys).GetProperty(property, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(p != null, "production entry DialogTransmitKeys." + property + " is missing");
        p!.SetValue(null, value);
    }

    private static void AssertTheDeskIsSafe()
    {
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    /// <summary>A dialog reduced to the two ways it can take the keyboard away mid-hold.</summary>
    private sealed class FakeWindow : DialogTransmitKeys.IHoldWindow
    {
        public event EventHandler? Deactivated;
        public event EventHandler? Closed;
        public void LoseFocus() => Deactivated?.Invoke(this, EventArgs.Empty);
        public void Close() => Closed?.Invoke(this, EventArgs.Empty);
    }

    private static PttSafetyController.PttState Hold => PttSafetyController.PttState.PttHold;
    private static PttSafetyController.PttState Idle => PttSafetyController.PttState.Idle;
    private static PttSafetyController.PttState Locked => PttSafetyController.PttState.Locked;

    private static bool Down(FakeWindow w, Keys chord, bool repeat = false, bool text = false)
        => DialogTransmitKeys.KeyDown(w, chord, repeat, text);

    private static string SpokenAfter(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
        press();
        var recent = ScreenReaderOutput.RecentMessages;
        Assert.NotEqual(marker, recent.FirstOrDefault());
        return recent.First();
    }

    // ── The decision ──

    [Theory]
    [InlineData(Keys.Space | Keys.Control, false, false, "PushToTalkDown")]
    [InlineData(Keys.Space | Keys.Control, true, false, "PushToTalkRepeat")]
    [InlineData(Keys.Space | Keys.Control, false, true, "PushToTalkDown")]   // Noel 06:42: Ctrl+Space types nothing, so it still keys
    [InlineData(Keys.Space | Keys.Shift, false, false, "LockToggle")]
    [InlineData(Keys.Space | Keys.Shift, true, false, "LockRepeat")]         // B2: a held Shift+Space toggles once
    [InlineData(Keys.Space | Keys.Shift, false, true, "TypesASpace")]        // Noel 06:42: a text box keeps its space
    [InlineData(Keys.Escape, false, false, "Escape")]                        // B1: unkeys if transmitting
    [InlineData(Keys.Space, false, false, "NotOurs")]                        // plain Space presses the button, as it should
    [InlineData(Keys.Space | Keys.Alt, false, false, "NotOurs")]             // the system menu
    [InlineData(Keys.Space | Keys.Control | Keys.Shift, false, false, "NotOurs")]
    [InlineData(Keys.Space | Keys.Control | Keys.Alt, false, false, "NotOurs")]
    [InlineData(Keys.A | Keys.Control, false, false, "NotOurs")]
    [InlineData(Keys.Tab, false, false, "NotOurs")]
    public void The_verdict_for_a_chord(Keys chord, bool isRepeat, bool editableText, string expected)
    {
        AssertTheDeskIsSafe();
        // The verdict is named, not typed: the door is internal, so its enum
        // cannot appear in a public theory's signature.
        Assert.Equal(expected, DialogTransmitKeys.Decide(chord, isRepeat, editableText).ToString());
    }

    // ── Keying, and every way the hold ends, through the real edges ──

    [Fact]
    public void Ctrl_space_in_a_dialog_keys_through_the_edges_and_the_key_up_unkeys()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.Equal(Idle, _controller.State);

        Assert.True(Down(w, Keys.Space | Keys.Control));
        Assert.Equal(Hold, _controller.State);
        Assert.Same(w, DialogTransmitKeys.HoldWindow);

        // An ordinary human hold, long enough that the release is plausible.
        Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 20);
        Assert.True(DialogTransmitKeys.KeyUp(Keys.Space | Keys.Control));
        Assert.Equal(Idle, _controller.State);
        Assert.Null(DialogTransmitKeys.HoldWindow);
        Assert.Equal(1, _downs);
        Assert.Equal(1, _ups);
    }

    [Fact]
    public void Letting_go_of_ctrl_before_space_still_unkeys()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control));
        Assert.Equal(Hold, _controller.State);
        Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 20);
        Assert.False(DialogTransmitKeys.KeyUp(Keys.ControlKey));   // Ctrl's own up is nobody's release
        Assert.Equal(Hold, _controller.State);
        Assert.True(DialogTransmitKeys.KeyUp(Keys.Space));
        Assert.Equal(Idle, _controller.State);
    }

    [Fact]
    public void A_held_ctrl_space_repeat_is_consumed_and_keys_nothing_new()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control));
        for (int i = 0; i < 5; i++) Assert.True(Down(w, Keys.Space | Keys.Control, repeat: true));
        Assert.Equal(Hold, _controller.State);
        Assert.Equal(1, _downs);
    }

    [Fact]
    public void A_second_down_from_another_window_during_the_hold_keys_once()
    {
        // Through the real edge this is PttKeyDownEdge's _pttKeyDown guard,
        // and behind it the controller ignoring a down in any TX state.
        AssertTheDeskIsSafe(); WireDoor();
        var a = new FakeWindow();
        var b = new FakeWindow();
        Assert.True(Down(a, Keys.Space | Keys.Control));
        Assert.True(Down(b, Keys.Space | Keys.Control));
        Assert.Equal(Hold, _controller.State);
        Assert.Equal(2, _downs);          // both reached the edge
        Assert.Same(b, DialogTransmitKeys.HoldWindow);
        a.Close();
        Assert.Equal(Hold, _controller.State);
        b.Close();
        Assert.Equal(Idle, _controller.State);
    }

    // ── F2: window loss releases at once, even inside the absorber's deferral ──

    [Fact]
    public void Focus_moving_to_another_window_mid_hold_unkeys_at_once()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control));
        Assert.Equal(Hold, _controller.State);
        w.LoseFocus();
        Assert.Equal(Idle, _controller.State);
        Assert.Null(DialogTransmitKeys.HoldWindow);
        Assert.Equal(1, _releases);
        Assert.False(DialogTransmitKeys.KeyUp(Keys.Space));   // the later key-up has nothing to unkey
        Assert.Equal(Idle, _controller.State);
    }

    [Fact]
    public void The_dialog_closing_mid_hold_unkeys_at_once()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control));
        w.Close();
        Assert.Equal(Idle, _controller.State);
        Assert.Null(DialogTransmitKeys.HoldWindow);
    }

    [Fact]
    public void Window_loss_inside_the_synthetic_release_window_still_unkeys_at_once()
    {
        // Codex's F2 probe: a release the absorber would DEFER (it looks like a
        // screen reader's synthetic pair) must not defer a window loss. The
        // filter is put into the implausible window the way the probe did it,
        // then the window goes away.
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control));
        _pttFilter.NoteDown(Environment.TickCount64);   // "the down was a moment ago"
        w.LoseFocus();
        Assert.Equal(Idle, _controller.State);
        Assert.False(_pttFilter.ReleasePending);
        Assert.Null(DialogTransmitKeys.HoldWindow);
    }

    [Fact]
    public void A_deferred_key_up_keeps_the_window_subscribed_until_the_hold_really_ends()
    {
        // A key-up that lands in the synthetic window is deferred by the real
        // edge (PttKeyUpEdge arms its timer; the controller stays in the hold).
        // The window must stay subscribed, so that losing it then releases.
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control));
        _pttFilter.NoteDown(Environment.TickCount64);
        Assert.True(DialogTransmitKeys.KeyUp(Keys.Space));
        Assert.Equal(Hold, _controller.State);            // deferred, not released
        Assert.True(_pttFilter.ReleasePending);
        Assert.Same(w, DialogTransmitKeys.HoldWindow);    // still ours to release

        w.LoseFocus();
        Assert.Equal(Idle, _controller.State);
        Assert.Null(DialogTransmitKeys.HoldWindow);
    }

    // ── Repair round two, Codex F2: a finished hold forgets its dialog, and a dialog releases only its own hold ──

    [Fact]
    public void When_the_deferred_release_finally_runs_the_dialog_is_forgotten_and_a_later_system_wide_hold_survives_its_window_loss()
    {
        // Codex's sequence: dialog hold, deferred up, timer expiry, then a
        // system-wide hold, then the OLD dialog deactivates. Before this
        // round the stale owner released the new hold.
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control));
        _pttFilter.NoteDown(Environment.TickCount64);
        Assert.True(DialogTransmitKeys.KeyUp(Keys.Space));
        Assert.Equal(Hold, _controller.State);
        Assert.True(_pttFilter.ReleasePending);

        DeferTimerExpires();
        Assert.Equal(Idle, _controller.State);
        Assert.False(_pttFilter.ReleasePending);
        Assert.Null(DialogTransmitKeys.HoldWindow);       // the mutation control: skip HoldReleased() in the Tick and this fails

        Call("HandleSystemWidePttEdge", true);
        Assert.Equal(Hold, _controller.State);
        w.LoseFocus();
        Assert.Equal(Hold, _controller.State);            // not the dialog's to release
        Assert.Equal(0, _releases);
        Call("HandleSystemWidePttEdge", false);
    }

    [Fact]
    public void A_dialogs_window_loss_does_not_release_a_hold_another_door_keyed()
    {
        // The owner check on its own: the system-wide chord keys first, the
        // dialog's Ctrl+Space lands as a redundant down (the edge's repeat
        // guard) but subscribes the dialog, and the dialog then deactivates.
        // The hold in flight was never the dialog's.
        AssertTheDeskIsSafe(); WireDoor();
        Call("HandleSystemWidePttEdge", true);
        Assert.Equal(Hold, _controller.State);
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control));
        Assert.Same(w, DialogTransmitKeys.HoldWindow);
        Assert.Equal(Hold, _controller.State);

        w.LoseFocus();
        Assert.Equal(Hold, _controller.State);            // the mutation control: drop the door check in HandleDialogHoldLost and this fails
        Assert.Equal(1, _releases);                       // the door asked; MainWindow declined
        Assert.Null(DialogTransmitKeys.HoldWindow);
        Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 20);
        Call("HandleSystemWidePttEdge", false);
        Assert.Equal(Idle, _controller.State);
    }

    // ── B1: Escape while transmitting unkeys first ──

    [Fact]
    public void Escape_after_locking_in_a_dialog_unkeys_and_is_consumed_and_a_second_escape_is_not_ours()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Shift));
        Assert.Equal(Locked, _controller.State);

        Assert.True(Down(w, Keys.Escape));       // consumed: the dialog stays open
        Assert.Equal(Idle, _controller.State);
        Assert.Equal(1, _escapes);

        Assert.False(Down(w, Keys.Escape));      // nothing transmitting: the dialog's own Escape closes it
        Assert.Equal(Idle, _controller.State);
    }

    [Fact]
    public void Escape_during_a_hold_unkeys_and_releases_the_window()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control));
        Assert.Equal(Hold, _controller.State);
        Assert.True(Down(w, Keys.Escape));
        Assert.Equal(Idle, _controller.State);
        Assert.Null(DialogTransmitKeys.HoldWindow);
    }

    [Fact]
    public void Escape_during_a_deferred_release_forgets_the_hold_so_the_next_ctrl_space_keys()
    {
        // Repair round two, Codex B1: Escape unkeyed, but the hold filter's
        // pending deferral read the next Ctrl+Space as a continuation of a
        // hold Escape had already ended, and consumed it without keying.
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control));
        _pttFilter.NoteDown(Environment.TickCount64);
        Assert.True(DialogTransmitKeys.KeyUp(Keys.Space));
        Assert.Equal(Hold, _controller.State);
        Assert.True(_pttFilter.ReleasePending);

        Assert.True(Down(w, Keys.Escape));
        Assert.Equal(Idle, _controller.State);
        Assert.False(_pttFilter.ReleasePending);          // the mutation control: drop ResetPttInput() from HandleDialogEscapeUnkey and this fails
        Assert.False((bool)Get("_pttKeyDown")!);
        var timer = Get("_pttDeferTimer") as System.Windows.Threading.DispatcherTimer;
        Assert.False(timer?.IsEnabled ?? false, "the deferral timer must be stopped by Escape");

        Assert.True(Down(w, Keys.Space | Keys.Control));
        Assert.Equal(Hold, _controller.State);            // keys fresh, not swallowed
        Assert.Equal(2, _downs);
    }

    [Fact]
    public void Escape_with_nothing_transmitting_is_left_to_the_dialog()
    {
        AssertTheDeskIsSafe(); WireDoor();
        Assert.False(Down(new FakeWindow(), Keys.Escape));
        Assert.Equal(Idle, _controller.State);
    }

    [Fact]
    public void Escape_is_left_to_the_audio_workshop_while_an_audio_check_is_active()
    {
        // The workshop's two-stage Escape keeps its first stage: the door
        // declines, so AudioWorkshopDialog.OnPreviewKeyDown sees the key.
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Shift));
        Assert.Equal(Locked, _controller.State);
        _sessionActive = true;
        Assert.False(Down(w, Keys.Escape));
        Assert.Equal(Locked, _controller.State);
        Assert.Equal(0, _escapes);
    }

    // ── B2: a held Shift+Space toggles once ──

    [Fact]
    public void Shift_space_toggles_the_lock_and_its_windows_repeats_are_consumed_without_a_toggle()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Shift));
        Assert.Equal(Locked, _controller.State);
        for (int i = 0; i < 5; i++) Assert.True(Down(w, Keys.Space | Keys.Shift, repeat: true));
        Assert.Equal(Locked, _controller.State);
        Assert.Equal(1, _locks);
        Assert.Null(DialogTransmitKeys.HoldWindow);   // a lock is not a hold

        _clock += 400;
        DialogTransmitKeys.KeyUp(Keys.Space);
        _clock += 2_000;
        Assert.True(Down(w, Keys.Space | Keys.Shift));   // a second press, later: unlock
        Assert.Equal(Idle, _controller.State);
    }

    // Repair round two: the lock no longer guesses whether the key is still
    // held (Codex B2 swallowed a deliberate unlock). Each shape below is
    // replayed through the dialog door into the real lock edges, from BOTH
    // starting states. The state is asserted after EVERY event, because an
    // odd number of stray toggles lands on the right state by parity and
    // hides the defect (round one's first test did exactly that).

    private void LockDown(FakeWindow w, long at)
    {
        _clock = at;
        Assert.True(Down(w, Keys.Space | Keys.Shift));
    }

    private void SpaceUp(long at)
    {
        _clock = at;
        DialogTransmitKeys.KeyUp(Keys.Space);
    }

    private PttSafetyController.PttState Start(bool locked)
    {
        if (locked) { _controller.ToggleLock(); Assert.Equal(Locked, _controller.State); }
        return locked ? Locked : Idle;
    }

    private PttSafetyController.PttState Other(PttSafetyController.PttState s) => s == Locked ? Idle : Locked;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_held_shift_space_under_nvda_toggles_once(bool startLocked)
    {
        // One down, Windows' repeats carrying IsRepeat, one up two seconds on.
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        var from = Start(startLocked);
        var to = Other(from);

        LockDown(w, 100_000);
        Assert.Equal(to, _controller.State);
        for (int i = 0; i < 5; i++)
        {
            _clock += 33;
            Assert.True(Down(w, Keys.Space | Keys.Shift, repeat: true));
            Assert.Equal(to, _controller.State);
        }
        SpaceUp(102_000);
        Assert.Equal(to, _controller.State);
        Assert.Equal(1, _locks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_held_shift_space_under_jaws_toggles_once_from_the_measured_cold_stream(bool startLocked)
    {
        // The measured stream as the edges see it (Radios.Tests.PttHoldFilterTests
        // .The_measured_jaws_stream_arms_on_the_second_pair): down, the first
        // up at the repeat delay, then pairs every 250 ms with the up 5 ms
        // after its down. Codex's cold replay re-keyed at 762 ms on round
        // one's filter; here the first down toggles and nothing else does.
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        var from = Start(startLocked);
        var to = Other(from);

        LockDown(w, 100_000); Assert.Equal(to, _controller.State);
        SpaceUp(100_517);     Assert.Equal(to, _controller.State);
        long t = 100_762;
        for (int i = 0; i < 6; i++)
        {
            LockDown(w, t);   Assert.Equal(to, _controller.State);
            SpaceUp(t + 5);   Assert.Equal(to, _controller.State);
            t += 250;
        }
        Assert.Equal(7, _locks);                 // every pair reached the edge; the edge toggled once
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_held_shift_space_under_jaws_toggles_once_from_the_other_measured_first_press_shape(bool startLocked)
    {
        // The 2026-08-26 trace's shape: an up 3 ms after the first down, the
        // re-down at the repeat delay, then the 250 ms stream.
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        var from = Start(startLocked);
        var to = Other(from);

        LockDown(w, 100_000); Assert.Equal(to, _controller.State);
        SpaceUp(100_003);     Assert.Equal(to, _controller.State);
        long t = 100_501;
        for (int i = 0; i < 6; i++)
        {
            LockDown(w, t);   Assert.Equal(to, _controller.State);
            SpaceUp(t + 3);   Assert.Equal(to, _controller.State);
            t += 250;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_deliberate_tap_straight_after_a_jaws_stream_toggles(bool startLocked)
    {
        // Codex B2: five synthesised pairs, then a 120 ms press 133 ms after
        // the last up. Round one swallowed it and left the transmitter locked
        // with nothing said. It is decided at its up, by its own length.
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        var from = Start(startLocked);
        var to = Other(from);

        foreach (long at in new long[] { 100_000, 100_512, 100_762, 101_012, 101_262 })
        {
            LockDown(w, at); Assert.Equal(to, _controller.State);
            SpaceUp(at + 5); Assert.Equal(to, _controller.State);
        }
        LockDown(w, 101_400);
        Assert.Equal(to, _controller.State);     // pending: too soon after the last up to tell
        SpaceUp(101_520);
        Assert.Equal(from, _controller.State);   // the mutation control: toggle on every pending up and the pairs above fail; never toggle at the up and this fails
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_real_taps_300_ms_apart_toggle_twice(bool startLocked)
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        var from = Start(startLocked);
        var to = Other(from);

        LockDown(w, 100_000); Assert.Equal(to, _controller.State);
        SpaceUp(100_120);     Assert.Equal(to, _controller.State);
        LockDown(w, 100_300); Assert.Equal(to, _controller.State);     // inside the window: its up decides
        SpaceUp(100_420);     Assert.Equal(from, _controller.State);   // a human-length press toggles
    }

    [Fact]
    public void Two_human_taps_of_shift_space_well_apart_lock_then_unlock_at_their_downs()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        LockDown(w, 100_000); Assert.Equal(Locked, _controller.State);
        SpaceUp(100_120);
        LockDown(w, 102_000); Assert.Equal(Idle, _controller.State);
        SpaceUp(102_120);     Assert.Equal(Idle, _controller.State);
    }

    [Fact]
    public void The_system_wide_lock_toggles_at_once_even_inside_the_window_because_its_up_never_comes()
    {
        // SystemWideKeys posts one toggle per chord and swallows the chord's
        // own release, so a system-wide press cannot be decided at an up.
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        LockDown(w, 100_000); Assert.Equal(Locked, _controller.State);
        SpaceUp(100_120);
        _clock = 100_300;
        Call("HandleSystemWideLockToggle");
        Assert.Equal(Idle, _controller.State);
        _clock = 100_500;
        Call("HandleSystemWideLockToggle");
        Assert.Equal(Locked, _controller.State);
    }

    [Fact]
    public void A_key_up_while_the_radio_is_locked_leaves_the_lock_alone()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control));
        Assert.True(Down(w, Keys.Space | Keys.Shift));    // latch the lock mid-hold
        Assert.Equal(Locked, _controller.State);
        Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 20);
        Assert.False(DialogTransmitKeys.KeyUp(Keys.Space));
        Assert.Equal(Locked, _controller.State);
    }

    // ── B3: an Audio Check owns the transmitter ──

    [Fact]
    public void While_an_audio_check_is_active_push_to_talk_is_refused_out_loud_and_keys_nothing()
    {
        AssertTheDeskIsSafe(); WireDoor();
        _sessionActive = true;
        var w = new FakeWindow();
        string said = SpokenAfter(() => Assert.True(Down(w, Keys.Space | Keys.Control)));
        Assert.Equal("No push to talk here.", said);
        Assert.Equal(Idle, _controller.State);
        Assert.Equal(0, _downs);
        Assert.Null(DialogTransmitKeys.HoldWindow);
        // A held repeat stays quiet and keys nothing either.
        Assert.True(Down(w, Keys.Space | Keys.Control, repeat: true));
        Assert.Equal(0, _downs);
    }

    [Fact]
    public void While_an_audio_check_is_active_the_lock_is_refused_out_loud_and_toggles_nothing()
    {
        AssertTheDeskIsSafe(); WireDoor();
        _sessionActive = true;
        var w = new FakeWindow();
        string said = SpokenAfter(() => Assert.True(Down(w, Keys.Space | Keys.Shift)));
        Assert.Equal("No transmit lock here.", said);
        Assert.Equal(Idle, _controller.State);
        Assert.Equal(0, _locks);
    }

    [Fact]
    public void A_second_workshops_close_leaves_the_first_sessions_guard_standing()
    {
        // Repair round two, Codex B3. Settings constructs a second
        // AudioWorkshopDialog beside the tracked one; before this round
        // EVERY workshop's Closed handler cleared the static pointer, so
        // closing the second one made the first's active check invisible to
        // the door. Unconstructed objects, as Codex's probe: no window, no
        // session start, no transmit. What this test cannot see is the real
        // Closed lambda running (it reaches controls and the earcon player),
        // so the guarded statement itself is pinned from source in
        // Radios.Tests.ValueSubLayerTests; here the pointer is cleared the
        // way the fixed handler clears it, and the control shows the old
        // unguarded assignment would have dropped the guard.
        AssertTheDeskIsSafe(); WireDoor();
        var workshopType = typeof(JJFlexWpf.Dialogs.AudioWorkshopDialog);
        var first = RuntimeHelpers.GetUninitializedObject(workshopType);
        var second = RuntimeHelpers.GetUninitializedObject(workshopType);
        var sessionType = workshopType.GetNestedType("AudioCheckSession", BindingFlags.NonPublic);
        Assert.True(sessionType != null, "AudioWorkshopDialog.AudioCheckSession is missing");
        var session = RuntimeHelpers.GetUninitializedObject(sessionType!);
        var activeField = sessionType!.GetField("<Active>k__BackingField", Any);
        Assert.True(activeField != null, "AudioCheckSession.Active backing field is missing");
        activeField!.SetValue(session, true);
        var sessionField = workshopType.GetField("_session", Any);
        Assert.True(sessionField != null, "AudioWorkshopDialog._session is missing");
        sessionField!.SetValue(first, session);
        var instance = workshopType.GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(instance != null, "AudioWorkshopDialog._instance is missing");
        var guard = workshopType.GetProperty("AnAudioCheckIsActive", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(guard != null, "AudioWorkshopDialog.AnAudioCheckIsActive is missing");
        var before = instance!.GetValue(null);
        try
        {
            instance.SetValue(null, first);
            Door("SessionOwnsTransmitter", (Func<bool>)(() => (bool)guard!.GetValue(null)!));
            var w = new FakeWindow();
            Assert.True((bool)guard!.GetValue(null)!);
            Assert.True(Down(w, Keys.Space | Keys.Shift));
            Assert.Equal(Idle, _controller.State);          // refused: the check owns the transmitter

            // The second workshop closes: the fixed handler clears the
            // pointer only when it is this workshop.
            if (ReferenceEquals(instance.GetValue(null), second)) instance.SetValue(null, null);
            Assert.True((bool)guard.GetValue(null)!, "the first workshop's session is still active and still guarded");
            Assert.True(Down(w, Keys.Space | Keys.Shift));
            Assert.Equal(Idle, _controller.State);          // still refused

            // Control: the OLD handler's unguarded clear is what the probe
            // reproduced, and the guard then misses the live session.
            instance.SetValue(null, null);
            Assert.False((bool)guard.GetValue(null)!);

            // The tracked workshop's own close does clear it.
            instance.SetValue(null, first);
            if (ReferenceEquals(instance.GetValue(null), first)) instance.SetValue(null, null);
            Assert.False((bool)guard.GetValue(null)!);
        }
        finally
        {
            instance.SetValue(null, before);
        }
    }

    // ── The radio's own gate ──

    [Fact]
    public void With_the_radio_off_nothing_keys_and_the_operator_hears_the_no_radio_sentence()
    {
        AssertTheDeskIsSafe(); WireDoor();
        Put("_radioPowerOn", false);
        var w = new FakeWindow();
        string said = SpokenAfter(() => Assert.True(Down(w, Keys.Space | Keys.Control)));
        Assert.Equal("JJ Flexible Home, no radio connected", said);   // the #307 sentence at Chatty, named here so its wording is on record (F4)
        Assert.Equal(Idle, _controller.State);
        Assert.True(Down(w, Keys.Space | Keys.Shift));
        Assert.Equal(Idle, _controller.State);
    }

    // ── F1: the system-wide hook at its defaults leaves plain Ctrl+Space to the dialog ──

    [Fact]
    public void With_the_hook_on_at_its_defaults_a_dialogs_ctrl_space_reaches_the_door_and_keys_once()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var decider = new SystemWideKeyDecider { Set = SystemWideKeySet.Defaults };
        var ctrl = new SystemWideModifiers(true, false, false);
        var plain = decider.Decide(0x20 /* VK_SPACE */, true, ctrl);
        Assert.False(plain.Swallow);
        Assert.Equal(SystemWideAction.None, plain.Action);
        // Positive control: the configured chord IS the hook's.
        var chord = decider.Decide(0x20, true, new SystemWideModifiers(true, true, false));
        Assert.True(chord.Swallow);
        Assert.Equal(SystemWideAction.PttDown, chord.Action);

        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control));
        Assert.Equal(Hold, _controller.State);
        Assert.Equal(1, _downs);
    }

    // ── Text controls ──

    [Fact]
    public void Shift_space_in_an_editable_text_control_is_left_to_the_text_box()
    {
        AssertTheDeskIsSafe(); WireDoor();
        Assert.False(Down(new FakeWindow(), Keys.Space | Keys.Shift, text: true));
        Assert.Equal(Idle, _controller.State);
        Assert.Equal(0, _locks);
    }

    [Fact]
    public void Ctrl_space_in_an_editable_text_control_still_keys()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control, text: true));
        Assert.Equal(Hold, _controller.State);
        w.Close();
        Assert.Equal(Idle, _controller.State);
    }

    [Fact]
    public void Plain_space_and_alt_space_are_not_taken()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.False(Down(w, Keys.Space));
        Assert.False(Down(w, Keys.Space | Keys.Alt));
        Assert.Equal(Idle, _controller.State);
        Assert.Equal(0, _downs + _locks);
        Assert.False(DialogTransmitKeys.KeyUp(Keys.Space));
    }

    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var t = new Thread(() => { try { result = body(); } catch (Exception ex) { failure = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw failure;
        return result;
    }

    [Fact]
    public void Editable_text_is_a_writable_text_box_or_a_password_box_and_nothing_else()
    {
        AssertTheDeskIsSafe();
        var results = OnSta(() => new[]
        {
            DialogTransmitKeys.IsEditableText(new TextBox()),
            DialogTransmitKeys.IsEditableText(new TextBox { IsReadOnly = true }),
            DialogTransmitKeys.IsEditableText(new PasswordBox()),
            DialogTransmitKeys.IsEditableText(new Button()),
            DialogTransmitKeys.IsEditableText(new CheckBox()),
            DialogTransmitKeys.IsEditableText(new ListBox()),
            DialogTransmitKeys.IsEditableText(null),
        });
        Assert.Equal(new[] { true, false, true, false, false, false, false }, results);
    }

    [Fact]
    public void A_focused_part_inside_a_text_control_counts_as_the_text_control()
    {
        AssertTheDeskIsSafe();
        var (inside, insideReadOnly, besideInGrid) = OnSta(() =>
        {
            var run = new System.Windows.Documents.Run("x");
            var rich = new RichTextBox(new System.Windows.Documents.FlowDocument(new System.Windows.Documents.Paragraph(run)));
            var roRun = new System.Windows.Documents.Run("y");
            var readOnly = new RichTextBox(new System.Windows.Documents.FlowDocument(new System.Windows.Documents.Paragraph(roRun))) { IsReadOnly = true };
            var grid = new Grid();
            var button = new Button();
            grid.Children.Add(button);
            return (DialogTransmitKeys.IsEditableText(run), DialogTransmitKeys.IsEditableText(roRun), DialogTransmitKeys.IsEditableText(button));
        });
        Assert.True(inside);
        Assert.False(insideReadOnly);
        Assert.False(besideInGrid);
    }
}
