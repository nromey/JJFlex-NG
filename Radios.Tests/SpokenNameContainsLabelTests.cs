using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #840: the screen reader must hear the words the screen shows. Every
    /// control whose spoken name is set must contain its shown label, and
    /// every relabel in code must rename the control with the same text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found on the bench 2026-10-28: ConfirmActionDialog's No button showed
    /// "Disconnect without saving" and was announced "No, cancel" (fixed in
    /// 4624ee48). This is the tree-wide form of the check
    /// ConfirmDialogSpokenNameTests makes for that one dialog.
    /// </para>
    /// <para>
    /// <b>The Settings tab headed "Notifications" PASSES this rule, and that
    /// is correct for this rule.</b> Its spoken name, "Verbosity and
    /// notifications settings", contains "Notifications"; what #840 records
    /// about it is that the name ADDS a word the route sentences never use.
    /// Its one name is awaiting Noel (#840), and it is deliberately not on
    /// the allow-list below, because an entry for a control that passes would
    /// be flagged as stale by the test that keeps the list honest.
    /// </para>
    /// <para>
    /// Every entry on the allow-lists is a control this guard finds today and
    /// that #840's sweep did not change, with the reason. Most are fuller names
    /// that say what a button does; changing any of them means choosing new
    /// words, which is Noel's call, so they are listed in the batch report
    /// rather than reworded here.
    /// </para>
    /// </remarks>
    public sealed class SpokenNameContainsLabelTests
    {
        private const string Fuller = "a fuller description of what it does; rewording is Noel's (#840)";
        private const string Abbreviation = "spells out an abbreviation the screen shows; rewording is Noel's (#840)";
        private const string AwaitingNoel = "a route the operator is told by name; its one name is awaiting Noel (#840)";
        private const string Unconstructed = "FiltersDspControl is constructed by nothing (#482)";

        /// <summary>(file, shown label) for XAML and code-built controls.</summary>
        private static readonly Dictionary<(string File, string Shown), string> ShownNotInName = new()
        {
            [("JJFlexWpf/Controls/FiltersDspControl.xaml", "DSP / Filters")] = Unconstructed, // announced "DSP and Filters"
            [("JJFlexWpf/Controls/FiltersDspControl.xaml", "CW / Keyer")] = Unconstructed, // announced "CW and Keyer"
            [("JJFlexWpf/Controls/FiltersDspControl.xaml", "Audio / Microphone")] = Unconstructed, // announced "Audio and Microphone"
            [("JJFlexWpf/Controls/FiltersDspControl.xaml", "RX EQ")] = Unconstructed, // announced "RX Equalizer"
            [("JJFlexWpf/Controls/FiltersDspControl.xaml", "TX EQ")] = Unconstructed, // announced "TX Equalizer"
            [("JJFlexWpf/Controls/FiltersDspControl.xaml", "TNF")] = Unconstructed, // announced "Tracking Notch Filter"
            [("JJFlexWpf/Controls/FiltersDspControl.xaml", "Info")] = Unconstructed, // announced "Feature Availability"
            [("JJFlexWpf/Controls/MetersPanel.xaml", "Test")] = Fuller, // announced "Play a two second preview of this meter's tone"
            [("JJFlexWpf/Controls/MetersPanel.xaml", "Add Meter")] = Fuller, // announced "Add a new meter tone"
            [("JJFlexWpf/Controls/MetersPanel.xaml", "Auto-enable meters when tuning")] = Fuller, // announced "Automatically enable meter tones when tune carrier is activated"
            [("JJFlexWpf/Controls/MetersPanel.xaml", "Speak meter values periodically")] = Fuller, // announced "Periodically speak meter values at set interval"
            [("JJFlexWpf/Controls/StaticIpControl.xaml", "_Use the address the radio has now")] = Fuller, // announced "Fill in the fields using the address the radio is using right now"
            [("JJFlexWpf/Controls/StaticIpControl.xaml", "App_ly to radio")] = Fuller, // announced "Apply the address mode and settings to the radio"
            [("JJFlexWpf/Dialogs/AboutDialog.xaml", "_Copy Everything")] = Fuller, // announced "Copy all information to clipboard"
            [("JJFlexWpf/Dialogs/AudioDevicesDialog.xaml", "_Refresh device list")] = Fuller, // announced "Refresh the device list"
            [("JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml", "Earcon Explorer")] = AwaitingNoel, // announced "Earcon Sound Explorer"
            [("JJFlexWpf/Dialogs/CommandFinderDialog.xaml", "Show all commands")] = Fuller, // announced "Show commands from all modes"
            [("JJFlexWpf/Dialogs/EarconScratchpadDialog.xaml", "_Decay: each note falls away inside its own length")] = Fuller, // announced "Decay"
            [("JJFlexWpf/Dialogs/EarconScratchpadDialog.xaml", "_Both, back to back")] = Fuller, // announced "Both countdowns back to back"
            [("JJFlexWpf/Dialogs/EscDialog.xaml", "Enhanced Signal Clarity (ESC) enabled")] = Fuller, // announced "Enhanced Signal Clarity enabled"
            [("JJFlexWpf/Dialogs/FilterPresetEditorDialog.xaml", "Move _Up")] = Fuller, // announced "Move preset up"
            [("JJFlexWpf/Dialogs/FilterPresetEditorDialog.xaml", "Move Do_wn")] = Fuller, // announced "Move preset down"
            [("JJFlexWpf/Dialogs/FilterPresetEditorDialog.xaml", "_Reset to Defaults")] = Fuller, // announced "Reset presets to defaults for this mode"
            [("JJFlexWpf/Dialogs/GpsStatusDialog.xaml", "_Speak status")] = Fuller, // announced "Speak the GPS summary"
            [("JJFlexWpf/Dialogs/GpsStatusDialog.xaml", "_Copy details")] = Fuller, // announced "Copy all GPS details to the clipboard"
            [("JJFlexWpf/Dialogs/MessageDialog.xaml", "Don't show this again")] = Fuller, // announced "Don't show this message again"
            [("JJFlexWpf/Dialogs/PersonalInfoDialog.xaml", "_OK")] = Fuller, // announced "Save operator information"
            [("JJFlexWpf/Dialogs/RigSelectorDialog.xaml", "Connect Locally")] = Fuller, // announced "Connect to selected radio over the local network only"
            [("JJFlexWpf/Dialogs/RigSelectorDialog.xaml", "Connect over SmartLink")] = Fuller, // announced "Connect to selected radio over SmartLink only, never falling back to local"
            [("JJFlexWpf/Dialogs/RigSelectorDialog.xaml", "Add to Favorites")] = Fuller, // announced "Add selected radio to favorites"
            [("JJFlexWpf/Dialogs/RigSelectorDialog.xaml", "Remove Radio...")] = Fuller, // announced "Remove selected radio from the list"
            [("JJFlexWpf/Dialogs/RigSelectorDialog.xaml", "Show Remote Radios")] = Fuller, // announced "Show this account's SmartLink radios"
            [("JJFlexWpf/Dialogs/RigSelectorDialog.xaml", "_Low BW")] = Abbreviation, // announced "Low bandwidth for selected radio"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "PTT")] = AwaitingNoel, // announced "Push to talk settings"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Debounce tuning speech (group rapid steps)")] = Fuller, // announced "Debounce tuning speech"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Band boundary notifications")] = Fuller, // announced "Notify when tuning near band boundaries"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "TX band lockout")] = Fuller, // announced "Prevent transmitting outside licensed bands"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Enforce transmit rules (60m channels, power limits)")] = Fuller, // announced "Enforce transmit parameters based on published amateur radio rules"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Mute the headphone output")] = Fuller, // announced "Mute the radio's headphone output"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Mute the line out output")] = Fuller, // announced "Mute the radio's line out output"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Mute the front panel speaker")] = Fuller, // announced "Mute the radio's front panel speaker"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Enable CW notifications (AS, BT, SK prosigns)")] = Fuller, // announced "Enable Morse code notifications for connection events"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Use different TCP and UDP ports (advanced)")] = Fuller, // announced "Allow different TCP and UDP ports"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Te_st port")] = Fuller, // announced "Test the port number locally for validity and common conflicts"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Refresh what the radio reports")] = Fuller, // announced "Re-read the radio's reachability status"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Only allow connections from addresses on your own network")] = Fuller, // announced "Only allow connections from private network addresses"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Test _network")] = Fuller, // announced "Run a fresh SmartLink network diagnostic probe against the connected radio"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Copy _report")] = Fuller, // announced "Copy the last network diagnostic report to the clipboard as markdown"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Sa_ve report")] = Fuller, // announced "Save the last network diagnostic report to a file"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Verbose network diagnostics (include raw details in status announcements)")] = Fuller, // announced "Enable verbose network diagnostic output"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Let JJ Flexible learn which connection path each radio prefers")] = Fuller, // announced "Learn which connection path each radio prefers"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Radios")] = AwaitingNoel, // announced "Per-radio settings"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "_Refresh all steps")] = Fuller, // announced "Re-read the radio and update the status of every step"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Set na_me now")] = Fuller, // announced "Set the name on the radio right now"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Unregister...")] = Fuller, // announced "Remove this radio's SmartLink registration"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "_Download firmware for this radio")] = Fuller, // announced "Look for firmware for this radio on the JJ Flexible servers and download it"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "C_hoose a file instead...")] = Fuller, // announced "Choose a firmware file already on this computer"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Send to radio")] = Fuller, // announced "Send the chosen firmware file to the radio"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Stop reporting the version mismatch")] = Fuller, // announced "Stop reporting the firmware version mismatch"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Open Net_work settings")] = Fuller, // announced "Go to the Network tab to set the port"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Dip radio audio while a warning sounds")] = Fuller, // announced "Dip radio audio through this computer while a warning sounds"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Step 6 — Check it from outside")] = Fuller, // announced "Step 6, check the radio can be reached from outside"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Run the network _test")] = Fuller, // announced "Run the SmartLink network diagnostic"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Enable alert sounds (earcons)")] = Fuller, // announced "Enable alert sounds such as beeps and tones"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Show panadapter / waterfall display")] = Fuller, // announced "Show the panadapter and waterfall display"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Speak connection progress while connecting to a radio")] = Fuller, // announced "Speak connection progress"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Updates")] = AwaitingNoel, // announced "Update channel and auto-check preferences"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Normal (recommended)")] = Fuller, // announced "Normal detail, recommended"
            [("JJFlexWpf/Dialogs/SettingsDialog.xaml", "Measure now")] = Fuller, // announced "Measure the settings folder now"
            [("JJFlexWpf/Dialogs/SilencedMessagesDialog.xaml", "_Show this again")] = Fuller, // announced "Show the selected message again"
            [("JJFlexWpf/Dialogs/SilencedMessagesDialog.xaml", "Show _all of them again")] = Fuller, // announced "Show all silenced messages again"
            [("JJFlexWpf/Dialogs/TXControlsDialog.xaml", "TX Req RCA")] = Abbreviation, // announced "TX Request RCA Enabled"
            [("JJFlexWpf/Dialogs/TXControlsDialog.xaml", "TX Req ACC")] = Abbreviation, // announced "TX Request ACC Enabled"
            [("JJFlexWpf/MainWindow.xaml", "Tune")] = AwaitingNoel, // announced "Antenna Tuner"
            [("JJFlexWpf/Dialogs/AudioWorkshopDialog.MeterInventory.cs", "Copy to clipboard")] = Fuller, // announced "Copy the meter inventory to the clipboard"
            [("JJFlexWpf/Dialogs/AudioWorkshopDialog.MicProfiles.cs", "Apply Setup")] = Fuller, // announced "Apply microphone setup"
            [("JJFlexWpf/Dialogs/AudioWorkshopDialog.MicProfiles.cs", "Save Setup...")] = Fuller, // announced "Save microphone setup"
            [("JJFlexWpf/Dialogs/AudioWorkshopDialog.MicProfiles.cs", "Delete Setup")] = Fuller, // announced "Delete microphone setup"
            [("JJFlexWpf/Dialogs/NoiseProfilesDialog.cs", "Apply Recommended Levels")] = Fuller, // announced "Set strength and floor to the recommended values for the stages that are on"
            [("JJFlexWpf/Dialogs/NoiseProfilesDialog.cs", "Capture Noise Profile")] = Fuller, // announced "Capture a noise profile from the current audio"
            [("JJFlexWpf/Dialogs/NoiseProfilesDialog.cs", "Load Selected Profile")] = Fuller, // announced "Load the selected noise profile into PC Spectral NR"
            [("JJFlexWpf/Dialogs/NoiseProfilesDialog.cs", "Save Current As")] = Fuller, // announced "Save the current noise profile under the name in the profile name box"
            [("JJFlexWpf/Dialogs/NoiseProfilesDialog.cs", "Clear Loaded Profile")] = Fuller, // announced "Clear the loaded noise profile so PC Spectral NR has nothing to subtract"
            [("JJFlexWpf/Dialogs/NoiseProfilesDialog.cs", "Open Profiles Folder")] = Fuller, // announced "Open the noise profiles folder in File Explorer"
            [("JJFlexWpf/Dialogs/RadioOwnershipDialog.cs", "It is _my radio")] = AwaitingNoel, // announced "Yes, this radio is mine"
            [("JJFlexWpf/Dialogs/RadioOwnershipDialog.cs", "It is s_omeone else's")] = AwaitingNoel, // announced "No, this radio belongs to someone else"
        };

        /// <summary>(file, control) for relabels in code that do not rename with the same text.</summary>
        private static readonly Dictionary<(string File, string Control), string> RelabelWithoutRename = new()
        {
            // The Header setters of RadioComboBox, RadioNumberBox and RadioInfoBox
            // rename the inner control themselves.
            [("JJFlexWpf/Controls/FiltersDspControl.xaml.cs", "*")] = Unconstructed + "; and each Header setter renames the inner control itself",
            [("JJFlexWpf/Dialogs/LogEntryDialog.xaml.cs", "FormContent")] = "its Content is the hosted form panel, not a label",
            [("JJFlexWpf/Dialogs/PersonalInfoDialog.xaml.cs", "OKButton")] = "shows Add or Update and is announced \"Save operator information\"; " + Fuller,
            [("JJFlexWpf/Dialogs/RigSelectorDialog.xaml.cs", "FavoriteMenuItem")] = "renamed in the same block from its own fuller \"_name\" lexicon key, deliberately",
            [("JJFlexWpf/Dialogs/RigSelectorDialog.xaml.cs", "RemoteListMenuItem")] = "renamed in the same block from its own fuller \"_name\" lexicon key, deliberately",
            [("JJFlexWpf/Dialogs/RigSelectorDialog.xaml.cs", "AccountButton")] = "renamed in the same block from its own fuller \"_name\" lexicon keys, deliberately",
            [("JJFlexWpf/Dialogs/StillRunningDialog.xaml.cs", "StopButton")] = "shows the lexicon text the XAML already shows, inside a fuller XAML name; " + Fuller,
            [("JJFlexWpf/Dialogs/StillRunningDialog.xaml.cs", "CloseButton")] = "shows the lexicon text the XAML already shows, inside a fuller XAML name; " + Fuller,
            [("JJFlexWpf/Dialogs/StillRunningDialog.xaml.cs", "StayButton")] = "shows the lexicon text the XAML already shows, inside a fuller XAML name; " + Fuller,
            [("JJFlexWpf/MainWindow.xaml.cs", "TransmitButton")] = "renamed, the screen reader would say \"TX On\" as the radio keys; that audible change on the transmit button is Noel's to rule (#840)",
        };

        private static string Root => FieldKeyMapScan.RepoRoot();

        private static List<(string File, int Line, string Shown, string Name)> AllMismatches()
        {
            var found = new List<(string, int, string, string)>();
            foreach (string file in SpokenNameScan.XamlFiles(Root))
            {
                string xaml = File.ReadAllText(Path.Combine(Root, file));
                foreach (var c in SpokenNameScan.XamlControls(file, xaml))
                    if (!SpokenNameScan.NameContainsShown(c.SpokenName, c.Shown))
                        found.Add((c.File, c.Line, c.Shown, c.SpokenName));
            }
            foreach (string file in SpokenNameScan.CodeFiles(Root))
            {
                string code = File.ReadAllText(Path.Combine(Root, file));
                foreach (var c in SpokenNameScan.CodeBuiltControls(file, code))
                    if (!SpokenNameScan.NameContainsShown(c.SpokenName, c.Shown))
                        found.Add((c.File, c.Line, c.Shown, c.SpokenName));
            }
            return found;
        }

        private static List<SpokenNameScan.CodeAssignment> AllUnrenamedRelabels()
        {
            var found = new List<SpokenNameScan.CodeAssignment>();
            foreach (string xamlFile in SpokenNameScan.XamlFiles(Root))
            {
                var named = SpokenNameScan.NamedControls(File.ReadAllText(Path.Combine(Root, xamlFile)));
                if (named.Count == 0) continue;
                foreach (string codeFile in SpokenNameScan.CodeFilesFor(Root, xamlFile))
                {
                    string code = File.ReadAllText(Path.Combine(Root, codeFile));
                    foreach (var a in SpokenNameScan.Assignments(codeFile, code, named))
                        if (a.Rename == null || !SpokenNameScan.SameText(a.Shown, a.Rename))
                            found.Add(a);
                }
            }
            return found;
        }

        [Fact]
        public void Every_spoken_name_contains_the_label_the_control_shows()
        {
            var unexplained = AllMismatches()
                .Where(m => !ShownNotInName.ContainsKey((m.File, m.Shown)))
                .Select(m => $"{m.File}:{m.Line}: shows \"{m.Shown}\", is announced \"{m.Name}\"")
                .ToList();
            Assert.True(unexplained.Count == 0,
                "These controls are announced by words that do not contain what they show. "
                + "Make the spoken name contain the label, or, if the difference is deliberate, "
                + "add it to ShownNotInName with the reason:\n" + string.Join("\n", unexplained));
        }

        [Fact]
        public void Every_relabel_in_code_renames_the_control_with_the_same_text()
        {
            var unexplained = AllUnrenamedRelabels()
                .Where(a => !RelabelWithoutRename.ContainsKey((a.File, a.Control))
                            && !RelabelWithoutRename.ContainsKey((a.File, "*")))
                .Select(a => $"{a.File}:{a.Line}: {a.Control} shows {a.Shown}, "
                             + (a.Rename == null ? "and is not renamed in the same block" : "but is renamed to " + a.Rename))
                .ToList();
            Assert.True(unexplained.Count == 0,
                "These relabels leave the XAML's spoken name behind. Follow each assignment, in "
                + "the same block, with AutomationProperties.SetName for the same text:\n"
                + string.Join("\n", unexplained));
        }

        /// <summary>
        /// An allow-list rots: a control gets fixed or relabelled and its
        /// excuse stays behind. Every entry must still describe a mismatch the
        /// scan finds today.
        /// </summary>
        [Fact]
        public void Nothing_on_the_allow_lists_has_quietly_been_fixed_or_renamed()
        {
            var shown = AllMismatches().Select(m => (m.File, m.Shown)).ToHashSet();
            var staleShown = ShownNotInName.Keys.Where(k => !shown.Contains(k))
                .Select(k => k.File + ": " + k.Shown).ToList();
            Assert.True(staleShown.Count == 0,
                "ShownNotInName entries that no longer match anything (remove them):\n" + string.Join("\n", staleShown));

            var relabels = AllUnrenamedRelabels().Select(a => (a.File, a.Control)).ToHashSet();
            // A "*" entry covers every relabel in its file, and is stale when there are none.
            var staleRelabels = RelabelWithoutRename.Keys
                .Where(k => k.Control == "*" ? !relabels.Any(r => r.File == k.File) : !relabels.Contains(k))
                .Select(k => k.File + ": " + k.Control).ToList();
            Assert.True(staleRelabels.Count == 0,
                "RelabelWithoutRename entries that no longer match anything (remove them):\n" + string.Join("\n", staleRelabels));
        }

        /// <summary>The rule is containment: a name may add words, never drop them.</summary>
        [Fact]
        public void The_rule_is_containment_and_ignores_case_access_keys_and_punctuation()
        {
            Assert.True(SpokenNameScan.NameContainsShown("No, cancel", "_No"));
            Assert.True(SpokenNameScan.NameContainsShown("Keep a diagnostic log, recommended",
                "Keep a diagnostic log (recommended)"));
            Assert.True(SpokenNameScan.NameContainsShown("Reset All", "Reset _All..."));
            Assert.True(SpokenNameScan.NameContainsShown("Disconnect without saving", "_Disconnect without saving"));

            Assert.False(SpokenNameScan.NameContainsShown("Yes, this radio is mine", "It is _my radio"));
            Assert.False(SpokenNameScan.NameContainsShown("No, cancel", "_Disconnect without saving"));
            // A word, not a substring: "Temp" is not "Temperature".
            Assert.False(SpokenNameScan.NameContainsShown("PA Temperature", "PA Temp"));
        }

        /// <summary>
        /// Positive control on a known mismatch, in XAML and in each shape of
        /// code-built control the scan claims to see.
        /// </summary>
        [Fact]
        public void The_scan_sees_each_shape_it_claims_to_see()
        {
            var xaml = SpokenNameScan.XamlControls("t.xaml",
                "<Button x:Name=\"A\" Content=\"_No\" AutomationProperties.Name=\"No, cancel\"/>\n"
                + "<TabItem Header=\"Notifications\" AutomationProperties.Name=\"Verbosity settings\">\n"
                + "<Button Content=\"{Binding X}\" AutomationProperties.Name=\"Bound\"/>").ToList();
            Assert.Equal(2, xaml.Count);
            Assert.True(SpokenNameScan.NameContainsShown(xaml[0].SpokenName, xaml[0].Shown));
            Assert.False(SpokenNameScan.NameContainsShown(xaml[1].SpokenName, xaml[1].Shown));

            const string code = @"
                var later = new Button
                {
                    Content = ""_Not now"",
                    MinWidth = 100,
                };
                AutomationProperties.SetName(later, ""Wait"");
                var mine = MakeAnswer(""It is _my radio"", ""Yes, mine"", 1);
                private Button MakeAnswer(string label, string automationName, int value)
                {
                    var button = new Button { Content = label };
                    AutomationProperties.SetName(button, automationName);
                    return button;
                }";
            var built = SpokenNameScan.CodeBuiltControls("t.cs", code).ToList();
            Assert.Contains(built, c => c.Shown == "_Not now" && c.SpokenName == "Wait");
            Assert.Contains(built, c => c.Shown == "It is _my radio" && c.SpokenName == "Yes, mine");
        }

        /// <summary>
        /// Positive control on the real tree: the two mismatches the probe run
        /// of 2026-10-29 heard on the live app, on the radio ownership question,
        /// are found by the scan. Their wording is Noel's, so they are listed,
        /// not reworded.
        /// </summary>
        [Fact]
        public void The_scan_finds_the_ownership_question_answers_heard_on_the_live_app()
        {
            var found = AllMismatches();
            Assert.Contains(found, m => m.File == "JJFlexWpf/Dialogs/RadioOwnershipDialog.cs"
                                        && m.Shown == "It is _my radio" && m.Name == "Yes, this radio is mine");
            Assert.Contains(found, m => m.File == "JJFlexWpf/Dialogs/RadioOwnershipDialog.cs"
                                        && m.Shown == "It is s_omeone else's"
                                        && m.Name == "No, this radio belongs to someone else");
            // "_Not now" announced "Not now, do not answer" contains its label.
            Assert.DoesNotContain(found, m => m.File == "JJFlexWpf/Dialogs/RadioOwnershipDialog.cs"
                                             && m.Shown == "_Not now");
        }

        /// <summary>
        /// Positive control for the code half: each assignment is judged on its
        /// own, so one SetName does not excuse a second relabel, and a rename
        /// with other text is not a rename.
        /// </summary>
        [Fact]
        public void A_second_relabel_is_not_covered_by_the_first_ones_rename()
        {
            var named = new Dictionary<string, string> { ["NoButton"] = "No, cancel", ["Go"] = "Go" };
            const string code = @"
                void A()
                {
                    NoButton.Content = noLabel;
                    AutomationProperties.SetName(NoButton, noLabel.Replace(""_"", """"));
                }
                void B()
                {
                    NoButton.Content = ""_Later"";
                }
                void C()
                {
                    Go.Content = ""_Stop"";
                    AutomationProperties.SetName(Go, ""Halt"");
                }";
            var all = SpokenNameScan.Assignments("t.cs", code, named).ToList();
            Assert.Equal(3, all.Count);
            Assert.True(all[0].Rename != null && SpokenNameScan.SameText(all[0].Shown, all[0].Rename!));
            Assert.Null(all[1].Rename);
            Assert.False(SpokenNameScan.SameText(all[2].Shown, all[2].Rename!));
        }
    }
}
