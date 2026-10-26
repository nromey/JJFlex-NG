using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #717, found by Noel in one minute of real use: Manage Messages
    /// (<c>CWMessageUpdateDialog</c>) said nothing when the library was empty,
    /// its only exit was a button labelled Cancel that cancelled nothing, the
    /// one path that changed something (deleting the last message) reported
    /// a cancellation, and the list's help named neither Tab nor Escape.
    /// </summary>
    /// <remarks>
    /// Read from source, in the <see cref="LeaderUnknownKeyTests"/> family:
    /// Radios.Tests cannot load the WPF assembly. The empty-state rows
    /// themselves are exercised in JJFlexWpf.Tests (ManageMessagesRowsTests),
    /// windowless. None of this is a substitute for pressing the keys; the
    /// dialog's whole history is that it compiled and reviewed clean.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ManageMessagesDialogTests
    {
        private const string Xaml = "JJFlexWpf/Dialogs/CWMessageUpdateDialog.xaml";
        private const string CodeBehind = "JJFlexWpf/Dialogs/CWMessageUpdateDialog.xaml.cs";

        private static string Source(string file) =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), file));

        [Fact]
        public void The_way_out_is_a_Close_button_and_Escape_still_works()
        {
            string xaml = Source(Xaml);
            Assert.DoesNotContain("_Cancel", xaml);
            Assert.DoesNotContain("AutomationProperties.Name=\"Cancel\"", xaml);

            Match close = Regex.Match(xaml, "<Button x:Name=\"CloseButton\"[^>]*>", RegexOptions.Singleline);
            Assert.True(close.Success, "no Close button in the dialog");
            Assert.Contains("Content=\"_Close\"", close.Value);
            Assert.Contains("IsCancel=\"True\"", close.Value);          // Escape closes
            Assert.Contains("AutomationProperties.Name=\"Close\"", close.Value);
            Assert.Contains("AutomationProperties.AcceleratorKey=\"Alt+C\"", close.Value);

            // Close, not OK: every edit is saved when its own step returns.
            Assert.DoesNotContain("Content=\"_OK\"", xaml);
        }

        [Fact]
        public void Nothing_in_the_dialog_is_recited_on_focus_as_a_description()
        {
            // Noel's bench, 2026-10-25 19:19: the list's help, set as
            // AutomationProperties.HelpText, was read on EVERY focus, even at
            // verbosity Off, because NVDA reads HelpText as the control's
            // description (memory: project_helptext_announced_on_focus). It is
            // JJFlexHelp.Text now, read on Ctrl+F1 and never on focus. Held for
            // every control in the dialog, XAML and code alike.
            string xaml = Source(Xaml);
            string code = Source(CodeBehind);
            Assert.DoesNotContain("AutomationProperties.HelpText", xaml);
            Assert.DoesNotContain("SetHelpText", code);
            Assert.DoesNotContain("HelpTextProperty", code);
        }

        [Fact]
        public void Deleting_the_last_message_does_not_report_a_cancellation()
        {
            string code = Source(CodeBehind);
            Assert.DoesNotContain("DialogResult = false", code);
            Assert.DoesNotContain("DialogResult=false", code);
        }

        [Fact]
        public void The_list_says_when_it_is_empty_and_names_the_way_out()
        {
            string code = Source(CodeBehind);
            Assert.Contains("RowsFor(labels, Radios.Lexicon", code);
            Assert.Contains("settings.cw_message.list_empty", code);
            Assert.Contains("JJFlexHelp.SetText(KeysList, Radios.Lexicon", code);
            Assert.Contains("settings.cw_message.list_help", code);

            Lexicon.Forget();
            Lexicon.Load(Lexicon.Partitions);
            string help = Lexicon.Get("settings.cw_message.list_help");
            // What the row carries is still named: label, key and payload kind
            // are on the row on purpose (Sprint 48 Track A).
            Assert.Contains("each message with the key that sends it and what it carries: CW text, a recording, or both.", help);
            Assert.Contains("Tab", help);
            Assert.Contains("Escape", help);
            string empty = Lexicon.Get("settings.cw_message.list_empty");
            Assert.Contains("Alt+A", empty);
            Assert.Contains("Escape", empty);
        }
    }
}
