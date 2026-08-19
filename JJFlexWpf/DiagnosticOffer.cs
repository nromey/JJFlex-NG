using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// Decides whether a failure is worth interrupting the operator for, and
    /// offers them the diagnostic log when it is.
    ///
    /// This is the whole policy, in one place, on purpose. An offer that fires
    /// at the wrong moments trains the operator to dismiss it — permanently and
    /// invisibly — and an offer that fails to fire is worse than no offer at
    /// all, because by then the operator believes a safety net exists. Neither
    /// mistake shows up in a diff, so the reasoning is written down beside the
    /// rules rather than left to be reconstructed.
    ///
    /// WHAT IS OFFERED ON, and why each one earns an interruption:
    ///
    ///   SettingNotSaved — the operator changed something, it did not reach
    ///     disk, and they will not find out until the next launch, when the
    ///     setting is quietly back where it was. Today the only message is "see
    ///     the trace file for details", which is a dead end for someone who has
    ///     never been told where the trace file is.
    ///
    ///   ConnectFailed — the single most-reported problem, and the log holds the
    ///     entire handshake. This is the case where evidence goes stale fastest:
    ///     the next connect attempt rotates the interesting session out of easy
    ///     reach.
    ///
    ///   AudioUnavailable — the operator hears nothing. There is no visual cue
    ///     to fall back on and no way to inspect device negotiation from the UI.
    ///
    ///   ReportingFailed — the pipeline itself broke. The offer IS the fallback
    ///     here: if the bundle would not build, the raw log is what is left.
    ///
    /// WHAT IS DELIBERATELY NOT OFFERED ON:
    ///
    ///   Crashes. CrashReporter already shows a bundle prompt with a full
    ///     manifest and an upload choice. A second offer at the same moment
    ///     would be two windows deep at the worst possible time.
    ///
    ///   "No radios found" and an empty discovery. Not a failure — an ordinary
    ///     state with an obvious next action.
    ///
    ///   Login and token rejections. The operator's own next action fixes them,
    ///     and the log carries their SmartLink email and JWT fragments, so
    ///     offering to export raises the privacy cost with no diagnostic gain.
    ///
    ///   Anything a retry absorbed. A failure that recovered is not a failure
    ///     the operator needs to act on.
    ///
    ///   Corrupt preset files. Those already sideline the file and speak its
    ///     path — an honest, actionable message that needs no help.
    ///
    ///   Firmware download failures. Re-downloadable by definition.
    ///
    /// WHEN IT STAYS QUIET even for a kind it would otherwise offer on:
    ///   - while transmitting (never take the operator off the air)
    ///   - once per kind per session, and at most twice per session overall
    ///   - never after the operator has answered "Not now" even once
    ///   - never when the diagnostic log is off; there is nothing to offer
    ///   - never before the UI exists, or after shutdown has begun
    /// </summary>
    public static class DiagnosticOffer
    {
        /// <summary>
        /// Total offers allowed in one session, across all kinds. Two is a
        /// judgement, not a measurement: one is enough to establish the safety
        /// net exists, a second covers a genuinely different failure, and a
        /// third starts to feel like nagging — which is how an offer stops being
        /// read at all.
        /// </summary>
        private const int MaxOffersPerSession = 2;

        private static readonly HashSet<FailureKind> _offered = new();
        private static readonly object _gate = new();
        private static int _offerCount;
        private static bool _declinedForSession;
        private static bool _installed;
        private static bool _shuttingDown;
        private static Dispatcher? _ui;

        /// <summary>Set by the app when transmit state is known. Null means "cannot tell".</summary>
        public static Func<bool>? IsTransmitting { get; set; }

        /// <summary>
        /// Subscribe to failure reports. Called once at startup, on the UI
        /// thread, so the dispatcher captured here is the one that can show a
        /// window. Idempotent.
        /// </summary>
        public static void Install()
        {
            lock (_gate)
            {
                if (_installed) return;
                _installed = true;
                _ui = Dispatcher.CurrentDispatcher;
            }
            OperationFailure.Reported += OnFailureReported;
        }

        /// <summary>
        /// Called when the app begins shutting down. After this, failures are
        /// still traced but never open a window — a modal fighting a teardown is
        /// how an app ends up with no exit path at all.
        /// </summary>
        public static void BeginShutdown() => _shuttingDown = true;

        /// <summary>Test and diagnostic hook: forget this session's offer history.</summary>
        public static void ResetSessionState()
        {
            lock (_gate)
            {
                _offered.Clear();
                _offerCount = 0;
                _declinedForSession = false;
            }
        }

        private static void OnFailureReported(object? sender, OperationFailureEventArgs e)
        {
            try
            {
                if (!ShouldOffer(e.Kind)) return;

                var ui = _ui;
                if (ui == null) return;

                // Marshal to the UI thread. Failures are reported from wherever
                // they happen — config writes, connect steps, audio callbacks —
                // and none of those is guaranteed to be a thread that can show a
                // window. BeginInvoke, not Invoke: blocking a failing code path
                // on a modal dialog is how a failure becomes a hang.
                ui.BeginInvoke(new Action(() => ShowOffer(e)));
            }
            catch { /* an offer that cannot be made must not become a second failure */ }
        }

        private static bool ShouldOffer(FailureKind kind)
        {
            if (_shuttingDown) return false;

            // Nothing to offer if nothing is being recorded — but a running
            // capture counts even when the standing log is switched off. That
            // combination is the operator deliberately hunting something, which
            // is the LAST moment to decide there is no evidence worth offering.
            try
            {
                if (DiagnosticsBridge.KeepLog?.Invoke() == false && !DiagnosticsBridge.Capturing())
                    return false;
            }
            catch { }
            try { if (string.IsNullOrEmpty(DiagnosticsBridge.LiveLogPath?.Invoke())) return false; }
            catch { return false; }

            // Never take the operator off the air. A modal stealing focus mid
            // transmission is worse than any diagnostic is worth.
            try { if (IsTransmitting?.Invoke() == true) return false; }
            catch { }

            lock (_gate)
            {
                if (_declinedForSession) return false;
                if (_offerCount >= MaxOffersPerSession) return false;
                if (!_offered.Add(kind)) return false;
                _offerCount++;
                return true;
            }
        }

        private static void ShowOffer(OperationFailureEventArgs e)
        {
            try
            {
                string logPath = "";
                try { logPath = DiagnosticsBridge.LiveLogPath?.Invoke() ?? ""; } catch { }

                // The title carries the failure. Do NOT speak first and then
                // open the window: the screen reader flushes its queue on the
                // window change and the announcement is destroyed, whether it
                // was queued or interrupting. This was tried three ways on
                // 2026-08-18 for the disconnect announcement and the operator
                // heard the new window's title and nothing else, every time.
                string title = string.IsNullOrEmpty(e.What)
                    ? "Something went wrong. Save the diagnostic log?"
                    : $"{e.What}. Save the diagnostic log?";

                var dlg = new Dialogs.DiagnosticOfferDialog(title, e.Detail, logPath);
                dlg.ShowDialog();

                if (dlg.Declined)
                {
                    lock (_gate) { _declinedForSession = true; }
                    JJTrace.Tracing.TraceLine(
                        "DiagnosticOffer: operator declined; no further offers this session",
                        System.Diagnostics.TraceLevel.Info);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    JJTrace.Tracing.TraceLine(
                        "DiagnosticOffer.ShowOffer failed: " + ex.Message,
                        System.Diagnostics.TraceLevel.Warning);
                }
                catch { }
            }
        }
    }
}
