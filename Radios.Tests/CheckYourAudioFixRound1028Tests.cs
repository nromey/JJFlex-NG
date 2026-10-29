using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The 2026-10-28 fix round's rulings for Check your audio and the Audio
    /// Workshop (#835), pinned where they live in source.
    /// </summary>
    /// <remarks>
    /// Source rules, for the reason <c>CheckYourAudioWiringTests</c> gives: each
    /// lives in a WPF window this suite must never construct. What they cannot
    /// see is whether a press does what the source says; every one has a bench
    /// step in the round's report. Each was shown red against the round's base,
    /// <c>4191ea3b</c>.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class CheckYourAudioFixRound1028Tests
    {
        private static string Source(string path)
        {
            string full = Path.Combine(IntegrationPassTree.Root, path);
            Assert.True(File.Exists(full), "Expected " + path + ". It has moved, so the rule reading it proves nothing.");
            return File.ReadAllText(full).Replace("\r\n", "\n");
        }

        private static string Dialog(string part) => Source("JJFlexWpf/Dialogs/" + part + ".cs");

        private static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "Could not find \"" + signature + "\"; the rule built on it is proving nothing.");
            int open = source.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("Unbalanced braces after " + signature);
        }

        // ── Item 3: a microphone change, and which window opened the picker ──

        [Fact]
        public void A_save_in_Audio_Devices_tells_every_open_window_whichever_door_opened_it()
        {
            string picker = Dialog("AudioDevicesDialog.xaml");
            string ok = Body(picker, "private void OkButton_Click(object sender, RoutedEventArgs e)");
            int notice = ok.IndexOf("TransmitGainControls.MicrophoneChoiceMayHaveChanged(null);", StringComparison.Ordinal);
            int result = ok.IndexOf("DialogResult = true;", StringComparison.Ordinal);
            Assert.True(notice >= 0 && result > notice, "OK tells every binding, after the devices are saved and before it closes");
        }

        [Fact]
        public void The_trace_names_the_window_that_opened_the_picker_how_it_closed_and_which_window_rebound()
        {
            string picker = Dialog("AudioDevicesDialog.xaml");
            Assert.Contains("Audio Devices: opened from", Body(picker, "public static bool ShowPicker("));
            Assert.Contains("Audio Devices: closed with OK", picker);
            Assert.Contains("Audio Devices: closed WITHOUT saving", picker);

            string gain = Dialog("TransmitGainControls");
            Assert.Contains("\"Transmit gain (\" + Host + \"): binding the Windows level to", gain);
            Assert.Contains("host: \"Check your audio\"", Dialog("CheckYourAudioDialog"));
            Assert.Contains("host: \"Audio Workshop\"", Dialog("AudioWorkshopDialog.xaml"));
        }

        // ── Items 4 and 5: the "About this method" line ──

        [Fact]
        public void The_about_line_is_one_short_sentence_per_method_and_not_the_full_description()
        {
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var m in AudioCheckMethods.DisplayOrder)
            {
                string about = AudioCheckMethods.About(m);
                Assert.NotEqual(AudioCheckMethods.AboutKey(m), about);   // present in the lexicon
                Assert.EndsWith(".", about);
                Assert.DoesNotContain(". ", about, StringComparison.Ordinal);   // one sentence
                Assert.True(about.Length < AudioCheckMethods.Description(m).Length, m + ": the short sentence is not shorter than the description");
                Assert.True(seen.Add(about), m + " says the same as another method");
            }
        }

        [Fact]
        public void The_about_line_shows_the_short_sentence_and_carries_it_in_its_accessible_name()
        {
            string status = Body(Dialog("CheckYourAudioDialog.Check"), "private void UpdateMethodStatus()");
            Assert.Contains("AudioCheckMethods.About(SelectedMethod)", status);
            Assert.DoesNotContain("AudioCheckMethods.Description(", status);
            Assert.DoesNotContain("MissingMethodsSentence", status);
            Assert.Contains("AutomationProperties.SetName(_methodStatus", status);
            // The missing-methods sentence moved to Ctrl+F1 on the method.
            Assert.Contains("MissingMethodsSentence(_rig)",
                Body(Dialog("CheckYourAudioDialog.Check"), "private void UpdateMethodHelp(AudioCheckListenMethods method)"));
        }

        // ── Item 8: the Workshop's Check Microphone button is gone ──

        [Fact]
        public void The_Workshop_has_no_Check_Microphone_button_and_nothing_points_at_one()
        {
            foreach (var part in new[] { "AudioWorkshopDialog.TxAudio", "AudioWorkshopDialog.xaml" })
            {
                string src = Dialog(part);
                Assert.DoesNotContain("\"Check Microphone...\"", src);
                Assert.DoesNotContain("OpenMicrophoneCheck", src);
            }
            foreach (var page in new[] { "AudioWorkshop/AudioWorkshop.md", "JJFlexibleHome/HomeWithoutARadio.md" })
                Assert.DoesNotContain("the microphone check and", Source("docs/help/md/" + page).ToLowerInvariant());
            // Positive control: the capability moved, it did not vanish.
            Assert.Contains("AudioCheckListenMethods.ComputerMic", Source("Radios/AudioCheckMethods.cs"));
        }

        // ── Item 6: the method field is a sequence; nothing else moved ──

        [Fact]
        public void Only_the_method_field_is_a_sequence_and_it_plays_the_existing_boundary_sound()
        {
            string build = Body(Dialog("CheckYourAudioDialog.Check"), "private void BuildCheckDialog()");
            Assert.Contains("_listenMethodControl.Wraps = false;", build);
            Assert.Contains("_listenMethodControl.DownMovesForward = true;", build);
            Assert.Contains("_listenMethodControl.HomeEndJump = true;", build);
            Assert.Contains("EarconPlayer.FilterBoundaryHitTone(", build);
            // No other file sets the options: every other cycle field keeps its keys.
            var setters = Directory.GetFiles(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Where(f => File.ReadAllText(f).Contains(".DownMovesForward = true"))
                .Select(Path.GetFileName).ToList();
            Assert.Equal(new[] { "CheckYourAudioDialog.Check.cs" }, setters);
        }

        // ── Items 9 and 10: Close in both windows, Alt+C in the Workshop ──

        [Fact]
        public void Both_windows_have_a_tabbable_Close_that_takes_the_title_bar_route_not_Escape()
        {
            string xaml = Source("JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml");
            Assert.Contains("x:Name=\"CloseButton\"", xaml);
            Assert.Contains("Click=\"Close_Click\"", xaml);
            Assert.DoesNotContain("IsCancel", xaml);   // Escape keeps its own route
            Assert.Contains("private void Close_Click(object sender, RoutedEventArgs e) => Close();",
                Dialog("AudioWorkshopDialog.xaml"));

            string build = Body(Dialog("CheckYourAudioDialog.Check"), "private void BuildCheckDialog()");
            Assert.Contains("_closeButton.Click += (s, e) => Close();", build);
            Assert.DoesNotContain("IsCancel", build);
            // Last in the window, outside every section.
            Assert.True(build.IndexOf("CheckContent.Children.Add(_closeButton);", StringComparison.Ordinal)
                > build.IndexOf("BuildOffAirResults();", StringComparison.Ordinal));
        }

        [Fact]
        public void Alt_C_in_the_Workshop_opens_Check_your_audio_with_Alt_alone()
        {
            string keys = Body(Dialog("AudioWorkshopDialog.xaml"), "protected override void OnPreviewKeyDown(KeyEventArgs e)");
            Assert.Contains("var pressed = e.Key == Key.System ? e.SystemKey : e.Key;", keys);
            Assert.Contains("if (pressed == Key.C && Keyboard.Modifiers == ModifierKeys.Alt)", keys);
            Assert.Contains("CheckYourAudio_Click(this, new RoutedEventArgs());", keys);
            // Documented where the Workshop's keys are listed.
            Assert.Contains("**Alt+C** — Open Check your audio", Source("docs/help/md/KeyboardReference/KeyboardReference.md"));
            Assert.Contains("`Alt+C` — open Check your audio", Source("docs/help/md/AudioWorkshop/AudioWorkshop.md"));
        }

        // ── Item 11: the Diagnostics page's short labels and their Ctrl+F1 ──

        [Theory]
        [InlineData("audio.iq.diagnostic_start", "Diagnostic take", "audio.diagnostics.help.diagnostic_take")]
        [InlineData("audio.iq.preflight_button", "Check IQ capacity", "audio.diagnostics.help.capacity")]
        [InlineData("audio.iq.detune_plus", "Play 1 kHz higher", "audio.diagnostics.help.detune_plus")]
        [InlineData("audio.iq.detune_minus", "Play 1 kHz lower", "audio.diagnostics.help.detune_minus")]
        [InlineData("audio.iq.recovery_button", "Review unrestored settings", "audio.diagnostics.help.recovery")]
        public void Each_bench_button_has_its_short_label_and_a_Ctrl_F1_explanation(string label, string text, string help)
        {
            Assert.Equal(text, Lexicon.Get(label));
            string explanation = Lexicon.Get(help);
            Assert.NotEqual(help, explanation);
            Assert.EndsWith(".", explanation);
        }

        [Fact]
        public void Every_control_on_the_Diagnostics_page_has_Ctrl_F1_and_none_uses_HelpText()
        {
            string bench = Dialog("AudioWorkshopDialog.BenchTools");
            string diag = Dialog("AudioWorkshopDialog.Diagnostics");
            Assert.Contains("JJFlexHelp.SetText(button, Lexicon.Get(helpKey));",
                Body(bench, "private Button BenchButton(string key, string helpKey, Action click)"));
            foreach (var control in new[] { "_benchStatus", "_benchRate", "_benchSeparate", "_benchFdx", "_benchAntenna", "_benchUnavailable" })
                Assert.Contains("JJFlexHelp.SetText(" + control + ",", bench);
            foreach (var control in new[] { "rxButton", "txButton", "_copyEvidenceButton", "_rxAdvisoryBox", "_txReportBox", "_evidenceBox" })
                Assert.Contains("JJFlexHelp.SetText(" + control + ",", diag);
            Assert.DoesNotContain("SetHelpText(", bench);
            Assert.DoesNotContain("SetHelpText(", diag);
        }

        // ── Item 12: the Fix tool button in both windows ──

        [Fact]
        public void Both_windows_open_the_Fix_tool_the_way_the_Tools_menu_does_and_wait_for_a_running_check()
        {
            Assert.Contains("Dialogs.FixerDialog.Show(() => Rig, System.Windows.Window.GetWindow(_window))",
                Source("JJFlexWpf/NativeMenuBar.cs"));   // the route being matched

            string check = Dialog("CheckYourAudioDialog.Check");
            string open = Body(check, "private void OpenFixTool()");
            Assert.Contains("FixerDialog.Show(", open);
            Assert.Contains("audio.fix_tool.wait_for_check", open);
            Assert.True(open.IndexOf("audio.fix_tool.wait_for_check", StringComparison.Ordinal)
                < open.IndexOf("FixerDialog.Show(", StringComparison.Ordinal), "refuses before it opens");
            Assert.Contains("JJFlexHelp.SetText(_fixToolButton,", check);
            Assert.Contains("\"audio.fix_tool.help\"", check);

            string diag = Dialog("AudioWorkshopDialog.Diagnostics");
            string open2 = Body(diag, "private void OpenFixTool()");
            Assert.Contains("FixerDialog.Show(", open2);
            Assert.Contains("CheckYourAudioDialog.ACheckIsStartingOrActive", open2);
            Assert.Contains("JJFlexHelp.SetText(fixTool,", diag);
            Assert.Contains("\"audio.fix_tool.help\"", diag);
            Assert.DoesNotContain("SetHelpText(fixTool", diag);
        }

        [Theory]
        [InlineData("audio.check.could_not_start")]
        [InlineData("audio.check.radio_did_not_key")]
        [InlineData("audio.check.no_recording_available")]
        [InlineData("audio.fix_tool.pointer")]
        public void A_failed_checks_sentence_names_the_Fix_tool_button(string key)
        {
            Assert.Contains("Fix tool button", Lexicon.Get(key), StringComparison.Ordinal);
        }

        [Fact]
        public void The_Fix_tool_label_is_short_and_its_Ctrl_F1_is_Noels_sentence()
        {
            Assert.Equal("Fix tool", Lexicon.Get("audio.fix_tool.label"));
            Assert.Equal("Something wrong with your audio? Use this button to help you figure out what's wrong.",
                Lexicon.Get("audio.fix_tool.help"));
        }

        // ── Item 13: the Check your audio button in Home's Audio and Slice expander ──

        [Fact]
        public void Homes_audio_expander_has_Check_your_audio_straight_after_the_mic_verdict_by_the_same_route()
        {
            string panel = Source("JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs");
            int verdict = panel.IndexOf("AudioContent.Children.Add(_micVerdictDisplay);", StringComparison.Ordinal);
            int button = panel.IndexOf("AudioContent.Children.Add(checkYourAudio);", StringComparison.Ordinal);
            int recorder = panel.IndexOf("BuildComputerRecorderControls();", verdict, StringComparison.Ordinal);
            Assert.True(verdict >= 0 && button > verdict && recorder > button, "the verdict, then the button, then the recorder");
            Assert.Contains("checkYourAudio.Click += (s, e) => Dialogs.CheckYourAudioDialog.ShowOrFocus(_rig);", panel);
            Assert.DoesNotContain("ShowOrFocusAndStartCheck(_rig)", panel);   // opening it never starts a check
            Assert.Contains("JJFlexHelp.SetText(checkYourAudio,", panel);
        }
    }
}
