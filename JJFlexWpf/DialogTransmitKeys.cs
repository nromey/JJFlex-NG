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
/// The THIRD door into the push-to-talk pipeline (#778c): Ctrl+Space and
/// Shift+Space pressed in any WPF window that is not Home. Ruled by Noel,
/// 2026-10-21 06:30: <i>"To me, they should send to PTT, ctrl+space and
/// shift+space should never be used to activate a button in a dialog."</i>
/// Every dialog, a layer open or not.
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
/// and asks here first.
/// </para>
/// <para>
/// <b>One pipeline, three doors.</b> This class decides and then calls
/// MainWindow's <c>HandleDialogPttEdge</c> and <c>HandleDialogLockToggle</c>
/// through the two delegates below, exactly as <see cref="SystemWideKeys"/>
/// reaches <c>HandleSystemWidePttEdge</c>. Both land in <c>PttKeyDownEdge</c>
/// and <c>PttKeyUpEdge</c>, so the repeat guard, the JAWS absorber (#216) and
/// the safety controller are shared, not copied. Nothing here touches the
/// controller, the rig or RF.
/// </para>
/// <para>
/// <b>The release must always be seen.</b> A dialog that keys and misses the
/// release leaves the radio transmitting, so every way a hold can end is a
/// release here: the Space key-up arriving in the window, whatever the
/// modifiers are doing by then (Ctrl is often let go first); the keyed window
/// losing activation to another window; the keyed window closing. The last
/// two come through <see cref="IHoldWindow"/>, which is the window reduced to
/// those two events so a test can stand in for it without constructing one.
/// </para>
/// <para>
/// <b>Text boxes, ruled by Noel 2026-10-21 06:42.</b> In a focused editable
/// text control Shift+Space keeps typing a space (WPF binds
/// <c>EditingCommands.ShiftSpace</c> there, and a fast typist still has Shift
/// down from a capital), so the lock does NOT take it there. Ctrl+Space types
/// nothing in a TextBox or PasswordBox, so it still keys. The one WPF control
/// that binds Ctrl+Space itself is RichTextBox (<c>EditingCommands.ResetFormat</c>,
/// registered only where the editor accepts rich content), and this tree has
/// none; see the #778c report for the search.
/// </para>
/// <para>
/// <b>No double keying.</b> With the system-wide chord (#307) enabled the
/// low-level hook swallows every push-to-talk edge before any window sees it
/// (<c>SystemWideKeyDecider</c> returns <c>Swallow</c> true for PttDown and
/// PttUp), so a Ctrl+Space in our own dialog reaches the hook door only and
/// this one never fires. With it disabled, only this door fires. If both ever
/// did, <c>PttKeyDownEdge</c>'s <c>_pttKeyDown</c> guard makes the second
/// down a no-op.
/// </para>
/// </remarks>
internal static class DialogTransmitKeys
{
    /// <summary>What a Space chord pressed in a dialog is.</summary>
    internal enum Verdict
    {
        /// <summary>Not Ctrl+Space or Shift+Space, or a modifier set this door does not own. The control gets it.</summary>
        NotOurs,

        /// <summary>Ctrl+Space, first down: key the transmitter.</summary>
        PushToTalkDown,

        /// <summary>Ctrl+Space, a held repeat: nothing to key, but the control must not see it either.</summary>
        PushToTalkRepeat,

        /// <summary>Shift+Space on a non-text control: toggle the transmit lock.</summary>
        LockToggle,

        /// <summary>Shift+Space in an editable text control: it types a space, as it always has.</summary>
        TypesASpace,
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

    /// <summary>The transmit lock toggle, as Shift+Space does it on Home.</summary>
    internal static Action? ToggleLock { get; set; }

    /// <summary>Whether the controller is in a push-to-talk hold right now, read BEFORE a release runs.</summary>
    internal static Func<bool>? HoldInFlight { get; set; }

    // ── the decision ─────────────────────────────────────────────────────

    /// <summary>The pure decision: which chord, first down or repeat, and whether a text control has focus.</summary>
    internal static Verdict Decide(Keys chord, bool isRepeat, bool editableTextFocused)
    {
        if ((chord & Keys.KeyCode) != Keys.Space) return Verdict.NotOurs;
        Keys mods = chord & Keys.Modifiers;
        if (mods == Keys.Control) return isRepeat ? Verdict.PushToTalkRepeat : Verdict.PushToTalkDown;
        if (mods == Keys.Shift) return editableTextFocused ? Verdict.TypesASpace : Verdict.LockToggle;
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
        switch (Decide(chord, isRepeat, editableTextFocused))
        {
            case Verdict.PushToTalkDown:
                BeginHold(window);
                PttEdge?.Invoke(true);
                return true;

            case Verdict.PushToTalkRepeat:
                // The repeat guard lives in PttKeyDownEdge's caller on Home
                // (e.IsRepeat) and in the hook's decider; here too, so the
                // edge sees one down per press. Consumed so the held key does
                // not start pressing the focused button either.
                return true;

            case Verdict.LockToggle:
                ToggleLock?.Invoke();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// A key came up in any window. Space, whatever the modifiers now say, is
    /// a release: through the same edge Home's PreviewKeyUp uses, which unkeys
    /// a hold and otherwise only clears the repeat guard. Returns whether a
    /// hold was in flight, read BEFORE the edge ran, so the very key-up that
    /// unkeyed is marked handled and does not fall through to the control.
    /// </summary>
    internal static bool KeyUp(Keys code)
    {
        if ((code & Keys.KeyCode) != Keys.Space) return false;
        bool wasHold = HoldInFlight?.Invoke() == true;
        EndHold();
        PttEdge?.Invoke(false);
        return wasHold;
    }

    /// <summary>
    /// The keyed window lost the keyboard without a key-up we could see:
    /// focus moved to another window, or the window closed. Unkey through the
    /// same edge. Idempotent; nothing to do when no hold is tracked here.
    /// </summary>
    internal static void HoldWindowLost(string why)
    {
        if (_holdWindow == null) return;
        Tracing.TraceLine("PTT: the dialog that keyed push to talk " + why
            + " before the key came up; releasing through the pipeline", TraceLevel.Warning);
        EndHold();
        PttEdge?.Invoke(false);
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
        if ((chord & Keys.KeyCode) != Keys.Space) return false;
        var focused = (e.OriginalSource as DependencyObject)
                      ?? System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
        return KeyDown(WindowHold.For(window), chord, e.IsRepeat, IsEditableText(focused));
    }

    /// <summary>The preview key-up from the Window class handler. True when the release unkeyed a hold.</summary>
    internal static bool OnPreviewKeyUp(System.Windows.Input.KeyEventArgs e)
    {
        var raw = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (raw != System.Windows.Input.Key.Space) return false;
        return KeyUp(Keys.Space);
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
