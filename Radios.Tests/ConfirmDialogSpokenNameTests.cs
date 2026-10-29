using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The confirmation dialog's buttons are announced by the words they show.
    /// </summary>
    /// <remarks>
    /// Found on the bench 2026-10-28: the disconnect question showed "Disconnect
    /// without saving" and NVDA announced it as "No, cancel", the name the XAML
    /// gives the plain No. Yes had been given its label's name; No never had,
    /// so all thirteen callers that relabel No were announced wrongly, and the
    /// operator could not tell which button saved. Read from source because the
    /// dialog is a window, and a window is not built on a live desk.
    /// </remarks>
    public sealed class ConfirmDialogSpokenNameTests
    {
        private static string Code() => File.ReadAllText(Path.Combine(
            FieldKeyMapScan.RepoRoot(), "JJFlexWpf/Dialogs/ConfirmActionDialog.xaml.cs"));

        [Fact]
        public void A_relabelled_No_button_is_announced_by_its_label()
        {
            string code = Code();
            int content = code.IndexOf("NoButton.Content = noLabel;", System.StringComparison.Ordinal);
            Assert.True(content >= 0);
            int named = code.IndexOf("AutomationProperties.SetName(NoButton, noLabel.Replace(\"_\", \"\"));", content,
                System.StringComparison.Ordinal);
            int closes = code.IndexOf('}', content);
            Assert.True(named > content && named < closes,
                "the No button's spoken name must be set from the same label, in the same block that shows it");
        }

        /// <summary>
        /// The class, not the instance: any control in this dialog whose shown
        /// text is replaced in code gets its spoken name from the same text.
        /// </summary>
        [Fact]
        public void Every_control_whose_text_is_replaced_is_renamed_too()
        {
            string code = Code();
            var replaced = Regex.Matches(code, @"(\w+)\.Content\s*=")
                .Select(m => m.Groups[1].Value).Distinct().ToList();
            // Positive control: the three the dialog relabels today.
            Assert.Contains("YesButton", replaced);
            Assert.Contains("NoButton", replaced);
            Assert.Contains("DontShowAgainCheck", replaced);
            foreach (string name in replaced)
                Assert.True(code.Contains("AutomationProperties.SetName(" + name + ","),
                    name + "'s text is replaced in code but its spoken name is not, so the screen reader says something else");
        }

        /// <summary>
        /// The plain No, with no label passed, keeps the fuller name the XAML
        /// gives it: it does cancel, and "No, cancel" still contains its label.
        /// </summary>
        [Fact]
        public void The_plain_No_keeps_its_fuller_name()
        {
            string xaml = File.ReadAllText(Path.Combine(
                FieldKeyMapScan.RepoRoot(), "JJFlexWpf/Dialogs/ConfirmActionDialog.xaml"));
            Assert.Contains("AutomationProperties.Name=\"No, cancel\"", xaml);
            Assert.Contains("string? noLabel = null", Code());
            Assert.Contains("if (noLabel != null)", Code());
        }
    }
}
