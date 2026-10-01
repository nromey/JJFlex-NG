#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The one property this surface exists for: it is reachable when there is
    /// no radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Source checks, because the thing being protected is an ABSENCE.</b>
    /// A running test can show that the window opens; it cannot show that the
    /// window will still open a year from now when somebody adds a convenience
    /// property to it. The failure would not be an exception — it would be a
    /// handler that quietly needs a rig and a button that is therefore useless
    /// in the two situations it was built for, which is precisely the defect
    /// that made this surface necessary.
    /// </para>
    /// <para>
    /// Every check below is paired with something it must FIND, so a broken
    /// repository-root walk cannot turn the whole file green.
    /// </para>
    /// </remarks>
    // LEXICON_SCANNER_EXEMPT — this file quotes the PREFIX "facts." as a
    // needle to prove the window looks its words up rather than writing them.
    // That is not a key and was never meant to resolve; sweeping it would
    // report the check's own instrument as a defect.
    public class UndeliveredSurfaceReachabilityTests
    {
        private const string Dialog = "JJFlexWpf/Dialogs/UndeliveredDetailsDialog.xaml.cs";
        private const string DialogXaml = "JJFlexWpf/Dialogs/UndeliveredDetailsDialog.xaml";
        private const string StatusXaml = "JJFlexWpf/Dialogs/StatusDialog.xaml";
        private const string StatusCode = "JJFlexWpf/Dialogs/StatusDialog.xaml.cs";
        private const string Commands = "JJFlexWpf/KeyCommands.cs";

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that cannot "
                + "find its subject proves nothing about it.");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// The file with its comments taken out.
        /// </summary>
        /// <remarks>
        /// A doc comment that EXPLAINS why this window takes no rig has to be
        /// allowed to say the word "rig". Checking the raw text would make the
        /// explanation itself the failure, which teaches the next author to
        /// delete the explanation.
        /// </remarks>
        private static string CodeOnly(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            text = Regex.Replace(text, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
            return text;
        }

        [Fact]
        public void TheWindowNeverAsksForARadio()
        {
            string code = CodeOnly(Read(Dialog));

            // The positive control: this file really is the window, and it
            // really does hold a store.
            Assert.Contains("FactStore", code, StringComparison.Ordinal);
            Assert.Contains("ApplicationFacts.Store", code, StringComparison.Ordinal);

            Assert.DoesNotContain("FlexBase", code, StringComparison.Ordinal);
            Assert.DoesNotContain("GetRigControl", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Rig", code, StringComparison.Ordinal);
        }

        [Fact]
        public void TheCommandHandlerNeverAsksForARadioEither()
        {
            string code = Read(Commands);
            Match handler = Regex.Match(
                code,
                @"private void ShowUndeliveredDetailsHandler\(\)\s*\{(?<body>[^}]*)\}",
                RegexOptions.Singleline);

            Assert.True(handler.Success,
                "ShowUndeliveredDetailsHandler was not found in " + Commands + ", so this test "
                + "is checking nothing. If it was renamed, rename it here too.");

            string body = handler.Groups["body"].Value;
            Assert.Contains("UndeliveredDetailsDialog", body, StringComparison.Ordinal);

            // Every other dialog handler in this file opens by fetching the
            // rig. This one must not: it has to work with no radio, with a
            // disconnected one, with the picker open and after the reader
            // binding has been replaced.
            Assert.DoesNotContain("GetRigControl", body, StringComparison.Ordinal);
        }

        [Fact]
        public void TheCommandIsGlobalAndUnboundWithAStatedReason()
        {
            string code = Read(Commands);

            Assert.Contains(
                "new(Keys.None, CommandValues.ShowUndeliveredDetails, KeyScope.Global)",
                code, StringComparison.Ordinal);

            // Global scope is the load-bearing half. A Radio-scoped command
            // would be filtered out of the Command Finder exactly when there is
            // no radio, which is the case it exists for.
            Assert.Contains(
                "CommandValues.ShowUndeliveredDetails, KeyTypes.Command, ShowUndeliveredDetailsHandler",
                code, StringComparison.Ordinal);
            Assert.Contains("[CommandValues.ShowUndeliveredDetails] = new(UnboundReason.CommandFinderOnly",
                code, StringComparison.Ordinal);
        }

        [Fact]
        public void TheStatusEntryIsOutsideTheReadoutAndOutsideItsThreeEarlyReturns()
        {
            string xaml = Read(StatusXaml);
            string code = Read(StatusCode);

            // In the layout, not in the text. RefreshStatus builds StatusText
            // and returns early three times; anything built in there is missing
            // when there is no radio, when the radio is disconnected, and while
            // the operator is reading.
            Assert.Contains("x:Name=\"UndeliveredButton\"", xaml, StringComparison.Ordinal);
            Assert.Contains("x:Name=\"PendingSummary\"", xaml, StringComparison.Ordinal);

            Match refresh = Regex.Match(
                code,
                @"private void RefreshStatus\(\)\s*\{(?<body>.*?)\n    \}",
                RegexOptions.Singleline);
            Assert.True(refresh.Success,
                "RefreshStatus was not found in " + StatusCode + ", so this test is checking "
                + "nothing.");

            string body = refresh.Groups["body"].Value;

            // The positive control: the body really is the readout builder.
            Assert.Contains("connect.status.not_connected", body, StringComparison.Ordinal);

            Assert.DoesNotContain("UndeliveredButton", body, StringComparison.Ordinal);
            Assert.DoesNotContain("PendingSummary", body, StringComparison.Ordinal);
        }

        [Fact]
        public void TheDetailIsOrdinaryReadOnlyTextAndNotHelpText()
        {
            string xaml = Read(DialogXaml);

            // Regular selectable read-only text, reached through the platform's
            // own text pattern. Putting the contents in HelpText would make
            // them something the reader recites on focus rather than something
            // the operator navigates, and would take the braille display with
            // it.
            Assert.Contains("IsReadOnly=\"True\"", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("JJFlexHelp.Text=\"{Binding", xaml, StringComparison.Ordinal);

            // Every control that carries meaning names itself.
            foreach (string name in new[] { "Items", "DetailText", "PendingView", "HistoryView" })
            {
                Assert.Contains("x:Name=\"" + name + "\"", xaml, StringComparison.Ordinal);
            }
            Assert.Contains("AutomationProperties.Name", xaml, StringComparison.Ordinal);
        }

        [Fact]
        public void TheWindowSpeaksNothingOfItsOwnAccord()
        {
            string code = Read(Dialog);

            // A window whose reason for existing is that the speech route
            // failed must not depend on that route. It renders text and lets
            // the platform read it.
            Assert.DoesNotContain("ScreenReaderOutput", code, StringComparison.Ordinal);
            Assert.DoesNotContain(".Speak(", code, StringComparison.Ordinal);
        }

        [Fact]
        public void EveryWordTheWindowShowsComesFromTheLexicon()
        {
            string code = Read(Dialog);

            // The words are Noel's and they are provisional, so they live where
            // he can read and change them in one file. A sentence assembled in
            // code here would be one he could not reach.
            Assert.Contains("Lexicon.Get(\"facts.", code, StringComparison.Ordinal);

            // No bare double-quoted prose: anything a person reads is a key
            // lookup. Allowed literals are keys, empty strings and member
            // names, none of which contain a space.
            foreach (Match literal in Regex.Matches(code, "\"(?<text>[^\"\\n]*)\""))
            {
                string text = literal.Groups["text"].Value;
                if (text.Length == 0) continue;
                if (!text.Contains(' ')) continue;
                Assert.Fail(
                    "The window builds a sentence in code: \"" + text + "\". Every word an "
                    + "operator reads belongs in Radios/Lexicon/facts.json, where he can find "
                    + "it beside the rest and change it.");
            }
        }
    }
}
