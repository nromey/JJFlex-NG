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
    }
}
