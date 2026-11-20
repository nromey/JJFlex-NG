#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// An in-app update closes the app, in the right order (#1204, junk box
    /// batch 16, repair 1): download and stage only; on the Settings route
    /// apply Settings' edits; ask the exit questions; and only on go launch
    /// the helper or installer and close the app, without asking the
    /// questions a second time.
    /// </summary>
    /// <remarks>
    /// Source test: the dialog and its callers are WPF, the shell and the exit
    /// are VB, and the updater starts processes; none of it can be run here
    /// without a window or a launch. The order itself is run windowless in
    /// JJFlexWpf.Tests (<c>UpdateExitCancelledTests</c>).
    /// </remarks>
    public class UpdateCloseTests
    {
        private static string Source(string relative) =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), relative)).Replace("\r\n", "\n");

        private static string Body(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " is missing");
            int brace = src.IndexOf('{', start);
            int depth = 0;
            for (int i = brace; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src.Substring(start, i - start + 1);
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
        }

        /// <summary>A VB member's text, from its signature to the next <c>End Function</c>.</summary>
        private static string VbBody(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " is missing");
            int end = src.IndexOf("End Function", start, StringComparison.Ordinal);
            Assert.True(end > start, "no End Function after " + signature);
            return src.Substring(start, end - start);
        }

        private static int Count(string text, string what) =>
            Regex.Matches(text, Regex.Escape(what)).Count;

        [Fact]
        public void The_update_dialog_downloads_and_launches_nothing()
        {
            string dialog = Source("JJFlexWpf/Dialogs/UpdateAvailableDialog.xaml.cs");
            Assert.DoesNotContain("Application.Current", dialog, StringComparison.Ordinal);
            Assert.DoesNotContain(".Shutdown(", dialog, StringComparison.Ordinal);
            string click = Body(dialog, "private async void DownloadAndInstallButton_Click(");
            Assert.Contains(".PrepareAsync(plan, _sink, _cts.Token)", click, StringComparison.Ordinal);
            foreach (string launch in new[] { "ExecuteAsync", "ExecuteFullBundleAsync", ".Launch(", "LaunchAndDetach", "LaunchSilentAndDetach" })
                Assert.DoesNotContain(launch, click, StringComparison.Ordinal);
            // "Update is ready. Closing..." is said only after go, by the app.
            Assert.DoesNotContain("connect.update.helper_handoff", dialog, StringComparison.Ordinal);
            Assert.DoesNotContain("connect.update.installer_running", dialog, StringComparison.Ordinal);
            // Positive control: the success path is still where this looked.
            Assert.Contains("DialogResult = true;", click, StringComparison.Ordinal);
        }

        [Fact]
        public void Preparing_an_update_launches_nothing_and_launching_is_its_own_step()
        {
            string service = Source("JJFlexUpdater/UpdaterService.cs");
            foreach (string sig in new[] { "public async Task<PreparedUpdate> PrepareAsync(",
                                           "public async Task<PreparedUpdate> PrepareFullBundleAsync(" })
            {
                string body = Body(service, sig);
                foreach (string launch in new[] { "LaunchAndDetach", "LaunchSilentAndDetach", ".Launch(" })
                    Assert.DoesNotContain(launch, body, StringComparison.Ordinal);
            }
            string launchStep = Body(service, "public UpdateExecutionResult Launch(");
            Assert.Contains("HelperLauncher.LaunchAndDetach(", launchStep, StringComparison.Ordinal);
            Assert.Contains("InstallerLauncher.LaunchSilentAndDetach(", launchStep, StringComparison.Ordinal);
            // Positive control: a missing helper is still caught before any
            // question, where the fallback to the full installer can download.
            Assert.Contains("throw new HelperLaunchException(", Body(service, "public async Task<PreparedUpdate> PrepareAsync("),
                StringComparison.Ordinal);
        }

        [Fact]
        public void The_menu_check_installs_on_success_and_only_on_success()
        {
            string menu = Source("JJFlexWpf/NativeMenuBar.cs");
            string check = Body(menu, "private async System.Threading.Tasks.Task RunUpdateCheckAsync(");
            Assert.Contains("if (dialog.ShowDialog() == true && dialog.Prepared != null)\n                _window.InstallDownloadedUpdate(dialog.Prepared);",
                check, StringComparison.Ordinal);
            Assert.Equal(1, Count(check, "InstallDownloadedUpdate"));
        }

        [Fact]
        public void The_automatic_prompt_installs_on_success_and_only_on_success()
        {
            string auto = Source("JJFlexWpf/MainWindow.AutoUpdate.cs");
            string result = Body(auto, "private void OnBackgroundUpdateResult(");
            Assert.Contains("if (dialog.ShowDialog() == true && dialog.Prepared != null)\n                InstallDownloadedUpdate(dialog.Prepared);",
                result, StringComparison.Ordinal);
            Assert.Equal(1, Count(result, "InstallDownloadedUpdate"));
        }

        [Fact]
        public void Settings_applies_its_edits_as_OK_does_before_it_hands_the_update_on()
        {
            string settings = Source("JJFlexWpf/Dialogs/SettingsDialog.Updates.cs");
            string check = Body(settings, "private async Task RunManualCheckAsync(");
            int shown = check.IndexOf("if (dialog.ShowDialog() == true && dialog.Prepared != null)", StringComparison.Ordinal);
            Assert.True(shown > 0, "Settings' Check now must act only on a downloaded update");
            int apply = check.IndexOf("applyPendingEdits: () => ApplyAllSettings(closing: true)", shown, StringComparison.Ordinal);
            int marked = check.IndexOf("DownloadedUpdate = prepared;", shown, StringComparison.Ordinal);
            Assert.True(apply > shown && marked > apply, "the edits are applied through OK's own commit, then the update is marked");
            Assert.Contains("connect.update.settings_need_fixing", check, StringComparison.Ordinal);
            // Settings never installs or closes the app itself: from inside its
            // modal loop that would run before Settings has gone.
            Assert.DoesNotContain("InstallDownloadedUpdate(", check, StringComparison.Ordinal);

            // The decision: a failed apply hands nothing on.
            string decide = Body(settings, "internal static bool HandOffDownloadedUpdate(");
            int failed = decide.IndexOf("if (!applyPendingEdits())", StringComparison.Ordinal);
            int handed = decide.IndexOf("markAndClose();", StringComparison.Ordinal);
            Assert.True(failed > 0 && handed > failed, "the apply is checked before the update is handed on");

            string menu = Source("JJFlexWpf/NativeMenuBar.cs");
            string open = Body(menu, "private void ShowSettingsDialog(");
            int modal = open.IndexOf("dialog.ShowDialog();", StringComparison.Ordinal);
            int after = open.IndexOf("if (dialog.DownloadedUpdate != null)\n            _window.InstallDownloadedUpdate(dialog.DownloadedUpdate);", StringComparison.Ordinal);
            Assert.True(modal > 0 && after > modal, "the update installs after Settings' ShowDialog has returned");
        }

        [Fact]
        public void The_install_is_posted_and_asks_before_it_launches_and_launches_before_it_closes()
        {
            string auto = Source("JJFlexWpf/MainWindow.AutoUpdate.cs");
            string post = Body(auto, "public void InstallDownloadedUpdate(");
            Assert.Contains("Dispatcher.BeginInvoke(", post, StringComparison.Ordinal);
            Assert.Contains("InstallAndClose(() => prepared.Launch())", post, StringComparison.Ordinal);

            string run = Body(auto, "internal void InstallAndClose(");
            int ask = run.IndexOf("ExitQuestionsCallback?.Invoke()", StringComparison.Ordinal);
            int launch = run.IndexOf("result = launch();", StringComparison.Ordinal);
            int mark = run.IndexOf("_exitQuestionsAnswered = ExitQuestionsCallback != null;", StringComparison.Ordinal);
            int close = run.IndexOf("CloseShellCallback?.Invoke();", StringComparison.Ordinal);
            int clear = run.IndexOf("_exitQuestionsAnswered = false;", StringComparison.Ordinal);
            Assert.True(ask > 0 && launch > ask && mark > launch && close > mark && clear > close,
                "ask, then launch, then mark, then close, then clear the mark");
            foreach (string forbidden in new[] { "Environment.Exit", "Application.Exit", "Application.Current", "new Application", "Shutdown(" })
            {
                Assert.DoesNotContain(forbidden, post, StringComparison.Ordinal);
                Assert.DoesNotContain(forbidden, run, StringComparison.Ordinal);
            }

            // Exactly the three routes call it.
            int calls = 0;
            foreach (string file in new[] { "JJFlexWpf/NativeMenuBar.cs", "JJFlexWpf/MainWindow.AutoUpdate.cs",
                                             "JJFlexWpf/Dialogs/SettingsDialog.Updates.cs", "JJFlexWpf/MainWindow.xaml.cs" })
                calls += Count(Source(file), "InstallDownloadedUpdate(dialog.");
            Assert.Equal(3, calls);
        }

        [Fact]
        public void The_exit_skips_only_the_questions_an_update_already_asked()
        {
            string globals = Source("globals.vb");

            // The questions alone: the same two, in the same order.
            string ask = VbBody(globals, "Friend Function AskExitQuestions() As Boolean");
            int contact = ask.IndexOf("LogEntry.optionalWrite()", StringComparison.Ordinal);
            int running = ask.IndexOf("ConfirmStillRunningAtExit()", StringComparison.Ordinal);
            Assert.True(contact > 0 && running > contact, "the unsaved contact first, then the still-running question");

            // The exit takes the one-shot mark before it would ask, and asks
            // through the same function otherwise.
            string exit = VbBody(globals, "Friend Function ExitApplication() As Boolean");
            int take = exit.IndexOf("Dim answered As Boolean = WpfMainWindow IsNot Nothing AndAlso WpfMainWindow.TakeExitQuestionsAnswered()", StringComparison.Ordinal);
            int gate = exit.IndexOf("If answered Then", StringComparison.Ordinal);
            int asks = exit.IndexOf("ElseIf Not AskExitQuestions() Then", StringComparison.Ordinal);
            Assert.True(take > 0 && gate > take && asks > gate,
                "the mark is taken first, and the questions are asked only when it was not set");
            Assert.Equal(1, Count(exit, "AskExitQuestions()"));
            Assert.DoesNotContain("LogEntry.optionalWrite()", exit, StringComparison.Ordinal);
            Assert.DoesNotContain("ConfirmStillRunningAtExit()", exit, StringComparison.Ordinal);
            // Positive control: everything after the questions still runs.
            Assert.Contains("FinalizeTraceForShutdown(", exit, StringComparison.Ordinal);

            string events = Source("ApplicationEvents.vb");
            Assert.Contains("WpfMainWindow.ExitQuestionsCallback = AddressOf AskExitQuestions", events, StringComparison.Ordinal);
            Assert.Contains("WpfMainWindow.AppExitCallback = AddressOf ExitApplication", events, StringComparison.Ordinal);

            // The mark is consumed by reading it.
            string take1 = Body(Source("JJFlexWpf/MainWindow.AutoUpdate.cs"), "public bool TakeExitQuestionsAnswered(");
            Assert.Contains("_exitQuestionsAnswered = false;", take1, StringComparison.Ordinal);
        }

        /// <summary>
        /// Any close of the update dialog cancels its download (#1216), and
        /// once the window has closed the download's end touches neither the
        /// result nor a box. Escape closes the window from the base dialog's
        /// own handler and never clicks Cancel, so the cancel has to live in
        /// the close itself.
        /// </summary>
        [Fact]
        public void Any_close_of_the_update_dialog_cancels_the_download_and_reports_nothing_after()
        {
            string dialog = Source("JJFlexWpf/Dialogs/UpdateAvailableDialog.xaml.cs");
            string closed = Body(dialog, "protected override void OnClosed(");
            Assert.Contains("_windowClosed = true;", closed, StringComparison.Ordinal);
            Assert.Contains("_cts?.Cancel();", closed, StringComparison.Ordinal);

            string click = Body(dialog, "private async void DownloadAndInstallButton_Click(");
            // Every end of the download asks first, before the result or a box.
            Assert.True(Count(click, "if (EndAfterClose()) return;") >= 4, "every end of the download checks for a closed window");
            int prepared = click.IndexOf(".PrepareAsync(plan, _sink, _cts.Token)", StringComparison.Ordinal);
            int check = click.IndexOf("if (EndAfterClose()) return;", prepared, StringComparison.Ordinal);
            int result = click.IndexOf("DialogResult = true;", StringComparison.Ordinal);
            Assert.True(prepared > 0 && check > prepared && result > check, "the closed check sits between the download and the result");
            int failed = click.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
            int failedCheck = click.IndexOf("if (EndAfterClose()) return;", failed, StringComparison.Ordinal);
            int box = click.IndexOf("AdvisoryDialog.Show(", failed, StringComparison.Ordinal);
            Assert.True(failed > 0 && failedCheck > failed && box > failedCheck, "the closed check comes before the failure box");

            // Fixed in this dialog, not in the base class every dialog shares.
            string baseDialog = Source("JJFlexWpf/JJFlexDialog.cs");
            Assert.Contains("private void JJFlexDialog_PreviewKeyDown(", baseDialog, StringComparison.Ordinal);
            Assert.DoesNotContain("_cts", baseDialog, StringComparison.Ordinal);
            // Positive control: the Cancel button still cancels as it did.
            Assert.Contains("_cts?.Cancel();", Body(dialog, "private void CancelButton_Click("), StringComparison.Ordinal);
        }
    }
}
