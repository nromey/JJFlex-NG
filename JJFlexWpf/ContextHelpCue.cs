using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace JJFlexWpf;

/// <summary>
/// The context-help availability cue (#275): a flick and a soft note an octave
/// above it, a moment after focus settles on a control, meaning "Ctrl+F1 has
/// something to say here that it has not already said". The WPF half — focus
/// watching, the settle timer and the shutdown latch; the decision itself is
/// Radios.ContextHelpCueDecider and the sound is
/// EarconPlayer.ContextHelpAvailableTone.
///
/// TIMING. The tone fires only after focus has RESTED on one control for
/// <see cref="SettleMs"/> — Noel's own proposed delay. Someone tabbing
/// through controls restarts the clock at every stop and never hears it at
/// all, and when it does fire it lands well BEHIND the screen reader's focus
/// announcement rather than before or under it. Controls that speak on focus
/// already cut off group announcements (#69); this cue must not join that
/// pile, and a delay is the only honest way to sequence against a screen
/// reader whose speech we cannot observe.
///
/// WHAT IT PROMISES. The cue resolves the SAME walk Ctrl+F1 resolves
/// (JJFlexHelp.FindExplanation), so a tone is a true statement that pressing
/// Ctrl+F1 right now would say something new. Content the operator has
/// already been cued for — or has just heard Ctrl+F1 read — stays silent.
///
/// COVERAGE, stated honestly: this watches WPF keyboard focus. The main
/// surface (a UserControl inside a WinForms ElementHost) and every WPF
/// dialog raise it; legacy WinForms-hosted surfaces do not, the same gap the
/// global key routing documents.
/// </summary>
public static class ContextHelpCue
{
    /// <summary>
    /// How long focus must rest before the cue may sound — long enough that
    /// moving between controls stays silent and the focus announcement has
    /// gone first.
    /// </summary>
    /// <remarks>
    /// 1500 was Noel's original proposal and it was too eager in the field
    /// (2026-08-27): the cue fired while he was still moving. 2500 has NOT
    /// been auditioned at the time of writing — if it is still wrong, this
    /// constant is the whole of the fix.
    /// </remarks>
    internal const int SettleMs = 2500;

    private static bool _installed;
    private static readonly Radios.ContextHelpCueDecider Decider = new();
    private static DispatcherTimer? _settle;
    private static WeakReference<DependencyObject>? _pending;
    private static WeakReference<IInputElement>? _lastSeen;
    private static bool _shuttingDown;

    /// <summary>
    /// Register the focus watcher. Call once from the WPF surface's
    /// construction; further calls are no-ops.
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        // Class handler at UIElement level, because the main surface has no
        // WPF Window above it (UserControl in an ElementHost) — a Window
        // class handler would cover dialogs only. The event bubbles through
        // every ancestor, so the handler runs several times per focus change;
        // the _lastSeen check makes the repeats free.
        EventManager.RegisterClassHandler(
            typeof(UIElement),
            Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus),
            handledEventsToo: true);
    }

    /// <summary>
    /// The application is on its way out — disarm, and stay disarmed.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists (#275).</b> Noel heard a tone with no speech
    /// just before exiting on 2026-08-27 and could not place it; once he had
    /// identified this cue, the shape matched. It is not a coincidence and it
    /// is not about the sound. Exiting puts a confirmation in front of the
    /// operator, focus lands on it, and reading a prompt takes longer than the
    /// settle interval — so the cue resolves and announces that help is
    /// available on a surface that is about to cease to exist. Every axis of
    /// the design is right and the statement is still false.</para>
    /// <para>Called at the TOP of MainWindow.RequestShutdown, before the exit
    /// sequence runs, because the exit prompt is inside that sequence and is
    /// the most likely thing focus was resting on.</para>
    /// <para>Shutdown can be declined, so this is reversible — see
    /// <see cref="ResumeAfterCancelledShutdown"/>. A latch that could not be
    /// released would silently kill the cue for the rest of a session in which
    /// the operator changed their mind about leaving.</para>
    /// </remarks>
    public static void SuspendForShutdown()
    {
        _shuttingDown = true;
        _settle?.Stop();
        _pending = null;
        JJTrace.Tracing.TraceLine(
            "ContextHelpCue: disarmed for shutdown",
            System.Diagnostics.TraceLevel.Verbose);
    }

    /// <summary>
    /// The operator declined to exit. Re-arm.
    /// </summary>
    public static void ResumeAfterCancelledShutdown()
    {
        if (!_shuttingDown) return;
        _shuttingDown = false;
        JJTrace.Tracing.TraceLine(
            "ContextHelpCue: re-armed, shutdown was cancelled",
            System.Diagnostics.TraceLevel.Verbose);
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_shuttingDown) return;

        var focused = e.NewFocus;
        if (focused == null) return;

        // The event bubbles; only the first sighting of this focus change
        // matters.
        if (_lastSeen != null && _lastSeen.TryGetTarget(out var seen)
            && ReferenceEquals(seen, focused))
            return;
        _lastSeen = new WeakReference<IInputElement>(focused);

        if (focused is not DependencyObject d) return;
        _pending = new WeakReference<DependencyObject>(d);

        if (_settle == null)
        {
            _settle = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(SettleMs),
            };
            _settle.Tick += OnSettled;
        }
        // Restart: a move within the window cancels the previous pending cue.
        _settle.Stop();
        _settle.Start();
    }

    private static void OnSettled(object? sender, EventArgs e)
    {
        _settle?.Stop();
        try
        {
            if (_shuttingDown) return;
            if (_pending == null || !_pending.TryGetTarget(out var element)) return;

            // Only cue the control the operator is STILL on. Focus that moved
            // to a WinForms surface leaves WPF's FocusedElement null, which
            // also lands here as silence.
            if (!ReferenceEquals(Keyboard.FocusedElement, element)) return;

            // Still attached to a live window? A surface being torn down keeps
            // its element references — and its keyboard focus — for a while
            // after it stops being anywhere an operator can act. This is the
            // second line behind SuspendForShutdown, and it is here because
            // that latch depends on one caller remembering to call it, while
            // this holds for any teardown path anyone adds later.
            if (PresentationSource.FromDependencyObject(element) == null)
            {
                JJTrace.Tracing.TraceLine(
                    "ContextHelpCue: settled on a control with no live window — silent",
                    System.Diagnostics.TraceLevel.Verbose);
                return;
            }

            string? help = JJFlexHelp.FindExplanation(element);
            bool cue = Decider.ShouldCue(help, DateTime.UtcNow);

            // Verbose, so it lands in a DETAILED capture and nowhere else.
            // Before this there was NO trace on the earcon path at any level —
            // EarconPlayer records which earcon it played exactly never — so a
            // quiet log said nothing whatsoever about whether this cue fired,
            // and a 7,998-line Info trace containing no earcon lines was read
            // on 2026-08-27 as if it were evidence. It was not. This one line
            // is what makes the next audition settleable from a capture.
            JJTrace.Tracing.TraceLine(
                "ContextHelpCue: settled on " + element.GetType().Name
                    + (cue ? " — sounding" : " — silent (no new help)"),
                System.Diagnostics.TraceLevel.Verbose);

            if (cue)
                EarconPlayer.ContextHelpAvailableTone();
        }
        catch (Exception ex)
        {
            // A cue that can crash focus handling is worse than no cue.
            JJTrace.Tracing.TraceLine(
                "ContextHelpCue: settle failed: " + ex.Message,
                System.Diagnostics.TraceLevel.Warning);
        }
    }

    /// <summary>
    /// Ctrl+F1 just read this content aloud — cueing its availability now
    /// would announce what the operator already has. Cancels any pending
    /// settle and records the content as heard.
    /// </summary>
    internal static void NoteSpoken(string? content)
    {
        _settle?.Stop();
        Decider.NoteSpoken(content);
    }
}
