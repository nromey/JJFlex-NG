#nullable enable

using System;
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
    }
}
