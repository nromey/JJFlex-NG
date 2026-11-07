#nullable enable

using System;
using System.Linq;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The 2026-11-06 junk box integration pass: what the advisory
    /// registration query may change (#713), and how the connect's account
    /// decision reaches everything that names or uses an account (#798).
    /// </summary>
    public sealed partial class SmartLinkSignInPlanTests
    {
        /// <summary>
        /// Integration finding 1. The registration query runs after every LAN
        /// connect, under the radio's own account. Until now it made that
        /// account's session the ACTIVE one, which moves the spoken SmartLink
        /// status, the next connect's fallback session, and ReconnectRemote's
        /// "is SmartLink live" question to another account. An advisory query
        /// must leave the active account where it was.
        /// </summary>
        [Fact]
        public void The_registration_query_never_changes_the_active_account()
        {
            var operatorSession = _coordinator.EnsureSessionForAccount(Operator);   // the operator's active account
            var (_, ownerWan) = ConnectedNotYetListed(Owner);                        // the radio's own account, live
            Assert.Same(operatorSession, _coordinator.ActiveSession);
            var rig = NewRig();

            var query = System.Threading.Tasks.Task.Run(() => rig.AskSmartLinkAboutSerial(Radio6300, Owner, "test-jwt"));
            WaitUntil(() => ownerWan.SendRegisterCallCount > 0, "the query never registered");
            ownerWan.RaiseWanRadioRadioListReceived(new[] { WanRadio(SomeOtherRadio) });
            Assert.True(query.Wait(8000));

            Assert.Same(operatorSession, _coordinator.ActiveSession);
            // And the query still hears the server's answer through the
            // session it did not activate.
            Assert.True(query.Result.FromALiveServerAnswer);
            Assert.Equal(FlexBase.SmartLinkRegistrationQuery.NotInAccountList, query.Result.Verdict);
        }

        /// <summary>
        /// The control: a connect under an account does make it the active
        /// one, as it always has, so the test above is about the query alone.
        /// </summary>
        [Fact]
        public void A_connect_under_an_account_still_makes_it_active()
        {
            var operatorSession = _coordinator.EnsureSessionForAccount(Operator);
            var (ownerSession, _) = Listed(Owner, SomeOtherRadio);
            var rig = NewRig();

            Assert.Equal("Success", Connect(rig, "test-jwt", Owner).ToString());

            Assert.Same(ownerSession, _coordinator.ActiveSession);
            Assert.NotSame(operatorSession, _coordinator.ActiveSession);
        }

        /// <summary>
        /// Integration finding 3. A connect through the radio's own held
        /// session used no sign-in, and so never changed the connection's own
        /// account (_currentAccount). The Settings Network tab then saved a
        /// port preference to, and named, the old account, and the legacy
        /// hole-punch port was read from it. The held account is adopted.
        /// </summary>
        [Fact]
        public void A_connect_through_a_held_session_adopts_that_account()
        {
            Listed(Operator);
            Listed(Owner, Radio6300);
            var rig = NewRig();
            CurrentAccountField!.SetValue(rig, new SmartLinkAccount { Email = Operator, FriendlyName = Operator });
            rig.ShowAccountSelector = _ => (false, null!, false);

            Assert.True(rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: false, savedAccounts: Saved(Operator, Owner)));

            Assert.Equal(Owner, rig.CurrentSmartLinkEmail);
        }

        /// <summary>
        /// Integration finding 6, as Codex F2 settled it: the old fast path's
        /// state (the active session is live and lists the radio) is still
        /// recognised, but only for the trace. The plan decides every connect;
        /// <see cref="ReconnectRemote_prepares_every_connect_through_the_one_plan"/>
        /// pins the route.
        /// </summary>
        [Fact]
        public void The_active_session_listing_the_radio_is_recognised()
        {
            Assert.False(FlexBase.TheActiveSessionListsTheRadio(Radio6300));      // no active session

            var active = _coordinator.EnsureSessionForAccount(Operator);
            var (_, wan) = Listed(Operator, SomeOtherRadio);
            Assert.Same(active, _coordinator.ActiveSession);
            Assert.False(FlexBase.TheActiveSessionListsTheRadio(Radio6300));      // live, but not listing it

            wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Radio6300) });
            WaitUntil(() => active.RadioListSnapshot.Radios.Any(r => r.Serial == Radio6300), "the list never landed");
            Assert.True(FlexBase.TheActiveSessionListsTheRadio(Radio6300));       // listed
        }

        /// <summary>
        /// Integration finding 4 (#401's class). With no session up yet, the
        /// picker's "Trying ... as" sentence and the row label named the
        /// active default account while the plan signed in as the radio's own.
        /// They now name the account the plan uses, from one reading.
        /// </summary>
        [Fact]
        public void The_account_named_before_a_connect_is_the_one_the_plan_signs_in_as()
        {
            Assert.True(RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = Owner; }));
            try
            {
                Assert.Equal(Owner, FlexBase.AccountThatWillBroker(Radio6300, Operator, new[] { Operator, Owner }));
                // Nothing known: the default, as before.
                Assert.Equal(Operator, FlexBase.AccountThatWillBroker(SomeOtherRadio, Operator, new[] { Operator, Owner }));
            }
            finally
            {
                RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = ""; });
            }
        }

        /// <summary>
        /// The other half of finding 6: now that the plan runs with a live
        /// session, its "nothing better is known" case keeps the old fast
        /// path, with no sign-in and no account selector.
        /// </summary>
        [Fact]
        public void With_a_live_session_and_nothing_known_the_plan_signs_in_to_nothing()
        {
            _coordinator.EnsureSessionForAccount(Operator);
            Listed(Operator, SomeOtherRadio);                 // live, not listing the radio
            var rig = NewRig();
            int selectorAsked = 0;
            rig.ShowAccountSelector = _ => { selectorAsked++; return (false, null!, false); };

            bool ready = rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: false,
                savedAccounts: Saved(Operator, Owner));        // Owner has not answered: no verdict

            Assert.True(ready);
            Assert.Equal(0, selectorAsked);
            Assert.Null(rig.LastConnectFailureReport);
        }
    }
}
