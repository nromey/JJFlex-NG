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
        ReportingFailed,

        /// <summary>
        /// A recording that has ended is not yet safe: its durable recovery
        /// record could not be written, its archive could not be committed, or
        /// its last lines may not have reached the disk. The raw file is kept
        /// and the current log carries on; what is at risk is that the
        /// recording's details are recovered automatically if the application
        /// closes before the archive commits. Added Sprint 45 Track H7 under
        /// Astra's ruling that this failure reaches the operator through the
        /// existing accessible route rather than only through the trace, and
        /// as its own kind so a first announcement is not swallowed by an
        /// earlier, unrelated <see cref="ReportingFailed"/>.
        /// </summary>
        RecordingRecoveryAtRisk,

        /// <summary>
        /// Nothing is being written to the diagnostic log, and the operator did
        /// not turn it off: the live file failed a write, or a successor could
        /// not be opened after a seal. Distinct from
        /// <see cref="RecordingRecoveryAtRisk"/> because the consequence is
        /// different — evidence from now on, not the filing of evidence already
        /// taken — and both deserve to be heard once.
        /// </summary>
        RecordingStopped
    }

    /// <summary>
    /// One failure, described in the operator's language.
    /// </summary>
    public sealed class OperationFailureEventArgs : EventArgs
    {
        public OperationFailureEventArgs(FailureKind kind, string what, string detail)
            : this(kind, what, detail, key: null, isUpdate: false)
        {
        }

        public OperationFailureEventArgs(FailureKind kind, string what, string detail,
                                         string? key, bool isUpdate)
            : this(kind, what, detail, key, isUpdate, isResolution: false)
        {
        }

        public OperationFailureEventArgs(FailureKind kind, string what, string detail,
                                         string? key, bool isUpdate, bool isResolution)
        {
            Kind = kind;
            What = what ?? "";
            Detail = detail ?? "";
            Key = key;
            IsUpdate = isUpdate;
            IsResolution = isResolution;
        }

        /// <summary>
        /// True when the thing <see cref="Key"/> names has come RIGHT, and
        /// this is the sentence that replaces what the entry said while it
        /// was wrong. Implies <see cref="IsUpdate"/>. It differs from an
        /// ordinary update in what a subscriber does when it finds no entry
        /// to replace: nothing. A resolution is never a new problem, so it
        /// is never recorded as one and never announced (Sol's review of H8,
        /// blocker 3: a successful retry left "still filing it in the
        /// background" standing for the session).
        /// </summary>
        public bool IsResolution { get; }

        /// <summary>Which policy bucket this failure falls in.</summary>
        public FailureKind Kind { get; }

        /// <summary>
        /// The identity of the THING this failure is about, when the reporter
        /// may need to say something different about it later — a recording
        /// whose filing was pending and then failed, say. Null for a failure
        /// that is a moment rather than a thing. Two reports with the same key
        /// describe one entry in the Problems list, not two.
        /// </summary>
        public string? Key { get; }

        /// <summary>
        /// True when this replaces what was said before about <see cref="Key"/>.
        /// An update changes the entry the operator can read; it is never
        /// announced, because the operator was already told there is a
        /// problem and what changed is the answer to "what should I do about
        /// it" — which they read on demand, not hear unbidden.
        /// </summary>
        public bool IsUpdate { get; }

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

        /// <summary>
        /// Report a failure about a THING the reporter may have more to say
        /// about later, so that a later <see cref="UpdateKeyed"/> with the same
        /// key replaces this entry rather than adding beside it. Announced
        /// under the same policy as <see cref="Report"/>. Sprint 45 Track H8,
        /// for a recording whose filing was pending and then failed: the
        /// Problems entry must follow the ticket, and must not be spoken
        /// twice (Sol's review of H7, finding 4).
        /// </summary>
        public static void ReportKeyed(FailureKind kind, string what, string detail, string key)
        {
            try
            {
                JJTrace.Tracing.TraceLine(
                    $"OperationFailure[{kind}] ({key}): {what} — {detail}",
                    System.Diagnostics.TraceLevel.Error);
            }
            catch { }

            try { Reported?.Invoke(null, new OperationFailureEventArgs(kind, what, detail, key, isUpdate: false)); }
            catch { /* never let the offer path break the failing path */ }
        }

        /// <summary>
        /// Replace what was said about <paramref name="key"/>. Never announced:
        /// the operator already heard there was a problem with this thing,
        /// and the Problems entry is where they read what to do now. A
        /// subscriber that finds no entry for the key treats it as new — the
        /// original may have been pushed out of the list, or reported before
        /// the subscriber was listening — and only then may it announce.
        /// </summary>
        public static void UpdateKeyed(FailureKind kind, string what, string detail, string key)
        {
            try
            {
                JJTrace.Tracing.TraceLine(
                    $"OperationFailure[{kind}] ({key}) updated: {what} — {detail}",
                    System.Diagnostics.TraceLevel.Error);
            }
            catch { }

            try { Reported?.Invoke(null, new OperationFailureEventArgs(kind, what, detail, key, isUpdate: true)); }
            catch { /* never let the offer path break the failing path */ }
        }

        /// <summary>
        /// The thing <paramref name="key"/> names has come right: replace
        /// what the Problems entry says about it with the sentence that is
        /// true now, keeping the entry (and its clock time) because the
        /// problem WAS announced and a list that loses the entry loses the
        /// answer to "what was that about". Never announced, and — unlike
        /// <see cref="UpdateKeyed"/> — never recorded as new when no entry
        /// carries the key: a resolution is not a problem. Sprint 45 Track
        /// H9 (Sol's review of H8, blocker 3).
        /// </summary>
        public static void ResolveKeyed(FailureKind kind, string what, string detail, string key)
        {
            try
            {
                JJTrace.Tracing.TraceLine(
                    $"OperationFailure[{kind}] ({key}) resolved: {what} — {detail}",
                    System.Diagnostics.TraceLevel.Info);
            }
            catch { }

            try
            {
                Reported?.Invoke(null, new OperationFailureEventArgs(kind, what, detail, key,
                                                                     isUpdate: true, isResolution: true));
            }
            catch { /* never let the offer path break the failing path */ }
        }
    }
}
