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
        /// The class, not the instance: every assignment in this dialog that
        /// replaces a control's shown text is followed, in the same block, by
        /// a rename to the same text. Each assignment is judged on its own
        /// (#840's follow-up from the Codex check of 4624ee48): this used to
        /// deduplicate the control names and accept one SetName anywhere in
        /// the file, so a second, later NoButton.Content with the old name
        /// left behind would still have passed.
        /// </summary>
        [Fact]
        public void Every_control_whose_text_is_replaced_is_renamed_too()
        {
            var named = SpokenNameScan.NamedControls(Xaml());
            // The scan pairs against controls the XAML names. Yes has no XAML
            // name, but every relabel in this dialog is held to the same rule.
            named.TryAdd("YesButton", "Yes");
            var assignments = SpokenNameScan.Assignments("ConfirmActionDialog.xaml.cs", Code(), named).ToList();

            // Positive control: the three the dialog relabels today, each once.
            Assert.Equal(new[] { "DontShowAgainCheck", "YesButton", "NoButton" },
                assignments.Select(a => a.Control).ToArray());
            foreach (var a in assignments)
                Assert.True(a.Rename != null && SpokenNameScan.SameText(a.Shown, a.Rename),
                    a.Control + " (line " + a.Line + ") shows " + a.Shown
                    + " but is not renamed to the same text in the same block");
        }

        /// <summary>
        /// The plain No, with no label passed, keeps the fuller name the XAML
        /// gives it: it does cancel, and "No, cancel" still contains its label.
        /// Tied to the NoButton element itself (#840's follow-up): this used to
        /// check three strings anywhere in the files.
        /// </summary>
        [Fact]
        public void The_plain_No_keeps_its_fuller_name()
        {
            var no = SpokenNameScan.XamlControls("ConfirmActionDialog.xaml", Xaml())
                .Single(c => c.XName == "NoButton");
            Assert.Equal("_No", no.Shown);
            Assert.Equal("No, cancel", no.SpokenName);
            Assert.True(SpokenNameScan.NameContainsShown(no.SpokenName, no.Shown));

            // And the code renames it only when a caller relabels it.
            string code = Code();
            int guard = code.IndexOf("if (noLabel != null)", System.StringComparison.Ordinal);
            int relabel = code.IndexOf("NoButton.Content = noLabel;", System.StringComparison.Ordinal);
            Assert.True(guard >= 0 && relabel > guard && relabel < code.IndexOf('}', guard),
                "NoButton is relabelled only inside the noLabel != null block");
            Assert.Contains("string? noLabel = null", code);
        }

        private static string Xaml() => File.ReadAllText(Path.Combine(
            FieldKeyMapScan.RepoRoot(), "JJFlexWpf/Dialogs/ConfirmActionDialog.xaml"));
    }
}
