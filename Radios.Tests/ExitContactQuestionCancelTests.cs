#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The unsaved-contact question at exit offers Cancel, which keeps JJ
    /// Flexible open (#1242, ruled by Noel 2026-11-23). It was a Yes/No box,
    /// so its Cancel branch could never run and Escape did nothing.
    /// </summary>
    /// <remarks>
    /// Source-reading, as <see cref="UpdateCloseTests"/> is: the question is a
    /// real message box, and no test here may open one. The behaviour pinned is
    /// that a Cancel returns False with the entry still marked unsaved, and
    /// that Cancel is offered only on the exit route: Page Up, Page Down and
    /// Find Log Entry ignore the answer, so a Cancel there would discard the
    /// entry.
    /// </remarks>
    public sealed class ExitContactQuestionCancelTests
    {
        private static string Source(string relative) =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), relative)).Replace("\r\n", "\n");

        private static string OptionalWrite() =>
            SourceMember.VisualBasic(Source("LogEntry.vb"), "Friend Function optionalWrite(Optional offerCancel As Boolean = False) As Boolean");

        [Fact]
        public void The_question_offers_Cancel_only_when_asked_to()
        {
            string body = OptionalWrite();
            Assert.Contains("If(offerCancel, MessageBoxButtons.YesNoCancel, MessageBoxButtons.YesNo)", body, StringComparison.Ordinal);
            // The box is shown with those buttons (no Lexicon key quoted here:
            // the key-coverage scan reads test sources too).
            Assert.Matches(@"MessageBox\.Show\(.*, buttons\)", body);
        }

        [Fact]
        public void A_Cancel_returns_False_and_leaves_the_entry_marked_unsaved()
        {
            string body = OptionalWrite();
            int cancel = body.IndexOf("Case DialogResult.Cancel", StringComparison.Ordinal);
            int stay = body.IndexOf("Return False", cancel, StringComparison.Ordinal);
            int endSelect = body.IndexOf("End Select", cancel, StringComparison.Ordinal);
            int cleared = body.IndexOf("NeedsWrite = False", StringComparison.Ordinal);
            Assert.True(cancel > 0 && stay > cancel && stay < endSelect,
                "Cancel returns False inside its own case");
            Assert.True(cleared > endSelect,
                "the entry is marked written only after the answer, so a Cancel never reaches it");
            Assert.Single(Regex.Matches(body, "NeedsWrite = False"));
            // Positive control: Yes still writes.
            Assert.Contains("Case DialogResult.Yes\n                    iWrite()", body, StringComparison.Ordinal);
        }

        [Fact]
        public void Only_the_exit_route_offers_Cancel_and_the_others_still_get_Yes_and_No()
        {
            string ask = SourceMember.VisualBasic(Source("globals.vb"), "Friend Function AskExitQuestions() As Boolean");
            Assert.Contains("If Not LogEntry.optionalWrite(offerCancel:=True) Then Return False", ask, StringComparison.Ordinal);

            // Page Up and Page Down in the log entry, and Find Log Entry,
            // ignore the answer: they call it bare, so Cancel is never offered.
            string logEntry = Source("LogEntry.vb");
            string find = Source("FindLogEntry.vb");
            Assert.Equal(2, Regex.Matches(logEntry, @"\boptionalWrite\(\) ' See if need to write\.").Count);
            Assert.Single(Regex.Matches(find, @"logForm\.optionalWrite\(\)"));

            // Every call in the app: three bare, one offering Cancel, and no other.
            int offering = 0, bare = 0;
            foreach (string file in Directory.GetFiles(FieldKeyMapScan.RepoRoot(), "*.vb", SearchOption.TopDirectoryOnly))
            {
                string text = File.ReadAllText(file);
                offering += Regex.Matches(text, @"optionalWrite\(offerCancel:=True\)").Count;
                bare += Regex.Matches(text, @"optionalWrite\(\)").Count;
            }
            Assert.Equal(1, offering);
            Assert.Equal(3, bare);
        }
    }
}
