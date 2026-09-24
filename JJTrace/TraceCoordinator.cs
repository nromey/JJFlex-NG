using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace JJTrace
{
    /// <summary>
    /// Immutable identity carried INTO a sink, so rotation never has to look a
    /// session up from inside its own lock.
    /// </summary>
    internal sealed class TraceSinkStamp
    {
        internal TraceSinkStamp(TraceSession session, string archiveRootDir, string livePath)
        {
            Session = session;
            ArchiveRootDir = archiveRootDir;
            LivePath = livePath;
        }

        internal TraceSession Session { get; }
        internal string ArchiveRootDir { get; }
        internal string LivePath { get; }
        internal Guid SessionId => Session?.SessionId ?? Guid.Empty;
        internal DateTime BootTimeUtc => Session?.BootTimeUtc ?? DateTime.UtcNow;
    }

    /// <summary>What a caller is asking the boundary to do.</summary>
    public sealed class TraceSealRequest
    {
        /// <summary>The recording this operation is ABOUT. Null is only legal
        /// with <see cref="ShutdownAuthority"/>.</summary>
        public TraceSessionHandle Expected { get; set; }

        /// <summary>
        /// Process shutdown, and nothing else, may seal whichever session
        /// happens to remain. Exit intentionally closes all recording, so it is
        /// the one caller that does not name a session — and it says so here
        /// rather than being implemented as an unlocked read of the current
        /// pointer followed by a generic helper.
        /// </summary>
        public bool ShutdownAuthority { get; set; }

        public string Outcome { get; set; }
        public string OutcomeDetail { get; set; }

        /// <summary>
        /// Lines written into the OWNED sink, in order, before the terminal
        /// state marker. This is how a caller's last observation reaches the
        /// file it belongs to: collected by the caller, handed over as data,
        /// and written only if this operation wins. A losing caller's line is
        /// discarded rather than landing in somebody else's log.
        /// </summary>
        public IReadOnlyList<string> TerminalLines { get; set; }

        public TraceResumeIntent Resume { get; set; } = TraceResumeIntent.Standing;

        /// <summary>Level for <see cref="TraceResumeIntent.Explicit"/>.</summary>
        public TraceLevel ResumeLevel { get; set; } = TraceLevel.Info;

        /// <summary>
        /// Identity of the operation, so a second call from the same caller
        /// gets the same ticket instead of repeating the effects. The drop uses
        /// its claim, Stop uses the capture id, shutdown uses a fixed id shared
        /// by both exit hooks.
        /// </summary>
        public Guid OperationId { get; set; }

        /// <summary>When set, the transition is refused unless this capture is
        /// the one running. Stop reads identity and handle together.</summary>
        public Guid ExpectedCaptureId { get; set; }

        /// <summary>True when the caller requires a capture to be running —
        /// Stop, and only Stop.</summary>
        public bool RequireCaptureRunning { get; set; }

        /// <summary>
        /// The successor this transition opens IS the operator's detailed
        /// capture. Its identity, start time and completed-capture slot are
        /// set inside the same transition that opens it.
        ///
        /// <para><b>Why this is not a second call.</b> Capture start used to
        /// seal, open a successor, release the gate, and THEN mark that
        /// successor as the capture. Anything landing in between — a drop, a
        /// Stop, a settings change, an exit — acted on a session that was about
        /// to become a capture but was not one yet: a drop sealed it as an
        /// ordinary session and the capture was then marked on nothing, or a
        /// Stop found no capture running (Sol's review of H3, finding 2). A
        /// successor is published only once it is complete, so nobody can see
        /// it half made.</para>
        /// </summary>
        public bool SuccessorIsCapture { get; set; }

        /// <summary>Start time for that capture, local clock; now when
        /// null.</summary>
        public DateTime? CaptureStartedLocal { get; set; }
    }

    /// <summary>
    /// The one place a trace session begins, changes or ends.
    ///
    /// <para><b>What it owns.</b> The current session handle, the sink that
    /// session's bytes go to, the live path, the detail level, the rotation
    /// position, the detailed capture's identity and start time, the operator's
    /// standing-log intent, the shutdown latch, and the identity of every
    /// ticket already handed out. Those things used to be seven independent
    /// controls that four callers set in sequence, which is why a caller could
    /// read one session, close a shared listener, and clear a different
    /// session's pointer (#612).</para>
    ///
    /// <para><b>What happens under the gate.</b> Validate ownership, write the
    /// terminal records to the owned sink, freeze the metadata and the rotation
    /// position, close and move the file to a unique retained path, and publish
    /// either the successor or the real off/faulted state. Capture state and
    /// shutdown intent belong to that same transition, because a capture that
    /// is "running" against a session that has been archived is the same defect
    /// wearing different clothes.</para>
    ///
    /// <para><b>What never happens under the gate:</b> compression. The
    /// boundary returns a ticket and the zip is made on the archive worker
    /// afterwards. This runs during a teardown, on a file that can be
    /// megabytes, and a lock held across LZMA would stall every trace line in
    /// the process while the radio is dying. The boundary also never calls
    /// <c>Trace.WriteLine</c>, <c>Trace.Flush</c>, <c>Trace.Close</c>, never
    /// changes <c>Trace.Listeners</c>, never logs an exception through
    /// <see cref="Tracing"/>, never invokes a caller's delegate and never waits
    /// on a task — all of which would take the framework's trace lock in the
    /// opposite order. Faults are collected and published afterwards.</para>
    /// </summary>
    public static class TraceCoordinator
    {
        /// <summary>
        /// The gate. Shared with <see cref="TraceRouterListener"/>, which is
        /// the point: a write and a sink switch cannot interleave.
        /// </summary>
        private static readonly object _gate = new object();

        private static RotatingTraceListener _sink;
        private static TraceSession _session;
        private static TraceSessionHandle _handle;

        /// <summary>
        /// The current handle as last PUBLISHED, readable without the gate.
        ///
        /// <para><b>Why a second field.</b> The gate is held across a
        /// transition's flush, close, move, pending-record write and successor
        /// open — file I/O, which can stall. The drop callback runs on FlexLib's
        /// transport thread and has to name the session it is about at the
        /// moment of the drop; reading <see cref="_handle"/> under the gate made
        /// that thread wait out whatever the disk was doing (Sol's review of
        /// H3, finding 1; Astra's design asked for exactly this snapshot).</para>
        ///
        /// <para><b>Published at the END of a transition, never part way
        /// through.</b> A seal nulls <see cref="_handle"/> before it opens the
        /// successor; a reader that could see that intermediate null would
        /// report "nothing was recording" during a perfectly ordinary restart.
        /// So a lock-free reader sees the handle from before a transition or the
        /// one after it, nothing in between. A handle is immutable, so reading
        /// the reference is reading the whole value.</para>
        ///
        /// <para>It is a request TARGET, not permission: whoever acts on it
        /// still goes through the boundary, which compares it against the real
        /// current session under the gate.</para>
        /// </summary>
        private static volatile TraceSessionHandle _published;

        /// <summary>Caller must hold the gate. Called on the way OUT of every
        /// gated operation that can change the current handle.</summary>
        private static void PublishLocked() => _published = _handle;

        /// <summary>
        /// Tests only: called at named points INSIDE a transition, with the gate
        /// held, so a test can hold the gate through a slow transition on a
        /// barrier and race something against it. Null in production, and the
        /// one exception to the rule that nothing runs a caller's delegate under
        /// the gate — which is why it is internal and named for tests.
        /// </summary>
        internal static Action<string> TransitionProbeForTests;

        private static void Probe(string point) => TransitionProbeForTests?.Invoke(point);
        private static TraceSinkStamp _stamp;
        private static string _livePath;
        private static TraceLevel _level = TraceLevel.Off;

        private static bool _shuttingDown;

        private static Guid _captureId;
        private static Guid _captureSessionId;
        private static DateTime? _captureStartedLocal;

        // ── The completed-capture slot ─────────────────────────────────────
        //
        // Which capture the operator's "export this capture" surface is about,
        // and the archive path it can offer. ONE value, under ONE lock (Sol's
        // review of H3, finding 3). The application used to keep these as two
        // fields: a completion checked the slot, then assigned the path in a
        // separate statement, while starting a capture reset both. So an old
        // completion could pass its check, lose the processor to a new capture's
        // reset, and then write the OLD capture's path into the NEW capture's
        // slot — pointing Export at the wrong evening.
        //
        // Its own small lock rather than the gate: a completion arrives on the
        // archive worker and has no business waiting on a transition's file I/O.
        // Lock order is gate, then this — a capture start claims the slot from
        // inside its transition — and nothing holding this lock ever takes the
        // gate.
        private static readonly object _slotLock = new object();
        private static Guid _slotCaptureId;
        private static string _slotArchivePath;

        /// <summary>
        /// Tests only: runs INSIDE <see cref="RecordCaptureArchive"/>, between
        /// the check that the slot still names this capture and the write of
        /// its path — the exact interleaving point of the defect this lock
        /// closes. Null in production.
        /// </summary>
        internal static Action<Guid> BeforeCaptureSlotWriteForTests;

        private static bool _standingKeep;
        private static TraceLevel _standingLevel = TraceLevel.Info;

        private static readonly Dictionary<Guid, TraceArchiveTicket> _ticketsByOperation =
            new Dictionary<Guid, TraceArchiveTicket>();
        private static readonly Dictionary<Guid, TraceArchiveTicket> _ticketsBySession =
            new Dictionary<Guid, TraceArchiveTicket>();

        /// <summary>The id both exit hooks share, so the second one gets the
        /// first one's ticket rather than sealing again.</summary>
        internal static readonly Guid ShutdownOperationId =
            new Guid("5e3b1f84-9a2c-4f7d-9b61-0f2c6f9a71d3");

        /// <summary>
        /// Unchanging facts the terminal record needs. Set once at boot, before
        /// anything can seal.
        /// </summary>
        public static TraceEnvironment AppIdentity { get; set; } = new TraceEnvironment();

        /// <summary>Where sealed sessions get compressed to. Set once at boot.</summary>
        public static string ArchiveRootDir { get; set; }

        // ── Observation ────────────────────────────────────────────────────

        /// <summary>The live session, or null. A read, never a claim.</summary>
        public static TraceSession CurrentSession
        {
            get { lock (_gate) { return _session; } }
        }

        /// <summary>
        /// The live session's handle, or null — as last published, WITHOUT
        /// taking the gate. Safe on a thread that must not wait on a file: the
        /// drop callback reads this on FlexLib's transport thread. A transition
        /// in progress is reported as the state before it; see
        /// <see cref="_published"/>.
        /// </summary>
        public static TraceSessionHandle CurrentHandle => _published;

        /// <summary>Where lines are landing right now, or null.</summary>
        public static string LivePath
        {
            get { lock (_gate) { return _sink != null ? _sink.FilePath : null; } }
        }

        public static bool Recording
        {
            get { lock (_gate) { return _sink != null && !_sink.IsClosed; } }
        }

        public static bool ShuttingDown
        {
            get { lock (_gate) { return _shuttingDown; } }
        }

        public static bool CaptureRunning
        {
            get { lock (_gate) { return _captureStartedLocal.HasValue; } }
        }

        public static DateTime? CaptureStartedLocal
        {
            get { lock (_gate) { return _captureStartedLocal; } }
        }

        public static Guid CaptureId
        {
            get { lock (_gate) { return _captureId; } }
        }

        /// <summary>
        /// The archive path of the capture that owns the completed-capture
        /// slot, or null — cleared the moment a new capture starts, and filled
        /// only by that capture's own committed archive. What the "export this
        /// capture" surface offers.
        /// </summary>
        public static string CompletedCaptureArchivePath
        {
            get { lock (_slotLock) { return _slotArchivePath; } }
        }

        /// <summary>The capture the completed-capture slot belongs to, or
        /// <see cref="Guid.Empty"/>.</summary>
        public static Guid CompletedCaptureSlotId
        {
            get { lock (_slotLock) { return _slotCaptureId; } }
        }

        /// <summary>
        /// Record a capture's committed archive in the completed-capture slot —
        /// if, and only if, the slot still belongs to that capture. The check
        /// and the write are one step, so a newly started capture's reset
        /// cannot fall between them.
        /// </summary>
        /// <returns>True when the path was recorded; false when the slot has
        /// moved on to another capture, which is not a failure.</returns>
        public static bool RecordCaptureArchive(Guid captureId, string archivePath)
        {
            if (captureId == Guid.Empty) return false;
            lock (_slotLock)
            {
                if (_slotCaptureId != captureId) return false;
                BeforeCaptureSlotWriteForTests?.Invoke(captureId);
                _slotArchivePath = archivePath;
                return true;
            }
        }

        /// <summary>One immutable snapshot of everything above, taken together.
        /// Six separate property reads can describe six different moments; this
        /// cannot.</summary>
        public static TraceObservation Observe()
        {
            lock (_gate)
            {
                return new TraceObservation
                {
                    Handle = _handle,
                    Recording = _sink != null && !_sink.IsClosed,
                    LivePath = _sink?.FilePath,
                    Level = _level,
                    ShuttingDown = _shuttingDown,
                    CaptureRunning = _captureStartedLocal.HasValue,
                    CaptureId = _captureId,
                    CaptureStartedLocal = _captureStartedLocal,
                    PartNumber = _sink?.PartNumber ?? 1,
                    HasParts = _sink?.HasRotated ?? false,
                    SessionBootTimeUtc = _session?.BootTimeUtc,
                };
            }
        }

        // ── Routing ────────────────────────────────────────────────────────

        internal static void RouteWrite(string message)
        {
            if (message == null) return;
            lock (_gate) { _sink?.Write(message); }
        }

        internal static void RouteWriteLine(string message)
        {
            if (message == null) return;
            lock (_gate) { _sink?.WriteLine(message); }
        }

        internal static void RouteFlush()
        {
            lock (_gate) { _sink?.Flush(); }
        }

        /// <summary>The live sink, for the rotation surface on
        /// <see cref="Tracing"/>. Null when nothing is recording.</summary>
        internal static RotatingTraceListener Sink
        {
            get { lock (_gate) { return _sink; } }
        }

        // ── The unmanaged sink, for the console tools ──────────────────────
        //
        // RadioInTheLoop and the SmartLink harness have no session lifecycle:
        // they want a file and they want lines in it. They get a sink with no
        // session behind it, through the same gate and the same router, so the
        // lock discipline is identical and there is still exactly one place a
        // sink is opened or closed.

        internal static void OpenUnmanagedSink(string path)
        {
            var faults = new List<string>();
            lock (_gate)
            {
                if (_session != null) return;
                CloseSinkLocked();
                OpenSessionLocked(path, _level, faults, startPartNumber: 1,
                                  continuing: null);
                // No managed session: drop the pointer the open just set, so
                // nothing mistakes a console tool's file for a lifecycle
                // session it could seal.
                _session = null;
                _handle = null;
                PublishLocked();
            }
        }

        internal static void CloseUnmanagedSink()
        {
            lock (_gate)
            {
                if (_session != null) return;
                CloseSinkLocked();
            }
        }

        private static void CloseSinkLocked()
        {
            if (_sink == null) return;
            try { _sink.FlushAndClose(out _); } catch { }
            _sink = null;
            _stamp = null;
        }

        // ── Standing intent and shutdown ───────────────────────────────────

        /// <summary>
        /// The operator's standing choice — whether they keep a log at all, and
        /// at what detail. The boundary consults this when a transition asks
        /// for <see cref="TraceResumeIntent.Standing"/>, so a successor is
        /// opened because the operator wants one, not because a worker assumed
        /// it.
        /// </summary>
        public static void SetStandingIntent(bool keepLog, TraceLevel level)
        {
            lock (_gate)
            {
                _standingKeep = keepLog;
                _standingLevel = level;
            }
        }

        /// <summary>
        /// Exit is committed: after any chance to cancel it and before teardown
        /// begins. New captures, log-enable requests and restarts are refused
        /// from here on. The existing session stays writable so the closing
        /// evidence can still be recorded.
        /// </summary>
        public static void LatchShutdown()
        {
            lock (_gate) { _shuttingDown = true; }
        }

        /// <summary>Tests only. Shutdown is a one-way latch in production.</summary>
        internal static void UnlatchShutdownForTests()
        {
            lock (_gate) { _shuttingDown = false; }
        }

        // ── Beginning a session ────────────────────────────────────────────

        /// <summary>
        /// Open a sink at <paramref name="livePath"/> and begin a session on it.
        /// Refused while shutting down, and refused while a session is already
        /// recording — nothing may flip the switch without settling what was
        /// already open.
        /// </summary>
        public static TraceTransitionResult Begin(string livePath,
                                                  TraceLevel level,
                                                  bool asDetailedCapture,
                                                  DateTime? captureStartedLocal = null)
        {
            var faults = new List<string>();
            TraceTransitionResult result;
            lock (_gate)
            try
            {
                if (_shuttingDown)
                {
                    return TraceTransitionResult.Refusal(TraceTransition.ShuttingDown,
                        Guid.Empty, _session?.SessionId ?? Guid.Empty,
                        "TraceCoordinator.Begin refused: exit is committed", Recording);
                }
                if (_session != null || (_sink != null && !_sink.IsClosed))
                {
                    return TraceTransitionResult.Refusal(TraceTransition.AlreadyRecording,
                        Guid.Empty, _session?.SessionId ?? Guid.Empty,
                        "TraceCoordinator.Begin refused: session "
                        + (_session?.SessionId.ToString() ?? "(none)") + " is still recording",
                        true);
                }

                result = OpenSessionLocked(livePath, level, faults);
                if (result.Status == TraceTransition.Accepted && asDetailedCapture)
                {
                    result.StartedCaptureId = StartCaptureLocked(captureStartedLocal);
                }
                result.TracingOn = _sink != null && !_sink.IsClosed;
            }
            finally { PublishLocked(); }
            result.DeferredFaults = faults;
            return result;
        }

        /// <summary>
        /// Make the session just opened the operator's detailed capture, and
        /// give that capture the completed-capture slot. Caller must hold the
        /// gate, and must call this in the SAME transition that opened the
        /// session — there is deliberately no public way to mark a session as a
        /// capture after the fact, because that gap is where a drop or a Stop
        /// used to land (Sol's review of H3, finding 2; this replaces H3's
        /// <c>MarkSuccessorAsCapture</c>).
        /// </summary>
        /// <returns>The new capture's identity.</returns>
        private static Guid StartCaptureLocked(DateTime? startedLocal)
        {
            _captureId = Guid.NewGuid();
            _captureSessionId = _session.SessionId;
            _captureStartedLocal = startedLocal ?? DateTime.Now;
            lock (_slotLock)
            {
                _slotCaptureId = _captureId;
                _slotArchivePath = null;
            }
            return _captureId;
        }

        /// <summary>
        /// Begin a session with no file behind it. The key-event and outcome
        /// surface still works, nothing is written anywhere. Used by tests and
        /// by any run where tracing is gated off but the lifecycle still wants
        /// a session object.
        /// </summary>
        internal static TraceSession BeginSessionOnly()
        {
            lock (_gate)
            {
                _session = new TraceSession();
                _handle = new TraceSessionHandle(_session);
                _session.VerbosityLevel = _level.ToString();
                PublishLocked();
                return _session;
            }
        }

        /// <summary>
        /// Drop the session pointer without touching any sink. Tests and the
        /// no-file path only; every production end goes through the boundary.
        /// </summary>
        internal static TraceSession EndSessionOnly()
        {
            lock (_gate)
            {
                TraceSession ending = _session;
                ending?.End();
                _session = null;
                _handle = null;
                _stamp = null;
                PublishLocked();
                return ending;
            }
        }

        /// <summary>
        /// Put the session pointer back without stamping an end time on a
        /// session the caller never opened. Tests only — the process-wide
        /// pointer is exactly what the lifecycle tests have to drive.
        /// </summary>
        internal static void RestoreSessionForTests(TraceSession session)
        {
            lock (_gate)
            {
                _session = session;
                _handle = session == null ? null : new TraceSessionHandle(session);
                if (session == null) _stamp = null;
                PublishLocked();
            }
        }

        /// <summary>Tests only: forget every ticket and capture claim.</summary>
        internal static void ResetClaimsForTests()
        {
            lock (_gate)
            {
                _ticketsByOperation.Clear();
                _ticketsBySession.Clear();
                _captureId = Guid.Empty;
                _captureSessionId = Guid.Empty;
                _captureStartedLocal = null;
                lock (_slotLock)
                {
                    _slotCaptureId = Guid.Empty;
                    _slotArchivePath = null;
                }
            }
        }

        /// <summary>Caller must hold the gate.</summary>
        private static TraceTransitionResult OpenSessionLocked(string livePath, TraceLevel level,
                                                               List<string> faults,
                                                               int startPartNumber = 1,
                                                               TraceSession continuing = null,
                                                               bool append = false)
        {
            var result = new TraceTransitionResult();
            try
            {
                TraceSession session = continuing ?? new TraceSession();
                var stamp = new TraceSinkStamp(session, ArchiveRootDir, livePath);
                var sink = new RotatingTraceListener(
                    livePath,
                    Tracing.RotationThresholdBytes,
                    partNumber => ResolvePartPathFor(stamp, partNumber),
                    (path, part) => OnPartClosedFor(stamp, path, part),
                    stamp,
                    startPartNumber,
                    append);

                _sink = sink;
                _session = session;
                _handle = new TraceSessionHandle(session);
                _stamp = stamp;
                _livePath = livePath;
                _level = level;
                if (continuing == null) session.VerbosityLevel = level.ToString();

                // Opening a sink turns emission on. It has to happen here
                // rather than at each call site: Tracing.On is the gate every
                // TraceLine tests, and a caller that opened a session without
                // raising it would write to a file nothing ever reached. That
                // is not hypothetical — the standing log turned on from
                // Settings after a launch with logging off would have been
                // silent, because boot was the only place that raised it.
                //
                // Nothing here ever lowers it. Turning emission off is the
                // console tools' and the tests' business, and the old coupling
                // of "stop emitting" to "close this session's file" is exactly
                // what let one caller close another's trace.
                Tracing.On = true;

                result.Status = TraceTransition.Accepted;
                result.Successor = _handle;
                result.TracingOn = true;
            }
            catch (Exception ex)
            {
                faults.Add("TraceCoordinator: could not open a trace at " + livePath + ": " + ex.Message);
                _sink = null;
                _session = null;
                _handle = null;
                _stamp = null;
                result.Status = TraceTransition.Failed;
                result.FailedStage = "open";
                result.TracingOn = false;
            }
            return result;
        }

        // ── Rotation callbacks, bound to immutable identity ────────────────

        private static string ResolvePartPathFor(TraceSinkStamp stamp, int partNumber)
        {
            // Nothing here reads the coordinator. The stamp was fixed when the
            // sink was built, so a rotation that races a lifecycle transition
            // still names its own session's part.
            return TraceFileNaming.StampedPartPath(stamp.LivePath, stamp.BootTimeUtc, partNumber);
        }

        private static void OnPartClosedFor(TraceSinkStamp stamp, string partPath, int partNumber)
        {
            // Runs inside the sink's own lock. It must not block and it must
            // not acquire the gate: it freezes the part's identity and hands it
            // to the archive worker, which is a queue push.
            Tracing.NotePartClosed(partPath);
            if (string.IsNullOrEmpty(partPath)) return;

            TraceSession session = stamp.Session;
            string fileTag = session != null ? session.ResolvePartFileTag() : TraceSessionOutcome.Unknown;
            TraceSessionEntry entry = session != null
                ? FreezeEntry(session, isFinalPart: false)
                : null;
            if (entry == null) return;

            var ticket = new TraceArchiveTicket
            {
                SessionId = stamp.SessionId,
                PartNumber = partNumber,
                IsFinalPart = false,
                SourcePath = partPath,
                ArchiveRootDir = stamp.ArchiveRootDir,
                Entry = entry,
                OutcomeFileTag = fileTag,
                StampLocal = stamp.BootTimeUtc.ToLocalTime(),
            };
            TraceArchiveWorker.Queue(ticket);
        }

        // ── The terminal transition ────────────────────────────────────────

        /// <summary>
        /// Seal the session this caller was asked about: write its last
        /// records, freeze it, detach its bytes, and publish either a successor
        /// or the real off state. Returns a ticket; the zip happens later, off
        /// this thread and outside this lock.
        /// </summary>
        public static TraceTransitionResult TrySeal(TraceSealRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var faults = new List<string>();
            TraceTransitionResult result;
            TraceArchiveTicket queued = null;

            lock (_gate)
            {
                try { result = SealLocked(request, faults, out queued); }
                finally { PublishLocked(); }
            }

            // Outside the gate, deliberately: queueing touches a Task chain and
            // the design forbids waiting on anything while the gate is held.
            if (queued != null) TraceArchiveWorker.Queue(queued);

            result.DeferredFaults = faults;
            return result;
        }

        private static TraceTransitionResult SealLocked(TraceSealRequest request,
                                                        List<string> faults,
                                                        out TraceArchiveTicket queued)
        {
            queued = null;
            Guid expectedId = request.Expected?.SessionId ?? Guid.Empty;
            Guid observedId = _session?.SessionId ?? Guid.Empty;

            // An operation that already owns a ticket gets that ticket back and
            // nothing is repeated. This is what makes both exit hooks, and a
            // second Stop, idempotent rather than destructive.
            if (request.OperationId != Guid.Empty
                && _ticketsByOperation.TryGetValue(request.OperationId, out TraceArchiveTicket owned))
            {
                var already = new TraceTransitionResult
                {
                    Status = TraceTransition.AlreadyClaimed,
                    Ticket = owned,
                    ExpectedSessionId = expectedId,
                    ObservedSessionId = observedId,
                    TracingOn = _sink != null && !_sink.IsClosed,
                    Explanation = "TraceCoordinator: this operation already sealed session "
                                  + owned.SessionId + " — the same ticket is returned and nothing repeated",
                };
                return already;
            }

            if (_session == null || _sink == null || _sink.IsClosed)
            {
                return TraceTransitionResult.Refusal(TraceTransition.NoSession,
                    expectedId, observedId,
                    "TraceCoordinator: nothing was recording, so there was nothing to seal", false);
            }

            if (!request.ShutdownAuthority && request.Expected == null)
            {
                return TraceTransitionResult.Refusal(TraceTransition.NotCurrent,
                    expectedId, observedId,
                    "TraceCoordinator: a seal must name the session it is about; only shutdown may seal whatever remains",
                    true);
            }

            if (request.Expected != null && request.Expected.SessionId != _session.SessionId)
            {
                // A refusal, logged as a refusal. It says which session was
                // wanted and which is here — never that the CURRENT session
                // suffered whatever the caller was reporting.
                return TraceTransitionResult.Refusal(TraceTransition.NotCurrent,
                    expectedId, observedId,
                    "TraceCoordinator: refused — expected session " + expectedId
                    + ", observed " + observedId
                    + "; no listener closed, no capture state cleared, no logging restarted",
                    true);
            }

            if (request.RequireCaptureRunning && !_captureStartedLocal.HasValue)
            {
                return TraceTransitionResult.Refusal(TraceTransition.NotCurrent,
                    expectedId, observedId,
                    "TraceCoordinator: refused — no detailed capture is running", true);
            }

            if (request.ExpectedCaptureId != Guid.Empty && request.ExpectedCaptureId != _captureId)
            {
                return TraceTransitionResult.Refusal(TraceTransition.NotCurrent,
                    expectedId, observedId,
                    "TraceCoordinator: refused — expected capture " + request.ExpectedCaptureId
                    + ", observed " + _captureId, true);
            }

            TraceSession sealing = _session;
            RotatingTraceListener sink = _sink;

            if (!string.IsNullOrEmpty(request.Outcome))
            {
                sealing.MarkOutcome(request.Outcome, request.OutcomeDetail);
            }

            // The caller's last observations, written into the file they belong
            // to and nowhere else.
            if (request.TerminalLines != null)
            {
                foreach (string line in request.TerminalLines)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    sink.WriteTerminalLine(Tracing.TracePrefix() + line);
                }
            }

            // The seal marker, written BEFORE the rotation position is frozen
            // because it is itself a write. Rotation is suppressed for the
            // duration of a terminal write, so the number below cannot go stale
            // underneath us.
            sink.WriteTerminalLine(Tracing.TracePrefix()
                + TraceStateMarker.RenderTerminal(AppIdentity, sealing.BootTimeUtc, sink.FilePath));

            bool hadParts = sink.HasRotated;
            int finalPart = sink.PartNumber;
            string sourcePath = sink.FilePath;

            sealing.End();
            string fileTag = hadParts ? sealing.ResolvePartFileTag() : sealing.Outcome;
            TraceSessionEntry entry = FreezeEntry(sealing, isFinalPart: hadParts);

            if (!sink.FlushAndClose(out string closeFailure))
            {
                faults.Add("TraceCoordinator: the trace file did not close cleanly: " + closeFailure);
            }

            // Close and MOVE before a successor opens. The old path compressed
            // first and renamed afterwards, so the next FileMode.Create at the
            // live path could truncate the very bytes being read.
            string target = hadParts
                ? TraceFileNaming.StampedPartPath(_livePath, sealing.BootTimeUtc, finalPart)
                : TraceFileNaming.StampedPath(_livePath, sealing.BootTimeUtc);
            string detached = TraceFileNaming.Detach(sourcePath, target, deleteOnFailure: false,
                                                     out string moveFailure);
            Probe("seal:detached");

            // Whatever happened to the file, this session is over as far as
            // every writer is concerned.
            _sink = null;
            _session = null;
            _handle = null;
            _stamp = null;

            bool endedCapture = _captureSessionId == sealing.SessionId && _captureStartedLocal.HasValue;
            Guid endedCaptureId = endedCapture ? _captureId : Guid.Empty;
            DateTime? endedCaptureStarted = endedCapture ? _captureStartedLocal : null;
            if (endedCapture)
            {
                _captureStartedLocal = null;
                _captureId = Guid.Empty;
                _captureSessionId = Guid.Empty;
            }

            if (detached == null)
            {
                // The raw evidence is still at the live path and it is still the
                // only copy. Do not reopen there — that would truncate it — and
                // do not delete it to make room.
                faults.Add("TraceCoordinator: could not detach " + sourcePath
                           + " (" + moveFailure + "); the raw trace is retained where it is and nothing was archived");
                return new TraceTransitionResult
                {
                    Status = TraceTransition.Failed,
                    FailedStage = "detach",
                    RetainedSourcePath = sourcePath,
                    ExpectedSessionId = expectedId,
                    ObservedSessionId = observedId,
                    TracingOn = false,
                    EndedDetailedCapture = endedCapture,
                    EndedCaptureId = endedCaptureId,
                    EndedCaptureStartedLocal = endedCaptureStarted,
                    Explanation = "TraceCoordinator: seal of " + sealing.SessionId
                                  + " failed at detach; evidence retained at " + sourcePath,
                };
            }

            var ticket = new TraceArchiveTicket
            {
                SessionId = sealing.SessionId,
                PartNumber = hadParts ? finalPart : 0,
                IsFinalPart = hadParts,
                SourcePath = detached,
                ArchiveRootDir = ArchiveRootDir,
                Entry = entry,
                OutcomeFileTag = fileTag,
                StampLocal = sealing.BootTimeUtc.ToLocalTime(),
            };

            // Durable metadata beside the raw source, written BEFORE a successor
            // is published: if the process dies between here and the commit, the
            // next boot finds a pending record rather than an orphan the
            // plain-text sweep will eventually delete unread.
            bool recordWritten = NotePendingRecord(ticket, faults);

            _ticketsBySession[ticket.SessionId] = ticket;
            if (request.OperationId != Guid.Empty) _ticketsByOperation[request.OperationId] = ticket;

            var result = new TraceTransitionResult
            {
                Status = TraceTransition.Accepted,
                Ticket = ticket,
                PendingRecordFailed = !recordWritten,
                ExpectedSessionId = expectedId,
                ObservedSessionId = observedId,
                EndedDetailedCapture = endedCapture,
                EndedCaptureId = endedCaptureId,
                EndedCaptureStartedLocal = endedCaptureStarted,
                Explanation = "TraceCoordinator: sealed session " + sealing.SessionId
                              + " to " + detached,
            };

            // The successor, decided here and reported as a fact rather than
            // assumed by the caller afterwards.
            bool wantSuccessor =
                request.Resume == TraceResumeIntent.Explicit
                || (request.Resume == TraceResumeIntent.Standing && _standingKeep);

            if (_shuttingDown && wantSuccessor)
            {
                // A drop that wins during teardown may seal its own session. It
                // may not open a successor: exit intentionally closes all
                // recording, and a log started here would be a leftover file
                // the next boot reads as a killed session.
                wantSuccessor = false;
                result.Explanation += "; no successor opened because exit is committed";
            }

            if (wantSuccessor)
            {
                TraceLevel resumeLevel = request.Resume == TraceResumeIntent.Explicit
                    ? request.ResumeLevel
                    : _standingLevel;
                TraceTransitionResult opened = OpenSessionLocked(_livePath, resumeLevel, faults);
                if (opened.Status == TraceTransition.Accepted)
                {
                    result.Successor = opened.Successor;
                    result.TracingOn = true;
                    Probe("seal:successor-opened");
                    // Still inside the gate, and before the successor is
                    // published: nobody can see this session until it is
                    // already the capture.
                    if (request.SuccessorIsCapture)
                    {
                        result.StartedCaptureId = StartCaptureLocked(request.CaptureStartedLocal);
                    }
                }
                else
                {
                    result.RestartFailed = true;
                    result.TracingOn = false;
                }
            }
            else
            {
                _level = TraceLevel.Off;
                result.TracingOn = false;
            }

            queued = ticket;
            return result;
        }

        // ── The bundler's checkpoint ───────────────────────────────────────

        /// <summary>
        /// Freeze the current part of <paramref name="expected"/> and open the
        /// next part of the SAME session, returning a pinned snapshot the
        /// problem-report bundler can compress at its leisure.
        ///
        /// <para><b>A checkpoint, not an end.</b> The bundler only ever wanted
        /// the live file released; it ended the logical session because that was
        /// the only way to get it. So the snapshot carries no terminal
        /// <c>capture=off</c> record and makes no claim that the session
        /// finished — a detailed capture running across a problem report keeps
        /// its identity and its start time, and its duration stays
        /// coherent.</para>
        /// </summary>
        public static TraceTransitionResult SnapshotForBundle(TraceSessionHandle expected)
        {
            var faults = new List<string>();
            TraceArchiveTicket queued = null;
            TraceTransitionResult result;

            lock (_gate)
            {
                Guid expectedId = expected?.SessionId ?? Guid.Empty;
                Guid observedId = _session?.SessionId ?? Guid.Empty;

                if (_session == null || _sink == null || _sink.IsClosed)
                {
                    result = TraceTransitionResult.Refusal(TraceTransition.NoSession,
                        expectedId, observedId,
                        "TraceCoordinator: nothing was recording, so there is no snapshot to take", false);
                }
                else if (expected == null || expected.SessionId != _session.SessionId)
                {
                    // If that session was already sealed, its own ticket IS the
                    // evidence and the bundler may use it. Otherwise say plainly
                    // that no snapshot is available — never silently substitute
                    // a later session's trace.
                    if (expectedId != Guid.Empty
                        && _ticketsBySession.TryGetValue(expectedId, out TraceArchiveTicket sealedTicket))
                    {
                        result = new TraceTransitionResult
                        {
                            Status = TraceTransition.AlreadyClaimed,
                            Ticket = sealedTicket,
                            ExpectedSessionId = expectedId,
                            ObservedSessionId = observedId,
                            TracingOn = true,
                            Successor = _handle,
                            Explanation = "TraceCoordinator: session " + expectedId
                                          + " was already sealed; the bundle uses that session's own evidence",
                        };
                    }
                    else
                    {
                        result = TraceTransitionResult.Refusal(TraceTransition.NotCurrent,
                            expectedId, observedId,
                            "TraceCoordinator: refused — expected session " + expectedId
                            + ", observed " + observedId + "; no trace snapshot is available for the bundle",
                            true);
                    }
                }
                else
                {
                    result = CheckpointLocked(faults, out queued);
                }
                PublishLocked();
            }

            if (queued != null) TraceArchiveWorker.Queue(queued);
            result.DeferredFaults = faults;
            return result;
        }

        private static TraceTransitionResult CheckpointLocked(List<string> faults,
                                                              out TraceArchiveTicket queued)
        {
            queued = null;
            TraceSession session = _session;
            RotatingTraceListener sink = _sink;

            int part = sink.PartNumber;
            string sourcePath = sink.FilePath;

            sink.WriteTerminalLine(Tracing.TracePrefix()
                + "TraceCheckpoint: part " + part.ToString("D3")
                + " frozen for a problem report; this session continues in part "
                + (part + 1).ToString("D3"));

            string fileTag = session.ResolvePartFileTag();
            TraceSessionEntry entry = FreezeEntry(session, isFinalPart: false);

            if (!sink.FlushAndClose(out string closeFailure))
            {
                faults.Add("TraceCoordinator: the trace file did not close cleanly for the snapshot: " + closeFailure);
            }

            string target = TraceFileNaming.StampedPartPath(_livePath, session.BootTimeUtc, part);
            string detached = TraceFileNaming.Detach(sourcePath, target, deleteOnFailure: false,
                                                     out string moveFailure);
            Probe("checkpoint:detached");

            if (detached == null)
            {
                // Nothing was taken away, but the file is closed and has to be
                // reopened or the session goes dark for the rest of the run.
                // APPEND, not create: the bytes are still the session's own, and
                // a FileMode.Create here would destroy the evidence in the
                // course of failing to preserve it.
                faults.Add("TraceCoordinator: could not freeze a snapshot of " + sourcePath
                           + " (" + moveFailure + ")");
                _sink = null;
                TraceTransitionResult reopened = OpenSessionLocked(_livePath, _level, faults,
                                                                   startPartNumber: part,
                                                                   continuing: session,
                                                                   append: true);
                return new TraceTransitionResult
                {
                    Status = TraceTransition.Failed,
                    FailedStage = "detach",
                    RetainedSourcePath = sourcePath,
                    ExpectedSessionId = session.SessionId,
                    ObservedSessionId = session.SessionId,
                    Successor = reopened.Successor,
                    TracingOn = reopened.Status == TraceTransition.Accepted,
                    Explanation = "TraceCoordinator: no snapshot was taken for the bundle; the session continues",
                };
            }

            var ticket = new TraceArchiveTicket
            {
                SessionId = session.SessionId,
                PartNumber = part,
                IsFinalPart = false,
                IsCheckpoint = true,
                SourcePath = detached,
                ArchiveRootDir = ArchiveRootDir,
                Entry = entry,
                OutcomeFileTag = fileTag,
                StampLocal = session.BootTimeUtc.ToLocalTime(),
            };
            // Same contract, same unresolved fallback, as the seal: see
            // NotePendingRecord.
            bool recordWritten = NotePendingRecord(ticket, faults);
            TraceEvidencePins.Pin(detached);

            _sink = null;
            TraceTransitionResult next = OpenSessionLocked(_livePath, _level, faults,
                                                           startPartNumber: part + 1,
                                                           continuing: session);
            if (next.Status == TraceTransition.Accepted)
            {
                _sink.WriteTerminalLine(Tracing.TracePrefix()
                    + "--- trace continues from part " + part.ToString("D3")
                    + " (" + Path.GetFileName(detached) + ") — this is part "
                    + (part + 1).ToString("D3") + " ---");
            }

            queued = ticket;
            return new TraceTransitionResult
            {
                Status = TraceTransition.Accepted,
                Ticket = ticket,
                PendingRecordFailed = !recordWritten,
                ExpectedSessionId = session.SessionId,
                ObservedSessionId = session.SessionId,
                Successor = next.Successor,
                RestartFailed = next.Status != TraceTransition.Accepted,
                TracingOn = next.Status == TraceTransition.Accepted,
                Explanation = "TraceCoordinator: froze part " + part + " of session "
                              + session.SessionId + " for a problem report; the session continues",
            };
        }

        // ── Shutdown ───────────────────────────────────────────────────────

        /// <summary>
        /// Close whichever session remains, for good. The one operation with
        /// authority over a session it did not name, because process shutdown
        /// intentionally closes all recording — and it says so here rather than
        /// being implemented as an unlocked read followed by a generic helper.
        /// Idempotent: both exit hooks share one ticket.
        /// </summary>
        public static TraceTransitionResult FinalizeShutdown(string outcome, string detail)
        {
            LatchShutdown();
            return TrySeal(new TraceSealRequest
            {
                ShutdownAuthority = true,
                Outcome = outcome,
                OutcomeDetail = detail,
                Resume = TraceResumeIntent.None,
                OperationId = ShutdownOperationId,
            });
        }

        /// <summary>
        /// Let queued compressions finish, up to one bounded budget, applied to
        /// final files as well as rotation parts — they share one worker now.
        /// If the budget expires the pending raw files and their records stay
        /// exactly where they are, and nothing claims an archive exists.
        /// </summary>
        public static bool DrainArchives(TimeSpan budget) => TraceArchiveWorker.Drain(budget);

        // ── Helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// Write a detached file's durable pending record and report the
        /// result. Caller holds the gate; nothing here waits or traces.
        ///
        /// <para><b>What happens when the write fails, and what deliberately
        /// does not.</b> The approved design says the durable record is written
        /// BEFORE a successor is published. H3 swallowed a failed write and
        /// published the successor anyway (Sol's review, finding 4). Now the
        /// failure is explicit — on the ticket, on the result, and in a fault
        /// that names the raw file and what a crash would cost — and the raw
        /// file is retained by a policy that needs no write at all: the
        /// plain-text sweep keeps any trace that no archive holds for as long as
        /// an archive would be kept (<see cref="TraceArchiveWorker.ClassifyPlainTextTrace"/>).
        /// The ticket is still queued, so in the ordinary case its archive
        /// commits seconds later and nothing is lost.</para>
        ///
        /// <para><b>NOT DECIDED HERE: whether a successor may open at all when
        /// this fails.</b> Keeping the design's contract to the letter means
        /// refusing the successor — logging stops because one small write
        /// failed. Keeping logging means the contract is broken for this
        /// session's METADATA (outcome, detail, connection target): a crash
        /// before its archive commits leaves the raw bytes kept but unlabelled.
        /// Sol named that trade as one for Astra to judge, and Track H6's brief
        /// said to stop and report it rather than choose. So the successor
        /// behaviour is exactly H3's, unchanged, and the question is open.</para>
        /// </summary>
        private static bool NotePendingRecord(TraceArchiveTicket ticket, List<string> faults)
        {
            bool written = TraceArchiveWorker.WritePendingRecord(ticket, faults);
            ticket.PendingRecordWritten = written;
            if (!written)
            {
                faults.Add("TraceCoordinator: " + ticket.SourcePath
                           + " has NO durable pending record. The raw trace is kept (the plain-text sweep keeps"
                           + " any trace no archive holds for " + SessionArchive.DefaultRetentionDays
                           + " days) and its archive is being made now; if the application ends before that"
                           + " archive commits, the next launch will not recover it automatically, and its"
                           + " outcome and detail will be lost with the record");
            }
            return written;
        }

        /// <summary>
        /// A deep enough copy of the session's metadata that later observations
        /// cannot rewrite a sealed ticket. <see cref="TraceSession.ToManifestEntry"/>
        /// already copies the key events; the connection target is a mutable
        /// object shared with the live session, so it is cloned here.
        /// </summary>
        private static TraceSessionEntry FreezeEntry(TraceSession session, bool isFinalPart)
        {
            TraceSessionEntry entry = session.ToManifestEntry(null, null, null);
            if (entry.ConnectionTarget != null)
            {
                entry.ConnectionTarget = new TraceConnectionTarget
                {
                    Serial = entry.ConnectionTarget.Serial,
                    Nickname = entry.ConnectionTarget.Nickname,
                    SmartlinkAccount = entry.ConnectionTarget.SmartlinkAccount,
                    Ip = entry.ConnectionTarget.Ip,
                };
            }
            if (!isFinalPart)
            {
                // Intermediate parts describe the session, not themselves. Only
                // a final part gets to claim the session ended.
                entry.EndTime = null;
                entry.DurationMs = null;
            }
            return entry;
        }
    }
}
