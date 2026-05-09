using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf.Modes;

// StickyAnnouncedMode — reusable scaffolding for the sticky-but-announced modal
// pattern (memory: project_sprint29_rit_xit_adjust_mode.md, three-category framework).
//
// Reach for this when:
//   1. The user is doing repeated micro-adjustments at a specific step / scale.
//   2. Entering and exiting the mode should be deliberate AND announced.
//   3. The mode is scoped to a specific focus or context (not global).
//
// Don't reach for it when the keystroke does one thing and stops — that's a
// plain handler, not a mode. Don't reach for it for held-modifier modals
// (Shift+Up = fine tune) — those don't have entry/exit; the modifier IS the
// state. The sticky pattern is the third category from the framework: you
// step in, you stay, you step out.
//
// Existing consumers:
//   * Filter-edge grab (FreqOutHandlers.cs HandleFilterAdjust)
//   * RIT/XIT scale-adjust (FreqOutHandlers.cs AdjustRITXIT)
//
// The helper owns: entry / exit announcements, entry / exit earcons, and the
// optional inactivity-timeout watchdog. The caller still owns the actual
// per-key handling — this isn't a key-binding framework, it's a state shell.
public sealed class StickyAnnouncedMode
{
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _inactivityTimeout;
    private readonly Action? _entryEarcon;
    private readonly Action? _exitEarcon;
    private CancellationTokenSource? _timeoutCts;

    public string Name { get; }

    public bool IsActive { get; private set; }

    // Optional callback fired when the inactivity timeout expires. Caller may
    // use this to clear its own per-mode state (e.g. which edge was grabbed).
    public Action? OnTimeout { get; set; }

    public StickyAnnouncedMode(
        string name,
        Dispatcher dispatcher,
        TimeSpan? inactivityTimeout = null,
        Action? entryEarcon = null,
        Action? exitEarcon = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _inactivityTimeout = inactivityTimeout ?? TimeSpan.Zero;
        _entryEarcon = entryEarcon;
        _exitEarcon = exitEarcon;
    }

    // Enter the mode. Plays the entry earcon, speaks the announcement, and
    // (if configured) starts the inactivity watchdog.
    public void Enter(string announcement, VerbosityLevel verbosity = VerbosityLevel.Terse)
    {
        IsActive = true;
        _entryEarcon?.Invoke();
        if (!string.IsNullOrEmpty(announcement))
            ScreenReaderOutput.Speak(announcement, verbosity, true);
        ResetInactivityTimer();
    }

    // Exit the mode. Plays the exit earcon and (if non-empty) speaks the
    // exit announcement. Idempotent: a no-op when not active.
    public void Exit(string? announcement = null, VerbosityLevel verbosity = VerbosityLevel.Terse, bool playEarcon = true)
    {
        if (!IsActive) return;
        IsActive = false;
        _timeoutCts?.Cancel();
        _timeoutCts = null;
        if (playEarcon) _exitEarcon?.Invoke();
        if (!string.IsNullOrEmpty(announcement))
            ScreenReaderOutput.Speak(announcement, verbosity, true);
    }

    // Restart the inactivity watchdog. Callers invoke this on every keystroke
    // that counts as "user is still working in the mode."
    public void ResetInactivityTimer()
    {
        if (!IsActive) return;
        if (_inactivityTimeout <= TimeSpan.Zero) return;

        _timeoutCts?.Cancel();
        _timeoutCts = new CancellationTokenSource();
        var token = _timeoutCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_inactivityTimeout, token);
                _dispatcher.Invoke(() =>
                {
                    if (!IsActive) return;
                    OnTimeout?.Invoke();
                    Exit();
                });
            }
            catch (OperationCanceledException) { }
        });
    }
}
