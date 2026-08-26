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
        private static string OutputDir
        {
            get
            {
                const string dir = @"C:\temp\fixer";
                Directory.CreateDirectory(dir);
                return dir;
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

            foreach (FixerReviewState state in FixerStates.All())
                File.WriteAllText(Path.Combine(dir, state.FileName), state.Html);

            File.WriteAllText(Path.Combine(dir, "report-as-emailed.txt"),
                              FixerReport.PlainText(FixerStates.ProblemsFound().Run));

            // Assert on all four, naming the directory, so a failure says
            // WHERE it looked rather than just that something was false.
            foreach (string name in new[] { "1-nothing-run-yet.html",
                                            "2-problems-found.html",
                                            "3-nothing-wrong.html",
                                            "report-as-emailed.txt" })
            {
                string full = Path.Combine(dir, name);
                Assert.True(File.Exists(full), "did not write " + full);
                Assert.True(new FileInfo(full).Length > 500, "wrote almost nothing to " + full);
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
