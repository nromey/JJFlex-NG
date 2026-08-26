#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 35 Track K (#259) — the held-open presence model. All offline
    /// against <see cref="MockWanServer"/>: auto-registration across
    /// reconnects, the one-registration-per-connection claim, the single
    /// silent recovery from a registration-invalid push, and the
    /// coordinator's attributed radio-list aggregation.
    /// </summary>
    public class SmartLinkPresenceModelTests
    {
        private static readonly int[] TestBackoff = { 10, 50, 200 };

        private static (WanSessionOwner owner, MockWanServer wan) Build(string accountId = "test-account")
        {
            var wan = new MockWanServer();
            var owner = new WanSessionOwner(
                sessionId: Guid.NewGuid().ToString("N").Substring(0, 12),
                accountId: accountId,
                wanServer: wan,
                audioSink: new DirectPassthroughSink(),
                backoffScheduleMs: TestBackoff);
            return (owner, wan);
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

        // --- Auto-registration ---

        [Fact]
        public void AutoRegistration_RegistersAfterConnect()
        {
            var (owner, wan) = Build();
            var forceArgs = new List<bool>();
            owner.EnableAutoRegistration(force => { lock (forceArgs) forceArgs.Add(force); return "jwt"; }, "TestProgram");

            owner.Connect();

            WaitUntil(() => wan.SendRegisterCallCount == 1, because: "expected the monitor to register after connect");
            lock (forceArgs) Assert.Equal(new[] { false }, forceArgs);
            Assert.Equal(SessionStatus.Connected, owner.Status);
            owner.Dispose();
        }

        [Fact]
        public void AutoRegistration_ReRegistersAfterSilentDropAndReconnect()
        {
            var (owner, wan) = Build();
            owner.EnableAutoRegistration(_ => "jwt", "TestProgram");
            owner.Connect();
            WaitUntil(() => wan.SendRegisterCallCount == 1);

            // The 2 AM case: the TLS session drops, the monitor quietly
            // reconnects — and MUST re-register, or the session sits
            // connected-but-unregistered and presence silently stops.
            wan.ForceIsConnected(false);

            WaitUntil(() => wan.SendRegisterCallCount == 2,
                because: "a reconnected session must register again — registration died with the old connection");
            owner.Dispose();
        }

        [Fact]
        public void AutoRegistration_SkipsWhenCallerAlreadyRegisteredThisConnection()
        {
            var (owner, wan) = Build();
            owner.Connect();
            WaitUntil(() => owner.IsConnected);

            // The interactive flow's path: claim, then send.
            Assert.True(owner.TryClaimRegistration());
            owner.ReRegister("TestProgram", "Win10", "jwt-interactive");
            Assert.Equal(1, wan.SendRegisterCallCount);

            // Presence engages afterwards (the setupRemote ordering): the
            // monitor must NOT double the registration.
            owner.EnableAutoRegistration(_ => "jwt-auto", "TestProgram");
            Thread.Sleep(150); // give the monitor a chance to do the wrong thing
            Assert.Equal(1, wan.SendRegisterCallCount);
            owner.Dispose();
        }

        [Fact]
        public void AutoRegistration_ProviderReturningNull_DoesNotRegister_AndStaysSilent()
        {
            var (owner, wan) = Build();
            owner.EnableAutoRegistration(_ => null, "TestProgram");

            owner.Connect();
            WaitUntil(() => owner.IsConnected);
            Thread.Sleep(100);

            // No JWT silently: no registration, no auth-expired panic — the
            // session stays connected and the monitor owns the retry cadence.
            Assert.Equal(0, wan.SendRegisterCallCount);
            Assert.Equal(SessionStatus.Connected, owner.Status);
            owner.Dispose();
        }

        // --- The registration claim ---

        [Fact]
        public void TryClaimRegistration_SecondClaimRefused_ClaimResetsWhenConnectionDrops()
        {
            var (owner, wan) = Build();
            owner.Connect();
            WaitUntil(() => owner.IsConnected);

            Assert.True(owner.TryClaimRegistration());
            Assert.False(owner.TryClaimRegistration());

            // Drop + reconnect: the new connection has no registration yet.
            // AttemptConnect resets the claim BEFORE dialing, so once the
            // second connect has landed the reset is already visible.
            wan.ForceIsConnected(false);
            WaitUntil(() => owner.IsConnected && wan.ConnectCallCount >= 2);
            Assert.True(owner.TryClaimRegistration(),
                "the claim must reset with the connection it belonged to");
            owner.Dispose();
        }

        // --- Registration-invalid recovery ---

        [Fact]
        public void RegistrationInvalid_WithProvider_RecoversOnceWithForcedRefresh()
        {
            var (owner, wan) = Build();
            var forceArgs = new List<bool>();
            owner.EnableAutoRegistration(force => { lock (forceArgs) forceArgs.Add(force); return "jwt"; }, "TestProgram");
            owner.Connect();
            WaitUntil(() => wan.SendRegisterCallCount == 1);

            wan.RaiseWanApplicationRegistrationInvalid();

            WaitUntil(() => wan.SendRegisterCallCount == 2,
                because: "one silent refresh + re-register before giving up");
            lock (forceArgs) Assert.Equal(new[] { false, true }, forceArgs);
            Assert.Equal(SessionStatus.Connected, owner.Status);
            owner.Dispose();
        }

        [Fact]
        public void RegistrationInvalid_TwiceWithoutAListBetween_SettlesIntoAuthorizationExpired()
        {
            var (owner, wan) = Build();
            owner.EnableAutoRegistration(_ => "jwt", "TestProgram");
            owner.Connect();
            WaitUntil(() => wan.SendRegisterCallCount == 1);

            wan.RaiseWanApplicationRegistrationInvalid();
            WaitUntil(() => wan.SendRegisterCallCount == 2);

            // The recovery's own registration bounced too: silence is out of
            // moves, and pretending otherwise would loop against the server.
            wan.RaiseWanApplicationRegistrationInvalid();
            WaitUntil(() => owner.Status == SessionStatus.AuthorizationExpired,
                because: "a second invalid without proof of life in between must give up");
            Assert.Equal(2, wan.SendRegisterCallCount);
            owner.Dispose();
        }

        [Fact]
        public void RegistrationInvalid_AfterAListArrived_EarnsAFreshRecovery()
        {
            var (owner, wan) = Build();
            owner.EnableAutoRegistration(_ => "jwt", "TestProgram");
            owner.Connect();
            WaitUntil(() => wan.SendRegisterCallCount == 1);

            wan.RaiseWanApplicationRegistrationInvalid();
            WaitUntil(() => wan.SendRegisterCallCount == 2);

            // A list is proof the recovered registration WORKED — a much
            // later invalid is a new incident, not the same one.
            wan.RaiseWanRadioRadioListReceived(Array.Empty<Radio>());

            wan.RaiseWanApplicationRegistrationInvalid();
            WaitUntil(() => wan.SendRegisterCallCount == 3,
                because: "recovery budget must reset once a list proves registration works");
            Assert.Equal(SessionStatus.Connected, owner.Status);
            owner.Dispose();
        }

        [Fact]
        public void RegistrationInvalid_WithoutProvider_KeepsLegacyImmediateAuthExpired()
        {
            var (owner, wan) = Build();
            owner.Connect();
            WaitUntil(() => owner.IsConnected);

            wan.RaiseWanApplicationRegistrationInvalid();

            WaitUntil(() => owner.Status == SessionStatus.AuthorizationExpired
                || owner.Status == SessionStatus.Disconnected,
                because: "no provider means no silent recovery — the pre-#259 behavior stands");
            Assert.Equal(0, wan.SendRegisterCallCount);
            owner.Dispose();
        }

        // --- List attribution ---

        [Fact]
        public void RadioList_ReRaisedWithOwnerAsSender_AndLastListTimeStamped()
        {
            var (owner, wan) = Build(accountId: "don@example.test");
            object? sender = null;
            IReadOnlyList<Radio>? radios = null;
            owner.RadioListReceived += (s, e) => { sender = s; radios = e.Radios; };
            owner.Connect();
            WaitUntil(() => owner.IsConnected);
            Assert.Null(owner.LastRadioListUtc);

            wan.RaiseWanRadioRadioListReceived(Array.Empty<Radio>());

            Assert.Same(owner, sender);
            Assert.NotNull(radios);
            Assert.NotNull(owner.LastRadioListUtc);
            Assert.Equal("don@example.test", ((IWanSessionOwner)owner).AccountId);
            owner.Dispose();
        }

        // --- Coordinator: attributed aggregation, passive creation ---

        private static SmartLinkSessionCoordinator BuildCoordinator(Dictionary<string, MockWanServer> wansByAccount)
        {
            return new SmartLinkSessionCoordinator(accountId =>
            {
                var wan = new MockWanServer();
                wansByAccount[accountId] = wan;
                return new WanSessionOwner(
                    Guid.NewGuid().ToString("N").Substring(0, 12),
                    accountId,
                    wan,
                    new DirectPassthroughSink(),
                    TestBackoff);
            });
        }

        [Fact]
        public void GetOrCreateSession_DoesNotTouchTheActivePointer()
        {
            var wans = new Dictionary<string, MockWanServer>();
            using var coordinator = BuildCoordinator(wans);

            var active = coordinator.EnsureSessionForAccount("noel@example.test");
            var held = coordinator.GetOrCreateSession("don@example.test");

            // The presence layer holding Don's session must not point radio
            // connects at Don's account.
            Assert.Same(active, coordinator.ActiveSession);
            Assert.NotSame(held, coordinator.ActiveSession);

            // And the connect flow re-activating is still what moves it.
            coordinator.EnsureSessionForAccount("don@example.test");
            Assert.Same(held, coordinator.ActiveSession);
        }

        [Fact]
        public void GetOrCreateSession_MatchesAccountsCaseInsensitively()
        {
            var wans = new Dictionary<string, MockWanServer>();
            using var coordinator = BuildCoordinator(wans);

            var a = coordinator.GetOrCreateSession("Don@Example.Test");
            var b = coordinator.GetOrCreateSession("don@example.test");

            Assert.Same(a, b);
            Assert.Same(a, coordinator.GetSessionForAccount("DON@EXAMPLE.TEST"));
        }

        [Fact]
        public void SessionRadioListReceived_CarriesTheDeliveringAccount()
        {
            var wans = new Dictionary<string, MockWanServer>();
            using var coordinator = BuildCoordinator(wans);
            var seen = new List<string>();
            coordinator.SessionRadioListReceived += (_, e) => { lock (seen) seen.Add(e.AccountId); };

            coordinator.GetOrCreateSession("noel@example.test");
            coordinator.GetOrCreateSession("don@example.test");

            wans["don@example.test"].RaiseWanRadioRadioListReceived(Array.Empty<Radio>());
            wans["noel@example.test"].RaiseWanRadioRadioListReceived(Array.Empty<Radio>());
            wans["don@example.test"].RaiseWanRadioRadioListReceived(Array.Empty<Radio>());

            lock (seen)
                Assert.Equal(new[] { "don@example.test", "noel@example.test", "don@example.test" }, seen);
        }

        [Fact]
        public void SessionRadioListReceived_StopsAfterDisconnectSession()
        {
            var wans = new Dictionary<string, MockWanServer>();
            using var coordinator = BuildCoordinator(wans);
            int fired = 0;
            coordinator.SessionRadioListReceived += (_, _) => Interlocked.Increment(ref fired);

            var session = coordinator.GetOrCreateSession("don@example.test");
            wans["don@example.test"].RaiseWanRadioRadioListReceived(Array.Empty<Radio>());
            Assert.Equal(1, fired);

            coordinator.DisconnectSession(session.SessionId);
            wans["don@example.test"].RaiseWanRadioRadioListReceived(Array.Empty<Radio>());
            Assert.Equal(1, fired);
        }
    }
}
