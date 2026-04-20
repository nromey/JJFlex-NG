#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using JJTrace;

namespace Radios.SmartLink
{
    /// <summary>
    /// Sprint 27 Track C — async-friendly facade over the fire-and-forget
    /// FlexLib NetworkTest surface (see C.0 audit). Converts
    /// <see cref="IWanServer.SendTestConnection"/> + the
    /// <see cref="IWanServer.TestConnectionResultsReceived"/> event into a
    /// <see cref="Task{NetworkDiagnosticReport}"/> the caller can await,
    /// with a caller-enforceable timeout (FlexLib does not enforce one).
    ///
    /// <para>
    /// <b>Caching:</b> results are keyed by radio serial with two TTLs —
    /// <see cref="PassCacheTtl"/> when the probe returned any positive
    /// subtest, <see cref="FailCacheTtl"/> when every subtest failed or the
    /// probe did not complete. This matches the plan's 5-minutes-pass /
    /// 30-seconds-fail policy while avoiding "hammering during a flaky
    /// period". Cache invalidation on explicit user action via
    /// <see cref="InvalidateCache"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Pending-probe dedup:</b> concurrent <see cref="RunAsync"/> calls
    /// for the same serial share one underlying probe. The second caller
    /// awaits the same <see cref="TaskCompletionSource{T}"/>, avoiding
    /// redundant <c>SendTestConnection</c> traffic.
    /// </para>
    ///
    /// <para>
    /// <b>Thread-safety:</b> all mutable state lives behind a
    /// <see cref="System.Threading.Lock"/>. The TestConnectionResultsReceived
    /// event fires on FlexLib's SSL listener thread; completing the TCS
    /// there is safe because we marshal via
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>
    /// so continuations run on a thread pool thread, not inline on the
    /// listener thread.
    /// </para>
    /// </summary>
    public sealed class NetworkTestRunner : IDisposable
    {
        private readonly IWanServer _wan;
        private readonly Func<DateTime> _clock;
        private readonly System.Threading.Lock _gate = new();
        private readonly Dictionary<string, CachedEntry> _cache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<NetworkDiagnosticReport>> _pending = new(StringComparer.Ordinal);
        private bool _disposed;

        /// <summary>TTL applied when at least one subtest in the probe result is true.</summary>
        public TimeSpan PassCacheTtl { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>TTL applied when every subtest is false, or the probe did not complete.</summary>
        public TimeSpan FailCacheTtl { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Default timeout used by <see cref="RunAsync"/> when the caller supplies none.</summary>
        public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Fires once per completed probe, regardless of whether a caller is awaiting.</summary>
        public event EventHandler<NetworkDiagnosticReport>? ReportReady;

        /// <param name="wan">The SmartLink surface this runner probes against.</param>
        /// <param name="clock">Injectable clock for testability. Defaults to <see cref="DateTime.UtcNow"/>.</param>
        public NetworkTestRunner(IWanServer wan, Func<DateTime>? clock = null)
        {
            _wan = wan ?? throw new ArgumentNullException(nameof(wan));
            _clock = clock ?? (() => DateTime.UtcNow);
            _wan.TestConnectionResultsReceived += OnTestConnectionResultsReceived;
        }

        /// <summary>
        /// Run (or retrieve a cached) NetworkTest probe for <paramref name="radioSerial"/>.
        /// Returns a report that is either: (a) a fresh cache hit, (b) the
        /// result of a live probe that completed within the timeout window,
        /// or (c) an error-state report when the probe timed out or
        /// <see cref="IWanServer.SendTestConnection"/> threw. Does not
        /// throw on timeout — the timeout is reported via
        /// <see cref="NetworkDiagnosticReport.ErrorDetail"/>.
        /// </summary>
        /// <param name="radioSerial">Radio serial to probe. Used as cache key.</param>
        /// <param name="forceRefresh">If true, ignores any cached result and issues a fresh probe.</param>
        /// <param name="timeout">Optional override for the timeout window; defaults to <see cref="DefaultTimeout"/>.</param>
        /// <param name="ct">Caller cancellation; throws <see cref="OperationCanceledException"/> when cancelled.</param>
        public async Task<NetworkDiagnosticReport> RunAsync(
            string radioSerial,
            bool forceRefresh = false,
            TimeSpan? timeout = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(radioSerial)) throw new ArgumentException("radioSerial required", nameof(radioSerial));
            ThrowIfDisposed();

            TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
            TaskCompletionSource<NetworkDiagnosticReport> tcs;
            bool shouldSendProbe;

            lock (_gate)
            {
                if (!forceRefresh && _cache.TryGetValue(radioSerial, out var cached) && _clock() < cached.ExpiresAt)
                {
                    Tracing.TraceLine($"NetworkTestRunner: cache hit serial={radioSerial}", TraceLevel.Verbose);
                    return cached.Report;
                }

                if (_pending.TryGetValue(radioSerial, out var existing))
                {
                    Tracing.TraceLine($"NetworkTestRunner: joining pending probe serial={radioSerial}", TraceLevel.Verbose);
                    tcs = existing;
                    shouldSendProbe = false;
                }
                else
                {
                    tcs = new TaskCompletionSource<NetworkDiagnosticReport>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pending[radioSerial] = tcs;
                    shouldSendProbe = true;
                }
            }

            if (shouldSendProbe)
            {
                try
                {
                    Tracing.TraceLine($"NetworkTestRunner: issuing probe serial={radioSerial}", TraceLevel.Info);
                    _wan.SendTestConnection(radioSerial);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"NetworkTestRunner: SendTestConnection threw serial={radioSerial} ex={ex.Message}", TraceLevel.Error);
                    var errorReport = BuildErrorReport(radioSerial, $"SendTestConnection threw: {ex.Message}");
                    FinishProbe(radioSerial, tcs, errorReport, pass: false);
                    return errorReport;
                }
            }

            // Race the probe TCS against the caller's timeout / cancellation.
            var delayTask = Task.Delay(effectiveTimeout, ct);
            var winner = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);

            if (winner == tcs.Task)
            {
                // Probe completed first (either via event or an earlier timeout
                // that completed the same TCS). Cancellation is not propagated
                // from delayTask in that case; swallow any pending exception on
                // delayTask.
                _ = delayTask.ContinueWith(_ => { /* observe to avoid unobserved task */ }, TaskScheduler.Default);
                return await tcs.Task.ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            // Timeout. Finish the probe with an error-state report. If the
            // real event arrives later, OnTestConnectionResultsReceived still
            // updates the cache with the real result (TrySetResult on the
            // already-completed TCS is a no-op but ReportReady fires so any
            // downstream listener gets the late-but-real data).
            Tracing.TraceLine($"NetworkTestRunner: timeout serial={radioSerial} afterSec={effectiveTimeout.TotalSeconds:0}", TraceLevel.Warning);
            var timeoutReport = BuildErrorReport(radioSerial, $"Timed out after {effectiveTimeout.TotalSeconds:0} seconds");
            FinishProbe(radioSerial, tcs, timeoutReport, pass: false);
            return timeoutReport;
        }

        /// <summary>
        /// Returns the most recently cached report for <paramref name="radioSerial"/>,
        /// or null if nothing is cached. Bypasses the TTL check — callers get
        /// whatever is in the cache, stale or not, to display "last tested N
        /// minutes ago" UI without forcing a refresh.
        /// </summary>
        public NetworkDiagnosticReport? GetLastReport(string radioSerial)
        {
            lock (_gate) return _cache.TryGetValue(radioSerial, out var entry) ? entry.Report : null;
        }

        /// <summary>
        /// Invalidate cache entries so the next <see cref="RunAsync"/> is
        /// forced to probe fresh. Pass null to clear every entry (e.g. when
        /// the user switches accounts); pass a serial to clear just that
        /// radio's cached result.
        /// </summary>
        public void InvalidateCache(string? radioSerial = null)
        {
            lock (_gate)
            {
                if (radioSerial == null) _cache.Clear();
                else _cache.Remove(radioSerial);
            }
        }

        private void OnTestConnectionResultsReceived(object? sender, WanTestConnectionResultsEventArgs e)
        {
            var report = new NetworkDiagnosticReport
            {
                RadioSerial = e.RadioSerial,
                TimestampUtc = _clock(),
                UpnpTcpReachable = e.UpnpTcpWorking,
                UpnpUdpReachable = e.UpnpUdpWorking,
                ManualForwardTcpReachable = e.ForwardTcpWorking,
                ManualForwardUdpReachable = e.ForwardUdpWorking,
                NatSupportsHolePunch = e.NatSupportsHolePunch,
            };
            bool anyPassed =
                e.UpnpTcpWorking ||
                e.UpnpUdpWorking ||
                e.ForwardTcpWorking ||
                e.ForwardUdpWorking ||
                e.NatSupportsHolePunch;

            Tracing.TraceLine(
                $"NetworkTestRunner: probe result serial={e.RadioSerial} anyPassed={anyPassed}",
                TraceLevel.Info);

            TaskCompletionSource<NetworkDiagnosticReport>? awaiter;
            lock (_gate)
            {
                _cache[e.RadioSerial] = new CachedEntry(report, _clock() + (anyPassed ? PassCacheTtl : FailCacheTtl));
                _pending.Remove(e.RadioSerial, out awaiter);
            }
            awaiter?.TrySetResult(report);
            ReportReady?.Invoke(this, report);
        }

        private NetworkDiagnosticReport BuildErrorReport(string serial, string detail) => new()
        {
            RadioSerial = serial,
            TimestampUtc = _clock(),
            ErrorDetail = detail,
        };

        private void FinishProbe(string serial, TaskCompletionSource<NetworkDiagnosticReport> tcs, NetworkDiagnosticReport report, bool pass)
        {
            bool fired = false;
            lock (_gate)
            {
                if (_pending.TryGetValue(serial, out var p) && ReferenceEquals(p, tcs))
                {
                    _pending.Remove(serial);
                }
                _cache[serial] = new CachedEntry(report, _clock() + (pass ? PassCacheTtl : FailCacheTtl));
                fired = tcs.TrySetResult(report);
            }
            if (fired) ReportReady?.Invoke(this, report);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NetworkTestRunner));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _wan.TestConnectionResultsReceived -= OnTestConnectionResultsReceived;
        }

        private sealed record CachedEntry(NetworkDiagnosticReport Report, DateTime ExpiresAt);
    }
}
