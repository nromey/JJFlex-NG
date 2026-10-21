using System.Windows;
using System.Windows.Controls;
using Keys = System.Windows.Forms.Keys;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #778c: Ctrl+Space and Shift+Space pressed in any WPF dialog are push to
/// talk and the transmit lock (Noel, 2026-10-21 06:30), and every way a hold
/// can end unkeys. Driven through the REAL <see cref="PttSafetyController"/>,
/// so every release assertion is on the controller's state, which is the
/// production gate for push to talk: a handler that was called and a radio
/// that is still keyed are different facts, and only the second one matters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Desk safety, checked against <c>Infrastructure/</c> rather than
/// assumed.</b> No window is constructed anywhere in this class. The door is
/// a static class, the controller is a plain object, and the hold window is a
/// fake that raises two events. The editable-text tests build a TextBox, a
/// PasswordBox, a Button and a CheckBox on a throwaway STA thread with no
/// PresentationSource, so nothing can appear on any desktop; a FrameworkElement
/// that is not in a Window is never shown. Nothing is audible: <see cref="QuietRun"/>
/// has render off before any type here loads, the controller is built with
/// chirps and speech off, and the first assertion of every test refuses to go
/// on if the run is not silent and isolated. No radio is touched: the rig is an
/// unconnected <see cref="FlexBase"/>, whose <c>Transmit</c> setter enqueues
/// onto a command queue that is not running (<c>q_t.Enqueue</c> traces
/// "outside main loop" and runs nothing), so <c>SetTx</c> reaches no radio.
/// </para>
/// <para>
/// <b>What this does NOT prove:</b> that MainWindow's <c>HandleDialogPttEdge</c>
/// and <c>HandleDialogLockToggle</c> are what the door's delegates point at,
/// and that they reach <c>PttKeyDownEdge</c> and <c>PttKeyUpEdge</c> with the
/// repeat guard and the JAWS absorber. MainWindow needs a window. Here the
/// door is wired to the controller's own <c>PttDown</c>, <c>PttUp</c> and
/// <c>ToggleLock</c>, which is where both production edges end. The wiring is
/// pinned from source in <c>Radios.Tests.ValueSubLayerTests</c>. Pressing the
/// keys in a real dialog is Noel's.
/// </para>
/// </remarks>
public sealed class DialogTransmitKeysTests : IDisposable
{
    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private int _downs, _ups, _locks;

    public DialogTransmitKeysTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(
            () => _rig,
            () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        DialogTransmitKeys.PttEdge = down =>
        {
            if (down) { _downs++; _controller.PttDown(); }
            else { _ups++; _controller.PttUp(); }
        };
        DialogTransmitKeys.ToggleLock = () => { _locks++; _controller.ToggleLock(); };
        DialogTransmitKeys.HoldInFlight = () => _controller.State == PttSafetyController.PttState.PttHold;
    }

    public void Dispose()
    {
        // Leave nothing keyed and nothing wired for the next test.
        DialogTransmitKeys.HoldWindowLost("test teardown");
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        DialogTransmitKeys.PttEdge = null;
        DialogTransmitKeys.ToggleLock = null;
        DialogTransmitKeys.HoldInFlight = null;
        _controller.Dispose();
        _rig.Dispose();
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

    // ── The decision ──

    [Theory]
    [InlineData(Keys.Space | Keys.Control, false, false, "PushToTalkDown")]
    [InlineData(Keys.Space | Keys.Control, true, false, "PushToTalkRepeat")]
    [InlineData(Keys.Space | Keys.Control, false, true, "PushToTalkDown")]   // Noel 06:42: Ctrl+Space types nothing, so it still keys
    [InlineData(Keys.Space | Keys.Shift, false, false, "LockToggle")]
    [InlineData(Keys.Space | Keys.Shift, false, true, "TypesASpace")]        // Noel 06:42: a text box keeps its space
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

    // ── Keying, and every way the hold ends, asserted on the controller ──

    [Fact]
    public void Ctrl_space_in_a_dialog_keys_the_controller_and_the_key_up_unkeys_it()
    {
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        Assert.Equal(Idle, _controller.State);

        Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Control, isRepeat: false, editableTextFocused: false));
        Assert.Equal(Hold, _controller.State);
        Assert.Same(w, DialogTransmitKeys.HoldWindow);

        // The release is handled (true) because a hold was in flight when it ran.
        Assert.True(DialogTransmitKeys.KeyUp(Keys.Space | Keys.Control));
        Assert.Equal(Idle, _controller.State);
        Assert.Null(DialogTransmitKeys.HoldWindow);
        Assert.Equal(1, _downs);
        Assert.Equal(1, _ups);
    }

    [Fact]
    public void Letting_go_of_ctrl_before_space_still_unkeys()
    {
        // The operator lifts Ctrl first, so the Space key-up arrives with no
        // modifiers. The release is by key code alone, as on Home.
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Control, false, false));
        Assert.Equal(Hold, _controller.State);

        Assert.True(DialogTransmitKeys.KeyUp(Keys.Space));
        Assert.Equal(Idle, _controller.State);
    }

    [Fact]
    public void Focus_moving_to_another_window_mid_hold_unkeys()
    {
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Control, false, false));
        Assert.Equal(Hold, _controller.State);

        w.LoseFocus();
        Assert.Equal(Idle, _controller.State);
        Assert.Null(DialogTransmitKeys.HoldWindow);

        // The key-up that arrives later, somewhere else, has nothing to unkey
        // and does not claim to have: not handled, controller untouched.
        Assert.False(DialogTransmitKeys.KeyUp(Keys.Space));
        Assert.Equal(Idle, _controller.State);
    }

    [Fact]
    public void The_dialog_closing_mid_hold_unkeys()
    {
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Control, false, false));
        Assert.Equal(Hold, _controller.State);

        w.Close();
        Assert.Equal(Idle, _controller.State);
        Assert.Null(DialogTransmitKeys.HoldWindow);
    }

    [Fact]
    public void A_held_repeat_is_consumed_and_keys_nothing_new()
    {
        // Ctrl+Space held: Windows repeats the key-down. Each repeat is
        // consumed so the focused button never sees it, and the edge is not
        // asked again, so the controller sees one down per press.
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Control, false, false));
        for (int i = 0; i < 5; i++)
            Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Control, isRepeat: true, editableTextFocused: false));
        Assert.Equal(Hold, _controller.State);
        Assert.Equal(1, _downs);

        Assert.True(DialogTransmitKeys.KeyUp(Keys.Space));
        Assert.Equal(Idle, _controller.State);
    }

    [Fact]
    public void A_second_down_from_another_window_during_the_hold_keys_once()
    {
        // The no-double-keying half this class can see: were two doors ever to
        // deliver the same press, the controller is already in the hold and
        // PttDown ignores a down in any transmitting state. (The system-wide
        // hook's door cannot even fire together with this one: its decider
        // swallows every push-to-talk edge before a window sees it, pinned in
        // Radios.Tests.SystemWideKeyDeciderTests.)
        AssertTheDeskIsSafe();
        var a = new FakeWindow();
        var b = new FakeWindow();
        Assert.True(DialogTransmitKeys.KeyDown(a, Keys.Space | Keys.Control, false, false));
        Assert.True(DialogTransmitKeys.KeyDown(b, Keys.Space | Keys.Control, false, false));
        Assert.Equal(Hold, _controller.State);
        Assert.Same(b, DialogTransmitKeys.HoldWindow);   // the newest window owns the hold

        // The first window going away no longer owns anything; the hold stands
        // until ITS window or the key-up releases it.
        a.Close();
        Assert.Equal(Hold, _controller.State);
        Assert.True(DialogTransmitKeys.KeyUp(Keys.Space));
        Assert.Equal(Idle, _controller.State);
    }

    [Fact]
    public void Shift_space_on_a_button_toggles_the_lock_and_never_presses_the_button()
    {
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Shift, false, editableTextFocused: false));
        Assert.Equal(Locked, _controller.State);
        Assert.Null(DialogTransmitKeys.HoldWindow);   // a lock is not a hold; no window owns it

        Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Shift, false, editableTextFocused: false));
        Assert.Equal(Idle, _controller.State);
        Assert.Equal(2, _locks);
    }

    [Fact]
    public void Shift_space_in_an_editable_text_control_is_left_to_the_text_box()
    {
        // Ruled by Noel 2026-10-21 06:42. The door declines, so WPF's
        // EditingCommands.ShiftSpace types the space as it always has, and the
        // lock is never asked.
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        Assert.False(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Shift, false, editableTextFocused: true));
        Assert.Equal(Idle, _controller.State);
        Assert.Equal(0, _locks);
    }

    [Fact]
    public void Ctrl_space_in_an_editable_text_control_still_keys()
    {
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Control, false, editableTextFocused: true));
        Assert.Equal(Hold, _controller.State);
        Assert.True(DialogTransmitKeys.KeyUp(Keys.Space));
        Assert.Equal(Idle, _controller.State);
    }

    [Fact]
    public void Plain_space_and_alt_space_are_not_taken()
    {
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        Assert.False(DialogTransmitKeys.KeyDown(w, Keys.Space, false, false));
        Assert.False(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Alt, false, false));
        Assert.Equal(Idle, _controller.State);
        Assert.Equal(0, _downs + _locks);
        // A Space key-up with no hold is not claimed either, so a button's
        // click on release still completes.
        Assert.False(DialogTransmitKeys.KeyUp(Keys.Space));
    }

    [Fact]
    public void A_key_up_while_the_radio_is_locked_leaves_the_lock_alone()
    {
        // Shift+Space mid-hold latches the lock (the controller's own rule);
        // the Space key-up that follows must not unlock it. PttUp only acts in
        // PttHold, and the door reports the release as not a hold release.
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Control, false, false));
        Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Shift, false, false));
        Assert.Equal(Locked, _controller.State);

        Assert.False(DialogTransmitKeys.KeyUp(Keys.Space));
        Assert.Equal(Locked, _controller.State);
    }

    // ── Which controls are editable text ──

    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { result = body(); } catch (Exception ex) { failure = ex; }
        });
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
        // An editable ComboBox focuses the TextBox inside its template, and a
        // template may put focus on a part inside the control; the walk up
        // finds the text control. A RichTextBox gives a real chain to walk
        // without a template: Run, Paragraph, FlowDocument, RichTextBox. A
        // read-only one is not editable text; a Button in a Grid never
        // reaches one.
        AssertTheDeskIsSafe();
        var (inside, insideReadOnly, besideInGrid) = OnSta(() =>
        {
            var run = new System.Windows.Documents.Run("x");
            var rich = new RichTextBox(new System.Windows.Documents.FlowDocument(
                new System.Windows.Documents.Paragraph(run)));
            var roRun = new System.Windows.Documents.Run("y");
            var readOnly = new RichTextBox(new System.Windows.Documents.FlowDocument(
                new System.Windows.Documents.Paragraph(roRun))) { IsReadOnly = true };
            var grid = new Grid();
            var button = new Button();
            grid.Children.Add(button);
            return (DialogTransmitKeys.IsEditableText(run),
                    DialogTransmitKeys.IsEditableText(roRun),
                    DialogTransmitKeys.IsEditableText(button));
        });
        Assert.True(inside);
        Assert.False(insideReadOnly);
        Assert.False(besideInGrid);
    }
}
