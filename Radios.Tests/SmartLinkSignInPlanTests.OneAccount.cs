#nullable enable

using System;
using System.Linq;
using Radios;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Codex's check of the 2026-11-06 integration pass (B1, F1, F2): the
    /// account a SmartLink connect NAMES, the account it ADOPTS, the session
    /// the dial goes THROUGH, and the account registration names must be one
    /// account, by every route. Three mock held sessions, no radio, no window.
    /// </summary>
    /// <remarks>
    /// <para>B1, reproduced by Codex with a probe over this harness: a radio
    /// listed by two held accounts, the owner's chosen. The plan named the
    /// owner, the preparation adopted the owner, then replayed every held list
    /// in the order the sessions were made, so the serial-keyed WAN bank ended
    /// on the club's, and the dial resolved through the club.</para>
    /// <para>F2: the fast path (the active session already lists the radio)
    /// skipped the plan, so it adopted nothing and skipped the shared-radio
    /// tie-break. F1: registration took only saved accounts, so a live account
    /// nobody saved, which the connect uses, was passed over.</para>
    /// </remarks>
    public sealed partial class SmartLinkSignInPlanTests
    {
        private const string Club = "club@example.com";

        /// <summary>Everything that names or uses an account for a connect to <see cref="Radio6300"/>, read after preparing it.</summary>
        private (string announced, bool prepared, string current, string wire, string registration) WalkTheConnect(
            FlexBase rig, params string[] saved)
        {
            string announced = FlexBase.AccountThatWillBroker(Radio6300, Operator, saved);
            rig.ShowAccountSelector = _ => (false, null!, false);
            bool prepared = rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: false, savedAccounts: Saved(saved));
            string wire = rig.BrokeringSessionForConnect(Radio6300, out _)?.AccountId ?? "";
            string registration = rig.ResolveRegistrationAccount(Radio6300, Saved(saved))?.Email ?? "";
            return (announced, prepared, rig.CurrentSmartLinkEmail, wire, registration);
        }

        private static void Prefer(string account)
            => Assert.True(RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = account; }));

        private static void PreferNobody()
            => RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = ""; });

        private static void AllAre(string expected, (string announced, bool prepared, string current, string wire, string registration) walk)
        {
            Assert.True(walk.prepared, "the connect was not ready to dial");
            Assert.Equal(expected, walk.announced);
            Assert.Equal(expected, walk.current);
            Assert.Equal(expected, walk.wire);
            Assert.Equal(expected, walk.registration);
        }

        /// <summary>
        /// B1. The owner is the radio's chosen account and the club's list was
        /// made last. The dial goes through the owner, the account named.
        /// </summary>
        [Fact]
        public void A_radio_two_held_accounts_list_is_dialled_through_the_account_the_plan_named()
        {
            Listed(Operator);
            Listed(Owner, Radio6300);
            Listed(Club, Radio6300);
            Prefer(Owner);
            try
            {
                var rig = NewRig();
                var walk = WalkTheConnect(rig, Operator, Owner, Club);

                // The positive control that this test reaches B1: after the
                // replay, the bank names the CLUB, so a dial that read the
                // bank would go through the club.
                Assert.Equal(Club, FlexBase.WanAccountForSerial(Radio6300));
                AllAre(Owner, walk);
            }
            finally { PreferNobody(); }
        }

        /// <summary>Codex's control: made in the other order, the answer is the same.</summary>
        [Fact]
        public void The_order_the_sessions_were_made_in_changes_nothing()
        {
            Listed(Operator);
            Listed(Club, Radio6300);
            Listed(Owner, Radio6300);
            Prefer(Owner);
            try
            {
                AllAre(Owner, WalkTheConnect(NewRig(), Operator, Owner, Club));
                Assert.Equal(Owner, FlexBase.WanAccountForSerial(Radio6300));
            }
            finally { PreferNobody(); }
        }

        /// <summary>Codex's control: one listing account, and everything agrees.</summary>
        [Fact]
        public void One_listing_account_agrees_throughout()
        {
            Listed(Operator);
            Listed(Owner, Radio6300);
            Prefer(Owner);
            try
            {
                AllAre(Owner, WalkTheConnect(NewRig(), Operator, Owner));
            }
            finally { PreferNobody(); }
        }

        /// <summary>
        /// F2. The active session already lists the radio (the old fast
        /// path), and so does the radio's chosen account. A fresh rig reaches
        /// the plan's answer, the chosen account, and adopts it, exactly as
        /// the full plan does. The fast path used to dial the active account
        /// and leave the rig's account empty.
        /// </summary>
        [Fact]
        public void The_fast_path_reaches_the_plans_answer_and_adopts_it()
        {
            Listed(Operator, Radio6300);
            Listed(Owner, Radio6300);
            _coordinator.EnsureSessionForAccount(Operator);
            Prefer(Owner);
            try
            {
                Assert.True(FlexBase.TheActiveSessionListsTheRadio(Radio6300), "the fast path's state was not set up");
                var rig = NewRig();
                Assert.Equal("", rig.CurrentSmartLinkEmail);

                AllAre(Owner, WalkTheConnect(rig, Operator, Owner));
            }
            finally { PreferNobody(); }
        }

        /// <summary>
        /// F2, the route: ReconnectRemote prepares every SmartLink connect, so
        /// the plan is the one decision, whichever state the session is in.
        /// </summary>
        [Fact]
        public void ReconnectRemote_prepares_every_connect_through_the_one_plan()
        {
            string src = FlexBasePartials.JoinedText();
            string reconnect = Body(src, "public bool ReconnectRemote(");
            Assert.Contains("bool remoteOk = PrepareSmartLinkForSerial(serial, allowInteractiveLogin, sw);", reconnect, StringComparison.Ordinal);
            // Nothing between the start of this attempt and the preparation
            // can route around it: the only statement there is the trace
            // that names the route, guarded by nothing else.
            int from = reconnect.IndexOf("LastConnectFailureReport = null;", StringComparison.Ordinal);
            int to = reconnect.IndexOf("bool remoteOk = PrepareSmartLinkForSerial(", StringComparison.Ordinal);
            Assert.True(from >= 0 && to > from);
            string between = string.Join(" ", reconnect.Substring(from, to - from)
                .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal)));
            Assert.StartsWith("LastConnectFailureReport = null; if (TheActiveSessionListsTheRadio(serial)) Tracing.TraceLine(", between, StringComparison.Ordinal);
            Assert.EndsWith("TraceLevel.Info);", between, StringComparison.Ordinal);
            Assert.Single(between.Split(new[] { "Tracing.TraceLine(" }, StringSplitOptions.None).Skip(1));
            Assert.DoesNotContain("return", between, StringComparison.Ordinal);
            Assert.DoesNotContain("else", between, StringComparison.Ordinal);
            Assert.DoesNotContain("ThePlanDecidesTheConnect", src, StringComparison.Ordinal);
            Assert.DoesNotContain("wanAlreadyConnected", reconnect, StringComparison.Ordinal);

            // And the dial resolves through the plan's account.
            string send = Body(src, "private bool sendRemoteConnect(Radio r)");
            Assert.Contains("BrokeringSessionForConnect(r.Serial, out bool viaOwningAccount, out string plannedAccount)", send, StringComparison.Ordinal);
            // ... and refuses, rather than falling through, when that account's session is gone (B1-R).
            Assert.Contains("if (session == null && plannedAccount != null)", send, StringComparison.Ordinal);
        }

        /// <summary>
        /// F1, ruled by the orchestrator under #713's principle: registration
        /// names the account the connect uses, a live account nobody saved
        /// included. Only the operator's account is saved, and it is the
        /// radio's chosen one; the owner's live session lists the radio.
        /// </summary>
        [Fact]
        public void Registration_names_the_live_account_the_connect_uses_even_when_nobody_saved_it()
        {
            Listed(Operator);
            Listed(Owner, Radio6300);
            Prefer(Operator);
            try
            {
                AllAre(Owner, WalkTheConnect(NewRig(), Operator));
            }
            finally { PreferNobody(); }
        }

        /// <summary>
        /// F1's consequence for the token: a live unsaved account that is this
        /// rig's own sign-in is used as that object, so its tokens come with
        /// it, rather than a bare name.
        /// </summary>
        [Fact]
        public void A_live_unsaved_account_signed_in_here_registers_with_its_own_sign_in()
        {
            Listed(Operator);
            Listed(Owner, Radio6300);
            var rig = NewRig();
            var signedIn = new SmartLinkAccount { Email = Owner, FriendlyName = Owner };
            CurrentAccountField!.SetValue(rig, signedIn);

            Assert.Same(signedIn, rig.ResolveRegistrationAccount(Radio6300, Saved(Operator)));
        }

        // ------------------------------------------------------------------
        // Round two (Codex's re-check of 671109bd)
        // ------------------------------------------------------------------

        private static readonly System.Reflection.MethodInfo SendRemoteConnect =
            typeof(FlexBase).GetMethod("sendRemoteConnect", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        /// <summary>
        /// B1-R, Codex's disconnect probe as a test. The plan named the owner
        /// and the preparation adopted it; then the owner's held session
        /// dropped before the dial. The dial used to fall through to the bank
        /// and go out through the CLUB, unannounced. A plan that named an
        /// account dials through that account or not at all: here, not at
        /// all, with a spoken reason, and no other account's session is asked
        /// to dial.
        /// </summary>
        [Fact]
        public void A_planned_account_that_drops_before_the_dial_is_refused_and_never_dialled_through_another()
        {
            Listed(Operator);
            var (ownerSession, ownerWan) = Listed(Owner, Radio6300);
            var (_, clubWan) = Listed(Club, Radio6300);
            Prefer(Owner);
            try
            {
                var rig = NewRig();
                rig.ShowAccountSelector = _ => (false, null!, false);
                Assert.True(rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: false, savedAccounts: Saved(Operator, Owner, Club)));
                // The positive control: healthy, the dial goes through the owner.
                Assert.Equal(Owner, rig.BrokeringSessionForConnect(Radio6300, out _)?.AccountId);

                ownerSession.Disconnect();
                WaitUntil(() => !ownerSession.IsConnected, "the owner's held session never went down");
                // The bank still names the club, so a fall-through would reach it.
                Assert.Equal(Club, FlexBase.WanAccountForSerial(Radio6300));

                Assert.Null(rig.BrokeringSessionForConnect(Radio6300, out _));

                int clubDials = clubWan.SendConnectMessageCallCount, ownerDials = ownerWan.SendConnectMessageCallCount;
                bool dialled = (bool)SendRemoteConnect.Invoke(rig, new object[] { WanRadio(Radio6300) })!;
                Assert.False(dialled);
                Assert.Equal(clubDials, clubWan.SendConnectMessageCallCount);
                Assert.Equal(ownerDials, ownerWan.SendConnectMessageCallCount);
                Assert.NotNull(rig.LastConnectFailureReport);
                Assert.Equal(ConnectFailureClass.SessionSetupFailed, rig.LastConnectFailureReport!.Class);
                // DRAFT wording; the radio's name falls back to its serial here.
                Assert.Equal(
                    "SmartLink lost the connection for owner@example.com before 1111-2222-6300-4444 was reached, so the radio was not contacted through another account. Connect again to try.",
                    rig.LastConnectFailureReport.SpokenSummary);
            }
            finally { PreferNobody(); }
        }

        /// <summary>
        /// Codex's three-listing controls: the operator's active session, the
        /// owner's and the club's all list the radio, the owner chosen, in
        /// both orders. Everything says the owner.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Three_listing_accounts_agree_on_the_chosen_one_in_either_order(bool clubFirst)
        {
            Listed(Operator, Radio6300);
            if (clubFirst) Listed(Club, Radio6300);
            Listed(Owner, Radio6300);
            if (!clubFirst) Listed(Club, Radio6300);
            _coordinator.EnsureSessionForAccount(Operator);
            Prefer(Owner);
            try
            {
                AllAre(Owner, WalkTheConnect(NewRig(), Operator, Owner, Club));
            }
            finally { PreferNobody(); }
        }

        /// <summary>
        /// F1-R. A held-session connect adopted an email-only account object
        /// in place of this rig's own unsaved sign-in for the same account,
        /// throwing away its tokens. The rig's own object is kept.
        /// </summary>
        [Fact]
        public void Adopting_a_held_session_keeps_this_rigs_own_sign_in_for_that_account()
        {
            Listed(Owner, Radio6300);
            Prefer(Owner);
            try
            {
                var rig = NewRig();
                var signedIn = new SmartLinkAccount { Email = Owner, FriendlyName = Owner, RefreshToken = "offline-token-marker" };
                CurrentAccountField!.SetValue(rig, signedIn);
                rig.ShowAccountSelector = _ => (false, null!, false);

                Assert.True(rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: false, savedAccounts: Saved(Operator)));

                Assert.Same(signedIn, CurrentAccountField.GetValue(rig));
                var registering = rig.ResolveRegistrationAccount(Radio6300, Saved(Operator));
                Assert.Same(signedIn, registering);
                Assert.Equal("offline-token-marker", registering!.RefreshToken);
            }
            finally { PreferNobody(); }
        }
    }
}
