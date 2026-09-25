#nullable enable
using System;
using System.Collections.Generic;

namespace Radios.Facts
{
    /// <summary>
    /// What a registered transport adapter is able to establish. Declared when
    /// it registers, and enforced on every piece of evidence it reports.
    /// </summary>
    /// <remarks>
    /// <b>A request-only adapter cannot report a completion by choosing that
    /// enum.</b> The store refuses evidence the adapter's registered capability
    /// does not cover, rather than believing it. Nothing here claims any
    /// backend can prove a stop or a drain (#621).
    /// </remarks>
    [Flags]
    public enum TransportCapability
    {
        /// <summary>Can say it issued a request, or that the request threw. Nothing more.</summary>
        RequestOnly = 0,

        /// <summary>Can report the backend accepting or refusing the request.</summary>
        ReportsAcceptance = 1,

        /// <summary>Can attribute progress to named clauses of the plan.</summary>
        ReportsProgress = 2,

        /// <summary>Can positively report that this attempt finished, or that it was cancelled.</summary>
        ReportsCompletion = 4,
    }

    /// <summary>One kind of evidence an adapter can report about an attempt.</summary>
    public enum EvidenceKind
    {
        RequestIssued = 0,
        RequestThrew = 1,
        BackendAccepted = 2,
        BackendRefused = 3,
        Progress = 4,
        Completed = 5,
        CancellationRequested = 6,
        Cancelled = 7,
        CompletionUnobservable = 8,
    }

    /// <summary>Why an attempt was cancelled, where the adapter can attribute it.</summary>
    public enum CancelCause
    {
        /// <summary>
        /// It stopped and nobody can say why. Pauses the grant involved, with a
        /// cause distinct from an observed quiet.
        /// </summary>
        Unknown = 0,

        /// <summary>Our own identified operation — named in the evidence — stopped it.</summary>
        OurOperation = 1,
    }

    /// <summary>
    /// One piece of attributable evidence about one attempt, in the adapter's
    /// own order.
    /// </summary>
    /// <remarks>
    /// Ordered by <see cref="AdapterSequence"/>, never by arrival time: a
    /// request-return callback that arrives after a synchronous completion
    /// cannot downgrade it, because state is derived from the whole set.
    /// </remarks>
    public sealed class TransportEvidence
    {
        private TransportEvidence(long adapterSequence, EvidenceKind kind, string? ticket,
                                  IReadOnlyList<string>? segments, CancelCause? cause,
                                  string? operation, string? reason)
        {
            AdapterSequence = adapterSequence;
            Kind = kind;
            Ticket = ticket;
            Segments = segments ?? Array.Empty<string>();
            Cause = cause;
            Operation = operation;
            Reason = reason;
        }

        public long AdapterSequence { get; }
        public EvidenceKind Kind { get; }

        /// <summary>The backend ticket this evidence is about, when the backend issues one.</summary>
        public string? Ticket { get; }

        /// <summary>For progress: the clause names the adapter can attribute. Empty means unattributable.</summary>
        public IReadOnlyList<string> Segments { get; }

        public CancelCause? Cause { get; }
        public string? Operation { get; }
        public string? Reason { get; }

        public static TransportEvidence RequestIssued(long seq, string? ticket = null) =>
            new(seq, EvidenceKind.RequestIssued, ticket, null, null, null, null);
        public static TransportEvidence RequestThrew(long seq, string reason, string? ticket = null) =>
            new(seq, EvidenceKind.RequestThrew, ticket, null, null, null, reason);
        public static TransportEvidence BackendAccepted(long seq, string? ticket = null) =>
            new(seq, EvidenceKind.BackendAccepted, ticket, null, null, null, null);
        public static TransportEvidence BackendRefused(long seq, string? reason = null, string? ticket = null) =>
            new(seq, EvidenceKind.BackendRefused, ticket, null, null, null, reason);
        public static TransportEvidence Progress(long seq, IReadOnlyList<string> segments, string? ticket = null) =>
            new(seq, EvidenceKind.Progress, ticket, segments, null, null, null);
        public static TransportEvidence Completed(long seq, string? ticket = null) =>
            new(seq, EvidenceKind.Completed, ticket, null, null, null, null);
        public static TransportEvidence CancellationRequested(long seq, string operation, string reason, string? ticket = null) =>
            new(seq, EvidenceKind.CancellationRequested, ticket, null, null, operation, reason);
        public static TransportEvidence Cancelled(long seq, CancelCause cause, string? operation = null, string? ticket = null) =>
            new(seq, EvidenceKind.Cancelled, ticket, null, cause, operation, null);
        public static TransportEvidence CompletionUnobservable(long seq, string? ticket = null) =>
            new(seq, EvidenceKind.CompletionUnobservable, ticket, null, null, null, null);

        /// <summary>What this evidence says, so a same-sequence resubmission can be told apart from a replay.</summary>
        internal string Fingerprint =>
            (int)Kind + "|" + (Ticket ?? "") + "|" + string.Join(",", Segments) + "|" + (Cause?.ToString() ?? "")
            + "|" + (Operation ?? "") + "|" + (Reason ?? "");

        /// <summary>The capability an adapter must hold to report this.</summary>
        internal TransportCapability Requires => Kind switch
        {
            EvidenceKind.BackendAccepted or EvidenceKind.BackendRefused => TransportCapability.ReportsAcceptance,
            EvidenceKind.Progress => TransportCapability.ReportsProgress,
            EvidenceKind.Completed or EvidenceKind.Cancelled => TransportCapability.ReportsCompletion,
            _ => TransportCapability.RequestOnly,
        };

        public override string ToString() => Kind + " #" + AdapterSequence;
    }

    /// <summary>Why the final start gate did not authorise an attempt.</summary>
    public enum NotStartedReason
    {
        /// <summary>An operator quiet was observed after the permission this plan relied on.</summary>
        QuietAfterPermission = 0,

        /// <summary>The episode's observation scope ended, or its publisher was revoked.</summary>
        ScopeEnded = 1,

        /// <summary>The current rendering is no longer justified — unknown, ended or superseded.</summary>
        NotCurrent = 2,

        /// <summary>Newer information or a newer exact value makes this plan's words wrong; re-render first.</summary>
        Superseded = 3,

        /// <summary>A wording reload happened since the plan was rendered.</summary>
        CatalogChanged = 4,

        /// <summary>The automatic permission was paused for an unknown-cause cancellation.</summary>
        PausedUnknownCause = 5,

        /// <summary>This allocation was already consumed once.</summary>
        AlreadyConsumed = 6,

        /// <summary>The selected historical record changed or became conflicted.</summary>
        HistoricalRecordChanged = 7,
    }

    /// <summary>Plain summary of what became of an attempt, derived from its evidence.</summary>
    public enum AttemptDisposition
    {
        /// <summary>Allocated, not yet through the final start gate.</summary>
        Allocated = 0,

        /// <summary>The final start gate refused it. Nothing was requested.</summary>
        NotStarted = 1,

        /// <summary>Authorised and in flight; nothing reported yet.</summary>
        InFlight = 2,

        /// <summary>A request was issued and the adapter can say nothing more. Not completion, not refusal.</summary>
        RequestedOnly = 3,

        /// <summary>The request call threw. Whatever happened next is unknown.</summary>
        RequestFailed = 4,

        /// <summary>The backend refused it. It was never presented.</summary>
        Refused = 5,

        /// <summary>Some attributable progress, no completion.</summary>
        Partial = 6,

        /// <summary>A positively reported completion of THIS attempt.</summary>
        Completed = 7,

        /// <summary>Cancelled by our own identified operation.</summary>
        CancelledByUs = 8,

        /// <summary>Cancelled and nobody can say why or how much was said.</summary>
        CancelledUnknownCause = 9,

        /// <summary>Evidence collection closed with completion unobservable. Bounds bookkeeping; proves nothing.</summary>
        Unobservable = 10,

        /// <summary>Contradictory terminal evidence. Its coverage can discharge nothing.</summary>
        Disputed = 11,

        /// <summary>Loaded from a process that ended while this was in flight. Completion unconfirmed.</summary>
        Interrupted = 12,
    }

    /// <summary>What kind of request a plan answers.</summary>
    public enum PlanRequestKind
    {
        /// <summary>Automatic presentation under an owner-issued or explicitly resumed grant.</summary>
        Automatic = 0,

        /// <summary>The operator selected this record and asked for it to be read, once.</summary>
        SelectedRead = 1,
    }

    /// <summary>Immutable view of one attempt and its evidence.</summary>
    public sealed class AttemptSnapshot
    {
        internal AttemptSnapshot(
            AttemptId id, long planId, PlanRequestKind kind, bool historicalPlan, string rendering,
            IReadOnlyCollection<long> planCoverage, IReadOnlyCollection<long> established,
            AttemptDisposition disposition, NotStartedReason? notStarted, bool cancellationRequested,
            string? cancelOperation, IReadOnlyList<TransportEvidence> evidence, string? bindingName,
            bool fromPreviousProcess)
        {
            Id = id;
            PlanId = planId;
            Kind = kind;
            HistoricalPlan = historicalPlan;
            RenderingIdentity = rendering;
            PlanCoverage = planCoverage;
            EstablishedCoverage = established;
            Disposition = disposition;
            NotStarted = notStarted;
            CancellationRequested = cancellationRequested;
            CancelOperation = cancelOperation;
            Evidence = evidence;
            BindingName = bindingName;
            FromPreviousProcess = fromPreviousProcess;
        }

        public AttemptId Id { get; }
        public long PlanId { get; }
        public PlanRequestKind Kind { get; }
        public bool HistoricalPlan { get; }
        public string RenderingIdentity { get; }

        /// <summary>What the immutable plan could convey.</summary>
        public IReadOnlyCollection<long> PlanCoverage { get; }

        /// <summary>What positively established evidence says it did convey. Empty when disputed.</summary>
        public IReadOnlyCollection<long> EstablishedCoverage { get; }

        public AttemptDisposition Disposition { get; }
        public NotStartedReason? NotStarted { get; }

        /// <summary>A cancellation was requested. Recorded beside, never instead of, what then happened.</summary>
        public bool CancellationRequested { get; }
        public string? CancelOperation { get; }

        public IReadOnlyList<TransportEvidence> Evidence { get; }
        public string? BindingName { get; }

        /// <summary>Evidence about a previous process's attempt. Historical; no current sink exists for it.</summary>
        public bool FromPreviousProcess { get; }

        /// <summary>True when the adapter positively reported full completion of this attempt.</summary>
        public bool CompletedFully => Disposition == AttemptDisposition.Completed;

        public override string ToString() => Id + " " + Disposition;
    }

    /// <summary>
    /// What is known about the earcon — the occurrence's receipt, kept apart
    /// from the sentence because the tone does not queue behind speech.
    /// </summary>
    /// <remarks>
    /// <b>None of these means heard.</b> A broken output device, a disabled
    /// category or a deliberate suppression all remain possible.
    /// </remarks>
    public enum ReceiptState
    {
        /// <summary>No receipt: the policy says none, or nothing has claimed it yet.</summary>
        NotRequested = 0,

        /// <summary>The application issued the tone request. The most anyone can usually say.</summary>
        Requested = 1,

        /// <summary>The player reported that it played. Still not the same as heard.</summary>
        PlaybackReported = 2,

        /// <summary>No output device, or the category is off. The tone did not happen.</summary>
        Unavailable = 3,

        /// <summary>Deliberately suppressed. An instruction being honoured, not a fault.</summary>
        Suppressed = 4,

        /// <summary>A permit was claimed and not yet used at the request boundary.</summary>
        Claimed = 5,

        /// <summary>The claimed permit was refused at the request boundary — a quiet or scope end came first. No tone was requested.</summary>
        Withheld = 6,

        /// <summary>The request boundary was crossed and nobody can say what happened next.</summary>
        OutcomeUnknown = 7,
    }

    /// <summary>What happened at the lowest tone-request boundary, as the receipt adapter reports it.</summary>
    public enum ToneRequestResult
    {
        Requested = 0,
        PlaybackReported = 1,
        Unavailable = 2,
        Suppressed = 3,
    }

    /// <summary>Immutable view of one occurrence's receipt.</summary>
    public sealed class ReceiptSnapshot
    {
        internal ReceiptSnapshot(ReceiptPolicy policy, ReceiptState state, bool allowanceConsumed,
                                 long receiptId, bool fromPreviousProcess, bool closed)
        {
            Policy = policy;
            State = state;
            AllowanceConsumed = allowanceConsumed;
            ReceiptId = receiptId;
            FromPreviousProcess = fromPreviousProcess;
            Closed = closed;
        }

        public ReceiptPolicy Policy { get; }
        public ReceiptState State { get; }

        /// <summary>
        /// The once-per-occurrence request allowance has been spent. Never
        /// reset automatically — uncertainty does not buy a second tone.
        /// </summary>
        public bool AllowanceConsumed { get; }

        public long ReceiptId { get; }

        /// <summary>Evidence about a previous process's occurrence. Never a fresh tone, never a replay permit.</summary>
        public bool FromPreviousProcess { get; }

        /// <summary>
        /// The observation scope ended before any request was made, so the
        /// allowance closed unused. Not requested, not played — and a
        /// continuation of the occurrence never reissues it.
        /// </summary>
        public bool Closed { get; }

        public override string ToString() =>
            Policy + " " + State + (AllowanceConsumed ? " (spent)" : "") + (Closed ? " (closed)" : "");
    }
}
