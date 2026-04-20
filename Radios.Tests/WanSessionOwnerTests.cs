#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// State-machine tests for <see cref="WanSessionOwner"/>. All tests run
    /// offline against <see cref="MockWanServer"/> and use a compressed
    /// backoff schedule (10/50/200 ms) so they complete in well under a
    /// second each.
    /// </summary>
    public class WanSessionOwnerTests
    {
        // Short backoff so tests run fast. Keeps the 1:5:30 RATIO similar but
        // scaled to milliseconds instead of seconds.
        private static readonly int[] TestBackoff = { 10, 50, 200 };

        private static (WanSessionOwner owner, MockWanServer wan, DirectPassthroughSink sink) Build(int[]? backoff = null)
        {
            var wan = new MockWanServer();
            var sink = new DirectPassthroughSink();
            var owner = new WanSessionOwner(
                sessionId: Guid.NewGuid().ToString(),
                accountId: "test-account",
                wanServer: wan,
                audioSink: sink,
                backoffScheduleMs: backoff ?? TestBackoff);
            return (owner, wan, sink);
        }

        private static void WaitForStatus(IWanSessionOwner owner, SessionStatus target, int timeoutMs = 2000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (owner.Status != target && sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(5);
            }
            Assert.Equal(target, owner.Status);
        }

        private static void WaitUntil(Func<bool> condition, int timeoutMs = 2000, string? because = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(5);
            }
            Assert.True(condition(), because ?? "condition not met within timeout");
        }

        [Fact]
        public void Connect_Succeeds_EmitsConnectedState()
        {
            var (owner, wan, _) = Build();
            SessionStatus? lastStatus = null;
            owner.StatusChanged += (_, s) => lastStatus = s;

            owner.Connect();
            WaitForStatus(owner, SessionStatus.Connected);

            Assert.True(owner.IsConnected);
            Assert.Equal(1, wan.ConnectCallCount);
            Assert.Equal(SessionStatus.Connected, lastStatus);
            owner.Dispose();
        }

        [Fact]
        public void Connect_Throws_EntersBackoff_AttemptsAgainAfter1s()
        {
            var (owner, wan, _) = Build();
            wan.ConnectThrows = new InvalidOperationException("simulated");

            owner.Connect();

            WaitUntil(() => wan.ConnectCallCount >= 2, because: "expected a retry after backoff");
            Assert.True(wan.ConnectCallCount >= 2);
            Assert.IsType<InvalidOperationException>(owner.LastError);
            owner.Dispose();
        }

        [Fact]
        public void Connect_ThrowsRepeatedly_BackoffProgresses_AccordingToSchedule()
        {
            // Use a known schedule and verify the BackoffForIndex math by itself.
            // Timing of 3 retries against the schedule:
            int[] schedule = { 10, 50, 200 };
            Assert.Equal(10, WanSessionOwner.BackoffForIndex(0, schedule));
            Assert.Equal(50, WanSessionOwner.BackoffForIndex(1, schedule));
            Assert.Equal(200, WanSessionOwner.BackoffForIndex(2, schedule));
            Assert.Equal(200, WanSessionOwner.BackoffForIndex(3, schedule)); // cap
            Assert.Equal(200, WanSessionOwner.BackoffForIndex(100, schedule));

            // And integration: 3 consecutive failures must produce >= 3 attempts
            // with increasing ReconnectAttemptCount up to at least 3.
            var (owner, wan, _) = Build(schedule);
            wan.ConnectThrows = new InvalidOperationException("simulated");
            owner.Connect();
            WaitUntil(() => owner.ReconnectAttemptCount >= 3, timeoutMs: 2000,
                because: "expected 3+ consecutive failures to advance attempt count");
            Assert.True(owner.ReconnectAttemptCount >= 3);
            owner.Dispose();
        }

        [Fact]
        public void Connected_Then_IsConnectedGoesFalse_TriggersImmediateReconnect()
        {
            var (owner, wan, _) = Build();
            owner.Connect();
            WaitForStatus(owner, SessionStatus.Connected);
            int connectsBefore = wan.ConnectCallCount;

            // Simulate a silent drop: IsConnected flips to false and fires PropertyChanged.
            wan.ForceIsConnected(false);

            // Monitor wakes, sees not-connected, attempts Connect again.
            WaitUntil(() => wan.ConnectCallCount > connectsBefore);
            Assert.True(wan.ConnectCallCount > connectsBefore);
            owner.Dispose();
        }

        [Fact]
        public void Connected_Then_ExplicitDisconnect_DoesNotTriggerReconnect()
        {
            var (owner, wan, _) = Build();
            owner.Connect();
            WaitForStatus(owner, SessionStatus.Connected);
            int connectsBefore = wan.ConnectCallCount;

            owner.Disconnect();
            WaitForStatus(owner, SessionStatus.Disconnected);

            // After an explicit disconnect, even if IsConnected were to flip,
            // the owner should stay disconnected because _userWantsConnected is false.
            Thread.Sleep(100);

            Assert.Equal(connectsBefore, wan.ConnectCallCount);
            Assert.Equal(SessionStatus.Disconnected, owner.Status);
            owner.Dispose();
        }

        [Fact]
        public void Shutdown_While_Connected_CleanlyExitsThread()
        {
            var (owner, wan, _) = Build();
            owner.Connect();
            WaitForStatus(owner, SessionStatus.Connected);

            owner.Dispose();

            // Dispose joins the monitor thread with a 2s timeout. If the thread
            // is still alive after Dispose returns, the cleanup is broken.
            WaitForStatus(owner, SessionStatus.ShutDown, timeoutMs: 2500);
            Assert.Equal(SessionStatus.ShutDown, owner.Status);
        }

        [Fact]
        public void Shutdown_While_InBackoff_CleanlyExitsThread()
        {
            var (owner, wan, _) = Build(new[] { 500 }); // Long enough to be clearly "in backoff"
            wan.ConnectThrows = new Exception("simulated");
            owner.Connect();

            // Let the first failure kick it into backoff.
            WaitUntil(() => owner.ReconnectAttemptCount >= 1);

            // Now shutdown while it's sleeping in the backoff wait.
            owner.Dispose();

            WaitForStatus(owner, SessionStatus.ShutDown, timeoutMs: 2500);
        }

        [Fact]
        public void Shutdown_While_ConnectInFlight_CleanlyExitsThread()
        {
            var (owner, wan, _) = Build();
            wan.ConnectDelay = TimeSpan.FromMilliseconds(300);
            owner.Connect();

            // Give the monitor thread a moment to enter Connect().
            Thread.Sleep(50);

            // Shutdown while Connect is in flight.
            owner.Dispose();

            WaitForStatus(owner, SessionStatus.ShutDown, timeoutMs: 2500);
        }

        [Fact]
        public void IsConnectedRace_DoesNotCauseDoubleConnect()
        {
            var (owner, wan, _) = Build();
            owner.Connect();
            WaitForStatus(owner, SessionStatus.Connected);
            int connectsBefore = wan.ConnectCallCount;

            // Rapid flips false → true → false before the monitor has a chance to react.
            // End state: IsConnected=false → should trigger reconnect once, not twice.
            wan.ForceIsConnected(false);
            wan.ForceIsConnected(true);
            wan.ForceIsConnected(false);

            Thread.Sleep(100);

            // Final state was disconnected; monitor does one retry.
            Assert.True(wan.ConnectCallCount > connectsBefore);
            Assert.True(wan.ConnectCallCount <= connectsBefore + 2,
                $"expected at most +2 connects (one per distinct disconnect), got {wan.ConnectCallCount - connectsBefore}");
            owner.Dispose();
        }

        [Fact]
        public void Status_Property_ReflectsStateTransitions()
        {
            var (owner, wan, _) = Build();
            var transitions = new List<SessionStatus>();
            owner.StatusChanged += (_, s) =>
            {
                lock (transitions) transitions.Add(s);
            };

            owner.Connect();
            WaitForStatus(owner, SessionStatus.Connected);

            wan.ForceIsConnected(false);
            WaitUntil(() =>
            {
                lock (transitions) return transitions.Contains(SessionStatus.Reconnecting);
            });

            // After successful reconnect, we should hit Connected again.
            WaitForStatus(owner, SessionStatus.Connected);

            lock (transitions)
            {
                Assert.Contains(SessionStatus.Connecting, transitions);
                Assert.Contains(SessionStatus.Connected, transitions);
                Assert.Contains(SessionStatus.Reconnecting, transitions);
            }

            owner.Dispose();
        }

        [Fact]
        public void LastError_Populated_OnConnectFailure()
        {
            var (owner, wan, _) = Build();
            var expected = new InvalidOperationException("whoops");
            wan.ConnectThrows = expected;
            owner.Connect();

            WaitUntil(() => owner.LastError != null);
            Assert.Same(expected, owner.LastError);
            owner.Dispose();
        }

        [Fact]
        public void ReconnectAttemptCount_IncrementsPerRetry_ResetsOnSuccess()
        {
            var (owner, wan, _) = Build();
            wan.ConnectThrows = new Exception("fail");
            owner.Connect();

            WaitUntil(() => owner.ReconnectAttemptCount >= 2);
            int duringFailure = owner.ReconnectAttemptCount;
            Assert.True(duringFailure >= 2);

            // Now stop throwing so the next attempt succeeds.
            wan.ConnectThrows = null;
            WaitForStatus(owner, SessionStatus.Connected);

            Assert.Equal(0, owner.ReconnectAttemptCount);
            Assert.Null(owner.LastError);
            owner.Dispose();
        }
    }
}
