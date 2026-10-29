using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #814: with no log file, Logging mode says so and offers to open or
    /// create one, and a cancelled choice does not come back on every field
    /// jump. Ruled by Noel 2026-10-23 13:37-13:38.
    /// </summary>
    /// <remarks>
    /// The rule is <see cref="LogFilePromptGate"/>, tested as behaviour. The
    /// wiring lives in the VB logger, which no test project loads, so it is
    /// pinned from source.
    /// </remarks>
    public sealed class NoLogFileChoiceTests
    {
        // ── The rule ────────────────────────────────────────────────────

        [Fact]
        public void A_missing_log_is_asked_about_the_first_time()
        {
            var gate = new LogFilePromptGate();
            Assert.True(gate.ShouldAsk(""));
            Assert.True(gate.ShouldAsk(@"C:\logs\gone.jrl"));
        }

        [Fact]
        public void A_cancelled_choice_is_not_asked_again_on_the_next_attempt()
        {
            var gate = new LogFilePromptGate();
            gate.Declined("");
            // Every Alt+letter field jump tries to open the log again.
            for (int jump = 0; jump < 5; jump++)
                Assert.False(gate.ShouldAsk(""));
            Assert.True(gate.IsDeclined(null));
        }

        [Fact]
        public void An_explicit_log_action_asks_again()
        {
            var gate = new LogFilePromptGate();
            gate.Declined(@"C:\logs\gone.jrl");
            gate.AskAgain();
            Assert.True(gate.ShouldAsk(@"C:\logs\gone.jrl"));
        }

        [Fact]
        public void A_different_missing_file_is_a_different_question()
        {
            var gate = new LogFilePromptGate();
            gate.Declined(@"C:\logs\gone.jrl");
            Assert.False(gate.ShouldAsk(@"C:\LOGS\GONE.JRL"));
            Assert.True(gate.ShouldAsk(@"C:\logs\other.jrl"));
        }

        [Fact]
        public void Choosing_a_file_clears_the_cancel()
        {
            var gate = new LogFilePromptGate();
            gate.Declined("");
            gate.Chosen();
            Assert.True(gate.ShouldAsk(""));
        }

        // ── The wiring, from source ─────────────────────────────────────

        private static string Read(string relative) =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), relative));

        private static string Between(string source, string from, string to)
        {
            int start = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, "not found: " + from);
            int end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
            Assert.True(end > start, "end not found after: " + from);
            return source.Substring(start, end - start);
        }

        [Fact]
        public void Opening_with_no_log_file_offers_the_choice_not_Log_Characteristics()
        {
            string open = Between(Read("LogClass.vb"), "Friend Function Open(", "End Function");
            Assert.DoesNotContain("LogCharacteristics.ShowDialog", open);
            Assert.Contains("ChooseLogFile(operater)", open);
        }

        [Fact]
        public void The_choice_asks_the_gate_before_it_shows_anything_and_records_a_cancel()
        {
            string choose = Between(Read("LogClass.vb"), "Private Function ChooseLogFile(", "End Function");
            int ask = choose.IndexOf("filePrompt.ShouldAsk(Name)", StringComparison.Ordinal);
            int show = choose.IndexOf("TaskDialog.ShowDialog(page)", StringComparison.Ordinal);
            Assert.True(ask >= 0 && show > ask, "the gate must be asked before the choice is shown");
            Assert.Contains("filePrompt.Declined(Name)", choose);
            Assert.Contains("TaskDialogButton.Cancel", choose);
            Assert.Contains(".AllowCancel = True", choose);
        }

        [Fact]
        public void Both_dialogs_belong_to_the_main_window_so_closing_one_does_not_announce_another_program()
        {
            // #850: unowned, each dialog's closing let Windows activate the window
            // behind the app, and NVDA said "for-noel - File Explorer" before
            // focus came back.
            string log = Read("LogClass.vb");
            string choose = Between(log, "Private Function ChooseLogFile(", "End Function");
            Assert.Contains("TaskDialog.ShowDialog(owner, page)", choose);
            Assert.Contains("= LogDialogOwner()", choose);
            string ask = Between(log, "Private Function AskForFile(", "End Function");
            Assert.Contains("dialog.ShowDialog(LogDialogOwner())", ask);
            Assert.DoesNotContain("dialog.ShowDialog()", ask);
            string owner = Between(log, "Private Shared Function LogDialogOwner(", "End Function");
            Assert.Contains("AppShellForm", owner);
            Assert.Contains("InvokeRequired", owner);
        }

        [Fact]
        public void A_field_jump_after_a_cancel_says_why_before_the_window_changes()
        {
            string bring = Between(Read("globals.vb"), ".BringUpLogForm = Sub(adifTag)", "End Sub,");
            int check = bring.IndexOf("ContactLog.LogFileDeclined", StringComparison.Ordinal);
            int hide = bring.IndexOf("WpfMainWindow.Visible = False", StringComparison.Ordinal);
            Assert.True(check >= 0 && hide > check,
                "a cancelled choice must be checked before the main window hides for the log form");
        }

        [Fact]
        public void Opening_the_full_log_form_asks_again()
        {
            string full = Between(Read("globals.vb"), "Friend Sub OpenFullLogEntryForm()", "End Sub");
            int again = full.IndexOf("ContactLog.AskForLogFileAgain()", StringComparison.Ordinal);
            int show = full.IndexOf("LogEntry.ShowDialog()", StringComparison.Ordinal);
            Assert.True(again >= 0 && show > again);
        }

        [Fact]
        public void The_lookup_password_never_reaches_the_trace()
        {
            string newLog = Between(Read("JJLogLib/Logs.cs"), "public static void NewLog(", "NewLoginInfo(");
            var traces = Regex.Matches(newLog, @"TraceLine\((?:[^;]*)\);");
            Assert.NotEmpty(traces);
            foreach (Match t in traces)
                Assert.DoesNotMatch(@"\+\s*password\b", t.Value);
        }
    }
}
