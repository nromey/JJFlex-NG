using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Escape in the browser sign-in form (#1222): Escape anywhere in the
    /// form, the web page included, closes it as Alt+F4 does, with
    /// DialogResult.Cancel, so the caller's existing cancel path runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> On the 2026-11-19 bench, Escape did nothing in this
    /// form: the web page held focus, keystrokes went to the browser process,
    /// and the form had no Escape handling at all. Only Alt+F4 closed it.
    /// </para>
    /// <para>
    /// <b>What these can see.</b> The decisions (which key, when a close is
    /// refused) are run directly. The wiring is read from source, because no
    /// test may construct the form: WebView2 needs a window.
    /// </para>
    /// <para>
    /// <b>What these cannot see.</b> That the WebView2 control really raises
    /// KeyDown for Escape while the page holds focus. Microsoft documents it
    /// (Escape is always an accelerator; the WinForms control raises
    /// accelerators as KeyDown), but only a bench press with the trace
    /// proves it: the trace line names the route, "page" or "form".
    /// </para>
    /// </remarks>
    public sealed class AuthFormEscapeTests
    {
        private static string Source() => File.ReadAllText(Path.Combine(IntegrationPassTree.Root,
            "Radios", "AuthFormWebView2.cs")).Replace("\r\n", "\n");

        private static string MethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"not found: {signature}");
            int open = source.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
        }

        [Fact]
        public void Plain_Escape_is_the_cancel_key_and_nothing_else_is()
        {
            Assert.True(AuthFormWebView2.IsCancelKey(Keys.Escape));
            Assert.False(AuthFormWebView2.IsCancelKey(Keys.Escape | Keys.Shift));
            Assert.False(AuthFormWebView2.IsCancelKey(Keys.Escape | Keys.Control));
            Assert.False(AuthFormWebView2.IsCancelKey(Keys.Enter));
            Assert.False(AuthFormWebView2.IsCancelKey(Keys.F4 | Keys.Alt));
        }

        [Fact]
        public void A_persons_close_is_refused_only_while_the_token_exchange_runs()
        {
            Assert.True(AuthFormWebView2.RefusesUserClose(exchangeInFlight: true, CloseReason.UserClosing));
            Assert.False(AuthFormWebView2.RefusesUserClose(exchangeInFlight: false, CloseReason.UserClosing));
            // The form's own close (setting DialogResult) is never refused.
            Assert.False(AuthFormWebView2.RefusesUserClose(exchangeInFlight: true, CloseReason.None));
            Assert.False(AuthFormWebView2.RefusesUserClose(exchangeInFlight: true, CloseReason.ApplicationExitCall));
        }

        [Fact]
        public void Escape_in_the_page_reaches_the_close_through_the_controls_KeyDown_and_is_posted()
        {
            string s = Source();
            Assert.Contains("webView.KeyDown += WebView_KeyDown;", s);

            string body = MethodBody(s, "private void WebView_KeyDown(object sender, KeyEventArgs e)");
            Assert.Contains("IsCancelKey(e.KeyData)", body);
            Assert.Contains("e.Handled = true;", body);
            // Posted, not closed inside the accelerator handler: the browser
            // process is blocked until the handler returns.
            Assert.Contains("BeginInvoke(", body);
            Assert.Contains("CancelFromEscape(\"page\")", body);
        }

        [Fact]
        public void Escape_outside_the_page_reaches_the_close_through_ProcessCmdKey()
        {
            string body = MethodBody(Source(), "protected override bool ProcessCmdKey(ref Message msg, Keys keyData)");
            Assert.Contains("IsCancelKey(keyData)", body);
            Assert.Contains("CancelFromEscape(\"form\")", body);
            Assert.Contains("return true;", body);
        }

        [Fact]
        public void Escape_closes_with_Cancel_unless_the_exchange_runs_and_speaks_nothing()
        {
            string body = MethodBody(Source(), "private void CancelFromEscape(string route)");
            Assert.Contains("RefusesUserClose(_exchangeInFlight, CloseReason.UserClosing)", body);
            Assert.Contains("DialogResult = DialogResult.Cancel;", body);
            // The refusal comes before the close.
            Assert.True(body.IndexOf("RefusesUserClose", StringComparison.Ordinal)
                        < body.IndexOf("DialogResult = DialogResult.Cancel;", StringComparison.Ordinal));
            // Escape and Alt+F4 stay identical: no sentence of its own.
            Assert.DoesNotContain("ScreenReaderOutput", body);
            Assert.DoesNotContain("Lexicon.Get", body);
        }

        [Fact]
        public void Alt_F4_asks_the_same_question_while_the_exchange_runs()
        {
            string s = Source();
            Assert.Contains("this.FormClosing += AuthFormWebView2_FormClosing;", s);
            string body = MethodBody(s, "private void AuthFormWebView2_FormClosing(object sender, FormClosingEventArgs e)");
            Assert.Contains("RefusesUserClose(_exchangeInFlight, e.CloseReason)", body);
            Assert.Contains("e.Cancel = true;", body);
        }

        [Fact]
        public void The_exchange_flag_covers_exactly_the_awaited_exchange()
        {
            string s = Source();
            var m = Regex.Match(s,
                @"_exchangeInFlight = true;\s*try\s*\{\s*success = await ExchangeCodeForTokens\(code\);\s*\}\s*finally\s*\{\s*_exchangeInFlight = false;\s*\}");
            Assert.True(m.Success, "the in-flight flag must be set around the awaited exchange and cleared in a finally");
            // The flag clears before the form sets its own result.
            int cleared = m.Index + m.Length;
            Assert.True(s.IndexOf("DialogResult = DialogResult.OK;", cleared, StringComparison.Ordinal) > cleared);
        }

        [Fact]
        public void Positive_control_the_scan_reads_the_forms_existing_cancel_paths()
        {
            // The error box, the state mismatch and the failed exchange each
            // already close with Cancel; if this count is wrong the scan is
            // not reading the file it thinks it is.
            string body = MethodBody(Source(),
                "private async void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)");
            Assert.Equal(3, Regex.Matches(body, @"DialogResult = DialogResult\.Cancel;").Count);
        }
    }
}
