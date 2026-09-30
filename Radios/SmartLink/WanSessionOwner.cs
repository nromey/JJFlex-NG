#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios.SmartLink
{
    /// <summary>
    /// Owns a SmartLink session lifecycle. Dedicated monitor thread implements
    /// the behavioral spec from <c>docs/planning/hole-punch-lifeline-ragchew.md</c>:
    /// retry with exponential backoff on Connect failure, wake on
    /// <see cref="IWanServer.PropertyChanged"/> IsConnected transitions, clean
    /// shutdown via an explicit flag.
    ///
    /// <para>
    /// <b>Backoff schedule:</b> 1s → 5s → 30s → 30s → … (caps at 30s).
    /// Resets to index 0 on every successful Connect.
    /// </para>
    ///
    /// <para>
    /// <b>Threading:</b> the monitor thread is the only thread that calls
    /// <see cref="IWanServer.Connect"/> or <see cref="IWanServer.Disconnect"/>.
    /// Public methods on this class post a wake signal and return immediately;
    /// they do not block on network I/O. Events (<see cref="StatusChanged"/>,
    /// <see cref="SignalThresholdCrossed"/>) fire on the monitor thread — UI
    /// consumers must marshal to the dispatcher thread before touching controls.
    /// </para>
    /// </summary>
    public sealed class WanSessionOwner : IWanSessionOwner
    {
        // Backoff schedule in milliseconds. Exposed internal for unit-test visibility.
        internal static readonly int[] BackoffScheduleMs = { 1000, 5000, 30000 };

        private readonly IWanServer _wan;
        private readonly ISessionAudioSink _audioSink;
        private readonly NetworkTestRunner _networkTestRunner;
        private readonly string _tracePrefix;
        private readonly Thread _monitorThread;
        private readonly AutoResetEvent _wakeEvent = new(initialState: false);
        private readonly System.Threading.Lock _stateGate = new();
        private readonly int[] _backoffScheduleMs;

        // State guarded by _stateGate for cross-thread reads. Mutated only on the monitor thread
        // except for _userWantsConnected / _shutdownRequested which are user-API driven.
        private SessionStatus _status;
        private Exception? _lastError;
        private int _reconnectAttemptCount;
        private IReadOnlyList<Radio> _availableRadios = Array.Empty<Radio>();
        private DateTime? _lastRadioListUtc;

        // Which connection the list above arrived on, and which connection is
        // live (#619). The list is kept across a drop on purpose — the
        // post-drop diagnostic probe reads it, and the connect flow replays it
        // — so emptying it at the boundary would break both. Instead each list
        // carries the generation of the transport it was born on, stamped by
        // the adapter where that transport was created, and a list describes
        // the live connection only while its generation is the live one.
        //
        // Track L3 numbered the connections HERE, advancing on every dial and
        // every IsConnected edge, and stamped a list with the number current
        // when its callback reached this owner. Sol's review of L3 found the
        // hole: FlexLib invokes the handler on the transport's read loop and
        // does not wait for one in flight when it closes the transport, so an
        // old connection's callback could pause, cross the reconnect edge, and
        // be stamped as the new connection's. The stamp is provenance now,
        // and this owner only correlates: it compares the stamp with the
        // newest generation the server has dialed.
        //
        // Track L4 read that newest generation from the server, lock-free,
        // and then took _stateGate to accept the list. Sol's review of L4
        // found the gap between the two: an old callback could read "newest
        // is 1", pause, and resume after a dial had published 2 — and then
        // store and re-raise its list as current, during the dial, before
        // AttemptConnect had caught up. So the check and the acceptance are
        // ONE critical section now, and the retirement joins it: the server
        // raises ConnectionDialing before the new transport exists, the
        // handler records it under _stateGate, and the list handler decides
        // under the same _stateGate. A callback is decided either before that
        // record (its connection really was the newest at that moment) or
        // after (refused); there is no third order. Nothing reads the
        // server's generation back after the dial any more, because a
        // fallback that catches up later is exactly a window.
        //
        // The "larger of the two" arm survives for a list whose generation is
        // newer than anything recorded. Through the real adapter it is
        // unreachable — the transport is created after the event — but it is
        // the correct reading of such a list if a server ever produced one,
        // and the suite's mock can.
        private long _liveConnectionGeneration;
        private long _radioListConnectionGeneration = -1;

        // The live connection has been torn down BY THIS OWNER — the operator
        // asked to disconnect, Reset, or Dispose — and nothing has been dialed
        // since. Its generation is still the newest, so the generation alone
        // would let a list from the closed transport in; Sol's review of L4
        // named that timing. A transport that DIES is not marked here: the
        // owner never knows the instant, the IsConnected edge can arrive late
        // or twice (which is why that handler does no bookkeeping), and the
        // snapshot already reports such a list as not live through the
        // session status. Cleared by the next ConnectionDialing.
        private bool _liveConnectionRetired;

        /// <summary>
        /// What the live connection's OWN transport has said about itself,
        /// recorded from the generation-stamped edge the server raises
        /// (<see cref="IWanServer.TransportStateChanged"/>), under _stateGate.
        /// The monitor's status is not this: it changes when the monitor
        /// thread gets round to it, and between the transport's death and
        /// that transition the session reads Connected and a list held from
        /// the dead transport reads as the live connection's (#619, Sol's
        /// review of L5). A list decided while this says Down is refused; a
        /// snapshot holding such a list is not live. NotYetUp is distinct
        /// from Down on purpose: a list can in principle be parsed before the
        /// vendor sets IsConnected true, and that list is not from a dead
        /// transport, so only Down refuses. Reset to NotYetUp by every
        /// ConnectionDialing; edges for any other generation are ignored by
        /// name.
        /// </summary>
        private enum TransportState { NotYetUp, Up, Down }
        private TransportState _liveTransportState = TransportState.NotYetUp;

        /// <summary>
        /// For the suite only. Runs at the top of the list handler, on the
        /// delivering thread, before the owner decides anything and before it
        /// takes any lock. A test parks a callback here so the overlap Sol
        /// named — a callback that has reached the owner when a dial begins
        /// and is decided while the dial is in progress — can be held exactly,
        /// rather than approximated with an event delivered after the fact.
        /// Null in production and never read by anything else.
        /// </summary>
        internal Action? BeforeListDecision;

        /// <summary>
        /// For the suite only. Runs after the owner has ACCEPTED a list under
        /// its lock and released the lock, and before it forwards the list to
        /// the coordinator. A test parks a callback here to hold the exact
        /// ordering Sol's review of L5 named: an accepted list that pauses
        /// while a dial begins and the next connection's list is accepted and
        /// forwarded, then arrives at the consumer late. L5's test parked
        /// before the decision, which is why it could not see this. Null in
        /// production and never read by anything else.
        /// </summary>
        internal Action? BeforeListForwarded;

        private volatile bool _userWantsConnected;
        private volatile bool _shutdownRequested;
        private volatile bool _started;
        private volatile bool _hasBeenConnected; // true after any successful Connect; reset only on Reset() or Dispose

        // --- Auto-registration (Sprint 35 Track K, #259 held-open sessions) ---
        // A session that lives for hours owns its own registration: the server
        // only pushes radio lists to a REGISTERED session, and a 2 AM TLS drop
        // that the monitor thread quietly reconnects would otherwise come back
        // connected-but-unregistered — presence stops arriving and nothing says
        // why. When a JWT provider is wired, the monitor thread registers after
        // every successful Connect and answers a registration-invalid push with
        // ONE silent token-refresh + re-register before giving up.
        private Func<bool, string?>? _registrationJwtProvider; // arg: forceRefresh
        private string _registrationProgramName = "";
        private string _registrationPlatform = "Win10";
        private bool _registeredThisConnection;   // guarded by _stateGate; a registration has been sent on the current connection
        private bool _registrationRecoveryTried;  // guarded by _stateGate; reset on drop and on any list receipt
        private volatile bool _registrationInvalidPending;

        // Retry cadence when auto-registration could not complete (no JWT
        // available silently, send threw). Connected-but-unregistered receives
        // no pushes, so the monitor retries rather than sleeping forever — but
        // each retry can cost an Auth0 refresh round trip, so the interval
        // escalates: 30s, 1m, 2m, … capped at 30m. An account whose refresh
        // token is genuinely revoked must not poke the token endpoint 2,880
        // times a day. Resets on success and on connection drop.
        internal const int RegistrationRetryMs = 30_000;
        internal const int RegistrationRetryMaxMs = 1_800_000;
        private int _registrationRetryCount; // monitor thread only

        // Pending ConnectToRadio request. Only one may be in flight per session at a time.
        // Completes with the WAN connection handle (string) on success, or null on failure.
        private TaskCompletionSource<string?>? _pendingRadioConnect;
        private string? _pendingRadioSerial;

        public string SessionId { get; }
        public string AccountId { get; }
        public ISessionAudioSink AudioSink => _audioSink;

        public event EventHandler<SessionStatus>? StatusChanged;
        public event EventHandler<SignalThresholdEventArgs>? SignalThresholdCrossed;
        public event EventHandler<NetworkDiagnosticReport>? NetworkReportReady;
        public event EventHandler<WanRadioListReceivedEventArgs>? RadioListReceived;
        public event EventHandler? ListCurrencyMayHaveChanged;

        /// <summary>
        /// Tell consumers that a list this session delivered may have stopped
        /// being current. Called after the change is recorded and outside
        /// _stateGate, so a consumer that asks <see cref="ListIsCurrent"/> at
        /// once gets the new answer and cannot re-enter the lock from here.
        /// A consumer's exception is traced, never thrown back into the
        /// transport, the monitor or the operator's call that raised it.
        /// </summary>
        private void RaiseListCurrencyMayHaveChanged(string why)
        {
            Tracing.TraceLine($"{_tracePrefix} list currency may have changed — {why} (#619)", TraceLevel.Info);
            try { ListCurrencyMayHaveChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"{_tracePrefix} a list-currency consumer threw: {ex.Message}", TraceLevel.Warning);
            }
        }

        public WanSessionOwner(
            string sessionId,
            string accountId,
            IWanServer wanServer,
            ISessionAudioSink audioSink,
            int[]? backoffScheduleMs = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId required", nameof(sessionId));
            if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("accountId required", nameof(accountId));
            SessionId = sessionId;
            AccountId = accountId;
            _wan = wanServer ?? throw new ArgumentNullException(nameof(wanServer));
            _audioSink = audioSink ?? throw new ArgumentNullException(nameof(audioSink));
            _tracePrefix = $"[session={sessionId}]";
            _status = SessionStatus.Disconnected;
            _backoffScheduleMs = backoffScheduleMs ?? BackoffScheduleMs;

            _wan.PropertyChanged += OnWanPropertyChanged;
            _wan.ConnectionDialing += OnWanConnectionDialing;
            _wan.TransportStateChanged += OnWanTransportStateChanged;
            _wan.WanRadioRadioListReceived += OnWanRadioListReceived;
            _wan.WanRadioConnectReady += OnWanRadioConnectReady;
            _wan.WanApplicationRegistrationInvalid += OnWanApplicationRegistrationInvalid;

            // Sprint 27 Track C — session owns its NetworkTest runner so the
            // cache is shared across all invocation points (post-connect,
            // on-demand Settings button, future post-disconnect heuristic).
            _networkTestRunner = new NetworkTestRunner(_wan);
            _networkTestRunner.ReportReady += OnNetworkReportReady;

            _monitorThread = new Thread(MonitorLoop)
            {
                Name = $"WanSessionOwner[{sessionId}]",
                IsBackground = true,
            };
        }

        // --- State properties (cross-thread reads) ---

        public bool IsConnected
        {
            get { lock (_stateGate) return _status == SessionStatus.Connected; }
        }

        public SessionStatus Status
        {
            get { lock (_stateGate) return _status; }
        }

        public Exception? LastError
        {
            get { lock (_stateGate) return _lastError; }
        }

        public int ReconnectAttemptCount
        {
            get { lock (_stateGate) return _reconnectAttemptCount; }
        }

        public IReadOnlyList<Radio> AvailableRadios
        {
            get { lock (_stateGate) return _availableRadios; }
        }

        public DateTime? LastRadioListUtc
        {
            get { lock (_stateGate) return _lastRadioListUtc; }
        }

        public SessionRadioListSnapshot RadioListSnapshot
        {
            get
            {
                lock (_stateGate)
                {
                    bool connected = _status == SessionStatus.Connected;
                    // "Live" is the same predicate the list handler accepts
                    // under, applied to the held list's own generation — so
                    // it goes false the moment that connection's transport
                    // reports itself gone, whatever the monitor's status
                    // still says (#619).
                    return new SessionRadioListSnapshot(
                        _availableRadios,
                        _lastRadioListUtc,
                        connected,
                        connected
                            && _lastRadioListUtc != null
                            && RefusalFor(_radioListConnectionGeneration) == null,
                        _radioListConnectionGeneration,
                        SessionId);
                }
            }
        }

        public bool ListIsCurrent(long connectionGeneration)
        {
            lock (_stateGate) return RefusalFor(connectionGeneration) == null;
        }

        /// <summary>
        /// Why a list born on <paramref name="connectionGeneration"/> is NOT
        /// this session's current knowledge right now, or null when it is.
        /// The one predicate behind the list handler's acceptance, the
        /// snapshot's liveness and <see cref="ListIsCurrent"/>, so the three
        /// cannot drift. Caller holds _stateGate.
        /// </summary>
        private string? RefusalFor(long connectionGeneration)
        {
            if (connectionGeneration < _liveConnectionGeneration)
                return $"connection {_liveConnectionGeneration} has been dialed since, so this is a late callback from a replaced transport";
            if (connectionGeneration == _liveConnectionGeneration && _liveConnectionRetired)
                return $"this session disconnected connection {_liveConnectionGeneration} and has dialed nothing since, so this is a late callback from a transport it closed";
            if (connectionGeneration == _liveConnectionGeneration && _liveTransportState == TransportState.Down)
                return $"connection {_liveConnectionGeneration}'s transport has reported itself gone and nothing has been dialed since, so this is a late callback from a dead transport";
            // Dispose sets this too, so one clause covers a list from a
            // connection dialed after Dispose as well as one arriving after
            // Disconnect() and before the monitor has marked the connection
            // retired. A separate shutdown clause was written first and
            // removed when its mutation turned nothing red: Dispose never
            // clears the operator's intent, so the clause could not be
            // observed (#637's lesson, applied here).
            if (!_userWantsConnected)
                return "this session has been asked to disconnect, or is being disposed, and has not been asked to connect since";
            return null;
        }

        // --- Public commands ---

        public void Connect()
        {
            Tracing.TraceLine($"{_tracePrefix} Connect requested", TraceLevel.Info);
            _userWantsConnected = true;

            // AuthorizationExpired is sticky through teardown (it is the one
            // fact the operator can act on), so a NEW connect must clear it
            // SYNCHRONOUSLY: consumers poll `Status == AuthorizationExpired`
            // as their auth-failed signal, and a stale read between this call
            // and the monitor's first transition would report yesterday's
            // failure as today's.
            if (Status == SessionStatus.AuthorizationExpired)
            {
                TransitionStatus(SessionStatus.Connecting, resetAttempts: false);
            }

            if (!_started)
            {
                _started = true;
                _monitorThread.Start();
            }

            _wakeEvent.Set();
        }

        public void Disconnect()
        {
            Tracing.TraceLine($"{_tracePrefix} Disconnect requested", TraceLevel.Info);
            _userWantsConnected = false;

            // Fail any in-flight ConnectToRadio so waiters don't hang.
            CancelPendingRadioConnect("session disconnected");

            // The operator's intent is one of RefusalFor's clauses, so every
            // list this session holds stopped being current on the line above
            // that cleared it. Said here, on the caller's thread, before the
            // monitor wakes and tears the transport down (#619).
            RaiseListCurrencyMayHaveChanged("the session was asked to disconnect");

            _wakeEvent.Set();
        }

        public void Reset()
        {
            Tracing.TraceLine($"{_tracePrefix} Reset requested", TraceLevel.Info);
            _userWantsConnected = true;
            _hasBeenConnected = false;
            lock (_stateGate)
            {
                _reconnectAttemptCount = 0;
                _lastError = null;
                // Torn down on purpose; the redial will retire it by number
                // as well, but a list landing between the two is history.
                _liveConnectionRetired = true;
            }
            try { _wan.Disconnect(); } catch { /* intentional — monitor loop re-tries */ }
            _wakeEvent.Set();
        }

        public void ReRegister(string programName, string platform, string jwt)
        {
            if (!IsConnected)
            {
                Tracing.TraceLine($"{_tracePrefix} ReRegister skipped — not connected", TraceLevel.Warning);
                return;
            }
            Tracing.TraceLine($"{_tracePrefix} ReRegister program={programName}", TraceLevel.Info);
            // A deliberate caller registration counts as this connection's
            // registration — the monitor's auto-register must not double it.
            lock (_stateGate) _registeredThisConnection = true;
            _wan.SendRegisterApplicationMessageToServer(programName, platform, jwt);
        }

        /// <summary>
        /// Wire the session to keep ITSELF registered (Sprint 35 Track K,
        /// #259). <paramref name="jwtProvider"/> is called on the monitor
        /// thread — it may block on a silent token refresh — with
        /// <c>forceRefresh</c> true only on the registration-invalid recovery
        /// path. Returning null means "no JWT available without UI"; the
        /// monitor then retries on a timer rather than surprising the operator
        /// with a sign-in form (#85: a background session never takes the
        /// foreground). Idempotent; last wiring wins.
        /// </summary>
        public void EnableAutoRegistration(Func<bool, string?> jwtProvider, string programName, string platform = "Win10")
        {
            _registrationProgramName = programName ?? "";
            _registrationPlatform = string.IsNullOrWhiteSpace(platform) ? "Win10" : platform;
            _registrationJwtProvider = jwtProvider ?? throw new ArgumentNullException(nameof(jwtProvider));
            Tracing.TraceLine($"{_tracePrefix} auto-registration enabled program={_registrationProgramName}", TraceLevel.Info);
            _wakeEvent.Set();
        }

        /// <summary>
        /// Atomically claim the one registration this connection needs.
        /// True = the caller should send it; false = someone (the monitor's
        /// auto-register or an earlier explicit <see cref="ReRegister"/>)
        /// already has, and sending again would only poke the server. The
        /// claim resets whenever the underlying connection drops.
        /// </summary>
        public bool TryClaimRegistration()
        {
            lock (_stateGate)
            {
                if (_registeredThisConnection) return false;
                _registeredThisConnection = true;
                return true;
            }
        }

        public Task<NetworkDiagnosticReport> RunNetworkDiagnosticAsync(
            string radioSerial,
            bool forceRefresh = false,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            return _networkTestRunner.RunAsync(radioSerial, forceRefresh, timeout, cancellationToken);
        }

        public NetworkDiagnosticReport? GetLastNetworkReport(string radioSerial)
        {
            return _networkTestRunner.GetLastReport(radioSerial);
        }

        public NetworkDiagnosticReport? MostRecentNetworkReport => _networkTestRunner.MostRecent;

        private void OnNetworkReportReady(object? sender, NetworkDiagnosticReport report)
        {
            NetworkReportReady?.Invoke(this, report);
        }

        public async Task<string?> ConnectToRadio(string serial, int holePunchPort = 0, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                Tracing.TraceLine($"{_tracePrefix} ConnectToRadio requested but session not connected", TraceLevel.Warning);
                return null;
            }

            TaskCompletionSource<string?> tcs;
            lock (_stateGate)
            {
                if (_pendingRadioConnect is { } prior && !prior.Task.IsCompleted)
                {
                    Tracing.TraceLine(
                        $"{_tracePrefix} ConnectToRadio {serial} overlaps pending {_pendingRadioSerial}; cancelling prior",
                        TraceLevel.Warning);
                    prior.TrySetResult(null);
                }
                tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingRadioConnect = tcs;
                _pendingRadioSerial = serial;
            }

            Tracing.TraceLine($"{_tracePrefix} ConnectToRadio serial={serial} holePunchPort={holePunchPort}", TraceLevel.Info);

            using var ctr = cancellationToken.Register(() =>
            {
                if (tcs.TrySetCanceled(cancellationToken))
                {
                    Tracing.TraceLine($"{_tracePrefix} ConnectToRadio serial={serial} cancelled by token", TraceLevel.Info);
                }
            });

            try
            {
                _wan.SendConnectMessageToRadio(serial, holePunchPort);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"{_tracePrefix} ConnectToRadio SendConnectMessageToRadio threw: {ex.Message}", TraceLevel.Error);
                tcs.TrySetResult(null);
            }

            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_pendingRadioConnect, tcs))
                    {
                        _pendingRadioConnect = null;
                        _pendingRadioSerial = null;
                    }
                }
            }
        }

        // --- Monitor thread ---

        private void MonitorLoop()
        {
            Tracing.TraceLine($"{_tracePrefix} monitor thread start", TraceLevel.Info);

            while (!_shutdownRequested)
            {
                if (!_userWantsConnected)
                {
                    // User wants to stay disconnected; ensure underlying session is torn down and sleep until signaled.
                    //
                    // Retired BEFORE the teardown, under the lock the list
                    // handler decides under, so a list the closing transport
                    // delivers from here on is refused — its generation is
                    // still the newest, and generation alone would let it
                    // through to the coordinator and the intake after the
                    // operator asked to be disconnected (Sol's review of L4,
                    // #619). Idempotent, so the loop can pass here freely.
                    lock (_stateGate) _liveConnectionRetired = true;
                    if (_wan.IsConnected)
                    {
                        try { _wan.Disconnect(); } catch (Exception ex) { TraceWarn("Disconnect threw", ex); }
                    }
                    // AuthorizationExpired stays visible through the teardown:
                    // "disconnected" would hide the one thing the operator can
                    // act on. A fresh Connect() clears it (see Connect).
                    if (Status != SessionStatus.AuthorizationExpired)
                    {
                        TransitionStatus(SessionStatus.Disconnected, resetAttempts: true);
                    }
                    _wakeEvent.WaitOne();
                    continue;
                }

                if (!_wan.IsConnected)
                {
                    AttemptConnect();
                }
                else
                {
                    // Connected — make sure the session is registered (a held
                    // session that reconnected at 2 AM must not sit connected-
                    // but-unregistered, receiving no pushes), then sleep until
                    // IsConnected flips or user action signals.
                    TransitionStatus(SessionStatus.Connected, resetAttempts: true);
                    bool registrationHealthy = ServiceRegistration();
                    if (_shutdownRequested || !_userWantsConnected) continue;
                    if (registrationHealthy)
                    {
                        _registrationRetryCount = 0;
                        _wakeEvent.WaitOne();
                    }
                    else
                    {
                        long waitMs = Math.Min(
                            (long)RegistrationRetryMs << Math.Min(_registrationRetryCount, 10),
                            RegistrationRetryMaxMs);
                        _registrationRetryCount++;
                        Tracing.TraceLine($"{_tracePrefix} auto-registration unhealthy — retrying in {waitMs / 1000}s", TraceLevel.Info);
                        _wakeEvent.WaitOne((int)waitMs);
                    }
                }
            }

            // Shutdown path: attempt clean tear-down of the underlying session.
            try { _wan.Disconnect(); } catch (Exception ex) { TraceWarn("Disconnect during shutdown threw", ex); }
            TransitionStatus(SessionStatus.ShutDown, resetAttempts: true);
            Tracing.TraceLine($"{_tracePrefix} monitor thread exit", TraceLevel.Info);
        }

        private void AttemptConnect()
        {
            int attemptIndex;
            lock (_stateGate)
            {
                attemptIndex = _reconnectAttemptCount;
                // Any registration belonged to the connection that just
                // dropped; the one we are about to dial needs its own.
                _registeredThisConnection = false;
                _registrationRecoveryTried = false;
            }
            _registrationInvalidPending = false;
            _registrationRetryCount = 0;

            var attemptStatus = (_hasBeenConnected || attemptIndex > 0)
                ? SessionStatus.Reconnecting
                : SessionStatus.Connecting;
            TransitionStatus(attemptStatus, resetAttempts: false);

            try
            {
                Tracing.TraceLine($"{_tracePrefix} Connect attempt index={attemptIndex}", TraceLevel.Info);
                _wan.Connect();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"{_tracePrefix} Connect threw: {ex.Message}", TraceLevel.Error);
                lock (_stateGate)
                {
                    _lastError = ex;
                }
            }

            // Which connection is live was recorded INSIDE the dial, by
            // OnWanConnectionDialing, before the transport existed. Nothing is
            // read back here: Track L4 did, and the read-after-the-dial was
            // the window Sol found — an old list accepted during the dial was
            // current until this point caught up (#619).

            if (_wan.IsConnected)
            {
                lock (_stateGate)
                {
                    _reconnectAttemptCount = 0;
                    _lastError = null;
                }
                _hasBeenConnected = true;
                Tracing.TraceLine($"{_tracePrefix} Connect succeeded", TraceLevel.Info);
                return;
            }

            // Failed. Advance backoff and wait the schedule interval (or wake signal).
            int waitMs = BackoffForIndex(attemptIndex, _backoffScheduleMs);
            Tracing.TraceLine($"{_tracePrefix} Connect failed; backoff {waitMs}ms (attempt {attemptIndex + 1})", TraceLevel.Warning);
            lock (_stateGate)
            {
                _reconnectAttemptCount = attemptIndex + 1;
            }
            _wakeEvent.WaitOne(waitMs);
        }

        /// <summary>
        /// Monitor-thread registration keeper. Returns true when registration
        /// is in a healthy state (sent, or not this owner's job); false means
        /// the caller should retry on a timer instead of sleeping forever.
        /// </summary>
        private bool ServiceRegistration()
        {
            // Registration-invalid recovery first: the server just told us the
            // registration we HAD is no good.
            if (_registrationInvalidPending)
            {
                _registrationInvalidPending = false;
                bool alreadyTried;
                lock (_stateGate)
                {
                    alreadyTried = _registrationRecoveryTried;
                    _registrationRecoveryTried = true;
                }
                if (alreadyTried || _registrationJwtProvider == null)
                {
                    Tracing.TraceLine($"{_tracePrefix} registration invalid and silent recovery exhausted — auth required", TraceLevel.Error);
                    TransitionStatus(SessionStatus.AuthorizationExpired, resetAttempts: false);
                    _userWantsConnected = false;
                    return false;
                }
                Tracing.TraceLine($"{_tracePrefix} registration invalid — trying ONE silent token refresh + re-register", TraceLevel.Warning);
                lock (_stateGate) _registeredThisConnection = true;
                if (!TryRegister(forceRefresh: true))
                {
                    Tracing.TraceLine($"{_tracePrefix} silent recovery failed — auth required", TraceLevel.Error);
                    TransitionStatus(SessionStatus.AuthorizationExpired, resetAttempts: false);
                    _userWantsConnected = false;
                    return false;
                }
                return true;
            }

            // No provider: registration stays the caller's business (the
            // pre-#259 interactive flow drives ReRegister itself).
            if (_registrationJwtProvider == null) return true;

            if (!TryClaimRegistration()) return true; // already registered this connection

            if (!TryRegister(forceRefresh: false))
            {
                // Release the claim so the timed retry — or an explicit
                // ReRegister from the interactive flow — can try again.
                lock (_stateGate) _registeredThisConnection = false;
                return false;
            }
            return true;
        }

        private bool TryRegister(bool forceRefresh)
        {
            var provider = _registrationJwtProvider;
            if (provider == null) return false;

            string? jwt = null;
            try
            {
                jwt = provider(forceRefresh);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"{_tracePrefix} registration JWT provider threw: {ex.Message}", TraceLevel.Error);
            }
            if (string.IsNullOrEmpty(jwt))
            {
                Tracing.TraceLine($"{_tracePrefix} auto-registration: no JWT available silently (forceRefresh={forceRefresh})", TraceLevel.Warning);
                return false;
            }

            try
            {
                Tracing.TraceLine($"{_tracePrefix} auto-registration: registering program={_registrationProgramName}", TraceLevel.Info);
                _wan.SendRegisterApplicationMessageToServer(_registrationProgramName, _registrationPlatform, jwt);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"{_tracePrefix} auto-registration: register send threw: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        internal static int BackoffForIndex(int index) => BackoffForIndex(index, BackoffScheduleMs);

        internal static int BackoffForIndex(int index, int[] schedule)
        {
            if (schedule.Length == 0) return 0;
            if (index < 0) index = 0;
            if (index >= schedule.Length) index = schedule.Length - 1;
            return schedule[index];
        }

        private void TransitionStatus(SessionStatus newStatus, bool resetAttempts)
        {
            bool changed = false;
            lock (_stateGate)
            {
                if (_status != newStatus)
                {
                    _status = newStatus;
                    changed = true;
                }
                if (resetAttempts && newStatus == SessionStatus.Connected)
                {
                    _reconnectAttemptCount = 0;
                    _lastError = null;
                }
            }
            if (changed)
            {
                Tracing.TraceLine($"{_tracePrefix} status → {newStatus}", TraceLevel.Info);
                StatusChanged?.Invoke(this, newStatus);

                // Sprint 27 Track D / Phase D.2 — post-disconnect diagnostic
                // probe. On the transition INTO Reconnecting (we had a
                // connection and now we're retrying), fire a NetworkTest so
                // the next status announcement ForStatusRich call has fresh
                // data to inform its overlay. Runner's cache + dedup handle
                // rate-limiting if Reconnecting re-fires rapidly.
                if (newStatus == SessionStatus.Reconnecting)
                {
                    MaybeKickDiagnosticProbe();
                }
            }
        }

        /// <summary>
        /// Sprint 27 Track D / Phase D.2 — fire-and-forget NetworkTest probe
        /// against the first known radio on this session. Silent no-op if no
        /// radios have been announced yet. The runner handles caching, dedup,
        /// and timeout internally; this method just schedules the work off
        /// the monitor thread.
        /// </summary>
        private void MaybeKickDiagnosticProbe()
        {
            IReadOnlyList<Radio> radios;
            lock (_stateGate) { radios = _availableRadios; }
            if (radios.Count == 0)
            {
                Tracing.TraceLine($"{_tracePrefix} D.2 diagnostic probe skipped — no radios known yet", TraceLevel.Info);
                return;
            }

            string serial = radios[0].Serial;
            Tracing.TraceLine($"{_tracePrefix} D.2 kicking post-disconnect diagnostic probe serial={serial}", TraceLevel.Info);

            _ = Task.Run(async () =>
            {
                try
                {
                    var report = await _networkTestRunner.RunAsync(serial).ConfigureAwait(false);
                    Tracing.TraceLine($"{_tracePrefix} D.2 probe complete: probeCompleted={report.ProbeCompleted}", TraceLevel.Info);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"{_tracePrefix} D.2 probe threw: {ex.Message}", TraceLevel.Warning);
                }
            });
        }

        private void TraceWarn(string label, Exception ex)
        {
            Tracing.TraceLine($"{_tracePrefix} {label}: {ex.Message}", TraceLevel.Warning);
        }

        // --- FlexLib event bridging ---

        private void OnWanPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IWanServer.IsConnected))
            {
                // No bookkeeping here, deliberately: which connection a list
                // belongs to is stamped on the list by the adapter and
                // compared in OnWanRadioListReceived, so an edge that arrives
                // late, twice, or on the receive thread cannot relabel
                // anything (#619). Just wake the monitor to re-evaluate
                // whether to reconnect or settle into connected state.
                _wakeEvent.Set();
            }
        }

        /// <summary>
        /// The server is dialing a new connection: record its generation as
        /// the live one, under the lock the list handler decides under. This
        /// runs on the monitor thread, inside the adapter's Connect, before
        /// the transport that will carry the generation exists — so by the
        /// time any list can say this number, it is already the live one
        /// here, and any list saying a smaller number decided after this
        /// point is refused (#619).
        /// </summary>
        private void OnWanConnectionDialing(object? sender, long generation)
        {
            long retiredFrom;
            lock (_stateGate)
            {
                retiredFrom = _liveConnectionGeneration;
                if (generation > _liveConnectionGeneration)
                {
                    _liveConnectionGeneration = generation;
                    _liveConnectionRetired = false;
                    // The transport that will carry this generation does not
                    // exist yet; its own edge says when it is up.
                    _liveTransportState = TransportState.NotYetUp;
                }
            }
            // A dial can overlap Dispose — the monitor decided to dial,
            // Dispose retired the connection, and the dial then began and
            // un-retired it here (Sol's review of L5). That is why the
            // operator's intent, which Dispose also withdraws, is part of
            // RefusalFor rather than a guard on this line: a list from a
            // connection dialed after Dispose is refused by the same
            // predicate whatever the retired flag says.
            Tracing.TraceLine(
                $"{_tracePrefix} connection {generation} dialing — connection {retiredFrom} is retired; a list must say {generation} to be this session's current knowledge (#619)",
                TraceLevel.Info);
            // Every list from an earlier connection is history from the
            // record above. A drop normally says so first, through its
            // transport's own edge; a redial the operator asked for (Reset,
            // a refresh) retires the connection without that edge having
            // been seen here first, so the dial says it too.
            RaiseListCurrencyMayHaveChanged($"connection {generation} is dialing");
        }

        /// <summary>
        /// A transport reported itself up or gone, and said which connection
        /// it carries. Recorded under the list handler's lock when it is the
        /// live connection's; a replaced transport's late report is ignored
        /// by name rather than relabelling anything (#619).
        /// </summary>
        private void OnWanTransportStateChanged(object? sender, WanTransportStateEventArgs e)
        {
            string note;
            bool liveTransportDied = false;
            lock (_stateGate)
            {
                if (e.ConnectionGeneration != _liveConnectionGeneration)
                {
                    note = $"connection {e.ConnectionGeneration} reported {(e.IsConnected ? "up" : "gone")} but connection {_liveConnectionGeneration} is the live one — ignored";
                }
                else
                {
                    _liveTransportState = e.IsConnected ? TransportState.Up : TransportState.Down;
                    liveTransportDied = !e.IsConnected;
                    note = $"connection {e.ConnectionGeneration}'s transport reported {(e.IsConnected ? "up" : "gone")}"
                         + (e.IsConnected ? "" : " — its list is history from here, whatever the session status still says (#619)");
                }
            }
            Tracing.TraceLine($"{_tracePrefix} {note}", TraceLevel.Info);

            // The moment a SmartLink drop becomes true here, and the one the
            // open picker has to hear about: the rows it took as live from
            // this connection's list are last seen from now, and nothing else
            // would tell it until another list arrived (#619, Noel's ruling
            // of 2026-09-30; Sol's review of L9). Only the live connection's
            // death counts: a replaced transport's edge changes nothing any
            // list's currency depends on.
            if (liveTransportDied)
                RaiseListCurrencyMayHaveChanged($"connection {e.ConnectionGeneration}'s transport reported itself gone");
        }

        private void OnWanRadioListReceived(object? sender, WanRadioListReceivedEventArgs e)
        {
            BeforeListDecision?.Invoke();

            // Correlation, not provenance: the list says which connection it
            // was born on; this owner records which connection is the newest
            // dialed, told by the server before that connection's transport
            // existed, and whether this owner has since torn it down. Decided
            // in ONE critical section, the same one OnWanConnectionDialing
            // records under, so a callback that reached this method before a
            // dial began and is decided during it sees the new generation —
            // the interleaving Sol's review of L4 named (#619). A list that
            // is refused is not held, not stamped, not re-raised, so the
            // coordinator and the connect flow never see it and it cannot be
            // captured as a push. Traced outside the lock: tracing is I/O.
            string? refused;
            lock (_stateGate)
            {
                // One predicate for accepting, for the snapshot's liveness
                // and for a consumer asking later (ListIsCurrent), so a list
                // this owner would refuse is never one it reports as live.
                // Beyond the two generation orders L5 closed, it refuses a
                // list from a transport that has reported itself gone, and one
                // arriving after the operator asked to disconnect — or after
                // Dispose, which withdraws the same intent — but before the
                // monitor has marked the connection retired (#619, Sol's
                // review of L5).
                refused = RefusalFor(e.ConnectionGeneration);
                if (refused == null)
                {
                    _availableRadios = e.Radios;
                    _lastRadioListUtc = DateTime.UtcNow;
                    _radioListConnectionGeneration = e.ConnectionGeneration;
                    // A list newer than any connection recorded as live can
                    // only be from a connection the server dialed without
                    // announcing; unreachable through the adapter, which
                    // announces before the transport exists, but if it ever
                    // happened this is the right reading of it.
                    if (e.ConnectionGeneration > _liveConnectionGeneration)
                    {
                        _liveConnectionGeneration = e.ConnectionGeneration;
                        _liveConnectionRetired = false;
                        // It delivered a list, so it is up; its own edge, if
                        // one ever comes, says the same.
                        _liveTransportState = TransportState.Up;
                    }
                    // A list arriving is proof the registration works, so a
                    // MUCH later registration-invalid gets its own recovery
                    // attempt.
                    _registrationRecoveryTried = false;
                }
            }

            if (refused != null)
            {
                Tracing.TraceLine(
                    $"{_tracePrefix} radio list count={e.Radios.Count} from connection {e.ConnectionGeneration} ignored — {refused} (#619)",
                    TraceLevel.Info);
                return;
            }

            Tracing.TraceLine($"{_tracePrefix} radio list received count={e.Radios.Count} connection={e.ConnectionGeneration}", TraceLevel.Info);
            BeforeListForwarded?.Invoke();
            // Re-raise with THIS owner as sender: with one held session per
            // account (#259), the sender's AccountId is what attributes the
            // list. Fires on the SmartLink receive thread — consumers marshal.
            //
            // Forwarded OUTSIDE the lock, deliberately, and that leaves a
            // window this owner cannot close: between the acceptance above
            // and this line a dial can begin and the next connection's list
            // can be accepted and forwarded, so this one arrives late and
            // would read as the newer. Holding _stateGate across the forward
            // would serialize it — through the coordinator and the intake,
            // arbitrary callbacks, one of which takes a lock of its own. So
            // the list carries its generation, and the consumer asks
            // ListIsCurrent with it at the moment it consumes (#619, Sol's
            // review of L5).
            RadioListReceived?.Invoke(this, e);
        }

        private void OnWanRadioConnectReady(object? sender, WanRadioConnectReadyEventArgs e)
        {
            TaskCompletionSource<string?>? tcs;
            string? expectedSerial;
            lock (_stateGate)
            {
                tcs = _pendingRadioConnect;
                expectedSerial = _pendingRadioSerial;
            }
            if (tcs != null && string.Equals(expectedSerial, e.Serial, StringComparison.Ordinal))
            {
                Tracing.TraceLine($"{_tracePrefix} radio connect ready serial={e.Serial} handle={e.Handle}", TraceLevel.Info);
                tcs.TrySetResult(e.Handle);
            }
            else
            {
                Tracing.TraceLine(
                    $"{_tracePrefix} radio connect ready serial={e.Serial} with no matching pending request",
                    TraceLevel.Warning);
            }
        }

        private void OnWanApplicationRegistrationInvalid(object? sender, EventArgs e)
        {
            if (_registrationJwtProvider != null)
            {
                // Held-open session (#259): don't declare auth dead from the
                // receive thread — hand the monitor thread ONE chance to
                // refresh the token silently and re-register. Only if that
                // fails does the session settle into AuthorizationExpired.
                Tracing.TraceLine($"{_tracePrefix} application registration invalid — deferring to monitor for silent recovery", TraceLevel.Warning);
                _registrationInvalidPending = true;
                _wakeEvent.Set();
                return;
            }
            Tracing.TraceLine($"{_tracePrefix} application registration invalid — auth required", TraceLevel.Error);
            TransitionStatus(SessionStatus.AuthorizationExpired, resetAttempts: false);
            _userWantsConnected = false;
            _wakeEvent.Set();
        }

        private void CancelPendingRadioConnect(string reason)
        {
            TaskCompletionSource<string?>? tcs;
            lock (_stateGate)
            {
                tcs = _pendingRadioConnect;
                _pendingRadioConnect = null;
                _pendingRadioSerial = null;
            }
            if (tcs != null && !tcs.Task.IsCompleted)
            {
                Tracing.TraceLine($"{_tracePrefix} pending radio connect cancelled: {reason}", TraceLevel.Warning);
                tcs.TrySetResult(null);
            }
        }

        // --- Disposal ---

        public void Dispose()
        {
            if (_shutdownRequested) return;

            Tracing.TraceLine($"{_tracePrefix} Dispose", TraceLevel.Info);
            _shutdownRequested = true;
            _userWantsConnected = false;
            // First, under the list handler's lock: a callback already in
            // flight when this began is decided against a retired connection
            // and refused, rather than re-raised into the coordinator from a
            // session that is going away (Sol's review of L4, #619). The
            // unsubscribe below is too late for a callback that has already
            // read the delegate.
            lock (_stateGate) _liveConnectionRetired = true;
            CancelPendingRadioConnect("session disposed");
            // Before the unsubscribes below, so a consumer holding this
            // session's rows hears that they are history rather than being
            // left holding rows from a session that no longer exists (#619).
            RaiseListCurrencyMayHaveChanged("the session is being disposed");
            _wakeEvent.Set();

            if (_started)
            {
                // Give the monitor thread a reasonable window to exit cleanly. If it doesn't,
                // we return anyway — the thread is a background thread and will not block process exit.
                _monitorThread.Join(TimeSpan.FromSeconds(2));
            }

            _wan.PropertyChanged -= OnWanPropertyChanged;
            _wan.ConnectionDialing -= OnWanConnectionDialing;
            _wan.TransportStateChanged -= OnWanTransportStateChanged;
            _wan.WanRadioRadioListReceived -= OnWanRadioListReceived;
            _wan.WanRadioConnectReady -= OnWanRadioConnectReady;
            _wan.WanApplicationRegistrationInvalid -= OnWanApplicationRegistrationInvalid;
            _networkTestRunner.ReportReady -= OnNetworkReportReady;
            _networkTestRunner.Dispose();

            if (_wan is IDisposable wanDisposable) wanDisposable.Dispose();
            _audioSink.Dispose();
            _wakeEvent.Dispose();
        }
    }
}
