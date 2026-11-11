using System;
using System.IO;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Renders the Fixer Tool's page to standalone HTML files so a person can
    /// read it in a browser — no application, no WebView, no radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written so Noel can review the one part of this tool nothing can
    /// verify but a person.</b> Everything structural has tests behind it now.
    /// The PROSE has none, and it is the surface an operator meets when
    /// something has already gone wrong.
    /// </para>
    /// <para>
    /// <b>It feeds real FACTS through the real analyzers.</b> Nothing here
    /// fabricates an outcome or a sentence — the findings, the wording and the
    /// ordering are produced by <c>AudioSetupCheck</c>, <c>TransmitStages</c>
    /// and <c>FixerPage</c> exactly as they would be at a radio. A mock-up of
    /// the page would review the mock-up.
    /// </para>
    /// <para>
    /// <b>What a browser CANNOT tell you:</b> the buttons will not work. The
    /// page talks to its host through <c>window.chrome.webview</c>, which does
    /// not exist outside the application. Pressing things will do nothing or
    /// error. Reading, heading navigation, button navigation, tab order and
    /// how it all sounds are exactly right; anything that requires the host to
    /// answer is not.
    /// </para>
    /// <para>
    /// Runs with the suite and writes every time — see the note in the test
    /// about why there is no gate. To regenerate the files on their own:
    /// <c>dotnet test Radios.Tests/Radios.Tests.csproj -c Debug -p:Platform=x64
    /// --filter "FullyQualifiedName~FixerPageForReview"</c>. Never the bare
    /// <c>dotnet test</c>, which puts real dialogs on the operator's desktop.
    /// </para>
    /// <para>
    /// <b>This paragraph said "skipped by default" and named an environment
    /// variable until 2026-08-26</b>, describing the very gate the test below
    /// records having removed. Two lines apart, in the file written to catch
    /// silent successes.
    /// </para>
    /// </remarks>
    public class FixerPageForReview
    {
        /// <summary>
        /// Where the pages land: <c>C:\temp\fixer</c>, by Noel.
        /// </summary>
        /// <remarks>
        /// NOT MyDocuments. That is the operator's own folder and this is
        /// throwaway output — generated review copies do not belong in a place
        /// somebody keeps things. It also resolved to
        /// <c>OneDrive\Documents</c> via folder redirection, so the files were
        /// being written somewhere different from where a check for them
        /// looked, and syncing to the cloud into the bargain.
        /// </remarks>
        private const string OutputDir = @"C:\temp\fixer";

        /// <summary>
        /// What a denial at <see cref="OutputDir"/> means (#603). Codex's
        /// sandbox denies that path, and three reviews in five days each spent
        /// a paragraph establishing that a green track was green.
        /// </summary>
        internal const string FixedFolderNote =
            "This test writes the Fixer review pages to a fixed folder by design (see OutputDir in "
            + "FixerPageForReview.cs), so a denial creating or writing there comes from the environment "
            + "the test runs in, such as a sandbox that does not allow that path. It is not a defect in "
            + "the code under test, and the rest of the suite's result stands on its own.";

        /// <summary>
        /// Create the folder and write every page, failing with
        /// <see cref="FixedFolderNote"/> when the environment denies either
        /// (#603). Not a skip, and not a pass: the skip gate was removed on
        /// purpose, and a test that cannot write has not done its job. A
        /// failure that explains itself is the whole change.
        /// </summary>
        /// <remarks>
        /// The denial recorded in every saved run was at the WRITE, with the
        /// folder already present, so the writes are inside the catch as well
        /// as the folder's creation. The folder is a parameter so the message
        /// can be shown on a folder a test controls.
        /// </remarks>
        internal static void WritePages(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);

                foreach (FixerReviewState state in FixerStates.All())
                    File.WriteAllText(Path.Combine(dir, state.FileName), state.Html);

                File.WriteAllText(Path.Combine(dir, "report-as-emailed.txt"),
                                  FixerReport.PlainText(FixerStates.ProblemsFound().Run));

                // WHICH TREE WROTE THESE. The path is absolute and shared, so
                // during a sprint EVERY worktree's test run writes here and the
                // last writer wins — found on 2026-08-27, when a page freshly
                // rewritten by a parallel track's suite showed this track's
                // change UNDONE, with a new timestamp, which reads exactly like
                // your own edit failing to take. The pages cannot carry the
                // stamp themselves without polluting what Noel reviews, so it
                // rides beside them: before trusting a walk of these files,
                // check SOURCE.txt names the tree you meant.
                File.WriteAllText(Path.Combine(dir, "SOURCE.txt"),
                    "Written from " + IntegrationPassTree.Root + Environment.NewLine
                    + "at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " local."
                    + Environment.NewLine
                    + "This folder is shared by every worktree; the last test run "
                    + "to finish owns these files." + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                       || ex is System.Security.SecurityException)
            {
                Assert.Fail(FixedFolderNote + " The folder was " + dir + "; what was refused: "
                            + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Positive control for the message (#603): a write that is denied
        /// while the folder exists, the case every saved run recorded, and a
        /// folder that cannot be created. Both fail, and both say why.
        /// </summary>
        [Fact]
        public void A_denied_folder_fails_and_says_the_denial_is_environmental()
        {
            string root = Path.Combine(Path.GetTempPath(), "jjflex-fixer-denied-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string blocked = Path.Combine(root, "1-nothing-run-yet.html");
            string notAFolder = Path.Combine(root, "a-file");
            try
            {
                File.WriteAllText(blocked, "kept");
                File.SetAttributes(blocked, FileAttributes.ReadOnly);
                var write = Assert.ThrowsAny<Exception>(() => WritePages(root));
                Assert.Contains(FixedFolderNote, write.Message, StringComparison.Ordinal);
                Assert.Contains("UnauthorizedAccessException", write.Message, StringComparison.Ordinal);
                Assert.Equal("kept", File.ReadAllText(blocked));

                File.WriteAllText(notAFolder, "a file where a folder is wanted");
                var create = Assert.ThrowsAny<Exception>(() => WritePages(Path.Combine(notAFolder, "fixer")));
                Assert.Contains(FixedFolderNote, create.Message, StringComparison.Ordinal);
            }
            finally
            {
                try { File.SetAttributes(blocked, FileAttributes.Normal); } catch { }
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Write_the_pages_a_person_can_read()
        {
            // NO GATE. The first version of this only wrote when an
            // environment variable was set, the variable never reached the
            // test host, and the test PASSED having done nothing — a silent
            // success in the very file written to catch silent successes.
            // Writing three small files costs nothing; a guard that can lie
            // costs an afternoon.
            string dir = OutputDir;
            WritePages(dir);

            // Assert on all five, naming the directory, so a failure says
            // WHERE it looked rather than just that something was false.
            foreach (string name in new[] { "1-nothing-run-yet.html",
                                            "2-problems-found.html",
                                            "3-nothing-wrong.html",
                                            "report-as-emailed.txt",
                                            "SOURCE.txt" })
            {
                string full = Path.Combine(dir, name);
                Assert.True(File.Exists(full), "did not write " + full);
                long floor = name == "SOURCE.txt" ? 100 : 500;
                Assert.True(new FileInfo(full).Length > floor,
                            "wrote almost nothing to " + full);
            }
        }

        // The three states moved to FixerStates on 2026-08-26, when the
        // integration pass's blind walk became a second reader of them. Two
        // copies of a fixture is exactly the duplication that pass exists to
        // find, and it may not commit it itself. This class keeps what is its
        // own: where the files land, what they are for, and what a browser
        // cannot tell you about them.
    }
}
