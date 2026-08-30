#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Task #386 — one intake, not N.
    ///
    /// <para><b>What was wrong.</b> <c>EngageSmartLinkPresence</c> and
    /// <c>ConnectToSmartLink</c> subscribed an INSTANCE handler to the
    /// coordinator's app-lifetime <c>SessionRadioListReceived</c>. The event
    /// therefore rooted every <see cref="FlexBase"/> that had ever engaged, and
    /// each of them kept consuming presence pushes for the life of the
    /// process — one more per picker open since #382 moved the switch-on point
    /// there.</para>
    ///
    /// <para><b>Why nothing ever broke.</b> Every consumer downstream is
    /// idempotent, so N handlers did N times the work and produced one correct
    /// outcome. That is what these tests exist to replace: an invariant that
    /// holds by design instead of by the luck of three unrelated decisions.
    /// The failure it was waiting for is a consumer that is NOT idempotent, at
    /// which point the symptom is a multiply-announced or multiply-applied
    /// action in code that looks innocent.</para>
    ///
    /// <para><b>All offline.</b> A <see cref="MockWanServer"/> behind a real
    /// <see cref="WanSessionOwner"/> behind a real
    /// <see cref="SmartLinkSessionCoordinator"/>, so a push travels the whole
    /// production path — nothing here reaches a network or a radio.</para>
    /// </summary>
    [Collection(SmartLinkSingletonCollection.Name)]
    public sealed class PresenceIntakeTests : IDisposable
    {
        // The account key FlexBase waits on when no account has been chosen —
        // FlexBase.CurrentSessionKey's fallback. Using it means the intake's
        // own latch is armed by these pushes, which is what makes "did this
        // instance consume it?" answerable at all. It deliberately has no '@',
        // so the intake's fast-paint cache write is skipped and no test here
        // touches a settings tree.
        private const string Account = "default-account";

        private readonly SmartLinkSessionCoordinator _original;
        private readonly Func<IReadOnlyList<SmartLinkAccount>>? _originalAccountsHook;
        private readonly Func<SmartLinkAccount, bool, string?>? _originalJwtHook;
        private readonly List<FlexBase> _rigs = new();
        private readonly List<SmartLinkSessionCoordinator> _coordinators = new();

        public PresenceIntakeTests()
        {
            // Read the real one before replacing it, so the override is
            // restored exactly rather than to a guess.
            _original = SmartLinkServices.Coordinator;

            _originalAccountsHook = SmartLinkPresenceService.AccountsHook;
            _originalJwtHook = SmartLinkPresenceService.SilentJwtHook;

            // Engaging presence also asks the presence service to hold sessions
            // for every saved account. Pre-wiring both hooks means FlexBase's
            // `??=` leaves these in place, so nothing here reads the operator's
            // saved accounts or tries to mint a token.
            SmartLinkPresenceService.AccountsHook = () => Array.Empty<SmartLinkAccount>();
            SmartLinkPresenceService.SilentJwtHook = (_, __) => null;
        }

        public void Dispose()
        {
            foreach (var rig in _rigs)
            {
                try { rig.Dispose(); } catch { /* a test may have disposed it already */ }
            }

            SmartLinkServices.Override(_original);
            SmartLinkPresenceService.AccountsHook = _originalAccountsHook;
            SmartLinkPresenceService.SilentJwtHook = _originalJwtHook;

            foreach (var c in _coordinators)
            {
                try { c.Dispose(); } catch { /* nothing under test depends on the teardown */ }
            }
        }

        // ------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------

        /// <summary>
        /// A coordinator holding one mock-backed session for <see cref="Account"/>,
        /// installed as the process singleton, plus the mock a test pushes
        /// through.
        /// </summary>
        private (SmartLinkSessionCoordinator coordinator, MockWanServer wan) NewCoordinator()
        {
            MockWanServer? wan = null;
            var coordinator = new SmartLinkSessionCoordinator(accountId =>
            {
                wan = new MockWanServer();
                return new WanSessionOwner(
                    sessionId: Guid.NewGuid().ToString("N").Substring(0, 12),
                    accountId: accountId,
                    wanServer: wan,
                    audioSink: new DirectPassthroughSink());
            });
            _coordinators.Add(coordinator);

            SmartLinkServices.Override(coordinator);

            // Registers the owner with the coordinator, which is what wires its
            // list event through to SessionRadioListReceived. Does not connect.
            coordinator.GetOrCreateSession(Account);

            Assert.NotNull(wan);
            return (coordinator, wan!);
        }

        private FlexBase NewRig()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            _rigs.Add(rig);
            return rig;
        }

        /// <summary>The server pushing an updated radio list, as it does for as
        /// long as a registered session lives.</summary>
        private static void Push(MockWanServer wan) =>
            wan.RaiseWanRadioRadioListReceived(Array.Empty<Radio>());

        // FlexBase's own record that a list arrived for the account it is
        // waiting on. Private, and read here rather than widened: the fix is
        // about who receives, and adding a public "did you receive?" would be
        // API invented for a test.
        private static readonly FieldInfo LatchField =
            typeof(FlexBase).GetField("wanListReceived", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static bool Consumed(FlexBase rig)
        {
            Assert.True(LatchField != null,
                "FlexBase.wanListReceived is not where this test reads it. Without that field " +
                "every assertion below is vacuous — find its new name before trusting a green run.");
            return (bool)LatchField!.GetValue(rig)!;
        }

        private static void ClearConsumed(FlexBase rig) => LatchField!.SetValue(rig, false);

        // The coordinator's list event, by its backing field, so the test can
        // count what is actually subscribed rather than infer it.
        private static readonly FieldInfo EventField =
            typeof(SmartLinkSessionCoordinator).GetField(
                nameof(SmartLinkSessionCoordinator.SessionRadioListReceived),
                BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static int HandlerCount(SmartLinkSessionCoordinator coordinator)
        {
            Assert.True(EventField != null,
                "SessionRadioListReceived's backing field is not where this test reads it, so a " +
                "count of zero below would mean nothing at all.");
            var del = (Delegate?)EventField!.GetValue(coordinator);
            return del?.GetInvocationList().Length ?? 0;
        }

        // ------------------------------------------------------------------
        // The invariant
        // ------------------------------------------------------------------

        /// <summary>
        /// The whole of #386 in one assertion: the coordinator holds ONE
        /// handler however many rigs engage. Before the fix this number was the
        /// number of rigs, and it only ever went up.
        /// </summary>
        [Fact]
        public void TheCoordinatorHoldsOneHandlerHoweverManyRigsEngage()
        {
            var (coordinator, _) = NewCoordinator();

            // Positive control. A fresh coordinator has nothing subscribed, so
            // the count below is measuring engagement and not a constant.
            Assert.Equal(0, HandlerCount(coordinator));

            // Three picker opens' worth. Each one used to leave a handler
            // behind for the life of the app.
            NewRig().EngageSmartLinkPresence();
            NewRig().EngageSmartLinkPresence();
            NewRig().EngageSmartLinkPresence();

            Assert.Equal(1, HandlerCount(coordinator));
        }

        /// <summary>
        /// The abandoned rig — the cancelled picker's, or any other — stops
        /// consuming the moment another engages. Deliberately WITHOUT disposing
        /// it: the invariant must not depend on a caller keeping a promise.
        /// </summary>
        [Fact]
        public void OnlyTheLastRigToEngageConsumesAPush()
        {
            var (_, wan) = NewCoordinator();

            var abandoned = NewRig();
            abandoned.EngageSmartLinkPresence();

            // Positive control, and it is load-bearing: it proves a push
            // travels mock → owner → coordinator → intake at all. Without it,
            // the "did not consume" assertion further down would pass just as
            // well if nothing were being delivered to anybody.
            Push(wan);
            Assert.True(Consumed(abandoned),
                "The one engaged rig did not see the push, so this test is not exercising the path it claims to.");

            ClearConsumed(abandoned);

            var current = NewRig();
            current.EngageSmartLinkPresence();

            Push(wan);

            Assert.True(Consumed(current), "The rig that engaged last did not consume the push.");
            Assert.False(Consumed(abandoned),
                "An abandoned rig is still consuming presence pushes. That is #386: it costs nothing " +
                "visible only while every consumer downstream is idempotent.");
        }

        /// <summary>
        /// Engaging twice on the same instance is what the ordinary connect
        /// flow does — the picker engages on open, then ConnectToSmartLink
        /// engages again — so it must stay idempotent.
        /// </summary>
        [Fact]
        public void ARigEngagingTwiceIsStillOneHandlerAndOneIntake()
        {
            var (coordinator, wan) = NewCoordinator();

            var rig = NewRig();
            rig.EngageSmartLinkPresence();
            rig.EngageSmartLinkPresence();

            Assert.Equal(1, HandlerCount(coordinator));
            Assert.Same(rig, FlexBase.PresenceIntake);

            Push(wan);
            Assert.True(Consumed(rig));
        }

        /// <summary>
        /// Disposal resigns. A torn-down rig in the push path would ghost-sweep
        /// a radio list it can no longer vouch for and raise RadioRemoved into
        /// whatever picker is open.
        /// </summary>
        [Fact]
        public void ADisposedRigIsNoLongerTheIntake()
        {
            var (_, wan) = NewCoordinator();

            var rig = NewRig();
            rig.EngageSmartLinkPresence();
            Assert.Same(rig, FlexBase.PresenceIntake);

            rig.Dispose();

            Assert.Null(FlexBase.PresenceIntake);

            // And a push with nobody home is a no-op rather than a throw: this
            // is the ordinary state between a teardown and the next picker.
            ClearConsumed(rig);
            Push(wan);
            Assert.False(Consumed(rig));
        }

        /// <summary>
        /// The nested case, and the reason a single slot is not enough.
        ///
        /// <para>The picker's Test button opens ConnectionTesterDialog while
        /// the picker is still on screen, and the tester builds, connects and
        /// disposes a FlexBase of its own for every test. Clearing the intake
        /// on Dispose would leave the picker open with nothing consuming
        /// presence pushes — its rows quietly stop updating, which is the state
        /// #382 exists to prevent, reached from the other side. The rig the
        /// tester displaced has to get the intake back.</para>
        /// </summary>
        [Fact]
        public void WhenANestedRigFinishesTheIntakeGoesBackToTheRigItDisplaced()
        {
            var (_, wan) = NewCoordinator();

            var picker = NewRig();
            picker.EngageSmartLinkPresence();

            var tester = NewRig();
            tester.EngageSmartLinkPresence();
            Assert.Same(tester, FlexBase.PresenceIntake);

            // Positive control on the displacement itself: while the tester
            // holds the intake, the picker's rig is genuinely not consuming.
            Push(wan);
            Assert.True(Consumed(tester));
            Assert.False(Consumed(picker));

            tester.Dispose();

            Assert.Same(picker, FlexBase.PresenceIntake);

            ClearConsumed(picker);
            Push(wan);
            Assert.True(Consumed(picker),
                "The picker's rig did not get the intake back when the nested rig was disposed, so its " +
                "rows would go stale for the rest of the picker session with nothing to say so.");
        }

        /// <summary>
        /// Disposing a rig that is NOT the intake must not silence the one that
        /// is. Same shape as the nested case, disposal the other way round.
        /// </summary>
        [Fact]
        public void DisposingSomeOtherRigDoesNotDisturbTheIntake()
        {
            var (_, wan) = NewCoordinator();

            var earlier = NewRig();
            earlier.EngageSmartLinkPresence();

            var current = NewRig();
            current.EngageSmartLinkPresence();

            earlier.Dispose();

            Assert.Same(current, FlexBase.PresenceIntake);
            Push(wan);
            Assert.True(Consumed(current),
                "Disposing an older rig cleared the intake, so presence pushes stopped reaching the live one.");
        }

        /// <summary>
        /// The dispatcher follows the coordinator SINGLETON, not a "wired once"
        /// flag. Replacing the coordinator — which <c>SmartLinkServices.Override</c>
        /// exists to do — must move the wiring, or pushes arrive at an object
        /// nobody is listening to and the silence looks exactly like a radio
        /// being switched off.
        /// </summary>
        [Fact]
        public void ReplacingTheCoordinatorMovesTheWiring()
        {
            var (first, firstWan) = NewCoordinator();
            NewRig().EngageSmartLinkPresence();
            Assert.Equal(1, HandlerCount(first));

            var (second, secondWan) = NewCoordinator();
            var rig = NewRig();
            rig.EngageSmartLinkPresence();

            Assert.Equal(0, HandlerCount(first));
            Assert.Equal(1, HandlerCount(second));

            Push(secondWan);
            Assert.True(Consumed(rig), "A push through the current coordinator did not reach the intake.");

            // And the retired coordinator is genuinely disconnected, not merely
            // uncounted.
            ClearConsumed(rig);
            Push(firstWan);
            Assert.False(Consumed(rig));
        }
    }
}
