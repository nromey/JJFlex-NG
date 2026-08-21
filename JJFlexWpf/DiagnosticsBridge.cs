using System;

namespace JJFlexWpf
{
    /// <summary>
    /// The one seam between the WPF diagnostics surface and the trace plumbing,
    /// which lives in the VB project (globals.vb) because that is where the log
    /// is opened, archived and rotated.
    ///
    /// Why a delegate table rather than a direct call: JJFlexWpf is referenced
    /// BY the VB project, not the other way round, so the surface cannot call
    /// the plumbing by name. Every earlier attempt at this problem in the
    /// codebase solved it by re-implementing the plumbing on the UI side — which
    /// is exactly how the retired trace dialog (deleted in Sprint 31) ended up
    /// starting traces that bypassed the session archive, misfiled them to
    /// Documents, and left the next boot tagging a perfectly clean exit as
    /// "killed".
    ///
    /// One implementation, four callers (Settings tab, Command Finder command,
    /// the Ctrl+J Ctrl+D chord, and later the feedback dialog). Every member is
    /// null-tolerant: an unwired bridge makes the surface report honestly that
    /// diagnostics are unavailable, never throw.
    /// </summary>
    public static class DiagnosticsBridge
    {
        // ── State ────────────────────────────────────────────────────────

        /// <summary>One sentence describing on/off, detail, and capture state.</summary>
        public static Func<string>? DescribeState { get; set; }

        /// <summary>True while a detailed capture is running.</summary>
        public static Func<bool>? IsCapturing { get; set; }

        /// <summary>The operator's standing "keep a diagnostic log" choice.</summary>
        public static Func<bool>? KeepLog { get; set; }

        /// <summary>0 = Normal, 1 = Detailed.</summary>
        public static Func<int>? DetailLevel { get; set; }

        // ── Actions ──────────────────────────────────────────────────────

        /// <summary>Start a detailed capture. Argument is a short reason for the manifest.</summary>
        public static Action<string>? StartCapture { get; set; }

        /// <summary>Stop the running capture, archive it, restore the standing level.</summary>
        public static Action? StopCapture { get; set; }

        /// <summary>Apply and persist (keepLog, detailLevel) immediately.</summary>
        public static Action<bool, int>? ApplySettings { get; set; }

        /// <summary>The operator's standing "record the meter stream" choice —
        /// the bench-session switch that puts the radio's continuous meter
        /// readings into the log, coalesced (task #170).</summary>
        public static Func<bool>? MeterStream { get; set; }

        /// <summary>Apply and persist the meter stream choice immediately.</summary>
        public static Action<bool>? ApplyMeterStream { get; set; }

        // StartLogAt / StopLog removed Sprint 31 (#103). They existed for the
        // retired trace dialog's "pick a file, pick a level, start" flow and
        // nothing else ever called them — not even that dialog, in the end,
        // which drove its own unwired delegates. Deleting the dialog deleted
        // the only reason to expose a way of pointing the log somewhere other
        // than the settings folder, which is exactly the bypass that used to
        // leave manual traces invisible to the browser and got the next boot
        // tagging a clean exit as "killed".

        // ── Where things live ────────────────────────────────────────────

        /// <summary>Resolved path of the live log file, or empty.</summary>
        public static Func<string>? LiveLogPath { get; set; }

        /// <summary>The settings folder the log and its archive live under.</summary>
        public static Func<string>? LogFolder { get; set; }

        /// <summary>Full path of the archive the last capture produced, or empty.</summary>
        public static Func<string>? LastCaptureArchivePath { get; set; }

        // ── What it is all costing ───────────────────────────────────────

        /// <summary>Spoken breakdown of what the settings folder is holding.</summary>
        public static Func<string>? DescribeStorage { get; set; }

        /// <summary>Spoken summary of saved crash reports and how many are unresolved.</summary>
        public static Func<string>? DescribeCrashReports { get; set; }

        /// <summary>Delete loose plain-text logs. Returns (files, bytes).</summary>
        public static Func<(int Files, long Bytes)>? DeleteLooseLogs { get; set; }

        /// <summary>Delete crash reports already sent or dismissed. Returns (files, bytes).</summary>
        public static Func<(int Files, long Bytes)>? DeleteResolvedCrashReports { get; set; }

        /// <summary>Human size, shared so labels and speech cannot disagree.</summary>
        public static Func<long, string>? DescribeBytes { get; set; }

        // ── Doors to other surfaces ──────────────────────────────────────

        /// <summary>Open the Saved Diagnostic Logs browser.</summary>
        public static Action? OpenSavedLogs { get; set; }

        /// <summary>Run the problem-report bundle collector.</summary>
        public static Action? SaveProblemReport { get; set; }

        /// <summary>Speak a message at Critical verbosity through the app's channel.</summary>
        public static Action<string>? Speak { get; set; }

        // ── Change notification ──────────────────────────────────────────

        /// <summary>
        /// Raised whenever the log's state changes. The status line subscribes so
        /// it re-reads reality instead of caching a copy of it — caching is
        /// precisely how the old dialog came to announce "Start tracing" for a
        /// trace that was already running.
        /// </summary>
        public static event EventHandler? StateChanged;

        /// <summary>Called by the plumbing after any state change.</summary>
        public static void NotifyStateChanged()
        {
            try { StateChanged?.Invoke(null, EventArgs.Empty); }
            catch { /* a subscriber's failure must not break the plumbing */ }
        }

        /// <summary>True when the plumbing has wired itself up.</summary>
        public static bool IsAvailable => DescribeState != null;

        /// <summary>Safe read of the current state sentence.</summary>
        public static string State()
        {
            try { return DescribeState?.Invoke() ?? "Diagnostic log state is not available."; }
            catch { return "Diagnostic log state is not available."; }
        }

        /// <summary>Safe read of "is a capture running".</summary>
        public static bool Capturing()
        {
            try { return IsCapturing?.Invoke() ?? false; }
            catch { return false; }
        }

        /// <summary>
        /// Start or stop the capture, whichever applies. The chord, the Command
        /// Finder command and the button all land here so the three can never
        /// disagree about what the toggle means.
        /// </summary>
        public static void ToggleCapture(string reason)
        {
            try
            {
                if (!IsAvailable)
                {
                    Speak?.Invoke("Detailed capture is not available.");
                    return;
                }
                if (Capturing()) StopCapture?.Invoke();
                else StartCapture?.Invoke(reason);
            }
            catch
            {
                Speak?.Invoke("The detailed capture could not be changed.");
            }
        }
    }
}
