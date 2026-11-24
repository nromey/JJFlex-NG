#nullable enable
using System;
using System.IO;
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

        [Fact]
        public void PerformNewLogin_asks_after_each_window_and_says_it_only_through_the_decision()
        {
            string wan = Source("Radios/FlexBase.Wan.cs");
            string perform = SourceMember.CSharp(wan, "private string PerformNewLogin(");
            Assert.Contains("SmartLinkSignInCancel.ShouldSay(native.result, null, false)", perform, StringComparison.Ordinal);
            Assert.Contains("SmartLinkSignInCancel.ShouldSay(DialogResult.Retry, dialogResult, operatorClosed)", perform, StringComparison.Ordinal);
            Assert.Contains("bool operatorClosed = form.ClosedByOperator;", perform, StringComparison.Ordinal);
            Assert.Equal(2, perform.Split("SmartLinkSignInCancel.Say();").Length - 1);
            Assert.DoesNotContain("login_cancelled", perform, StringComparison.Ordinal);
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
