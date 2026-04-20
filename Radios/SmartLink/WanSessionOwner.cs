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

        private volatile bool _userWantsConnected;
        private volatile bool _shutdownRequested;
        private volatile bool _started;
        private volatile bool _hasBeenConnected; // true after any successful Connect; reset only on Reset() or Dispose

        // Pending ConnectToRadio request. Only one may be in flight per session at a time.
        // Completes with the WAN connection handle (string) on success, or null on failure.
        private TaskCompletionSource<string?>? _pendingRadioConnect;
        private string? _pendingRadioSerial;

        public string SessionId { get; }
        public string AccountId { get; }
        public ISessionAudioSink AudioSink => _audioSink;

        public event EventHandler<SessionStatus>? StatusChanged;
        public event EventHandler<SignalThresholdEventArgs>? SignalThresholdCrossed;

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
            _wan.SendRegisterApplicationMessageToServer(programName, platform, jwt);
        }

        public async Task<string?> ConnectToRadio(string serial, CancellationToken cancellationToken = default)
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

            Tracing.TraceLine($"{_tracePrefix} ConnectToRadio serial={serial}", TraceLevel.Info);

            using var ctr = cancellationToken.Register(() =>
            {
                if (tcs.TrySetCanceled(cancellationToken))
                {
                    Tracing.TraceLine($"{_tracePrefix} ConnectToRadio serial={serial} cancelled by token", TraceLevel.Info);
                }
            });

            try
            {
                _wan.SendConnectMessageToRadio(serial, 0);
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
                    // Connected — sleep until IsConnected flips or user action signals.
                    TransitionStatus(SessionStatus.Connected, resetAttempts: true);
                    _wakeEvent.WaitOne();
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
            }

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
            }
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
            }
            Tracing.TraceLine($"{_tracePrefix} radio list received count={e.Radios.Count}", TraceLevel.Info);
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

            if (_wan is IDisposable wanDisposable) wanDisposable.Dispose();
            _audioSink.Dispose();
            _wakeEvent.Dispose();
        }
    }
}
