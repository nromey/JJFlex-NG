using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using JJFlexWpf.Tests.Infrastructure;
using JJTrace;
using Radios;
using Xunit;
using Keys = System.Windows.Forms.Keys;

namespace JJFlexWpf.Tests;

/// <summary>
/// #778c: Ctrl+Space pressed in any WPF dialog is push to talk (Noel,
/// 2026-10-21 06:30); Shift+Space there is consumed and refused for now, until
/// a quick JAWS tap has been measured at the bench (Noel, 10:55: "wait for the
/// bench"); Escape while transmitting unkeys first; an Audio Check owns the
/// transmitter; window loss releases at once. Driven through the PRODUCTION
/// entry: the door's <c>KeyDown</c>/<c>KeyUp</c>, wired to the real
/// <c>MainWindow</c> edge methods on an uninitialised MainWindow, so the
/// repeat guard, the <see cref="PttHoldFilter"/> and the real
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
/// <b>Why reflection for the MainWindow members.</b> Everything the repair
/// rounds added (<c>HandleDialogEscapeUnkey</c>, <c>HandleDialogHoldLost</c>,
/// the door's delegates) is reached by name, so a test that depends on a fix
/// fails on the base with "production entry X is missing" rather than failing
/// to compile. That is the red run.
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
    private readonly MainWindow _main;
    private readonly VerbosityLevel _verbosityBefore;
    private bool _sessionActive;
    private int _downs, _ups, _escapes, _releases;

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
    /// still construct the fixture on the base: the delegates are demanded by
    /// name only when a test needs them.
    /// </summary>
    private void WireDoor()
    {
        Door("PttEdge", (Action<bool>)(down =>
        {
            if (down) _downs++; else _ups++;
            Call("HandleDialogPttEdge", down);
        }));
        Door("HoldInFlight", (Func<bool>)(() => _controller.State == PttSafetyController.PttState.PttHold));
        Door("EscapeUnkey", (Func<bool>)(() => { _escapes++; return (bool)Call("HandleDialogEscapeUnkey")!; }));
        Door("ReleaseNow", (Action)(() => { _releases++; Call("HandleDialogHoldLost"); }));
        Door("SessionOwnsTransmitter", (Func<bool>)(() => _sessionActive));
    }

    public void Dispose()
    {
        DialogTransmitKeys.HoldWindowLost("test teardown");
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        foreach (var name in new[] { "PttEdge", "HoldInFlight", "EscapeUnkey", "ReleaseNow", "SessionOwnsTransmitter" })
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

    /// <summary>Lock the transmitter the way it still can be locked from outside Home: the system-wide chord's production door.</summary>
    private void LockFromTheSystemWideKey()
    {
        var before = _controller.State;
        Call("HandleSystemWideLockToggle");
        Assert.Equal(before == Locked ? Idle : Locked, _controller.State);
    }

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

    private static void NothingSpokenDuring(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
        press();
        Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
    }

    // ── The decision ──

    [Theory]
    [InlineData(Keys.Space | Keys.Control, false, false, "PushToTalkDown")]
    [InlineData(Keys.Space | Keys.Control, true, false, "PushToTalkRepeat")]
    [InlineData(Keys.Space | Keys.Control, false, true, "PushToTalkDown")]   // Noel 06:42: Ctrl+Space types nothing, so it still keys
    [InlineData(Keys.Space | Keys.Shift, false, false, "LockRefused")]       // round three: the dialog lock waits for the bench (Noel 10:55)
    [InlineData(Keys.Space | Keys.Shift, true, false, "LockRepeat")]         // a held Shift+Space stays consumed and quiet
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
    public void Escape_while_locked_from_the_system_wide_key_unkeys_and_is_consumed_and_a_second_escape_is_not_ours()
    {
        // Round three: a dialog can no longer lock, but Escape in a dialog
        // while the transmitter is LOCKED (from Home or the system-wide key)
        // still unkeys first (Noel 07:37, kept at 10:55).
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        LockFromTheSystemWideKey();
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
        LockFromTheSystemWideKey();
        Assert.Equal(Locked, _controller.State);
        _sessionActive = true;
        Assert.False(Down(w, Keys.Escape));
        Assert.Equal(Locked, _controller.State);
        Assert.Equal(0, _escapes);
    }

    // ── Round three: Shift+Space in a dialog is consumed and refused, for now ──

    /// <summary>
    /// The production classification of a button and a checkbox, so the
    /// refusal tests feed the door exactly what <c>OnPreviewKeyDown</c> would
    /// for those controls. Built on a throwaway STA with no PresentationSource;
    /// nothing is shown.
    /// </summary>
    private static (bool Button, bool CheckBox) FocusedControlKinds()
        => OnSta(() => (DialogTransmitKeys.IsEditableText(new Button()), DialogTransmitKeys.IsEditableText(new CheckBox())));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shift_space_on_a_button_or_a_checkbox_is_consumed_refused_out_loud_and_leaves_the_lock_where_it_was(bool startLocked)
    {
        // Noel, 2026-10-21 10:55: "wait for the bench". The dialog lock is
        // not built until a quick JAWS tap has been measured, so Shift+Space
        // in a dialog is taken (no button is pressed, no box is ticked) and
        // refused through the same path the Audio Check refusal uses: the
        // low tone and "You can't use the transmit lock here." at Chatty. The controller
        // does not move, from Idle or from Locked.
        AssertTheDeskIsSafe(); WireDoor();
        var (button, checkBox) = FocusedControlKinds();
        Assert.False(button);
        Assert.False(checkBox);
        if (startLocked) LockFromTheSystemWideKey();
        var from = startLocked ? Locked : Idle;
        var w = new FakeWindow();

        string onButton = SpokenAfter(() => Assert.True(Down(w, Keys.Space | Keys.Shift, text: button)));
        Assert.Equal("You can't use the transmit lock here.", onButton);     // the mutation control: let the verdict fall through unhandled, or drop the refusal, and this fails
        Assert.Equal(from, _controller.State);

        string onCheckBox = SpokenAfter(() => Assert.True(Down(w, Keys.Space | Keys.Shift, text: checkBox)));
        Assert.Equal("You can't use the transmit lock here.", onCheckBox);
        Assert.Equal(from, _controller.State);

        // A held key's repeats are consumed too, and quietly: one refusal
        // per press, never one per repeat.
        NothingSpokenDuring(() =>
        {
            for (int i = 0; i < 5; i++) Assert.True(Down(w, Keys.Space | Keys.Shift, repeat: true, text: button));
        });
        Assert.Equal(from, _controller.State);

        // The release of a refused press is nobody's release and nobody's toggle.
        Assert.False(DialogTransmitKeys.KeyUp(Keys.Space));
        Assert.Equal(from, _controller.State);
        Assert.Null(DialogTransmitKeys.HoldWindow);           // a refused lock never subscribed a window
        Assert.Equal(0, _downs);
    }

    [Fact]
    public void A_key_up_while_the_radio_is_locked_leaves_the_lock_alone()
    {
        AssertTheDeskIsSafe(); WireDoor();
        var w = new FakeWindow();
        Assert.True(Down(w, Keys.Space | Keys.Control));
        LockFromTheSystemWideKey();                           // latch the lock mid-hold, from the door that can
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
        Assert.Equal("You can't use push to talk here.", said);
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
        Assert.Equal("You can't use the transmit lock here.", said);
        Assert.Equal(Idle, _controller.State);
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
        // unguarded assignment would have dropped the guard. Round three:
        // the probe key is push to talk, because Shift+Space in a dialog is
        // now refused whether or not a check is running and would show nothing.
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
            Assert.True(Down(w, Keys.Space | Keys.Control));
            Assert.Equal(Idle, _controller.State);          // refused: the check owns the transmitter
            Assert.Equal(0, _downs);

            // The second workshop closes: the fixed handler clears the
            // pointer only when it is this workshop.
            if (ReferenceEquals(instance.GetValue(null), second)) instance.SetValue(null, null);
            Assert.True((bool)guard.GetValue(null)!, "the first workshop's session is still active and still guarded");
            Assert.True(Down(w, Keys.Space | Keys.Control));
            Assert.Equal(Idle, _controller.State);          // still refused
            Assert.Equal(0, _downs);

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
        // Shift+Space is refused before the radio is asked, so with the radio
        // off it says what it says everywhere else in a dialog.
        string lockSaid = SpokenAfter(() => Assert.True(Down(w, Keys.Space | Keys.Shift)));
        Assert.Equal("You can't use the transmit lock here.", lockSaid);
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

    // ── Round three, item 5: the bench measurement trace ──

    /// <summary>Everything written through <c>Trace.WriteLine</c> while attached; the sink the app would use is not open here.</summary>
    private sealed class CapturingListener : TraceListener
    {
        public readonly List<string> Lines = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { lock (Lines) Lines.Add(message ?? ""); }
    }

    [Fact]
    public void Each_space_key_event_in_a_dialog_is_timed_in_the_log_only_under_a_detailed_capture_and_the_keys_behave_the_same_either_way()
    {
        // The dialog lock waits on a bench session that needs every Space key
        // event and its timing in the diagnostic log. The line is gated the
        // way a detailed capture gates everything it adds: the trace switch at
        // Verbose, which StartDetailedCapture raises and StopDetailedCapture
        // restores. At the standing Normal level nothing is written; the keys
        // do exactly the same thing either way.
        AssertTheDeskIsSafe(); WireDoor();
        var listener = new CapturingListener();
        bool onBefore = Tracing.On;
        TraceLevel levelBefore = Tracing.TheSwitch.Level;
        Trace.Listeners.Add(listener);
        try
        {
            Tracing.On = true;
            var w = new FakeWindow();
            var timed = new Regex(@"KEYTIME space (down|up) mods=(?<mods>[A-Za-z, ]+) repeat=(True|False) tick=\d+ door=dialog$");

            // Positive control: the listener sees what the switch lets through.
            Tracing.TheSwitch.Level = TraceLevel.Info;
            Tracing.TraceLine("control line, standing level", TraceLevel.Info);
            Assert.Contains(listener.Lines, l => l.EndsWith("control line, standing level", StringComparison.Ordinal));

            // Standing level: a hold, a refused lock, nothing timed.
            Assert.True(Down(w, Keys.Space | Keys.Control));
            Assert.Equal(Hold, _controller.State);
            Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 20);
            Assert.True(DialogTransmitKeys.KeyUp(Keys.Space));
            Assert.Equal(Idle, _controller.State);
            Assert.True(Down(w, Keys.Space | Keys.Shift));
            Assert.Equal(Idle, _controller.State);
            Assert.False(DialogTransmitKeys.KeyUp(Keys.Space));
            Assert.DoesNotContain(listener.Lines, l => l.Contains("KEYTIME", StringComparison.Ordinal));   // the mutation control: write the line unconditionally and this fails

            // Under a detailed capture: the same presses, the same states, and
            // one line per Space event, down and up, with the modifiers at
            // that moment. Ctrl's own up is not a Space event and gets no line.
            listener.Lines.Clear();
            Tracing.TheSwitch.Level = TraceLevel.Verbose;
            Assert.True(Down(w, Keys.Space | Keys.Control));
            Assert.Equal(Hold, _controller.State);
            Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 20);
            Assert.False(DialogTransmitKeys.KeyUp(Keys.ControlKey));
            Assert.True(DialogTransmitKeys.KeyUp(Keys.Space));
            Assert.Equal(Idle, _controller.State);
            Assert.True(Down(w, Keys.Space | Keys.Shift));
            Assert.Equal(Idle, _controller.State);
            Assert.True(Down(w, Keys.Space | Keys.Shift, repeat: true));
            Assert.False(DialogTransmitKeys.KeyUp(Keys.Space | Keys.Shift));
            Assert.Equal(Idle, _controller.State);

            var lines = listener.Lines.Where(l => l.Contains("KEYTIME", StringComparison.Ordinal)).Select(l => timed.Match(l)).ToList();
            Assert.Equal(5, lines.Count);                 // the mutation control: drop the line and this fails
            Assert.All(lines, m => Assert.True(m.Success, "unexpected KEYTIME line shape"));
            Assert.Equal("Control", lines[0].Groups["mods"].Value); Assert.Contains("space down", lines[0].Value);
            Assert.Equal("None", lines[1].Groups["mods"].Value);    Assert.Contains("space up", lines[1].Value);
            Assert.Equal("Shift", lines[2].Groups["mods"].Value);   Assert.Contains("space down", lines[2].Value); Assert.Contains("repeat=False", lines[2].Value);
            Assert.Equal("Shift", lines[3].Groups["mods"].Value);   Assert.Contains("space down", lines[3].Value); Assert.Contains("repeat=True", lines[3].Value);
            Assert.Equal("Shift", lines[4].Groups["mods"].Value);   Assert.Contains("space up", lines[4].Value);
            Assert.Equal(2, _downs);                      // one hold per phase, each keyed
            Assert.Equal(4, _ups);                        // every Space up is offered to the edge: the hold's and the refused lock's, in each phase
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            Tracing.TheSwitch.Level = levelBefore;
            Tracing.On = onBefore;
        }
    }

    // ── Text controls ──

    [Fact]
    public void Shift_space_in_an_editable_text_control_is_left_to_the_text_box()
    {
        AssertTheDeskIsSafe(); WireDoor();
        NothingSpokenDuring(() => Assert.False(Down(new FakeWindow(), Keys.Space | Keys.Shift, text: true)));
        Assert.Equal(Idle, _controller.State);
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
        Assert.Equal(0, _downs);
        Assert.False(DialogTransmitKeys.KeyUp(Keys.Space));
    }

    [Fact]
    public void Cut_hold_release_audio_is_quiet_and_separated_from_the_optional_vox_on_pair()
    {
        AssertTheDeskIsSafe();
        // Offline sample rendering only: no mixer, device, window or radio.
        var releaseOnly = EarconPlayer.RenderCutHoldEnded(false, 200);
        var withVox = EarconPlayer.RenderCutHoldEnded(true, 200);
        int At(int ms) => EarconPlayer.MixerSampleRate * ms / 1000;
        Assert.All(withVox.Take(At(200)), sample => Assert.Equal(0f, sample));
        float releasePeak = withVox.Skip(At(200)).Take(At(105)).Max(Math.Abs);
        Assert.InRange(releasePeak, 0.20f, 0.30f); // well below the 0.65 alarm
        Assert.All(withVox.Skip(At(305)).Take(At(555) - At(305)),
            sample => Assert.Equal(0f, sample)); // 250 ms silence AFTER the fade tail
        Assert.InRange(withVox.Skip(At(555)).Max(Math.Abs), 0.45f, 0.55f);
        Assert.Equal(At(305) + 1, releaseOnly.Length);
        Assert.Equal(releaseOnly, withVox.Take(releaseOnly.Length));
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
