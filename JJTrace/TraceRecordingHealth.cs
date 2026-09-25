using System;
using System.Collections.Generic;
using System.Threading;

namespace JJTrace
{
    /// <summary>Whether anything is being written right now, and if not, why.</summary>
    public enum TraceSinkState
    {
        /// <summary>Nothing recording, by intent: logging off, exit committed,
        /// or a seal that deliberately opened no successor.</summary>
        Off,

        /// <summary>A sink is open AND its first record was written and
        /// flushed, and no write has failed since.</summary>
        Recording,

        /// <summary>A sink was wanted and could not be opened, or was open and
        /// then failed a write. Nothing is being recorded, and that was not
        /// the operator's choice.</summary>
        Failed,
    }

    /// <summary>
    /// One detached trace file whose recovery is not yet assured: its durable
    /// record could not be written, its archive could not be committed, or its
    /// last lines may not have reached the disk. Cleared, per ticket, only when
    /// that ticket's record persists or its archive commits.
    /// </summary>
    public sealed class TraceRecoveryCondition
    {
        public Guid TicketId { get; internal set; }
        public Guid SessionId { get; internal set; }
        public int PartNumber { get; internal set; }
        public bool IsCheckpoint { get; internal set; }

        /// <summary>The retained raw file. A path being present permits
        /// OFFERING the file, not claiming a complete recording.</summary>
        public string RawPath { get; internal set; }

        /// <summary>Whether the raw file existed the last time this was
        /// looked at. Not a promise about now.</summary>
        public bool RawRetained { get; internal set; }

        /// <summary>True when a terminal write or the close reported a
        /// failure: the file's tail is incomplete or uncertain.</summary>
        public bool TailUncertain { get; internal set; }

        /// <summary>True once the durable record is on disk beside the raw
        /// file — the next launch can finish this on its own.</summary>
        public bool RecoveryRecordWritten { get; internal set; }

        /// <summary>The archive worker's last failure for this ticket, or
        /// null: "no_archive_root", "compress", "manifest", "worker".</summary>
        public string ArchiveFailureStage { get; internal set; }

        public string ArchiveFailureMessage { get; internal set; }

        public DateTime NotedUtc { get; internal set; }

        /// <summary>The sink's own latched fault, when the tail is uncertain.</summary>
        public string SinkFault { get; internal set; }

        /// <summary>
        /// True on the clone carried by a
        /// <see cref="TraceRecordingHealthChangeKind.ConditionResolved"/>
        /// change when what resolved it was the archive committing. False
        /// there when it was the worker's retry writing the recovery record
        /// (the archive is still being made). Never true on an unresolved
        /// condition. The two resolutions are different sentences for the
        /// operator (Sol's review of H8, blocker 3).
        /// </summary>
        public bool ArchiveCommitted { get; internal set; }

        /// <summary>
        /// True on the clone carried by a
        /// <see cref="TraceRecordingHealthChangeKind.ConditionResolved"/>
        /// change when what resolved it was the worker's retry writing the
        /// recovery record. <see cref="RecoveryRecordWritten"/> on that clone
        /// is left AS IT WAS — false — so the clone still says what was
        /// wrong; the first cut flipped it and the operator's entry then
        /// read "what went wrong at the time: its recovery is not yet
        /// confirmed", which was neither what went wrong nor true.
        /// </summary>
        public bool RecoveryRecordPersisted { get; internal set; }

        internal TraceRecoveryCondition Clone() => (TraceRecoveryCondition)MemberwiseClone();
    }

    /// <summary>What kind of change a <see cref="TraceRecordingHealth.Changed"/>
    /// subscriber is being told about.</summary>
    public enum TraceRecordingHealthChangeKind
    {
        /// <summary>A ticket's recovery is newly at risk.</summary>
        ConditionRaised,

        /// <summary>
        /// A ticket that was at risk has its record or its archive. Which
        /// one is on the carried condition's
        /// <see cref="TraceRecoveryCondition.ArchiveCommitted"/>. Whoever
        /// keeps an operator-facing entry for the ticket must UPDATE it — the
        /// sentence it was told ("still filing it in the background") has
        /// stopped being true — and must not announce (Sol's review of H8,
        /// blocker 3).
        /// </summary>
        ConditionResolved,

        /// <summary>
        /// A ticket that was already at risk has got worse in a way the
        /// operator's entry must follow: its archive has now failed. Raised so
        /// a Problems entry composed while the archive was still pending —
        /// "still filing it in the background" — is replaced rather than left
        /// standing over a worker that has stopped (Sol's review of H7,
        /// finding 4). Not a new problem: whoever reports these must update,
        /// not announce.
        /// </summary>
        ConditionUpdated,

        /// <summary>The live sink failed: nothing is being written.</summary>
        SinkFailed,

        /// <summary>A sink is recording again (verified first write).</summary>
        SinkRecording,

        /// <summary>Recording stopped by intent.</summary>
        SinkOff,
    }

    public sealed class TraceRecordingHealthChange
    {
        public TraceRecordingHealthChangeKind Kind { get; internal set; }

        /// <summary>The condition concerned, for the two condition kinds.</summary>
        public TraceRecoveryCondition Condition { get; internal set; }

        /// <summary>The whole state after this change.</summary>
        public TraceRecordingHealthSnapshot Snapshot { get; internal set; }
    }

    /// <summary>An immutable read of the whole health state.</summary>
    public sealed class TraceRecordingHealthSnapshot
    {
        public TraceSinkState SinkState { get; internal set; }

        /// <summary>The live sink's latched fault, when <see cref="SinkState"/>
        /// is Failed; null otherwise.</summary>
        public string SinkFault { get; internal set; }

        /// <summary>Where the failed sink was writing, when known.</summary>
        public string SinkPath { get; internal set; }

        /// <summary>The session recording now, or empty.</summary>
        public Guid LiveSessionId { get; internal set; }

        /// <summary>Every ticket whose recovery is not yet assured, oldest first.</summary>
        public IReadOnlyList<TraceRecoveryCondition> Unresolved { get; internal set; }
            = Array.Empty<TraceRecoveryCondition>();

        /// <summary>How many write, record or archive failures this process
        /// has seen, resolved or not. Never cleared: resolution clears the
        /// CONDITION, not the history of it having happened.</summary>
        public int HistoricalFailures { get; internal set; }

        public string LastFailure { get; internal set; }
        public DateTime? LastFailureUtc { get; internal set; }

        /// <summary>Raw files adopted at startup under an inventory identity
        /// because nothing recorded whose they were.</summary>
        public int OrphansAdoptedAtBoot { get; internal set; }

        public bool HasUnresolved => Unresolved.Count > 0;

        /// <summary>True when anything here is worth telling the operator:
        /// an unresolved condition, or a sink that failed.</summary>
        public bool NeedsAttention => HasUnresolved || SinkState == TraceSinkState.Failed;
    }

    /// <summary>
    /// The retained, in-memory truth about whether diagnostic recording is
    /// working and whether every detached trace can be recovered.
    ///
    /// <para><b>Why a model and not a trace line.</b> Until Sprint 45 Track
    /// H7, a failed pending-record write reached the operator as a Warning
    /// line in the trace — a warning that a possibly unwritable log is the
    /// only place saying the log may be unrecoverable. Astra's ruling
    /// (<c>for-claude/2026-09-24-codex-design-pending-record-failure.md</c>):
    /// "Expose the facts independently, without requiring the failing disk to
    /// store the warning" — whether the current sink is recording, has failed,
    /// or was left off; whether an old ticket's raw file is available and
    /// whether its tail is complete; whether that ticket has a durable record,
    /// only its in-memory metadata, or a committed archive; and WHICH old
    /// session the limitation concerns, because a healthy new session does not
    /// erase an unresolved old ticket.</para>
    ///
    /// <para><b>Byte ownership, recovery state, current write health and
    /// archive commit are independent facts</b> and stay independently
    /// visible here. A successor recording perfectly says nothing about
    /// whether the session before it will be recoverable after a crash.</para>
    ///
    /// <para><b>Published outside the coordinator's gate, always.</b> Every
    /// note below is called after a transition has released the gate, or from
    /// the archive worker, or from a pool thread the router scheduled. The
    /// <see cref="Changed"/> event is raised outside this class's own lock
    /// too, so a subscriber may read <see cref="Snapshot"/> freely.</para>
    ///
    /// <para><b>Deduplicated.</b> <see cref="Changed"/> fires when a condition
    /// is RAISED for a ticket, when it is RESOLVED, and when the sink's state
    /// changes — never for a repeat of something already reported.</para>
    /// </summary>
    public static class TraceRecordingHealth
    {
        private static readonly object _sync = new object();
        private static readonly Dictionary<Guid, TraceRecoveryCondition> _unresolved =
            new Dictionary<Guid, TraceRecoveryCondition>();
        private static readonly List<Guid> _order = new List<Guid>();
        private static TraceSinkState _sinkState = TraceSinkState.Off;
        private static string _sinkFault;
        private static string _sinkPath;
        private static Guid _liveSession;

        /// <summary>The sink generation the state above describes. See
        /// <see cref="NoteSink"/>.</summary>
        private static long _generation;
        private static int _historicalFailures;
        private static string _lastFailure;
        private static DateTime? _lastFailureUtc;
        private static int _orphansAdopted;

        /// <summary>
        /// Raised, outside every lock, when the health changes in a way the
        /// operator has not been told about yet. Subscribers must not throw;
        /// anything that escapes is swallowed, because the reporting path must
        /// never be able to fail the recording path.
        /// </summary>
        public static event Action<TraceRecordingHealthChange> Changed;

        public static TraceRecordingHealthSnapshot Snapshot()
        {
            lock (_sync) { return SnapshotLocked(); }
        }

        private static TraceRecordingHealthSnapshot SnapshotLocked()
        {
            var list = new List<TraceRecoveryCondition>(_order.Count);
            foreach (Guid id in _order)
            {
                if (_unresolved.TryGetValue(id, out TraceRecoveryCondition c)) list.Add(c.Clone());
            }
            return new TraceRecordingHealthSnapshot
            {
                SinkState = _sinkState,
                SinkFault = _sinkFault,
                SinkPath = _sinkPath,
                LiveSessionId = _liveSession,
                Unresolved = list,
                HistoricalFailures = _historicalFailures,
                LastFailure = _lastFailure,
                LastFailureUtc = _lastFailureUtc,
                OrphansAdoptedAtBoot = _orphansAdopted,
            };
        }

        // ── Notes from the coordinator and the worker ──────────────────────

        /// <summary>
        /// A session (or checkpoint part) was detached. If its durable record
        /// did not write, or its terminal records did not all land, that
        /// ticket is now a condition. Called after the gate is released and
        /// BEFORE the ticket is queued, so the operator can be told before any
        /// wait on compression.
        /// </summary>
        internal static void NoteDetached(TraceArchiveTicket ticket)
        {
            if (ticket == null) return;
            bool atRisk = !ticket.PendingRecordWritten || ticket.TailUncertain;
            if (!atRisk) return;

            TraceRecordingHealthChange change = null;
            lock (_sync)
            {
                if (!_unresolved.ContainsKey(ticket.TicketId))
                {
                    var condition = new TraceRecoveryCondition
                    {
                        TicketId = ticket.TicketId,
                        SessionId = ticket.SessionId,
                        PartNumber = ticket.PartNumber,
                        IsCheckpoint = ticket.IsCheckpoint,
                        RawPath = ticket.SourcePath,
                        RawRetained = TraceArchiveWorker.SafeExists(ticket.SourcePath),
                        TailUncertain = ticket.TailUncertain,
                        SinkFault = ticket.SinkFault,
                        RecoveryRecordWritten = ticket.PendingRecordWritten,
                        NotedUtc = DateTime.UtcNow,
                    };
                    _unresolved[ticket.TicketId] = condition;
                    _order.Add(ticket.TicketId);
                    RecordFailureLocked(!ticket.PendingRecordWritten
                        ? "pending record not written for " + ticket.SourcePath
                        : "terminal records may not have reached " + ticket.SourcePath + ": " + ticket.SinkFault);
                    change = new TraceRecordingHealthChange
                    {
                        Kind = TraceRecordingHealthChangeKind.ConditionRaised,
                        Condition = condition.Clone(),
                        Snapshot = SnapshotLocked(),
                    };
                }
            }
            Raise(change);
        }

        /// <summary>
        /// The worker's retry wrote the durable record. That ticket's recovery
        /// is assured by the next launch, so its condition clears — even if
        /// its tail was uncertain, which stays in the historical record. The
        /// resolved clone keeps <see cref="TraceRecoveryCondition.RecoveryRecordWritten"/>
        /// false (what was wrong) and says how it resolved on
        /// <see cref="TraceRecoveryCondition.RecoveryRecordPersisted"/>.
        /// </summary>
        internal static void NoteRecoveryRecordPersisted(TraceArchiveTicket ticket)
        {
            if (ticket == null) return;
            Resolve(ticket.TicketId, c => c.RecoveryRecordPersisted = true);
        }

        /// <summary>
        /// The worker finished with a ticket. Committed clears its condition;
        /// anything else raises or updates one, with the failure stage.
        /// </summary>
        internal static void NoteArchiveOutcome(TraceArchiveTicket ticket, TraceArchiveCompletion completion)
        {
            if (ticket == null || completion == null) return;
            if (completion.ArchiveCommitted)
            {
                Resolve(ticket.TicketId, c => c.ArchiveCommitted = true);
                return;
            }

            TraceRecordingHealthChange change = null;
            lock (_sync)
            {
                bool isNew = !_unresolved.TryGetValue(ticket.TicketId, out TraceRecoveryCondition condition);
                if (isNew)
                {
                    condition = new TraceRecoveryCondition
                    {
                        TicketId = ticket.TicketId,
                        SessionId = ticket.SessionId,
                        PartNumber = ticket.PartNumber,
                        IsCheckpoint = ticket.IsCheckpoint,
                        RawPath = ticket.SourcePath,
                        TailUncertain = ticket.TailUncertain,
                        SinkFault = ticket.SinkFault,
                        RecoveryRecordWritten = ticket.PendingRecordWritten,
                        NotedUtc = DateTime.UtcNow,
                    };
                    _unresolved[ticket.TicketId] = condition;
                    _order.Add(ticket.TicketId);
                }
                bool becameArchiveFailure = condition.ArchiveFailureStage == null;
                condition.RawRetained = completion.RawRetained;
                condition.ArchiveFailureStage = completion.FailureStage ?? "unknown";
                condition.ArchiveFailureMessage = completion.FailureMessage;
                RecordFailureLocked("archive not committed (" + (completion.FailureStage ?? "unknown")
                                    + ") for " + ticket.SourcePath);
                if (isNew || becameArchiveFailure)
                {
                    // New: the operator has not heard of this ticket. Existing
                    // and newly failed: they have, and what they were told —
                    // that it is still being filed — is no longer true.
                    change = new TraceRecordingHealthChange
                    {
                        Kind = isNew ? TraceRecordingHealthChangeKind.ConditionRaised
                                     : TraceRecordingHealthChangeKind.ConditionUpdated,
                        Condition = condition.Clone(),
                        Snapshot = SnapshotLocked(),
                    };
                }
            }
            Raise(change);
        }

        private static void Resolve(Guid ticketId, Action<TraceRecoveryCondition> update)
        {
            TraceRecordingHealthChange change = null;
            lock (_sync)
            {
                if (_unresolved.TryGetValue(ticketId, out TraceRecoveryCondition condition))
                {
                    update?.Invoke(condition);
                    _unresolved.Remove(ticketId);
                    _order.Remove(ticketId);
                    change = new TraceRecordingHealthChange
                    {
                        Kind = TraceRecordingHealthChangeKind.ConditionResolved,
                        Condition = condition.Clone(),
                        Snapshot = SnapshotLocked(),
                    };
                }
            }
            Raise(change);
        }

        /// <summary>
        /// What the live sink is doing. Raised as a change only when the state
        /// actually moves, so a hundred seals that each open a healthy
        /// successor say nothing.
        ///
        /// <para><b>Notes are applied in sink order, not arrival order.</b>
        /// Every note carries the coordinator's generation of the sink it
        /// describes. The coordinator reports a live sink's write fault from a
        /// pool thread, and a transition can publish a successor's Recording
        /// state before that thread runs; until Track H8 the late note simply
        /// overwrote the newer state, so Diagnostics said the log had failed
        /// while the successor was recording (Sol's review of H7, finding 2).
        /// Now a note about an OLDER generation than the one held is history —
        /// a failure is counted and kept as the last failure, visible in the
        /// support snapshot — and moves neither the state nor a
        /// <see cref="TraceRecordingHealthChangeKind.SinkFailed"/> change. A
        /// note about a newer generation replaces the state. Within ONE
        /// generation Failed is absorbing: a sink that has failed does not
        /// become Recording again because the transition that opened it
        /// published its Recording note a moment late.</para>
        /// </summary>
        internal static void NoteSink(TraceSinkState state, string fault, string path, Guid liveSession,
                                      long generation)
        {
            TraceRecordingHealthChange change = null;
            lock (_sync)
            {
                bool older = generation < _generation;
                bool sameButAlreadyFailed = generation == _generation
                                            && _sinkState == TraceSinkState.Failed
                                            && state != TraceSinkState.Failed;
                if (older || sameButAlreadyFailed)
                {
                    if (state == TraceSinkState.Failed)
                    {
                        RecordFailureLocked("the trace sink for session " + liveSession + " failed: "
                                            + (fault ?? "unknown failure")
                                            + (string.IsNullOrEmpty(path) ? string.Empty : " at " + path)
                                            + " (reported after a newer sink was published;"
                                            + " the live state is unchanged)");
                    }
                    return;
                }
                _generation = generation;

                bool moved = state != _sinkState
                             || (state == TraceSinkState.Failed && !string.Equals(fault, _sinkFault, StringComparison.Ordinal));
                _sinkState = state;
                _sinkFault = state == TraceSinkState.Failed ? (fault ?? "unknown failure") : null;
                _sinkPath = path;
                _liveSession = liveSession;
                if (state == TraceSinkState.Failed && moved)
                {
                    RecordFailureLocked("the live trace sink failed: " + _sinkFault
                                        + (string.IsNullOrEmpty(path) ? string.Empty : " at " + path));
                }
                if (moved)
                {
                    change = new TraceRecordingHealthChange
                    {
                        Kind = state == TraceSinkState.Failed ? TraceRecordingHealthChangeKind.SinkFailed
                             : state == TraceSinkState.Recording ? TraceRecordingHealthChangeKind.SinkRecording
                             : TraceRecordingHealthChangeKind.SinkOff,
                        Snapshot = SnapshotLocked(),
                    };
                }
            }
            Raise(change);
        }

        /// <summary>
        /// Boot adoption filed raw files under an inventory identity because
        /// nothing said whose they were. A fact for the Diagnostics read; not
        /// a change the operator is announced to, because it describes a
        /// previous run and the files are kept either way.
        /// </summary>
        internal static void NoteOrphansAdopted(int count)
        {
            if (count <= 0) return;
            lock (_sync) { _orphansAdopted += count; }
        }

        private static void RecordFailureLocked(string description)
        {
            _historicalFailures++;
            _lastFailure = description;
            _lastFailureUtc = DateTime.UtcNow;
        }

        private static void Raise(TraceRecordingHealthChange change)
        {
            if (change == null) return;
            try { Changed?.Invoke(change); }
            catch
            {
                // A subscriber's failure must not become the recording path's.
            }
        }

        /// <summary>Tests only: forget everything.</summary>
        internal static void ResetForTests()
        {
            lock (_sync)
            {
                _unresolved.Clear();
                _order.Clear();
                _sinkState = TraceSinkState.Off;
                _sinkFault = null;
                _sinkPath = null;
                _liveSession = Guid.Empty;
                _generation = 0;
                _historicalFailures = 0;
                _lastFailure = null;
                _lastFailureUtc = null;
                _orphansAdopted = 0;
            }
        }
    }
}
