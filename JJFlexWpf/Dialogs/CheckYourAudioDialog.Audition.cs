using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Check your audio's audition (#262): hear your own voice through the
/// transmit chain. Record this computer's microphone with NOTHING KEYED, then
/// play the one take three ways, each by its own button.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ruled by Noel 2026-11-19 07:03.</b> X7: its own method in Check your
/// audio, recording the computer's microphone with nothing keyed. Playback is
/// by buttons and never automatic: <i>"play the clean audio since it's going to
/// be transmitted. Have buttons to play raw, residual, and clean separately.
/// Automatically playing that stuff could be long."</i> So the buttons are Play
/// cleaned, Play raw and Play what was removed, cleaned first, and nothing
/// plays until one is pressed. X8: the take is thrown away when this window
/// closes unless it is kept.
/// </para>
/// <para>
/// <b>Nothing in this file keys the radio, and nothing in it can.</b> It never
/// asks for the push-to-talk controller, never builds a check session and never
/// touches the radio; <c>ToggleAudioCheck</c> sends the audition here before it
/// reads the controller at all. The microphone is recorded through
/// <see cref="RecordingNarrator"/>, the one place a recording can start or stop
/// and the one voice that says so, into the audition's own working folder
/// (<see cref="ChainAuditionFolder"/>), never the recordings folder.
/// </para>
/// <para>
/// <b>Phase B (Sprint 49 Track Q phase B).</b> <see cref="AuditionProcessorSource"/>
/// is the offline twin of the configured transmit chain
/// (<see cref="ChainAuditionTwin"/>): both noise slots and the gate as
/// configured, in instances of its own. The analysis runs on a worker under the
/// take's generation; the reading box says it is processing, with how far it has
/// got, and the verdict is shown and said only for the newest analysis. A new
/// take, Discard and the window closing cancel it; choosing another method does
/// not, and its result waits for the method to come back.
/// </para>
/// </remarks>
public partial class CheckYourAudioDialog
{
    /// <summary>
    /// The offline chain the audition renders through: phase B's twin of the
    /// configured transmit chain, handed the window's own reading of its radio's
    /// live side (for the verdict only). It must never return the live chain:
    /// the audition changes no transmit setting (#262, "NEVER AUTO-APPLY").
    /// Phase A's placeholder, <see cref="ChainNotConnectedYet"/>, stays for tests.
    /// </summary>
    internal static Func<Func<AuditionLiveState?>, IChainAuditionProcessor> AuditionProcessorSource { get; set; } =
        live => ChainAuditionTwin.ForApp(live);

    /// <summary>
    /// The live transmit side as this window read it from its own radio when the
    /// latest analysis started (items 5 and 6): null with no radio. Read on the
    /// window's thread; the twin only reads this snapshot.
    /// </summary>
    private AuditionLiveState? _auditionLive;

    /// <summary>
    /// Noel's S6 (2026-11-20): while a take processes, the reading box says so
    /// with its percentage, and it is said once it has taken this long.
    /// </summary>
    internal static readonly TimeSpan AuditionProcessingSayAfter = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often the processing line, with its percentage, is said again after
    /// the first time. Zero says it once. How the percentage is spoken is a
    /// follow-up question for Noel (S6), so its cadence is this one constant.
    /// </summary>
    internal static readonly TimeSpan AuditionProcessingSayEvery = TimeSpan.Zero;

    private DispatcherTimer? _auditionProcessingTimer;
    private int _auditionProcessingPercent;   // the window's thread only: the newest ADMITTED figure (repair 1, R4)
    private long _auditionProgressPosted = -1; // the last (generation, percent) posted, so a worker does not flood the window

    /// <summary>
    /// Bumped on the window's thread at every boundary that ends what the
    /// audition said (repair 1, R2): the window closing, a new take, Process
    /// again, another method chosen. Speech admitted under an older value is
    /// superseded at that boundary and fails its validity check thereafter.
    /// </summary>
    private int _auditionSpeechEpoch;

    /// <summary>
    /// Bumped on the window's thread at every boundary that ends the take's
    /// OWNERSHIP of what its analysis publishes (repair 2, R2; Codex's re-check
    /// of 0194e190): the window closing, a new take admitted, Process again.
    /// Not another method: the result of a render still waits for the method to
    /// come back. An analysis publishes only while the owner value it captured
    /// when it STARTED (<see cref="_auditionAnalysisOwner"/>) is still the
    /// current one, so a previous take finishing during the next take's
    /// countdown can neither be shown nor acquire the new take's speech lifetime.
    /// </summary>
    private int _auditionOwnerEpoch;

    /// <summary>The owner value captured when the current analysis started; the window's thread only.</summary>
    private int _auditionAnalysisOwner;

    /// <summary>The generation whose result is in the reading box, so a held result is shown once.</summary>
    private int _auditionShownGeneration = -1;
    private int _auditionProcessingGeneration = -1;
    private Button? _auditionProcessAgainButton;

    /// <summary>The take, made on first use so an uninitialized dialog in a test has none.</summary>
    private ChainAuditionTake? _audition;

    private Button? _auditionCleanedButton;
    private Button? _auditionRawButton;
    private Button? _auditionRemovedButton;
    private Button? _auditionKeepButton;

    /// <summary>The audition's words for the reading box, shown while its method is chosen.</summary>
    private string _auditionReading = "";

    /// <summary>True while the audition is recording the microphone.</summary>
    internal bool AuditionRecording => _audition?.IsRecording == true;

    /// <summary>
    /// The take, marked at its start by the computer microphone check's own
    /// start tone (Track Q2), its file beginning once that tone is audibly over
    /// (Track Q3, Noel 2026-11-19 20:29): the timed tone reports that moment
    /// from the alert output's own position, not a fixed settle (repair 1;
    /// Codex's check of 83932446, C1). The tone plays only while the recorder
    /// beeps are on; with them off there is no tone and the file begins as the
    /// microphone opens.
    /// </summary>
    private ChainAuditionTake Audition => _audition ??= new ChainAuditionTake(ChainAuditionFolder.DefaultPath,
        new NarratorRecorder(), AuditionProcessorSource(() => Volatile.Read(ref _auditionLive)), LoadAuditionTake,
        startMarker: EarconPlayer.RecorderStartBeepTimed,
        markerSounds: () => EarconPlayer.RecorderBeepsEnabled);

    /// <summary>
    /// The audition's buttons, in the Results section, in the ruled order:
    /// Play cleaned first, since it is what goes out, then Play raw beside it,
    /// then Play what was removed, then Keep. Built hidden; they appear only
    /// with a take to play. Also sweeps the working folder, in case the app
    /// ended last time without this window closing.
    /// </summary>
    // WORDING PLACEHOLDERS (#262): every label, name and Ctrl+F1 text here is a lexicon key for Codex.
    private void BuildAuditionResults()
    {
        ChainAuditionFolder.Clear(ChainAuditionFolder.DefaultPath);

        _auditionCleanedButton = MakeAuditionButton("audio.audition.play.cleaned", "audio.audition.play_help.cleaned",
            () => PlayAuditionRender(AuditionRender.Cleaned));
        _auditionRawButton = MakeAuditionButton("audio.audition.play.raw", "audio.audition.play_help.raw",
            () => PlayAuditionRender(AuditionRender.Raw));
        _auditionRemovedButton = MakeAuditionButton("audio.audition.play.removed", "audio.audition.play_help.removed",
            () => PlayAuditionRender(AuditionRender.Removed));
        _auditionKeepButton = MakeAuditionButton("audio.audition.keep", "audio.audition.keep_help", KeepAuditionTake);
        foreach (var button in AuditionButtonsInOrder()) _sections.Add(CheckContent, button!);
        // After Keep (Track Q phase B, item 9; Noel's S5): the same take, processed
        // again with the settings as they are now, no new recording.
        _auditionProcessAgainButton = MakeAuditionButton("audio.audition.process_again", "audio.audition.process_again_help",
            ProcessAuditionAgain);
        _sections.Add(CheckContent, _auditionProcessAgainButton);

        RecordingNarrator.StateChanged += OnAuditionRecorderStateChanged;
        RecordingNarrator.RecordingSaved += OnAuditionRecordingSaved;
        RecordingPlayer.StateChanged += OnAuditionPlayerStateChanged;
    }

    private static Button MakeAuditionButton(string labelKey, string helpKey, Action click)
    {
        string label = Lexicon.Get(labelKey);
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(button, label);
        JJFlexHelp.SetText(button, Lexicon.Get(helpKey));
        button.Click += (s, e) => click();
        return button;
    }

    /// <summary>The audition's controls in tab order, the play buttons in <see cref="ChainAudition.PlayOrder"/>, then Keep.</summary>
    private Button?[] AuditionButtonsInOrder() => new[]
    {
        AuditionPlayButton(ChainAudition.PlayOrder[0]),
        AuditionPlayButton(ChainAudition.PlayOrder[1]),
        AuditionPlayButton(ChainAudition.PlayOrder[2]),
        _auditionKeepButton,
    };

    private Button? AuditionPlayButton(AuditionRender render) => render switch
    {
        AuditionRender.Cleaned => _auditionCleanedButton,
        AuditionRender.Raw => _auditionRawButton,
        _ => _auditionRemovedButton,
    };

    private static string PlayKey(AuditionRender render) => render switch
    {
        AuditionRender.Cleaned => "audio.audition.play.cleaned",
        AuditionRender.Raw => "audio.audition.play.raw",
        _ => "audio.audition.play.removed",
    };

    private static string PlayingKey(AuditionRender render) => render switch
    {
        AuditionRender.Cleaned => "audio.audition.playing.cleaned",
        AuditionRender.Raw => "audio.audition.playing.raw",
        _ => "audio.audition.playing.removed",
    };

    // ── Start and stop ───────────────────────────────────────────────────

    /// <summary>
    /// Start a take, or stop the one recording. Reached from
    /// <c>ToggleAudioCheck</c> before anything there asks for the push-to-talk
    /// controller: nothing is keyed.
    /// </summary>
    private void ToggleAudition()
    {
        if (AuditionRecording) { _audition!.StopRecording(); return; }
        if (RecordingNarrator.IsRunning)
        {
            // Another surface's recording holds the microphone; the narrator
            // would ignore a second start without a word.
            ScreenReaderOutput.Speak(Lexicon.Get("audio.audition.another_recording"), VerbosityLevel.Critical, interrupt: true);
            return;
        }
        _ = StartAuditionAsync();
    }

    /// <summary>
    /// Count down, then record (every method counts down unless the countdown
    /// is off, ruled 2026-10-29 23:59): the count is the operator's moment to
    /// get to the microphone. It takes the same start admission as every
    /// method, so Start, Ctrl+Enter and Escape during the count cancel it.
    /// </summary>
    /// <remarks>
    /// <b>Started the way the computer microphone check starts</b> (Track Q2,
    /// Noel 2026-11-19 12:22): the microphone opens only once the countdown has
    /// been said in full, it opens saying nothing, and the take is marked by
    /// the check's start tone. Nothing is spoken while it records; the reading
    /// box says it is recording, and it is not a live region.
    /// <para>
    /// <b>The order, every take (Track Q3, Noel 2026-11-19 20:26 and 20:29):</b>
    /// the countdown if on, then "Recording" said in full, then the start tone
    /// unless the recorder beeps are off, then the take, whose file begins once
    /// the tone has finished.
    /// </para>
    /// </remarks>
    private async Task StartAuditionAsync()
    {
        using var start = _checkStarts.TryBegin();
        if (start == null) return;
        // A new take: whatever the last one's verdict was still saying is over (repair 1, R2).
        EndAuditionSpeech("a new take began", endsOwnership: true);
        // A render asked for before this press is void (repair 3): its click
        // is still sounding, and it must not start playing over the count.
        VoidAuditionPlaybackRequests();
        SetStartButtonLabel("Stop Audio Check");
        // The play buttons and Keep go NOW, before the count (repair 2): they
        // would go when capture opens, and focus on one of them would then move
        // to Start inside the take, with the screen reader saying so. Moved
        // here, the move and its announcement come before the countdown, whose
        // gate waits for its own words. They come back if the start is called off.
        _auditionStartPending = true;
        SyncAuditionButtons();
        BeginStartCountdown(AudioCheckListenMethods.Audition);
        try
        {
            Func<bool> wanted = () => start.IsCurrent && !_closing;
            bool opened = await Audition.StartAfterAsync(CountedThenRadioPlaysStoppedAsync,
                stillWanted: wanted,
                localNow: () => DateTime.Now,
                // The last act before the microphone opens, in the same step as
                // the open: everything of ours still sounding stops, saying nothing.
                beforeOpening: QuietBeforeTake,
                // After the count, "Recording", said in full before anything opens (Track Q3).
                startSaid: () => RecordingSaidAsync(wanted));
            _auditionStartPending = false;
            if (_closing) return;
            if (opened) ShowAuditionReading(Lexicon.Get("audio.audition.recording"));
            SyncAuditionButtons();
        }
        finally
        {
            _auditionStartPending = false;
            EndStartCountdown();
            if (!AuditionRecording) SetStartButtonLabel("Start Audio Check");
            // Called off before the microphone opened: the take that was there owns its result again.
            if (!AuditionRecording && !_closing) ReclaimAuditionAfterCalledOffStart();
        }
    }

    /// <summary>True from the press that admits an audition start until its count ends: the take's buttons are hidden for it.</summary>
    private bool _auditionStartPending;

    /// <summary>
    /// Say "Recording" and wait until it has been said in full, so it is never
    /// in the take (Track Q3, Noel 2026-11-19 20:26: <i>"say 'Recording' before
    /// the microphone opens, every time, like the check's 'Transmitting'"</i>).
    /// Critical, so it is said at every verbosity; on the computer recorder's
    /// subject; followed to delivery the way the countdown's numbers are. True
    /// to open the microphone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Heard in full, every time (repair 1; Codex's check of 83932446,
    /// C2).</b> The words go through <c>CountdownSpeech.SayInFull</c>, whose
    /// handle counts them said only when the reader reported them completed
    /// and never cut; Track Q3 used the count's own handle, which counts a
    /// word cut part-way as heard, so a "Recording" cut at its first word
    /// opened the take. Cut, taken back, never reaching the reader or not said
    /// in time, with the tone on or off: nothing opens, with the error tone
    /// and a sentence saying so.
    /// </para>
    /// <para>
    /// With the beeps off, "Recording" is the only warning, so on a reader
    /// that does not report completion it is held for the count's margin past
    /// its estimate, as before.
    /// </para>
    /// </remarks>
    private async Task<bool> RecordingSaidAsync(Func<bool> stillWanted)
    {
        bool toneFollows = EarconPlayer.RecorderBeepsEnabled;
        var outcome = await SaidInFull.SayAsync(
            (text, onlyWarning) => ScreenReaderOutput.CountdownSpeech.SayInFull(text, onlyWarning,
                Radios.Speech.SpeechSubject.ComputerRecorder),
            why => ScreenReaderOutput.Supersede(Radios.Speech.SpeechSubject.ComputerRecorder, why),
            Lexicon.Get("audio.audition.recording_now"), onlyWarning: !toneFollows, stillWanted);
        if (outcome == SaidInFull.Outcome.NotSaid)
        {
            JJTrace.Tracing.TraceLine("Check your audio audition: \"Recording\" was not heard in full, so nothing opened",
                System.Diagnostics.TraceLevel.Warning);
            EarconPlayer.LeaderInvalidTone();
            // DRAFT PROSE (Track Q3 repair 1): a placeholder for Codex's words.
            // Nothing is recording, so this sentence cannot be in a take.
            ScreenReaderOutput.Speak(Lexicon.Get("audio.audition.recording_not_heard"), Radios.Speech.SpeechIntent.Interrupt,
                VerbosityLevel.Critical, subject: Radios.Speech.SpeechSubject.ComputerRecorder);
        }
        return SaidInFull.LetsItOpen(outcome);
    }

    /// <summary>
    /// The countdown's gate, then the radio's playbacks this window started
    /// (a Record yourself take playing on a slice) asked to stop, and waited
    /// for, so the radio has stopped before the microphone opens. A stop the
    /// radio refuses is said by the recorder controls, before anything opens.
    /// </summary>
    private async Task<bool> CountedThenRadioPlaysStoppedAsync()
    {
        if (!await CountdownLetsKeyAsync()) return false;
        // A playback still waiting for the radio's answer is not on the list
        // below yet (repair 4): wait for it, void, first.
        await RadioPlayRequestsAnsweredAsync();
        if (_rig is { } rig && _checkPlayTargets is { Count: > 0 })
        {
            foreach (var target in new List<SliceRecorderTarget>(_checkPlayTargets))
            {
                if (rig.IsSlicePlaying(target))
                    await RecorderControls.ReportStopFailureAsync(rig.StopSlicePlayAsync(target));
                // Only a playback the radio has stopped leaves the list; one
                // still playing keeps the take from opening.
                if (!rig.IsSlicePlaying(target)) _checkPlayTargets.Remove(target);
            }
        }
        return true;
    }

    /// <summary>
    /// The audition opens capture only when nothing of ours is sounding (Noel
    /// 2026-11-19 12:22; repair 2). Run in the same synchronous step as the
    /// open, so nothing can start between the two: every playback on this
    /// computer stops (a render of the last take, an Off the air take, any
    /// other), and the transmit tone's monitor, all saying nothing. A radio
    /// playback that would not stop keeps the take shut, and says so: nothing
    /// is recording yet, so that sentence cannot be in a take. So does a radio
    /// playback request still waiting for the radio's answer (repair 4).
    /// </summary>
    private bool QuietBeforeTake()
    {
        // Every render request still waiting for its click is void (repair 3),
        // so none starts, or refuses aloud, once the microphone is open.
        VoidAuditionPlaybackRequests();
        RecordingPlayer.StopForCapture();
        if (_toneMonitorSounding)
        {
            // It stays off while the take records: SyncToneMonitor asks.
            EarconPlayer.StopTxToneMonitor();
            _toneMonitorSounding = false;
            _toneMonitorProvider = null;
        }
        // A radio playback asked for after the gate waited, and still waiting
        // for its answer, keeps the take shut as one playing does (repair 4).
        if (!CheckPlaybackRunning && !RadioPlayRequested) return true;
        ScreenReaderOutput.Speak(Lexicon.Get("audio.audition.not_quiet"), Radios.Speech.SpeechIntent.Queue,
            VerbosityLevel.Critical, subject: Radios.Speech.SpeechSubject.ComputerRecorder);
        return false;
    }

    /// <summary>
    /// The recorder finished a file. If it is the audition's, analyse it and
    /// say the verdict, queued behind the recorder's own "stopped" sentence.
    /// Nothing plays: the three buttons appear, and the operator chooses.
    /// </summary>
    private void OnAuditionRecordingSaved(string path)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnAuditionRecordingSaved(path)));
            return;
        }
        var audition = _audition;
        if (audition == null || _closing) return;
        // The live side, read here from this window's own radio (items 5 and 6).
        Volatile.Write(ref _auditionLive, ChainAuditionTwin.ReadLive(_rig));
        // On a worker (Track Q phase B, item 4): a real render takes time.
        var run = audition.AnalyseInBackground(path, OnAuditionProgress);
        if (run == null) return;   // somebody else's recording
        SetStartButtonLabel("Start Audio Check");
        BeginAuditionProcessing(audition, run);
    }

    /// <summary>
    /// An analysis has started: the reading box says it is processing, at once
    /// and silently (it is not a live region), the play buttons and Keep are
    /// hidden, the spoken line waits for <see cref="AuditionProcessingSayAfter"/>,
    /// and the outcome is posted back here when the worker is done.
    /// </summary>
    private void BeginAuditionProcessing(ChainAuditionTake audition, Task<AuditionAnalysisOutcome> run)
    {
        _auditionProcessingGeneration = audition.Generation;
        // Ownership is captured here, as the analysis starts, never at queue time (repair 2, R2).
        _auditionAnalysisOwner = Volatile.Read(ref _auditionOwnerEpoch);
        _auditionProcessingPercent = 0;
        ShowAuditionReading(AuditionProcessingLine(0));
        SyncAuditionButtons();
        StartAuditionProcessingSpeech();
        run.ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => OnAuditionAnalysed(t.Result))),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // WORDING PLACEHOLDER (Track Q phase B, DRAFT PROSE): the processing line, with its percentage.
    private static string AuditionProcessingLine(int percent) => Lexicon.Get("audio.audition.processing", ("percent", percent));

    /// <summary>
    /// On the worker: how far the render has got. Nothing shared is written here
    /// (repair 1, R4; Codex's check of 3c61da16): the figure travels with its
    /// generation, and only the window's thread, once it has admitted that
    /// generation as current, stores and shows it. A stale callback changes
    /// nothing. Posted only when the generation's whole percentage moves.
    /// </summary>
    private void OnAuditionProgress(int generation, double fraction)
    {
        int percent = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
        long key = ((long)generation << 8) | (uint)percent;
        if (Interlocked.Exchange(ref _auditionProgressPosted, key) == key) return;
        Dispatcher.BeginInvoke(new Action(() => AdmitAuditionProgress(generation, percent)));
    }

    /// <summary>On the window's thread: show a progress figure only if its generation is the take's current analysis, and show that callback's own figure.</summary>
    private void AdmitAuditionProgress(int generation, int percent)
    {
        var audition = _audition;
        if (_closing || audition == null || !audition.IsAnalysing || generation != audition.Generation) return;
        _auditionProcessingPercent = percent;
        ShowAuditionReading(AuditionProcessingLine(percent));
    }

    private void StartAuditionProcessingSpeech()
    {
        StopAuditionProcessingSpeech();
        _auditionProcessingTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = AuditionProcessingSayAfter };
        _auditionProcessingTimer.Tick += OnAuditionProcessingTick;
        _auditionProcessingTimer.Start();
    }

    private void StopAuditionProcessingSpeech()
    {
        var timer = _auditionProcessingTimer;
        _auditionProcessingTimer = null;
        if (timer == null) return;
        timer.Stop();
        timer.Tick -= OnAuditionProcessingTick;
    }

    /// <summary>Said once it has taken a while (Noel's S6), only while the audition is the method shown and its analysis still current.</summary>
    private void OnAuditionProcessingTick(object? sender, EventArgs e)
    {
        var audition = _audition;
        if (_closing || audition == null || !audition.IsAnalysing || audition.Generation != _auditionProcessingGeneration)
        {
            StopAuditionProcessingSpeech();
            return;
        }
        if (SelectedMethod == AudioCheckListenMethods.Audition)
            ScreenReaderOutput.Speak(AuditionProcessingLine(_auditionProcessingPercent), Radios.Speech.SpeechIntent.Queue,
                VerbosityLevel.Terse, subject: Radios.Speech.SpeechSubject.AuditionVerdict,
                stillValid: AuditionSpeechStillValid(audition, audition.Generation));
        if (AuditionProcessingSayEvery > TimeSpan.Zero && _auditionProcessingTimer != null) _auditionProcessingTimer.Interval = AuditionProcessingSayEvery;
        else StopAuditionProcessingSpeech();
    }

    /// <summary>
    /// The worker is done. Shown and said only when it is the take's newest,
    /// uncancelled analysis and the window is open: an old generation's verdict
    /// is never shown or said. The verdict is said only while the audition is the
    /// method shown; otherwise it waits in the reading box for the method to
    /// come back. Nothing plays: the buttons appear, and the operator chooses.
    /// </summary>
    private void OnAuditionAnalysed(AuditionAnalysisOutcome outcome)
    {
        var audition = _audition;
        if (_closing || audition == null || !audition.IsCurrent(outcome)) return;
        // Repair 2, R2: and only while this window still owns that analysis's
        // publication. A new take admitted during the previous take's render ends
        // it at the admission, though the take's generation changes only when the
        // microphone opens, after the countdown. A called-off start gives it back
        // (ReclaimAuditionAfterCalledOffStart).
        if (outcome.Generation != _auditionProcessingGeneration
            || _auditionAnalysisOwner != Volatile.Read(ref _auditionOwnerEpoch)) return;
        StopAuditionProcessingSpeech();
        _auditionShownGeneration = outcome.Generation;
        string words;
        VerbosityLevel level;
        if (outcome.Result is { } result)
        {
            words = ChainAudition.Verdict(result);
            level = VerbosityLevel.Terse;
        }
        else
        {
            words = outcome.Trouble == ChainAuditionTake.TroubleUnreadable
                ? Lexicon.Get("audio.audition.unreadable") : Lexicon.Get("audio.audition.analysis_failed");
            level = VerbosityLevel.Critical;
        }
        ShowAuditionReading(words);
        if (SelectedMethod == AudioCheckListenMethods.Audition)
            ScreenReaderOutput.Speak(words, Radios.Speech.SpeechIntent.Queue, level, subject: Radios.Speech.SpeechSubject.AuditionVerdict,
                stillValid: AuditionSpeechStillValid(audition, outcome.Generation));
        SyncAuditionButtons();
    }

    /// <summary>
    /// The validity a queued audition sentence carries into the speech arbiter
    /// (repair 1, R2), asked before any delayed delivery or replay, from any
    /// thread: still the same take, the same analysis, and no boundary since.
    /// The boundaries supersede the subject as well (<see cref="EndAuditionSpeech"/>).
    /// </summary>
    /// <remarks>
    /// Repair 2, R2: the take's lifetime is the owner value its analysis captured
    /// when it STARTED, never one read now, so it cannot be inherited from a newer
    /// take. The view value read now (bumped when another method is chosen) only
    /// narrows it: it ends a sentence sooner, and can never lengthen one, because
    /// nothing is published without the start-captured ownership.
    /// </remarks>
    private Func<bool> AuditionSpeechStillValid(ChainAuditionTake audition, int generation)
    {
        int owner = _auditionAnalysisOwner;
        int view = Volatile.Read(ref _auditionSpeechEpoch);
        return () => Volatile.Read(ref _auditionOwnerEpoch) == owner && Volatile.Read(ref _auditionSpeechEpoch) == view
            && ReferenceEquals(_audition, audition) && audition.Generation == generation;
    }

    /// <summary>
    /// A new take's start was called off during its countdown (Escape, Start
    /// again, the window's own checks): the take that was there is still there,
    /// so it owns its analysis again (repair 2, R2). A result that finished during
    /// the countdown, and so was held back, is put in the reading box now,
    /// silently, as the buttons come back; one still running publishes as usual.
    /// </summary>
    private void ReclaimAuditionAfterCalledOffStart()
    {
        var audition = _audition;
        if (_closing || audition == null || audition.Generation != _auditionProcessingGeneration) return;
        _auditionAnalysisOwner = Volatile.Read(ref _auditionOwnerEpoch);
        if (audition.State == AuditionTakeState.Ready && audition.Result is { } result && _auditionShownGeneration != audition.Generation)
        {
            _auditionShownGeneration = audition.Generation;
            ShowAuditionReading(ChainAudition.Verdict(result));
        }
    }

    /// <summary>
    /// A boundary that ends what the audition said (repair 1, R2; Codex's check
    /// of 3c61da16): the window closing, a new take, Process again, another
    /// method chosen. Every sentence of the audition's subject still owed, in
    /// the pump, the ledger or held for salvage, is superseded, and nothing
    /// admitted before this passes its validity check again. The audition only
    /// ever speaks this subject queued, so there is no pending Latest to retire.
    /// </summary>
    private void EndAuditionSpeech(string why, bool endsOwnership)
    {
        if (endsOwnership) Interlocked.Increment(ref _auditionOwnerEpoch);
        Interlocked.Increment(ref _auditionSpeechEpoch);
        StopAuditionProcessingSpeech();
        ScreenReaderOutput.Supersede(Radios.Speech.SpeechSubject.AuditionVerdict, why);
    }

    /// <summary>
    /// Process the same take again with the settings as they are now (Track Q
    /// phase B, item 9; Noel's S5): no new recording. Our own renders stop
    /// first, saying nothing, because their files are about to be replaced, and
    /// any render still waiting for its click is void.
    /// </summary>
    private void ProcessAuditionAgain()
    {
        var audition = _audition;
        if (audition == null || _closing || audition.State != AuditionTakeState.Ready) return;
        EndAuditionSpeech("the take is being processed again", endsOwnership: true);
        VoidAuditionPlaybackRequests();
        StopAuditionPlayback();
        Volatile.Write(ref _auditionLive, ChainAuditionTwin.ReadLive(_rig));
        var run = audition.ProcessAgain(OnAuditionProgress);
        if (run == null) return;
        BeginAuditionProcessing(audition, run);
    }

    /// <summary>
    /// The recorder started or stopped. A stop that left no file (nothing
    /// captured, a fault) puts the audition back to idle; the recorder has
    /// already said why. Posted, so any file the same stop reported has been
    /// handled first.
    /// </summary>
    private void OnAuditionRecorderStateChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var audition = _audition;
            if (audition == null || _closing) return;
            bool was = audition.IsRecording;
            audition.RecorderStopped();
            if (was && !audition.IsRecording)
            {
                ShowAuditionReading("");
                SetStartButtonLabel("Start Audio Check");
            }
            SyncAuditionButtons();
        }));
    }

    /// <summary>Choosing another method stops a take that is recording; its result waits for the method to come back.</summary>
    private void ApplyAuditionLayout(AudioCheckListenMethods method)
    {
        // Another method chosen: what the audition was saying is no longer about
        // what is shown (repair 1, R2). The render goes on; its verdict waits.
        if (method != AudioCheckListenMethods.Audition && _audition != null) EndAuditionSpeech("another method was chosen", endsOwnership: false);
        if (method != AudioCheckListenMethods.Audition && AuditionRecording) _audition!.StopRecording();
        SyncAuditionButtons();
    }

    // ── Playing, keeping, discarding ─────────────────────────────────────

    /// <summary>
    /// Play one render, or stop it if it is the one playing. The ONLY way an
    /// audition render is played, and it is reached only from its button.
    /// Live receive is muted for it, the way every take here is played.
    /// </summary>
    private void PlayAuditionRender(AuditionRender render)
    {
        var audition = _audition;
        string? path = audition?.RenderPath(render);
        if (path == null) return;
        if (RecordingPlayer.IsPlaying && SamePath(RecordingPlayer.CurrentPath, path))
        {
            RecordingPlayer.Stop();
            return;
        }
        double seconds = (audition!.Result?.Render(render)?.Length ?? 0) / (double)Math.Max(1, audition.Result?.SampleRate ?? 1);
        string announcement = Lexicon.Get(PlayingKey(render), ("length", RecordingStore.DescribeLength(seconds)));
        _ = ListenbackTakes.PlayAfterClickAsync(path, announcement, _rig, AuditionPlaybackStillWanted());
    }

    /// <summary>
    /// Counts the moments a render's playback request stops being wanted: a
    /// take start admitted, and the microphone about to open (repair 3,
    /// Codex's re-check of <c>f9b67186</c>). A request waits 240 ms for its
    /// confirmation click before it plays, and stopping what is playing cannot
    /// see one still waiting: Play then Start inside the click let it start,
    /// or refuse with a sound and a sentence, after capture had opened. A radio
    /// playback request (<see cref="PlayRadioTakeAsync"/>) reads the same count
    /// from repair 4, and the gate also advances it while it waits for one.
    /// </summary>
    private int _auditionPlaybackGeneration;

    /// <summary>Make every render request, and every radio playback request, made before now void.</summary>
    private void VoidAuditionPlaybackRequests() => Interlocked.Increment(ref _auditionPlaybackGeneration);

    /// <summary>
    /// The question a render's playback asks after its click: still wanted
    /// only if the window is open, the radio is the same, and no take start
    /// has been admitted since the press.
    /// </summary>
    private Func<FlexBase?, bool> AuditionPlaybackStillWanted()
    {
        int asked = Volatile.Read(ref _auditionPlaybackGeneration);
        return rig => !_closing && ReferenceEquals(rig, _rig)
            && asked == Volatile.Read(ref _auditionPlaybackGeneration);
    }

    /// <summary>
    /// Copy the take into the recordings folder, where it also serves as a
    /// reference take (the Reference Audio section's help calls one "the honest
    /// reference for YOUR station"). The only way a take leaves the working
    /// folder. The file began after the start tone, so what is kept is exactly
    /// what was measured and played (Track Q3).
    /// </summary>
    private void KeepAuditionTake()
    {
        var audition = _audition;
        if (audition == null || audition.State != AuditionTakeState.Ready) return;
        string destination = RecordingStore.PathForNewTake(DateTime.Now);
        if (audition.Keep(destination))
        {
            // Keep goes once the take is kept: focus moves to the first play
            // button BEFORE it goes, so it never lands on nothing.
            if (_auditionKeepButton?.IsKeyboardFocusWithin == true) FirstVisibleAuditionButton()?.Focus();
            RefreshReferenceList(selectPath: destination);
            ScreenReaderOutput.Speak(Lexicon.Get("audio.audition.kept",
                ("file", Path.GetFileNameWithoutExtension(destination))), VerbosityLevel.Terse, interrupt: true);
        }
        else
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.audition.keep_failed"), VerbosityLevel.Critical, interrupt: true);
        }
        SyncAuditionButtons();
    }

    /// <summary>
    /// Stop a render of ours that is playing, before its file goes, saying
    /// nothing. Used as the window closes, after its own playback has stopped.
    /// A new take does not use this: <see cref="QuietBeforeTake"/> stops every
    /// playback, not only a render of ours (repair 2).
    /// </summary>
    private void StopAuditionPlayback()
    {
        if (RecordingPlayer.IsPlaying && InAuditionFolder(RecordingPlayer.CurrentPath)) RecordingPlayer.StopWithoutSpeaking();
    }

    /// <summary>
    /// The window is closing: stop following the recorder and the player, stop
    /// a take still recording, and empty the working folder. A kept copy is not
    /// in it and stays. Called after the window's own playback has stopped.
    /// </summary>
    private void EndAudition()
    {
        RecordingNarrator.StateChanged -= OnAuditionRecorderStateChanged;
        RecordingNarrator.RecordingSaved -= OnAuditionRecordingSaved;
        RecordingPlayer.StateChanged -= OnAuditionPlayerStateChanged;
        EndAuditionSpeech("the window closed", endsOwnership: true);
        StopAuditionPlayback();
        // Cancels an analysis still running: nothing it would write can appear after the clear.
        if (_audition != null) _audition.Discard();
        else ChainAuditionFolder.Clear(ChainAuditionFolder.DefaultPath);
    }

    // ── What is shown ────────────────────────────────────────────────────

    /// <summary>
    /// Show the buttons the take has renders for, and Keep until it is kept,
    /// only while the audition is the method shown. A button that would do
    /// nothing is out of the tab order. Each play button says "Stop playing"
    /// while its own render plays.
    /// </summary>
    private void SyncAuditionButtons()
    {
        if (_auditionCleanedButton == null) return;
        var audition = _audition;
        // Hidden from the press that starts a take (repair 2), so no focus
        // has to move once capture opens.
        bool shown = !_auditionStartPending
            && SelectedMethod == AudioCheckListenMethods.Audition && audition?.State == AuditionTakeState.Ready;
        bool focusLost = false;
        foreach (var render in ChainAudition.PlayOrder)
        {
            var button = AuditionPlayButton(render)!;
            string? path = shown ? audition!.RenderPath(render) : null;
            bool visible = path != null;
            if (!visible && button.IsKeyboardFocusWithin) focusLost = true;
            button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) continue;
            bool playing = RecordingPlayer.IsPlaying && SamePath(RecordingPlayer.CurrentPath, path!);
            string label = Lexicon.Get(playing ? "audio.audition.stop_playing" : PlayKey(render));
            if ((button.Content as string) != label) button.Content = label;
            if (AutomationProperties.GetName(button) != label) AutomationProperties.SetName(button, label);
        }
        bool keep = shown && !audition!.Kept;
        if (!keep && _auditionKeepButton!.IsKeyboardFocusWithin) focusLost = true;
        _auditionKeepButton!.Visibility = keep ? Visibility.Visible : Visibility.Collapsed;
        // Process again (item 9): while the take is ready, and also while it is
        // processing, so the button just pressed keeps the focus rather than the
        // screen reader announcing a move to Start; pressed then, it does nothing.
        if (_auditionProcessAgainButton != null)
        {
            bool again = !_auditionStartPending && SelectedMethod == AudioCheckListenMethods.Audition
                && audition?.State is AuditionTakeState.Ready or AuditionTakeState.Analysing;
            if (!again && _auditionProcessAgainButton.IsKeyboardFocusWithin) focusLost = true;
            _auditionProcessAgainButton.Visibility = again ? Visibility.Visible : Visibility.Collapsed;
        }
        // Never during a take: the screen reader would announce the move into it.
        if (focusLost && !AuditionRecording) _startCheckButton?.Focus();
        SyncResultsSection();
    }

    private Button? FirstVisibleAuditionButton()
    {
        foreach (var button in AuditionButtonsInOrder())
            if (button != null && button != _auditionKeepButton && button.Visibility == Visibility.Visible) return button;
        return null;
    }

    private void OnAuditionPlayerStateChanged() => Dispatcher.BeginInvoke(new Action(SyncAuditionButtons));

    /// <summary>Put the audition's words in the reading box, where every method's verdict appears. Empty means its idle words.</summary>
    private void ShowAuditionReading(string text)
    {
        _auditionReading = text;
        ShowingAuditionReading();
    }

    /// <summary>The reading box while the audition is chosen: its own words, never the radio's mic meter.</summary>
    private bool ShowingAuditionReading()
    {
        if (SelectedMethod != AudioCheckListenMethods.Audition || _micReadingBox == null) return false;
        string text = _auditionReading.Length > 0 ? _auditionReading : Lexicon.Get("audio.audition.idle");
        if (_micReadingBox.Text != text) _micReadingBox.Text = text;
        return true;
    }

    // ── The recorder and the loader the take is handed ───────────────────

    /// <summary>
    /// The take's recorder: <see cref="RecordingNarrator"/>, still the one place
    /// a recording starts or stops, in its check form: it opens saying nothing
    /// ("Recording", said in full before it, and the start tone after it are
    /// the announcement), begins its file once the tone has finished, refuses
    /// aloud as ever, and says the stop as ever.
    /// </summary>
    private sealed class NarratorRecorder : IAuditionRecorder
    {
        public bool IsRunning => RecordingNarrator.IsRunning;

        public bool IsRecordingTo(string path) =>
            RecordingNarrator.IsRunning && SamePath(RecordingNarrator.CurrentPath, path);

        public bool Start(string path, bool holdFileStart) =>
            RecordingNarrator.StartForCheck(path, holdFileStart, ChainAudition.TooShortSentence) && IsRecordingTo(path);

        public void BeginFileAt(string path, long? stopwatchTimestamp) =>
            RecordingNarrator.BeginCheckFileAt(path, stopwatchTimestamp);

        public void Stop() => RecordingNarrator.Stop();
    }

    /// <summary>
    /// The take as mono samples at 48 kHz, the transmit stream's rate, read so
    /// that a cancelled worker lets go of the file promptly (repair 2, R3;
    /// Codex's re-check of 0194e190): the file is read into memory a piece at a
    /// time, the cancel checked between pieces, and the file is closed before it
    /// is decoded. Decoding is the reference audio's own recipe
    /// (<c>TxAudioFile</c>: mixed to mono first, then <c>WdlResamplingSampleProvider</c>
    /// to the target rate), run on the bytes in memory, because that loader
    /// takes only a path and holds the file while it decodes. Only the audition's
    /// own takes come through here, WAV files our recorder wrote.
    /// </summary>
    internal static (float[] Mono, int Rate)? LoadAuditionTake(string path, CancellationToken cancel)
    {
        byte[] bytes;
        try { bytes = ReadTakeCancellably(path, cancel); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("Check your audio audition: the take could not be read: " + ex.GetType().Name,
                System.Diagnostics.TraceLevel.Warning);
            return null;
        }
        try
        {
            using var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(bytes, writable: false));
            NAudio.Wave.ISampleProvider source = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader);
            if (source.WaveFormat.Channels > 1) source = NAudio.Wave.WaveExtensionMethods.ToMono(source);
            if (source.WaveFormat.SampleRate != TakeRate) source = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(source, TakeRate);
            var samples = new List<float>((int)Math.Min(int.MaxValue / 2, reader.SampleCount + TakeRate));
            var block = new float[TakeRate];
            int read;
            while ((read = source.Read(block)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                for (int i = 0; i < read; i++) samples.Add(block[i]);
            }
            return samples.Count == 0 ? null : (samples.ToArray(), TakeRate);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("Check your audio audition: the take could not be decoded: " + ex.GetType().Name,
                System.Diagnostics.TraceLevel.Warning);
            return null;
        }
    }

    private const int TakeRate = 48000;

    /// <summary>The piece the take is read in, between checks of the cancel: 256 KB, under 3 seconds of a 48 kHz take.</summary>
    internal const int TakeReadPieceBytes = 256 * 1024;

    /// <summary>Tests only: run after each piece of the take is read, with the file still open.</summary>
    internal static Action? AfterTakePieceReadForTest { get; set; }

    /// <summary>The whole file into memory, a piece at a time, giving up at once when cancelled. The file is open only inside this call.</summary>
    internal static byte[] ReadTakeCancellably(string path, CancellationToken cancel)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        var bytes = new byte[file.Length];
        int at = 0;
        while (at < bytes.Length)
        {
            cancel.ThrowIfCancellationRequested();
            int got = file.Read(bytes, at, Math.Min(TakeReadPieceBytes, bytes.Length - at));
            if (got <= 0) break;
            at += got;
            AfterTakePieceReadForTest?.Invoke();
        }
        cancel.ThrowIfCancellationRequested();
        if (at < bytes.Length) Array.Resize(ref bytes, at);
        return bytes;
    }

    /// <summary>True for a file in the audition's working folder: an audition take or render, never a reference take until it is kept.</summary>
    internal static bool InAuditionFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            return dir != null && string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(ChainAuditionFolder.DefaultPath).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }
}
