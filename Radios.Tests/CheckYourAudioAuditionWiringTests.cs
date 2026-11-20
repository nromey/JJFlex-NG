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
            Assert.Contains("RecordingNarrator.StartForCheck(path, holdFileStart, ChainAudition.TooShortSentence)", code);
            // Repair 1: the file's start reaches the narrator's recording for this path only.
            Assert.Contains("RecordingNarrator.BeginCheckFileAt(path, stopwatchTimestamp)", code);
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
            int open = start.IndexOf("Audition.StartAfterAsync(CountedThenRadioPlaysStoppedAsync,", StringComparison.Ordinal);
            Assert.True(count >= 0 && open > count, "the take must open through the countdown's gate, after the count begins");
            // Repair 2: the gate is the countdown's own, first, and nothing else stands in for it.
            string gate = Body(Code(Dialog("CheckYourAudioDialog.Audition")), "private async Task<bool> CountedThenRadioPlaysStoppedAsync()");
            Assert.StartsWith("{\n        if (!await CountdownLetsKeyAsync()) return false;", gate);
            // Never around the gate: no direct start, and no other wait standing in for it.
            Assert.DoesNotContain("Audition.Start(", start);
            Assert.DoesNotContain("await CountdownLetsKeyAsync()", start);
            // Nothing is spoken as the take starts.
            Assert.DoesNotContain("Speak(", start);
        }

        [Fact]
        public void The_last_act_before_the_microphone_opens_stops_every_playback_saying_nothing()
        {
            // Repairs 1 and 2 (Codex's check of dd7c03b5; Noel 2026-11-19 12:22):
            // the window's last act before capture opens stops everything of ours
            // still sounding, saying nothing, and refuses the take while anything
            // still sounds. The behaviour is AuditionNextTakeDuringPlaybackTests'
            // (JJFlexWpf.Tests, the real player); these pin the window to it.
            string audition = Code(Dialog("CheckYourAudioDialog.Audition"));
            string start = Body(audition, "private async Task StartAuditionAsync()");
            Assert.Contains("beforeOpening: QuietBeforeTake,", start);
            string quiet = Body(audition, "private bool QuietBeforeTake()");
            Assert.Contains("RecordingPlayer.StopForCapture();", quiet);
            Assert.DoesNotContain("RecordingPlayer.Stop();", quiet);
            Assert.DoesNotContain("StopWithoutSpeaking", quiet);
            Assert.DoesNotContain("InAuditionFolder", quiet);   // every playback, not only a render of ours
            Assert.Contains("EarconPlayer.StopTxToneMonitor();", quiet);
            // Repair 4: a radio playback still waiting for its answer keeps it shut too.
            Assert.Contains("if (!CheckPlaybackRunning && !RadioPlayRequested) return true;", quiet);
            Assert.DoesNotContain("await", quiet);   // one synchronous step with the open

            // The player's capture stop says nothing at all, the limiter notice included.
            string player = Code(Source("JJFlexWpf/RecordingPlayer.cs"));
            Assert.Contains("public static void StopForCapture() => End(spokenReason: \"\", sayLimited: false);", player);
            Assert.Contains("if (limited && sayLimited) ScreenReaderOutput.Speak(", player);

            // A radio's playback this window started is stopped after the gate and
            // waited for; one the radio did not stop stays counted, so the take stays shut.
            string gate = Body(audition, "private async Task<bool> CountedThenRadioPlaysStoppedAsync()");
            int counted = gate.IndexOf("await CountdownLetsKeyAsync()", StringComparison.Ordinal);
            int stop = gate.IndexOf("await RecorderControls.ReportStopFailureAsync(rig.StopSlicePlayAsync(target));", StringComparison.Ordinal);
            Assert.True(counted >= 0 && stop > counted, "the radio's playback must be stopped after the count and waited for");
            // Repair 4 (Codex's second re-check of b9afa71f): a playback still
            // waiting for the radio's answer is waited for, void, before that
            // stop. The behaviour is AuditionPendingRadioPlayTests' (JJFlexWpf.Tests,
            // the real request, the real FlexBase recorder and the real take).
            int answered = gate.IndexOf("await RadioPlayRequestsAnsweredAsync();", StringComparison.Ordinal);
            Assert.True(answered > counted && stop > answered,
                "a radio playback still waiting for its answer must be waited for after the count and before the stop");
            string request = Body(Code(Dialog("CheckYourAudioDialog.Check")), "private async Task PlayRadioTakeAsync()");
            int joins = request.IndexOf("RadioPlayRequests.Add(answered.Task);", StringComparison.Ordinal);
            int sent = request.IndexOf("await rig.StartSlicePlayAsync(target);", StringComparison.Ordinal);
            Assert.True(joins >= 0 && sent > joins, "the request must be the window's before it waits for the radio");
            Assert.Contains("if (!rig.IsSlicePlaying(target)) _checkPlayTargets.Remove(target);", gate);
            Assert.DoesNotContain("_checkPlayTargets.Clear()", gate);

            // The tone monitor stays off while a take records.
            string monitor = Body(Code(Dialog("CheckYourAudioDialog.TestTone")), "private void SyncToneMonitor()");
            Assert.Contains("&& !AuditionRecording;", monitor);
        }

        [Fact]
        public void A_render_request_waiting_for_its_click_is_void_once_a_take_start_is_admitted()
        {
            // Repair 3 (Codex's re-check of f9b67186): a render's playback waits
            // 240 ms for its click, and a stop cannot see a request that is not
            // playing yet. The behaviour is AuditionPendingPlaybackTests'
            // (JJFlexWpf.Tests, the real request and the real take); these pin
            // the window to it.
            string audition = Code(Dialog("CheckYourAudioDialog.Audition"));

            // The window's render request asks the window's own predicate,
            // which turns down a press made before a take start.
            string play = Body(audition, "private void PlayAuditionRender(AuditionRender render)");
            Assert.Contains("PlayAfterClickAsync(path, announcement, _rig, AuditionPlaybackStillWanted());", play);
            string wanted = Body(audition, "private Func<FlexBase?, bool> AuditionPlaybackStillWanted()");
            Assert.Contains("asked == Volatile.Read(ref _auditionPlaybackGeneration)", wanted);

            // Voided at the press that admits the start, before the count, so a
            // waiting render does not play over the countdown either...
            string start = Body(audition, "private async Task StartAuditionAsync()");
            int admitted = start.IndexOf("if (start == null) return;", StringComparison.Ordinal);
            int voided = start.IndexOf("VoidAuditionPlaybackRequests();", StringComparison.Ordinal);
            int count = start.IndexOf("BeginStartCountdown(AudioCheckListenMethods.Audition);", StringComparison.Ordinal);
            Assert.True(admitted >= 0 && voided > admitted && count > voided,
                "a waiting render must be void from the press that admits the start, before the countdown");

            // ...and again as the last act before the microphone opens.
            string quiet = Body(audition, "private bool QuietBeforeTake()");
            int fence = quiet.IndexOf("VoidAuditionPlaybackRequests();", StringComparison.Ordinal);
            int stop = quiet.IndexOf("RecordingPlayer.StopForCapture();", StringComparison.Ordinal);
            Assert.True(fence >= 0 && stop > fence, "the waiting renders must be void before the capture stop");
        }

        [Fact]
        public void Any_focus_move_comes_before_the_countdown_and_none_during_the_take()
        {
            // Repair 2: the take's buttons go at the press, before the count, so
            // focus on one moves to Start then; once a take records, nothing moves it.
            string audition = Code(Dialog("CheckYourAudioDialog.Audition"));
            string start = Body(audition, "private async Task StartAuditionAsync()");
            int pending = start.IndexOf("_auditionStartPending = true;", StringComparison.Ordinal);
            int sync = start.IndexOf("SyncAuditionButtons();", StringComparison.Ordinal);
            int count = start.IndexOf("BeginStartCountdown(AudioCheckListenMethods.Audition);", StringComparison.Ordinal);
            int open = start.IndexOf("Audition.StartAfterAsync(", StringComparison.Ordinal);
            Assert.True(pending >= 0 && sync > pending && count > sync && open > count,
                "the buttons must go, and any focus move happen, before the countdown begins");
            // Cleared before the buttons are shown again after the count, and on every exit.
            int cleared = start.IndexOf("_auditionStartPending = false;", open, StringComparison.Ordinal);
            int resync = start.IndexOf("SyncAuditionButtons();", open, StringComparison.Ordinal);
            Assert.True(cleared > open && resync > cleared, "a called-off start must bring the buttons back");
            Assert.Contains("_auditionStartPending = false;", Body(start, "finally"));

            string sync2 = Body(audition, "private void SyncAuditionButtons()");
            Assert.Contains("bool shown = !_auditionStartPending", sync2);
            Assert.Contains("if (focusLost && !AuditionRecording) _startCheckButton?.Focus();", sync2);
        }

        [Fact]
        public void The_take_is_marked_by_the_computer_microphone_checks_own_start_tone_timed_to_its_real_end()
        {
            string audition = Code(Dialog("CheckYourAudioDialog.Audition"));
            // Repair 1 (C1): the same tone, timed: it reports when it is audibly over,
            // and the take's file begins then, not a fixed settle after the open.
            Assert.Contains("startMarker: EarconPlayer.RecorderStartBeepTimed,", audition);
            Assert.DoesNotContain("StartToneSettle", audition);
            string timed = Body(Code(Source("JJFlexWpf/EarconPlayer.ToneEnd.cs")), "public static Task<long?> RecorderStartBeepTimed()");
            Assert.Contains("RecorderStartBeepSamples()", timed);
            Assert.Contains("new ToneEnd.Probe(new RenderedSampleProvider(buffer), end)", timed);
            Assert.Contains("if (!RecorderBeepsEnabled || !MasterAdmits) return Task.FromResult<long?>(null);", timed);
            // The plain beep and the timed one sound the same steps.
            Assert.Contains("PlayVoicedDecaySequence(EarconVoices.Press, RecorderStartSteps, VolumeNormal);",
                Body(Code(Source("JJFlexWpf/EarconPlayer.cs")), "public static void RecorderStartBeep()"));
            // Track Q3: the tone plays only while the recorder beeps are on, and the take knows it.
            Assert.Contains("markerSounds: () => EarconPlayer.RecorderBeepsEnabled", audition);
            // Positive control: those are the computer microphone check's, the pattern being reused.
            string mic = Body(Code(Dialog("CheckYourAudioDialog.ComputerMic")), "private void StartComputerMicProbeNow()");
            Assert.Contains("EarconPlayer.RecorderStartBeep();", mic);
            Assert.Contains("new DispatcherTimer(StartToneSettle,", mic);
        }

        [Fact]
        public void The_narrators_check_form_opens_saying_nothing_and_says_nothing_while_it_records()
        {
            string narrator = Code(Source("JJFlexWpf/RecordingNarrator.cs"));
            string check = Body(narrator, "public static bool StartForCheck(string path, bool holdFileStart, Func<double, string>? stoppedTooSoon = null)");
            Assert.Contains("Open(path, quiet: true, holdFileStart)", check);
            string open = Body(narrator, "private static bool Open(string? path, bool quiet, bool holdFileStart = false)");
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

        // ── Track Q3: "Recording", then the tone, then the file ────────────
        // Noel 2026-11-19 20:26 and 20:29. The behaviour is ChainAuditionStartTests';
        // these pin the window, the narrator and the recorder to it.

        [Fact]
        public void Recording_is_said_in_full_before_the_microphone_opens_on_the_recorders_subject()
        {
            string audition = Code(Dialog("CheckYourAudioDialog.Audition"));
            string start = Body(audition, "private async Task StartAuditionAsync()");
            Assert.Contains("startSaid: () => RecordingSaidAsync(wanted));", start);
            Assert.Contains("stillWanted: wanted,", start);

            string said = Body(audition, "private async Task<bool> RecordingSaidAsync(Func<bool> stillWanted)");
            Assert.Contains("SaidInFull.SayAsync(", said);
            // Repair 1 (C2): heard in full, not the count's own rule.
            Assert.Contains("ScreenReaderOutput.CountdownSpeech.SayInFull(text, onlyWarning,", said);
            Assert.DoesNotContain("CountdownSpeech.Say(", said);
            Assert.Contains("Radios.Speech.SpeechSubject.ComputerRecorder", said);
            Assert.Contains("ScreenReaderOutput.Supersede(Radios.Speech.SpeechSubject.ComputerRecorder, why)", said);
            Assert.Contains("Lexicon.Get(\"audio.audition.recording_now\"", said);
            // The only warning when no tone follows (the estimate path's margin).
            Assert.Contains("bool toneFollows = EarconPlayer.RecorderBeepsEnabled;", said);
            Assert.Contains("onlyWarning: !toneFollows", said);
            Assert.Contains("return SaidInFull.LetsItOpen(outcome);", said);
            // Not heard in full, tone or no tone: the error tone, and a Critical sentence saying so.
            Assert.Contains("if (outcome == SaidInFull.Outcome.NotSaid)", said);
            Assert.Contains("EarconPlayer.LeaderInvalidTone();", said);
            Assert.Contains("Lexicon.Get(\"audio.audition.recording_not_heard\"", said);
            Assert.Contains("VerbosityLevel.Critical, subject: Radios.Speech.SpeechSubject.ComputerRecorder", said);
            Assert.Contains("\"audio.audition.recording_not_heard\": {\"text\": \"WORDING PLACEHOLDER: DRAFT PROSE:", Source("Radios/Lexicon/audio.json"));
            // And only "Said" opens.
            Assert.Contains("public static bool LetsItOpen(Outcome outcome) => outcome == Outcome.Said;", Code(Source("Radios/SaidInFull.cs")));

            // Noel's word, ruled, and said at every verbosity (the countdown's road speaks at Critical).
            // Read from the lexicon file, not the store: the store resolves the settings root.
            Assert.Contains("\"audio.audition.recording_now\": {\"text\": \"Recording\", \"delivery\": null}", Source("Radios/Lexicon/audio.json"));
            string speech = Code(Source("Radios/Speech/CountdownSpeech.cs"));
            string say = Body(speech, "public Radios.ICountdownWords SayInFull(string text, bool onlyWarning, string subject)");
            Assert.Contains("VerbosityLevel.Critical", say);
            Assert.Contains("subject, additive: true", say);
            Assert.Contains("new InFullWords(", say);
            // The count's own Say still speaks on its own subject, unchanged.
            Assert.Contains("SpeechSubject.CheckCountdown, additive: true", Body(speech, "public Radios.ICountdownWords Say(string text, bool onlyWarning)"));
        }

        [Fact]
        public void The_file_begins_where_the_take_says_and_every_block_reaches_it_through_the_cut()
        {
            string narrator = Code(Source("JJFlexWpf/RecordingNarrator.cs"));
            string open = Body(narrator, "private static bool Open(string? path, bool quiet, bool holdFileStart = false)");
            Assert.Contains("recorder.Start(device, _path, holdFileStart, out string failure);", open);
            Assert.Contains("recorder.BeginFileAt(stopwatchTimestamp);", Body(narrator, "public static void BeginCheckFileAt(string path, long? stopwatchTimestamp)"));

            string recorder = Code(Source("JJPortaudio/JJPortaudio/MicRecorder.cs"));
            string frames = Body(recorder, "private void OnFrames(float[] buffer, int count, int frames, CaptureBlockTiming timing)");
            Assert.Contains("fileStart.Block(w, buffer, count, frames, timing)", frames);
            // Repair 2 (C1b): the timed tap. Repair 3 (Codex's re-check of
            // b6543ac1, C1b): the clock is read BEFORE the backlog is asked and
            // again after the answer, before any metering, so the pair can only
            // put the file's start later (CaptureBlockTiming derives why).
            Assert.Contains("probe.TimedFrameSink = OnFrames;", recorder);
            Assert.DoesNotContain("probe.FrameSink = OnFrames;", recorder);
            string probe = Code(Source("JJPortaudio/JJPortaudio/MicProbe.cs"));
            int read = probe.IndexOf("PortAudio.Pa_ReadStream(stream, buffer, chunkFrames);", StringComparison.Ordinal);
            int backlog = probe.IndexOf("backlog = PortAudio.Pa_GetStreamReadAvailable(stream);", StringComparison.Ordinal);
            int arrival = probe.IndexOf("long arrival = Stopwatch.GetTimestamp();", StringComparison.Ordinal);
            int answered = probe.IndexOf("long answered = Stopwatch.GetTimestamp();", StringComparison.Ordinal);
            int meter = probe.IndexOf("Accumulate(buffer, (int)chunkFrames * channels, (int)chunkFrames);", StringComparison.Ordinal);
            Assert.True(read > 0 && read < arrival && arrival < backlog && backlog < answered && answered < meter,
                "read, then the arrival stamp, then the backlog, then the answer's stamp, then metering");
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(probe, "Pa_GetStreamReadAvailable").Count);
            Assert.Contains("backlogAnsweredAt: answeredAt", probe);
            int started = probe.IndexOf("long streamStartedAt = Stopwatch.GetTimestamp();", StringComparison.Ordinal);
            Assert.True(started > 0 && started < probe.IndexOf("perr = PortAudio.Pa_StartStream(stream);", StringComparison.Ordinal),
                "the stream's start is stamped before it is asked to start");
            Assert.DoesNotContain(".Write(", frames);   // no block reaches the file around the file start
            string start = Body(recorder, "public StartOutcome Start(Devices.DeviceInfo device, string path, bool holdFileStart, out string message)");
            Assert.Contains("_fileStart = new CaptureFileStart(rate, holdFileStart, inputLatency, Stopwatch.Frequency);", start);
            Assert.Contains("double inputLatency = opened.InputLatencySeconds;", start);
            // Held frames are written, or not, before the file closes.
            Assert.Contains("fileStart.Finish(writer)", Body(recorder, "private string StopInternal(StopReason reason, string fault)"));

            // Nothing is trimmed after the fact: what is analysed is the file.
            string take = Code(Source("Radios/ChainAuditionTake.cs"));
            Assert.Contains("ChainAudition.Analyse(take.Mono, take.Rate, _processor);", take);
            Assert.Contains("File.Copy(TakePath, destination, overwrite: false);", Body(take, "public bool Keep(string destination)"));
        }

        [Fact]
        public void A_take_stopped_before_its_file_began_says_the_audition_too_short_words_not_check_the_microphone()
        {
            // Repair 1, item 3. The behaviour is StopKind's (JJFlexWpf.Tests,
            // AuditionToneEndTests) and CaptureFileStart's (ChainAuditionStartTests);
            // these pin the narrator's stop and the window to it.
            string narrator = Code(Source("JJFlexWpf/RecordingNarrator.cs"));
            string stop = Body(narrator, "public static void Stop()");
            Assert.Contains("double captured = recorder.CapturedSeconds;", stop);
            Assert.Contains("bool duringLead = recorder.StoppedDuringLead;", stop);
            Assert.Contains("switch (StopKind(reason == MicRecorder.StopReason.Faulted, !string.IsNullOrEmpty(saved), captured, stoppedTooSoon != null, duringLead))", stop);
            Assert.Contains("Speak(stoppedTooSoon!(seconds), interrupt: true);", stop);
            // The microphone sentence is said only for a genuine capture fault;
            // a stop during the held lead is the operator's (repair 2, item 3).
            Assert.Contains("return checkHasWords && (capturedSeconds > 0 || stoppedDuringLead) ? StopSaying.StoppedTooSoon : StopSaying.NothingCaptured;",
                Body(narrator, "internal static StopSaying StopKind(bool faulted, bool fileSaved, double capturedSeconds, bool checkHasWords,"));
            string recorder = Code(Source("JJPortaudio/JJPortaudio/MicRecorder.cs"));
            Assert.Contains("_stoppedDuringLead = _stoppedBeforeFileBegan && fileStart.StoppedDuringLead(stoppedAt);",
                Body(recorder, "private string StopInternal(StopReason reason, string fault)"));
            // The audition's own existing too-short words, no new ones.
            string audition = Code(Dialog("CheckYourAudioDialog.Audition"));
            Assert.Contains("ChainAudition.TooShortSentence)", audition);
            Assert.Contains("Lexicon.Get(\"audio.audition.verdict.too_short\"", Code(Source("Radios/ChainAudition.cs")));
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
