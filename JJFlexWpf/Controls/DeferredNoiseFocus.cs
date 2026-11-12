using System;

namespace JJFlexWpf.Controls;

/// <summary>
/// A focus request the noise fields make after opening (Sprint 49 Track E
/// follow-up E3, repair round 1): asked for once the fields are laid out,
/// tried once more when the app is idle, and CANCELLED the moment it is no
/// longer the operator's request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it needs an owner, not only a dispatcher priority</b> (Codex's
/// second finding on the track). Input outranks idle, so between the first
/// try and the retry the operator can press keys and move on; an unowned
/// retry would then pull focus back to a noise field they had left. So a
/// request is honoured only while all of these still hold, checked before
/// each try:
/// </para>
/// <list type="bullet">
/// <item>it is the newest request (a later one, or <see cref="Cancel"/>, retires it);</item>
/// <item>something had keyboard focus when the request was made (its owner),
/// and focus has not moved off it since, not even for a moment: a move away
/// and back, or the app losing focus and getting it again, cancels (repair
/// round 2; WPF clears keyboard focus when its window deactivates);</item>
/// <item>the fields still show the engine the request was made for;</item>
/// <item>the panel is still there to take focus: shown, expanded, connected.</item>
/// </list>
/// <para>
/// Every seam is injected so the real logic runs in a windowless test with
/// hand-cranked queues; <see cref="ScreenFieldsPanel"/> wires the dispatcher,
/// WPF's keyboard focus and the panel's own state.
/// </para>
/// </remarks>
internal sealed class DeferredNoiseFocus
{
    private readonly Action<Action> _afterLayout;
    private readonly Action<Action> _whenIdle;
    private readonly Func<object?> _focused;
    private readonly Func<string?> _shownEngine;
    private readonly Func<bool> _ready;
    private readonly Action<string> _trace;
    private int _generation;

    /// <summary>The element focus was on when the newest request was made: its owner.</summary>
    private object? _owner;

    /// <summary>
    /// Whether focus has been anywhere but the owner since the newest request
    /// (repair round 2). Endpoint equality is not evidence that focus never
    /// moved: Tab away then Shift+Tab back, or leaving the app and returning,
    /// ends where it began, and an old idle retry would then take focus.
    /// </summary>
    private bool _movedSinceRequest;

    /// <param name="afterLayout">Run an action once the panel has laid out (Loaded priority after a layout pass).</param>
    /// <param name="whenIdle">Run an action when the app is idle (ApplicationIdle priority).</param>
    /// <param name="focused">The element that has keyboard focus now, or null.</param>
    /// <param name="shownEngine">The engine the noise fields show now.</param>
    /// <param name="ready">Whether the panel can take focus now: shown, expanded, connected to a window.</param>
    /// <param name="trace">Where a cancelled or failed request is recorded.</param>
    /// <param name="watchFocus">
    /// Subscribes a callback to every keyboard focus change in the app, with the
    /// element that now has focus (null when the app lost it). Every move counts,
    /// not only where focus ends up.
    /// </param>
    public DeferredNoiseFocus(Action<Action> afterLayout, Action<Action> whenIdle, Func<object?> focused,
        Func<string?> shownEngine, Func<bool> ready, Action<string> trace, Action<Action<object?>> watchFocus)
    {
        _afterLayout = afterLayout; _whenIdle = whenIdle; _focused = focused;
        _shownEngine = shownEngine; _ready = ready; _trace = trace;
        watchFocus(FocusChanged);
    }

    /// <summary>Every focus change: a move to anything but the owner, nothing included, means the operator (or another window) has moved on.</summary>
    private void FocusChanged(object? now)
    {
        if (!ReferenceEquals(now, _owner)) _movedSinceRequest = true;
    }

    /// <summary>Retire any request still waiting: the panel was hidden, collapsed or unloaded.</summary>
    public void Cancel() => _generation++;

    /// <summary>
    /// Ask for focus on <paramref name="target"/> once laid out. The target is
    /// read at each try and returns the element's focus call, or null when
    /// there is nothing to focus; the call returns whether focus landed.
    /// </summary>
    public void Request(Func<Func<bool>?> target, string what)
    {
        int generation = ++_generation;
        object? focusedAtRequest = _focused();
        string? engineAtRequest = _shownEngine();
        _owner = focusedAtRequest;
        _movedSinceRequest = false;

        string? Stale()
        {
            if (generation != _generation) return "a later request or a cancel replaced it";
            // No element had focus when it was asked for: nobody owns the request,
            // so it may not take focus (the app may not even be the active window).
            if (focusedAtRequest == null) return "nothing had focus when it was asked for";
            if (_movedSinceRequest || !ReferenceEquals(_focused(), focusedAtRequest)) return "focus moved since it was asked for";
            if (!string.Equals(_shownEngine(), engineAtRequest, StringComparison.Ordinal)) return "the fields show another engine";
            if (!_ready()) return "the panel is not showing";
            return null;
        }

        bool Try(out string? why)
        {
            why = Stale();
            if (why != null) return false;
            return target()?.Invoke() == true;
        }

        _afterLayout(() =>
        {
            if (Try(out string? why))
            {
                if (generation == _generation) _generation++; // landed: nothing is left waiting
                return;
            }
            if (why != null) { _trace("OpenNoise: focus on " + what + " cancelled: " + why); return; }
            _whenIdle(() =>
            {
                bool landed = Try(out string? whyLater);
                if (generation == _generation) _generation++;
                _trace("OpenNoise: focus on " + what + (landed ? " landed on the second try"
                    : whyLater != null ? " cancelled: " + whyLater : " did not land"));
            });
        });
    }
}
