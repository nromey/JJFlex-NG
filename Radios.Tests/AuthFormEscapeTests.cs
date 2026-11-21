using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Escape in the browser sign-in form (#1222): Escape anywhere in the
    /// form, the web page included, closes it as Alt+F4 does, with
    /// DialogResult.Cancel, so the caller's existing cancel path runs. A close
    /// during the token exchange cancels the exchange.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> On the 2026-11-19 bench, Escape did nothing in this
    /// form: the web page held focus, keystrokes went to the browser process,
    /// and the form had no Escape handling at all. Only Alt+F4 closed it.
    /// </para>
    /// <para>
    /// <b>Repair 1.</b> The first build ignored Escape and Alt+F4 while the
    /// token exchange ran. That broke the standing rule that a close during an
    /// asynchronous wait cancels the wait: the exchange is a network request,
    /// HttpClient's default timeout is about 100 seconds, and a stalled
    /// endpoint left no way out. Now any close during the exchange cancels it,
    /// and a cancelled exchange never lands afterwards.
    /// </para>
    /// <para>
    /// <b>What these can see.</b> The key decision is run directly. The close
    /// handlers are run on an UNINITIALIZED form (no constructor, so no
    /// window, no handle, no WebView2), with the exchange's token source set
    /// as it is while the exchange is awaited. The exchange itself makes a
    /// network call, so its wiring is read from source.
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

        private static string MethodBody(string source, string signature) =>
            SourceMember.CSharpBlock(source, signature);

        private const string ExchangeSignature =
            "private async Task<bool> ExchangeCodeForTokens(string code, CancellationToken ct)";

        private const string NavigationSignature =
            "private async void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)";

        [Fact]
        public void Plain_Escape_is_the_cancel_key_and_nothing_else_is()
        {
            Assert.True(AuthFormWebView2.IsCancelKey(Keys.Escape));
            Assert.False(AuthFormWebView2.IsCancelKey(Keys.Escape | Keys.Shift));
            Assert.False(AuthFormWebView2.IsCancelKey(Keys.Escape | Keys.Control));
            Assert.False(AuthFormWebView2.IsCancelKey(Keys.Enter));
            Assert.False(AuthFormWebView2.IsCancelKey(Keys.F4 | Keys.Alt));
        }

        // ---- The close handlers, run on a form that was never constructed ----

        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>
        /// The form without its constructor: no handle, no window, no WebView2.
        /// Its finalizer is suppressed, since Dispose on a never-built
        /// control is not something to run on the finalizer thread.
        /// </summary>
        private static AuthFormWebView2 UnbuiltForm(CancellationTokenSource exchangeCts)
        {
            var form = (AuthFormWebView2)RuntimeHelpers.GetUninitializedObject(typeof(AuthFormWebView2));
            GC.SuppressFinalize(form);
            var field = typeof(AuthFormWebView2).GetField("_exchangeCts", Instance);
            Assert.NotNull(field);
            field.SetValue(form, exchangeCts);
            return form;
        }

        private static FormClosingEventArgs Close(AuthFormWebView2 form, CloseReason reason)
        {
            var handler = typeof(AuthFormWebView2).GetMethod("AuthFormWebView2_FormClosing", Instance);
            Assert.NotNull(handler);
            var e = new FormClosingEventArgs(reason, cancel: false);
            handler.Invoke(form, new object[] { null, e });
            return e;
        }

        private static void PressEscape(AuthFormWebView2 form)
        {
            var method = typeof(AuthFormWebView2).GetMethod("CancelFromEscape", Instance);
            Assert.NotNull(method);
            method.Invoke(form, new object[] { "test" });
        }

        [Theory]
        [InlineData(CloseReason.UserClosing)]          // Alt+F4, the title bar
        [InlineData(CloseReason.None)]                 // Escape's own close (setting DialogResult)
        [InlineData(CloseReason.ApplicationExitCall)]
        public void A_close_during_the_exchange_cancels_it_and_goes_ahead(CloseReason reason)
        {
            using var cts = new CancellationTokenSource();
            var form = UnbuiltForm(cts);

            var e = Close(form, reason);

            Assert.True(cts.IsCancellationRequested, "a close during the exchange must cancel it");
            Assert.False(e.Cancel, "no close is ever refused, the exchange included");
        }

        [Fact]
        public void Escape_during_the_exchange_cancels_it_and_closes_with_Cancel()
        {
            using var cts = new CancellationTokenSource();
            var form = UnbuiltForm(cts);

            PressEscape(form);

            Assert.True(cts.IsCancellationRequested, "Escape during the exchange must cancel it");
            Assert.Equal(DialogResult.Cancel, form.DialogResult);
        }

        [Fact]
        public void With_no_exchange_running_both_closes_go_ahead_exactly_as_before()
        {
            Assert.False(Close(UnbuiltForm(null), CloseReason.UserClosing).Cancel);

            var form = UnbuiltForm(null);
            PressEscape(form);
            Assert.Equal(DialogResult.Cancel, form.DialogResult);
        }

        [Fact]
        public void Positive_control_an_unbuilt_form_starts_with_no_result()
        {
            // If the unbuilt form already said Cancel, the Escape tests above
            // would pass without Escape doing anything.
            Assert.Equal(DialogResult.None, UnbuiltForm(null).DialogResult);
        }

        [Fact]
        public void No_close_key_is_ignored_anywhere_in_the_form()
        {
            string s = Source();
            Assert.DoesNotContain("e.Cancel = true", s);
            Assert.DoesNotContain("RefusesUserClose", s);
            Assert.DoesNotContain("ignored while the sign-in completes", s);
        }

        // ---- The wiring, read from source ----

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
        public void Escape_speaks_nothing_of_its_own()
        {
            // Escape and Alt+F4 stay identical: no sentence of its own.
            string body = MethodBody(Source(), "private void CancelFromEscape(string route)");
            Assert.DoesNotContain("ScreenReaderOutput", body);
            Assert.DoesNotContain("Lexicon.Get", body);
        }

        [Fact]
        public void Alt_F4_and_the_title_bar_reach_the_same_cancel()
        {
            string s = Source();
            Assert.Contains("this.FormClosing += AuthFormWebView2_FormClosing;", s);
            string body = MethodBody(s, "private void AuthFormWebView2_FormClosing(object sender, FormClosingEventArgs e)");
            Assert.Contains("CancelExchange(", body);
        }

        [Fact]
        public void The_forms_token_reaches_every_awaited_call_in_the_exchange()
        {
            string body = MethodBody(Source(), ExchangeSignature);
            Assert.Contains("client.PostAsync($\"https://{Auth0Domain}/oauth/token\", content, ct)", body);
            Assert.Contains("ReadAsStringAsync(ct)", body);
            var awaits = Regex.Matches(body, @"await [^;]*;");
            Assert.True(awaits.Count >= 2, "the scan must see the exchange's awaits");
            foreach (Match awaited in awaits)
                Assert.Contains("ct)", awaited.Value);
        }

        [Fact]
        public void Our_own_cancel_is_quiet_and_keeps_no_token()
        {
            string body = MethodBody(Source(), ExchangeSignature);

            // A close that lands after the response arrived still wins, before
            // any token is stored or any failure box shown.
            int read = body.IndexOf("ReadAsStringAsync(ct)", StringComparison.Ordinal);
            int check = body.IndexOf("ct.ThrowIfCancellationRequested();", read, StringComparison.Ordinal);
            Assert.True(read >= 0 && check > read, "the token must be checked after the last await");
            Assert.True(check < body.IndexOf("MessageBox.Show(", StringComparison.Ordinal));
            Assert.True(check < body.IndexOf("IdToken = ", StringComparison.Ordinal));

            // The quiet catch comes before the error box's catch, and shows nothing.
            const string quiet = "catch (OperationCanceledException) when (ct.IsCancellationRequested)";
            int q = body.IndexOf(quiet, StringComparison.Ordinal);
            int general = body.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
            Assert.True(q >= 0 && q < general, "our own cancel must be caught before the general catch");
            string quietBlock = MethodBody(body.Substring(q), quiet);
            Assert.DoesNotContain("MessageBox", quietBlock);
            Assert.Contains("return false;", quietBlock);
        }

        [Fact]
        public void A_cancelled_exchange_never_lands_on_the_form()
        {
            string body = MethodBody(Source(), NavigationSignature);
            var m = Regex.Match(body,
                @"_exchangeCts = exchangeCts;\s*try\s*\{\s*success = await ExchangeCodeForTokens\(code, exchangeCts\.Token\);\s*\}\s*finally\s*\{\s*_exchangeCts = null;\s*cancelled = exchangeCts\.IsCancellationRequested;\s*exchangeCts\.Dispose\(\);\s*\}");
            Assert.True(m.Success, "the token source must be set around the awaited exchange and cleared in a finally");

            int after = m.Index + m.Length;
            int guard = body.IndexOf("if (cancelled)", after, StringComparison.Ordinal);
            Assert.True(guard > after, "a cancelled exchange must be checked after the await");
            Assert.True(guard < body.IndexOf("DialogResult = DialogResult.OK;", after, StringComparison.Ordinal),
                "the check comes before the form is given OK");
            Assert.True(guard < body.IndexOf("DialogResult = DialogResult.Cancel;", after, StringComparison.Ordinal),
                "and before the failed exchange's own Cancel");
            string guardBlock = MethodBody(body.Substring(guard), "if (cancelled)");
            Assert.Contains("return;", guardBlock);
            Assert.DoesNotContain("DialogResult", guardBlock);
            Assert.DoesNotContain("MessageBox", guardBlock);
            Assert.DoesNotContain("Tokens", guardBlock);
        }

        // ---- A close during WebView2's start-up (#1245) ----

        private const string LoadSignature = "private async void AuthFormWebView2_Load(object sender, EventArgs e)";

        private static bool LoadStopped(AuthFormWebView2 form)
        {
            var method = typeof(AuthFormWebView2).GetMethod("LoadStoppedByClose", Instance);
            Assert.NotNull(method);
            return (bool)method.Invoke(form, new object[] { "test" })!;
        }

        [Theory]
        [InlineData(CloseReason.UserClosing)]          // Alt+F4, the title bar
        [InlineData(CloseReason.None)]                 // Escape's own close
        public void After_any_close_the_start_up_stops(CloseReason reason)
        {
            var form = UnbuiltForm(null);
            Close(form, reason);
            Assert.True(LoadStopped(form), "a close during start-up must stop the load handler");
        }

        [Fact]
        public void Positive_control_an_open_form_carries_on_starting()
        {
            // If the check said "stopped" on an open form, every start-up
            // would end quietly and the tests above would prove nothing.
            Assert.False(LoadStopped(UnbuiltForm(null)));
        }

        [Fact]
        public void Every_await_in_the_load_handler_is_followed_by_the_close_check()
        {
            string body = MethodBody(Source(), LoadSignature);
            var awaits = Regex.Matches(body, @"await [^;]*;");
            // CreateAsync, the retry delay, the profile's start-up, the
            // shared start-up and the cookie read.
            Assert.Equal(5, awaits.Count);
            for (int i = 0; i < awaits.Count; i++)
            {
                int from = awaits[i].Index + awaits[i].Length;
                int to = i + 1 < awaits.Count ? awaits[i + 1].Index : body.Length;
                string after = body.Substring(from, to - from);
                int check = after.IndexOf("if (LoadStoppedByClose(", StringComparison.Ordinal);
                Assert.True(check >= 0, $"no close check after: {awaits[i].Value}");
                // The check is the very next statement, not somewhere later.
                string between = after.Substring(0, check);
                Assert.True(between.Trim().Length == 0, $"something runs between the await and its check: {awaits[i].Value}");
                string checkLine = after.Substring(check).Split('\n')[0];
                Assert.EndsWith(") return;", checkLine.TrimEnd());
            }
        }

        [Fact]
        public void A_failed_profile_start_up_never_falls_through_to_the_shared_profile_after_a_close()
        {
            string body = MethodBody(Source(), LoadSignature);
            int profileCatch = body.IndexOf("using shared profile", StringComparison.Ordinal);
            int shared = body.IndexOf("if (!profileApplied)", StringComparison.Ordinal);
            Assert.True(profileCatch >= 0 && shared > profileCatch);
            Assert.Contains("if (LoadStoppedByClose(", body.Substring(profileCatch, shared - profileCatch));
        }

        [Fact]
        public void The_start_up_error_box_is_never_shown_on_a_closed_form()
        {
            string body = MethodBody(Source(), LoadSignature);
            int outer = body.LastIndexOf("catch (Exception ex)", StringComparison.Ordinal);
            Assert.True(outer >= 0);
            string handler = MethodBody(body.Substring(outer), "catch (Exception ex)");

            int closedCheck = handler.IndexOf("if (_closed || IsDisposed)", StringComparison.Ordinal);
            int box = handler.IndexOf("MessageBox.Show(", StringComparison.Ordinal);
            int abort = handler.IndexOf("DialogResult = DialogResult.Abort;", StringComparison.Ordinal);
            Assert.True(closedCheck >= 0, "the catch must test for a close");
            Assert.True(box > closedCheck && abort > closedCheck, "the test comes before the box and the Abort");

            string quiet = MethodBody(handler.Substring(closedCheck), "if (_closed || IsDisposed)");
            Assert.Contains("return;", quiet);
            Assert.DoesNotContain("MessageBox", quiet);
            Assert.DoesNotContain("DialogResult", quiet);

            // Positive control: the real failure on an open form still shows
            // the box and still closes with Abort.
            Assert.Contains("connect.smartlink.browser.init_failed_body", handler);
        }

        [Fact]
        public void Every_close_sets_the_flag_the_start_up_reads()
        {
            string body = MethodBody(Source(), "private void AuthFormWebView2_FormClosing(object sender, FormClosingEventArgs e)");
            Assert.Contains("_closed = true;", body);
            Assert.Contains("CancelExchange(", body);
        }

        [Fact]
        public void Positive_control_the_scan_reads_the_forms_existing_cancel_paths()
        {
            // The error box, the state mismatch and the failed exchange each
            // already close with Cancel; if this count is wrong the scan is
            // not reading the file it thinks it is.
            string body = MethodBody(Source(), NavigationSignature);
            Assert.Equal(3, Regex.Matches(body, @"DialogResult = DialogResult\.Cancel;").Count);
        }
    }
}
