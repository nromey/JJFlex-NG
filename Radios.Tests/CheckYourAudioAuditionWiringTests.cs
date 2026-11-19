using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The audition's window side (#262, Sprint 49 Track Q phase A), read from
    /// the source: it keys nothing, it records only through the narrator, it
    /// plays only when a button is pressed, and its take is thrown away when the
    /// window closes unless it was kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source pins, because the dialog cannot be run here: pressing Start would
    /// open the operator's microphone, and opening the window would put it on
    /// his desktop. The behaviour behind each pin is tested windowless in
    /// <c>ChainAuditionTakeTests</c> and <c>ChainAuditionAnalysisTests</c>.
    /// </para>
    /// <para>
    /// <b>What these cannot see:</b> what NVDA says, the earcons, and that the
    /// radio really stays in receive. Those are the harness walk's: read the
    /// radio's transmit state with RigSurface and the trace throughout a take.
    /// </para>
    /// </remarks>
    public sealed class CheckYourAudioAuditionWiringTests
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

        private static string Code(string source) =>
            // Comments out, so a sentence about the push-to-talk controller is
            // not mistaken for a use of it.
            Regex.Replace(source, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

        [Fact]
        public void Start_sends_the_audition_away_before_anything_asks_for_the_push_to_talk_controller()
        {
            string toggle = Body(Dialog("CheckYourAudioDialog.Check"), "public void ToggleAudioCheck()");
            int audition = toggle.IndexOf("SelectedMethod == AudioCheckListenMethods.Audition", StringComparison.Ordinal);
            int ptt = toggle.IndexOf("PttControllerSource", StringComparison.Ordinal);
            int session = toggle.IndexOf("new AudioCheckSession", StringComparison.Ordinal);
            Assert.True(audition > 0, "ToggleAudioCheck has no audition branch");
            Assert.True(ptt > 0 && session > 0, "positive control: the controller and the session are still asked for below");
            Assert.True(audition < ptt, "the audition branch must come before the push-to-talk controller is read");
            string branch = toggle.Substring(audition, toggle.IndexOf("return;", audition, StringComparison.Ordinal) - audition);
            Assert.Contains("ToggleAudition();", branch);
        }

        [Fact]
        public void Nothing_in_the_audition_file_can_key_the_radio()
        {
            string code = Code(Dialog("CheckYourAudioDialog.Audition"));
            foreach (string forbidden in new[]
            {
                "Ptt", "AudioCheckSession", ".Transmit", "TuneDoors", "StartAudioCheckSessionAsync", "TrackStart(",
                "TxAudioConditioning", "TxNoiseChain", "Mox",
            })
                Assert.DoesNotContain(forbidden, code);
            // Positive control: the reader sees the file's code, not an empty string.
            Assert.Contains("ToggleAudition()", code);
        }

        [Fact]
        public void The_audition_records_only_through_the_narrator_into_its_own_folder()
        {
            string code = Code(Dialog("CheckYourAudioDialog.Audition"));
            // The narrator's check form (Track Q2): still the narrator, still the one way in.
            Assert.Contains("RecordingNarrator.StartForCheck(path)", code);
            Assert.DoesNotContain("RecordingNarrator.Start(", code);
            Assert.DoesNotContain("RecordingNarrator.Toggle(", code);
            Assert.DoesNotContain("new MicRecorder", code);
            Assert.DoesNotContain("new MicProbe", code);
            Assert.Contains("new ChainAuditionTake(ChainAuditionFolder.DefaultPath", code);
            // The take is never written where the kept recordings live.
            Assert.DoesNotContain("RecordingStore.FolderPath", code);
        }

        // ── Track Q2: it starts the way the other checks start ─────────────
        // Noel 2026-11-19 12:22: "start recording after speech is done, start it
        // with a beep or count it down like we've done in all the other stuff."
        // The behaviour is ChainAuditionStartTests'; these pin the window to it.

        [Fact]
        public void The_audition_opens_the_microphone_only_through_the_countdowns_gate()
        {
            string start = Body(Code(Dialog("CheckYourAudioDialog.Audition")), "private async Task StartAuditionAsync()");
            int count = start.IndexOf("BeginStartCountdown(AudioCheckListenMethods.Audition);", StringComparison.Ordinal);
            int open = start.IndexOf("Audition.StartAfterAsync(CountdownLetsKeyAsync,", StringComparison.Ordinal);
            Assert.True(count >= 0 && open > count, "the take must open through the countdown's gate, after the count begins");
            // Never around the gate: no direct start, and no other wait standing in for it.
            Assert.DoesNotContain("Audition.Start(", start);
            Assert.DoesNotContain("await CountdownLetsKeyAsync()", start);
            // Nothing is spoken as the take starts.
            Assert.DoesNotContain("Speak(", start);
        }

        [Fact]
        public void The_last_act_before_the_microphone_opens_stops_a_render_saying_nothing()
        {
            // Repair 1 (Codex's check of dd7c03b5): the window's last act before
            // capture opens is stopping a render still playing, and the
            // player's ordinary stop queues its sentence there, after the gate.
            // The behaviour is AuditionNextTakeDuringPlaybackTests' (JJFlexWpf.Tests,
            // the real player); this pins that it is the method the window passes.
            string start = Body(Code(Dialog("CheckYourAudioDialog.Audition")), "private async Task StartAuditionAsync()");
            Assert.Contains("beforeOpening: StopAuditionPlayback)", start);
            string stop = Body(Code(Dialog("CheckYourAudioDialog.Audition")), "private void StopAuditionPlayback()");
            Assert.Contains("RecordingPlayer.StopWithoutSpeaking();", stop);
            Assert.DoesNotContain("RecordingPlayer.Stop();", stop);
            Assert.DoesNotContain("Speak(", stop);
        }

        [Fact]
        public void The_take_is_marked_by_the_computer_microphone_checks_own_start_tone_and_settle()
        {
            string audition = Code(Dialog("CheckYourAudioDialog.Audition"));
            Assert.Contains("startMarker: EarconPlayer.RecorderStartBeep, markerSettle: StartToneSettle", audition);
            // Positive control: those are the computer microphone check's, the pattern being reused.
            string mic = Body(Code(Dialog("CheckYourAudioDialog.ComputerMic")), "private void StartComputerMicProbeNow()");
            Assert.Contains("EarconPlayer.RecorderStartBeep();", mic);
            Assert.Contains("new DispatcherTimer(StartToneSettle,", mic);
        }

        [Fact]
        public void The_narrators_check_form_opens_saying_nothing_and_says_nothing_while_it_records()
        {
            string narrator = Code(Source("JJFlexWpf/RecordingNarrator.cs"));
            string check = Body(narrator, "public static bool StartForCheck(string path)");
            Assert.Contains("Open(path, quiet: true)", check);
            string open = Body(narrator, "private static bool Open(string? path, bool quiet)");
            foreach (string body in new[] { check, open })
            {
                Assert.DoesNotContain("Speak(", body);
                Assert.DoesNotContain("Tone(", body);
            }
            // Its refusals are said aloud, before anything is captured.
            Assert.Contains("Refuse(", open);
            // While it runs: the quiet check comes before anything the tick says.
            string tick = Body(narrator, "private static void OnTick(object? sender, EventArgs e)");
            int quiet = tick.IndexOf("if (_quiet) return;", StringComparison.Ordinal);
            int firstSpeak = tick.IndexOf("Speak(", StringComparison.Ordinal);
            Assert.True(quiet > 0 && firstSpeak > quiet, "the elapsed time or the cap warning can be spoken into a check's take");
            // Positive control, and the record key's recording unchanged: it still
            // opens with the confirm tone and its sentence.
            string start = Body(narrator, "public static void Start(string purpose, string? path = null)");
            Assert.Contains("Open(path, quiet: false)", start);
            Assert.Contains("EarconPlayer.ConfirmTone();", start);
            Assert.Contains("\"audio.recording.started\"", start);
        }

        [Fact]
        public void A_render_plays_only_when_its_button_is_pressed()
        {
            // Ruled 2026-11-19 07:03: "Automatically playing that stuff could be long."
            string code = Code(Dialog("CheckYourAudioDialog.Audition"));
            Assert.Equal(1, Regex.Matches(code, @"PlayAfterClickAsync\(").Count);
            Assert.DoesNotContain("RecordingPlayer.Play", code);
            Assert.Contains("PlayAfterClickAsync(", Body(code, "private void PlayAuditionRender(AuditionRender render)"));
            // PlayAuditionRender is reached from the three buttons and nowhere else.
            Assert.Equal(1 + 3, Regex.Matches(code, @"PlayAuditionRender\(").Count);
            foreach (string render in new[] { "Cleaned", "Raw", "Removed" })
                Assert.Contains("() => PlayAuditionRender(AuditionRender." + render + "));", code);
            // Finishing a take shows the buttons and says the verdict; it plays nothing.
            string saved = Body(code, "private void OnAuditionRecordingSaved(string path)");
            Assert.DoesNotContain("Play", saved.Replace("Playable", ""));
        }

        [Fact]
        public void The_play_buttons_are_built_cleaned_first_then_raw_then_removed_then_keep()
        {
            string build = Body(Code(Dialog("CheckYourAudioDialog.Audition")), "private void BuildAuditionResults()");
            int cleaned = build.IndexOf("audio.audition.play.cleaned", StringComparison.Ordinal);
            int raw = build.IndexOf("audio.audition.play.raw", StringComparison.Ordinal);
            int removed = build.IndexOf("audio.audition.play.removed", StringComparison.Ordinal);
            int keep = build.IndexOf("audio.audition.keep\"", StringComparison.Ordinal);
            Assert.True(cleaned >= 0 && cleaned < raw && raw < removed && removed < keep);
            // Added to the section in ChainAudition.PlayOrder, which its own test pins.
            Assert.Contains("foreach (var button in AuditionButtonsInOrder()) _sections.Add(CheckContent, button!);", build);
        }

        [Fact]
        public void The_buttons_sit_in_the_Results_section_beside_Play_last_take()
        {
            string build = Body(Dialog("CheckYourAudioDialog.Check"), "private void BuildCheckDialog()");
            int results = build.IndexOf("_sections.OpenRadio(CheckContent, \"Results\")", StringComparison.Ordinal);
            int audition = build.IndexOf("BuildAuditionResults();", StringComparison.Ordinal);
            int fix = build.IndexOf("_fixToolButton = new Button", StringComparison.Ordinal);
            Assert.True(results >= 0 && results < audition && audition < fix);
        }

        [Fact]
        public void Closing_the_window_throws_the_take_away_after_its_playback_has_stopped()
        {
            string closed = Dialog("CheckYourAudioDialog");
            int detach = closed.IndexOf("DetachTakePlayback();", StringComparison.Ordinal);
            int end = closed.IndexOf("EndAudition();", StringComparison.Ordinal);
            Assert.True(detach > 0 && end > detach, "EndAudition must run, and after the window's playback stops");
            string endBody = Body(Dialog("CheckYourAudioDialog.Audition"), "private void EndAudition()");
            Assert.Contains("_audition.Discard()", endBody);
            Assert.Contains("ChainAuditionFolder.Clear(ChainAuditionFolder.DefaultPath)", endBody);
            // And the next time it opens, in case the app ended without closing it.
            Assert.Contains("ChainAuditionFolder.Clear(ChainAuditionFolder.DefaultPath);",
                Body(Dialog("CheckYourAudioDialog.Audition"), "private void BuildAuditionResults()"));
        }

        [Fact]
        public void Escape_stops_a_recording_audition_and_stays()
        {
            string keys = Body(Dialog("CheckYourAudioDialog"), "protected override void OnPreviewKeyDown(KeyEventArgs e)");
            int branch = keys.IndexOf("e.Key == Key.Escape && AuditionRecording", StringComparison.Ordinal);
            Assert.True(branch > 0);
            string arm = keys.Substring(branch, keys.IndexOf("return;", branch, StringComparison.Ordinal) - branch);
            Assert.Contains("_audition!.StopRecording();", arm);
            Assert.Contains("e.Handled = true;", arm);
            // Ctrl+Enter stops it too.
            Assert.Contains("checkLive |= AuditionRecording;", keys);
        }

        [Fact]
        public void Phase_A_wires_the_placeholder_chain_and_nothing_live()
        {
            string code = Code(Dialog("CheckYourAudioDialog.Audition"));
            Assert.Contains("AuditionProcessorSource { get; set; } = () => ChainNotConnectedYet.Instance;", code);
        }

        [Fact]
        public void An_audition_take_does_not_move_the_reference_pick()
        {
            string saved = Body(Dialog("CheckYourAudioDialog.ReferenceAudio"), "private void OnRecordingSaved(string path)");
            int guard = saved.IndexOf("if (InAuditionFolder(path)) return;", StringComparison.Ordinal);
            int refresh = saved.IndexOf("RefreshReferenceList", StringComparison.Ordinal);
            Assert.True(guard > 0 && guard < refresh);
        }

        [Fact]
        public void The_Fix_tool_door_stops_an_audition_recording_like_the_computer_microphone_check()
        {
            string hand = Body(Dialog("CheckYourAudioDialog.Check"), "internal static bool HandOffToFixTool(string door)");
            Assert.Contains("inst?.AuditionRecording == true", hand);
            Assert.Contains("inst._audition!.StopRecording();", hand);
        }
    }
}
