#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 27 Track C / Phase C.2. Verifies <see cref="NetworkTestRunner"/>
    /// dedup / cache / timeout / error semantics against MockWanServer. Does
    /// not go over any real network.
    /// </summary>
    public class NetworkTestRunnerTests
    {
        private const string Serial = "1234-5678";

        private static NetworkTestRunner Make(MockWanServer wan, Func<DateTime>? clock = null, TimeSpan? defaultTimeout = null)
        {
            return new NetworkTestRunner(wan, clock)
            {
                DefaultTimeout = defaultTimeout ?? TimeSpan.FromMilliseconds(200),
                PassCacheTtl = TimeSpan.FromMinutes(5),
                FailCacheTtl = TimeSpan.FromSeconds(30),
            };
        }

        [Fact]
        public async Task RunAsync_CompletesWhenEventArrives()
        {
            var wan = new MockWanServer();
            using var runner = Make(wan);

            var task = runner.RunAsync(Serial);
            // Simulate SmartLink response
            wan.RaiseTestConnectionResultsReceived(Serial, true, true, true, true, true);

            var report = await task;
            Assert.True(report.ProbeCompleted);
            Assert.Equal(true, report.UpnpTcpReachable);
            Assert.Equal(true, report.NatSupportsHolePunch);
            Assert.Equal(1, wan.SendTestConnectionCallCount);
        }

        [Fact]
        public async Task RunAsync_ReturnsCachedResultOnSecondCallWithinTtl()
        {
            var now = new DateTime(2026, 4, 20, 14, 0, 0, DateTimeKind.Utc);
            DateTime clockValue = now;
            var wan = new MockWanServer();
            using var runner = Make(wan, () => clockValue);

            var t1 = runner.RunAsync(Serial);
            wan.RaiseTestConnectionResultsReceived(Serial, true, false, true, false, true);
            var r1 = await t1;

            // Advance clock by 1 minute — well inside the pass TTL (5 min).
            clockValue = now.AddMinutes(1);
            var r2 = await runner.RunAsync(Serial);

            Assert.Same(r1, r2);
            // Only ONE probe hit the wire; second call was a cache hit.
            Assert.Equal(1, wan.SendTestConnectionCallCount);
        }

        [Fact]
        public async Task RunAsync_ForceRefreshIgnoresCache()
        {
            var wan = new MockWanServer();
            using var runner = Make(wan);

            var t1 = runner.RunAsync(Serial);
            wan.RaiseTestConnectionResultsReceived(Serial, true, true, true, true, true);
            await t1;

            // Force refresh should issue a second probe even though cache is hot.
            var t2 = runner.RunAsync(Serial, forceRefresh: true);
            wan.RaiseTestConnectionResultsReceived(Serial, false, false, false, false, false);
            var r2 = await t2;

            Assert.Equal(2, wan.SendTestConnectionCallCount);
            Assert.Equal(false, r2.UpnpTcpReachable);
        }

        [Fact]
        public async Task RunAsync_ConcurrentCallsDedupToSingleProbe()
        {
            var wan = new MockWanServer();
            using var runner = Make(wan);

            // Start two calls for the same serial before any event arrives.
            var t1 = runner.RunAsync(Serial);
            var t2 = runner.RunAsync(Serial);

            // Only one probe should have been issued.
            Assert.Equal(1, wan.SendTestConnectionCallCount);

            wan.RaiseTestConnectionResultsReceived(Serial, true, true, true, true, true);

            var r1 = await t1;
            var r2 = await t2;
            // Both awaiters receive the same report instance.
            Assert.Same(r1, r2);
        }

        [Fact]
        public async Task RunAsync_DifferentSerialsDoNotShareCacheOrProbes()
        {
            var wan = new MockWanServer();
            using var runner = Make(wan);

            var tA = runner.RunAsync("AAAA-1111");
            var tB = runner.RunAsync("BBBB-2222");
            // Two distinct probes.
            Assert.Equal(2, wan.SendTestConnectionCallCount);

            wan.RaiseTestConnectionResultsReceived("AAAA-1111", true, true, true, true, true);
            wan.RaiseTestConnectionResultsReceived("BBBB-2222", false, false, false, false, false);

            var rA = await tA;
            var rB = await tB;
            Assert.Equal(true, rA.UpnpTcpReachable);
            Assert.Equal(false, rB.UpnpTcpReachable);
        }

        [Fact]
        public async Task RunAsync_TimeoutReturnsErrorReport()
        {
            var wan = new MockWanServer();
            using var runner = Make(wan, defaultTimeout: TimeSpan.FromMilliseconds(75));

            // No event fires — the probe should time out.
            var report = await runner.RunAsync(Serial);

            Assert.False(report.ProbeCompleted);
            Assert.NotNull(report.ErrorDetail);
            Assert.Contains("Timed out", report.ErrorDetail!);
            Assert.Null(report.UpnpTcpReachable);
        }

        [Fact]
        public async Task RunAsync_CancellationThrows()
        {
            var wan = new MockWanServer();
            using var runner = Make(wan, defaultTimeout: TimeSpan.FromSeconds(5));
            using var cts = new CancellationTokenSource();

            var task = runner.RunAsync(Serial, ct: cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        }

        [Fact]
        public async Task RunAsync_LateEventAfterTimeoutStillUpdatesCache()
        {
            var wan = new MockWanServer();
            using var runner = Make(wan, defaultTimeout: TimeSpan.FromMilliseconds(50));

            var timeoutReport = await runner.RunAsync(Serial);
            Assert.False(timeoutReport.ProbeCompleted);

            // The real response arrives after the timeout already fired.
            wan.RaiseTestConnectionResultsReceived(Serial, true, true, true, true, true);

            // GetLastReport should now see the real (successful) result, not
            // the stale timeout report.
            var last = runner.GetLastReport(Serial);
            Assert.NotNull(last);
            Assert.True(last!.ProbeCompleted);
            Assert.Equal(true, last.UpnpTcpReachable);
        }

        [Fact]
        public async Task RunAsync_AllFailuresUseShortCacheTtl()
        {
            var now = new DateTime(2026, 4, 20, 14, 0, 0, DateTimeKind.Utc);
            DateTime clockValue = now;
            var wan = new MockWanServer();
            using var runner = Make(wan, () => clockValue);

            var t1 = runner.RunAsync(Serial);
            wan.RaiseTestConnectionResultsReceived(Serial, false, false, false, false, false);
            await t1;

            // 31 seconds later, still inside pass-TTL (5min) but past fail-TTL
            // (30s). Cache should have expired so a new probe goes out.
            clockValue = now.AddSeconds(31);
            var t2 = runner.RunAsync(Serial);
            Assert.Equal(2, wan.SendTestConnectionCallCount);

            // Complete the second probe too so we don't leak a pending task.
            wan.RaiseTestConnectionResultsReceived(Serial, false, false, false, false, false);
            await t2;
        }

        [Fact]
        public async Task InvalidateCache_ForcesFreshProbeOnNextCall()
        {
            var wan = new MockWanServer();
            using var runner = Make(wan);

            var t1 = runner.RunAsync(Serial);
            wan.RaiseTestConnectionResultsReceived(Serial, true, true, true, true, true);
            await t1;

            runner.InvalidateCache(Serial);

            var t2 = runner.RunAsync(Serial);
            Assert.Equal(2, wan.SendTestConnectionCallCount);
            wan.RaiseTestConnectionResultsReceived(Serial, true, true, true, true, true);
            await t2;
        }

        [Fact]
        public async Task InvalidateCache_AllClearsEverything()
        {
            var wan = new MockWanServer();
            using var runner = Make(wan);

            var tA = runner.RunAsync("AAAA");
            wan.RaiseTestConnectionResultsReceived("AAAA", true, true, true, true, true);
            await tA;
            var tB = runner.RunAsync("BBBB");
            wan.RaiseTestConnectionResultsReceived("BBBB", true, true, true, true, true);
            await tB;

            Assert.NotNull(runner.GetLastReport("AAAA"));
            Assert.NotNull(runner.GetLastReport("BBBB"));

            runner.InvalidateCache();

            // Cache entries cleared, but GetLastReport still returns null only
            // because the cache is the sole source; after invalidation both
            // should be gone.
            Assert.Null(runner.GetLastReport("AAAA"));
            Assert.Null(runner.GetLastReport("BBBB"));
        }

        [Fact]
        public async Task RunAsync_FiresReportReadyEvent()
        {
            var wan = new MockWanServer();
            using var runner = Make(wan);

            NetworkDiagnosticReport? captured = null;
            runner.ReportReady += (_, r) => captured = r;

            var task = runner.RunAsync(Serial);
            wan.RaiseTestConnectionResultsReceived(Serial, true, false, true, false, true);
            await task;

            Assert.NotNull(captured);
            Assert.Equal(Serial, captured!.RadioSerial);
            Assert.Equal(true, captured.UpnpTcpReachable);
        }

        [Fact]
        public async Task Dispose_UnsubscribesFromWanEvents()
        {
            var wan = new MockWanServer();
            var runner = Make(wan);
            runner.Dispose();

            // After dispose, raising the event must not crash or linger-cache.
            wan.RaiseTestConnectionResultsReceived(Serial, true, true, true, true, true);

            // GetLastReport on a disposed runner should not throw on the
            // read path (cache was never populated, returns null).
            Assert.Null(runner.GetLastReport(Serial));

            // RunAsync on a disposed runner throws ObjectDisposedException.
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await runner.RunAsync(Serial));
        }
    }
}
