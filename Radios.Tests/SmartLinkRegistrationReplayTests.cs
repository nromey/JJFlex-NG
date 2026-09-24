#nullable enable

using System;
using System.Collections.Generic;
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
        /// answer Registered, and the account must not count as consulted
        /// (#619). The same held lists feed the query's no-round-trip fast
        /// path, so this is the path that could answer before any network
        /// ask.
        /// </summary>
        /// <remarks>
        /// The connect flow still REPLAYS the old list here, and the latch
        /// assertion proves it ran: the replay is discovery for the selector's
        /// rows, not evidence, and it is deliberately left alone.
        /// </remarks>
        [Fact]
        public void A_listing_from_before_a_reconnect_does_not_answer_for_the_new_connection()
        {
            var (session, wan) = NewSession();
            ReconnectWithoutANewList(session, wan);

            // The trap: connected, and holding a list that names the radio.
            Assert.NotNull(session.LastRadioListUtc);
            Assert.Contains(session.AvailableRadios, r => r.Serial == Asked);

            var rig = NewRig();
            var finding = rig.AskSmartLinkAboutSerial(Asked, Account, "test-jwt");

            Assert.True(Latched(rig),
                "The list-received latch is not set, so the connect flow did not walk the held-list path.");
            Assert.NotEqual(Verdict.Registered, finding.Verdict);
            Assert.Equal(string.Empty, finding.ListedUnderAccount);
            Assert.Equal(0, finding.AccountsConsulted);
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
    }
}
