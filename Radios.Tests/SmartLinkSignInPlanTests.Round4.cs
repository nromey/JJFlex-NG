#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Repair round 4 (Codex's re-check of #713): a call finishes only on an
    /// answer for ITS account; another call's replay of a held list never
    /// finishes it. Every schedule waits on the barrier it names.
    /// </summary>
    public sealed partial class SmartLinkSignInPlanTests
    {
        /// <summary>
        /// Codex B3, on one rig. B's query is waiting for its list. B's list
        /// (a radio, not the one asked about) is accepted and parked before it
        /// is forwarded. A's query starts and replays B's held list as a
        /// foreign account. Until round 4 that replay set B's waiting latch,
        /// with no server answer, and B returned Unknown before its own push
        /// was released.
        /// </summary>
        [Fact]
        public void Another_calls_replay_of_a_list_never_finishes_a_query_waiting_for_its_own()
        {
            var (bSession, bWan) = ConnectedNotYetListed(Owner);
            Listed(Operator);                                        // A: answered empty, found at its start
            var bOwner = (WanSessionOwner)bSession;
            var rig = NewRig();

            var bQuery = Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, Owner, "test-jwt"));
            WaitUntil(() => bWan.SendRegisterCallCount > 0, "B's query never opened its wait");

            var accepted = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            bOwner.BeforeListForwarded = () =>
            {
                bOwner.BeforeListForwarded = null;
                accepted.Set();
                release.Wait(10000);
            };
            var push = Task.Run(() => bWan.RaiseWanRadioRadioListReceived(new[] { WanRadio(SomeOtherRadio) }));
            Assert.True(accepted.Wait(5000), "B's list was never accepted");

            try
            {
                var aQuery = Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, Operator, "test-jwt"));
                Assert.True(aQuery.Wait(8000), "A's query never finished");

                bool bFinishedOnTheReplay = bQuery.Wait(600);
                Assert.False(bFinishedOnTheReplay, "B's query finished on A's replay of B's list, without its own answer.");
            }
            finally
            {
                release.Set();
                push.Wait(5000);
            }

            Assert.True(bQuery.Wait(8000), "B's query never finished");
            Assert.True(bQuery.Result.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, bQuery.Result.Verdict);
        }

        /// <summary>
        /// The paired control: the same waiting query and the same parked
        /// list, with no other call. B waits, and the released push is its
        /// live answer. So the test above fails only through the replay.
        /// </summary>
        [Fact]
        public void With_no_other_call_the_waiting_query_hears_its_own_answer()
        {
            var (bSession, bWan) = ConnectedNotYetListed(Owner);
            var bOwner = (WanSessionOwner)bSession;
            var rig = NewRig();

            var bQuery = Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, Owner, "test-jwt"));
            WaitUntil(() => bWan.SendRegisterCallCount > 0, "B's query never opened its wait");

            var accepted = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            bOwner.BeforeListForwarded = () =>
            {
                bOwner.BeforeListForwarded = null;
                accepted.Set();
                release.Wait(10000);
            };
            var push = Task.Run(() => bWan.RaiseWanRadioRadioListReceived(new[] { WanRadio(SomeOtherRadio) }));
            Assert.True(accepted.Wait(5000), "B's list was never accepted");
            bool finishedEarly = bQuery.Wait(600);
            release.Set();
            push.Wait(5000);

            Assert.False(finishedEarly);
            Assert.True(bQuery.Wait(8000));
            Assert.True(bQuery.Result.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, bQuery.Result.Verdict);
        }

        /// <summary>
        /// Codex's coverage note: two queries finishing in the order they
        /// started (the round-2 test covers the reverse). Each hears its own
        /// account's answer.
        /// </summary>
        [Fact]
        public void Two_queries_finishing_in_the_order_they_started_each_hear_their_own_answer()
        {
            _coordinator.GetOrCreateSession(Operator);
            _coordinator.GetOrCreateSession(Owner);
            var rig = NewRig();

            var first = Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, Operator, "test-jwt"));
            WaitUntil(() => Wan(Operator).SendRegisterCallCount > 0, "the first query never registered");
            var second = Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, Owner, "test-jwt"));
            WaitUntil(() => Wan(Owner).SendRegisterCallCount > 0, "the second query never registered");

            Wan(Operator).RaiseWanRadioRadioListReceived(Array.Empty<Radio>());       // the first answers first
            Assert.True(first.Wait(8000));
            Wan(Owner).RaiseWanRadioRadioListReceived(new[] { WanRadio(SomeOtherRadio) });
            Assert.True(second.Wait(8000));

            Assert.True(first.Result.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, first.Result.Verdict);
            Assert.True(second.Result.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, second.Result.Verdict);
        }

        // ------------------------------------------------------------------
        // #798/#613 round three, Codex B3-R3a and B3-R3b: what completes a call
        // ------------------------------------------------------------------

        private const string ThirdRadio = "7777-8888-6600-9999";

        /// <summary>
        /// Codex B3-R3a. The rig holds A's rows from A's first list. A query
        /// under A starts; while it replays a held account, a NEWER A list on
        /// the same connection is accepted and parked before it is forwarded.
        /// Until round 4 the old rows were taken as rows from the newer list
        /// (they matched its session and connection), so the query finished
        /// on them and lost the server's answer.
        /// </summary>
        [Fact]
        public void Older_rows_on_the_same_connection_are_not_the_newer_list()
        {
            var (aSession, aWan) = Listed(Operator, SomeOtherRadio);
            var rig = NewRig();
            Assert.True(rig.ReplayHeldListsIntoTheIntake(Operator, sessionWasAlreadyConnected: true) >= 1);
            Listed(Owner, ThirdRadio);                 // held, replayed by the query: the barrier
            var aOwner = (WanSessionOwner)aSession;
            long generation = aOwner.RadioListSnapshot.ConnectionGeneration;

            var accepted = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            bool parked = false;
            rig.WanIntakeStageReached = stage =>
            {
                if (stage != FlexBase.WanIntakeStage.ReplaySnapshotTaken || parked) return;
                parked = true;
                aOwner.BeforeListForwarded = () =>
                {
                    aOwner.BeforeListForwarded = null;
                    accepted.Set();
                    release.Wait(10000);
                };
                Task.Run(() => aWan.RaiseWanRadioRadioListReceived(new[] { WanRadio(ThirdRadio) }));
                Assert.True(accepted.Wait(5000), "A's newer list was never accepted");
                Assert.Equal(generation, aOwner.RadioListSnapshot.ConnectionGeneration);
            };

            var query = Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, Operator, "test-jwt"));
            bool reached = accepted.Wait(8000);
            bool finishedOnOldRows = query.Wait(600);
            release.Set();
            Assert.True(query.Wait(8000));

            Assert.True(reached, "the schedule never ran");
            Assert.False(finishedOnOldRows, "the query finished on rows from an older list while its own answer was on its way");
            Assert.True(query.Result.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, query.Result.Verdict);
        }

        /// <summary>
        /// Codex B3-R3b, on one rig. A's query is replaying a held account
        /// (B, earlier in the coordinator's order) when A's first list, with a
        /// radio in it, is accepted and parked. A concurrent connect under B
        /// replays A's just-accepted list as a held copy. Until round 4 that
        /// set A's latch, or gave this rig rows from A's list that A's query
        /// then answered from, and A's server answer was lost as Unknown.
        /// </summary>
        [Fact]
        public void A_concurrent_calls_replay_of_a_fresh_list_does_not_answer_the_call_waiting_for_it()
        {
            Listed(Owner, ThirdRadio);                                    // B: replayed first
            var (aSession, aWan) = ConnectedNotYetListed(Operator);      // A: no list yet
            var aOwner = (WanSessionOwner)aSession;
            var rig = NewRig();

            var accepted = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var resumeA = new ManualResetEventSlim();
            int hooks = 0;
            rig.WanIntakeStageReached = stage =>
            {
                if (stage != FlexBase.WanIntakeStage.ReplaySnapshotTaken) return;
                if (Interlocked.Increment(ref hooks) != 1) return;   // only A's first replay is held
                aOwner.BeforeListForwarded = () =>
                {
                    aOwner.BeforeListForwarded = null;
                    accepted.Set();
                    release.Wait(10000);
                };
                Task.Run(() => aWan.RaiseWanRadioRadioListReceived(new[] { WanRadio(SomeOtherRadio) }));
                Assert.True(accepted.Wait(5000), "A's list was never accepted");
                resumeA.Wait(10000);
            };

            var aQuery = Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, Operator, "test-jwt"));
            Assert.True(accepted.Wait(8000), "the schedule never reached A's accepted list");

            // The concurrent call, on the same rig, under B.
            var bConnect = Task.Run(() => Connect(rig, "test-jwt", Owner));
            Assert.True(bConnect.Wait(8000), "B's connect never finished");
            Assert.Equal("Success", bConnect.Result.ToString());

            resumeA.Set();
            bool aFinishedOnTheReplay = aQuery.Wait(600);
            release.Set();
            Assert.True(aQuery.Wait(8000));

            Assert.False(aFinishedOnTheReplay, "A's query finished on B's replay of A's list, without its own answer");
            Assert.True(aQuery.Result.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, aQuery.Result.Verdict);
        }

        /// <summary>
        /// The held side of B3-R3a. This rig took A's rows from A's first
        /// list; A's second list, on the same connection and naming the radio,
        /// was accepted while no rig was the intake, so this rig never took
        /// it. A query under A then finds that second list at its start and
        /// answers from it. It must answer from rows taken from THAT list: rows
        /// matched by session and connection alone would pass for it, and the
        /// query would answer from the first list's rows instead.
        /// </summary>
        [Fact]
        public void Held_rows_from_an_older_list_on_the_same_connection_are_refreshed_before_they_answer()
        {
            var (aSession, aWan) = Listed(Operator, SomeOtherRadio);
            var rig = NewRig();
            Assert.True(rig.ReplayHeldListsIntoTheIntake(Operator, sessionWasAlreadyConnected: true) >= 1);
            long generation = aSession.RadioListSnapshot.ConnectionGeneration;
            long before = aSession.RadioListSnapshot.ListRevision;

            aWan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Radio6300) });   // no rig is the intake
            WaitUntil(() => aSession.RadioListSnapshot.ListRevision > before, "A's second list was never accepted");
            Assert.Equal(generation, aSession.RadioListSnapshot.ConnectionGeneration);

            var finding = rig.AskSmartLinkAboutSerial(Radio6300, Operator, "test-jwt");

            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.Registered, finding.Verdict);
            // The rows the query answered from are the held list's, not the first list's.
            Assert.NotNull(rig.FindRadioBySerial(Radio6300));
            Assert.Null(rig.FindRadioBySerial(SomeOtherRadio));
        }
    }
}
