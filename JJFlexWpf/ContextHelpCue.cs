using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace JJFlexWpf;

/// <summary>
/// The context-help availability cue (#275): two quick rising taps, a moment
/// after focus settles on a control, meaning "Ctrl+F1 has something to say
/// here that it has not already said". The WPF half — focus watching and the
/// settle timer; the decision itself is Radios.ContextHelpCueDecider and the
/// sound is EarconPlayer.ContextHelpAvailableTone.
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
    /// How long focus must rest before the cue may sound. 1.5 seconds, per
    /// Noel's proposal — long enough that moving between controls stays
    /// silent and the focus announcement has gone first.
    /// </summary>
    internal const int SettleMs = 1500;

    private static bool _installed;
    private static readonly Radios.ContextHelpCueDecider Decider = new();
    private static DispatcherTimer? _settle;
    private static WeakReference<DependencyObject>? _pending;
    private static WeakReference<IInputElement>? _lastSeen;

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

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
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
            if (_pending == null || !_pending.TryGetTarget(out var element)) return;

            // Only cue the control the operator is STILL on. Focus that moved
            // to a WinForms surface leaves WPF's FocusedElement null, which
            // also lands here as silence.
            if (!ReferenceEquals(Keyboard.FocusedElement, element)) return;

            string? help = JJFlexHelp.FindExplanation(element);
            if (Decider.ShouldCue(help, DateTime.UtcNow))
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
