#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;

namespace Radios.SmartLink
{
    /// <summary>
    /// The proof rule's numbers (the tiers plan, decision 10, ruled by Noel
    /// 2026-11-19). An object rather than constants so a test can drive the
    /// rule on a controlled clock, and so the bench's answers arrive as a
    /// change of value, not of code.
    /// </summary>
    public sealed class SmartLinkProofRules
    {
        /// <summary>
        /// How long after FlexLib's connect returns (the command connection up
        /// and the VITA socket made) qualified UDP may take to arrive. Fifteen
        /// seconds "until the bench measures otherwise" (decision 10). The
        /// WATCH starts earlier, at socket creation, so nothing that arrives
        /// before the open completes is missed; only this deadline counts from
        /// the transport being up, because on a hole punch the socket exists
        /// for the whole TCP and TLS stage, before the radio has any reason to
        /// send to it.
        /// </summary>
        public const int DefaultWindowMs = 15_000;

        /// <summary>
        /// OFF (zero). The hardware gate's open question (plan section 4):
        /// whether qualified VITA arriving proves the radio heard OUR
        /// registration. If the bench shows it does not, success additionally
        /// needs the command connection to survive the radio's registration
        /// timeout after the evidence is in, and this becomes that time (about
        /// ten seconds). Until the bench answers, the rule is the ruled one:
        /// a completed open plus the sighting.
        /// </summary>
        public const int DefaultSurvivalMs = 0;

        /// <summary>
        /// ONE overall budget for a method attempt, from its request to its
        /// proof. Neither bench (E1, E2) has measured it, so it is the sum of
        /// the bounds the stages already carry and therefore pre-empts none of
        /// them: 5 s for the handle; up to two TCP connects, each with 15 s
        /// connect and 15 s handshake limits (a failed TLS handshake redials
        /// once for TLS 1.2), 60 s; 5 s for the client address; the open, 45 s
        /// for the station name with the antenna list's 20 s beside it; and
        /// the proof window's 15 s. A dead forwarded port usually costs one
        /// 15 s TCP timeout, which is the common case, not this bound. It is a
        /// backstop against a stage that overruns its own bound, and the
        /// number the benches are asked to replace with a measured one.
        /// </summary>
        public const int DefaultMethodBudgetMs = 130_000;

        public int WindowMs { get; init; } = DefaultWindowMs;
        public int SurvivalMs { get; init; } = DefaultSurvivalMs;
        public int MethodBudgetMs { get; init; } = DefaultMethodBudgetMs;

        public static readonly SmartLinkProofRules Default = new();
    }

    /// <summary>What an attempt made of one qualified-UDP sighting.</summary>
    public enum SightingVerdict
    {
        /// <summary>It is this attempt's proof: its socket, its command connection.</summary>
        Counted = 0,
        /// <summary>Kept until the transport's identity is known; judged then.</summary>
        HeldUntilTransportIsKnown = 1,
        /// <summary>A socket made before this attempt began, or one no longer live.</summary>
        RefusedStaleSocket = 2,
        /// <summary>It arrived under another command connection than this attempt's.</summary>
        RefusedOtherConnection = 3,
        /// <summary>The attempt had already ended; nothing decides it now.</summary>
        IgnoredAttemptOver = 4,
        /// <summary>This attempt already has its sighting.</summary>
        AlreadyCounted = 5,
    }

    /// <summary>
    /// ONE SmartLink method attempt, and everything it owns: its connect
    /// request (with the token that stops us listening for it), the identity
    /// of its command connection and VITA socket, its proof deadline and its
    /// outcome. The tiers plan, section 4.
    /// </summary>
    /// <remarks>
    /// <para><b>Why an object.</b> A method attempt outlives several calls and
    /// two threads: the dial, FlexLib's connect, the open, and the receive
    /// thread's sightings. Each of those used to answer to "whatever is
    /// current". Here every event is handed to the attempt it belongs to, and
    /// an attempt that has ended answers every later event with "ignored",
    /// so a late sighting, a late loss or a late timeout can never decide an
    /// attempt it was not about.</para>
    /// <para><b>Proof</b> (decision 10): a completed open AND a qualified
    /// sighting on this attempt's socket under this attempt's command
    /// connection, in either order. It fails on a terminal loss of this
    /// attempt's command connection, or when no sighting counts within the
    /// window. A client removal followed by a re-add is none of its business:
    /// the open's own wait tolerates it, and this type has no input for
    /// client arrivals or removals at all, so a second operator arriving can
    /// never be taken for its proof.</para>
    /// <para>Pure apart from its clock, which is given. Thread safe.</para>
    /// </remarks>
    public sealed class SmartLinkAttempt
    {
        private readonly object _gate = new();
        private readonly Func<long> _nowMs;
        private readonly SmartLinkProofRules _rules;
        private readonly CancellationTokenSource _request = new();
        private readonly List<(int Generation, object? Connection, TimeSpan SinceSocket, bool SocketCurrent)> _heldSightings = new();
        private readonly List<object> _heldLosses = new();

        private bool _requestSettled;
        private bool _handleArrived;
        private bool _transportUp;
        private object? _connection;
        private int _socketGeneration;
        private long _proofDeadlineMs;
        private bool _openCompleted;
        private TimeSpan? _qualifiedUdpAfter;
        private long _evidenceCompleteAtMs = -1;
        private SmartLinkAttemptEnd _end = SmartLinkAttemptEnd.Pending;
        private string _endDetail = "";
        private int _lateEventsIgnored;

        public SmartLinkAttempt(long connectId, int sequence, SmartLinkMethod method, int socketGenerationFloor,
            Func<long> nowMs, SmartLinkProofRules? rules = null)
        {
            ConnectId = connectId;
            Sequence = sequence;
            Method = method;
            SocketGenerationFloor = socketGenerationFloor;
            _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
            _rules = rules ?? SmartLinkProofRules.Default;
            StartedAtMs = _nowMs();
        }

        /// <summary>The connect this attempt belongs to (<see cref="SmartLinkConnectRecord.ConnectId"/>).</summary>
        public long ConnectId { get; }
        /// <summary>Its place in that connect, from 1.</summary>
        public int Sequence { get; }
        public SmartLinkMethod Method { get; }
        public long StartedAtMs { get; }
        /// <summary>The radio's newest VITA socket BEFORE this attempt. Only a later one can be this attempt's.</summary>
        public int SocketGenerationFloor { get; }

        /// <summary>
        /// The token of this attempt's SmartLink connect request. Cancelled
        /// when the request is given up, and whenever the attempt ends with the
        /// request unanswered. SmartLink has no wire command that cancels a
        /// connect request, so cancelling means exactly this: we stop
        /// listening for its answer.
        /// </summary>
        public CancellationToken RequestToken => _request.Token;

        public bool RequestSettled { get { lock (_gate) return _requestSettled; } }
        public bool HandleArrived { get { lock (_gate) return _handleArrived; } }
        public bool TransportUp { get { lock (_gate) return _transportUp; } }
        public bool OpenCompleted { get { lock (_gate) return _openCompleted; } }
        /// <summary>This attempt's command connection; null until the transport is up.</summary>
        public object? Connection { get { lock (_gate) return _connection; } }
        /// <summary>This attempt's VITA socket; 0 until the transport is up, or when it made none.</summary>
        public int SocketGeneration { get { lock (_gate) return _socketGeneration; } }
        /// <summary>From this attempt's socket creation to the sighting that counted; null if none has.</summary>
        public TimeSpan? QualifiedUdpAfter { get { lock (_gate) return _qualifiedUdpAfter; } }
        public SmartLinkAttemptEnd End { get { lock (_gate) return _end; } }
        public string EndDetail { get { lock (_gate) return _endDetail; } }
        /// <summary>True once the attempt has its final end. Nothing changes it afterwards.</summary>
        public bool IsOver { get { lock (_gate) return Over; } }
        public bool IsProven { get { lock (_gate) return _end == SmartLinkAttemptEnd.Proven; } }
        /// <summary>Ended, and not proven: an open still running for this attempt must stop.</summary>
        public bool HasFailed { get { lock (_gate) return Over && _end != SmartLinkAttemptEnd.Proven; } }
        /// <summary>Events that arrived after the end and decided nothing. For the trace and the tests.</summary>
        public int LateEventsIgnored { get { lock (_gate) return _lateEventsIgnored; } }

        private bool Over => _end != SmartLinkAttemptEnd.Pending && _end != SmartLinkAttemptEnd.Dialled;

        /// <summary>
        /// The request is settled: its handle arrived, or it is given up and
        /// its token cancelled so a late answer finds nobody listening.
        /// </summary>
        public void SettleRequest(bool handleArrived)
        {
            bool cancel;
            lock (_gate)
            {
                if (_requestSettled) return;
                _requestSettled = true;
                _handleArrived = handleArrived && !Over;
                if (_handleArrived) _end = SmartLinkAttemptEnd.Dialled;
                cancel = !handleArrived;
            }
            if (cancel) CancelRequestToken();
        }

        /// <summary>
        /// FlexLib's connect returned true: this is the attempt's command
        /// connection and its VITA socket, and the proof window runs from now.
        /// Sightings and losses that arrived first are judged here.
        /// </summary>
        public void NoteTransportUp(object? connection, int socketGeneration)
        {
            lock (_gate)
            {
                if (Over) { _lateEventsIgnored++; return; }
                _transportUp = true;
                _connection = connection;
                // A socket not newer than the floor was made by an earlier
                // attempt: this attempt has none, and can never be proven.
                _socketGeneration = socketGeneration > SocketGenerationFloor ? socketGeneration : 0;
                _proofDeadlineMs = _nowMs() + _rules.WindowMs;

                foreach (object lost in _heldLosses)
                {
                    if (connection != null && ReferenceEquals(lost, connection))
                    {
                        EndLocked(SmartLinkAttemptEnd.CommandConnectionLost,
                            "the connection to the radio was lost as soon as it was made");
                        break;
                    }
                }
                _heldLosses.Clear();
                if (!Over)
                {
                    foreach (var s in _heldSightings)
                    {
                        if (JudgeLocked(s.Generation, s.Connection, s.SocketCurrent) == SightingVerdict.Counted)
                        {
                            CountLocked(s.SinceSocket);
                            break;
                        }
                    }
                }
                _heldSightings.Clear();
            }
        }

        /// <summary>
        /// One qualified-UDP sighting (MIGRATION.md items 30 and 31): Flex
        /// VITA from the radio's own address. It counts only on this attempt's
        /// own live socket and under this attempt's own command connection.
        /// </summary>
        /// <param name="socketGeneration">The socket that saw it.</param>
        /// <param name="connection">The command connection current when it arrived.</param>
        /// <param name="sinceSocketCreated">From that socket's creation to the packet.</param>
        /// <param name="socketIsCurrent">Whether that socket is still the radio's live one.</param>
        public SightingVerdict NoteSighting(int socketGeneration, object? connection, TimeSpan sinceSocketCreated, bool socketIsCurrent)
        {
            lock (_gate)
            {
                if (Over) { _lateEventsIgnored++; return SightingVerdict.IgnoredAttemptOver; }
                if (socketGeneration <= SocketGenerationFloor || !socketIsCurrent) return SightingVerdict.RefusedStaleSocket;
                if (!_transportUp)
                {
                    _heldSightings.Add((socketGeneration, connection, sinceSocketCreated, socketIsCurrent));
                    return SightingVerdict.HeldUntilTransportIsKnown;
                }
                var verdict = JudgeLocked(socketGeneration, connection, socketIsCurrent);
                if (verdict == SightingVerdict.Counted) CountLocked(sinceSocketCreated);
                return verdict;
            }
        }

        private SightingVerdict JudgeLocked(int socketGeneration, object? connection, bool socketIsCurrent)
        {
            if (_qualifiedUdpAfter != null) return SightingVerdict.AlreadyCounted;
            if (!socketIsCurrent || _socketGeneration == 0 || socketGeneration != _socketGeneration)
                return SightingVerdict.RefusedStaleSocket;
            if (_connection == null || !ReferenceEquals(connection, _connection))
                return SightingVerdict.RefusedOtherConnection;
            return SightingVerdict.Counted;
        }

        private void CountLocked(TimeSpan sinceSocketCreated)
        {
            _qualifiedUdpAfter = sinceSocketCreated;
            EvaluateLocked();
        }

        /// <summary>The open completed (the radio's setup data is in and it took our station name).</summary>
        public void NoteOpenCompleted()
        {
            lock (_gate)
            {
                if (Over) { _lateEventsIgnored++; return; }
                _openCompleted = true;
                EvaluateLocked();
            }
        }

        /// <summary>
        /// A command connection fell, having been up. A method failure when it
        /// is THIS attempt's connection and the attempt is not yet proven; any
        /// other connection's fall is not about this attempt.
        /// </summary>
        public bool NoteCommandConnectionLost(object? connection)
        {
            lock (_gate)
            {
                if (Over) { _lateEventsIgnored++; return false; }
                if (connection == null) return false;
                if (!_transportUp)
                {
                    _heldLosses.Add(connection);
                    return false;
                }
                if (!ReferenceEquals(connection, _connection)) return false;
                EndLocked(SmartLinkAttemptEnd.CommandConnectionLost,
                    "the connection to the radio was made, then lost before it was confirmed");
                return true;
            }
        }

        /// <summary>
        /// Read the clock against this attempt's deadlines. Called by whoever
        /// is waiting on it (the open's waits, and the wait for proof after
        /// the open), so no timer thread is needed. Returns the end as it now
        /// stands.
        /// </summary>
        public SmartLinkAttemptEnd Poll()
        {
            lock (_gate)
            {
                if (Over || !_transportUp) return _end;
                long now = _nowMs();
                if (_qualifiedUdpAfter == null && now >= _proofDeadlineMs)
                {
                    EndLocked(SmartLinkAttemptEnd.NoQualifiedUdp,
                        $"the connection was made, but no audio or data came from the radio within {_rules.WindowMs / 1000} seconds");
                    return _end;
                }
                EvaluateLocked();
                if (!Over && now >= StartedAtMs + _rules.MethodBudgetMs)
                {
                    EndLocked(SmartLinkAttemptEnd.OpenFailed,
                        $"the radio did not finish opening within the {_rules.MethodBudgetMs / 1000} seconds a method is given");
                }
                return _end;
            }
        }

        private void EvaluateLocked()
        {
            if (Over || !_openCompleted || _qualifiedUdpAfter == null) return;
            if (_evidenceCompleteAtMs < 0) _evidenceCompleteAtMs = _nowMs();
            if (_rules.SurvivalMs <= 0 || _nowMs() >= _evidenceCompleteAtMs + _rules.SurvivalMs)
            {
                EndLocked(SmartLinkAttemptEnd.Proven,
                    "the radio opened, and its audio and data arrived on this connection");
            }
        }

        /// <summary>
        /// End the attempt. The first end stands; a later one is ignored and
        /// counted, and false comes back. <see cref="SmartLinkAttemptEnd.Pending"/>
        /// and <see cref="SmartLinkAttemptEnd.Dialled"/> are not ends.
        /// </summary>
        public bool TryEnd(SmartLinkAttemptEnd end, string detail)
        {
            if (end == SmartLinkAttemptEnd.Pending || end == SmartLinkAttemptEnd.Dialled)
                throw new ArgumentException("not a final end", nameof(end));
            bool ended;
            lock (_gate)
            {
                if (Over) { _lateEventsIgnored++; return false; }
                EndLocked(end, detail);
                ended = true;
            }
            return ended;
        }

        private void EndLocked(SmartLinkAttemptEnd end, string detail)
        {
            _end = end;
            _endDetail = detail ?? "";
            if (!_requestSettled)
            {
                _requestSettled = true;
                // Outside the lock would be tidier, but a token's callbacks
                // here only complete a waiting task; nothing re-enters.
                CancelRequestToken();
            }
        }

        private void CancelRequestToken()
        {
            try { _request.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { /* a listener's fault is not the attempt's */ }
        }

        public override string ToString() =>
            $"connect#{ConnectId} attempt {Sequence} {Method} end={End}";
    }
}
