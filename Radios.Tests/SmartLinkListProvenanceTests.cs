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

using Evidence = Radios.SmartLinkRegistrationEvidence;
using Verdict = Radios.FlexBase.SmartLinkRegistrationQuery;

namespace Radios.Tests
{
    /// <summary>
    /// Task #619, Sol's review of Track L5: a list's provenance travels to the
    /// decision that consumes it, and every consumer compares it with the
    /// live connection at the moment of deciding.
    /// </summary>
    /// <remarks>
    /// <para><b>The four orderings, each held exactly.</b> L5 closed the
    /// read-before-lock gap in the owner, and its tests parked a callback
    /// BEFORE the owner's decision. Sol's review named four orderings that
    /// live after it: an accepted list forwarded after the next connection's
    /// (parked BETWEEN acceptance and forwarding, which L5's hook could not
    /// reach); a list from a transport that has died while the session still
    /// reads Connected; a re-entered rig whose rows predate the live list
    /// because another rig was the intake; and a Dispose or Disconnect that
    /// overlaps a dial. Each test below builds one of them with the monitor
    /// or a callback parked, so the assertion is about the ordering and not
    /// about the scheduler.</para>
    ///
    /// <para><b>Nothing here reaches a network, a radio or a settings
    /// tree.</b> The account key is the connect flow's own fallback,
    /// <c>default-account</c>, which has no '@', so the intake's fast-paint
    /// cache write is skipped.</para>
    /// </remarks>
    [Collection(SmartLinkSingletonCollection.Name)]
    public sealed class SmartLinkListProvenanceTests : IDisposable
    {
        private const string Account = "default-account";
        private const string Listed = "4925-1213-8600-6245";
        private const string Other = "1111-2222-6400-3333";
        private const string Third = "5555-6666-6600-7777";

        private readonly SmartLinkSessionCoordinator _original;
        private readonly Func<IReadOnlyList<SmartLinkAccount>>? _originalAccountsHook;
        private readonly Func<SmartLinkAccount, bool, string?>? _originalJwtHook;
        private readonly List<FlexBase> _rigs = new();
        private readonly List<SmartLinkSessionCoordinator> _coordinators = new();

        public SmartLinkListProvenanceTests()
        {
            _original = SmartLinkServices.Coordinator;
            _originalAccountsHook = SmartLinkPresenceService.AccountsHook;
            _originalJwtHook = SmartLinkPresenceService.SilentJwtHook;
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
            ForgetWanRadiosForAccount?.Invoke(null, new object[] { Account, "test teardown" });
        }

        // ------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------

        /// <summary>
        /// A mock behind a real owner behind a real coordinator installed as
        /// the process singleton, so a push travels the production path to
        /// whichever rig is the intake. The owner is returned as its concrete
        /// type for the suite-only hooks.
        /// </summary>
        private (WanSessionOwner owner, MockWanServer wan) NewSession()
        {
            MockWanServer? wan = null;
            var coordinator = new SmartLinkSessionCoordinator(accountId =>
            {
                wan = new MockWanServer();
                return new WanSessionOwner(
                    sessionId: Guid.NewGuid().ToString("N").Substring(0, 12),
                    accountId: accountId,
                    wanServer: wan,
                    audioSink: new DirectPassthroughSink(),
                    backoffScheduleMs: new[] { 50, 50, 50 });
            });
            _coordinators.Add(coordinator);
            SmartLinkServices.Override(coordinator);
            var owner = (WanSessionOwner)coordinator.GetOrCreateSession(Account);
            Assert.NotNull(wan);
            return (owner, wan!);
        }

        private FlexBase NewRig()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            _rigs.Add(rig);
            return rig;
        }

        private static void WaitUntil(Func<bool> condition, string because, int timeoutMs = 5000)
        {
            Assert.True(SpinWait.SpinUntil(condition, timeoutMs), because);
        }

        private static Radio WanRadio(string serial) => SmartLinkRegistrationReplayTests.WanRadio(serial);

        private static readonly FieldInfo? RadiosField =
            typeof(FlexBase).GetField("radios", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo? MyRadioListField =
            typeof(FlexBase).GetField("myRadioList", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo? ForgetWanRadiosForAccount =
            typeof(FlexBase).GetMethod("ForgetWanRadiosForAccount", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo? LatchField =
            typeof(FlexBase).GetField("wanListReceived", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo? ServerListThisCallField =
            typeof(FlexBase).GetField("_serverListThisCall", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>The raw latch field, read without the intake's lock —
        /// what an unguarded reader would see.</summary>
        private static bool LatchFieldOf(FlexBase rig)
        {
            Assert.True(LatchField != null, "FlexBase.wanListReceived is not where this test reads it.");
            return (bool)LatchField!.GetValue(rig)!;
        }

        private static bool ServerSpokeThisCall(FlexBase rig)
        {
            Assert.True(ServerListThisCallField != null, "FlexBase._serverListThisCall is not where this test reads it.");
            return ServerListThisCallField!.GetValue(rig) != null;
        }

        /// <summary>What ConnectToSmartLink does when a call begins: the
        /// latch and the server-spoke capture are cleared, so the next list
        /// is the one this call is waiting for.</summary>
        private static void BeginACall(FlexBase rig)
        {
            Assert.True(LatchField != null && ServerListThisCallField != null,
                "The connect flow's latch fields are not where this test clears them.");
            LatchField!.SetValue(rig, false);
            ServerListThisCallField!.SetValue(rig, null);
        }

        /// <summary>The serials of the list the connect flow reads downstream.</summary>
        private static List<string> RadiosOf(FlexBase rig)
        {
            Assert.True(RadiosField != null, "FlexBase.radios is not where this test reads it.");
            var list = (List<Radio>?)RadiosField!.GetValue(rig);
            return list == null ? new List<string>() : list.Select(r => r.Serial).ToList();
        }

        private static List<string> WanSerialsInMyRadioList(FlexBase rig)
        {
            Assert.True(MyRadioListField != null, "FlexBase.myRadioList is not where this test reads it.");
            var list = (List<Radio>)MyRadioListField!.GetValue(rig)!;
            return list.Where(r => r.IsWan).Select(r => r.Serial).ToList();
        }

        private static void ConnectAndList(WanSessionOwner owner, MockWanServer wan, string serial)
        {
            owner.Connect();
            WaitUntil(() => owner.IsConnected, "the mock session never reported connected");
            wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(serial) });
        }

        private static void DropAndRedial(WanSessionOwner owner, MockWanServer wan)
        {
            int dialsBefore = wan.ConnectCallCount;
            wan.ForceIsConnected(false);
            WaitUntil(() => wan.ConnectCallCount > dialsBefore && owner.IsConnected,
                "the session never dialled a new connection after the drop");
        }

        // ------------------------------------------------------------------
        // 1. An accepted list forwarded after the next connection's
        // ------------------------------------------------------------------

        /// <summary>
        /// The ordering L5's test could not hold. Connection 1's list is
        /// ACCEPTED by the owner — under its lock, correctly, it was the live
        /// connection then — and parked before the owner forwards it. The
        /// transport dies, the monitor dials connection 2, connection 2's
        /// list is accepted, forwarded and consumed by the intake. Then the
        /// parked list is forwarded. The coordinator used to drop the
        /// generation, so the intake read it as a fresh push and overwrote
        /// connection 2's list with connection 1's: the latch, <c>radios</c>,
        /// the ghost sweep, the WAN bank, the cache. The list carries its
        /// generation now, and the intake asks the session whether that
        /// generation is still current at the moment it consumes.
        /// </summary>
        /// <remarks>
        /// The first push is the positive control: it proves this rig is
        /// the intake and consumes a forwarded list, so "did not consume"
        /// below is refusal and not silence. Letting the intake consume
        /// without asking turns the <c>radios</c> assertion red.
        /// </remarks>
        [Fact]
        public void An_accepted_list_forwarded_after_the_next_connections_is_not_consumed()
        {
            var (owner, wan) = NewSession();
            var release = new ManualResetEventSlim();
            try
            {
                var rig = NewRig();
                rig.EngageSmartLinkPresence();
                ConnectAndList(owner, wan, Listed);
                Assert.Equal(new[] { Listed }, RadiosOf(rig));

                // Connection 1's second list: accepted, then parked before
                // the forward.
                var accepted = new ManualResetEventSlim();
                owner.BeforeListForwarded = () =>
                {
                    owner.BeforeListForwarded = null;
                    accepted.Set();
                    Assert.True(release.Wait(5000), "the parked list was never released");
                };
                var oldList = Task.Run(() =>
                    wan.RaiseWanRadioRadioListReceivedFrom(1, new[] { WanRadio(Other) }));
                Assert.True(accepted.Wait(5000), "connection 1's list was never accepted");
                // The trap, stated: the owner holds it as its current list.
                Assert.Contains(owner.AvailableRadios, r => r.Serial == Other);

                DropAndRedial(owner, wan);
                Assert.Equal(2, wan.ConnectionGeneration);
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Third) });
                Assert.Equal(new[] { Third }, RadiosOf(rig));
                Assert.Equal(new[] { Third }, WanSerialsInMyRadioList(rig));

                // Connection 1's list arrives at the consumer late.
                release.Set();
                Assert.True(oldList.Wait(5000), "the parked list never finished forwarding");

                Assert.Equal(new[] { Third }, RadiosOf(rig));
                Assert.Equal(new[] { Third }, WanSerialsInMyRadioList(rig));
                Assert.Contains(owner.AvailableRadios, r => r.Serial == Third);
            }
            finally
            {
                release.Set();
                owner.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // 2. A transport that has died while the session still reads Connected
        // ------------------------------------------------------------------

        /// <summary>
        /// Between the transport reporting its death and the monitor thread
        /// changing the session's status, the session reads Connected, and
        /// the list held from that transport read as the live connection's:
        /// a registration query took it as a current listing. The monitor is
        /// parked inside its own Connected transition, so the status cannot
        /// move; the transport dies; the snapshot must not call the list
        /// live, and a late list from the same transport must be refused —
        /// on the transport's own generation-stamped edge, not on a status
        /// the monitor has not reached.
        /// </summary>
        /// <remarks>
        /// The snapshot before the death is the positive control. Reading
        /// liveness from the monitor's status instead of the transport's
        /// edge turns both the snapshot and the re-raise assertions red.
        /// </remarks>
        [Fact]
        public void A_dead_transports_list_is_not_live_while_the_session_still_reads_Connected()
        {
            var (owner, wan) = NewSession();
            var releaseMonitor = new ManualResetEventSlim();
            try
            {
                var parked = new ManualResetEventSlim();
                bool parkedOnce = false;
                owner.StatusChanged += (_, status) =>
                {
                    if (status != SessionStatus.Connected || parkedOnce) return;
                    parkedOnce = true;
                    parked.Set();
                    releaseMonitor.Wait(30000);
                };
                owner.Connect();
                Assert.True(parked.Wait(5000), "the monitor never reached Connected");
                Assert.True(owner.IsConnected);

                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Listed) });
                Assert.True(owner.RadioListSnapshot.ArrivedOnTheLiveConnection);
                Assert.Equal(Evidence.ListSource.HeldByAConnectedSession,
                    Evidence.HeldLists(new[] { owner }).Single().Source);

                int reRaised = 0;
                owner.RadioListReceived += (_, __) => Interlocked.Increment(ref reRaised);

                // The transport dies. The monitor is parked, so the status
                // stays Connected — the exact window Sol named.
                wan.ForceIsConnected(false);
                Assert.Equal(SessionStatus.Connected, owner.Status);
                Assert.Equal(1, wan.ConnectionGeneration);

                var snapshot = owner.RadioListSnapshot;
                Assert.True(snapshot.SessionConnected);
                Assert.False(snapshot.ArrivedOnTheLiveConnection,
                    "A list from a transport that has reported itself gone was still called the live connection's (#619).");
                var lists = Evidence.HeldLists(new[] { owner });
                Assert.Equal(Evidence.ListSource.HeldFromAnEarlierConnection, lists.Single().Source);
                Assert.NotEqual(Verdict.Registered, Evidence.Judge(Listed, false, true, true, lists).Verdict);

                // A callback the dead transport had in flight lands now.
                wan.RaiseWanRadioRadioListReceivedFrom(1, new[] { WanRadio(Other) });
                Assert.Equal(0, reRaised);
                Assert.DoesNotContain(owner.AvailableRadios, r => r.Serial == Other);

                // Positive control: the monitor resumes, dials connection 2,
                // and connection 2's own list is accepted, re-raised and live.
                releaseMonitor.Set();
                WaitUntil(() => wan.ConnectionGeneration == 2 && owner.IsConnected,
                    "the monitor never dialled the replacement connection");
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Other) });
                Assert.Equal(1, reRaised);
                Assert.True(owner.RadioListSnapshot.ArrivedOnTheLiveConnection);
                Assert.Equal(Evidence.ListSource.HeldByAConnectedSession,
                    Evidence.HeldLists(new[] { owner }).Single().Source);
            }
            finally
            {
                releaseMonitor.Set();
                owner.Dispose();
            }
        }

        /// <summary>
        /// The same death, at the consumer. A list is accepted and parked
        /// before the forward; the transport dies; the monitor is held
        /// before its redial so the generation is unchanged and nothing but
        /// the death can refuse the list. The forwarded list reaches the
        /// intake, which asks the session and is told the transport is gone.
        /// L5 forwarded an accepted list without asking anything, and the
        /// intake labelled it a server push of this call.
        /// </summary>
        [Fact]
        public void A_dead_transports_list_forwarded_to_the_intake_is_not_consumed()
        {
            var (owner, wan) = NewSession();
            var releaseList = new ManualResetEventSlim();
            var releaseDial = new ManualResetEventSlim();
            try
            {
                var rig = NewRig();
                rig.EngageSmartLinkPresence();
                ConnectAndList(owner, wan, Listed);
                Assert.Equal(new[] { Listed }, RadiosOf(rig));

                var accepted = new ManualResetEventSlim();
                owner.BeforeListForwarded = () =>
                {
                    owner.BeforeListForwarded = null;
                    accepted.Set();
                    Assert.True(releaseList.Wait(5000), "the parked list was never released");
                };
                var lateList = Task.Run(() =>
                    wan.RaiseWanRadioRadioListReceivedFrom(1, new[] { WanRadio(Other) }));
                Assert.True(accepted.Wait(5000), "the list was never accepted");

                // The transport dies and the monitor is held with the redial
                // decided but not begun: the generation is still 1.
                var dialing = new ManualResetEventSlim();
                wan.BeforeDialHook = () =>
                {
                    wan.BeforeDialHook = null;
                    dialing.Set();
                    releaseDial.Wait(30000);
                };
                wan.ForceIsConnected(false);
                Assert.True(dialing.Wait(5000), "the monitor never went to redial");
                Assert.Equal(1, wan.ConnectionGeneration);

                releaseList.Set();
                Assert.True(lateList.Wait(5000), "the parked list never finished forwarding");

                Assert.Equal(new[] { Listed }, RadiosOf(rig));
                Assert.Equal(new[] { Listed }, WanSerialsInMyRadioList(rig));

                // Positive control: connection 2 comes up and its list is
                // consumed.
                releaseDial.Set();
                WaitUntil(() => wan.ConnectionGeneration == 2 && owner.IsConnected,
                    "the replacement connection never came up");
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Third) });
                Assert.Equal(new[] { Third }, RadiosOf(rig));
            }
            finally
            {
                releaseList.Set();
                releaseDial.Set();
                owner.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // 3. A re-entered rig whose rows predate the live list
        // ------------------------------------------------------------------

        /// <summary>
        /// Rig A takes connection 1's list. The session drops and redials,
        /// and connection 2's list arrives while another rig is the intake —
        /// so A still holds connection 1's rows. L5 let A's rows answer the
        /// connect because the SESSION's list was live, and skipped the
        /// replay because A had rows. The rows carry which list they came
        /// from now: A may not answer from them, and the replay refreshes
        /// them from the live connection's list.
        /// </summary>
        /// <remarks>
        /// The first two assertions are the positive control: with the live
        /// list in hand, the rows may answer and the replay is skipped.
        /// Letting the session's liveness stand for the rows' turns the
        /// "may not answer" assertion red; skipping the replay on any
        /// matching row turns the replay count red.
        /// </remarks>
        [Fact]
        public void A_rigs_rows_taken_from_an_earlier_connection_may_not_answer_for_the_live_one()
        {
            var (owner, wan) = NewSession();
            try
            {
                ConnectAndList(owner, wan, Listed);

                var a = NewRig();
                Assert.Equal(1, a.ReplayHeldListsIntoTheIntake(Account, sessionWasAlreadyConnected: true));
                Assert.Equal(new[] { Listed }, WanSerialsInMyRadioList(a));
                Assert.True(a.OwnRowsMayAnswerTheConnect(owner, Account));
                Assert.Equal(0, a.ReplayHeldListsIntoTheIntake(Account, sessionWasAlreadyConnected: true));

                // Another rig is the intake when connection 2's list lands.
                var b = NewRig();
                b.EngageSmartLinkPresence();
                DropAndRedial(owner, wan);
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Other) });
                Assert.Equal(new[] { Other }, WanSerialsInMyRadioList(b));
                // The trap, stated: the session's list is live, and A still
                // holds connection 1's rows.
                Assert.True(owner.RadioListSnapshot.ArrivedOnTheLiveConnection);
                Assert.Equal(new[] { Listed }, WanSerialsInMyRadioList(a));

                Assert.False(a.OwnRowsMayAnswerTheConnect(owner, Account),
                    "A rig's rows from an earlier connection were allowed to answer because the SESSION's list was live (#619).");

                Assert.Equal(1, a.ReplayHeldListsIntoTheIntake(Account, sessionWasAlreadyConnected: true));
                Assert.Equal(new[] { Other }, WanSerialsInMyRadioList(a));
                Assert.True(a.OwnRowsMayAnswerTheConnect(owner, Account));
            }
            finally
            {
                owner.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // 4. Dispose and Disconnect overlapping a dial
        // ------------------------------------------------------------------

        /// <summary>
        /// The monitor has decided to dial connection 2 and not yet begun.
        /// Dispose retires the connection. The dial then begins and clears
        /// the retired flag — and connection 2 delivers a list inside the
        /// dial, while the owner is still subscribed because Dispose is
        /// waiting on the monitor. L5's list handler checked neither
        /// shutdown nor user intent, so that list was accepted and forwarded
        /// from a session that was going away. L5's dispose test started from
        /// a connected session and never overlapped a dial.
        /// </summary>
        /// <remarks>
        /// The pending ConnectToRadio is how the test knows Dispose has
        /// passed its retire-and-cancel step before the dial is released:
        /// Dispose cancels it right after marking the connection retired.
        /// Dropping the shutdown check from the owner's predicate turns the
        /// re-raise count red.
        /// </remarks>
        [Fact]
        public void A_list_from_a_connection_dialed_after_Dispose_is_refused()
        {
            var (owner, wan) = NewSession();
            var releaseDial = new ManualResetEventSlim();
            try
            {
                ConnectAndList(owner, wan, Listed);
                var pending = owner.ConnectToRadio(Listed);
                Assert.False(pending.IsCompleted);

                int reRaised = 0;
                owner.RadioListReceived += (_, __) => Interlocked.Increment(ref reRaised);

                var dialing = new ManualResetEventSlim();
                wan.BeforeDialHook = () =>
                {
                    wan.BeforeDialHook = null;
                    dialing.Set();
                    releaseDial.Wait(30000);
                };
                wan.ForceIsConnected(false);
                Assert.True(dialing.Wait(5000), "the monitor never went to redial");

                var disposing = Task.Run(owner.Dispose);
                Assert.True(pending.Wait(5000), "Dispose never cancelled the pending radio connect");
                Assert.Null(pending.Result);

                // Connection 2 delivers a list inside the dial.
                wan.OnPropertyChangedHook = () =>
                {
                    if (!wan.IsConnected) return;
                    wan.OnPropertyChangedHook = null;
                    wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Other) });
                };
                releaseDial.Set();
                Assert.True(disposing.Wait(10000), "Dispose never returned");
                Assert.Null(wan.OnPropertyChangedHook); // the list was delivered inside the dial
                Assert.Equal(2, wan.ConnectionGeneration);

                Assert.Equal(0, reRaised);
                Assert.DoesNotContain(owner.AvailableRadios, r => r.Serial == Other);
                Assert.False(owner.RadioListSnapshot.ArrivedOnTheLiveConnection);
                Assert.Equal(SessionStatus.ShutDown, owner.Status);
            }
            finally
            {
                releaseDial.Set();
                owner.Dispose();
            }
        }

        /// <summary>
        /// The window between Disconnect() recording the operator's intent
        /// and the monitor marking the connection retired, held open: the
        /// monitor is inside a dial when the operator disconnects. A list
        /// from the connection being dialed passed L5's generation check —
        /// it IS the newest — and was forwarded into the coordinator and
        /// the intake from a session the operator had just asked to close.
        /// The owner reads the operator's intent in the same predicate.
        /// </summary>
        /// <remarks>
        /// Dropping the user-intent check from the predicate turns the
        /// re-raise count red; the positive control after the reconnect
        /// proves the harness can produce a re-raise at all.
        /// </remarks>
        [Fact]
        public void A_list_arriving_after_the_operator_disconnected_during_a_dial_is_refused()
        {
            var (owner, wan) = NewSession();
            var releaseDial = new ManualResetEventSlim();
            try
            {
                ConnectAndList(owner, wan, Listed);

                int reRaised = 0;
                owner.RadioListReceived += (_, __) => Interlocked.Increment(ref reRaised);

                var dialing = new ManualResetEventSlim();
                wan.DialHook = () =>
                {
                    wan.DialHook = null;
                    dialing.Set();
                    releaseDial.Wait(30000);
                };
                wan.ForceIsConnected(false);
                Assert.True(dialing.Wait(5000), "the monitor never dialled after the drop");
                Assert.Equal(2, wan.ConnectionGeneration);

                owner.Disconnect();
                // The trap, stated: connection 2 is the newest, nothing has
                // retired it, and the monitor is still inside the dial.
                Assert.NotEqual(SessionStatus.Disconnected, owner.Status);

                wan.RaiseWanRadioRadioListReceivedFrom(2, new[] { WanRadio(Other) });
                Assert.Equal(0, reRaised);
                Assert.DoesNotContain(owner.AvailableRadios, r => r.Serial == Other);

                releaseDial.Set();
                WaitUntil(() => owner.Status == SessionStatus.Disconnected,
                    "the session never settled into Disconnected");

                // Positive control: a new Connect dials connection 3, and its
                // list is accepted, re-raised and live.
                owner.Connect();
                WaitUntil(() => wan.ConnectionGeneration == 3 && owner.IsConnected,
                    "the session never reconnected");
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Third) });
                Assert.Equal(1, reRaised);
                Assert.Contains(owner.AvailableRadios, r => r.Serial == Third);
                Assert.True(owner.RadioListSnapshot.ArrivedOnTheLiveConnection);
            }
            finally
            {
                releaseDial.Set();
                owner.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // 5. The intake's own window (Sol's review of L6)
        // ------------------------------------------------------------------

        /// <summary>
        /// The replay takes one snapshot of a held session, decides from it,
        /// and reaches the intake later. The replay is parked exactly there —
        /// snapshot taken, intake not entered — while the transport dies and
        /// the monitor dials connection 2. Until Track L7 the intake asked
        /// only a PUSH whether its list was still current, so the replay
        /// delivered connection 1's list as the account's full current list:
        /// the latch, <c>radios</c>, the rows, the ghost sweep, the bank.
        /// </summary>
        /// <remarks>
        /// The first rig's replay is the positive control: with nothing
        /// between the snapshot and the intake, the same list is taken.
        /// Exempting a replay from the intake's currency question turns the
        /// parked rig's replay count and rows red.
        /// </remarks>
        [Fact]
        public void A_replay_whose_list_stops_being_current_after_its_snapshot_is_not_consumed()
        {
            var (owner, wan) = NewSession();
            var release = new ManualResetEventSlim();
            try
            {
                ConnectAndList(owner, wan, Listed);

                var control = NewRig();
                Assert.Equal(1, control.ReplayHeldListsIntoTheIntake(Account, sessionWasAlreadyConnected: true));
                Assert.Equal(new[] { Listed }, WanSerialsInMyRadioList(control));
                Assert.Equal(new[] { Listed }, RadiosOf(control));

                var rig = NewRig();
                var parked = new ManualResetEventSlim();
                rig.WanIntakeStageReached = stage =>
                {
                    if (stage != FlexBase.WanIntakeStage.ReplaySnapshotTaken) return;
                    rig.WanIntakeStageReached = null;
                    parked.Set();
                    Assert.True(release.Wait(5000), "the parked replay was never released");
                };
                var replay = Task.Run(() => rig.ReplayHeldListsIntoTheIntake(Account, sessionWasAlreadyConnected: true));
                Assert.True(parked.Wait(5000), "the replay never took its snapshot");

                DropAndRedial(owner, wan);
                Assert.Equal(2, wan.ConnectionGeneration);
                // The trap, stated: the replay holds connection 1's list, the
                // session still holds it too, and it is no longer current.
                Assert.Contains(owner.AvailableRadios, r => r.Serial == Listed);
                Assert.False(owner.ListIsCurrent(1));

                release.Set();
                Assert.True(replay.Wait(5000), "the parked replay never finished");
                Assert.Equal(0, replay.Result);
                Assert.Empty(WanSerialsInMyRadioList(rig));
                Assert.Empty(RadiosOf(rig));
                Assert.False(rig.ConnectListLatched(),
                    "A replayed list that stopped being current between its snapshot and the intake set the connect latch (#619, Sol's review of L6).");
            }
            finally
            {
                release.Set();
                owner.Dispose();
            }
        }

        /// <summary>
        /// A push that the intake has asked about and been told is current,
        /// parked after that answer and before the latch is written, while
        /// the transport dies and connection 2 is dialed. L6a's check was the
        /// only question, so the latch, <c>radios</c>, the server-spoke
        /// capture and the rows all took connection 1's list after
        /// connection 2 existed. The intake now asks again straight after
        /// writing the latch and puts it back when the answer has changed.
        /// </summary>
        /// <remarks>
        /// The first push is the positive control that this rig is the
        /// intake; connection 2's push at the end is the control that the
        /// latch can still be set. Removing the re-validation turns the
        /// latch, <c>radios</c>, the capture and the rows red.
        /// </remarks>
        [Fact]
        public void A_push_whose_connection_is_replaced_after_its_currency_check_leaves_the_latch_as_it_was()
        {
            var (owner, wan) = NewSession();
            var release = new ManualResetEventSlim();
            try
            {
                var rig = NewRig();
                rig.EngageSmartLinkPresence();
                ConnectAndList(owner, wan, Listed);
                Assert.Equal(new[] { Listed }, RadiosOf(rig));
                BeginACall(rig);

                var checkedCurrent = new ManualResetEventSlim();
                rig.WanIntakeStageReached = stage =>
                {
                    if (stage != FlexBase.WanIntakeStage.CurrencyChecked) return;
                    rig.WanIntakeStageReached = null;
                    checkedCurrent.Set();
                    Assert.True(release.Wait(5000), "the parked push was never released");
                };
                var late = Task.Run(() =>
                    wan.RaiseWanRadioRadioListReceivedFrom(1, new[] { WanRadio(Other) }));
                Assert.True(checkedCurrent.Wait(5000), "the intake never checked the push");

                DropAndRedial(owner, wan);
                Assert.Equal(2, wan.ConnectionGeneration);
                Assert.False(owner.ListIsCurrent(1));

                release.Set();
                Assert.True(late.Wait(5000), "the parked push never finished");

                Assert.False(LatchFieldOf(rig),
                    "A push whose connection was replaced after its currency check set the connect latch (#619, Sol's review of L6).");
                Assert.False(ServerSpokeThisCall(rig));
                Assert.Equal(new[] { Listed }, RadiosOf(rig));
                Assert.Equal(new[] { Listed }, WanSerialsInMyRadioList(rig));

                // Control: connection 2's own list sets it.
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Third) });
                Assert.True(rig.ConnectListLatched());
                Assert.True(ServerSpokeThisCall(rig));
                Assert.Equal(new[] { Third }, RadiosOf(rig));
            }
            finally
            {
                release.Set();
                owner.Dispose();
            }
        }

        /// <summary>
        /// The latch is written before it is re-validated, so for a moment
        /// inside the intake it can hold a list that the re-validation will
        /// take back. The connect flow must never see that moment. The push
        /// is parked there — latch written, not yet re-validated — and the
        /// flow's reader must say "not yet" whatever the field holds.
        /// </summary>
        /// <remarks>
        /// The release is the positive control: the same list, re-validated,
        /// is read as latched. Reading the field without regard to the
        /// intake turns the parked assertion red.
        /// </remarks>
        [Fact]
        public void The_connect_flow_never_reads_a_latch_the_intake_has_not_yet_re_validated()
        {
            var (owner, wan) = NewSession();
            var release = new ManualResetEventSlim();
            try
            {
                var rig = NewRig();
                rig.EngageSmartLinkPresence();
                ConnectAndList(owner, wan, Listed);
                BeginACall(rig);

                var written = new ManualResetEventSlim();
                rig.WanIntakeStageReached = stage =>
                {
                    if (stage != FlexBase.WanIntakeStage.LatchWritten) return;
                    rig.WanIntakeStageReached = null;
                    written.Set();
                    Assert.True(release.Wait(5000), "the parked push was never released");
                };
                var push = Task.Run(() =>
                    wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Other) }));
                Assert.True(written.Wait(5000), "the intake never wrote the latch");

                // The trap, stated: the field is set, and it is not yet known
                // to be kept.
                Assert.True(LatchFieldOf(rig));
                Assert.False(rig.ConnectListLatched(),
                    "The connect flow read a latch the intake had written and not yet re-validated (#619).");

                release.Set();
                Assert.True(push.Wait(5000), "the parked push never finished");
                Assert.True(rig.ConnectListLatched());
                Assert.Equal(new[] { Other }, RadiosOf(rig));
            }
            finally
            {
                release.Set();
                owner.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // 6. The window after the intake's final check (Sol's review of L7)
        // ------------------------------------------------------------------

        /// <summary>Every RadioFound raised while this is alive, in order.</summary>
        private sealed class Sightings : IDisposable
        {
            private readonly List<FlexBase.RigData> _seen = new();
            private readonly FlexBase.RadioFoundDel _onFound;

            public Sightings()
            {
                _onFound = (_, r) => { lock (_seen) _seen.Add(r); };
                FlexBase.RadioFound += _onFound;
            }

            public FlexBase.RigData Of(string serial)
            {
                lock (_seen)
                {
                    var matching = _seen.Where(r => r.Serial == serial).ToList();
                    Assert.True(matching.Count == 1,
                        $"expected exactly one RadioFound for {serial}, saw {matching.Count}");
                    return matching[0];
                }
            }

            public void Dispose() => FlexBase.RadioFound -= _onFound;
        }

        /// <summary>
        /// Park a push inside the intake at <paramref name="stage"/> and
        /// return the task delivering it. The caller changes the session
        /// underneath the parked list, then releases it.
        /// </summary>
        private static Task ParkPush(FlexBase rig, MockWanServer wan, long generation, string serial,
            FlexBase.WanIntakeStage stage, ManualResetEventSlim release)
        {
            var parked = new ManualResetEventSlim();
            rig.WanIntakeStageReached = reached =>
            {
                if (reached != stage) return;
                rig.WanIntakeStageReached = null;
                parked.Set();
                Assert.True(release.Wait(5000), "the parked push was never released");
            };
            var push = Task.Run(() => wan.RaiseWanRadioRadioListReceivedFrom(generation, new[] { WanRadio(serial) }));
            Assert.True(parked.Wait(5000), $"the intake never reached {stage}");
            return push;
        }

        /// <summary>
        /// The ordering L7 called harmless and Sol traced one consumer further.
        /// A push has passed the intake's LAST check — the re-validation
        /// after the latch — and is parked before the display writes. The
        /// transport dies and connection 2 is dialed. The intake goes on to
        /// raise RadioFound for the list, as it must (the writes cannot be
        /// taken back), and the picker treats RadioFound as a fresh live
        /// sighting: a live-row rewrite, the connecting window closed, an
        /// arrival announced. The sighting must therefore be able to answer,
        /// at the picker's decision, that the list behind it is no longer
        /// current — and it does. Connection 2's own list is the control
        /// that a current sighting answers yes.
        /// </summary>
        /// <remarks>
        /// Stamping no provenance on the intake's sightings, or answering
        /// yes without asking the session, turns the parked assertion red.
        /// The LAN clause — a sighting with no WAN list behind it answers
        /// yes — is pinned here too, because the picker's refusal must never
        /// reach a radio found on the local network.
        /// </remarks>
        [Fact]
        public void A_sighting_raised_after_the_intakes_final_check_knows_when_a_dial_has_made_its_list_history()
        {
            var (owner, wan) = NewSession();
            var release = new ManualResetEventSlim();
            using var sightings = new Sightings();
            try
            {
                var rig = NewRig();
                rig.EngageSmartLinkPresence();
                ConnectAndList(owner, wan, Listed);
                Assert.True(sightings.Of(Listed).StillCurrent(),
                    "the control sighting, from the live connection with nothing changed, answered no");

                var late = ParkPush(rig, wan, 1, Other, FlexBase.WanIntakeStage.Revalidated, release);

                DropAndRedial(owner, wan);
                Assert.Equal(2, wan.ConnectionGeneration);
                Assert.False(owner.ListIsCurrent(1));

                release.Set();
                Assert.True(late.Wait(5000), "the parked push never finished");

                // The trap, stated: the intake consumed the list — it had
                // passed every check — and raised the sighting.
                Assert.Equal(new[] { Other }, WanSerialsInMyRadioList(rig));
                var stale = sightings.Of(Other);
                Assert.True(stale.WanAvailable);
                Assert.False(stale.StillCurrent(),
                    "A sighting raised from a list whose connection was redialed after the intake's final check still claimed to be current at the consumer's decision (#619, Sol's review of L7).");
                Assert.Contains("connection 1", stale.Origin, StringComparison.Ordinal);

                // Control: connection 2's own list raises a sighting that is
                // current, so the picker takes it.
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Third) });
                Assert.True(sightings.Of(Third).StillCurrent());

                // LAN sightings carry no WAN list and are unaffected.
                Assert.True(new FlexBase.RigData().StillCurrent());
                Assert.Equal("local discovery", new FlexBase.RigData().Origin);
            }
            finally
            {
                release.Set();
                owner.Dispose();
            }
        }

        /// <summary>
        /// The same ordering for a transport death with NO redial: the
        /// monitor is held before it dials, so connection 1 stays the newest
        /// and only its transport's own edge says it is gone. The parked
        /// sighting answers no on that clause alone; once the monitor is
        /// released and connection 2 is up, its list's sighting answers yes.
        /// </summary>
        [Fact]
        public void A_sighting_raised_after_the_intakes_final_check_knows_when_its_transport_has_died()
        {
            var (owner, wan) = NewSession();
            var release = new ManualResetEventSlim();
            var releaseDial = new ManualResetEventSlim();
            using var sightings = new Sightings();
            try
            {
                var rig = NewRig();
                rig.EngageSmartLinkPresence();
                ConnectAndList(owner, wan, Listed);

                var late = ParkPush(rig, wan, 1, Other, FlexBase.WanIntakeStage.Revalidated, release);

                var heldBeforeDial = new ManualResetEventSlim();
                wan.BeforeDialHook = () =>
                {
                    wan.BeforeDialHook = null;
                    heldBeforeDial.Set();
                    releaseDial.Wait(30000);
                };
                wan.ForceIsConnected(false);
                Assert.True(heldBeforeDial.Wait(5000), "the monitor never got as far as deciding to dial");
                // The trap, stated: connection 1 is still the newest, nothing
                // has retired it, and only its transport has spoken.
                Assert.Equal(1, wan.ConnectionGeneration);
                Assert.False(owner.ListIsCurrent(1));

                release.Set();
                Assert.True(late.Wait(5000), "the parked push never finished");

                Assert.Equal(new[] { Other }, WanSerialsInMyRadioList(rig));
                Assert.False(sightings.Of(Other).StillCurrent(),
                    "A sighting raised from a list whose transport died after the intake's final check still claimed to be current at the consumer's decision (#619, Sol's review of L7).");

                // Control: let the monitor dial connection 2; its list's
                // sighting is current.
                releaseDial.Set();
                WaitUntil(() => wan.ConnectionGeneration == 2 && owner.IsConnected,
                    "the session never reconnected");
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Third) });
                Assert.True(sightings.Of(Third).StillCurrent());
            }
            finally
            {
                release.Set();
                releaseDial.Set();
                owner.Dispose();
            }
        }

        /// <summary>
        /// Sol's follow-up on L7: the connect flow read <c>radios</c> bare
        /// after its wait, while the intake writes <c>radios</c> before the
        /// re-validation that may put it back. A push is parked exactly
        /// there — latch and <c>radios</c> written, not yet re-validated —
        /// and the flow's read is started. It must not complete while the
        /// intake holds the list; the transport then dies, so the intake
        /// puts the list back; and the read must return the list the intake
        /// SETTLED, not the one it took back.
        /// </summary>
        /// <remarks>
        /// Reading the field without the intake's lock returns the
        /// transient list at once, which turns both the "not yet" and the
        /// settled-value assertions red. The release with nothing changed
        /// is the control that the read does return a list the intake kept.
        /// </remarks>
        [Fact]
        public void The_connect_flow_reads_the_list_the_intake_settled_never_one_it_took_back()
        {
            var (owner, wan) = NewSession();
            var release = new ManualResetEventSlim();
            var releaseDial = new ManualResetEventSlim();
            try
            {
                var rig = NewRig();
                rig.EngageSmartLinkPresence();
                ConnectAndList(owner, wan, Listed);
                Assert.Equal(new[] { Listed }, RadiosOf(rig));
                BeginACall(rig);

                var late = ParkPush(rig, wan, 1, Other, FlexBase.WanIntakeStage.LatchWritten, release);

                // The trap, stated: the field holds the list the intake has
                // not yet decided to keep.
                Assert.Equal(new[] { Other }, RadiosOf(rig));
                var read = Task.Run(() => rig.ConnectListSettled());
                Assert.False(read.Wait(300),
                    "The connect flow's read returned while the intake still held the list, so it read a list the intake could take back (#619, Sol's review of L7).");

                wan.BeforeDialHook = () => { wan.BeforeDialHook = null; releaseDial.Wait(30000); };
                wan.ForceIsConnected(false);
                WaitUntil(() => !owner.ListIsCurrent(1), "the transport's death never reached the session");

                release.Set();
                Assert.True(late.Wait(5000), "the parked push never finished");
                Assert.True(read.Wait(5000), "the connect flow's read never completed once the intake finished");
                Assert.Equal(new[] { Listed }, read.Result.Select(r => r.Serial).ToList());
                Assert.Equal(new[] { Listed }, RadiosOf(rig));

                // Control: with nothing changed, a settled list is read.
                releaseDial.Set();
                WaitUntil(() => wan.ConnectionGeneration == 2 && owner.IsConnected,
                    "the session never reconnected");
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Third) });
                Assert.Equal(new[] { Third }, rig.ConnectListSettled().Select(r => r.Serial).ToList());
            }
            finally
            {
                release.Set();
                releaseDial.Set();
                owner.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // Track L9: Noel's two picker rulings of 2026-09-30 (#619)
        // ------------------------------------------------------------------

        /// <summary>Every sighting raised, in order — the replay raises a
        /// serial the intake has already raised, so the one-per-serial
        /// collector above cannot hold these.</summary>
        private sealed class AllSightings : IDisposable
        {
            private readonly List<FlexBase.RigData> _seen = new();
            private readonly FlexBase.RadioFoundDel _onFound;

            public AllSightings()
            {
                _onFound = (_, r) => { lock (_seen) _seen.Add(r); };
                FlexBase.RadioFound += _onFound;
            }

            public int Count(string serial)
            {
                lock (_seen) return _seen.Count(r => r.Serial == serial);
            }

            public FlexBase.RigData Last(string serial)
            {
                lock (_seen)
                {
                    var last = _seen.LastOrDefault(r => r.Serial == serial);
                    Assert.True(last != null, $"no RadioFound was raised for {serial}");
                    return last!;
                }
            }

            public void Dispose() => FlexBase.RadioFound -= _onFound;
        }

        /// <summary>
        /// Ruling one. Sol's review of L8: the picker asks for a replay of
        /// every held row when it opens, the replay raised each row with no
        /// list behind it, a sighting with no list answers "current", and so
        /// a SmartLink row kept across an ordinary drop opened as ONLINE —
        /// setting "a live radio has been seen", closing the connecting
        /// window, eligible for an arrival. Noel ruled: the row is shown as
        /// LAST SEEN, not online, and stays selectable, because connecting is
        /// itself the check. So the replayed row carries the list it came
        /// from, answers "not current" after the drop, and the picker's
        /// decision takes it as last seen: not live, no arrival, the
        /// SmartLink leg still there to try.
        /// </summary>
        /// <remarks>
        /// The replay before the drop is the positive control: the same
        /// replay of the same row, from a list that IS current, is live and
        /// arrives — so "last seen" below is the drop speaking, not the
        /// replay. Replaying bare, as before L9, turns the stale sighting's
        /// currency assertion red; a decision that takes a stale sighting as
        /// live turns the treatment assertions red.
        /// </remarks>
        [Fact]
        public void After_a_drop_a_replayed_SmartLink_row_is_last_seen_never_live_and_stays_selectable()
        {
            var (owner, wan) = NewSession();
            using var sightings = new AllSightings();
            try
            {
                var rig = NewRig();
                rig.EngageSmartLinkPresence();
                ConnectAndList(owner, wan, Listed);

                // Control: nothing has changed, so the replayed row is news
                // about now.
                rig.ReplayDiscoveredRadios();
                var current = sightings.Last(Listed);
                Assert.True(current.WanAvailable);
                Assert.True(current.StillCurrent(),
                    "the control replay, from a list that is still current, answered no");
                var live = PickerSighting.Decide(current, current.LanAvailable, current.WanAvailable, null, null);
                Assert.Equal(PickerSightingTreatment.Live, live.Treatment);
                Assert.True(live.Arrived);
                Assert.True(live.Paths.IsLive);

                // The drop the ruling is about: the transport dies and the
                // session redials, and the new connection has sent no list.
                DropAndRedial(owner, wan);
                Assert.False(owner.ListIsCurrent(1));

                int before = sightings.Count(Listed);
                rig.ReplayDiscoveredRadios();
                Assert.Equal(before + 1, sightings.Count(Listed));
                var held = sightings.Last(Listed);

                Assert.True(held.WanAvailable,
                    "the replayed row lost its SmartLink leg; a last-seen radio must stay selectable");
                Assert.False(held.StillCurrent(),
                    "A SmartLink row replayed after a drop still claimed its list was current, so the picker would open it as online (#619, Sol's review of L8).");
                Assert.Contains("connection 1", held.Origin, StringComparison.Ordinal);

                // A picker opening now has no row for it yet...
                var fresh = PickerSighting.Decide(held, held.LanAvailable, held.WanAvailable, null, null);
                AssertLastSeen(fresh);

                // ...or a roster row painted from history, not live.
                var roster = PickerSighting.Decide(held, held.LanAvailable, held.WanAvailable,
                    new PickerRowPaths(false, false, false), null);
                AssertLastSeen(roster);

                // A local-network sighting is not SmartLink's word: it makes
                // the row live by its own path and leaves the SmartLink half
                // last seen.
                var lan = new FlexBase.RigData { Serial = Listed };
                var lanTaken = PickerSighting.Decide(lan, true, true, fresh.Paths, held);
                Assert.Equal(PickerSightingTreatment.Live, lanTaken.Treatment);
                Assert.True(lanTaken.Paths.IsLive);
                Assert.True(lanTaken.Paths.WanUnconfirmed,
                    "a local-network sighting confirmed a SmartLink half no current list had carried");

                // The new connection's own list confirms it: live again, and
                // that IS an arrival.
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Listed) });
                var confirmed = sightings.Last(Listed);
                Assert.True(confirmed.StillCurrent());
                var back = PickerSighting.Decide(confirmed, confirmed.LanAvailable, confirmed.WanAvailable,
                    fresh.Paths, held);
                Assert.Equal(PickerSightingTreatment.Live, back.Treatment);
                Assert.True(back.Arrived);
                Assert.False(back.Paths.WanUnconfirmed);

                // And an older sighting landing after it — a replay reads its
                // list, releases the lock and raises later — does not speak
                // over the row a current list vouches for.
                var late = PickerSighting.Decide(held, held.LanAvailable, held.WanAvailable,
                    back.Paths, confirmed);
                Assert.Equal(PickerSightingTreatment.Ignored, late.Treatment);
                Assert.Equal(back.Paths, late.Paths);
                Assert.False(late.Arrived);

                // Replayed now, the row carries connection 2's list.
                rig.ReplayDiscoveredRadios();
                Assert.True(sightings.Last(Listed).StillCurrent());

                // A held SmartLink row with no list recorded for it has
                // nothing vouching for it, and is historical outright.
                var rowFrom = typeof(FlexBase).GetField("_wanRowFrom", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.True(rowFrom != null, "FlexBase._wanRowFrom is not where this test reads it.");
                ((System.Collections.IDictionary)rowFrom!.GetValue(rig)!).Clear();
                rig.ReplayDiscoveredRadios();
                var unvouched = sightings.Last(Listed);
                Assert.False(unvouched.StillCurrent(),
                    "a held SmartLink row with no list recorded for it answered current");
                AssertLastSeen(PickerSighting.Decide(unvouched, unvouched.LanAvailable, unvouched.WanAvailable, null, null));
            }
            finally
            {
                owner.Dispose();
            }

            static void AssertLastSeen(PickerSightingOutcome outcome)
            {
                Assert.Equal(PickerSightingTreatment.LastSeen, outcome.Treatment);
                Assert.False(outcome.TakenAsLive,
                    "a sighting from a list that is no longer current was taken as live: it would set the picker's live-radio flag and close the connecting window (#619, Noel 2026-09-30)");
                Assert.False(outcome.Arrived, "a last-seen row was announced as arriving");
                Assert.False(outcome.Paths.IsLive, "a last-seen row read as online");
                Assert.True(outcome.Paths.HasPathToTry, "a last-seen row was not selectable");
                Assert.True(outcome.Paths.Wan);
                Assert.True(outcome.Paths.WanUnconfirmed);
            }
        }

        /// <summary>
        /// Ruling two. The picker's row decision takes a sighting as live on
        /// the discovery thread, and the arrival is spoken a dispatch later on
        /// the UI thread; the list can stop being current in between. Noel
        /// ruled: ask again at the last instant, immediately before speaking,
        /// and say nothing if it is no longer current. Here the row decision
        /// takes a current sighting as an arrival, the transport dies and the
        /// session redials in the dispatch gap, and the last-instant question
        /// answers no — so no arrival is announced.
        /// </summary>
        /// <remarks>
        /// The same question asked before the drop is the positive control,
        /// and a local-network sighting always answers yes. A last-instant
        /// check that does not ask the sighting turns the refusal red.
        /// </remarks>
        [Fact]
        public void A_list_that_stops_being_current_before_the_arrival_is_spoken_produces_no_announcement()
        {
            var (owner, wan) = NewSession();
            using var sightings = new AllSightings();
            try
            {
                var rig = NewRig();
                rig.EngageSmartLinkPresence();
                ConnectAndList(owner, wan, Listed);
                var sighting = sightings.Last(Listed);

                // The row decision, on the discovery thread: live, an arrival.
                var decided = PickerSighting.Decide(sighting, sighting.LanAvailable, sighting.WanAvailable, null, null);
                Assert.True(decided.TakenAsLive);
                Assert.True(decided.Arrived);

                // Control: with nothing changed, the last instant says speak.
                Assert.True(PickerSighting.MayAnnounceArrival(sighting));

                // The dispatch gap: the list stops being current.
                DropAndRedial(owner, wan);

                Assert.False(PickerSighting.MayAnnounceArrival(sighting),
                    "An arrival would have been spoken for a radio whose list stopped being current between the row decision and the announcement (#619, Noel 2026-09-30).");

                // A sighting with no SmartLink list behind it always speaks.
                Assert.True(PickerSighting.MayAnnounceArrival(new FlexBase.RigData { Serial = Other }));
                Assert.True(PickerSighting.MayAnnounceArrival(null!));
            }
            finally
            {
                owner.Dispose();
            }
        }

        /// <summary>
        /// The two decisions are only as good as the handler that asks them,
        /// and that handler is a WPF window no unit test constructs. So its
        /// source is read: the row decision goes through
        /// <see cref="PickerSighting.Decide"/>, the live treatment — the
        /// live-radio flag, the roster record, the connecting window — sits
        /// behind its answer, and the last-instant question is asked inside
        /// the dispatch, as the statement immediately before the arrival.
        /// </summary>
        /// <remarks>
        /// Deleting the last-instant check, or moving it out of the dispatch
        /// to before it, turns this red; so does setting the live-radio flag
        /// ahead of the decision.
        /// </remarks>
        [Fact]
        public void The_picker_handler_asks_both_decisions_where_the_ruling_puts_them()
        {
            string source = ReadRepoFile("JJFlexWpf/Dialogs/RigSelectorDialog.xaml.cs");
            int start = source.IndexOf("private void OnRadioFound(RadioListItem radio)", StringComparison.Ordinal);
            Assert.True(start >= 0, "RigSelectorDialog.OnRadioFound is not where this test reads it.");
            int end = source.IndexOf("private readonly HashSet<string> _arrivalsAnnounced", start, StringComparison.Ordinal);
            Assert.True(end > start, "The end of OnRadioFound is not where this test reads it.");
            string handler = source.Substring(start, end - start);

            int decide = handler.IndexOf("Radios.PickerSighting.Decide(", StringComparison.Ordinal);
            int liveBranch = handler.IndexOf("if (outcome.TakenAsLive)", StringComparison.Ordinal);
            int liveFlag = handler.IndexOf("_anyLiveRadioSeen = true;", StringComparison.Ordinal);
            int notLiveReturn = handler.IndexOf("if (!outcome.TakenAsLive)", StringComparison.Ordinal);
            int record = handler.IndexOf("RecordSightingOnce(radio);", StringComparison.Ordinal);
            int dispatch = handler.LastIndexOf("Dispatcher.Invoke(() =>", StringComparison.Ordinal);
            int close = handler.IndexOf("_closeConnecting();", StringComparison.Ordinal);
            int recheck = handler.IndexOf("Radios.PickerSighting.MayAnnounceArrival(radio.RigData)", StringComparison.Ordinal);
            int announce = handler.IndexOf("AnnounceArrival(radio, arrived);", StringComparison.Ordinal);

            Assert.True(decide >= 0, "OnRadioFound no longer asks PickerSighting.Decide.");
            Assert.True(liveBranch > decide && liveFlag > liveBranch && liveFlag < notLiveReturn,
                "The live-radio flag is set outside the branch the decision's live answer guards (#619).");
            Assert.Equal(1, Count(handler, "_anyLiveRadioSeen = true;"));
            Assert.True(record > notLiveReturn,
                "The roster record is made before the not-live sighting has returned (#619).");
            Assert.True(dispatch > record && close > dispatch,
                "The connecting window's close is not behind the live-only dispatch (#619).");
            Assert.True(recheck > dispatch,
                "The last-instant currency check is not inside the dispatch that speaks the arrival (#619, Noel 2026-09-30).");
            Assert.True(announce > recheck, "The arrival is spoken before the last-instant check.");
            Assert.Equal(1, Count(handler, "AnnounceArrival(radio, arrived);"));

            // Nothing but the refusal stands between the check and the
            // announcement: the check is the statement immediately before it.
            string between = handler.Substring(recheck, announce - recheck);
            Assert.Equal(1, Count(between, "return;"));
            Assert.DoesNotContain("Refresh", between, StringComparison.Ordinal);
            Assert.DoesNotContain("Focus", between, StringComparison.Ordinal);

            static int Count(string text, string what)
            {
                int n = 0, at = 0;
                while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
                return n;
            }
        }

        // ------------------------------------------------------------------
        // Track L11: a picker already open when SmartLink drops (#619)
        // ------------------------------------------------------------------

        /// <summary>
        /// Sol's review of L9. <see cref="PickerSighting.Decide"/> runs only
        /// when a sighting arrives, and a drop raises none, so a picker
        /// already open kept the SmartLink row it had taken as live reading
        /// online — IsLive, the occupancy clause, auto-connect eligible —
        /// until another list happened to come. Noel's ruling of 2026-09-30:
        /// last-list radios are shown as last seen, never online, and stay
        /// selectable. The session now signals when its lists may have
        /// stopped being current, and the picker re-asks every row through
        /// <see cref="PickerSighting.Reassess"/>, which asks the same
        /// question of the same sighting.
        /// </summary>
        /// <remarks>
        /// The picker's row is held here exactly as the dialog holds it — the
        /// paths <see cref="PickerSighting.Decide"/> returned and the sighting
        /// <see cref="PickerSighting.WanHalfSighting"/> recorded — and the
        /// signal is taken from the process coordinator, the instance the
        /// dialog subscribes to. The monitor is held before it redials, so
        /// the transport's own edge is the only thing that has spoken. The
        /// reassessment before the drop is the control: a current list is
        /// not withdrawn. Dropping the edge's signal, raising it before the
        /// death is recorded, a reassessment that withdraws nothing, an
        /// auto-connect decision that accepts a path to try, and a LAN
        /// sighting replacing the list's sighting each turn this red.
        /// </remarks>
        [Fact]
        public void A_picker_already_open_when_SmartLink_drops_shows_its_live_row_as_last_seen_and_auto_connect_refuses_it()
        {
            var (owner, wan) = NewSession();
            var coordinator = SmartLinkServices.Coordinator;
            var releaseDial = new ManualResetEventSlim();
            using var sightings = new AllSightings();

            // The open picker's row for the radio, as the dialog holds it.
            PickerRowPaths row = default;
            object? wanHalf = null;
            var atSignal = new List<(bool ListStillCurrent, PickerSightingOutcome Reassessed)>();
            EventHandler<IWanSessionOwner> onSignal = (_, _) =>
            {
                var reassessed = PickerSighting.Reassess(row, wanHalf!);
                lock (atSignal) atSignal.Add((owner.ListIsCurrent(1), reassessed));
            };
            coordinator.SessionListCurrencyMayHaveChanged += onSignal;
            try
            {
                var rig = NewRig();
                rig.EngageSmartLinkPresence();
                ConnectAndList(owner, wan, Listed);

                var sighting = sightings.Last(Listed);
                var taken = PickerSighting.Decide(sighting, sighting.LanAvailable, sighting.WanAvailable, null, null);
                Assert.Equal(PickerSightingTreatment.Live, taken.Treatment);
                row = taken.Paths;
                wanHalf = PickerSighting.WanHalfSighting(sighting, null!);
                Assert.Same(sighting, wanHalf);

                // Control: nothing has changed, so the row stays live and the
                // timer may choose it.
                Assert.True(row.IsLive);
                Assert.True(PickerSighting.AutoConnectMayChoose(row));
                Assert.Equal(PickerSightingTreatment.Ignored, PickerSighting.Reassess(row, wanHalf).Treatment);

                // A LAN broadcast of a dual-homed radio refreshes the row's
                // rig data every second; it says nothing about SmartLink, so
                // the list's sighting stays the one the row re-asks.
                var lan = new FlexBase.RigData { Serial = Listed };
                Assert.Same(sighting, PickerSighting.WanHalfSighting(lan, wanHalf));

                int before;
                lock (atSignal) before = atSignal.Count;

                // The drop, with the redial held: only the transport's own
                // edge has spoken.
                var heldBeforeDial = new ManualResetEventSlim();
                wan.BeforeDialHook = () =>
                {
                    wan.BeforeDialHook = null;
                    heldBeforeDial.Set();
                    releaseDial.Wait(30000);
                };
                wan.ForceIsConnected(false);
                Assert.True(heldBeforeDial.Wait(5000), "the monitor never got as far as deciding to dial");
                Assert.Equal(1, wan.ConnectionGeneration);

                List<(bool ListStillCurrent, PickerSightingOutcome Reassessed)> fromTheDrop;
                lock (atSignal) fromTheDrop = atSignal.Skip(before).ToList();
                Assert.True(fromTheDrop.Count > 0,
                    "A SmartLink drop told the open picker nothing, so the row it had taken as live went on reading online (#619, Sol's review of L9).");
                Assert.False(fromTheDrop[0].ListStillCurrent,
                    "The drop was signalled before it was recorded, so a picker re-asking at once was told the dead list was current.");
                Assert.Equal(PickerSightingTreatment.LastSeen, fromTheDrop[0].Reassessed.Treatment);

                var lastSeen = PickerSighting.Reassess(row, wanHalf);
                Assert.Equal(PickerSightingTreatment.LastSeen, lastSeen.Treatment);
                Assert.False(lastSeen.Arrived);
                Assert.False(lastSeen.Paths.IsLive,
                    "After a SmartLink drop the open picker's row still read online (#619, Noel 2026-09-30).");
                Assert.False(PickerSighting.AutoConnectMayChoose(lastSeen.Paths),
                    "The auto-connect timer could still choose a row whose only evidence is a list that is no longer current (#619, Sol's review of L9).");
                Assert.True(lastSeen.Paths.HasPathToTry, "a last-seen row was not selectable");
                Assert.True(lastSeen.Paths.Wan);
                Assert.True(lastSeen.Paths.WanUnconfirmed);

                // A dual-homed row keeps its local half and loses only the
                // SmartLink one; a row with no SmartLink half, one already
                // last seen, and one no list ever spoke for are left alone.
                var dual = PickerSighting.Reassess(new PickerRowPaths(true, true, false), wanHalf);
                Assert.Equal(PickerSightingTreatment.LastSeen, dual.Treatment);
                Assert.Equal(new PickerRowPaths(true, true, true), dual.Paths);
                Assert.True(dual.Paths.IsLive, "the local half was withdrawn by a SmartLink drop");
                Assert.Equal(PickerSightingTreatment.Ignored,
                    PickerSighting.Reassess(new PickerRowPaths(true, false, false), wanHalf).Treatment);
                Assert.Equal(PickerSightingTreatment.Ignored, PickerSighting.Reassess(lastSeen.Paths, wanHalf).Treatment);
                Assert.Equal(PickerSightingTreatment.Ignored, PickerSighting.Reassess(row, null!).Treatment);
                Assert.Equal(PickerSightingTreatment.Ignored, PickerSighting.Reassess(row, lan).Treatment);

                // The new connection's list brings it back, through the
                // ordinary sighting decision, and the timer may choose it.
                releaseDial.Set();
                WaitUntil(() => wan.ConnectionGeneration == 2 && owner.IsConnected, "the session never reconnected");
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Listed) });
                var confirmed = sightings.Last(Listed);
                var back = PickerSighting.Decide(confirmed, confirmed.LanAvailable, confirmed.WanAvailable,
                    lastSeen.Paths, wanHalf);
                Assert.Equal(PickerSightingTreatment.Live, back.Treatment);
                Assert.True(PickerSighting.AutoConnectMayChoose(back.Paths));
                Assert.Equal(PickerSightingTreatment.Ignored,
                    PickerSighting.Reassess(back.Paths, PickerSighting.WanHalfSighting(confirmed, wanHalf)).Treatment);
            }
            finally
            {
                coordinator.SessionListCurrencyMayHaveChanged -= onSignal;
                releaseDial.Set();
                owner.Dispose();
            }
        }

        /// <summary>
        /// Every change that can take a list out of currency is signalled,
        /// after it is recorded, on the thread that made it: the transport's
        /// death, a dial, the operator's Disconnect and Dispose. A transport
        /// coming up is not, because nothing becomes stale by it.
        /// </summary>
        /// <remarks>
        /// Each is pinned where only it can have spoken: the dial between the
        /// mock's two dial hooks, the death on the thread that reported it
        /// with the redial held, Disconnect and Dispose on the calling thread
        /// before the monitor has acted. Removing any one of the four signals
        /// turns exactly its own assertion red; signalling the transport's
        /// up edge turns the "not signalled" assertion red.
        /// </remarks>
        [Fact]
        public void Every_change_that_can_take_a_list_out_of_currency_is_signalled_once_it_is_recorded()
        {
            var (owner, wan) = NewSession();
            var coordinator = SmartLinkServices.Coordinator;
            var releaseDial = new ManualResetEventSlim();
            int testThread = Environment.CurrentManagedThreadId;
            var signals = new List<(int Thread, bool FirstCurrent, bool SecondCurrent, IWanSessionOwner From)>();
            EventHandler<IWanSessionOwner> onSignal = (_, from) =>
            {
                var entry = (Environment.CurrentManagedThreadId, owner.ListIsCurrent(1), owner.ListIsCurrent(2), from);
                lock (signals) signals.Add(entry);
            };
            coordinator.SessionListCurrencyMayHaveChanged += onSignal;
            int Count() { lock (signals) return signals.Count; }
            List<(int Thread, bool FirstCurrent, bool SecondCurrent, IWanSessionOwner From)> Since(int n)
            {
                lock (signals) return signals.Skip(n).ToList();
            }

            try
            {
                // Connection 1 dials: signalled. Its transport comes up: not.
                int atBeforeDial = -1, atDial = -1;
                wan.BeforeDialHook = () => { wan.BeforeDialHook = null; atBeforeDial = Count(); };
                wan.DialHook = () => { wan.DialHook = null; atDial = Count(); };
                ConnectAndList(owner, wan, Listed);
                Assert.True(atBeforeDial >= 0 && atDial >= 0, "the mock never dialed");
                Assert.Equal(atBeforeDial + 1, atDial);
                Assert.Equal(atDial, Count());
                Assert.All(Since(0), s => Assert.Same(owner, s.From));
                Assert.True(owner.ListIsCurrent(1));

                // The transport dies: signalled on the thread that reported
                // it, already recorded. The redial is held so nothing else
                // can have spoken.
                var heldBeforeDial = new ManualResetEventSlim();
                wan.BeforeDialHook = () =>
                {
                    wan.BeforeDialHook = null;
                    atBeforeDial = Count();
                    heldBeforeDial.Set();
                    releaseDial.Wait(30000);
                };
                wan.DialHook = () => { wan.DialHook = null; atDial = Count(); };
                int beforeDeath = Count();
                wan.ForceIsConnected(false);
                var death = Since(beforeDeath);
                Assert.True(death.Count >= 1 && death[0].Thread == testThread,
                    "A transport's death was not signalled on the thread that reported it (#619, Sol's review of L9).");
                Assert.False(death[0].FirstCurrent);

                // Connection 2 dials: signalled.
                Assert.True(heldBeforeDial.Wait(5000), "the monitor never got as far as deciding to dial");
                releaseDial.Set();
                WaitUntil(() => wan.ConnectionGeneration == 2 && owner.IsConnected, "the session never reconnected");
                Assert.Equal(atBeforeDial + 1, atDial);
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Other) });
                Assert.True(owner.ListIsCurrent(2));

                // The operator disconnects: signalled on the caller's thread,
                // before the monitor tears anything down.
                int beforeDisconnect = Count();
                owner.Disconnect();
                Assert.Contains(Since(beforeDisconnect), s => s.Thread == testThread && !s.SecondCurrent);

                // Dispose: the same.
                int beforeDispose = Count();
                owner.Dispose();
                Assert.Contains(Since(beforeDispose), s => s.Thread == testThread);
            }
            finally
            {
                coordinator.SessionListCurrencyMayHaveChanged -= onSignal;
                releaseDial.Set();
                owner.Dispose();
            }
        }

        /// <summary>
        /// The two tests above hold the decisions; the dialog that acts on
        /// them is a WPF window no unit test constructs, so its source is
        /// read. The picker subscribes to the coordinator's signal before its
        /// opening replay and unsubscribes on Closing; the handler only
        /// queues the re-ask on the dispatcher; the re-ask goes through
        /// <see cref="PickerSighting.Reassess"/> under the list lock and
        /// repaints; the auto-connect timer asks
        /// <see cref="PickerSighting.AutoConnectMayChoose"/>; and every row
        /// write in the sighting handler records which list the SmartLink
        /// half came from.
        /// </summary>
        /// <remarks>
        /// Deleting the subscription, re-asking on the SmartLink thread,
        /// letting the timer read IsLive past the seam, or a row write that
        /// forgets its list each turn this red.
        /// </remarks>
        [Fact]
        public void The_open_picker_re_asks_its_rows_when_a_session_says_its_lists_may_be_history()
        {
            string source = ReadRepoFile("JJFlexWpf/Dialogs/RigSelectorDialog.xaml.cs");

            int subscribe = source.IndexOf("_listCurrencySource.SessionListCurrencyMayHaveChanged += OnWanListCurrencyMayHaveChanged;", StringComparison.Ordinal);
            int replay = source.IndexOf("_callbacks.ReplayDiscoveredRadios?.Invoke();", StringComparison.Ordinal);
            Assert.True(subscribe >= 0, "The picker no longer subscribes to the sessions' list-currency signal (#619, Sol's review of L9).");
            Assert.True(replay > subscribe, "The picker subscribes after its opening replay, so a drop during the opening pass is missed.");
            Assert.Contains("_listCurrencySource = Radios.SmartLink.SmartLinkServices.Coordinator;", source, StringComparison.Ordinal);

            string closing = Body(source, "private void RigSelectorDialog_Closing(");
            Assert.Contains("_listCurrencySource.SessionListCurrencyMayHaveChanged -= OnWanListCurrencyMayHaveChanged;", closing, StringComparison.Ordinal);

            string handler = Body(source, "private void OnWanListCurrencyMayHaveChanged(");
            Assert.Contains("Dispatcher.BeginInvoke(new Action(ReassessWanRows))", handler, StringComparison.Ordinal);
            Assert.DoesNotContain("lock (", handler, StringComparison.Ordinal);
            Assert.DoesNotContain("Dispatcher.Invoke(", handler, StringComparison.Ordinal);

            string reassess = Body(source, "private void ReassessWanRows()");
            int lockAt = reassess.IndexOf("lock (_radiosLock)", StringComparison.Ordinal);
            int ask = reassess.IndexOf("Radios.PickerSighting.Reassess(row.Paths, row.WanSighting)", StringComparison.Ordinal);
            int write = reassess.IndexOf("row.WanUnconfirmed = outcome.Paths.WanUnconfirmed;", StringComparison.Ordinal);
            int flag = reassess.IndexOf("_anyLiveRadioSeen = _radiosList.Any(r => r.IsLive);", StringComparison.Ordinal);
            int repaint = reassess.IndexOf("RefreshRadiosList();", StringComparison.Ordinal);
            Assert.True(lockAt >= 0 && ask > lockAt && write > ask && flag > write,
                "The re-ask does not go through PickerSighting.Reassess under the list lock (#619).");
            Assert.True(repaint > flag, "The rows are withdrawn but the list is not repainted.");

            string tick = Body(source, "private void AutoConnectTimer_Tick(");
            Assert.Contains("Radios.PickerSighting.AutoConnectMayChoose(r.Paths)", tick, StringComparison.Ordinal);
            Assert.DoesNotContain("r.IsLive", tick, StringComparison.Ordinal);

            string found = Body(source, "private void OnRadioFound(RadioListItem radio)");
            Assert.Contains("row?.Paths, row?.WanSighting ?? row?.RigData);", found, StringComparison.Ordinal);
            Assert.Equal(4, Count(found, "WanSighting = Radios.PickerSighting.WanHalfSighting(radio.RigData,"));

            static string Body(string text, string signature)
            {
                int start = text.IndexOf(signature, StringComparison.Ordinal);
                Assert.True(start >= 0, signature + " is not where this test reads it.");
                int open = text.IndexOf('{', start);
                int depth = 0;
                for (int i = open; i < text.Length; i++)
                {
                    if (text[i] == '{') depth++;
                    else if (text[i] == '}' && --depth == 0) return text.Substring(start, i - start + 1);
                }
                Assert.Fail("The body of " + signature + " never closes.");
                return "";
            }

            static int Count(string text, string what)
            {
                int n = 0, at = 0;
                while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
                return n;
            }
        }

        private static string ReadRepoFile(string relative)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                dir = dir.Parent;
            Assert.True(dir != null, "Could not find the repository root above " + AppContext.BaseDirectory);
            string path = System.IO.Path.Combine(dir!.FullName, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Assert.True(System.IO.File.Exists(path),
                "Could not find " + relative + ". A test that cannot find its subject proves nothing about it.");
            return System.IO.File.ReadAllText(path);
        }
    }
}
