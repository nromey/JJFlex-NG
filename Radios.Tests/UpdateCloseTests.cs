#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// An in-app update closes the app once it is handed off (#1204, junk box
    /// batch 16). The update dialog used to end with a WPF Application
    /// shutdown, which does nothing here (there is no WPF Application), and
    /// none of its three callers closed the app, so the update helper waited
    /// its 30 seconds and gave up.
    /// </summary>
    /// <remarks>
    /// Source test: the dialog and its callers are WPF and the shell is VB, and
    /// none of it can be run without a window. What the posted half does with
    /// a cancelled exit is run windowless in JJFlexWpf.Tests
    /// (<c>UpdateExitCancelledTests</c>).
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

        private static int Count(string text, string what) =>
            Regex.Matches(text, Regex.Escape(what)).Count;

        [Fact]
        public void The_update_dialog_no_longer_asks_a_missing_WPF_Application_to_shut_down()
        {
            string dialog = Source("JJFlexWpf/Dialogs/UpdateAvailableDialog.xaml.cs");
            Assert.DoesNotContain("Application.Current", dialog, StringComparison.Ordinal);
            Assert.DoesNotContain(".Shutdown(", dialog, StringComparison.Ordinal);
            // Positive control: the success path is still where this looked.
            string click = Body(dialog, "private async void DownloadAndInstallButton_Click(");
            Assert.Contains("DialogResult = true;", click, StringComparison.Ordinal);
        }

        [Fact]
        public void The_menu_check_closes_the_app_on_success_and_only_on_success()
        {
            string menu = Source("JJFlexWpf/NativeMenuBar.cs");
            string check = Body(menu, "private async System.Threading.Tasks.Task RunUpdateCheckAsync(");
            Assert.Contains("if (dialog.ShowDialog() == true)\n                _window.CloseForInstalledUpdate();", check, StringComparison.Ordinal);
            Assert.Equal(1, Count(check, "CloseForInstalledUpdate"));
        }

        [Fact]
        public void The_automatic_prompt_closes_the_app_on_success_and_only_on_success()
        {
            string auto = Source("JJFlexWpf/MainWindow.AutoUpdate.cs");
            string result = Body(auto, "private void OnBackgroundUpdateResult(");
            Assert.Contains("if (dialog.ShowDialog() == true)\n                CloseForInstalledUpdate();", result, StringComparison.Ordinal);
            Assert.Equal(1, Count(result, "CloseForInstalledUpdate"));
        }

        [Fact]
        public void Settings_closes_itself_on_success_and_its_opener_then_closes_the_app()
        {
            string settings = Source("JJFlexWpf/Dialogs/SettingsDialog.Updates.cs");
            string check = Body(settings, "private async Task RunManualCheckAsync(");
            int shown = check.IndexOf("if (dialog.ShowDialog() == true)", StringComparison.Ordinal);
            Assert.True(shown > 0, "Settings' Check now must act only on a successful hand-off");
            int handed = check.IndexOf("UpdateHandedOff = true;", shown, StringComparison.Ordinal);
            int closed = check.IndexOf("Close();", shown, StringComparison.Ordinal);
            Assert.True(handed > shown && closed > handed, "Settings marks the hand-off, then closes itself");
            Assert.Equal(1, Count(check, "UpdateHandedOff = true;"));
            // Settings never closes the app itself: from inside its modal loop
            // the close would run before Settings has gone.
            Assert.DoesNotContain("CloseForInstalledUpdate()", check, StringComparison.Ordinal);

            string menu = Source("JJFlexWpf/NativeMenuBar.cs");
            string open = Body(menu, "private void ShowSettingsDialog(");
            int modal = open.IndexOf("dialog.ShowDialog();", StringComparison.Ordinal);
            int after = open.IndexOf("if (dialog.UpdateHandedOff)\n            _window.CloseForInstalledUpdate();", StringComparison.Ordinal);
            Assert.True(modal > 0 && after > modal, "the app closes after Settings' ShowDialog has returned");
        }

        [Fact]
        public void The_close_is_posted_through_the_shells_own_close_and_nowhere_else()
        {
            string auto = Source("JJFlexWpf/MainWindow.AutoUpdate.cs");
            string post = Body(auto, "public void CloseForInstalledUpdate(");
            Assert.Contains("Dispatcher.BeginInvoke(", post, StringComparison.Ordinal);
            Assert.Contains("CloseShellForInstalledUpdate", post, StringComparison.Ordinal);
            string run = Body(auto, "internal void CloseShellForInstalledUpdate(");
            Assert.Contains("CloseShellCallback?.Invoke();", run, StringComparison.Ordinal);
            foreach (string forbidden in new[] { "Environment.Exit", "Application.Exit", "Application.Current", "new Application", "Shutdown(" })
            {
                Assert.DoesNotContain(forbidden, post, StringComparison.Ordinal);
                Assert.DoesNotContain(forbidden, run, StringComparison.Ordinal);
            }

            // Exactly the three routes call it, and only as checked above.
            int calls = 0;
            foreach (string file in new[] { "JJFlexWpf/NativeMenuBar.cs", "JJFlexWpf/MainWindow.AutoUpdate.cs",
                                             "JJFlexWpf/Dialogs/SettingsDialog.Updates.cs", "JJFlexWpf/MainWindow.xaml.cs" })
                calls += Count(Source(file), "CloseForInstalledUpdate();");
            Assert.Equal(3, calls);
        }
    }
}
