using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
/// <b>Phase A.</b> The offline transmit chain is not built yet, so
/// <see cref="AuditionProcessorSource"/> hands back <see cref="ChainNotConnectedYet"/>:
/// the take is analysed, the raw render is offered, and the verdict says the
/// chain is not connected. Phase B replaces that one wiring with the offline
/// twin of the configured chain, and the other two buttons appear by themselves.
/// </para>
/// </remarks>
public partial class CheckYourAudioDialog
{
    /// <summary>
    /// The offline chain the audition renders through. Phase A's is
    /// <see cref="ChainNotConnectedYet"/>; phase B replaces this one wiring.
    /// It must never return the live chain: the audition changes no transmit
    /// setting (#262, "NEVER AUTO-APPLY").
    /// </summary>
    internal static Func<IChainAuditionProcessor> AuditionProcessorSource { get; set; } = () => ChainNotConnectedYet.Instance;

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

    private ChainAuditionTake Audition => _audition ??= new ChainAuditionTake(ChainAuditionFolder.DefaultPath,
        new NarratorRecorder(), AuditionProcessorSource(), LoadAuditionTake);

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
    private async Task StartAuditionAsync()
    {
        using var start = _checkStarts.TryBegin();
        if (start == null) return;
        SetStartButtonLabel("Stop Audio Check");
        BeginStartCountdown(AudioCheckListenMethods.Audition);
        try
        {
            if (!await CountdownLetsKeyAsync() || !start.IsCurrent || _closing) return;
            // A render still playing is a file in the folder the new take empties.
            StopAuditionPlayback();
            if (Audition.Start(DateTime.Now)) ShowAuditionReading(Lexicon.Get("audio.audition.recording"));
            SyncAuditionButtons();
        }
        finally
        {
            EndStartCountdown();
            if (!AuditionRecording) SetStartButtonLabel("Start Audio Check");
        }
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
        var result = audition.Saved(path, out string trouble);
        if (result == null)
        {
            if (trouble.Length == 0) return;   // somebody else's recording
            string unreadable = Lexicon.Get("audio.audition.unreadable");
            ShowAuditionReading(unreadable);
            ScreenReaderOutput.Speak(unreadable, VerbosityLevel.Critical, interrupt: false);
        }
        else
        {
            string verdict = ChainAudition.Verdict(result);
            ShowAuditionReading(verdict);
            // The speech subject this wants lives in SpeechSubject.cs, which the
            // transmit rulings work is changing; it waits for that merge.
            ScreenReaderOutput.Speak(verdict, VerbosityLevel.Terse, interrupt: false);
        }
        SetStartButtonLabel("Start Audio Check");
        SyncAuditionButtons();
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
        _ = ListenbackTakes.PlayAfterClickAsync(path, announcement, _rig, rig => !_closing && ReferenceEquals(rig, _rig));
    }

    /// <summary>
    /// Copy the raw take into the recordings folder, where it also serves as a
    /// reference take (the Reference Audio section's help calls one "the honest
    /// reference for YOUR station"). The only way a take leaves the working folder.
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

    /// <summary>Stop a render of ours that is playing, before its file goes.</summary>
    private void StopAuditionPlayback()
    {
        if (RecordingPlayer.IsPlaying && InAuditionFolder(RecordingPlayer.CurrentPath)) RecordingPlayer.Stop();
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
        StopAuditionPlayback();
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
        bool shown = SelectedMethod == AudioCheckListenMethods.Audition && audition?.State == AuditionTakeState.Ready;
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
        if (focusLost) _startCheckButton?.Focus();
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

    /// <summary>The take's recorder: <see cref="RecordingNarrator"/>, which announces every start and stop itself.</summary>
    private sealed class NarratorRecorder : IAuditionRecorder
    {
        public bool IsRunning => RecordingNarrator.IsRunning;

        public bool IsRecordingTo(string path) =>
            RecordingNarrator.IsRunning && SamePath(RecordingNarrator.CurrentPath, path);

        public bool Start(string path)
        {
            RecordingNarrator.Start(Lexicon.Get("audio.audition.recording_purpose"), path);
            return IsRecordingTo(path);
        }

        public void Stop() => RecordingNarrator.Stop();
    }

    /// <summary>The take as mono samples at 48 kHz, the transmit stream's rate, through the reference audio's own loader.</summary>
    private static (float[] Mono, int Rate)? LoadAuditionTake(string path) =>
        TxAudioFile.TryLoad(path, 48000, out var loaded, out _) && loaded != null ? (loaded.Mono, loaded.SampleRate) : null;

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
