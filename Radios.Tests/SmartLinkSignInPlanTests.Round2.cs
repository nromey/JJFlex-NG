#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.SmartLink;
using Xunit;

using Plan = Radios.FlexBase.SmartLinkSignInPlanKind;

namespace Radios.Tests
{
    /// <summary>
    /// Repair round 2 (Codex's re-checks of #798, #613 and #713): received
    /// state between deciding and acting, and one flow's wait changed by
    /// another's. Real session owners over the mock server; every schedule is
    /// held at a named hook, not raced.
    /// </summary>
    public sealed partial class SmartLinkSignInPlanTests
    {
        private const string Third = "third@example.com";

        private WanSessionOwner Owner_(string account) => (WanSessionOwner)_coordinator.GetSessionForAccount(account)!;

        private MockWanServer Wan(string account)
        {
            lock (_servers) return _servers[account];
        }

        private static readonly MethodInfo ConnectToSmartLinkMethod =
            typeof(FlexBase).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(m => m.Name == "ConnectToSmartLink" && m.GetParameters().Length == 2);

        private static object Connect(FlexBase rig, string jwt, string? account) =>
            ConnectToSmartLinkMethod.Invoke(rig, new object?[] { jwt, account })!;

        // ------------------------------------------------------------------
        // #713 B1: one flow's scope must not change another flow's wait
        // ------------------------------------------------------------------

        /// <summary>
        /// The connection's own connect (account A) and a registration query
        /// (account B) on the same rig at once. A's list arrives while the
        /// query waits; B's empty list arrives after. Until round 2 the
        /// query's account key replaced A's for the whole rig, so A's own list
        /// was not "ours" and A's connect finished on B's empty list.
        /// </summary>
        [Fact]
        public void A_query_under_another_account_does_not_change_what_the_connect_waits_for()
        {
            _coordinator.GetOrCreateSession(Operator);
            _coordinator.GetOrCreateSession(Owner);
            var rig = NewRig();
            CurrentAccountField!.SetValue(rig, new SmartLinkAccount { Email = Operator, FriendlyName = Operator });

            var connect = Task.Run(() => Connect(rig, "test-jwt", null));
            WaitUntil(() => Wan(Operator).SendRegisterCallCount > 0, "the connect never registered");
            var query = Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, Owner, "test-jwt"));
            WaitUntil(() => Wan(Owner).SendRegisterCallCount > 0, "the query never registered");

            // A's own list: a radio, not the one the query asks about, so the
            // query's verdict rests on its own account's answer alone.
            Wan(Operator).RaiseWanRadioRadioListReceived(new[] { WanRadio(SomeOtherRadio) });
            Assert.True(connect.Wait(8000), "the connect never finished on its own account's list");
            Wan(Owner).RaiseWanRadioRadioListReceived(Array.Empty<Radio>());
            Assert.True(query.Wait(8000), "the query never finished");

            Assert.Equal("Success", connect.Result.ToString());
            Assert.True(query.Result.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, query.Result.Verdict);
        }

        /// <summary>
        /// Two queries under two accounts, finishing in the opposite order to
        /// the one they started in: each hears its own account's answer.
        /// </summary>
        [Fact]
        public void Two_queries_finishing_out_of_order_each_hear_their_own_answer()
        {
            _coordinator.GetOrCreateSession(Operator);
            _coordinator.GetOrCreateSession(Owner);
            var rig = NewRig();

            var first = Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, Operator, "test-jwt"));
            WaitUntil(() => Wan(Operator).SendRegisterCallCount > 0, "the first query never registered");
            var second = Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, Owner, "test-jwt"));
            WaitUntil(() => Wan(Owner).SendRegisterCallCount > 0, "the second query never registered");

            Wan(Owner).RaiseWanRadioRadioListReceived(new[] { WanRadio(SomeOtherRadio) });   // the second answers first
            Assert.True(second.Wait(8000));
            Wan(Operator).RaiseWanRadioRadioListReceived(Array.Empty<Radio>());
            Assert.True(first.Wait(8000));

            Assert.True(second.Result.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, second.Result.Verdict);
            Assert.True(first.Result.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, first.Result.Verdict);
        }

        // ------------------------------------------------------------------
        // #613 / #798 B3: the empty shortcut and a fresh answer
        // ------------------------------------------------------------------

        /// <summary>
        /// The account's session is connected but has had no list when the
        /// call starts. During the call its first list, empty, is accepted and
        /// parked before it reaches the intake. The call must wait for that
        /// answer, not take the session's snapshot as an old one and return
        /// without it (Codex B3, the adjacent schedule).
        /// </summary>
        [Fact]
        public void A_first_empty_list_arriving_during_the_call_is_the_calls_answer()
        {
            var (session, wan) = ConnectedNotYetListed("default-account");
            Listed(Third, SomeOtherRadio);              // a held account whose list the call replays
            var owner = (WanSessionOwner)session;
            var release = new ManualResetEventSlim();
            var rig = NewRig();

            bool parked = false;
            rig.WanIntakeStageReached = stage =>
            {
                if (stage != FlexBase.WanIntakeStage.ReplaySnapshotTaken || parked) return;
                parked = true;
                var accepted = new ManualResetEventSlim();
                owner.BeforeListForwarded = () =>
                {
                    owner.BeforeListForwarded = null;
                    accepted.Set();
                    release.Wait(10000);
                };
                Task.Run(() => wan.RaiseWanRadioRadioListReceived(Array.Empty<Radio>()));
                Assert.True(accepted.Wait(5000), "the account's empty list was never accepted");
            };

            var query = Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, "default-account", "test-jwt"));
            Thread.Sleep(600);
            bool finishedWhileParked = query.IsCompleted;
            release.Set();
            Assert.True(query.Wait(8000));

            Assert.True(parked, "the schedule never ran: no held list was replayed");
            Assert.False(finishedWhileParked, "the call returned while the account's own answer was still on its way");
            Assert.True(query.Result.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, query.Result.Verdict);
        }

        /// <summary>
        /// Codex B3, the cycle schedule. The account's session starts the call
        /// connected with an arrived EMPTY list, which alone would let the call
        /// skip its wait. During the call the connection drops and comes back,
        /// and the new connection's empty list is accepted and parked before
        /// the intake. That list is this call's answer and must be waited for:
        /// the snapshot is empty in both readings, but it is not the same list.
        /// </summary>
        [Fact]
        public void A_reconnects_empty_list_during_the_call_is_waited_for_not_skipped()
        {
            var (session, wan) = Listed("default-account");          // connected, arrived, empty
            Listed(Third, SomeOtherRadio);
            var owner = (WanSessionOwner)session;
            var release = new ManualResetEventSlim();
            var rig = NewRig();

            bool cycled = false;
            rig.WanIntakeStageReached = stage =>
            {
                if (stage != FlexBase.WanIntakeStage.ReplaySnapshotTaken || cycled) return;
                cycled = true;
                long before = owner.RadioListSnapshot.ConnectionGeneration;
                int dials = wan.ConnectCallCount;
                wan.ForceIsConnected(false);
                WaitUntil(() => wan.ConnectCallCount > dials && session.IsConnected, "the session never reconnected");
                var accepted = new ManualResetEventSlim();
                owner.BeforeListForwarded = () =>
                {
                    owner.BeforeListForwarded = null;
                    accepted.Set();
                    release.Wait(10000);
                };
                Task.Run(() => wan.RaiseWanRadioRadioListReceived(Array.Empty<Radio>()));
                Assert.True(accepted.Wait(5000), "the new connection's empty list was never accepted");
                Assert.True(owner.RadioListSnapshot.ArrivedOnTheLiveConnection);
            };

            var query = Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, "default-account", "test-jwt"));
            Thread.Sleep(600);
            bool finishedWhileParked = query.IsCompleted;
            release.Set();
            Assert.True(query.Wait(8000));

            Assert.True(cycled, "the schedule never ran");
            Assert.False(finishedWhileParked, "the call skipped its wait on a list it had not found at its start");
            Assert.True(query.Result.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, query.Result.Verdict);
        }

        // ------------------------------------------------------------------
        // #798 B1: live attribution is the session's CURRENT list
        // ------------------------------------------------------------------

        /// <summary>
        /// Codex B1, schedule two. Account A's bank entry names the radio; on
        /// the same connection A's session then accepts a fresh EMPTY list,
        /// parked before the bank is swept. The session already says "not
        /// listed"; the bank still says A. The radio's chosen account B must
        /// win.
        /// </summary>
        [Fact]
        public void A_bank_entry_older_than_the_sessions_current_list_is_not_live_attribution()
        {
            var (session, wan) = Listed(Operator, Radio6300);
            var rig = NewRig();
            rig.ReplayHeldListsIntoTheIntake(Operator, sessionWasAlreadyConnected: true);
            Assert.True(RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = Owner; }));
            var owner = (WanSessionOwner)session;
            var release = new ManualResetEventSlim();
            var accepted = new ManualResetEventSlim();
            owner.BeforeListForwarded = () =>
            {
                owner.BeforeListForwarded = null;
                accepted.Set();
                release.Wait(10000);
            };
            var push = Task.Run(() => wan.RaiseWanRadioRadioListReceived(Array.Empty<Radio>()));
            try
            {
                Assert.True(accepted.Wait(5000));
                Assert.Equal(new AccountRoute(Owner, AccountRouteSource.Preferred), RadioAccountRouting.ForSerial(Radio6300));
            }
            finally
            {
                release.Set();
                push.Wait(5000);
                RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = ""; });
            }
        }

        // ------------------------------------------------------------------
        // #798 B2: the all-accounts verdict must describe one moment
        // ------------------------------------------------------------------

        /// <summary>
        /// Codex B2. A and B both have current empty lists. The planner reads
        /// A; before it reads B, A's new list naming the radio is received.
        /// Saying "not on any of your accounts" then would be false.
        /// </summary>
        [Fact]
        public void A_list_received_while_the_accounts_are_read_is_not_lost_to_a_false_absence()
        {
            Listed(Operator);
            Listed(Owner);
            var rig = NewRig();
            bool changed = false;
            rig.AfterAccountPictureTaken = () =>
            {
                if (changed) return;
                changed = true;
                Wan(Operator).RaiseWanRadioRadioListReceived(new[] { WanRadio(Radio6300) });
                WaitUntil(() => Owner_(Operator).RadioListSnapshot.Radios.Any(r => r.Serial == Radio6300),
                    "the operator's new list never landed");
            };

            bool ready = rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: false,
                savedAccounts: Saved(Operator, Owner));

            Assert.True(changed, "the schedule never ran");
            Assert.True(ready, "A radio listed by the time the verdict was acted on was reported as on no account.");
            Assert.Null(rig.LastConnectFailureReport);
        }
    }
}
