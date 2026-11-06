#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// A SmartLink connect decides how to reach a radio from the RADIO, before
    /// anything signs in (#798), and an account whose list for its live
    /// connection has already come back empty is not waited on (#613).
    /// </summary>
    /// <remarks>
    /// <para>Real session owners over a mock SmartLink server, one per account,
    /// the same harness <see cref="SmartLinkRegistrationReplayTests"/> uses. No
    /// radio, no window, no network.</para>
    /// <para>The field case these pin: the operator's own account is the saved
    /// default and its radio is switched off, so its list is empty; the radio
    /// being connected to is on ANOTHER saved account, listed live on that
    /// account's held presence session. The connect used to sign in as the
    /// default, wait ten seconds, cycle the session and say "Could not sign in
    /// to SmartLink", and never reach the owner-aware routing at all.</para>
    /// </remarks>
    [Collection(SmartLinkSingletonCollection.Name)]
    public sealed partial class SmartLinkSignInPlanTests : IDisposable
    {
        private const string Operator = "operator@example.com";   // the saved default, its radio off
        private const string Owner = "owner@example.com";         // the account the radio is on
        private const string Radio6300 = "1111-2222-6300-4444";
        private const string SomeOtherRadio = "5555-6666-6400-7777";

        private readonly SmartLinkSessionCoordinator _original;
        private readonly Func<IReadOnlyList<SmartLinkAccount>>? _originalAccountsHook;
        private readonly Func<SmartLinkAccount, bool, string?>? _originalJwtHook;
        private readonly List<FlexBase> _rigs = new();
        private readonly Dictionary<string, MockWanServer> _servers = new(StringComparer.OrdinalIgnoreCase);
        private readonly SmartLinkSessionCoordinator _coordinator;

        public SmartLinkSignInPlanTests()
        {
            _original = SmartLinkServices.Coordinator;
            _originalAccountsHook = SmartLinkPresenceService.AccountsHook;
            _originalJwtHook = SmartLinkPresenceService.SilentJwtHook;
            SmartLinkPresenceService.AccountsHook = () => Array.Empty<SmartLinkAccount>();
            SmartLinkPresenceService.SilentJwtHook = (_, __) => null;

            _coordinator = new SmartLinkSessionCoordinator(accountId =>
            {
                var wan = new MockWanServer();
                lock (_servers) _servers[accountId] = wan;
                return new WanSessionOwner(
                    sessionId: Guid.NewGuid().ToString("N").Substring(0, 12),
                    accountId: accountId,
                    wanServer: wan,
                    audioSink: new DirectPassthroughSink(),
                    backoffScheduleMs: new[] { 50, 50, 50 });
            });
            SmartLinkServices.Override(_coordinator);
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
            try { _coordinator.Dispose(); } catch { }

            foreach (var account in new[] { Operator, Owner, "default-account" })
                ForgetWanRadiosForAccount?.Invoke(null, new object[] { account, "test teardown" });
        }

        // ------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------

        private static readonly MethodInfo? ForgetWanRadiosForAccount =
            typeof(FlexBase).GetMethod("ForgetWanRadiosForAccount", BindingFlags.NonPublic | BindingFlags.Static);

        private static Radio WanRadio(string serial) => SmartLinkRegistrationReplayTests.WanRadio(serial);

        /// <summary>A held presence session for the account, connected, that has had this list on its live connection.</summary>
        private (IWanSessionOwner session, MockWanServer wan) Listed(string account, params string[] serials)
        {
            var session = _coordinator.GetOrCreateSession(account);
            MockWanServer wan;
            lock (_servers) wan = _servers[account];
            session.Connect();
            WaitUntil(() => session.IsConnected, account + "'s mock session never reported connected");
            wan.RaiseWanRadioRadioListReceived(serials.Select(WanRadio).ToArray());
            WaitUntil(() => session.RadioListSnapshot.ArrivedOnTheLiveConnection,
                account + "'s list never registered as arrived on the live connection");
            return (session, wan);
        }

        /// <summary>A held session that has connected and NOT yet been listed on this connection.</summary>
        private (IWanSessionOwner session, MockWanServer wan) ConnectedNotYetListed(string account)
        {
            var session = _coordinator.GetOrCreateSession(account);
            MockWanServer wan;
            lock (_servers) wan = _servers[account];
            session.Connect();
            WaitUntil(() => session.IsConnected, account + "'s mock session never reported connected");
            Assert.False(session.RadioListSnapshot.ArrivedOnTheLiveConnection,
                "The harness meant a session with no list yet, and it has one.");
            return (session, wan);
        }

        private FlexBase NewRig()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            _rigs.Add(rig);
            return rig;
        }

        /// <summary>The plan from one picture of the real sessions, with the radio's chosen or last-seen account taken from a route.</summary>
        private FlexBase.SmartLinkSignInPlan PlanFor(string[] saved, AccountRoute route)
        {
            string preferred = route.Source == AccountRouteSource.Preferred ? route.Account : "";
            string lastSeen = route.Source == AccountRouteSource.LastSeen ? route.Account : "";
            return FlexBase.PlanSmartLinkSignIn(
                FlexBase.TakeAccountPicture(Radio6300, _coordinator.AllSessions, preferred, lastSeen, onLan: false, saved));
        }

        private static List<SmartLinkAccount> Saved(params string[] emails) =>
            emails.Select(e => new SmartLinkAccount { Email = e, FriendlyName = e }).ToList();

        private static void WaitUntil(Func<bool> condition, string because, int timeoutMs = 5000)
        {
            Assert.True(SpinWait.SpinUntil(condition, timeoutMs), because);
        }

        // ------------------------------------------------------------------
        // #798: the plan, decided from the radio
        // ------------------------------------------------------------------

        /// <summary>
        /// The field case, as a decision: a live foreign row. The radio is on
        /// the owner's held session; the default account's list is empty. The
        /// plan is the owner's held session, not a sign-in as the default.
        /// </summary>
        [Fact]
        public void A_radio_live_on_another_accounts_held_session_plans_that_session()
        {
            Listed(Operator);                 // the default, its own radio off
            Listed(Owner, Radio6300);         // the radio, live on the owner's session

            var plan = PlanFor(new[] { Operator, Owner }, AccountRoute.None);

            Assert.Equal(Plan.UseHeldSession, plan.Kind);
            Assert.Equal(Owner, plan.Account);
        }

        /// <summary>
        /// The same field case driven through the preparation the connect
        /// runs: nothing signs in as the default, the account selector is
        /// never asked, the owner's session becomes the one the connect works
        /// through, and the radio is findable for the dial.
        /// </summary>
        [Fact]
        public void The_connect_reaches_a_live_foreign_radio_through_its_owners_session_with_no_sign_in()
        {
            var (_, operatorWan) = Listed(Operator);
            var (ownerSession, ownerWan) = Listed(Owner, Radio6300);
            int operatorRegistrationsBefore = operatorWan.SendRegisterCallCount;
            int ownerRegistrationsBefore = ownerWan.SendRegisterCallCount;

            var rig = NewRig();
            int selectorAsked = 0;
            rig.ShowAccountSelector = _ => { selectorAsked++; return (false, null!, false); };

            bool ready = rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: false,
                savedAccounts: Saved(Operator, Owner));

            Assert.True(ready, "The connect was not ready to dial a radio its owner's held session lists.");
            Assert.Equal(0, selectorAsked);
            Assert.Equal(operatorRegistrationsBefore, operatorWan.SendRegisterCallCount);
            Assert.Equal(ownerRegistrationsBefore, ownerWan.SendRegisterCallCount);
            Assert.Same(ownerSession, _coordinator.ActiveSession);
            Assert.NotNull(rig.FindRadioBySerial(Radio6300));
            Assert.Null(rig.LastConnectFailureReport);
        }

        /// <summary>
        /// Every saved account has listed on its live connection and none
        /// lists the radio: that is an answer. Said at once, with no sign-in
        /// and no wait, and in words that are true.
        /// </summary>
        [Fact]
        public void A_radio_no_saved_account_lists_is_said_at_once_without_signing_in()
        {
            Listed(Operator);
            Listed(Owner, SomeOtherRadio);

            var plan = PlanFor(new[] { Operator, Owner }, AccountRoute.None);
            Assert.Equal(Plan.NotListedByAnySavedAccount, plan.Kind);

            var rig = NewRig();
            int selectorAsked = 0;
            rig.ShowAccountSelector = _ => { selectorAsked++; return (false, null!, false); };
            var sw = Stopwatch.StartNew();
            bool ready = rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: false,
                savedAccounts: Saved(Operator, Owner));
            sw.Stop();

            Assert.False(ready);
            Assert.Equal(0, selectorAsked);
            Assert.True(sw.ElapsedMilliseconds < 3000, $"Saying a radio is on no account took {sw.ElapsedMilliseconds} ms.");
            var report = rig.LastConnectFailureReport;
            Assert.NotNull(report);
            Assert.Equal(ConnectFailureClass.RadioNotFound, report!.Class);
            // Named by the radio (no saved name here, so its serial), and not
            // a sign-in failure. The wording itself is draft and lives in the
            // lexicon; this class stays off the lexicon store (it holds the
            // SmartLink singleton, not the settings statics).
            Assert.Contains(Radio6300, report.SpokenSummary, StringComparison.Ordinal);
            Assert.Contains("SmartLink accounts", report.SpokenSummary, StringComparison.Ordinal);
            Assert.DoesNotContain("sign in", report.SpokenSummary, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The ambiguity #798 names: a session that has connected and not yet
        /// had its list holds nothing, exactly as a session whose list came
        /// back empty does. It must NOT be read as "not on that account".
        /// </summary>
        [Fact]
        public void An_account_whose_list_has_not_arrived_is_not_read_as_not_listing_the_radio()
        {
            Listed(Operator);
            ConnectedNotYetListed(Owner);

            var plan = PlanFor(new[] { Operator, Owner }, AccountRoute.None);

            Assert.NotEqual(Plan.NotListedByAnySavedAccount, plan.Kind);
            Assert.Equal(Plan.SignInAsUsual, plan.Kind);
        }

        /// <summary>
        /// The radio's own account is known (here, the operator's choice for
        /// it) but its session cannot answer yet: sign in as THAT account,
        /// never the default.
        /// </summary>
        [Fact]
        public void A_known_radio_account_with_no_live_answer_is_signed_in_as_that_account()
        {
            Listed(Operator);
            ConnectedNotYetListed(Owner);

            var plan = PlanFor(new[] { Operator, Owner }, new AccountRoute(Owner, AccountRouteSource.Preferred));

            Assert.Equal(Plan.SignInAsRadiosAccount, plan.Kind);
            Assert.Equal(Owner, plan.Account);
        }

        /// <summary>
        /// A route that names an account nobody saved cannot be signed in as;
        /// the plan falls back to the default path rather than refusing.
        /// </summary>
        [Fact]
        public void A_route_to_an_unsaved_account_falls_back_to_the_usual_sign_in()
        {
            ConnectedNotYetListed(Operator);

            var plan = PlanFor(new[] { Operator }, new AccountRoute("stranger@example.com", AccountRouteSource.LastSeen));

            Assert.Equal(Plan.SignInAsUsual, plan.Kind);
        }

        /// <summary>
        /// Repair round 1, Codex B1. The WAN bank keeps a radio's handle, and
        /// the account that delivered it, across a drop, which is right for a
        /// recovery dial. It is NOT live attribution. Here account A once
        /// listed the radio and its session has since disconnected; the
        /// operator has since chosen account B for the radio. The resolver must
        /// answer B, and the plan must sign in as B, not A.
        /// </summary>
        [Fact]
        public void A_dropped_sessions_old_attribution_does_not_outrank_the_radios_chosen_account()
        {
            var (operatorSession, _) = Listed(Operator, Radio6300);
            var rig = NewRig();
            rig.ReplayHeldListsIntoTheIntake(Operator, sessionWasAlreadyConnected: true);
            Assert.Equal(Operator, FlexBase.WanAccountForSerial(Radio6300));   // the bank holds the old attribution

            _coordinator.DisconnectSession(operatorSession.SessionId);
            WaitUntil(() => !operatorSession.IsConnected, "the operator's session never reported disconnected");
            Assert.True(RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = Owner; }));
            try
            {
                var route = RadioAccountRouting.ForSerial(Radio6300);
                Assert.Equal(new AccountRoute(Owner, AccountRouteSource.Preferred), route);

                var plan = PlanFor(new[] { Operator, Owner }, route);
                Assert.Equal(Plan.SignInAsRadiosAccount, plan.Kind);
                Assert.Equal(Owner, plan.Account);
            }
            finally
            {
                RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = ""; });
            }
        }

        /// <summary>
        /// The control for the test above: while the session that listed the
        /// radio is connected and its list is current, that live attribution
        /// still outranks the stored choice.
        /// </summary>
        [Fact]
        public void A_current_listing_is_live_attribution_and_outranks_the_stored_choice()
        {
            Listed(Operator, Radio6300);
            var rig = NewRig();
            rig.ReplayHeldListsIntoTheIntake(Operator, sessionWasAlreadyConnected: true);
            Assert.True(RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = Owner; }));
            try
            {
                Assert.Equal(new AccountRoute(Operator, AccountRouteSource.LiveAttribution),
                    RadioAccountRouting.ForSerial(Radio6300));
            }
            finally
            {
                RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = ""; });
            }
        }

        // ------------------------------------------------------------------
        // #798: the shared routing resolver's order (#405's routing half)
        // ------------------------------------------------------------------

        [Fact]
        public void The_routing_resolver_answers_live_then_preferred_then_last_seen_then_empty()
        {
            Assert.Equal(new AccountRoute(Owner, AccountRouteSource.LiveAttribution),
                RadioAccountRouting.Resolve(Owner, "club@example.com", "seen@example.com"));
            Assert.Equal(new AccountRoute("club@example.com", AccountRouteSource.Preferred),
                RadioAccountRouting.Resolve("", "club@example.com", "seen@example.com"));
            Assert.Equal(new AccountRoute("seen@example.com", AccountRouteSource.LastSeen),
                RadioAccountRouting.Resolve(null, "  ", "seen@example.com"));
            var none = RadioAccountRouting.Resolve(null, null, null);
            Assert.True(none.IsEmpty);
            Assert.Equal(AccountRouteSource.None, none.Source);
        }

        // ------------------------------------------------------------------
        // #713: the registration query asks under the radio's own account
        // ------------------------------------------------------------------

        private static readonly FieldInfo? CurrentAccountField =
            typeof(FlexBase).GetField("_currentAccount", BindingFlags.NonPublic | BindingFlags.Instance);

        private Radios.SmartLinkRegistrationEvidence.Finding AskWithCurrentAccount(string current, string asked)
        {
            _coordinator.GetOrCreateSession(asked);
            MockWanServer wan;
            lock (_servers) wan = _servers[asked];
            var rig = NewRig();
            Assert.True(CurrentAccountField != null, "FlexBase._currentAccount is not where this test sets it.");
            CurrentAccountField!.SetValue(rig, new SmartLinkAccount { Email = current, FriendlyName = current });

            // The server answers the registration with the account's list,
            // explicitly empty: a fresh absence, as for a first registration.
            var server = Task.Run(() =>
            {
                WaitUntil(() => wan.SendRegisterCallCount > 0, "the query never registered on the asked account's session");
                Thread.Sleep(200);
                wan.RaiseWanRadioRadioListReceived(Array.Empty<Radio>());
            });
            var finding = rig.AskSmartLinkAboutSerial(Radio6300, asked, "test-jwt");
            server.Wait(15000);
            return finding;
        }

        /// <summary>
        /// Repair round 1, Codex's second #713 blocker. The query opens the
        /// asked account's session, but the intake's one-shot latch and the
        /// capture of the server's answer were keyed to the CONNECTION's
        /// account. So account B's fresh list was not "ours", no live answer
        /// was captured, and a fresh absence came back Unknown after the full
        /// list wait.
        /// </summary>
        [Fact]
        public void A_query_under_the_radios_own_account_hears_that_accounts_answer()
        {
            var sw = Stopwatch.StartNew();
            var finding = AskWithCurrentAccount(current: Operator, asked: Owner);
            sw.Stop();

            Assert.True(finding.FromALiveServerAnswer,
                "The asked account's own list was not taken as the server's answer, because the reply was filtered by the connection's account.");
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, finding.Verdict);
            Assert.True(sw.ElapsedMilliseconds < 8000, $"The query took {sw.ElapsedMilliseconds} ms: it sat out the list wait.");
        }

        /// <summary>The control: the same push under the matching account was always heard.</summary>
        [Fact]
        public void A_query_under_the_connections_own_account_hears_its_answer()
        {
            var finding = AskWithCurrentAccount(current: Owner, asked: Owner);
            Assert.True(finding.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, finding.Verdict);
        }

        // ------------------------------------------------------------------
        // #613: the ten-second wait with nothing to wait for
        // ------------------------------------------------------------------

        /// <summary>
        /// The account's held session had its list for its live connection,
        /// and the list was empty. The connect flow answers from it at once
        /// instead of waiting ten seconds for a list the server does not send
        /// again on that connection.
        /// </summary>
        [Fact]
        public void A_live_list_that_arrived_empty_is_answered_without_the_ten_second_wait()
        {
            var (session, _) = Listed("default-account");
            Assert.True(FlexBase.TheLiveListAnsweredEmpty(session.RadioListSnapshot, "default-account"));

            var rig = NewRig();
            var sw = Stopwatch.StartNew();
            rig.AskSmartLinkAboutSerial(Radio6300, "default-account", "test-jwt");
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 5000,
                $"The connect flow waited {sw.ElapsedMilliseconds} ms on an account whose list for its live connection had already arrived empty (#613).");
        }

        /// <summary>
        /// The partner, and the half that keeps the fix honest: a live session
        /// whose list has NOT arrived yet still waits for it, and the list that
        /// then arrives is the answer.
        /// </summary>
        [Fact]
        public async Task A_live_session_whose_list_has_not_arrived_still_waits_for_it()
        {
            var (session, wan) = ConnectedNotYetListed("default-account");
            Assert.False(FlexBase.TheLiveListAnsweredEmpty(session.RadioListSnapshot, "default-account"));

            var rig = NewRig();
            var server = Task.Run(() =>
            {
                WaitUntil(() => wan.SendRegisterCallCount > 0, "the connect flow never registered");
                Thread.Sleep(1500);
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Radio6300) });
            });

            var sw = Stopwatch.StartNew();
            var finding = rig.AskSmartLinkAboutSerial(Radio6300, "default-account", "test-jwt");
            sw.Stop();
            await server;

            Assert.True(sw.ElapsedMilliseconds >= 1400,
                $"The flow returned after {sw.ElapsedMilliseconds} ms, before the list it should have waited for.");
            Assert.True(finding.FromALiveServerAnswer, "The list the flow waited for was not what answered it.");
        }

        // ------------------------------------------------------------------
        // #798: the wiring, pinned at the source
        // ------------------------------------------------------------------

        /// <summary>
        /// The tests above drive the preparation directly; this pins that the
        /// connect actually runs it. With no active session, ReconnectRemote
        /// must decide through <c>PrepareSmartLinkForSerial</c>, and must not
        /// call <c>setupRemote</c> itself, which signs in as the selector's
        /// default and is the defect.
        /// </summary>
        [Fact]
        public void ReconnectRemote_decides_from_the_radio_before_any_sign_in()
        {
            string flexBase = FlexBasePartials.JoinedText();
            string reconnect = Body(flexBase, "public bool ReconnectRemote(");
            Assert.Contains("PrepareSmartLinkForSerial(serial, allowInteractiveLogin, sw)", reconnect, StringComparison.Ordinal);
            Assert.DoesNotContain("setupRemote(", reconnect, StringComparison.Ordinal);
            Assert.Contains("LastConnectFailureReport = null;", reconnect, StringComparison.Ordinal);
        }

        /// <summary>
        /// The false sentence: setupRemote's empty-list exits used to file no
        /// report, so the connect's fallback said sign-in had failed when it
        /// had succeeded. The done label now files the true one.
        /// </summary>
        [Fact]
        public void An_account_that_signs_in_to_an_empty_list_files_a_true_report()
        {
            string flexBase = FlexBasePartials.JoinedText();
            string setup = Body(flexBase, "private bool setupRemote(");
            int label = setup.IndexOf("setupRemoteDone:", StringComparison.Ordinal);
            Assert.True(label > 0, "setupRemote's done label is not where this test reads it.");
            string tail = setup.Substring(label);
            Assert.Contains("if (connectResult == SmartLinkConnectResult.NoRadios)", tail, StringComparison.Ordinal);
            Assert.Contains("\"connect.failure.smartlink_lists_no_radios\"", tail, StringComparison.Ordinal);
        }

        private static string Body(string text, string signature)
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

        /// <summary>
        /// The decision itself, on one snapshot: arrived and empty answers;
        /// arrived with a radio does not (the rows answer that case); not
        /// arrived does not.
        /// </summary>
        [Fact]
        public void Only_an_arrived_empty_list_is_an_empty_answer()
        {
            var (empty, _) = Listed(Operator);
            var (full, _) = Listed(Owner, Radio6300);
            Assert.True(FlexBase.TheLiveListAnsweredEmpty(empty.RadioListSnapshot, Operator));
            Assert.False(FlexBase.TheLiveListAnsweredEmpty(full.RadioListSnapshot, Owner));
        }
    }
}
