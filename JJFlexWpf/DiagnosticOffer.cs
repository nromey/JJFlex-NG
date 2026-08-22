using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// Decides whether a failure is worth telling the operator about, records
    /// every one that is, and announces the first of each kind.
    ///
    /// NOTHING HERE OPENS A WINDOW ANY MORE. Sprint 31 (#100) replaced the
    /// failure-moment dialog with an earcon, one short spoken line, and a
    /// Problems list that persists for the session (see ProblemLog). Noel
    /// rejected the interaction model — "I worry that a window popping up might
    /// confuse the user", plus the deeper worry that he misses Windows
    /// notifications and then has no way to ask what he missed. The technical
    /// reason that settles it independently: a screen reader flushes its speech
    /// queue when a window opens, so a window that appears ON a failure destroys
    /// the sentence explaining the failure. The connect path is the proof — it
    /// speaks "Connection failed" and its advice one line before reporting, and
    /// the old dialog ate it.
    ///
    /// The classification below is unchanged and is the valuable half. What
    /// changed is only how the operator meets it.
    ///
    /// WHY THIS IS SAFE RATHER THAN A COMPROMISE, and the reason the case for
    /// interrupting collapses once you see it: the diagnostic log is written
    /// either way. The offer was never a safety net — it was a convenience that
    /// saved hunting later. Miss the announcement, ignore it, or quit the app
    /// entirely, and the evidence is still on disk and still exportable from
    /// Settings, Diagnostics. Nothing is lost by not interrupting.
    ///
    /// WHAT IS RECORDED AND ANNOUNCED, and why each one earns the operator's
    /// attention:
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
    ///   ReportingFailed — the pipeline itself broke. The record IS the fallback
    ///     here: if the bundle would not build, knowing that is what is left.
    ///
    /// WHAT IS DELIBERATELY NOT SURFACED:
    ///
    ///   Crashes. CrashReporter already shows a bundle prompt with a full
    ///     manifest and an upload choice. A second surface at the same moment
    ///     competes with the one that can actually act.
    ///
    ///   "No radios found" and an empty discovery. Not a failure — an ordinary
    ///     state with an obvious next action.
    ///
    ///   Login and token rejections. The operator's own next action fixes them,
    ///     and the log carries their SmartLink email and JWT fragments, so
    ///     pointing at the log raises the privacy cost with no diagnostic gain.
    ///     This exclusion is on PRIVACY grounds and must survive any future
    ///     loosening of the rest of the policy.
    ///
    ///   Anything a retry absorbed. A failure that recovered is not a failure
    ///     the operator needs to act on.
    ///
    ///   Corrupt preset files. Those already sideline the file and speak its
    ///     path — an honest, actionable message that needs no help.
    ///
    ///   Firmware download failures. Re-downloadable by definition.
    ///
    /// WHAT THE LIMITS ARE NOW, and what changed:
    ///   - EVERY qualifying failure is RECORDED. No per-kind limit, no session
    ///     cap, no "the operator said not now so stop". Those rules existed to
    ///     stop a modal window becoming a nuisance; with nothing stealing focus
    ///     there is nothing to be a nuisance about, and discarding a failure
    ///     because a similar one already happened throws away the repetition
    ///     that is often the whole diagnosis.
    ///   - The FIRST failure of each kind is announced; later ones of that kind
    ///     are not. Four announcements in a session, maximum, each one genuinely
    ///     new information. Anyone who missed one loses nothing: the list holds
    ///     everything and Ctrl+J, Ctrl+R reads it.
    ///   - Never announced while transmitting — never take the operator off the
    ///     air. It is still recorded, which is the repair: the old policy
    ///     dropped mid-transmit failures on the floor entirely.
    ///   - Never announced after shutdown has begun. Still recorded, in case
    ///     shutdown is what is failing.
    ///   - Announced whether or not the diagnostic log is running. The old
    ///     policy went silent with the log off, which is precisely when the
    ///     operator has the least evidence and most needs to be told. The
    ///     announcement makes no promise about a log, so it is honest either
    ///     way.
    ///
    /// NAME: still "DiagnosticOffer" because it is still the one place the
    /// judgement lives, and renaming it would churn call sites in three files
    /// for no behavioural gain. It offers a way in rather than a window now.
    /// </summary>
    public static class DiagnosticOffer
    {
        private static readonly HashSet<FailureKind> _announced = new();
        private static readonly object _gate = new();
        private static bool _installed;
        private static bool _shuttingDown;
        private static Dispatcher? _ui;

        /// <summary>Set by the app when transmit state is known. Null means "cannot tell".</summary>
        public static Func<bool>? IsTransmitting { get; set; }

        /// <summary>
        /// Subscribe to failure reports. Called once at startup, on the UI
        /// thread, so the dispatcher captured here is the one that owns the
        /// speech and earcon channels. Idempotent.
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
        /// still traced and still recorded, but nothing is spoken — speech
        /// racing a teardown arrives after the operator has stopped listening.
        /// </summary>
        public static void BeginShutdown() => _shuttingDown = true;

        /// <summary>Test and diagnostic hook: forget what has already been announced.</summary>
        public static void ResetSessionState()
        {
            lock (_gate) { _announced.Clear(); }
        }

        private static void OnFailureReported(object? sender, OperationFailureEventArgs e)
        {
            try
            {
                // RECORD FIRST, unconditionally, before any judgement about
                // whether to speak. Everything below can decide to stay quiet;
                // none of it may decide to forget.
                ProblemLog.Record(e.Kind, e.What, e.Detail);

                if (!ShouldAnnounce(e.Kind)) return;

                // Marshal to the UI thread when we have one. Failures are
                // reported from wherever they happen — config writes, connect
                // steps, audio callbacks. BeginInvoke, not Invoke: blocking a
                // failing code path on the UI thread is how a failure becomes a
                // hang. With no dispatcher (bridge never wired) announce inline
                // rather than silently swallowing it.
                var ui = _ui;
                if (ui == null) Announce(e);
                else ui.BeginInvoke(new Action(() => Announce(e)));
            }
            catch { /* an announcement that cannot be made must not become a second failure */ }
        }

        private static bool ShouldAnnounce(FailureKind kind)
        {
            if (_shuttingDown) return false;

            // Never take the operator off the air.
            try { if (IsTransmitting?.Invoke() == true) return false; }
            catch { }

            lock (_gate)
            {
                // First of each kind only. Add returns false when the kind is
                // already present, which is the whole rule.
                return _announced.Add(kind);
            }
        }

        private static void Announce(OperationFailureEventArgs e)
        {
            try
            {
                // Earcon first, so the operator knows something landed even
                // before the sentence starts.
                EarconPlayer.ProblemRecordedTone();

                // QUEUED, never interrupting. This is the entire point of the
                // redesign: the failing code path has usually just spoken its
                // own message — the connect path says "Connection failed" and
                // its advice one line before reporting — and interrupting that
                // destroys the explanation to announce that an explanation
                // exists. A failure notice is the SECOND half of a series, not
                // a supersession of one.
                string what = string.IsNullOrEmpty(e.What)
                    ? Radios.Lexicon.Get("logging.problem.unnamed")
                    : e.What;
                Radios.ScreenReaderOutput.Speak(
                    Radios.Lexicon.Get("logging.problem.announcement", ("what", what)),
                    Radios.Speech.SpeechIntent.Queue,
                    Radios.VerbosityLevel.Critical);
            }
            catch (Exception ex)
            {
                try
                {
                    JJTrace.Tracing.TraceLine(
                        "DiagnosticOffer.Announce failed: " + ex.Message,
                        System.Diagnostics.TraceLevel.Warning);
                }
                catch { }
            }
        }
    }
}
