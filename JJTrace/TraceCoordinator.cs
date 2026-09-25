using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

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

        // ── Transitions do not make anyone wait ────────────────────────────
        //
        // The gate is held for two very different lengths of time. An ordinary
        // write holds it for one sink write — microseconds, or a rotation's
        // rename and reopen. A lifecycle TRANSITION holds it across flush,
        // close, move, pending-record write and successor open: file I/O that
        // a stalled disk can stretch to minutes. Every writer used to wait for
        // whichever it hit. Sol's review of H6 (finding 1) showed what that
        // meant on FlexLib's transport thread: our Connected handler returned
        // promptly, and then FlexLib's own Radio.Disconnect raised further
        // property changes on the same thread, each traced through the gate,
        // so the radio library's teardown still waited out the disk.
        //
        // That thread cannot be marked and its teardown cannot be scoped — the
        // read loop that raises the fall is a thread-pool task, and nothing
        // tells us when Disconnect ends. So the rule lives at the router, for
        // every thread: a write that finds a TRANSITION holding the gate is
        // queued (Tracing.DeferUnbound) and written by that transition on its
        // way out, into the sink that results — which is exactly where the
        // line would have landed had the writer waited. A write that finds
        // another WRITER holding the gate waits as before, briefly, and never
        // long: if a transition begins while it waits, it defers.
        //
        // The depth is written only under the gate, so a thread holding the
        // gate always sees zero unless it is itself a transition.

        private static volatile int _transitionDepth;

        /// <summary>True while a lifecycle transition holds the gate across
        /// file I/O. Readable without the gate.</summary>
        public static bool TransitionInProgress => _transitionDepth > 0;

        /// <summary>Caller holds the gate, and is a transition.</summary>
        private static void BeginTransitionLocked() => _transitionDepth++;

        /// <summary>
        /// Caller holds the gate. Drain what queued up while this transition
        /// held it — into the sink that now exists, before anything written
        /// directly can get ahead of it — then let writers in again.
        /// </summary>
        private static void EndTransitionLocked()
        {
            try { DrainDeferredLocked(); }
            catch { /* the drain must not be able to fail a transition */ }
            _transitionDepth--;
            PublishLocked();
        }

        /// <summary>
        /// How long a writer will wait behind another WRITER before it gives up
        /// and defers. Ordinary contention resolves in microseconds; only a
        /// sink write that is itself stalling on the disk reaches this, and a
        /// stalled sink write is exactly the thing not to wait on.
        /// </summary>
        private const int ContendedWriteWaitMs = 100;

        /// <summary>
        /// Take the gate for one write, or say no. Never waits on a transition:
        /// checked before trying, and again every few milliseconds while
        /// waiting on another writer, so a transition that begins meanwhile is
        /// noticed within one slice.
        /// </summary>
        private static bool TryEnterForWrite()
        {
            if (_transitionDepth > 0) return false;
            if (Monitor.TryEnter(_gate, 0)) return true;
            long deadline = Environment.TickCount64 + ContendedWriteWaitMs;
            while (true)
            {
                if (_transitionDepth > 0) return false;
                if (Monitor.TryEnter(_gate, 5)) return true;
                if (Environment.TickCount64 >= deadline) return false;
            }
        }

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

        // ── Retained evidence: a seal whose bytes could not be moved aside ──
        //
        // A seal that fails at the detach leaves the sealed file at the live
        // path — the only copy — and returns Failed. Until Track H9 nothing
        // remembered that: the operator's prescribed off-and-on after a sink
        // fault called Begin, Begin found no session and nothing recording,
        // and OpenSessionLocked opened the live path with FileMode.Create,
        // truncating the evidence the seal had just refused to delete (Sol's
        // review of H8, blocker 1). The checkpoint path knew better — it
        // reopens with append and says why — but a seal is not a checkpoint:
        // the session is over, and a new one must not be appended onto it.
        //
        // So the seal records what it could not move, keyed by the path it
        // left it at; the ONE place a sink opens refuses to open over such a
        // path; and Begin — the operator's deliberate retry — first tries the
        // move again, with everything the seal had frozen, so a detach that
        // was only transiently blocked becomes an ordinary ticket after all.
        // A retry that fails again refuses the open and says why, and the
        // file stays where it is. Never open over retained evidence.

        /// <summary>Everything a seal froze about a session whose bytes are
        /// still at the path they were written to, so the move can be tried
        /// again later and produce the ticket the seal could not.</summary>
        private sealed class RetainedSeal
        {
            public TraceSession Session;
            public string SourcePath;
            public bool HadParts;
            public int FinalPart;
            public TraceSessionEntry Entry;
            public string FileTag;
            public bool TailUncertain;
            public string SinkFault;
            public string MoveFailure;
        }

        private static readonly Dictionary<string, RetainedSeal> _retainedAtPath =
            new Dictionary<string, RetainedSeal>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Tests only: the paths at which a failed detach left a sealed
        /// session's bytes, and that no open may truncate.
        /// </summary>
        internal static IReadOnlyCollection<string> RetainedEvidencePathsForTests
        {
            get { lock (_gate) { return new List<string>(_retainedAtPath.Keys); } }
        }

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

        /// <summary>
        /// The live sink's latched write failure, or null. Non-null means a
        /// session is nominally open and nothing is being written — the state
        /// the operator must be able to tell apart from "logging is off".
        /// </summary>
        public static string SinkFault
        {
            get { lock (_gate) { return _sink?.WriteFault; } }
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
            if (!TryEnterForWrite()) { Tracing.DeferUnbound(message, newLine: false); return; }
            try
            {
                _sink?.Write(message);
                NoticeSinkFaultLocked();
            }
            finally { Monitor.Exit(_gate); }
        }

        internal static void RouteWriteLine(string message)
        {
            if (message == null) return;
            if (!TryEnterForWrite()) { Tracing.DeferUnbound(message, newLine: true); return; }
            try
            {
                _sink?.WriteLine(message);
                NoticeSinkFaultLocked();
            }
            finally { Monitor.Exit(_gate); }
        }

        internal static void RouteFlush()
        {
            // A flush that cannot get in is not queued: the transition holding
            // the gate flushes the sink it leaves behind, and the drain flushes
            // after writing.
            if (!Monitor.TryEnter(_gate, 0)) return;
            try
            {
                _sink?.Flush();
                NoticeSinkFaultLocked();
            }
            finally { Monitor.Exit(_gate); }
        }

        /// <summary>
        /// Set once per sink, under the gate, so a failed sink is reported to
        /// the health model exactly once — from a pool thread, never from
        /// inside the gate.
        /// </summary>
        private static bool _sinkFaultPublished;

        // ── Sink generations: the order the health model sorts by ──────────
        //
        // Every change of the live sink — an open, a close, a seal, a retire,
        // an open that failed — advances this count, under the gate. Every
        // note the health model receives carries the generation of the sink
        // it describes, and the model applies a note only if it is not about
        // an OLDER sink than the one it already knows. That is what makes the
        // reports safe to deliver from any thread in any order: a live sink's
        // write fault is reported from a pool thread, and a transition can
        // publish a successor's Recording state before that thread runs (Sol's
        // review of H7, finding 2). Identity alone could not order those two
        // notes — a successor's own fault, reported before the transition's
        // Recording note, names a session the model has never seen and would
        // read as stale — so the order is a number, assigned where the sink
        // changes.

        private static long _sinkGeneration;

        /// <summary>Caller holds the gate. The one place the live sink changes.</summary>
        private static void SetSinkLocked(RotatingTraceListener sink)
        {
            _sink = sink;
            _sinkGeneration++;
        }

        /// <summary>Tests only: the current generation, under the gate.</summary>
        internal static long SinkGenerationForTests
        {
            get { lock (_gate) { return _sinkGeneration; } }
        }

        /// <summary>The production fault-retire queue, so a test can put it
        /// back. Declared above the property that reads it: static
        /// initialisers run in textual order.</summary>
        internal static readonly Action<Action> DefaultFaultRetireQueue =
            work => ThreadPool.UnsafeQueueUserWorkItem(_ => work(), null);

        /// <summary>
        /// How the fault-retire work item reaches a pool thread. Production
        /// queues it; a test replaces it to hold the work back and race a
        /// caller against it, exactly as <c>CaptureSeal.Queue</c> is
        /// replaced. Null is never allowed; reset to the default in tests.
        /// </summary>
        internal static Action<Action> FaultRetireQueue { get; set; } = DefaultFaultRetireQueue;

        /// <summary>
        /// Caller holds the gate. If the live sink has just latched a fault,
        /// hand the fact to <see cref="TraceRecordingHealth"/> off this thread
        /// AND have the session retired — a later sink failure "must update
        /// recording/capture health and the accessible status" (Astra,
        /// implementation note 3), and the sink itself cannot raise anything
        /// from inside its own lock.
        ///
        /// <para><b>Why the session is retired and not merely reported.</b>
        /// Until Track H8 this published the fault and left <c>_session</c>
        /// and <c>_sink</c> in place. A seal then refused with NoSession
        /// because the sink was closed, and a Begin refused with
        /// AlreadyRecording because the session was still there — so the
        /// operator's off-and-on could not open a fresh file, Stop could not
        /// end a capture whose file had died, and Diagnostics said "capture in
        /// progress" beside "the log has stopped" (Sol's review of H7, finding
        /// 1). A session is a file; when the file has closed itself the
        /// session is over, and the coordinator says so by sealing it through
        /// the same path every other end takes. The work runs on a pool
        /// thread: retiring detaches the file and writes its record, which is
        /// file I/O this writer's thread must not do.</para>
        /// </summary>
        private static void NoticeSinkFaultLocked()
        {
            RotatingTraceListener sink = _sink;
            if (sink == null || _sinkFaultPublished) return;
            string fault = sink.WriteFault;
            if (fault == null) return;
            _sinkFaultPublished = true;
            string path = sink.FilePath;
            Guid session = _session?.SessionId ?? Guid.Empty;
            long generation = _sinkGeneration;
            try
            {
                FaultRetireQueue(() => RetireFaultedSession(session, fault, path, generation));
            }
            catch
            {
                // A queue that will not take the work must not fail the write
                // that noticed the fault; the next gated entry retires inline.
            }
        }

        /// <summary>
        /// The fault-retire work item: publish the sink failure to the health
        /// model, then, as a transition, retire the faulted session if it is
        /// still the current one. Idempotent by construction — a Begin, a
        /// seal or a bundle snapshot that reached the gate first has already
        /// settled the session, and this finds nothing to do.
        ///
        /// <para>The health note goes first, in this order, on this thread: it
        /// carries the faulted sink's generation, and
        /// <see cref="TraceRecordingHealth"/> keeps it as history if a newer
        /// sink has been published since — which is what stops an old sink's
        /// failure overwriting a newer sink's state (Sol's review of H7,
        /// finding 2).</para>
        /// </summary>
        internal static void RetireFaultedSession(Guid sessionId, string fault, string path, long generation)
        {
            try { TraceRecordingHealth.NoteSink(TraceSinkState.Failed, fault, path, sessionId, generation); }
            catch { /* the health note must not fail the retirement */ }
            if (sessionId == Guid.Empty) return;

            var faults = new List<string>();
            TraceArchiveTicket queued = null;
            lock (_gate)
            {
                if (_session == null || _session.SessionId != sessionId) return;
                BeginTransitionLocked();
                try { RetireFaultedLocked(faults, out queued); }
                finally { EndTransitionLocked(); }
            }
            if (queued != null)
            {
                TraceRecordingHealth.NoteDetached(queued);
                TraceArchiveWorker.Queue(queued);
            }
            // Outside the gate, so the framework's trace lock is taken in the
            // ordinary order. With no successor these land nowhere, which is
            // the truth of the moment; if something has since opened a fresh
            // file they land there, where the next reader will want them.
            foreach (string f in faults)
            {
                try { Tracing.TraceLine(f, TraceLevel.Warning); } catch { }
            }
        }

        /// <summary>
        /// Caller holds the gate and is a transition. If the current session's
        /// sink has closed itself over a fault, seal that session with the
        /// coordinator's own outcome and no successor, through
        /// <see cref="SealLocked"/> so there is exactly one way a session ends.
        /// Returns true when a session was retired; <paramref name="queued"/>
        /// then carries its ticket for the caller to publish and queue OUTSIDE
        /// the gate, as every seal does.
        /// </summary>
        private static bool RetireFaultedLocked(List<string> faults, out TraceArchiveTicket queued)
        {
            queued = null;
            if (_session == null || _sink == null || !_sink.IsClosed) return false;

            Guid retiring = _session.SessionId;
            string fault = _sink.WriteFault ?? "the trace file closed without reporting why";
            string path = _sink.FilePath;
            TraceTransitionResult retiredResult = SealLocked(new TraceSealRequest
            {
                Expected = _handle,
                Outcome = TraceSessionOutcome.RecordingFailed,
                OutcomeDetail = "The trace file stopped accepting writes (" + fault
                                + "); the trace coordinator closed this session and opened nothing in its place",
                Resume = TraceResumeIntent.None,
                OperationId = Guid.Empty,
            }, faults, out queued);
            faults.Add("TraceCoordinator: retired session " + retiring + " because its trace file " + path
                       + " failed a write (" + fault + "); nothing is recording until the log is turned"
                       + " off and on again, or restarted");
            return retiredResult.Status == TraceTransition.Accepted
                   || retiredResult.Status == TraceTransition.Failed;
        }

        /// <summary>
        /// Caller holds the gate and is a transition. If a failed detach left
        /// a sealed session's bytes at <paramref name="livePath"/>, try the
        /// move again with everything that seal froze. On success the session
        /// gets the ticket its seal could not make — pending record, session
        /// index, the lot — and <paramref name="queued"/> carries it for the
        /// caller to publish and queue OUTSIDE the gate, as every seal does.
        /// On failure the file stays exactly where it is, the fault says so,
        /// and the open that follows refuses (see <see cref="OpenSessionLocked"/>).
        /// Nothing to reclaim is not a failure.
        /// </summary>
        private static bool ReclaimRetainedLocked(string livePath, List<string> faults,
                                                  out TraceArchiveTicket queued)
        {
            queued = null;
            if (string.IsNullOrEmpty(livePath)
                || !_retainedAtPath.TryGetValue(livePath, out RetainedSeal r)) return false;

            // The same target the seal wanted; the naming is collision-safe,
            // so a name taken since is simply skipped, never overwritten.
            string target = r.HadParts
                ? TraceFileNaming.StampedPartPath(_livePath, r.Session.BootTimeUtc, r.FinalPart)
                : TraceFileNaming.StampedPath(_livePath, r.Session.BootTimeUtc);
            string detached = TraceFileNaming.Detach(r.SourcePath, target, deleteOnFailure: false,
                                                     out string moveFailure);
            Probe("reclaim:detached");
            if (detached == null)
            {
                r.MoveFailure = moveFailure;
                faults.Add("TraceCoordinator: the raw trace of session " + r.Session.SessionId
                           + " is still at " + r.SourcePath + " and still could not be moved aside ("
                           + moveFailure + "); it is retained where it is, and nothing will open over it");
                return false;
            }

            _retainedAtPath.Remove(livePath);
            var ticket = new TraceArchiveTicket
            {
                SessionId = r.Session.SessionId,
                PartNumber = r.HadParts ? r.FinalPart : 0,
                IsFinalPart = r.HadParts,
                SourcePath = detached,
                ArchiveRootDir = ArchiveRootDir,
                Entry = r.Entry,
                OutcomeFileTag = r.FileTag,
                StampLocal = r.Session.BootTimeUtc.ToLocalTime(),
                TailUncertain = r.TailUncertain,
                SinkFault = r.SinkFault,
            };
            NotePendingRecord(ticket, faults);
            _ticketsBySession[ticket.SessionId] = ticket;
            faults.Add("TraceCoordinator: the raw trace of session " + r.Session.SessionId
                       + " that an earlier seal could not move (" + r.MoveFailure + ") has now been detached to "
                       + detached + " and queued for archiving");
            queued = ticket;
            return true;
        }

        // ── Draining deferred lines ────────────────────────────────────────

        /// <summary>
        /// Write every queued line, taking the gate to do it. The pool drainer
        /// and <see cref="Tracing.FlushDeferred"/> come here; a transition
        /// drains from inside the gate instead.
        /// </summary>
        internal static void DrainDeferred()
        {
            lock (_gate) { DrainDeferredLocked(); }
        }

        /// <summary>
        /// Caller holds the gate. Consume the queue in order: an unbound line
        /// goes to the current sink; a bound line goes to the current sink
        /// only if that IS its session, and is otherwise written as a refusal
        /// record naming both sessions — never as a bare line that would read
        /// as a statement about the session it did not describe, and never
        /// dropped, because the fall it recorded happened.
        ///
        /// <para>Consumed ONLY under the gate. Sol's interleaving for H6 was a
        /// drainer that dequeued, then waited for the gate behind a Stop, and
        /// wrote the old session's lines into the successor the Stop opened.
        /// With the queue consumed under the gate there is no gap between
        /// deciding a line's session and writing it.</para>
        ///
        /// <para><b>Bounded to what was queued when it began.</b> A drain that
        /// ran until the queue was empty would chase a producer: while a
        /// transition holds the gate every writer in the process is deferring
        /// INTO this queue, so a thread writing at full speed keeps it
        /// non-empty and the transition never ends. Found by the first full
        /// run at H7 — the rotation test's producer, with a four-kilobyte
        /// threshold, made a seal rotate once per drained line and never
        /// return. Lines queued during a drain wait for the next one: the end
        /// of the transition, or the pool drainer after it.</para>
        /// </summary>
        private static void DrainDeferredLocked()
        {
            bool any = false;
            Guid current = _session?.SessionId ?? Guid.Empty;
            int budget = Tracing.DeferredLinesQueued;
            while (budget-- > 0 && Tracing.TryDequeueDeferred(out DeferredTraceLine line))
            {
                any = true;
                if (_sink == null) continue;   // nothing recording: as a direct write would be, dropped
                if (line.BoundSession == Guid.Empty || line.BoundSession == current)
                {
                    if (line.NewLine) _sink.WriteLine(line.Text);
                    else _sink.Write(line.Text);
                    continue;
                }
                Tracing.NoteDeferredRefused();
                _sink.WriteLine(Tracing.TracePrefix()
                    + "TraceDeferred: REFUSED — the following line was formatted while session "
                    + line.BoundSession + " was recording, and that session has since been sealed"
                    + " (session " + current + " is current). It describes that session, not this one;"
                    + " kept here so the moment is not lost: " + line.Text);
            }
            if (any)
            {
                _sink?.Flush();
                NoticeSinkFaultLocked();
            }
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
                BeginTransitionLocked();
                try
                {
                    CloseSinkLocked();
                    OpenSessionLocked(path, _level, faults, startPartNumber: 1,
                                      continuing: null);
                    // No managed session: drop the pointer the open just set, so
                    // nothing mistakes a console tool's file for a lifecycle
                    // session it could seal.
                    _session = null;
                    _handle = null;
                }
                finally { EndTransitionLocked(); }
            }
        }

        internal static void CloseUnmanagedSink()
        {
            lock (_gate)
            {
                if (_session != null) return;
                BeginTransitionLocked();
                try { CloseSinkLocked(); }
                finally { EndTransitionLocked(); }
            }
        }

        private static void CloseSinkLocked()
        {
            if (_sink == null) return;
            try { _sink.FlushAndClose(out _); } catch { }
            SetSinkLocked(null);
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
        ///
        /// <para><b>A session whose sink has died is settled here first.</b>
        /// It is not recording, so it cannot be the reason to refuse; it is
        /// retired through the ordinary seal, its ticket queued, and the open
        /// proceeds. This is what makes the operator's off-and-on, and
        /// <c>RestartDiagnosticLog</c>, really open a fresh file after a
        /// write fault (Sol's review of H7, finding 1) — whether or not the
        /// pool's own retirement has run yet.</para>
        /// </summary>
        public static TraceTransitionResult Begin(string livePath,
                                                  TraceLevel level,
                                                  bool asDetailedCapture,
                                                  DateTime? captureStartedLocal = null)
        {
            var faults = new List<string>();
            TraceTransitionResult result;
            TraceArchiveTicket retired = null;
            TraceArchiveTicket reclaimed = null;
            lock (_gate)
            {
                BeginTransitionLocked();
                try
                {
                    if (_shuttingDown)
                    {
                        result = TraceTransitionResult.Refusal(TraceTransition.ShuttingDown,
                            Guid.Empty, _session?.SessionId ?? Guid.Empty,
                            "TraceCoordinator.Begin refused: exit is committed",
                            _sink != null && !_sink.IsClosed);
                    }
                    else
                    {
                        RetireFaultedLocked(faults, out retired);
                        // The operator's retry is also the retry of a detach
                        // that failed: a session whose bytes are still at
                        // this path gets its move tried again, and its ticket
                        // if the move succeeds, BEFORE anything opens. If it
                        // fails again the open below refuses rather than
                        // truncates.
                        ReclaimRetainedLocked(livePath, faults, out reclaimed);
                        if (_session != null || (_sink != null && !_sink.IsClosed))
                        {
                            // The real sink state, not a constant: a refusal that
                            // said "tracing on" over a closed sink is how the
                            // settings path once reported a log it had not opened.
                            result = TraceTransitionResult.Refusal(TraceTransition.AlreadyRecording,
                                Guid.Empty, _session?.SessionId ?? Guid.Empty,
                                "TraceCoordinator.Begin refused: session "
                                + (_session?.SessionId.ToString() ?? "(none)") + " is still recording",
                                _sink != null && !_sink.IsClosed);
                        }
                        else
                        {
                            result = OpenSessionLocked(livePath, level, faults);
                            if (result.Status == TraceTransition.Accepted && asDetailedCapture)
                            {
                                result.StartedCaptureId = StartCaptureLocked(captureStartedLocal);
                            }
                            result.TracingOn = _sink != null && !_sink.IsClosed;
                        }
                    }
                    result.SinkGeneration = _sinkGeneration;
                    result.Reclaimed = reclaimed;
                }
                finally { EndTransitionLocked(); }
            }
            result.DeferredFaults = faults;
            if (retired != null)
            {
                TraceRecordingHealth.NoteDetached(retired);
                TraceArchiveWorker.Queue(retired);
            }
            if (reclaimed != null)
            {
                // Same order as every seal: the health model first, then the
                // queue. This ticket is what the failed seal would have
                // produced; it is a whole session's evidence and its tail is
                // whatever the seal recorded.
                TraceRecordingHealth.NoteDetached(reclaimed);
                TraceArchiveWorker.Queue(reclaimed);
            }
            if (result.Status != TraceTransition.ShuttingDown
                && result.Status != TraceTransition.AlreadyRecording)
            {
                PublishSinkHealth(result);
            }
            return result;
        }

        /// <summary>
        /// Tell the health model what the live sink is doing, from a result,
        /// OUTSIDE the gate. Called on the way out of every transition that can
        /// change the sink.
        /// </summary>
        private static void PublishSinkHealth(TraceTransitionResult result)
        {
            if (result == null) return;
            try
            {
                if (result.TracingOn)
                {
                    // The session recording NOW. A result with no successor of
                    // its own (a repeated seal answered AlreadyClaimed, say)
                    // still describes a live sink, and that sink has a session.
                    Guid live = result.Successor?.SessionId ?? CurrentHandle?.SessionId ?? Guid.Empty;
                    TraceRecordingHealth.NoteSink(TraceSinkState.Recording, null, LivePath, live,
                                                  result.SinkGeneration);
                }
                else if (result.RestartFailed || result.Status == TraceTransition.Failed)
                {
                    // The path, when the result names one: an open refused
                    // over retained evidence names the file it refused, and
                    // the operator's entry should say which file that is.
                    TraceRecordingHealth.NoteSink(TraceSinkState.Failed,
                        result.SinkFault ?? result.Explanation, result.RetainedSourcePath,
                        Guid.Empty, result.SinkGeneration);
                }
                else if (result.Status == TraceTransition.Accepted)
                {
                    // Sealed with no successor, by intent.
                    TraceRecordingHealth.NoteSink(TraceSinkState.Off, null, null, Guid.Empty,
                                                  result.SinkGeneration);
                }
            }
            catch { /* the health note must not fail the transition it describes */ }
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
                _retainedAtPath.Clear();
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

        /// <summary>
        /// Caller must hold the gate.
        ///
        /// <para><b>A sink is not "recording" until its first record has been
        /// written and flushed.</b> Constructing the stream proves the path
        /// could be opened; it says nothing about the next byte. So the open
        /// writes one header line — the session and part it belongs to — and
        /// checks it landed. A header that will not write is an open that
        /// failed, reported as such, with the sink closed and nothing
        /// published (Astra's ruling, implementation note 3: "Verify the
        /// successor's first record and flush before describing it as
        /// recording"). The optional last argument is the header to write and
        /// verify; null means the standard "session opened" line.</para>
        /// </summary>
        private static TraceTransitionResult OpenSessionLocked(string livePath, TraceLevel level,
                                                               List<string> faults,
                                                               int startPartNumber = 1,
                                                               TraceSession continuing = null,
                                                               bool append = false,
                                                               string firstLine = null)
        {
            var result = new TraceTransitionResult();

            // NEVER OPEN OVER RETAINED EVIDENCE. A non-append open here is a
            // FileMode.Create, and if a failed detach left a sealed session's
            // only bytes at this path, that create would truncate them in the
            // course of starting a fresh log (Sol's review of H8, blocker 1).
            // Guarded at the one place a sink opens rather than at each
            // caller, so no caller can bypass it; Begin, the operator's
            // retry, tries the move again BEFORE coming here (see
            // ReclaimRetainedLocked). An append is the checkpoint's own
            // continuation of a live session and is not a create.
            if (!append && _retainedAtPath.TryGetValue(livePath, out RetainedSeal retained))
            {
                string reason = "the file still holds the raw trace of session " + retained.Session.SessionId
                                + ", which could not be moved aside (" + retained.MoveFailure
                                + "), and nothing was opened over it";
                faults.Add("TraceCoordinator: refused to open a trace at " + livePath + ": " + reason
                           + "; turn the log off and on to try the move again");
                result.Status = TraceTransition.Failed;
                result.FailedStage = "retained-evidence";
                result.RetainedSourcePath = livePath;
                result.SinkFault = reason;
                result.TracingOn = false;
                result.Explanation = "TraceCoordinator: nothing opened at " + livePath
                                     + " because it holds retained evidence of session "
                                     + retained.Session.SessionId;
                return result;
            }

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

                string header = firstLine
                    ?? ("--- trace session " + session.SessionId + " part "
                        + startPartNumber.ToString("D3") + " opened "
                        + DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                        + " ---");
                if (!sink.WriteTerminalLine(Tracing.TracePrefix() + header))
                {
                    string fault = sink.WriteFault ?? "the first record could not be written";
                    try { sink.FlushAndClose(out _); } catch { }
                    faults.Add("TraceCoordinator: opened a trace at " + livePath
                               + " but its first record would not write (" + fault
                               + "); nothing is recording");
                    SetSinkLocked(null);
                    _session = null;
                    _handle = null;
                    _stamp = null;
                    result.Status = TraceTransition.Failed;
                    result.FailedStage = "first-write";
                    result.SinkFault = fault;
                    result.TracingOn = false;
                    return result;
                }

                SetSinkLocked(sink);
                _sinkFaultPublished = false;
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
                SetSinkLocked(null);
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
                BeginTransitionLocked();
                try
                {
                    result = SealLocked(request, faults, out queued);
                    result.SinkGeneration = _sinkGeneration;
                }
                finally { EndTransitionLocked(); }
            }

            result.DeferredFaults = faults;

            // Outside the gate, deliberately, and in this order: the health
            // model hears about the detached ticket BEFORE it is queued, so a
            // caller that waits on the archive afterwards (the drop hook, up
            // to its five-minute budget) has already had the failure published
            // to the operator's surface. Queueing touches a Task chain and the
            // design forbids waiting on anything while the gate is held.
            if (queued != null)
            {
                TraceRecordingHealth.NoteDetached(queued);
                TraceArchiveWorker.Queue(queued);
            }
            if (result.Owned) PublishSinkHealth(result);
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

            // A session whose sink has CLOSED ITSELF over a fault is still a
            // session, and it is still sealable: its terminal records fail
            // and are reported as an uncertain tail, its close reports the
            // latched fault, its bytes are detached and its ticket queued,
            // the capture it carried ends with it, and the caller's intent
            // decides the successor. Until Track H8 a closed sink answered
            // NoSession here and cleared nothing, which left Stop unable to
            // end a capture whose file had died (Sol's review of H7, finding
            // 1). Only a MISSING session or sink is nothing to seal.
            if (_session == null || _sink == null)
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

            // This operation owns the session. Everything queued up to this
            // moment — the fall's own bound lines above all — belongs in THIS
            // file, ahead of its terminal records. Drained here, under the
            // gate, so a line bound to this session cannot wait behind this
            // seal and land in the successor (Sol's review of H6, finding 1).
            Probe("seal:owned");
            DrainDeferredLocked();

            if (!string.IsNullOrEmpty(request.Outcome))
            {
                sealing.MarkOutcome(request.Outcome, request.OutcomeDetail);
            }

            // The caller's last observations, written into the file they belong
            // to and nowhere else — and each write's result kept, because a
            // terminal record that did not land is the tail of the evidence
            // going missing, and that has to be said rather than closed over.
            bool tailUncertain = false;
            if (request.TerminalLines != null)
            {
                foreach (string line in request.TerminalLines)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    if (!sink.WriteTerminalLine(Tracing.TracePrefix() + line)) tailUncertain = true;
                }
            }

            // The seal marker, written BEFORE the rotation position is frozen
            // because it is itself a write. Rotation is suppressed for the
            // duration of a terminal write, so the number below cannot go stale
            // underneath us.
            if (!sink.WriteTerminalLine(Tracing.TracePrefix()
                    + TraceStateMarker.RenderTerminal(AppIdentity, sealing.BootTimeUtc, sink.FilePath)))
            {
                tailUncertain = true;
            }

            bool hadParts = sink.HasRotated;
            int finalPart = sink.PartNumber;
            string sourcePath = sink.FilePath;

            sealing.End();
            string fileTag = hadParts ? sealing.ResolvePartFileTag() : sealing.Outcome;
            TraceSessionEntry entry = FreezeEntry(sealing, isFinalPart: hadParts);

            if (!sink.FlushAndClose(out string closeFailure))
            {
                tailUncertain = true;
                faults.Add("TraceCoordinator: the trace file did not close cleanly: " + closeFailure);
            }
            string sinkFault = tailUncertain ? (sink.WriteFault ?? closeFailure ?? "terminal record not written") : null;
            if (tailUncertain)
            {
                faults.Add("TraceCoordinator: not every terminal record reached " + sourcePath
                           + " (" + sinkFault + "); the bytes that did land are retained, and the file's tail is uncertain");
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
            SetSinkLocked(null);
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
                // do not delete it to make room. REMEMBER it, keyed by the path,
                // so the one place a sink opens refuses that path and the
                // operator's retry can try the move again (Sol's review of H8,
                // blocker 1: without this, the next Begin truncated it).
                _retainedAtPath[sourcePath] = new RetainedSeal
                {
                    Session = sealing,
                    SourcePath = sourcePath,
                    HadParts = hadParts,
                    FinalPart = finalPart,
                    Entry = entry,
                    FileTag = fileTag,
                    TailUncertain = tailUncertain,
                    SinkFault = sinkFault,
                    MoveFailure = moveFailure,
                };
                faults.Add("TraceCoordinator: could not detach " + sourcePath
                           + " (" + moveFailure + "); the raw trace is retained where it is and nothing was archived;"
                           + " nothing will open over it, and the next Begin tries the move again");
                return new TraceTransitionResult
                {
                    Status = TraceTransition.Failed,
                    FailedStage = "detach",
                    RetainedSourcePath = sourcePath,
                    ExpectedSessionId = expectedId,
                    ObservedSessionId = observedId,
                    TracingOn = false,
                    TailUncertain = tailUncertain,
                    SinkFault = sinkFault,
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
                TailUncertain = tailUncertain,
                SinkFault = sinkFault,
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
                TailUncertain = tailUncertain,
                SinkFault = sinkFault,
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
                    // Verified: the successor's first record was written and
                    // flushed inside OpenSessionLocked, or it would not be
                    // Accepted.
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
                    // The old ticket stands; only the restart failed. Said so,
                    // with the sink's own reason, so the operator can be told
                    // nothing is recording rather than "off".
                    result.RestartFailed = true;
                    result.TracingOn = false;
                    result.SinkFault = result.SinkFault ?? opened.SinkFault ?? opened.FailedStage;
                    _level = TraceLevel.Off;
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
            TraceArchiveTicket retired = null;
            TraceTransitionResult result;

            lock (_gate)
            {
                BeginTransitionLocked();
                try
                {
                // A session whose sink died is retired first, so the bundler
                // is handed that session's own sealed ticket below rather than
                // "nothing is recording" over a file full of evidence.
                RetireFaultedLocked(faults, out retired);
                Guid expectedId = expected?.SessionId ?? Guid.Empty;
                Guid observedId = _session?.SessionId ?? Guid.Empty;
                bool recording = _session != null && _sink != null && !_sink.IsClosed;

                if (recording && expected != null && expected.SessionId == _session.SessionId)
                {
                    result = CheckpointLocked(faults, out queued);
                }
                // THE SEALED SESSION'S OWN TICKET IS ASKED FOR BEFORE "NOTHING IS
                // RECORDING" (Sol's review of H3, finding 5). A Stop, or logging
                // switched off, can seal the expected session with no successor
                // between the bundler reading its handle and arriving here. That
                // session's evidence exists — sealed, detached, retained — and
                // the old order answered NoSession because nothing was recording
                // NOW, so the bundle dropped a trace it had every right to carry.
                else if (expectedId != Guid.Empty
                         && _ticketsBySession.TryGetValue(expectedId, out TraceArchiveTicket sealedTicket))
                {
                    // Pinned while the bundle is built, exactly as a fresh
                    // checkpoint is: the bundler's Finally releases whichever it
                    // was handed, so an unpinned hand-over would also have
                    // released somebody else's pin on the same file.
                    TraceEvidencePins.Pin(sealedTicket.SourcePath);
                    result = new TraceTransitionResult
                    {
                        Status = TraceTransition.AlreadyClaimed,
                        Ticket = sealedTicket,
                        ExpectedSessionId = expectedId,
                        ObservedSessionId = observedId,
                        TracingOn = recording,
                        Successor = _handle,
                        Explanation = "TraceCoordinator: session " + expectedId
                                      + " was already sealed; the bundle uses that session's own evidence",
                    };
                }
                else if (!recording)
                {
                    result = TraceTransitionResult.Refusal(TraceTransition.NoSession,
                        expectedId, observedId,
                        "TraceCoordinator: nothing was recording, so there is no snapshot to take", false);
                }
                else
                {
                    // Say plainly that no snapshot is available — never silently
                    // substitute a later session's trace.
                    result = TraceTransitionResult.Refusal(TraceTransition.NotCurrent,
                        expectedId, observedId,
                        "TraceCoordinator: refused — expected session " + expectedId
                        + ", observed " + observedId + "; no trace snapshot is available for the bundle",
                        true);
                }
                result.SinkGeneration = _sinkGeneration;
                }
                finally { EndTransitionLocked(); }
            }

            result.DeferredFaults = faults;
            // Same order as a seal: the health model before the queue, and
            // before anything the bundler waits on.
            if (retired != null)
            {
                TraceRecordingHealth.NoteDetached(retired);
                TraceArchiveWorker.Queue(retired);
            }
            if (queued != null)
            {
                TraceRecordingHealth.NoteDetached(queued);
                TraceArchiveWorker.Queue(queued);
            }
            if (result.Status == TraceTransition.Accepted || result.Status == TraceTransition.Failed)
            {
                PublishSinkHealth(result);
            }
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

            // What queued up while this transition took the gate belongs to
            // the part being frozen, ahead of its checkpoint record.
            DrainDeferredLocked();

            bool tailUncertain = !sink.WriteTerminalLine(Tracing.TracePrefix()
                + "TraceCheckpoint: part " + part.ToString("D3")
                + " frozen for a problem report; this session continues in part "
                + (part + 1).ToString("D3"));

            string fileTag = session.ResolvePartFileTag();
            TraceSessionEntry entry = FreezeEntry(session, isFinalPart: false);

            if (!sink.FlushAndClose(out string closeFailure))
            {
                tailUncertain = true;
                faults.Add("TraceCoordinator: the trace file did not close cleanly for the snapshot: " + closeFailure);
            }
            string sinkFault = tailUncertain ? (sink.WriteFault ?? closeFailure ?? "checkpoint record not written") : null;

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
                SetSinkLocked(null);
                TraceTransitionResult reopened = OpenSessionLocked(_livePath, _level, faults,
                    startPartNumber: part,
                    continuing: session,
                    append: true,
                    firstLine: "--- trace resumes in the same file: a problem-report snapshot could not"
                               + " be taken (" + moveFailure + ") and nothing was moved ---");
                return new TraceTransitionResult
                {
                    Status = TraceTransition.Failed,
                    FailedStage = "detach",
                    RetainedSourcePath = sourcePath,
                    ExpectedSessionId = session.SessionId,
                    ObservedSessionId = session.SessionId,
                    Successor = reopened.Successor,
                    TracingOn = reopened.Status == TraceTransition.Accepted,
                    TailUncertain = tailUncertain,
                    SinkFault = reopened.SinkFault ?? sinkFault,
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
                TailUncertain = tailUncertain,
                SinkFault = sinkFault,
            };
            // Same contract, same continuation rule, as the seal: see
            // NotePendingRecord.
            bool recordWritten = NotePendingRecord(ticket, faults);
            TraceEvidencePins.Pin(detached);

            SetSinkLocked(null);
            // The continuation header IS the next part's verified first write.
            TraceTransitionResult next = OpenSessionLocked(_livePath, _level, faults,
                startPartNumber: part + 1,
                continuing: session,
                firstLine: "--- trace continues from part " + part.ToString("D3")
                           + " (" + Path.GetFileName(detached) + ") — this is part "
                           + (part + 1).ToString("D3") + " ---");

            queued = ticket;
            return new TraceTransitionResult
            {
                Status = TraceTransition.Accepted,
                Ticket = ticket,
                PendingRecordFailed = !recordWritten,
                TailUncertain = tailUncertain,
                SinkFault = next.Status == TraceTransition.Accepted ? sinkFault : (next.SinkFault ?? sinkFault),
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
        /// <para><b>RULED: logging continues.</b> Astra's design ruling on the
        /// pending-record failure
        /// (<c>for-claude/2026-09-24-codex-design-pending-record-failure.md</c>):
        /// "Allow logging to continue after a failed pending-record write,
        /// provided the old file has been safely detached and the successor
        /// can write. Report the old ticket's recovery failure independently
        /// of the new session's recording state." The durable-record-before-
        /// successor rule existed to preserve evidence across asynchronous
        /// archiving, not to turn a metadata failure into the loss of every
        /// subsequent observation. So: the frozen ticket is still queued, the
        /// successor still opens (and is verified before it is called
        /// recording), and the failure goes to
        /// <see cref="TraceRecordingHealth"/>, where Diagnostics and the
        /// operator's notification read it — not only to this trace, which is
        /// the log that may not be writable. The archive worker makes one
        /// retry of the sidecar from the frozen ticket; a crash before either
        /// that or the archive commits still loses the metadata, and the
        /// health model says so rather than claiming it is recoverable.</para>
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
