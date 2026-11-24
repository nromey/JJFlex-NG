#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// "Login cancelled" from both of the SmartLink sign-in windows a connect
    /// opens (#1244, ruled by Noel 2026-11-23): JJ Flexible's own "SmartLink
    /// Sign In" form and the browser page after it. Said only when the
    /// operator closed the window; never after an error box, a failed token
    /// exchange or a failure on the form's own thread, and never when the
    /// operator chose the browser instead.
    /// </summary>
    /// <remarks>
    /// The decision is <see cref="SmartLinkSignInCancel.ShouldSay"/>, tested
    /// pure. Neither window is ever constructed here (the browser page starts
    /// WebView2); where each records its close, and where
    /// <c>PerformNewLogin</c> asks, is pinned by source.
    /// </remarks>
    public sealed class SmartLinkSignInCancelTests
    {
        [Fact]
        public void The_operator_cancelling_the_native_form_says_it()
        {
            Assert.True(SmartLinkSignInCancel.ShouldSay(DialogResult.Cancel, null, false));
        }

        [Fact]
        public void A_failure_on_the_native_forms_thread_says_nothing()
        {
            Assert.False(SmartLinkSignInCancel.ShouldSay(DialogResult.Abort, null, false));
        }

        [Fact]
        public void Signing_in_says_nothing()
        {
            Assert.False(SmartLinkSignInCancel.ShouldSay(DialogResult.OK, null, false));
            Assert.False(SmartLinkSignInCancel.ShouldSay(DialogResult.Retry, DialogResult.OK, false));
            Assert.False(SmartLinkSignInCancel.ShouldSay(DialogResult.Retry, DialogResult.OK, true));
        }

        [Fact]
        public void Choosing_the_browser_instead_is_not_a_cancel()
        {
            // The native form's Retry alone, before the page has closed.
            Assert.False(SmartLinkSignInCancel.ShouldSay(DialogResult.Retry, null, false));
        }

        [Fact]
        public void The_operator_closing_the_browser_page_says_it()
        {
            Assert.True(SmartLinkSignInCancel.ShouldSay(DialogResult.Retry, DialogResult.Cancel, true));
        }

        [Theory]
        [InlineData(DialogResult.Cancel)]   // an error box, or a failed token exchange
        [InlineData(DialogResult.Abort)]    // WebView2 could not start
        public void A_browser_page_closed_by_anything_but_the_operator_says_nothing(DialogResult result)
        {
            Assert.False(SmartLinkSignInCancel.ShouldSay(DialogResult.Retry, result, false));
        }

        private static string Source(string relative) =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), relative)).Replace("\r\n", "\n");

        [Fact]
        public void The_native_forms_thread_failure_is_Abort_not_Cancel()
        {
            string wan = Source("Radios/FlexBase.Wan.cs");
            string show = SourceMember.CSharp(wan, "ShowNativeLoginDialog(SmartLinkAccount expectedAccount, bool forceNewLogin)");
            int caught = show.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
            Assert.True(caught > 0);
            string handler = show.Substring(caught);
            Assert.Contains("dr = DialogResult.Abort;", handler, StringComparison.Ordinal);
            Assert.DoesNotContain("dr = DialogResult.Cancel;", handler, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every way PerformNewLogin ends without a token goes through ONE
        /// method, SignInEndedWithoutAToken. Since repair 1 the test seam stands
        /// in for the two WINDOWS only, so the execution tests in
        /// SmartLinkSignInPlanTests.SignInCancel and RegistrationHoldTests run
        /// the method's own two endings: a <c>false &amp;&amp;</c> in front of
        /// either (Codex's note) now turns a cancel test red by running it.
        /// What is left to pin is that the seam never builds either window, and
        /// that nothing else in the method speaks or decides.
        /// </summary>
        [Fact]
        public void PerformNewLogin_ends_every_window_without_a_token_through_the_one_method()
        {
            string wan = Source("Radios/FlexBase.Wan.cs");
            string perform = SourceMember.CSharp(wan, "private string PerformNewLogin(");
            Assert.Contains("return SignInEndedWithoutAToken(native.result, null, false);", perform, StringComparison.Ordinal);
            Assert.Contains("return SignInEndedWithoutAToken(DialogResult.Retry, dialogResult, operatorClosed);", perform, StringComparison.Ordinal);
            Assert.Contains("bool operatorClosed = windowsForTest?.browserClosedByOperator ?? form.ClosedByOperator;", perform, StringComparison.Ordinal);
            // The seam replaces the windows and never builds one: the native
            // form is not shown and the browser page is not constructed.
            Assert.Contains("var windowsForTest = SignInWindowsForTest?.Invoke(expectedAccount);", perform, StringComparison.Ordinal);
            Assert.Contains(": ShowNativeLoginDialog(expectedAccount, forceNewLogin);", perform, StringComparison.Ordinal);
            Assert.Contains("var form = windowsForTest == null ? new AuthFormWebView2() : null;", perform, StringComparison.Ordinal);
            Assert.Single(perform.Split("new AuthFormWebView2()").Skip(1));
            Assert.Single(perform.Split("ShowNativeLoginDialog(").Skip(1));
            // The seam offers no sign-in that signs in, so it can never reach
            // FinishInteractiveLogin and save an account.
            Assert.Equal(2, perform.Split("throw new InvalidOperationException(\"SignInWindowsForTest offers no sign-in that signs in.\");").Length - 1);
            // Exactly two endings without a token, and the decision and the
            // words live only in the one method.
            Assert.Equal(2, perform.Split("SignInEndedWithoutAToken(").Length - 1);
            Assert.DoesNotContain("SmartLinkSignInCancel.", perform, StringComparison.Ordinal);
            Assert.DoesNotContain("login_cancelled", perform, StringComparison.Ordinal);
            // Each sign-in decides its own cancel, before either window.
            int clear = perform.IndexOf("SignInCancelledByOperator = false;", StringComparison.Ordinal);
            int native = perform.IndexOf("ShowNativeLoginDialog(", StringComparison.Ordinal);
            Assert.True(clear > 0 && clear < native, "a sign-in must clear the earlier cancel before its windows");

            string ended = SourceMember.CSharp(wan, "internal string SignInEndedWithoutAToken(");
            Assert.Contains("SmartLinkSignInCancel.ShouldSay(nativeResult, browserResult, browserClosedByOperator)", ended, StringComparison.Ordinal);
            Assert.Single(ended.Split("SmartLinkSignInCancel.Say();").Skip(1));
        }

        // ------------------------------------------------------------------
        // The callers' rules (#1244), pure. Each: a cancel yields the cancel
        // outcome and no failure; an error yields the failure as before.
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SetupRemote_files_nothing_after_a_cancelled_sign_in_whatever_came_before(bool authFailedFirst)
        {
            // The initial no-token path has no server answer at all; the
            // re-login after AuthFailed has one. Neither files after a cancel.
            SmartLinkConnectResult? result = authFailedFirst ? SmartLinkConnectResult.AuthFailed : null;
            Assert.Null(FlexBase.SetupRemoteFailureReport(result, signInCancelled: true, "op@example.com"));
            Assert.Null(FlexBase.SetupRemoteFailureReport(SmartLinkConnectResult.ConnectFailed, signInCancelled: true, "op@example.com"));
        }

        [Fact]
        public void SetupRemote_still_files_a_refused_sign_in_and_an_unreachable_server()
        {
            var auth = FlexBase.SetupRemoteFailureReport(SmartLinkConnectResult.AuthFailed, signInCancelled: false, "op@example.com");
            Assert.NotNull(auth);
            Assert.Equal(ConnectFailureClass.AuthenticationFailed, auth!.Class);
            Assert.Equal("SmartLink did not accept the sign-in for op@example.com. Signing in again from the SmartLink account manager is the fix.", auth.SpokenSummary);

            var server = FlexBase.SetupRemoteFailureReport(SmartLinkConnectResult.ConnectFailed, signInCancelled: false, null);
            Assert.NotNull(server);
            Assert.Equal(ConnectFailureClass.SessionSetupFailed, server!.Class);
            Assert.StartsWith("Could not reach the SmartLink server, or it stopped answering.", server.SpokenSummary, StringComparison.Ordinal);

            // No result, or one setupRemote files elsewhere, files nothing here.
            Assert.Null(FlexBase.SetupRemoteFailureReport(null, false, "op@example.com"));
            Assert.Null(FlexBase.SetupRemoteFailureReport(SmartLinkConnectResult.Success, false, "op@example.com"));
            Assert.Null(FlexBase.SetupRemoteFailureReport(SmartLinkConnectResult.NoRadios, false, "op@example.com"));
        }

        [Fact]
        public void ReconnectRemote_files_no_fallback_after_a_cancelled_sign_in()
        {
            Assert.Null(FlexBase.PreparationFallbackReport(null, signInCancelled: true));
        }

        [Fact]
        public void ReconnectRemote_still_files_its_fallback_after_any_other_empty_sign_in()
        {
            var fallback = FlexBase.PreparationFallbackReport(null, signInCancelled: false);
            Assert.NotNull(fallback);
            Assert.Equal(ConnectFailureClass.SessionSetupFailed, fallback!.Class);
            Assert.Equal("Could not sign in to SmartLink or reach its server, so the radio was never contacted.", fallback.SpokenSummary);
            // A report filed underneath stands; no fallback over it.
            var filed = new ConnectFailureReport { Class = ConnectFailureClass.RadioNotFound, SpokenSummary = "x" };
            Assert.Null(FlexBase.PreparationFallbackReport(filed, signInCancelled: false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Auto_connect_stops_after_a_cancelled_sign_in_and_says_nothing(bool lastLeg)
        {
            var after = FlexBase.AutoConnectAfterSmartLinkLegFailed(signInCancelled: true, cancelRequested: false, lastLeg);
            Assert.True(after.stop, "the walk must not try the next leg after the operator cancelled the sign-in");
            Assert.False(after.sayUnreachable);
        }

        [Fact]
        public void Auto_connect_walks_on_after_any_other_SmartLink_failure_as_before()
        {
            // Another leg follows: "could not reach" is said, then the next leg.
            Assert.Equal((false, true), FlexBase.AutoConnectAfterSmartLinkLegFailed(false, false, lastLeg: false));
            // The last leg: nothing here; the chain's own verdict follows.
            Assert.Equal((false, false), FlexBase.AutoConnectAfterSmartLinkLegFailed(false, false, lastLeg: true));
            // The connecting window's cancel: quiet, and the next leg's top check returns.
            Assert.Equal((false, false), FlexBase.AutoConnectAfterSmartLinkLegFailed(false, true, lastLeg: false));
        }

        [Fact]
        public void Registration_after_a_cancelled_sign_in_is_skipped_not_failed()
        {
            Assert.Equal(GuardedOutcome.Skipped, FlexBase.RegistrationWithoutAToken(signInCancelled: true));
            Assert.Equal(GuardedOutcome.Failed, FlexBase.RegistrationWithoutAToken(signInCancelled: false));
        }

        // ------------------------------------------------------------------
        // The glue the rules cannot run: each caller asks its rule with the
        // rig's own cancel. The VB walk and the WPF window are read by source;
        // there is no windowless way to run them.
        // ------------------------------------------------------------------

        [Fact]
        public void The_radio_layers_callers_ask_their_rules_with_the_rigs_cancel()
        {
            string wan = Source("Radios/FlexBase.Wan.cs");
            string setup = SourceMember.CSharp(wan, "private bool setupRemote(");
            Assert.Contains("SetupRemoteFailureReport(connectResult, SignInCancelledByOperator, _currentAccount?.Email)", setup, StringComparison.Ordinal);
            Assert.DoesNotContain("did not accept the sign-in", setup, StringComparison.Ordinal);
            int clear = setup.IndexOf("SignInCancelledByOperator = false;", StringComparison.Ordinal);
            int firstSignIn = setup.IndexOf("GetJwtFromSavedAccount(", StringComparison.Ordinal);
            Assert.True(clear > 0 && clear < firstSignIn, "setupRemote must start its pass with no cancel");

            string filing = SourceMember.CSharp(wan, "internal void FileReportForFailedPreparation(");
            Assert.Contains("PreparationFallbackReport(LastConnectFailureReport, SignInCancelledByOperator)", filing, StringComparison.Ordinal);

            string flexBase = Source("Radios/FlexBase.cs");
            string reconnect = SourceMember.CSharp(flexBase, "public bool ReconnectRemote(");
            int prepared = reconnect.IndexOf("if (!remoteOk)", StringComparison.Ordinal);
            Assert.True(prepared > 0);
            string failedBranch = SourceMember.CSharpAt(reconnect, prepared);
            Assert.Contains("FileReportForFailedPreparation();", failedBranch, StringComparison.Ordinal);
            Assert.DoesNotContain("Could not sign in to SmartLink", reconnect, StringComparison.Ordinal);
            Assert.True(reconnect.IndexOf("SignInCancelledByOperator = false;", StringComparison.Ordinal)
                        < reconnect.IndexOf("PrepareSmartLinkForSerial(", StringComparison.Ordinal));

            string connect = SourceMember.CSharp(flexBase, "public bool Connect(string serial, bool lowBW, bool preferWanPath = false)");
            Assert.Contains("SignInCancelledByOperator = false;", connect, StringComparison.Ordinal);

            string auto = SourceMember.CSharp(flexBase, "public bool TryAutoConnect(AutoConnectConfig config");
            Assert.Contains("var afterLeg = AutoConnectAfterSmartLinkLegFailed(SignInCancelledByOperator, _cancelRequested, lastLeg);", auto, StringComparison.Ordinal);
            int stop = auto.IndexOf("if (afterLeg.stop)", StringComparison.Ordinal);
            Assert.True(stop > 0);
            Assert.Contains("return false;", SourceMember.CSharpAt(auto, stop), StringComparison.Ordinal);
            Assert.True(auto.IndexOf("SignInCancelledByOperator = false;", StringComparison.Ordinal)
                        < auto.IndexOf("for (int leg = 0; leg < chain.Count; leg++)", StringComparison.Ordinal));

            string smartLink = Source("Radios/FlexBase.SmartLink.cs");
            string send = SourceMember.CSharp(smartLink, "private GuardedOutcome SendRegistrationCommand(");
            Assert.Contains("return RegistrationWithoutAToken(SignInCancelledByOperator);", send, StringComparison.Ordinal);
            Assert.True(send.IndexOf("SignInCancelledByOperator = false;", StringComparison.Ordinal)
                        < send.IndexOf("GetJwtFromSavedAccount(account)", StringComparison.Ordinal));
        }

        [Fact]
        public void The_connect_walk_treats_a_cancelled_sign_in_as_the_operator_stopping()
        {
            string globals = Source("globals.vb");

            // The walk never goes on, and the exhausted chain never retries with a sign-in.
            string walkCancelled = SourceMember.VisualBasic(globals, "Private Function WalkCancelled() As Boolean");
            Assert.Contains("rig.CancelRequested OrElse rig.SmartLinkLegCancelled OrElse rig.SignInCancelledByOperator", walkCancelled, StringComparison.Ordinal);

            string selector = SourceMember.VisualBasic(globals, "Private Sub wpfSelectorProc(");
            // The held verdict: suppressed, before PendingConnectVerdict is set.
            int verdictCancel = selector.IndexOf("ElseIf RigControl.SignInCancelledByOperator Then", StringComparison.Ordinal);
            int held = selector.IndexOf("PendingConnectVerdict = failMsg", StringComparison.Ordinal);
            Assert.True(verdictCancel > 0 && held > verdictCancel, "the verdict must be withheld after a cancelled sign-in");
            // The diagnostic offer ("The connection to ... failed").
            int offerGuard = selector.IndexOf("Not RigControl.SignInCancelledByOperator AndAlso", StringComparison.Ordinal);
            int offer = selector.IndexOf("Radios.OperationFailure.Report(", StringComparison.Ordinal);
            Assert.True(offerGuard > 0 && offer > offerGuard, "no failure offer after a cancelled sign-in");

            // The tier offer is dismissed, never put.
            string tier = SourceMember.VisualBasic(globals, "Private Function OfferHigherSmartLinkTier(");
            Assert.Contains("If rig.CancelRequested OrElse rig.SignInCancelledByOperator Then", tier, StringComparison.Ordinal);

            // The failed-open ladder never retries, and the auto-connect retry says no verdict.
            Assert.Contains("Not RigControl.CancelRequested AndAlso\n                   Not RigControl.SignInCancelledByOperator Then", globals, StringComparison.Ordinal);
            Assert.Contains("If Not rv AndAlso RigControl IsNot Nothing AndAlso RigControl.SignInCancelledByOperator Then", globals, StringComparison.Ordinal);
            Assert.Contains("giveUpRig.CancelRequested OrElse giveUpRig.SignInCancelledByOperator", globals, StringComparison.Ordinal);

            // Startup auto-connect: no Auto-Connect Failed box, on either pass.
            string startup = SourceMember.VisualBasic(globals, "Private Function TryAutoConnectOnStartup() As AutoConnectStartupResult");
            int firstCancel = startup.IndexOf("If startupRig.SignInCancelledByOperator Then", StringComparison.Ordinal);
            int box = startup.IndexOf("Radios.AutoConnectFailedDialog.ShowDialog(", StringComparison.Ordinal);
            Assert.True(firstCancel > 0 && box > firstCancel, "the failure box must not follow a cancelled sign-in");
            Assert.Contains("If tryAgainRig.SignInCancelledByOperator Then", startup, StringComparison.Ordinal);
            // The box's own Cancel, plus these two.
            Assert.Equal(3, startup.Split("Return AutoConnectStartupResult.UserCancelled").Length - 1);
        }

        [Fact]
        public void Radio_Setup_says_nothing_more_after_a_cancelled_sign_in()
        {
            string setup = Source("JJFlexWpf/Dialogs/SettingsDialog.RadioSetup.cs");
            string start = SourceMember.CSharp(setup, "private void StartRegistration(bool register, FlexBase.RegistrationCheck confirmed)");
            int skipped = start.IndexOf("else if (sent == GuardedOutcome.Skipped)", StringComparison.Ordinal);
            int failed = start.IndexOf("else if (sent != GuardedOutcome.Done)", StringComparison.Ordinal);
            Assert.True(skipped > 0 && failed > skipped, "Skipped must be answered before the failure branch");
            string branch = start.Substring(skipped, failed - skipped);
            Assert.Contains("RefreshSetupStatuses();", branch, StringComparison.Ordinal);
            Assert.DoesNotContain("Speak(", branch, StringComparison.Ordinal);
            Assert.DoesNotContain("command_not_sent", branch, StringComparison.Ordinal);
        }

        [Fact]
        public void The_browser_page_records_only_the_operators_own_close()
        {
            string form = Source("Radios/AuthFormWebView2.cs");
            string escape = SourceMember.CSharp(form, "private void CancelFromEscape(string route)");
            Assert.Contains("ClosedByOperator = true;", escape, StringComparison.Ordinal);
            string closing = SourceMember.CSharp(form, "private void AuthFormWebView2_FormClosing(object sender, FormClosingEventArgs e)");
            Assert.Contains("if (e.CloseReason == CloseReason.UserClosing) ClosedByOperator = true;", closing, StringComparison.Ordinal);
            // Set nowhere else: the error boxes and the token exchange never set it.
            Assert.Equal(2, form.Split("ClosedByOperator = true;").Length - 1);
        }
    }
}
