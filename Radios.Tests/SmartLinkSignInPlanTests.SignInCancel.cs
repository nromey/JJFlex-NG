#nullable enable

using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Radios;
using Radios.SmartLink;
using Xunit;

using Plan = Radios.FlexBase.SmartLinkSignInPlanKind;

namespace Radios.Tests
{
    /// <summary>
    /// A SmartLink sign-in the OPERATOR cancelled abandons the connect (#1244):
    /// "Login cancelled" is said once, and nothing after it claims a failure.
    /// Driven through the real preparation a connect runs
    /// (<c>PrepareSmartLinkForSerial</c>, then <c>setupRemote</c>, then
    /// <c>GetJwtFromSavedAccount</c> for the radio's own account, then
    /// <c>PerformNewLogin</c>) and <c>ReconnectRemote</c>'s answer to a failed
    /// preparation, over the same mock SmartLink sessions as the rest of this
    /// class.
    /// </summary>
    /// <remarks>
    /// <para><b>No window is ever built.</b> Every rig here has
    /// <c>SignInWindowsForTest</c> set before anything can sign in, so
    /// <c>PerformNewLogin</c> never reaches either sign-in form; the seam says
    /// how the windows closed and the rig's own ending
    /// (<c>SignInEndedWithoutAToken</c>) decides. Speech is suppressed on the
    /// rig; what is said is <see cref="SignInCancelSpeechTests"/>'s.</para>
    /// <para>The error half of each case is the same road with a window that
    /// closed for any other reason, and the failure must come back exactly as
    /// before: an error, a null token or a refused sign-in is never a
    /// cancel.</para>
    /// </remarks>
    public sealed partial class SmartLinkSignInPlanTests
    {
        public static IEnumerable<object?[]> OperatorCancels() => new[]
        {
            new object?[] { DialogResult.Cancel, null, false },                  // JJ Flexible's own form: Cancel, Escape, a close
            new object?[] { DialogResult.Retry, DialogResult.Cancel, true },     // the browser page, closed by the operator
        };

        public static IEnumerable<object?[]> EndingsThatAreNotCancels() => new[]
        {
            new object?[] { DialogResult.Abort, null, false },                   // a failure on the form's own thread
            new object?[] { DialogResult.Retry, DialogResult.Cancel, false },    // an error box, or a failed token exchange
            new object?[] { DialogResult.Retry, DialogResult.Abort, false },     // WebView2 could not start
        };

        /// <summary>A rig whose sign-in windows are the seam, recording which account each sign-in was for.</summary>
        private FlexBase RigWhoseSignInEnds(DialogResult native, DialogResult? browser, bool closedByOperator, List<string> signIns)
        {
            var rig = NewRig();
            rig.SuppressSpeech = true;
            rig.SignInWindowsForTest = account =>
            {
                signIns.Add(account?.Email ?? "(a new sign-in)");
                return (native, browser, closedByOperator);
            };
            // If the process-wide account store holds any account, setupRemote
            // asks the selector first: answer "a new sign-in", which reaches
            // the same PerformNewLogin as the no-saved-accounts road.
            rig.ShowAccountSelector = _ => (true, null!, true);
            Assert.NotNull(rig.SignInWindowsForTest);   // the guard on every window below
            return rig;
        }

        [Theory]
        [MemberData(nameof(OperatorCancels))]
        public void A_cancelled_usual_sign_in_abandons_the_connect_and_files_no_failure(
            DialogResult native, DialogResult? browser, bool closedByOperator)
        {
            var signIns = new List<string>();
            var rig = RigWhoseSignInEnds(native, browser, closedByOperator, signIns);

            // The operator's own account, its list not yet known: the usual sign-in.
            bool ready = rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: true, savedAccounts: Saved(Operator));

            Assert.False(ready);
            Assert.Single(signIns);                       // one sign-in, so one "Login cancelled"
            Assert.True(rig.SignInCancelledByOperator);
            Assert.Null(rig.LastConnectFailureReport);    // setupRemote filed nothing
            // What the picker's pending connect is handed (repair 1): the cancel, not "unreachable".
            Assert.Equal(RemotePassOutcome.SignInCancelled, rig.RemotePassOutcome);

            // ReconnectRemote's answer to the failed preparation: still nothing.
            rig.FileReportForFailedPreparation();
            Assert.Null(rig.LastConnectFailureReport);
            Assert.Null(rig.LastConnectFailureAdvice);    // so the walk has no advice to hold
        }

        [Theory]
        [MemberData(nameof(EndingsThatAreNotCancels))]
        public void Any_other_empty_usual_sign_in_still_files_the_failure(
            DialogResult native, DialogResult? browser, bool closedByOperator)
        {
            var signIns = new List<string>();
            var rig = RigWhoseSignInEnds(native, browser, closedByOperator, signIns);

            bool ready = rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: true, savedAccounts: Saved(Operator));

            Assert.False(ready);
            Assert.Single(signIns);                       // positive control: the sign-in was reached
            Assert.False(rig.SignInCancelledByOperator);
            Assert.NotEqual(RemotePassOutcome.SignInCancelled, rig.RemotePassOutcome);
            rig.FileReportForFailedPreparation();
            var report = rig.LastConnectFailureReport;
            Assert.NotNull(report);
            Assert.Equal(ConnectFailureClass.SessionSetupFailed, report!.Class);
            Assert.Equal("Could not sign in to SmartLink or reach its server, so the radio was never contacted.", report.SpokenSummary);
        }

        /// <summary>
        /// The radio's own account is known and its silent token is gone, so
        /// <c>GetJwtFromSavedAccount</c> offers its recovery sign-in. Cancelled,
        /// the connect is abandoned; the recovery was for THAT account.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_radios_own_account_recovery_cancelled_abandons_the_connect_and_an_error_still_fails(bool cancelled)
        {
            Listed(Operator);
            ConnectedNotYetListed(Owner);
            var signIns = new List<string>();
            var rig = cancelled
                ? RigWhoseSignInEnds(DialogResult.Cancel, null, false, signIns)
                : RigWhoseSignInEnds(DialogResult.Abort, null, false, signIns);
            Assert.True(RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = Owner; }));
            try
            {
                // Positive control: this is the road through the radio's own account.
                var route = RadioAccountRouting.ForSerial(Radio6300);
                Assert.Equal(Plan.SignInAsRadiosAccount, PlanFor(new[] { Operator, Owner }, route).Kind);

                bool ready = rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: true,
                    savedAccounts: Saved(Operator, Owner));

                Assert.False(ready);
                Assert.Equal(new[] { Owner }, signIns);   // GetJwtFromSavedAccount's recovery, for the owner
                Assert.Equal(cancelled, rig.SignInCancelledByOperator);
                rig.FileReportForFailedPreparation();
                if (cancelled)
                {
                    Assert.Null(rig.LastConnectFailureReport);
                }
                else
                {
                    Assert.NotNull(rig.LastConnectFailureReport);
                    Assert.Equal(ConnectFailureClass.SessionSetupFailed, rig.LastConnectFailureReport!.Class);
                }
            }
            finally
            {
                RadioConfig.Update(Radio6300, cfg => { cfg.PreferredAccount = ""; });
            }
        }

        /// <summary>
        /// A cancel belongs to the sign-in that saw it. The next pass, with a
        /// window that failed, must not inherit it and keep its failure quiet.
        /// </summary>
        [Fact]
        public void A_cancel_does_not_stand_for_the_next_sign_in()
        {
            var signIns = new List<string>();
            var ending = (native: DialogResult.Cancel, browser: (DialogResult?)null, closed: false);
            var rig = NewRig();
            rig.SuppressSpeech = true;
            rig.SignInWindowsForTest = account => { signIns.Add(account?.Email ?? "new"); return (ending.native, ending.browser, ending.closed); };
            rig.ShowAccountSelector = _ => (true, null!, true);

            Assert.False(rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: true, savedAccounts: Saved(Operator)));
            Assert.True(rig.SignInCancelledByOperator);

            ending = (DialogResult.Abort, null, false);
            Assert.False(rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: true, savedAccounts: Saved(Operator)));
            Assert.Equal(2, signIns.Count);
            Assert.False(rig.SignInCancelledByOperator);
            rig.FileReportForFailedPreparation();
            Assert.NotNull(rig.LastConnectFailureReport);
        }

        /// <summary>
        /// The radio picker's own pass (repair 1): what <c>RemoteRadios</c> runs
        /// after initialising the API, which is <c>setupRemote</c> with no
        /// radio's account, and the outcome the picker's discovery callback
        /// then hands its pending connect. Called directly so the API and its
        /// discovery never start here. A cancel is the cancel; any other empty
        /// sign-in is a failed pass, as before.
        /// </summary>
        [Theory]
        [MemberData(nameof(OperatorCancels))]
        [MemberData(nameof(EndingsThatAreNotCancels))]
        public void The_pickers_own_pass_hands_back_a_cancel_only_when_the_operator_cancelled(
            DialogResult native, DialogResult? browser, bool closedByOperator)
        {
            bool cancel = SmartLinkSignInCancel.ShouldSay(native, browser, closedByOperator);
            var signIns = new List<string>();
            var rig = RigWhoseSignInEnds(native, browser, closedByOperator, signIns);
            var setupRemote = typeof(FlexBase).GetMethod("setupRemote",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(setupRemote);

            bool live = (bool)setupRemote!.Invoke(rig, new object?[] { true, null })!;

            Assert.False(live);
            Assert.Single(signIns);                       // positive control: the sign-in was reached
            Assert.Equal(cancel, rig.SignInCancelledByOperator);
            Assert.Equal(cancel ? RemotePassOutcome.SignInCancelled : RemotePassOutcome.NotLive, rig.RemotePassOutcome);
        }

        /// <summary>
        /// The walk's half of a sign-in nobody was offered: with interactive
        /// sign-in off, no window, no cancel, and the failure is filed.
        /// </summary>
        [Fact]
        public void With_no_sign_in_offered_there_is_no_cancel()
        {
            var signIns = new List<string>();
            var rig = RigWhoseSignInEnds(DialogResult.Cancel, null, false, signIns);

            Assert.False(rig.PrepareSmartLinkForSerial(Radio6300, allowInteractiveLogin: false, savedAccounts: Saved(Operator)));

            Assert.Empty(signIns);
            Assert.False(rig.SignInCancelledByOperator);
            rig.FileReportForFailedPreparation();
            Assert.NotNull(rig.LastConnectFailureReport);
        }
    }
}
