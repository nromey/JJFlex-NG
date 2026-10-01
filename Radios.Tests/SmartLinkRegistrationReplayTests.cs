#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.SmartLink;
using Xunit;

using Verdict = Radios.FlexBase.SmartLinkRegistrationQuery;

namespace Radios.Tests
{
    /// <summary>
    /// Task #619: the registration query's absence verdict, driven through the
    /// REAL connect flow rather than handed to the judge.
    /// </summary>
    /// <remarks>
    /// <para><b>Why these exist.</b> Track L's tests called
    /// <c>SmartLinkRegistrationEvidence.Judge</c> directly with
    /// "a server answered" set by hand. Sol's review found the defect was in
    /// how that answer was BUILT: <c>ConnectToSmartLink</c> replays a held
    /// session's cached list through the same handler a live push uses, the
    /// handler set the latch the query read as "the server spoke", and the
    /// negative verdict could then be produced from held state. A test that
    /// constructs the flag by hand cannot catch a bug in how the flag is
    /// constructed, so these construct nothing: a mock server behind a real
    /// session owner behind a real coordinator, and a real
    /// <see cref="FlexBase"/> running its own connect flow.</para>
    ///
    /// <para><b>Nothing here reaches a network, a radio or a settings
    /// tree.</b> The account key is the connect flow's own fallback,
    /// <c>default-account</c>, which has no '@', so the intake's fast-paint
    /// cache write is skipped.</para>
    /// </remarks>
    [Collection(SmartLinkSingletonCollection.Name)]
    public sealed class SmartLinkRegistrationReplayTests : IDisposable
    {
        private const string Account = "default-account";
        private const string Asked = "4925-1213-8600-6245";
        private const string SomeoneElse = "1111-2222-6400-3333";

        private readonly SmartLinkSessionCoordinator _original;
        private readonly Func<IReadOnlyList<SmartLinkAccount>>? _originalAccountsHook;
        private readonly Func<SmartLinkAccount, bool, string?>? _originalJwtHook;
        private readonly List<FlexBase> _rigs = new();
        private readonly List<SmartLinkSessionCoordinator> _coordinators = new();

        public SmartLinkRegistrationReplayTests()
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

            // The replay banks its radios in FlexBase's static WAN map, exactly
            // as production does. Put the map back so no later test inherits a
            // handle for a radio that never existed.
            ForgetWanRadiosForAccount?.Invoke(null, new object[] { Account, "test teardown" });
        }

        // ------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------

        private (IWanSessionOwner session, MockWanServer wan) NewSession()
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

            var session = coordinator.GetOrCreateSession(Account);
            Assert.NotNull(wan);
            return (session, wan!);
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

        /// <summary>
        /// A WAN <see cref="Radio"/> as the server's list would carry it. The
        /// vendor's constructors and the IsWan setter are internal, so this
        /// reaches them by reflection rather than widening FlexLib.
        /// </summary>
        internal static Radio WanRadio(string serial)
        {
            var ctor = typeof(Radio).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(string), typeof(string), typeof(IPAddress), typeof(string) },
                null);
            Assert.True(ctor != null,
                "FlexLib's Radio(model, serial, name, ip, version) constructor is not where this test " +
                "looks for it, so no list below would carry a radio and the replay would never run.");
            var radio = (Radio)ctor!.Invoke(new object[] { "FLEX-6400", serial, "test", IPAddress.Loopback, "4.2.20.0" });

            var setIsWan = typeof(Radio).GetProperty(nameof(Radio.IsWan))!.GetSetMethod(nonPublic: true);
            Assert.True(setIsWan != null, "Radio.IsWan's setter is not where this test looks for it.");
            setIsWan!.Invoke(radio, new object[] { true });
            return radio;
        }

        private static readonly FieldInfo LatchField =
            typeof(FlexBase).GetField("wanListReceived", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static readonly MethodInfo? ForgetWanRadiosForAccount =
            typeof(FlexBase).GetMethod("ForgetWanRadiosForAccount", BindingFlags.NonPublic | BindingFlags.Static);

        private static bool Latched(FlexBase rig)
        {
            Assert.True(LatchField != null,
                "FlexBase.wanListReceived is not where this test reads it, so the assertion that the " +
                "replay ran would mean nothing.");
            return (bool)LatchField!.GetValue(rig)!;
        }

        // The other things the intake writes when it is given a list — each
        // one an effect Sol's review of Track L3 listed the replay as having,
        // read back by reflection because none has a public face.
        private static readonly FieldInfo? RadiosField =
            typeof(FlexBase).GetField("radios", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo? MyRadioListField =
            typeof(FlexBase).GetField("myRadioList", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo? WanBankField =
            typeof(FlexBase).GetField("_wanRadiosBySerial", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>The list the connect flow reads downstream, or null before any list.</summary>
        private static List<Radio>? Radios(FlexBase rig)
        {
            Assert.True(RadiosField != null, "FlexBase.radios is not where this test reads it.");
            return (List<Radio>?)RadiosField!.GetValue(rig);
        }

        /// <summary>The serials of the WAN radios in this instance's discovery list.</summary>
        private static List<string> WanSerialsInMyRadioList(FlexBase rig)
        {
            Assert.True(MyRadioListField != null, "FlexBase.myRadioList is not where this test reads it.");
            var list = (List<Radio>)MyRadioListField!.GetValue(rig)!;
            return list.Where(r => r.IsWan).Select(r => r.Serial).ToList();
        }

        /// <summary>Whether the static WAN object bank holds a handle for this serial.</summary>
        private static bool WanBankHas(string serial)
        {
            Assert.True(WanBankField != null, "FlexBase._wanRadiosBySerial is not where this test reads it.");
            var bank = (System.Collections.IDictionary)WanBankField!.GetValue(null)!;
            return bank.Contains(serial);
        }

        // ------------------------------------------------------------------
        // The path Sol found
        // ------------------------------------------------------------------

        /// <summary>
        /// The whole of #619. A session is already live and holds a list that
        /// does not contain the radio being asked about. The connect flow
        /// replays that held list, the replay satisfies the connect, and no
        /// server says anything during the call. The query must not report an
        /// absence: nothing was observed.
        /// </summary>
        /// <remarks>
        /// The first two assertions are the positive control. They prove the
        /// replay really ran and really set the latch Track L trusted — so the
        /// verdict assertion below is refusing the exact path Sol described,
        /// not passing because the path was never walked.
        /// </remarks>
        [Fact]
        public void A_replayed_held_list_never_produces_an_absence_verdict()
        {
            var (session, wan) = NewSession();
            session.Connect();
            WaitUntil(() => session.IsConnected, "the mock session never reported connected");

            // The server's one list for this TLS session, arriving BEFORE the
            // rig exists — which is why the rig can only ever see it as a
            // replay. It carries a radio, because the connect flow does not
            // replay an empty list.
            wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(SomeoneElse) });
            Assert.NotNull(session.LastRadioListUtc);

            var rig = NewRig();
            var finding = rig.AskSmartLinkAboutSerial(Asked, Account, "test-jwt");

            Assert.True(Latched(rig),
                "The list-received latch is not set, so the replay did not run and this test did not " +
                "walk the path it is named for.");

            Assert.NotEqual(Verdict.NotInAccountList, finding.Verdict);
            Assert.False(finding.FromALiveServerAnswer,
                "A replay of a held list was reported as a live server answer (#619).");
        }

        /// <summary>
        /// The other half of the control: through the same harness, a list the
        /// server really pushes during the call DOES produce the narrow
        /// absence verdict. Without this, "never NotInAccountList" above could
        /// be passing because the harness cannot produce that verdict at all.
        /// </summary>
        [Fact]
        public async Task A_list_the_server_pushes_during_the_call_can_report_an_absence()
        {
            var (_, wan) = NewSession();
            var rig = NewRig();

            // The server answers the registration the connect flow sends, as
            // it does in the field: one list per TLS session, shortly after
            // registration. Explicitly empty, as on 2026-09-23.
            var server = Task.Run(() =>
            {
                WaitUntil(() => wan.SendRegisterCallCount > 0, "the connect flow never registered");
                wan.RaiseWanRadioRadioListReceived(Array.Empty<Radio>());
            });

            var finding = rig.AskSmartLinkAboutSerial(Asked, Account, "test-jwt");
            await server;

            Assert.Equal(Verdict.NotInAccountList, finding.Verdict);
            Assert.True(finding.FromALiveServerAnswer);

            // One account answered, once. The push was captured AND the held
            // session now carries the same list, and Track L counted those as
            // two — which is how the caller's "only the signed-in account was
            // asked" caveat could be suppressed after hearing from one account.
            Assert.Equal(1, finding.AccountsConsulted);
        }

        // ------------------------------------------------------------------
        // The reconnect Sol found in Track L2
        // ------------------------------------------------------------------

        /// <summary>
        /// A held session connects, hears a list carrying the radio, drops, and
        /// the monitor dials a new connection — and the server has not yet
        /// sent that connection anything.
        /// </summary>
        private static void ReconnectWithoutANewList(IWanSessionOwner session, MockWanServer wan)
        {
            session.Connect();
            WaitUntil(() => session.IsConnected, "the mock session never reported connected");
            wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Asked) });

            int dialsBefore = wan.ConnectCallCount;
            wan.ForceIsConnected(false);
            WaitUntil(() => wan.ConnectCallCount > dialsBefore && session.IsConnected,
                "the session never dialled a new connection after the drop");
        }

        /// <summary>
        /// Through the real connect flow: between a reconnect and the new
        /// connection's first list, the old connection's listing must not
        /// answer Registered (#619) — and the flow must not be satisfied by
        /// it either. The server sends the new connection's list a moment
        /// after registration, as it does in the field; the finding comes
        /// from that push, which proves the flow was still waiting for it
        /// rather than having been satisfied on the spot by a replay of the
        /// old list (Sol's review of Track L3).
        /// </summary>
        /// <remarks>
        /// The push is delayed past the point where the flow decides whether
        /// to replay, which is microseconds after it registers. Track L3's
        /// version of this test asserted the latch was set BY THE REPLAY;
        /// the replay no longer runs for an earlier connection's list, so the
        /// latch here is set by the push, and the per-effect assertions live
        /// in the two tests that drive the replay directly.
        /// </remarks>
        [Fact]
        public async Task A_listing_from_before_a_reconnect_does_not_answer_for_the_new_connection()
        {
            var (session, wan) = NewSession();
            ReconnectWithoutANewList(session, wan);

            // The trap: connected, and holding a list that names the radio.
            Assert.NotNull(session.LastRadioListUtc);
            Assert.Contains(session.AvailableRadios, r => r.Serial == Asked);

            var rig = NewRig();
            var server = Task.Run(() =>
            {
                WaitUntil(() => wan.SendRegisterCallCount > 0, "the connect flow never registered");
                Thread.Sleep(500);
                wan.RaiseWanRadioRadioListReceived(Array.Empty<Radio>());
            });

            var finding = rig.AskSmartLinkAboutSerial(Asked, Account, "test-jwt");
            await server;

            Assert.True(Latched(rig), "No list reached the connect flow at all, so this test walked nothing.");
            Assert.True(finding.FromALiveServerAnswer,
                "The connect flow returned before the new connection's list arrived, so something else " +
                "satisfied it — the replay of the earlier connection's list is the only candidate (#619).");
            Assert.NotEqual(Verdict.Registered, finding.Verdict);
            Assert.Equal(string.Empty, finding.ListedUnderAccount);
            Assert.DoesNotContain(Asked, WanSerialsInMyRadioList(rig));
        }

        // ------------------------------------------------------------------
        // The replay's effects, one by one (Sol's review of Track L3)
        // ------------------------------------------------------------------

        /// <summary>
        /// Sol listed what the replay does with a list — sets the connect
        /// flow's latch, assigns <c>radios</c>, adds to <c>myRadioList</c>,
        /// banks the WAN objects, and raises RadioFound for the selector —
        /// and noted the L3 test asserted none of them. An earlier
        /// connection's list is now not replayed at all, so every one of
        /// those is absent. Driven through the replay directly, so the
        /// decision is deterministic and the flow's ten-second list wait is
        /// not paid.
        /// </summary>
        [Fact]
        public void An_earlier_connections_list_is_not_replayed_in_any_of_its_effects()
        {
            var (session, wan) = NewSession();
            ReconnectWithoutANewList(session, wan);
            Assert.Contains(session.AvailableRadios, r => r.Serial == Asked);

            var rig = NewRig();
            var found = new List<string>();
            var removed = new List<string>();
            FlexBase.RadioFoundDel onFound = (_, r) => found.Add(r.Serial);
            FlexBase.RadioRemovedDel onRemoved = (_, serial, __) => removed.Add(serial);
            FlexBase.RadioFound += onFound;
            FlexBase.RadioRemoved += onRemoved;
            try
            {
                int replayed = rig.ReplayHeldListsIntoTheIntake(Account, sessionWasAlreadyConnected: true);

                Assert.Equal(0, replayed);
                Assert.False(Latched(rig), "The earlier connection's list satisfied the connect flow's latch.");
                Assert.Null(Radios(rig));
                Assert.Empty(WanSerialsInMyRadioList(rig));
                Assert.False(WanBankHas(Asked), "The earlier connection's list was banked as a current WAN handle.");
                Assert.Empty(found);
                Assert.Empty(removed);
            }
            finally
            {
                FlexBase.RadioFound -= onFound;
                FlexBase.RadioRemoved -= onRemoved;
            }
        }

        /// <summary>
        /// The positive control for every assertion above: the same session,
        /// once the live connection has sent its own list, IS replayed, and
        /// each effect is present. Without this the test above could pass
        /// because the harness cannot produce any of them.
        /// </summary>
        [Fact]
        public void The_live_connections_list_is_replayed_with_every_effect()
        {
            var (session, wan) = NewSession();
            ReconnectWithoutANewList(session, wan);
            wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Asked) });

            var rig = NewRig();
            var found = new List<string>();
            FlexBase.RadioFoundDel onFound = (_, r) => found.Add(r.Serial);
            FlexBase.RadioFound += onFound;
            try
            {
                int replayed = rig.ReplayHeldListsIntoTheIntake(Account, sessionWasAlreadyConnected: true);

                Assert.Equal(1, replayed);
                Assert.True(Latched(rig));
                Assert.Contains(Radios(rig)!, r => r.Serial == Asked);
                Assert.Contains(Asked, WanSerialsInMyRadioList(rig));
                Assert.True(WanBankHas(Asked));
                Assert.Contains(Asked, found);
            }
            finally
            {
                FlexBase.RadioFound -= onFound;
            }
        }

        /// <summary>
        /// The partner: once the new connection has sent its own list, the
        /// same flow answers Registered, under the account that listed it.
        /// </summary>
        [Fact]
        public void The_new_connections_own_list_answers_through_the_connect_flow()
        {
            var (session, wan) = NewSession();
            ReconnectWithoutANewList(session, wan);
            wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Asked) });

            var rig = NewRig();
            var finding = rig.AskSmartLinkAboutSerial(Asked, Account, "test-jwt");

            Assert.Equal(Verdict.Registered, finding.Verdict);
            Assert.Equal(Account, finding.ListedUnderAccount);
            Assert.Equal(1, finding.AccountsConsulted);
        }

        // ------------------------------------------------------------------
        // A re-entered rig (Sol's review of Track L4)
        // ------------------------------------------------------------------

        /// <summary>
        /// The rig that received the first list, REUSED — every test above
        /// built a fresh one. Its myRadioList keeps the rows it took on
        /// connection 1 across a SmartLink-only drop: nothing removes them,
        /// and FlexLib's RadioRemoved never fires for a WAN-only radio. So
        /// after the reconnect, with no push yet, the connect flow's shortcut
        /// read "session connected, rows in hand" and answered on the spot
        /// from rows that describe the previous connection, while a fresh
        /// rig — whose replay Track L4 made wait — waited for the push. The
        /// decision, driven directly: the rows may answer only once the live
        /// connection has listed the account.
        /// </summary>
        [Fact]
        public void A_re_entered_rigs_own_rows_may_answer_only_once_the_live_connection_has_listed()
        {
            var (session, wan) = NewSession();
            session.Connect();
            WaitUntil(() => session.IsConnected, "the mock session never reported connected");
            wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Asked) });

            var rig = NewRig();
            Assert.Equal(1, rig.ReplayHeldListsIntoTheIntake(Account, sessionWasAlreadyConnected: true));
            Assert.Contains(Asked, WanSerialsInMyRadioList(rig));

            // Positive control: with the live connection's list in hand, the
            // rows may answer. Without this, "may not" below could be passing
            // because the harness can never produce "may".
            Assert.True(rig.OwnRowsMayAnswerTheConnect(session, Account));

            // The drop and the redial, with no push. The trap, stated: the
            // session is connected again and the rig's rows survived.
            int dialsBefore = wan.ConnectCallCount;
            wan.ForceIsConnected(false);
            WaitUntil(() => wan.ConnectCallCount > dialsBefore && session.IsConnected,
                "the session never dialled a new connection after the drop");
            Assert.Contains(Asked, WanSerialsInMyRadioList(rig));

            Assert.False(rig.OwnRowsMayAnswerTheConnect(session, Account),
                "A re-entered rig's rows from before the reconnect were allowed to answer the connect " +
                "as if they were the live connection's list (#619).");

            // The live connection lists the account. Track L5 let the rows
            // answer from here, on the session's list being live; Sol's
            // review of L5 found that says nothing about THIS rig's rows —
            // it was not the intake when the list arrived, so they are still
            // the earlier connection's. The replay is what refreshes them,
            // and only then may they answer (Track L6a).
            wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Asked) });
            Assert.False(rig.OwnRowsMayAnswerTheConnect(session, Account),
                "A rig's rows from before the reconnect answered because the SESSION's list was live, " +
                "though this rig never took that list (#619).");
            Assert.Equal(1, rig.ReplayHeldListsIntoTheIntake(Account, sessionWasAlreadyConnected: true));
            Assert.True(rig.OwnRowsMayAnswerTheConnect(session, Account));
        }

        /// <summary>
        /// The same rig through the real connect flow, twice. First over
        /// connection 1, where it takes the account's rows and answers
        /// Registered; then after a drop and a reconnect the server has not
        /// yet answered, where the old flow answered instantly from those
        /// rows. The server sends the new connection's list half a second
        /// after the re-entered flow registers, and the finding comes from
        /// that push — which proves the flow was waiting for it rather than
        /// satisfied by its own rows.
        /// </summary>
        /// <remarks>
        /// The same timing caveat as the fresh-rig test above: on a machine
        /// stalled longer than half a second between registration and the
        /// shortcut decision, the push would land first and this would pass
        /// for the wrong reason. It cannot fail for the wrong reason, and the
        /// deterministic test above drives the decision itself.
        /// </remarks>
        [Fact]
        public async Task A_re_entered_rig_waits_for_the_new_connections_list_instead_of_answering_from_its_own_rows()
        {
            var (session, wan) = NewSession();
            session.Connect();
            WaitUntil(() => session.IsConnected, "the mock session never reported connected");
            wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Asked) });

            var rig = NewRig();
            var first = rig.AskSmartLinkAboutSerial(Asked, Account, "test-jwt");
            Assert.Equal(Verdict.Registered, first.Verdict);
            Assert.Contains(Asked, WanSerialsInMyRadioList(rig));

            int dialsBefore = wan.ConnectCallCount;
            wan.ForceIsConnected(false);
            WaitUntil(() => wan.ConnectCallCount > dialsBefore && session.IsConnected,
                "the session never dialled a new connection after the drop");
            // The trap: connected again, rows still in hand — the old
            // shortcut's whole condition.
            Assert.Contains(Asked, WanSerialsInMyRadioList(rig));

            int registrationsBefore = wan.SendRegisterCallCount;
            var server = Task.Run(() =>
            {
                WaitUntil(() => wan.SendRegisterCallCount > registrationsBefore,
                    "the re-entered connect flow never registered on the new connection");
                Thread.Sleep(500);
                wan.RaiseWanRadioRadioListReceived(Array.Empty<Radio>());
            });

            var second = rig.AskSmartLinkAboutSerial(Asked, Account, "test-jwt");
            await server;

            Assert.True(second.FromALiveServerAnswer,
                "The re-entered connect flow returned before the new connection's list arrived, so it " +
                "answered from the rows it took on the previous connection (#619).");
            Assert.NotEqual(Verdict.Registered, second.Verdict);
            // The live connection's list is the account's whole current
            // list, and it is empty: the intake swept the old row.
            Assert.DoesNotContain(Asked, WanSerialsInMyRadioList(rig));
        }
    }
}
