using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Read-only notes explain a control; they do not stand in front of it
    /// (#211).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found 2026-08-24, by Noel hitting it live: "I see a box shift tab from
    /// the selector which is not an edit, it just reads, and I don't see a way
    /// to select which system to use." The Audio system combo was there and was
    /// enabled. He could not find it because a note sat between it and the
    /// device list, and the note was a tab stop — so Shift+Tab from the list
    /// landed on prose, and concluding from that there was no control is the
    /// correct inference from what he was given.
    /// </para>
    /// <para>
    /// The tension is real and the fix is not simply deleting Focusable. WPF
    /// dialogs run in focus mode, so a screen reader cannot reach static text by
    /// arrowing at all; Focusable="True" was the standard workaround and it was
    /// the right instinct, because an explanation nobody can reach is not an
    /// explanation. What was never counted is that it puts the explanation AHEAD
    /// of the thing it explains in the one ordering a keyboard operator walks.
    /// </para>
    /// <para>
    /// So each note is registered against the control it describes
    /// (<c>JJFlexHelp.SetNoteFor</c>) and answers Ctrl+F1 there, read live. The
    /// note keeps its words, its place on screen and its accessible name. It
    /// loses only the toll.
    /// </para>
    /// <para>
    /// Source-read, in the LexiconKeyCoverageTests family, because Radios.Tests
    /// cannot load the WPF assembly — and because what is being verified is
    /// literal markup written by people. This cannot prove where focus LANDS;
    /// only pressing Tab on a real build can, and the report says so.
    /// </para>
    /// </remarks>
    public class ReadOnlyNotesNotTabStopsTests
    {
        private const string DeviceDialogXaml = "JJFlexWpf/Dialogs/AudioDevicesDialog.xaml";
        private const string DeviceDialogCode = "JJFlexWpf/Dialogs/AudioDevicesDialog.xaml.cs";

        /// <summary>
        /// The seven read-only lines on the audio device page, and the control
        /// each one belongs to. This list IS the decision — the assertions below
        /// only hold it in place.
        /// </summary>
        private static readonly (string Note, string Control)[] Notes =
        {
            ("StatusText", "RefreshButton"),
            ("HostApiNote", "HostApiCombo"),
            ("RadioOutputNote", "RadioOutputList"),
            ("RadioInputNote", "RadioInputList"),
            ("TxRateNote", "TxRateCombo"),
            ("MicLevelNote", "MicLevelSlider"),
            ("FilterNoteText", "AdvancedDevicesCheck"),
        };

        // ────────────────────────────────────────────────────────────────
        //  Prove the instrument before trusting its silence
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_scanner_finds_focusable_text_where_some_is_still_meant_to_be()
        {
            // A "no matches" result also claims the scanner would have SEEN a
            // match. Point it at the Settings dialog, which deliberately keeps
            // two focusable read-only lines: both are SECTION signposts rather
            // than notes about a control — the Radio Outputs advisory exists to
            // say why the panel beneath it is empty, which is exactly when
            // there is no control to hang it on. Text that is the only carrier
            // of its information stays reachable.
            string settings = Source("JJFlexWpf/Dialogs/SettingsDialog.xaml");
            Assert.NotEmpty(FocusableElementNames(settings));
        }

        [Fact]
        public void The_scanner_reads_a_name_out_of_a_focusable_element()
        {
            const string sample = @"
                <TextBlock x:Name=""SomeNote""
                           TextWrapping=""Wrap""
                           Focusable=""True""
                           AutomationProperties.Name=""Some note""/>
                <TextBlock x:Name=""QuietNote"" TextWrapping=""Wrap""/>
";
            var found = FocusableElementNames(sample);
            Assert.Equal(new[] { "SomeNote" }, found);
        }

        // ────────────────────────────────────────────────────────────────
        //  The device page, which is where this was reported
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void No_read_only_line_on_the_audio_device_page_is_a_tab_stop()
        {
            var focusable = FocusableElementNames(Source(DeviceDialogXaml));
            Assert.Empty(focusable);
        }

        [Fact]
        public void Every_note_names_the_control_it_explains()
        {
            string code = Source(DeviceDialogCode);

            foreach (var (note, control) in Notes)
            {
                // Removing the tab stop without this would silently take the
                // words away from the operator who needs them most, which is
                // worse than the friction it saves.
                Assert.Contains(
                    "JJFlexHelp.SetNoteFor(" + note + ", " + control + ")",
                    code);
            }
        }

        [Fact]
        public void Every_note_and_every_control_it_points_at_still_exists()
        {
            string xaml = Source(DeviceDialogXaml);
            foreach (var (note, control) in Notes)
            {
                Assert.Contains("x:Name=\"" + note + "\"", xaml);
                Assert.Contains("x:Name=\"" + control + "\"", xaml);
            }
        }

        [Fact]
        public void The_note_registration_runs_before_anything_writes_a_note()
        {
            // RegisterNotes has to be in the constructor and ahead of the first
            // status write, or the page would come up with notes belonging to
            // nothing and Ctrl+F1 would answer with the control's own help
            // alone — a quiet, plausible, half-correct answer, which is the
            // worst kind to debug.
            string code = Source(DeviceDialogCode);
            int register = code.IndexOf("RegisterNotes();", StringComparison.Ordinal);
            int firstWrite = code.IndexOf("ReloadPortAudioDevices(", StringComparison.Ordinal);

            Assert.True(register >= 0, "RegisterNotes() is not called");
            Assert.True(firstWrite >= 0);
            Assert.True(register < firstWrite);
        }

        // ────────────────────────────────────────────────────────────────
        //  The other dialogs the sweep reached
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_power_dialog_registers_both_its_lines_against_both_fields()
        {
            string code = Source("JJFlexWpf/Dialogs/PowerDialog.xaml.cs");
            foreach (string note in new[] { "HeaderText", "RangeText" })
                foreach (string field in new[] { "RfPowerField", "TunePowerField" })
                    Assert.Contains("JJFlexHelp.SetNoteFor(" + note + ", " + field + ")", code);

            Assert.Empty(FocusableElementNames(Source("JJFlexWpf/Dialogs/PowerDialog.xaml")));
        }

        [Fact]
        public void The_filter_calculator_keeps_its_answer_reachable_and_drops_its_instruction()
        {
            // ResultText is still a tab stop, deliberately: it is the answer the
            // operator came for, not prose about a control, and no control
            // repeats it. The instruction line above the boxes is gone from the
            // tab order and lives on the panel, where the Ctrl+F1 walk finds it
            // from any of the three boxes.
            string xaml = Source("JJFlexWpf/Dialogs/FilterCalculatorDialog.xaml");

            Assert.Equal(new[] { "ResultText" }, FocusableElementNames(xaml));
            Assert.Contains("local:JJFlexHelp.Text=", xaml);
        }

        [Fact]
        public void The_settings_pc_audio_status_belongs_to_the_checkbox_above_it()
        {
            string code = Source("JJFlexWpf/Dialogs/SettingsDialog.xaml.cs");
            Assert.Contains("JJFlexHelp.SetNoteFor(PcAudioStatusText, PcAudioCheck)", code);
        }

        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The x:Name of every element in <paramref name="xaml"/> that declares
        /// Focusable="True". Unnamed ones are reported as their tag, so a
        /// focusable anonymous TextBlock cannot hide from the sweep.
        /// </summary>
        private static IReadOnlyList<string> FocusableElementNames(string xaml)
        {
            var names = new List<string>();

            // Elements are matched from their opening angle bracket to the end
            // of the start tag, so an attribute is always attributed to the
            // element that actually carries it.
            foreach (Match m in Regex.Matches(xaml, @"<(\w[\w:.]*)\b[^<>]*?/?>", RegexOptions.Singleline))
            {
                string tag = m.Value;
                if (!Regex.IsMatch(tag, @"Focusable\s*=\s*""True""")) continue;

                Match name = Regex.Match(tag, @"x:Name\s*=\s*""([^""]+)""");
                names.Add(name.Success ? name.Groups[1].Value : "<" + m.Groups[1].Value + ">");
            }

            return names;
        }

        private static string Source(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "source not found: " + path);
            return File.ReadAllText(path);
        }

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
    }
}
