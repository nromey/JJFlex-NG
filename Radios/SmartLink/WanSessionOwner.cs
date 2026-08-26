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

        // Retry interval when auto-registration could not complete (no JWT
        // available silently, send threw). Connected-but-unregistered receives
        // no pushes, so the monitor retries rather than sleeping forever.
        internal const int RegistrationRetryMs = 30_000;

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

        // --- Public commands ---

        public void Connect()
        {
            Tracing.TraceLine($"{_tracePrefix} Connect requested", TraceLevel.Info);
            _userWantsConnected = true;

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
                    if (_wan.IsConnected)
                    {
                        try { _wan.Disconnect(); } catch (Exception ex) { TraceWarn("Disconnect threw", ex); }
                    }
                    TransitionStatus(SessionStatus.Disconnected, resetAttempts: true);
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
                    if (registrationHealthy) _wakeEvent.WaitOne();
                    else _wakeEvent.WaitOne(RegistrationRetryMs);
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
                // Wake monitor to re-evaluate whether to reconnect or settle into connected state.
                _wakeEvent.Set();
            }
        }

        private void OnWanRadioListReceived(object? sender, WanRadioListReceivedEventArgs e)
        {
            lock (_stateGate)
            {
                _availableRadios = e.Radios;
                _lastRadioListUtc = DateTime.UtcNow;
                // A list arriving is proof the registration works, so a MUCH
                // later registration-invalid gets its own recovery attempt.
                _registrationRecoveryTried = false;
            }
            Tracing.TraceLine($"{_tracePrefix} radio list received count={e.Radios.Count}", TraceLevel.Info);
            // Re-raise with THIS owner as sender: with one held session per
            // account (#259), the sender's AccountId is what attributes the
            // list. Fires on the SmartLink receive thread — consumers marshal.
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
            CancelPendingRadioConnect("session disposed");
            _wakeEvent.Set();

            if (_started)
            {
                // Give the monitor thread a reasonable window to exit cleanly. If it doesn't,
                // we return anyway — the thread is a background thread and will not block process exit.
                _monitorThread.Join(TimeSpan.FromSeconds(2));
            }

            _wan.PropertyChanged -= OnWanPropertyChanged;
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
