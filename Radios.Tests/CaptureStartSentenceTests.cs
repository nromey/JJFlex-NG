#nullable enable

using System;
using System.IO;
using System.Text.RegularExpressions;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The sentence that confirms a detailed capture has started names a way
    /// to stop it that fits the door the operator came in by. It used to say
    /// "stop the capture from this button" whichever door started it, so the
    /// JJ key chord, which works from anywhere and presses no button, told the
    /// operator to use a button they had never touched.
    /// </summary>
    /// <remarks>
    /// The choice lives in <c>StartDetailedCapture</c> in the VB application,
    /// which no test project references, so these read the source: each door
    /// passes its named reason, and the start routine maps each reason to its
    /// own sentence.
    /// </remarks>
    public sealed class CaptureStartSentenceTests
    {
        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), Path.Combine(parts)));

        private static string StartRoutine()
        {
            string globals = Read("globals.vb");
            int start = globals.IndexOf("Friend Sub StartDetailedCapture(", StringComparison.Ordinal);
            Assert.True(start >= 0, "StartDetailedCapture was not found in globals.vb");
            int end = globals.IndexOf("End Sub", start, StringComparison.Ordinal);
            return globals.Substring(start, end - start);
        }

        /// <summary>The key each Case arm of the start routine speaks.</summary>
        private static string KeyFor(string routine, string caseLabel)
        {
            var m = Regex.Match(routine,
                Regex.Escape(caseLabel) + @"\s*\r?\n\s*SpeakDiagnostics\(Radios\.Lexicon\.Get\(""([^""]+)""\)\)");
            Assert.True(m.Success, "no spoken sentence found for " + caseLabel);
            return m.Groups[1].Value;
        }

        [Fact]
        public void The_chord_names_itself_as_the_way_to_stop()
        {
            string key = KeyFor(StartRoutine(), "Case JJFlexWpf.DiagnosticsBridge.ChordDoor");
            Assert.Equal("logging.capture.started_from_chord", key);
            string text = Lexicon.Get(key);
            Assert.Contains("Control J then Control D", text, StringComparison.Ordinal);
            Assert.DoesNotContain("button", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Only_the_Diagnostics_tab_says_this_button()
        {
            string routine = StartRoutine();
            Assert.Equal("logging.capture.started",
                KeyFor(routine, "Case JJFlexWpf.DiagnosticsBridge.DiagnosticsTabDoor"));
            Assert.Contains("this button", Lexicon.Get("logging.capture.started"), StringComparison.Ordinal);

            // Any other caller, such as a check run's own recording, gets the
            // plain line: it names no button and no chord.
            string other = KeyFor(routine, "Case Else");
            Assert.Equal("logging.capture.started_plain", other);
            Assert.DoesNotContain("button", Lexicon.Get(other), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Each_door_passes_its_own_named_reason()
        {
            // A door that passed a literal instead of the named reason would
            // fall to the plain line, or, worse, to another door's sentence.
            Assert.Contains("DiagnosticsBridge.ToggleCapture(DiagnosticsBridge.ChordDoor);",
                Read("JJFlexWpf", "KeyCommands.cs"), StringComparison.Ordinal);
            Assert.Contains("DiagnosticsBridge.ToggleCapture(DiagnosticsBridge.DiagnosticsTabDoor);",
                Read("JJFlexWpf", "Dialogs", "SettingsDialog.Diagnostics.cs"), StringComparison.Ordinal);
            Assert.DoesNotContain("ToggleCapture(\"", Read("JJFlexWpf", "KeyCommands.cs"), StringComparison.Ordinal);
        }
    }
}
