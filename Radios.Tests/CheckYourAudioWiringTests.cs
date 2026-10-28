using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The rulings behind Check your audio (#835), pinned where they live in
    /// source: the doors, what moved out of the Audio Workshop and where it
    /// went, and the safety properties of the methods.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Source rules, deliberately.</b> Every one of these lives in a WPF
    /// window this suite must never construct, and none is a compiled shape:
    /// "the chord opens and never starts", "this take reads only these
    /// options", "nothing else reaches the listening slice". A comment asking
    /// for any of them is obeyed until the first editor who did not read it.
    /// </para>
    /// <para>
    /// <b>What they cannot see:</b> whether a press does what the source says.
    /// Every one of these needs its bench step too (the report lists them).
    /// Against the base commit (<c>44cbe31a</c>) every rule here is red: the
    /// files it reads did not exist.
    /// </para>
    /// </remarks>
    public sealed class CheckYourAudioWiringTests
    {
        private static string Source(string path)
        {
            string full = Path.Combine(IntegrationPassTree.Root, path);
            Assert.True(File.Exists(full), "Expected " + path + ". It has moved, so the rule reading it proves nothing.");
            return File.ReadAllText(full).Replace("\r\n", "\n");
        }

        private static string Dialog(string part) => Source("JJFlexWpf/Dialogs/" + part + ".cs");

        /// <summary>The body of a member, from its signature to the matching close brace.</summary>
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

        /// <summary>The text of a switch arm, from its case label to its break.</summary>
        private static string Arm(string source, string label)
        {
            int at = source.IndexOf(label, StringComparison.Ordinal);
            Assert.True(at >= 0, "No arm " + label);
            int end = source.IndexOf("break;", at, StringComparison.Ordinal);
            return source.Substring(at, end - at);
        }

        // ── The doors (14:59) and what they do (13:57) ─────────────────────

        [Fact]
        public void The_JJ_key_Alt_C_opens_the_window_and_never_starts_the_check()
        {
            string keys = Source("JJFlexWpf/KeyCommands.cs");
            string arm = Arm(keys, "case Keys.C | Keys.Alt:");
            Assert.Contains("OpenCheckYourAudioHandler();", arm);
            string handler = Body(keys, "private void OpenCheckYourAudioHandler()");
            Assert.Contains("Dialogs.CheckYourAudioDialog.ShowOrFocus(rig)", handler);
            Assert.DoesNotContain("StartCheck", handler);
            Assert.DoesNotContain("ToggleAudioCheck", handler);

            // Positive control: the reader can see a start when there is one.
            Assert.Contains("ShowOrFocusAndStartCheck", Body(keys, "private void StartAudioCheckHandler()"));
        }

        [Fact]
        public void StartAudioCheck_stays_globally_unbound_with_its_reason()
        {
            string keys = Source("JJFlexWpf/KeyCommands.cs");
            Assert.Contains("new(Keys.None, CommandValues.StartAudioCheck, KeyScope.Radio), // unbound: CommandFinderOnly", keys);
            Assert.Contains("[CommandValues.StartAudioCheck] = new(UnboundReason.CommandFinderOnly,", keys);
        }

        [Fact]
        public void The_Audio_menu_carries_one_row_beside_the_Workshop_and_its_letter_is_its_own()
        {
            var rows = AudioMenuLayout.Entries.ToList();
            int workshop = rows.FindIndex(r => r.Id == "audio-workshop");
            int check = rows.FindIndex(r => r.Id == "check-your-audio");
            Assert.True(workshop >= 0 && check == workshop + 1, "Check Your Audio sits right after Audio Workshop");
            Assert.False(rows[check].NeedsRadio);
            Assert.Equal("Ctrl+J, Alt+C", rows[check].Accelerator);
            // First-letter navigation reaches it in one press only if no other
            // row starts with C (#297 is what happens when two rows share one).
            var cRows = rows.Where(r => r.Kind != AudioMenuEntryKind.Separator
                && r.Label.Replace("&", "").StartsWith("C", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Single(cRows);
            Assert.Contains("CheckYourAudioDialog.ShowOrFocus(Rig)", Source("JJFlexWpf/NativeMenuBar.cs"));
        }

        [Fact]
        public void The_Workshop_keeps_a_button_that_opens_the_window_and_hosts_no_check()
        {
            string xaml = Source("JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml");
            Assert.DoesNotContain("HearYourselfTab", xaml);
            Assert.Contains("Click=\"CheckYourAudio_Click\"", xaml);
            string shell = Dialog("AudioWorkshopDialog.xaml");
            Assert.Contains("private void CheckYourAudio_Click(object sender, RoutedEventArgs e) =>\n        CheckYourAudioDialog.ShowOrFocus(_rig);", shell);
            Assert.DoesNotContain("case Key.Enter:", Body(shell, "protected override void OnPreviewKeyDown(KeyEventArgs e)"));
            Assert.False(File.Exists(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "AudioWorkshopDialog.AudioCheck.cs")));
            foreach (string file in Directory.GetFiles(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs"), "AudioWorkshopDialog*.cs"))
            {
                string text = File.ReadAllText(file);
                Assert.DoesNotContain("class AudioCheckSession", text);
                Assert.DoesNotContain("ToggleLock()", text);
            }
            // The door's wiring in MainWindow did not move: the Workshop forwards.
            Assert.Contains("internal static bool AnAudioCheckIsActive => CheckYourAudioDialog.AnAudioCheckIsActive;", shell);
        }

        // ── The layout (13:45) ─────────────────────────────────────────────

        [Fact]
        public void Start_is_alone_in_the_first_section_and_focus_lands_on_the_method()
        {
            string check = Dialog("CheckYourAudioDialog.Check");
            string build = Body(check, "private void BuildCheckDialog()");
            int start = build.IndexOf("_sections.OpenRadio(CheckContent, \"Start\")", StringComparison.Ordinal);
            int button = build.IndexOf("_startCheckButton = new Button", StringComparison.Ordinal);
            int method = build.IndexOf("_sections.OpenRadio(CheckContent, \"Method\")", StringComparison.Ordinal);
            Assert.True(start >= 0 && button > start && method > button, "Start, its button, then the Method section");
            Assert.Equal(1, build.Substring(start, method - start).Split("_sections.Add(").Length - 1);
            Assert.Contains("_listenMethodControl.Focus()",
                Body(Dialog("CheckYourAudioDialog"), "protected override void FocusFirstControl()"));
        }

        [Fact]
        public void Ctrl_Enter_and_the_first_Escape_live_in_the_window()
        {
            // The first Escape's arm only. The second press's RESULT is
            // CheckYourAudioEscapeTests' (fix round 1): this test once carried
            // "two-stage" in its name while checking one stage.
            string keys = Body(Dialog("CheckYourAudioDialog"), "protected override void OnPreviewKeyDown(KeyEventArgs e)");
            Assert.Contains("Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter", keys);
            Assert.Contains("ToggleAudioCheck();", keys);
            int escape = keys.IndexOf("case AudioCheckEscapeAction.UnkeyAndStay:", StringComparison.Ordinal);
            Assert.True(escape >= 0);
            string first = keys.Substring(escape, keys.IndexOf("return;", escape, StringComparison.Ordinal) - escape);
            Assert.Contains("_session.StopCheck();", first);
            Assert.Contains("e.Handled = true;", first);
        }

        [Fact]
        public void Closing_this_window_stops_the_check_and_puts_everything_back()
        {
            string shell = Dialog("CheckYourAudioDialog");
            int closed = shell.IndexOf("Closed += (s, e) =>", StringComparison.Ordinal);
            Assert.True(closed >= 0);
            string handler = shell.Substring(closed, shell.IndexOf("};", closed, StringComparison.Ordinal) - closed);
            foreach (string step in new[] { "CancelCheckStart();", "_session?.ForceEnd(", "DisarmTone(speak: false);",
                                            "DisarmReference(speak: false);", "DetachTakePlayback();", "_gain.Dispose();",
                                            "if (ReferenceEquals(_instance, this)) _instance = null;" })
                Assert.Contains(step, handler);
        }

        // ── The methods (13:51, 13:57, 14:57) ──────────────────────────────

        [Fact]
        public void Test_your_microphone_always_runs_at_zero_power()
        {
            string toggle = Body(Dialog("CheckYourAudioDialog.Check"), "public void ToggleAudioCheck()");
            Assert.Contains("method == AudioCheckListenMethods.MicTest ? AudioCheckPowerModes.DummyLoad", toggle);
        }

        [Fact]
        public void Every_method_gives_the_mic_verdict_Off_the_air_included()
        {
            string check = Dialog("CheckYourAudioDialog.Check");
            Assert.DoesNotContain("_method != AudioCheckListenMethods.OffAir", check);
            string reading = Body(check, "private void UpdateMicReading()");
            Assert.Contains("MicAudioReport.Compose(rig, \"Mic audio last transmit:\", max, live: false)", reading);
            // Off the air's measurements join the same box (13:57).
            Assert.Contains("_offAirReading", reading);
        }

        [Fact]
        public void The_off_air_take_reads_only_its_rate_and_hear_yourself()
        {
            string start = Body(Dialog("CheckYourAudioDialog.OffAir"), "private async Task StartOffAirAsync(PttSafetyController ptt)");
            Assert.Contains("new ListenbackOptions(Rate: rate, OffAir: true,", start);
            Assert.Contains("HearSelfDuringOffAir: _iqHearSelfDuringTake?.IsChecked == true", start);
            foreach (string other in new[] { "SeparatePan", "FullDuplex", "ReceiveAntenna", "_iqSeparate", "_iqFdx", "_iqAntenna" })
                Assert.DoesNotContain(other, start);
            // The three it does not read live with the one take that reads them.
            string diag = Body(Dialog("AudioWorkshopDialog.BenchTools"), "private async Task ToggleDiagnosticAsync()");
            foreach (string own in new[] { "_benchSeparate", "_benchFdx", "_benchAntenna", "_benchRate" })
                Assert.Contains(own, diag);
        }

        [Fact]
        public void The_loopback_method_records_its_listening_slice_and_stays_up_while_it_plays()
        {
            string check = Dialog("CheckYourAudioDialog.Check");
            string start = Body(check, "public async Task<bool> StartAsync(AudioCheckStartGate.Lease start)");
            Assert.Contains("_recorderTarget = SliceRecorderTarget.LoopbackEars;", start);
            string post = Body(check, "private void TickPostUnkey(FlexBase rig)");
            int loop = post.IndexOf("if (_method == AudioCheckListenMethods.Loopback)", StringComparison.Ordinal);
            Assert.True(loop >= 0 && post.IndexOf("_phase = Phase.PlayingBack;", loop, StringComparison.Ordinal) > loop);
            Assert.Contains("AudioCheckListenMethods.Loopback, AudioCheckPowerModes.LowPower, 1,",
                Body(check, "private async Task StartLoopbackCheckAsync()"));
        }

        [Fact]
        public void Nothing_else_reaches_the_listening_slice_now_the_slice_buttons_are_gone()
        {
            // The Record and Play active-slice buttons' one unique job was the
            // Loopback Check's listening slice. The Loopback method does that
            // itself now, and nothing else in the app names that target.
            var uses = Directory.GetFiles(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Where(f => File.ReadAllText(f).Contains("SliceRecorderTarget.LoopbackEars"))
                .Select(Path.GetFileName).ToList();
            Assert.Equal(new[] { "CheckYourAudioDialog.Check.cs" }, uses);
            foreach (string file in Directory.GetFiles(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs"), "*.cs"))
            {
                string text = File.ReadAllText(file);
                Assert.DoesNotContain("record_active_slice", text);
                Assert.DoesNotContain("play_active_slice", text);
                Assert.DoesNotContain("compare_previous", text);
            }
        }

        [Fact]
        public void The_bench_tools_are_on_Diagnostics_and_not_in_the_check()
        {
            string bench = Dialog("AudioWorkshopDialog.BenchTools");
            foreach (string tool in new[] { "\"diagnostic_start\"", "\"preflight_button\"", "\"detune_plus\"", "\"detune_minus\"" })
                Assert.Contains(tool, bench);
            Assert.Contains("BuildBenchToolsSection();", Dialog("AudioWorkshopDialog.Diagnostics"));
            foreach (string part in new[] { "CheckYourAudioDialog", "CheckYourAudioDialog.Check", "CheckYourAudioDialog.OffAir",
                                            "CheckYourAudioDialog.TestTone", "CheckYourAudioDialog.ReferenceAudio" })
            {
                string text = Dialog(part);
                foreach (string tool in new[] { "diagnostic_start", "preflight_button", "detune_plus", "detune_minus" })
                    Assert.DoesNotContain(tool, text);
            }
            // Over SmartLink the section gives way to one sentence (13:39).
            Assert.Contains("_benchSection.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;",
                Body(bench, "private void UpdateBenchToolsAvailability()"));
        }

        [Fact]
        public void Never_two_sources_armed_at_once()
        {
            string tone = Body(Dialog("CheckYourAudioDialog.TestTone"), "private void ToneArmChanged(bool armed)");
            int guard = tone.IndexOf("rig.TxFilePlaying || rig.TxReferenceArmed", StringComparison.Ordinal);
            int arm = tone.IndexOf("WithInjectionPath(", StringComparison.Ordinal);
            Assert.True(guard >= 0 && arm > guard, "the tone refuses while the reference is armed, before it arms");
            string reference = Body(Dialog("CheckYourAudioDialog.ReferenceAudio"), "private void ReferenceArmChanged(bool armed)");
            int refGuard = reference.IndexOf("if (rig.TxToneEngaged)", StringComparison.Ordinal);
            Assert.True(refGuard >= 0 && reference.IndexOf("WithInjectionPath(", StringComparison.Ordinal) > refGuard);
        }

        [Fact]
        public void Both_windows_build_their_sections_through_one_implementation()
        {
            Assert.Contains("private readonly DialogSections _sections = new();", Dialog("AudioWorkshopDialog.Controls"));
            Assert.Contains("private readonly DialogSections _sections = new();", Dialog("CheckYourAudioDialog"));
            foreach (string part in new[] { "CheckYourAudioDialog", "CheckYourAudioDialog.Check", "CheckYourAudioDialog.OffAir",
                                            "CheckYourAudioDialog.TestTone", "CheckYourAudioDialog.ReferenceAudio" })
            {
                string text = Dialog(part);
                Assert.DoesNotContain("new GroupBox", text);
                Assert.DoesNotContain("SetHeadingLevel", text);
            }
        }

        [Fact]
        public void Mic_gain_and_the_Windows_level_are_one_implementation_hosted_twice()
        {
            Assert.Contains("_gain = new TransmitGainControls(", Dialog("AudioWorkshopDialog.xaml"));
            Assert.Contains("_gain = new TransmitGainControls(", Dialog("CheckYourAudioDialog"));
            // The binding of the level control to a Windows endpoint exists once.
            // (Microphone setups and the Fixer also look an endpoint up, to apply
            // or read a level; that is a different job and not a second control.)
            var binders = Directory.GetFiles(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs"), "*.cs")
                .Where(f => File.ReadAllText(f).Contains("private void BindPcLevel()"))
                .Select(Path.GetFileName).ToList();
            Assert.Equal(new[] { "TransmitGainControls.cs" }, binders);

            // One implementation is not one binding (fix round 1): a microphone
            // changed in the Workshop tells every live binding, and a change
            // made where neither window hears (Audio Devices from the Audio
            // menu) is caught by the saved choice's write time on the poll.
            // JJFlexWpf.Tests.TransmitGainBindingTests runs the first.
            Assert.Contains("TransmitGainControls.MicrophoneChoiceMayHaveChanged(_gain);", Dialog("AudioWorkshopDialog.TxAudio"));
            string gain = Dialog("TransmitGainControls");
            Assert.Contains("if (pcSource && DeviceFileStamp() != _boundFileStamp) BindPcLevel();",
                Body(gain, "public bool Poll(FlexBase rig, Action? fallbackFocus)"));
            Assert.Contains("_boundFileStamp = DeviceFileStamp();", Body(gain, "private void BindPcLevel()"));
        }
    }
}
