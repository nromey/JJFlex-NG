using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace JJTrace
{
    /// <summary>
    /// The identity a delayed caller carries so it can only ever act on the
    /// recording it was about.
    ///
    /// <para><b>An opaque handle rather than a bare Guid</b>, deliberately. A
    /// Guid is sufficient as a process-unique identity, but a handle cannot be
    /// confused with any other Guid at a call site, cannot be default-
    /// constructed into something that looks valid, and makes
    /// "expected session" a type rather than a convention.</para>
    /// </summary>
    public sealed class TraceSessionHandle
    {
        internal TraceSessionHandle(TraceSession session)
        {
            Session = session;
            SessionId = session.SessionId;
        }

        /// <summary>The session this handle names. JJTrace-internal: outside
        /// this assembly a handle is an identity, not a way to reach the
        /// mutable session.</summary>
        internal TraceSession Session { get; }

        /// <summary>Process-unique id of the session this handle names.</summary>
        public Guid SessionId { get; }

        public override string ToString() => SessionId.ToString();
    }

    /// <summary>
    /// What a lifecycle transition did. A null string could not express this
    /// contract: "nothing happened" has at least five distinct causes and the
    /// caller has to treat them differently.
    /// </summary>
    public enum TraceTransition
    {
        /// <summary>This operation owned the session and performed the
        /// transition. <see cref="TraceTransitionResult.Ticket"/> is set.</summary>
        Accepted,

        /// <summary>This exact operation already owns a ticket for that
        /// session. The same ticket comes back and nothing is repeated.</summary>
        AlreadyClaimed,

        /// <summary>Another transition has already ended the expected session.
        /// No lifecycle effect whatever: no listener closed, no capture state
        /// cleared, no logging restarted. Expected and observed identities are
        /// both reported so the trace can name them.</summary>
        NotCurrent,

        /// <summary>Nothing was recording. Not a failure.</summary>
        NoSession,

        /// <summary>Exit is committed. New captures, log-enable requests and
        /// restarts are refused from here on.</summary>
        ShuttingDown,

        /// <summary>A session is already recording. Nothing may open a second
        /// one: the rule that nothing flips the switch without settling what
        /// was already open is enforced here rather than remembered at five
        /// call sites.</summary>
        AlreadyRecording,

        /// <summary>The boundary was entered and something in it failed.
        /// <see cref="TraceTransitionResult.FailedStage"/> names where, and
        /// <see cref="TraceTransitionResult.RetainedSourcePath"/> names the raw
        /// evidence that was kept.</summary>
        Failed,
    }

    /// <summary>
    /// The outcome of one boundary operation, frozen at the moment the gate was
    /// released. Everything a caller needs to speak, trace or decide is in here,
    /// so nothing has to re-read shared state afterwards and discover a
    /// different world.
    /// </summary>
    public sealed class TraceTransitionResult
    {
        public TraceTransition Status { get; internal set; }

        /// <summary>True when the session really was detached — Accepted, or
        /// AlreadyClaimed returning the earlier ticket.</summary>
        public bool Owned => Status == TraceTransition.Accepted
                             || Status == TraceTransition.AlreadyClaimed;

        /// <summary>The detached session's archive ticket, or null. Accepted
        /// means the file was detached and queued — NOT that a zip and manifest
        /// have been committed. <see cref="TraceArchiveTicket.Completion"/> is
        /// what says that.</summary>
        public TraceArchiveTicket Ticket { get; internal set; }

        public Guid ExpectedSessionId { get; internal set; }
        public Guid ObservedSessionId { get; internal set; }

        /// <summary>The session that opened in the old one's place, or null
        /// when none did. A caller must read this rather than assuming: a drop
        /// during shutdown may seal its own session and open nothing.</summary>
        public TraceSessionHandle Successor { get; internal set; }

        public bool SuccessorOpened => Successor != null;

        /// <summary>True when a successor was wanted and could not be opened.
        /// The old ticket is still valid; only the restart failed.</summary>
        public bool RestartFailed { get; internal set; }

        /// <summary>Whether tracing is actually writing to a file now. The real
        /// state, not the intent — and since Track H7, VERIFIED: a sink counts
        /// as recording only once its first record was written and flushed.
        /// "Opened" used to be enough, and an opened stream proves nothing
        /// about the next byte.</summary>
        public bool TracingOn { get; internal set; }

        /// <summary>True when the detached session was carrying the operator's
        /// detailed capture, so its running state has been cleared.</summary>
        public bool EndedDetailedCapture { get; internal set; }

        /// <summary>The capture identity this transition ended, or
        /// <see cref="Guid.Empty"/>.</summary>
        public Guid EndedCaptureId { get; internal set; }

        /// <summary>When that capture started, local clock, or null.</summary>
        public DateTime? EndedCaptureStartedLocal { get; internal set; }

        /// <summary>
        /// The detailed capture this transition STARTED, or
        /// <see cref="Guid.Empty"/>. A fact of the transition, so a caller never
        /// has to re-read "is a capture running?" afterwards — by which time a
        /// drop may already have sealed it.
        /// </summary>
        public Guid StartedCaptureId { get; internal set; }

        public string FailedStage { get; internal set; }
        public string RetainedSourcePath { get; internal set; }

        /// <summary>
        /// The session was detached, but its durable pending record could not
        /// be written. Reported as a fact rather than swallowed (Sol's review of
        /// H3, finding 4); <see cref="DeferredFaults"/> says where the raw file
        /// is and what a crash before its archive commits would lose. The same
        /// fact is retained in <see cref="TraceRecordingHealth"/>, which is
        /// where the operator reaches it.
        /// </summary>
        public bool PendingRecordFailed { get; internal set; }

        /// <summary>
        /// A terminal record or the close reported a failure while the
        /// session was sealed: the detached file's last lines may not have
        /// reached the disk. The bytes that did land are still retained and
        /// still reported; this says the tail is uncertain.
        /// </summary>
        public bool TailUncertain { get; internal set; }

        /// <summary>
        /// The sink's latched fault when <see cref="TailUncertain"/>, or when a
        /// successor could not verify its first write; null otherwise.
        /// </summary>
        public string SinkFault { get; internal set; }

        /// <summary>
        /// Which generation of the live sink this result describes — the
        /// coordinator's count of sink changes, read under the gate as the
        /// transition ended. The health model orders notes by it, so a report
        /// about an older sink cannot overwrite the state of a newer one
        /// whatever thread it arrives on (Sol's review of H7, finding 2).
        /// </summary>
        public long SinkGeneration { get; internal set; }

        /// <summary>One line of trace text describing this result. Never user
        /// prose — a refusal is logged as a refusal, naming both identities,
        /// and never as a statement that the current session suffered
        /// anything.</summary>
        public string Explanation { get; internal set; }

        /// <summary>
        /// Faults collected while the gate was held and published afterwards.
        /// Nothing inside the boundary may log through <see cref="Tracing"/>:
        /// that would take the framework's trace lock in the opposite order.
        /// </summary>
        public IReadOnlyList<string> DeferredFaults { get; internal set; }
            = Array.Empty<string>();

        internal static TraceTransitionResult Refusal(
            TraceTransition status, Guid expected, Guid observed, string explanation, bool tracingOn)
        {
            return new TraceTransitionResult
            {
                Status = status,
                ExpectedSessionId = expected,
                ObservedSessionId = observed,
                Explanation = explanation,
                TracingOn = tracingOn,
            };
        }
    }

    /// <summary>
    /// A detached trace file waiting to be compressed, and the promise of a
    /// truthful answer about whether that ever happened.
    ///
    /// <para>The ticket is handed out with the gate already released. Its
    /// source file has been closed and moved to a path no future writer knows,
    /// so compression can take as long as it likes: <b>the boundary is never
    /// held while a zip is made.</b> That is the whole reason this type
    /// exists — the old path compressed a file that could be megabytes while
    /// the application was tearing down.</para>
    /// </summary>
    public sealed class TraceArchiveTicket
    {
        public Guid TicketId { get; internal set; } = Guid.NewGuid();
        public Guid SessionId { get; internal set; }

        /// <summary>1-based part number, or 0 for a session archived whole.</summary>
        public int PartNumber { get; internal set; }

        public bool IsFinalPart { get; internal set; }

        /// <summary>
        /// True when this ticket froze a part of a session that is STILL
        /// RUNNING — the problem-report bundler's checkpoint. A checkpoint is
        /// not an end: it must never claim the session finished.
        /// </summary>
        public bool IsCheckpoint { get; internal set; }

        /// <summary>The unique retained path the live file was moved to.</summary>
        public string SourcePath { get; internal set; }

        public string ArchiveRootDir { get; internal set; }

        /// <summary>
        /// Metadata frozen at the seal. Later observations cannot rewrite a
        /// sealed ticket, which is why this is a snapshot and not the live
        /// <see cref="TraceSession"/>.
        /// </summary>
        public TraceSessionEntry Entry { get; internal set; }

        /// <summary>Outcome tag baked into the archive FILENAME. Frozen with
        /// the rest, so a delayed compressor cannot rename a part out of its
        /// own chain.</summary>
        public string OutcomeFileTag { get; internal set; }

        /// <summary>Local boot stamp used for the archive's dated folder and
        /// filename.</summary>
        public DateTime StampLocal { get; internal set; }

        /// <summary>
        /// True when the durable pending record was written beside
        /// <see cref="SourcePath"/> before any successor was published. False
        /// means the raw file is retained by the plain-text sweep's
        /// unarchived-evidence rule instead, and a process ending before this
        /// ticket's archive commits would leave the raw file without its
        /// outcome or detail. See <see cref="TraceArchiveWorker.ClassifyPlainTextTrace"/>.
        /// </summary>
        public bool PendingRecordWritten { get; internal set; }

        /// <summary>
        /// True when a terminal write or the close failed as the file was
        /// sealed, so its tail may be incomplete. The retained bytes are
        /// still the evidence; this is the honest label on them.
        /// </summary>
        public bool TailUncertain { get; internal set; }

        /// <summary>The sink's latched fault, when <see cref="TailUncertain"/>.</summary>
        public string SinkFault { get; internal set; }

        /// <summary>Completes when the archive worker has finished with this
        /// ticket, one way or the other.</summary>
        public Task<TraceArchiveCompletion> Completion { get; internal set; }

        internal TaskCompletionSource<TraceArchiveCompletion> CompletionSource { get; set; }
    }

    /// <summary>
    /// What became of a ticket. Reports committed, retained-but-pending, or
    /// failed — three different things that a returned path string flattened
    /// into "null".
    /// </summary>
    public sealed class TraceArchiveCompletion
    {
        public Guid TicketId { get; internal set; }

        /// <summary>True only when the zip was published AND its manifest entry
        /// committed.</summary>
        public bool ArchiveCommitted { get; internal set; }

        public string ArchiveRelativeName { get; internal set; }
        public string ArchiveFullPath { get; internal set; }

        /// <summary>True when the raw evidence is still on disk under
        /// <see cref="RawPath"/>. A failed archive never deletes its source to
        /// make room.</summary>
        public bool RawRetained { get; internal set; }

        public string RawPath { get; internal set; }

        public string FailureStage { get; internal set; }
        public string FailureMessage { get; internal set; }
    }

    /// <summary>
    /// An immutable look at what the coordinator is doing right now. A read of
    /// this is a request target, never permission to mutate anything it names.
    /// </summary>
    public sealed class TraceObservation
    {
        public TraceSessionHandle Handle { get; internal set; }
        public Guid SessionId => Handle?.SessionId ?? Guid.Empty;
        public bool Recording { get; internal set; }
        public string LivePath { get; internal set; }
        public TraceLevel Level { get; internal set; }
        public bool ShuttingDown { get; internal set; }
        public bool CaptureRunning { get; internal set; }
        public Guid CaptureId { get; internal set; }
        public DateTime? CaptureStartedLocal { get; internal set; }
        public int PartNumber { get; internal set; }
        public bool HasParts { get; internal set; }
        public DateTime? SessionBootTimeUtc { get; internal set; }
    }

    /// <summary>
    /// The unchanging facts a terminal record needs, set once at boot. Data
    /// rather than a callback on purpose: the boundary never invokes a caller's
    /// code while it holds the gate.
    /// </summary>
    public sealed class TraceEnvironment
    {
        public int Instance { get; set; } = 1;
        public string AppVersion { get; set; } = "unknown";
        public string AppPath { get; set; } = string.Empty;
    }

    /// <summary>What, if anything, should start recording once the old session
    /// has been detached.</summary>
    public enum TraceResumeIntent
    {
        /// <summary>Nothing opens. Used by exit, and by a caller that has
        /// deliberately turned logging off.</summary>
        None,

        /// <summary>Open a successor at the operator's standing level, but only
        /// if they keep a standing log and shutdown has not begun.</summary>
        Standing,

        /// <summary>Open a successor at the level given in the request,
        /// regardless of the standing intent. Starting a capture.</summary>
        Explicit,
    }
}
