using System;

namespace Radios
{
    /// <summary>
    /// The kinds of failure the diagnostic-offer policy knows about.
    ///
    /// This enum is deliberately short. Every value in it is a case where the
    /// operator asked for something, did not get it, and cannot fix it from the
    /// message alone — and where the diagnostic log actually holds evidence
    /// about what happened. Adding a value is a policy decision, not a
    /// convenience: see DiagnosticOffer for what is deliberately absent and why.
    /// </summary>
    public enum FailureKind
    {
        /// <summary>
        /// Something the operator changed did not reach disk. The choice is
        /// live for this session and will be gone at the next launch.
        /// </summary>
        SettingNotSaved,

        /// <summary>
        /// A connection attempt to a named radio failed. NOT "no radios found",
        /// which is an ordinary state with an obvious next step.
        /// </summary>
        ConnectFailed,

        /// <summary>
        /// An audio stream would not open, or stopped during a session. The
        /// operator hears nothing and has no way to see why.
        /// </summary>
        AudioUnavailable,

        /// <summary>
        /// The reporting pipeline itself failed — a problem report that would
        /// not build, a capture that would not start. The one case where the
        /// offer is also the fallback.
        /// </summary>
        ReportingFailed
    }

    /// <summary>
    /// One failure, described in the operator's language.
    /// </summary>
    public sealed class OperationFailureEventArgs : EventArgs
    {
        public OperationFailureEventArgs(FailureKind kind, string what, string detail)
        {
            Kind = kind;
            What = what ?? "";
            Detail = detail ?? "";
        }

        /// <summary>Which policy bucket this failure falls in.</summary>
        public FailureKind Kind { get; }

        /// <summary>
        /// One short clause naming what did not happen, in the operator's terms
        /// and in the past tense — "Your radio profile could not be saved".
        ///
        /// This is SPOKEN, once, the moment the failure happens, followed by
        /// "Press Control J then Control R for details" — so keep it short
        /// enough to be heard in one breath and specific enough to stand alone.
        /// It is also the first half of the entry in the Problems list.
        /// </summary>
        public string What { get; }

        /// <summary>
        /// A sentence or two of consequence and next step. Not spoken at the
        /// moment of failure — it is the second half of the Problems list entry,
        /// read when the operator asks with Ctrl+J, Ctrl+R. That split is the
        /// point: the announcement stays short enough not to be a burden, and
        /// the explanation stays available for as long as the app is running.
        /// </summary>
        public string Detail { get; }
    }

    /// <summary>
    /// Where failures worth telling the operator about are reported.
    ///
    /// This type has no UI and lives in Radios so that anything — the config
    /// layer, the connect flow, the audio path — can report without knowing
    /// what happens next. JJFlexWpf.DiagnosticOffer subscribes, records every
    /// report in the Problems list, and owns every judgement about whether the
    /// operator hears about it.
    ///
    /// The split is the point. Reporting a failure must be cheap enough that
    /// nobody hesitates to do it; deciding to say something out loud must be
    /// expensive enough that it is done in exactly one place, with the whole
    /// policy visible at once.
    /// </summary>
    public static class OperationFailure
    {
        /// <summary>
        /// Raised for every reported failure. Subscribers must never throw;
        /// Report swallows anything that escapes, because a reporting path that
        /// can break the thing it is reporting on is worse than no reporting.
        /// </summary>
        public static event EventHandler<OperationFailureEventArgs>? Reported;

        /// <summary>
        /// Report a failure. Always traces; whether anything is shown to the
        /// operator is the subscriber's decision, not the caller's.
        /// </summary>
        /// <param name="kind">Policy bucket.</param>
        /// <param name="what">Short past-tense clause naming what did not happen.</param>
        /// <param name="detail">Consequence and next step, one or two sentences.</param>
        public static void Report(FailureKind kind, string what, string detail = "")
        {
            try
            {
                JJTrace.Tracing.TraceLine(
                    $"OperationFailure[{kind}]: {what} — {detail}",
                    System.Diagnostics.TraceLevel.Error);
            }
            catch { }

            try { Reported?.Invoke(null, new OperationFailureEventArgs(kind, what, detail)); }
            catch { /* never let the offer path break the failing path */ }
        }
    }
}
