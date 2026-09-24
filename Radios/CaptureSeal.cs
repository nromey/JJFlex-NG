using System;
using System.Diagnostics;
using System.Threading.Tasks;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// What the application's sealing hook is asked to do. Immutable, built at
    /// the removal and carried to the worker, so nothing downstream has to
    /// re-read a world that has moved on.
    /// </summary>
    public sealed class CaptureSealRequest
    {
        /// <summary>
        /// The recording this drop is ABOUT — a
        /// <c>JJTrace.TraceSessionHandle</c>, typed as <c>object</c> only so the
        /// hook's signature does not force every caller to name the type.
        /// </summary>
        public object ExpectedSession { get; set; }

        /// <summary>The claim this drop took, used as the boundary's operation
        /// identity so a repeat gets the same ticket rather than a second
        /// archive.</summary>
        public Guid DropOperationId { get; set; }

        /// <summary>The sentence written onto the manifest entry.</summary>
        public string OutcomeDetail { get; set; }

        /// <summary>
        /// The meter window this drop closed, already rendered, collected AFTER
        /// the claim was won. It is written into the accepted session's own file
        /// by the boundary, or discarded — never emitted globally (#618).
        /// </summary>
        public string PartialMeterLine { get; set; }
    }

    /// <summary>What the sealing hook did. Facts, not prose.</summary>
    public sealed class CaptureSealResult
    {
        /// <summary>Full path of the committed archive, or null when nothing
        /// is committed yet.</summary>
        public string ArchivePath { get; set; }

        /// <summary>The session that was archived.</summary>
        public Guid? ArchivedSessionId { get; set; }

        /// <summary>
        /// Whether a successor really opened. A drop that wins during a
        /// teardown seals its own session and opens nothing, and the operator's
        /// window must be able to say so rather than promising a restart that
        /// did not happen.
        /// </summary>
        public bool SuccessorOpened { get; set; }

        /// <summary>True when the boundary refused this operation.</summary>
        public bool Refused { get; set; }

        /// <summary>Why it was refused, in trace terms.</summary>
        public string RefusalReason { get; set; }

        /// <summary>Where the raw trace was retained when no archive is
        /// committed. Evidence is never deleted to make room.</summary>
        public string RawRetainedPath { get; set; }
    }

    /// <summary>
    /// Seals the running diagnostic capture when the RADIO's connection dies,
    /// and tells whoever is listening where the file went.
    ///
    /// <para><b>The gap this closes.</b> <c>JJTrace.TraceSessionOutcome</c> has
    /// defined <c>connection_dropped</c> since Sprint 29 and <b>no archive on
    /// this machine has ever carried it</b> — 231 zips, every one of them
    /// <c>clean_exit</c>, <c>killed</c>, <c>no_radios</c> or
    /// <c>slice_unavailable</c> (counted 2026-09-22). Nothing on the drop path
    /// archived anything: a radio dying mid-transmit left the capture open, the
    /// operator carried on, and hours later the app closed and sealed the whole
    /// evening as a normal one. <b>A radio death and a quiet evening produced
    /// the same file.</b></para>
    ///
    /// <para><b>Why it has to be told rather than inferred.</b> The outcome is
    /// decided by <c>TraceSession.MarkOutcome</c>, first call wins, and
    /// <c>ToManifestEntry</c> defaults an unmarked session to <c>clean_exit</c>
    /// the moment <c>End()</c> is called. So the drop must mark it AT THE DROP;
    /// there is no later point where the truth is still available.</para>
    ///
    /// <para><b>Only a radio-side loss.</b> The operator's own Disconnect is
    /// <c>RadioRemovalKind.SelfInitiated</c> and seals nothing — the manifest
    /// already has that vocabulary, and tagging a deliberate hang-up
    /// <c>connection_dropped</c> would poison the one query this outcome
    /// exists to answer.</para>
    ///
    /// <para><b>Why the seal is not conditional on a capture being running.</b>
    /// Whatever session is open is the evidence, standing log or detailed
    /// capture. Sealing it costs one zip and gains a file with the right word on
    /// it; declining to seal because the operator had not pressed Ctrl+J Ctrl+D
    /// would mean the unplanned case — which is every case that matters — keeps
    /// producing files that say <c>clean_exit</c>.</para>
    ///
    /// <para><b>THE REQUEST IS BOUND AT THE DROP, not resolved at the archive.</b>
    /// The first build queued only a radio name and let the worker ask, whenever
    /// it finally ran, "what is the current session?" — so a Stop, a new
    /// capture, a log toggle or an exit arriving in between made it archive a
    /// DIFFERENT session, or none, and nothing said so. The drop now captures
    /// the expected trace handle on its own thread, before queueing, and the
    /// ownership comparison happens INSIDE the trace boundary. A losing caller
    /// is refused explicitly and may not close a listener, clear capture state
    /// or restart logging.</para>
    ///
    /// <para><b>The duplicate is recognised by the CONNECTION, not by a bit and
    /// not by a timer.</b> The first build spent one process-global flag per
    /// trace session — and the seal's own restart of the standing log RE-ARMED
    /// it. H2 replaced that with the removal's <c>Radio</c> object plus a
    /// sixty-second window, because nothing had verified whether FlexLib hands
    /// back a new object after a reconnect. Astra read the vendor source and
    /// settled it: it does not guarantee one. So the key is now an
    /// application-owned connection lifetime
    /// (<see cref="ConnectionLifetime"/>), claimed terminally, once, per token
    /// — with no elapsed-time bound at all, which also disposes of the signed
    /// <c>Environment.TickCount</c> half-wrap that made the sixty seconds
    /// untrue anyway.</para>
    ///
    /// <para><b>And the flush is collected AFTER the claim (#618).</b> H2 wrote
    /// the <c>partial=connection_dropped</c> meter line before asking whether
    /// this removal was already claimed, so a repeat notice could stamp a false
    /// drop line into the fresh standing log the first seal had just started.
    /// Moving the call after the claim is NOT sufficient on its own: a session
    /// replacement can still land between the claim and a global
    /// <c>Tracing.TraceLine</c>. So the collection and the write are split — the
    /// caller hands over a function that RENDERS the line, this class calls it
    /// only after winning the claim, and the boundary writes it into the
    /// accepted session's own sink or discards it.</para>
    /// </summary>
    public static class CaptureSeal
    {
        /// <summary>
        /// Seal the trace session the drop was about, with the
        /// <c>connection_dropped</c> outcome, and hand back what happened.
        /// Installed by the application at startup; null until then, and a null
        /// hook makes every call below a no-op that says so in the trace.
        ///
        /// <para>A hook rather than a call, because the sealing lives in the VB
        /// application (<c>globals.vb</c>) and this assembly is referenced BY
        /// it. Same seam, and for the same reason, as
        /// <c>JJFlexWpf.DiagnosticsBridge</c>.</para>
        /// </summary>
        public static Func<CaptureSealRequest, CaptureSealResult> SealHook { get; set; }

        /// <summary>
        /// Raised once a drop has sealed a capture, carrying where it landed.
        /// The WPF layer subscribes and shows the operator the path; anything
        /// else that wants to know may too. Raised on a background thread —
        /// subscribers marshal for themselves. Once per accepted drop ticket,
        /// after the archive is committed, outside every lock.
        /// </summary>
        public static event Action<CaptureSealNotice> SealedAfterDrop;

        /// <summary>
        /// How the sealing work reaches a background thread. Production queues
        /// it; a test replaces it so the lifecycle race can be driven
        /// deliberately — queue the drop, end the session, THEN run the worker
        /// and watch it decline. That race is not visible to any test that
        /// cannot control when the worker wakes, which is why it survived the
        /// first build's thirty-seven tests.
        /// </summary>
        internal static Action<Action> Queue { get; set; } = work => Task.Run(work);

        /// <summary>
        /// The outcome detail written onto the manifest entry. A sentence, not a
        /// code: the trace browser shows this text, and "the radio's connection
        /// dropped" is what a person reading a list of sessions needs to see.
        /// </summary>
        public static string OutcomeDetail(string radioName) =>
            string.IsNullOrWhiteSpace(radioName)
                ? "The radio's connection dropped while this session was running"
                : "The connection to " + radioName.Trim() + " dropped while this session was running";

        /// <summary>
        /// A radio we were connected to went away without us asking. Seal the
        /// session that was recording AT THIS MOMENT, and announce where it
        /// landed.
        ///
        /// <para>Returns immediately: the sealing itself compresses a file that
        /// can be megabytes, and this is called when FlexLib reports our
        /// Radio's <c>Connected</c> property falling, on FlexLib's own transport
        /// thread, in the middle of a teardown. (Tracks H to H3 called it from
        /// the <c>RadioRemoved</c> handler instead, which FlexLib never raises
        /// for a radio reached only through SmartLink — see
        /// <c>FlexBase.sealIfOurConnectionDropped</c>.) Blocking
        /// that to zip a log would be a hang in the one situation where the
        /// application most needs to stay responsive. Nothing here waits for the
        /// trace boundary, for a file operation, for compression or for a UI
        /// dispatch.</para>
        ///
        /// <para>Which is exactly why the handle has to be read HERE, on this
        /// thread, before returning. Everything after this line is running in a
        /// world where the operator may already have stopped the capture,
        /// started another, toggled logging or closed the app.</para>
        /// </summary>
        /// <param name="dropToken">The drop's own identity — the <c>Radio</c>
        /// object whose <c>Connected</c> property fell. Its
        /// connection lifetime is what makes two notices one drop. May be null;
        /// the claim then falls back to one seal per session.</param>
        /// <param name="radioName">The radio's nickname, for the sentence on the
        /// manifest entry and in the operator's window.</param>
        /// <param name="collectPartialMeterLine">Renders the meter window this
        /// drop closed. <b>Called only if this removal wins the claim</b>, and
        /// its result is written to the accepted session or to nothing at all.
        /// May be null.</param>
        public static void AfterConnectionDrop(object dropToken,
                                               string radioName,
                                               Func<string> collectPartialMeterLine = null)
        {
            // One immutable read, on this thread. A handle, not a pointer into
            // anything the worker could find changed.
            //
            // AND NOTHING ON THIS THREAD WAITS ON THE TRACE GATE (Sol's review
            // of H3, finding 1). CurrentHandle is a published snapshot read
            // without the gate, and every line this method writes is DEFERRED:
            // an ordinary TraceLine passes the same gate that is held across a
            // transition's flush, close, move and successor open, so one line
            // here would have made FlexLib's transport thread wait out a
            // stalled disk — and made the claim below wait with it.
            TraceSessionHandle expected = TraceCoordinator.CurrentHandle;
            if (expected == null)
            {
                // Not a defect and not silence: nothing was being recorded, so
                // there is no evidence to seal. Said out loud because "no
                // connection_dropped archive appeared" needs to be answerable
                // afterwards, and "logging was off" is one of the answers.
                Tracing.TraceLineDeferred(
                    "CaptureSeal: the radio's connection dropped but nothing was recording — no session to seal",
                    TraceLevel.Warning);
                return;
            }

            var hook = SealHook;
            if (hook == null)
            {
                // Said out loud rather than swallowed. A missing hook means the
                // wiring never ran, and the symptom — a capture that quietly
                // says clean_exit — is indistinguishable from the bug this
                // class exists to fix. The claim is deliberately NOT taken: an
                // unwired call seals nothing, so it must not also consume the
                // drop and refuse a later, correctly wired one.
                Tracing.TraceLineDeferred(
                    "CaptureSeal: the radio's connection dropped but no seal hook is installed — "
                    + "the session will be archived as an ordinary one (wiring defect)",
                    TraceLevel.Warning);
                return;
            }

            if (!TryClaimDrop(dropToken, expected.SessionId, out Guid operationId))
            {
                // Logged as a refusal, naming what was refused — never as a
                // statement that the current session suffered a drop. And
                // nothing has been written anywhere: the flush below has not run.
                Tracing.TraceLineDeferred(
                    "CaptureSeal: this connection's loss was already claimed — not sealing again, "
                    + "and no partial meter line was written",
                    TraceLevel.Info);
                return;
            }

            // Claim first, THEN collect. #618 in one line: the window is
            // rendered only by the removal that owns the drop, and it travels as
            // data so a session replacement between here and the boundary
            // cannot land it in a successor's log.
            string partial = null;
            try { partial = collectPartialMeterLine?.Invoke(); }
            catch (Exception ex)
            {
                // The drop path must survive anything. A radio has just died; an
                // exception here would take the seal with it.
                Tracing.TraceLineDeferred("CaptureSeal: collecting the partial meter window failed: " + ex.Message,
                                          TraceLevel.Warning);
            }

            var request = new CaptureSealRequest
            {
                ExpectedSession = expected,
                DropOperationId = operationId,
                OutcomeDetail = OutcomeDetail(radioName ?? string.Empty),
                PartialMeterLine = partial,
            };

            string name = radioName ?? string.Empty;
            Queue(() => SealNow(hook, name, request));
        }

        // ── The claim ──────────────────────────────────────────────────────

        private static readonly object _claimGate = new object();
        private static Guid _sessionFallbackClaim;
        private static bool _hasSessionFallbackClaim;

        /// <summary>
        /// Claim this drop. Returns true to exactly one removal per connection.
        ///
        /// <para>The key is the CONNECTION's lifetime, not a flag, not the
        /// object alone and not a timer. A process-global bit could not do this
        /// job: the seal restarts the standing log, restarting a log begins a
        /// session, and the session is what re-armed the bit — so the guard
        /// disarmed itself in time for the duplicate it existed to refuse. An
        /// object plus a window could not either: the vendor does not guarantee
        /// a fresh object per reconnect, so the comparison is only as good as
        /// the lifetime behind it.</para>
        ///
        /// <para>With no object to compare, it falls back to one seal per trace
        /// session. That is the old rule, and it is still the safe answer when
        /// identity is unavailable — it was only ever wrong because a session
        /// event re-armed it, and nothing does that now.</para>
        /// </summary>
        private static bool TryClaimDrop(object dropToken, Guid sessionId, out Guid operationId)
        {
            operationId = Guid.Empty;
            if (dropToken != null)
            {
                ConnectionLifetime.Token token = ConnectionLifetime.TokenFor(dropToken);
                if (token == null)
                {
                    // A removal carrying an object this process never bound. It
                    // still identifies one drop, so give it a lifetime of its
                    // own rather than falling all the way back to the session.
                    token = ConnectionLifetime.Bind(dropToken, "unbound removal", out _);
                }
                if (!ConnectionLifetime.TryClaimLoss(token)) return false;
                operationId = OperationIdFor(token);
                return true;
            }

            lock (_claimGate)
            {
                if (_hasSessionFallbackClaim && _sessionFallbackClaim == sessionId) return false;
                _hasSessionFallbackClaim = true;
                _sessionFallbackClaim = sessionId;
            }
            operationId = sessionId;
            return true;
        }

        /// <summary>
        /// A stable operation identity for one connection's loss, so the trace
        /// boundary answers a second attempt with the first one's ticket. Built
        /// from the token's ordinal, which is unique within the process.
        /// </summary>
        private static Guid OperationIdFor(ConnectionLifetime.Token token)
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(token.Ordinal).CopyTo(bytes, 0);
            bytes[15] = 0xD0; // "drop", so it cannot collide with a session id
            return new Guid(bytes);
        }

        /// <summary>Forget the claim. Tests only: production never needs this,
        /// because a new drop brings a new connection with it.</summary>
        internal static void ForgetClaimForTests()
        {
            lock (_claimGate)
            {
                _hasSessionFallbackClaim = false;
                _sessionFallbackClaim = Guid.Empty;
            }
            ConnectionLifetime.ResetForTests();
        }

        // ── The worker ─────────────────────────────────────────────────────

        private static void SealNow(Func<CaptureSealRequest, CaptureSealResult> hook,
                                    string radioName,
                                    CaptureSealRequest request)
        {
            CaptureSealResult result = null;
            try
            {
                // The lines the fall wrote on FlexLib's thread were deferred so
                // that thread never waited on the trace gate. Write them NOW,
                // before anything seals, so they land in the session they
                // describe rather than in its successor. This thread may wait;
                // it is the worker, and waiting is its job.
                Tracing.FlushDeferred();
                Tracing.TraceLine(
                    "CaptureSeal: sealing the running capture as " + TraceSessionOutcome.ConnectionDropped
                    + " — " + request.OutcomeDetail,
                    TraceLevel.Warning);
                result = hook(request);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("CaptureSeal: sealing failed: " + ex.Message, TraceLevel.Error);
            }

            if (result == null || string.IsNullOrEmpty(result.ArchivePath))
            {
                // Nothing to point the operator at. Saying nothing is right
                // here: a dialog offering a path that does not exist is worse
                // than no dialog, and the standing log — restarted by the hook —
                // carries this line.
                Tracing.TraceLine(
                    "CaptureSeal: no archive was produced, so there is no path to show the operator"
                    + (result != null && !string.IsNullOrEmpty(result.RawRetainedPath)
                        ? "; the raw trace is retained at " + result.RawRetainedPath
                        : string.Empty),
                    TraceLevel.Warning);
                return;
            }

            Tracing.TraceLine("CaptureSeal: sealed to " + result.ArchivePath, TraceLevel.Warning);

            try
            {
                SealedAfterDrop?.Invoke(new CaptureSealNotice(
                    radioName, result.ArchivePath, result.SuccessorOpened, result.ArchivedSessionId));
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("CaptureSeal: telling the operator failed: " + ex.Message, TraceLevel.Error);
            }
        }
    }
}
