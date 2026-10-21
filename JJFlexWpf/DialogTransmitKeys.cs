using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using JJTrace;
using Keys = System.Windows.Forms.Keys;

namespace JJFlexWpf;

/// <summary>
/// The THIRD door into the push-to-talk pipeline (#778c): Ctrl+Space,
/// Shift+Space and Escape-while-transmitting pressed in any WPF window that
/// is not Home. Ruled by Noel, 2026-10-21 06:30: <i>"To me, they should send
/// to PTT, ctrl+space and shift+space should never be used to activate a
/// button in a dialog."</i> Every dialog, a layer open or not. Ctrl+Space
/// keys the radio through the shared edges. Shift+Space is TAKEN here, so
/// it never presses a button or ticks a box, but it does not lock yet: it is
/// refused, until the dialog lock has been measured at the bench (repair
/// round three; Noel, 10:55: <i>"wait for the bench"</i>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the window has to take these keys before the control sees them.</b>
/// dotnet/wpf's <c>ButtonBase.OnKeyDown</c> presses a focused button or
/// checkbox on Space unless the held modifiers, masked to Ctrl and Alt, are
/// exactly Alt. So until this door existed Ctrl+Space and Shift+Space in a
/// dialog pressed whatever had focus, and a layer open in a dialog refused
/// them out loud instead. The class-level preview handler
/// (<c>KeyCommands.AnyWindowPreviewKeyDown</c>) runs ahead of every control
/// and asks here first. <c>MessageBox.Show</c> is NOT a WPF window (its
/// <c>ShowCore</c> calls the native API), so a native message box is outside
/// this door; the help says so.
/// </para>
/// <para>
/// <b>One pipeline, three doors.</b> This class decides and then calls
/// MainWindow through the delegates below, exactly as <see cref="SystemWideKeys"/>
/// reaches <c>HandleSystemWidePttEdge</c>. Push to talk lands in
/// <c>PttKeyDownEdge</c> and <c>PttKeyUpEdge</c>; Escape in the controller's
/// <c>EscapeUnlock</c>, as Home's step 3 does. So the repeat guard, the JAWS
/// absorber (#216) and the safety controller are shared, not copied. Nothing
/// here touches the controller, the rig or RF.
/// </para>
/// <para>
/// <b>Shift+Space is refused, for now (repair round three).</b> Rounds one
/// and two tried to lock from a dialog with a held key toggling once under
/// both screen readers; round two decided a quick second press at its
/// key-up, and Codex showed (B5) that such a press stayed pending through
/// Escape at Idle, a close or a window switch, and a later unrelated Space
/// release locked the transmitter. Noel chose to wait for a measured JAWS
/// tap rather than guess. So a first Shift+Space on a non-text control is
/// consumed and refused through the one refusal path this door has, the
/// same one the Audio Check refusal uses: the low tone, and "No transmit
/// lock here." at Chatty, or at every level when the tone cannot sound. Its
/// repeats are consumed quietly. The lock still works from Home and from the
/// system-wide chord; the dialog lock returns as its own track once the tap
/// is measured. Escape in a dialog while the transmitter is locked from
/// either of those still unkeys first (Noel 07:37, kept at 10:55).
/// </para>
/// <para>
/// <b>Escape unkeys first (repair round one, B1; ruled by Noel 2026-10-21
/// 07:37).</b> While the controller is transmitting, Escape in a dialog
/// unkeys and is consumed; the dialog stays open and a second Escape closes
/// it, matching Home's step 3. An Audio Check session keeps its own two-stage
/// Escape: while one is active this door declines Escape and
/// <c>AudioWorkshopDialog.OnPreviewKeyDown</c> takes it.
/// </para>
/// <para>
/// <b>An Audio Check owns the transmitter (B3; ruled by Noel 2026-10-21
/// 07:38).</b> While a session is active, in ANY phase including the
/// post-unkey wait, Ctrl+Space is refused here with the ordinary refusal:
/// the low tone, and the layer's very short sentence at Chatty, or at every
/// level when the tone cannot sound. Keying under a session bypassed its
/// power setup and could leave a lock the session's own stop no longer
/// answered. Shift+Space is refused the same way whether or not a session
/// is active.
/// </para>
/// <para>
/// <b>The release must always be seen (F2).</b> Every way a hold can end is a
/// release here: the Space key-up arriving in the window, whatever the
/// modifiers are doing by then; the keyed window losing activation; the keyed
/// window closing. A key-up goes through the ordinary edge, which may defer
/// it while a screen reader's synthetic pairs are being absorbed. Window loss
/// is not a key-up: it goes through <see cref="ReleaseNow"/>, which drops the
/// deferral and unkeys immediately. The window stays subscribed until the
/// controller has actually left the hold, not merely until the first key-up;
/// when a deferred release finally runs, MainWindow's timer calls
/// <see cref="HoldReleased"/> and the subscriptions go (repair round two,
/// Codex F2). And <see cref="ReleaseNow"/> acts only when the hold in flight
/// is this door's: a hold Home or the system-wide chord keyed after the
/// dialog's ended is not the dialog's to release.
/// </para>
/// <para>
/// <b>Text boxes, ruled by Noel 2026-10-21 06:42.</b> In a focused editable
/// text control Shift+Space keeps typing a space (WPF binds
/// <c>EditingCommands.ShiftSpace</c> there, and a fast typist still has Shift
/// down from a capital), so this door does not take it there. Ctrl+Space
/// types nothing in a TextBox or PasswordBox, so it still keys. With a value
/// layer open over the text box the layer wins and refuses Shift+Space like
/// any key it does not take (Noel, 06:34: "if the layer's open, the layer is
/// open"). The one WPF control that binds Ctrl+Space itself is RichTextBox
/// (<c>EditingCommands.ResetFormat</c>), and this tree has none.
/// </para>
/// <para>
/// <b>The system-wide chord does not take Ctrl+Space away from a dialog.</b>
/// Its defaults are Ctrl+Alt+Space for push to talk and Alt+Shift+Space for
/// the lock, and a valid global chord needs two modifiers, so with the hook
/// on at its defaults plain Ctrl+Space still reaches this door and keys once;
/// the hook swallows only the chords it is configured for. A press can only
/// reach both doors if an operator configures the hook onto plain Ctrl+Space,
/// and then <c>PttKeyDownEdge</c>'s <c>_pttKeyDown</c> guard and the
/// controller's own idempotence make the second down a no-op.
/// </para>
/// <para>
/// <b>The measurement trace (round three, item 5).</b> Every Space key down
/// and up that reaches this door is timed into the diagnostic log through
/// <see cref="SpaceKeyTimingTrace"/>, under a detailed capture only, so the
/// bench session the dialog lock waits on can read how the reader delivers
/// a quick tap. It is written before the verdict and has no effect on it.
/// </para>
/// </remarks>
internal static class DialogTransmitKeys
{
    /// <summary>What a key pressed in a dialog is to this door.</summary>
    internal enum Verdict
    {
        /// <summary>Not a transmit key, or a modifier set this door does not own. The control gets it.</summary>
        NotOurs,

        /// <summary>Ctrl+Space, first down: key the transmitter.</summary>
        PushToTalkDown,

        /// <summary>Ctrl+Space, a held repeat: nothing to key, but the control must not see it either.</summary>
        PushToTalkRepeat,

        /// <summary>Shift+Space on a non-text control, first down: consumed and refused out loud. The dialog lock waits for the bench (round three).</summary>
        LockRefused,

        /// <summary>Shift+Space, a held repeat: refused already; consumed quietly so the control never sees it.</summary>
        LockRepeat,

        /// <summary>Shift+Space in an editable text control: it types a space, as it always has.</summary>
        TypesASpace,

        /// <summary>Escape: unkeys if the controller is transmitting, otherwise not ours.</summary>
        Escape,
    }

    /// <summary>
    /// The window a hold was keyed from, reduced to the two ways it can take
    /// the keyboard away mid-hold. <see cref="Window"/> is adapted onto this;
    /// a test raises the events itself.
    /// </summary>
    internal interface IHoldWindow
    {
        event EventHandler? Deactivated;
        event EventHandler? Closed;
    }

    // ── what MainWindow wires, beside the system-wide hooks ──────────────

    /// <summary>A push-to-talk edge, true for down and false for up, into MainWindow's pipeline.</summary>
    internal static Action<bool>? PttEdge { get; set; }

    /// <summary>Whether the controller is in a push-to-talk hold right now, read BEFORE a release runs.</summary>
    internal static Func<bool>? HoldInFlight { get; set; }

    /// <summary>Escape while transmitting: unkey through the controller, as Home's step 3 does. True when it unkeyed.</summary>
    internal static Func<bool>? EscapeUnkey { get; set; }

    /// <summary>The keyed window is gone or lost focus: unkey NOW, dropping any deferred release.</summary>
    internal static Action? ReleaseNow { get; set; }

    /// <summary>An Audio Check session is active, in any phase, and owns the transmitter.</summary>
    internal static Func<bool>? SessionOwnsTransmitter { get; set; }

    // ── the decision ─────────────────────────────────────────────────────

    /// <summary>The pure decision: which chord, first down or repeat, and whether a text control has focus.</summary>
    internal static Verdict Decide(Keys chord, bool isRepeat, bool editableTextFocused)
    {
        Keys code = chord & Keys.KeyCode;
        Keys mods = chord & Keys.Modifiers;
        if (code == Keys.Escape) return Verdict.Escape;
        if (code != Keys.Space) return Verdict.NotOurs;
        if (mods == Keys.Control) return isRepeat ? Verdict.PushToTalkRepeat : Verdict.PushToTalkDown;
        if (mods == Keys.Shift)
        {
            if (editableTextFocused) return Verdict.TypesASpace;
            return isRepeat ? Verdict.LockRepeat : Verdict.LockRefused;
        }
        return Verdict.NotOurs;
    }

    /// <summary>
    /// Is the focused element, or anything it sits inside, an editable text
    /// control: a TextBox or RichTextBox that is not read-only, or a
    /// PasswordBox. Walks up because an editable ComboBox focuses the TextBox
    /// inside its template, and a template part may be the focused element.
    /// </summary>
    internal static bool IsEditableText(DependencyObject? focused)
    {
        for (var d = focused; d != null; d = ParentOf(d))
        {
            switch (d)
            {
                case TextBoxBase t: return !t.IsReadOnly;
                case PasswordBox: return true;
                case Window: return false;
            }
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject d)
    {
        if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
        {
            var v = VisualTreeHelper.GetParent(d);
            if (v != null) return v;
        }
        return LogicalTreeHelper.GetParent(d);
    }

    // ── the hold, and every way it ends ──────────────────────────────────

    private static IHoldWindow? _holdWindow;

    /// <summary>The window holding a push-to-talk keyed through this door, or null.</summary>
    internal static IHoldWindow? HoldWindow => _holdWindow;

    /// <summary>
    /// A key went down in <paramref name="window"/>. True when this door took
    /// it, so the caller marks the event handled and no control sees it.
    /// </summary>
    internal static bool KeyDown(IHoldWindow window, Keys chord, bool isRepeat, bool editableTextFocused)
    {
        if ((chord & Keys.KeyCode) == Keys.Space)
            SpaceKeyTimingTrace.Note(down: true, chord, isRepeat, SpaceKeyTimingTrace.DialogDoor);

        var verdict = Decide(chord, isRepeat, editableTextFocused);
        if (verdict == Verdict.NotOurs || verdict == Verdict.TypesASpace) return false;

        if (verdict == Verdict.Escape)
        {
            // An active Audio Check keeps its own two-stage Escape; the
            // workshop's handler runs after this one and must see the key.
            if (SessionOwnsTransmitter?.Invoke() == true) return false;
            if (EscapeUnkey?.Invoke() != true) return false;
            EndHold();
            return true;
        }

        switch (verdict)
        {
            case Verdict.LockRefused:
                // Round three: taken, so no button is pressed and no box is
                // ticked; refused, so the operator hears why nothing locked.
                // The dialog lock waits for the bench.
                RefuseTransmitKey("audio.value_layer.lock_not_here");
                return true;

            case Verdict.LockRepeat:
            case Verdict.PushToTalkRepeat:
                // One refusal or one down per press; the held key does not
                // start pressing the focused button either.
                return true;
        }

        if (SessionOwnsTransmitter?.Invoke() == true)
        {
            // Refused, not ignored: consumed so no button is pressed, and the
            // operator hears why nothing keyed.
            RefuseTransmitKey("audio.value_layer.ptt_not_here");
            return true;
        }

        // Verdict.PushToTalkDown, the only one left.
        BeginHold(window);
        PttEdge?.Invoke(true);
        return true;
    }

    /// <summary>
    /// A key came up in any window. Space, whatever the modifiers now say, is
    /// a release through the same edge Home's PreviewKeyUp uses, which unkeys
    /// a hold (or defers it while synthetic pairs are being absorbed) and
    /// otherwise only clears the repeat guard. Returns whether a hold was in
    /// flight, read BEFORE the edge ran, so the very key-up that unkeyed is
    /// marked handled and does not fall through to the control. The window
    /// stays subscribed while a deferred release is still pending. The
    /// modifiers in <paramref name="chord"/> and <paramref name="isRepeat"/>
    /// are only recorded by the measurement trace.
    /// </summary>
    internal static bool KeyUp(Keys chord, bool isRepeat = false)
    {
        if ((chord & Keys.KeyCode) != Keys.Space) return false;
        SpaceKeyTimingTrace.Note(down: false, chord, isRepeat, SpaceKeyTimingTrace.DialogDoor);
        bool wasHold = HoldInFlight?.Invoke() == true;
        PttEdge?.Invoke(false);
        if (HoldInFlight?.Invoke() != true) EndHold();
        return wasHold;
    }

    /// <summary>
    /// The keyed window lost the keyboard without a key-up we could see:
    /// focus moved to another window, or the window closed. Window loss is
    /// not a key-up, so it does not go through the deferring edge; it unkeys
    /// at once. Idempotent; nothing to do when no hold is tracked here.
    /// Whether the hold in flight is really this door's is MainWindow's
    /// question, answered inside <see cref="ReleaseNow"/>.
    /// </summary>
    internal static void HoldWindowLost(string why)
    {
        if (_holdWindow == null) return;
        EndHold();
        if (HoldInFlight?.Invoke() != true) return;
        Tracing.TraceLine("PTT: the dialog that keyed push to talk " + why
            + " before the key came up; releasing now, ahead of any deferred release", TraceLevel.Warning);
        if (ReleaseNow != null) ReleaseNow();
        else PttEdge?.Invoke(false);
    }

    /// <summary>
    /// The hold ended somewhere other than a key-up this door saw: the
    /// deferred release ran to ground in MainWindow's timer. The dialog that
    /// keyed it has nothing left to release, so it stops listening. Idempotent;
    /// nothing to do when no hold is tracked here.
    /// </summary>
    internal static void HoldReleased() => EndHold();

    /// <summary>
    /// The one refusal path this door has: the low tone, and the lexicon's
    /// very short sentence at Chatty, or at every level when the tone cannot
    /// sound (<c>RefusalVoice.ToneStandsAlone</c>, the same rule as the value
    /// layers'). Shift+Space in a dialog (round three) and Ctrl+Space under
    /// an active Audio Check (B3) both come here.
    /// </summary>
    private static void RefuseTransmitKey(string lexiconKey)
    {
        bool toneWillSound = EarconPlayer.IsOn(EarconPlayer.EarconCategory.CommandsAndConfirmations);
        EarconPlayer.LeaderInvalidTone();
        if (Radios.Speech.RefusalVoice.ToneStandsAlone(Radios.ScreenReaderOutput.CurrentVerbosity, toneWillSound)) return;
        Radios.ScreenReaderOutput.SpeakYieldingToSafety(
            Radios.Lexicon.Get(lexiconKey),
            Radios.VerbosityLevel.Critical,
            subject: Radios.Speech.SpeechSubject.TransmitKeyRefused);
    }

    private static void BeginHold(IHoldWindow window)
    {
        if (ReferenceEquals(_holdWindow, window)) return;
        EndHold();
        _holdWindow = window;
        window.Deactivated += OnHoldWindowDeactivated;
        window.Closed += OnHoldWindowClosed;
    }

    private static void EndHold()
    {
        var w = _holdWindow;
        if (w == null) return;
        _holdWindow = null;
        w.Deactivated -= OnHoldWindowDeactivated;
        w.Closed -= OnHoldWindowClosed;
    }

    private static void OnHoldWindowDeactivated(object? sender, EventArgs e) => HoldWindowLost("lost focus to another window");
    private static void OnHoldWindowClosed(object? sender, EventArgs e) => HoldWindowLost("closed");

    // ── the WPF face ─────────────────────────────────────────────────────

    /// <summary>
    /// The preview key-down from the Window class handler. True when taken.
    /// Alt chords arrive as <c>Key.System</c>; the converter unwraps them and
    /// <see cref="Decide"/> then declines any Alt form, so Alt+Space still
    /// opens the system menu.
    /// </summary>
    internal static bool OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not Window window) return false;
        var chord = WpfKeyConverter.ToWinFormsKeys(e);
        Keys code = chord & Keys.KeyCode;
        if (code != Keys.Space && code != Keys.Escape) return false;
        var focused = (e.OriginalSource as DependencyObject)
                      ?? System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
        return KeyDown(WindowHold.For(window), chord, e.IsRepeat, IsEditableText(focused));
    }

    /// <summary>The preview key-up from the Window class handler. True when the release unkeyed a hold.</summary>
    internal static bool OnPreviewKeyUp(System.Windows.Input.KeyEventArgs e)
    {
        var raw = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (raw != System.Windows.Input.Key.Space) return false;
        return KeyUp(WpfKeyConverter.ToWinFormsKeys(e), e.IsRepeat);
    }

    /// <summary>
    /// A <see cref="Window"/> seen as an <see cref="IHoldWindow"/>. One per
    /// window, so BeginHold's identity check holds across presses.
    /// </summary>
    private sealed class WindowHold : IHoldWindow
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, WindowHold> Holds = new();

        internal static WindowHold For(Window w) => Holds.GetValue(w, static w => new WindowHold(w));

        private WindowHold(Window w)
        {
            w.Deactivated += (s, e) => Deactivated?.Invoke(this, e);
            w.Closed += (s, e) => Closed?.Invoke(this, e);
        }

        public event EventHandler? Deactivated;
        public event EventHandler? Closed;
    }
}
