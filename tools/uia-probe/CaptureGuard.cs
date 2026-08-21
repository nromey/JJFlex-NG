namespace JJFlex.UiaProbe;

/// <summary>
/// Turns the app's detailed capture back OFF when — and only when — this sweep
/// turned it ON (#173).
///
/// <para><b>Why this exists.</b> The sweep needs the detailed capture because
/// the Home field keys speak rather than reaching the dispatcher, so it presses
/// Ctrl+J, Ctrl+D when no capture is running. Until 2026-08-21 it then walked
/// away, leaving the firehose open: a verbose capture with a radio connected
/// costs roughly 1 MB per minute, and one morning's forgotten captures archived
/// as 72.6 MB and 50.0 MB sessions — the operator's ran about 75 minutes before
/// anyone noticed. This is the same category of mess as a stuck Alt key, slower
/// and measured in gigabytes, so it gets the same treatment
/// <see cref="Native.ReleaseAllModifiers"/> gets in Program.cs: restore in a
/// finally, and again from ProcessExit and Ctrl+C.</para>
///
/// <para><b>What it never does.</b> A capture that was already running when the
/// sweep started was started by the operator for the operator's reasons; the
/// sweep declines to press the toggle then (that logic is in Sweep.Run), never
/// arms this guard, and the capture is left exactly as found.</para>
/// </summary>
internal static class CaptureGuard
{
    private static readonly object Gate = new();
    private static bool _armed;
    private static int _pid;
    private static WindowInfo? _window;
    private static string? _appDir;
    private static int _instance;

    /// <summary>
    /// Called immediately BEFORE the sweep presses the capture toggle — before,
    /// not after confirmation, so a crash or Ctrl+C in the confirmation window
    /// still restores. From this moment the guard owns turning the capture off.
    /// </summary>
    public static void Arm(int pid, WindowInfo window, string appDir, int instance)
    {
        lock (Gate)
        {
            _armed = true;
            _pid = pid;
            _window = window;
            _appDir = appDir;
            _instance = instance;
        }
    }

    /// <summary>
    /// Called when the sweep learns its press went the WRONG direction — a new
    /// session opened without a capture. Nothing of ours is running, and a later
    /// blind press could stop a capture the operator restarts by hand, which is
    /// the exact harm this class exists to avoid inflicting.
    /// </summary>
    public static void Disarm()
    {
        lock (Gate) _armed = false;
    }

    /// <summary>
    /// Restore the capture to OFF if the guard is armed. Idempotent: the first
    /// caller does the work, everyone after gets a no-op — Sweep.Run's finally,
    /// ProcessExit and Ctrl+C can all call it without double-pressing the toggle.
    ///
    /// <para><paramref name="note"/> receives the outcome for the sweep report
    /// when there still is one; failures ALWAYS go to stderr as well, because a
    /// silent failure to restore is the same defect class as the leak itself.</para>
    /// </summary>
    public static void RestoreIfArmed(Action<string>? note)
    {
        lock (Gate)
        {
            if (!_armed) return;
            _armed = false;   // one attempt, ever — never double-press the toggle

            try
            {
                Restore(note);
            }
            catch (Exception ex)
            {
                Loud(note, "RESTORE FAILED with an exception, and the detailed capture MAY STILL BE RUNNING "
                    + $"at roughly 1 MB per minute: {ex.Message}. The sweep started it, so it is not the "
                    + "operator's. Press Ctrl+J, Ctrl+D once to stop it; the text lands in Saved "
                    + "Diagnostic Logs.");
            }
        }
    }

    private static void Restore(Action<string>? note)
    {
        // Re-read the state rather than trusting the arm-time belief: the app
        // may have exited, the operator may have stopped the capture, or the
        // sweep's own press may never have taken effect.
        if (_appDir == null) return;

        // App gone means the capture died with its session — nothing to press,
        // and pressing would type into whatever now owns the foreground.
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(_pid);
            if (p.HasExited)
            {
                note?.Invoke("The app exited before the capture could be restored; the capture ended with it.");
                return;
            }
        }
        catch (ArgumentException)
        {
            note?.Invoke("The app exited before the capture could be restored; the capture ended with it.");
            return;
        }

        (TraceLog.CaptureState state, bool markerKnown) = ReadState();
        if (state == TraceLog.CaptureState.Off)
        {
            note?.Invoke("The detailed capture this sweep started already reads as off — nothing was pressed "
                + "at exit.");
            return;
        }
        if (state == TraceLog.CaptureState.Unknown)
        {
            Loud(note, "The capture state could NOT be read at exit, so the toggle was NOT pressed — a blind "
                + "press could as easily start a capture as stop one. If this sweep's earlier Ctrl+J, Ctrl+D "
                + "did start a capture, it is STILL RUNNING at roughly 1 MB per minute. Check with "
                + "`jjprobe trace`, and press Ctrl+J, Ctrl+D once if it reports a capture running.");
            return;
        }

        // The capture is on and the sweep is why. Press the toggle off.
        WindowInfo? window = _window != null && Native.IsWindow(_window.Hwnd)
            ? _window
            : Targets.Resolve(_pid, null);
        if (window == null)
        {
            Loud(note, "The capture this sweep started is STILL RUNNING (roughly 1 MB per minute) and no app "
                + "window could be found to press the toggle at. Press Ctrl+J, Ctrl+D in the app once to "
                + "stop it; the text lands in Saved Diagnostic Logs.");
            return;
        }

        if (!Chord.TryParse("Ctrl+J, Ctrl+D", out Chord toggle, out _)) return;

        // Restoring is the tail of an armed sweep: the guard can only be armed
        // by the sweep command, which runs inside Program.Armed. Re-assert the
        // arming here because the ProcessExit path may run after the command's
        // scope has unwound.
        Native.InjectionArmed = true;

        string? live = TraceLog.LiveLogForInstance(_instance);
        string headBefore = live != null ? TraceLog.HeadLine(live) : "";

        Press.SendQuiet(toggle, window, pauseMs: 250);

        // Confirm the transition the same way the turn-on was confirmed: the
        // toggle archives the session and opens a fresh file, so the only
        // honest proof is a new session announcing itself in the OFF direction.
        // A pressed toggle is not a verified transition.
        TraceLog.CaptureState confirmed = TraceLog.CaptureState.Unknown;
        for (int waited = 0; waited < 5000 && confirmed == TraceLog.CaptureState.Unknown; waited += 250)
        {
            Thread.Sleep(250);

            TraceLog.TraceHeader? h = TraceLog.FindForApp(_appDir);
            if (h != null && h.Capture != TraceLog.CaptureState.Unknown)
            {
                confirmed = h.Capture;
                break;
            }

            // Pre-marker build: the fresh file's opening prose is the only
            // announcement, and the head changing at all proves a new session.
            string? l2 = TraceLog.LiveLogForInstance(_instance);
            string head2 = l2 != null ? TraceLog.HeadLine(l2) : "";
            if (head2.Length > 0 && !string.Equals(head2, headBefore, StringComparison.Ordinal))
            {
                (TraceLog.CaptureState s2, _) = TraceLog.LegacyStateFromHead(head2);
                if (s2 != TraceLog.CaptureState.Unknown) { confirmed = s2; break; }
            }
        }

        if (confirmed == TraceLog.CaptureState.Off)
        {
            note?.Invoke("The detailed capture this sweep started was turned back OFF at exit, confirmed by "
                + "the fresh session announcing itself without a capture. Its text is in Saved Diagnostic "
                + "Logs. A capture the operator had already running would have been left alone — this one "
                + "was the sweep's own."
                + (markerKnown ? "" : " (Confirmed from the session's opening prose — this build predates "
                + "CaptureState lines.)"));
            return;
        }

        Loud(note, confirmed == TraceLog.CaptureState.On
            ? "Ctrl+J, Ctrl+D was pressed at exit but the log STILL reports a capture running — the detailed "
              + "capture this sweep started is STILL ON at roughly 1 MB per minute. Press Ctrl+J, Ctrl+D in "
              + "the app once to stop it; the text lands in Saved Diagnostic Logs."
            : "Ctrl+J, Ctrl+D was pressed at exit but no fresh session announced itself within 5 seconds, so "
              + "the sweep CANNOT CONFIRM the capture stopped. If it is still running it costs roughly 1 MB "
              + "per minute. Check with `jjprobe trace`; press Ctrl+J, Ctrl+D once if it reports a capture "
              + "running.");
    }

    /// <summary>Current capture state, by the same evidence order the sweep's
    /// preflight uses: the log's own CaptureState line when the build writes
    /// one, the session's opening prose when it does not.</summary>
    private static (TraceLog.CaptureState State, bool MarkerKnown) ReadState()
    {
        TraceLog.TraceHeader? h = _appDir != null ? TraceLog.FindForApp(_appDir) : null;
        if (h != null && h.Capture != TraceLog.CaptureState.Unknown)
            return (h.Capture, true);

        string? live = TraceLog.LiveLogForInstance(_instance);
        if (live == null) return (TraceLog.CaptureState.Unknown, false);
        (TraceLog.CaptureState s, _) = TraceLog.LegacyStateFromHead(TraceLog.HeadLine(live));
        return (s, false);
    }

    /// <summary>A failure to restore goes to stderr unconditionally AND into the
    /// report when one still exists. Written for a person to act on: what is
    /// running, what it costs, and the one keystroke that fixes it.</summary>
    private static void Loud(Action<string>? note, string message)
    {
        Console.Error.WriteLine("jjprobe: CAPTURE NOT RESTORED. " + message);
        note?.Invoke("CAPTURE NOT RESTORED. " + message);
    }
}
