using System;
using System.Threading.Tasks;
using Radios;
using Radios.Listenback;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Check your audio's playback takes (Unified takes, #864, round two): which
/// methods save their playback, and "start a new test or add to this one".
/// Kept in its own file so the check's own flow changes only where it must.
/// </summary>
public partial class CheckYourAudioDialog
{
    /// <summary>
    /// "Save each playback as a take", as this radio's setting holds it. Off
    /// by default, and until the setting is loaded nothing here is reachable.
    /// </summary>
    private bool _savePlaybackTakes;

    /// <summary>
    /// Whether a check of <paramref name="method"/> saves its playback as a
    /// take: Radio recording and Looped back, with the box ticked. Looped back
    /// stays hidden over SmartLink (D1); <see cref="AudioCheckMethods.Offered"/>
    /// already keeps it out of the cycle there.
    /// </summary>
    internal bool SavesPlaybackTake(AudioCheckListenMethods method) =>
        _savePlaybackTakes && method is AudioCheckListenMethods.RecordPlayback or AudioCheckListenMethods.Loopback;

    /// <summary>A radio connected: the same radio keeps the current test, another starts afresh (D3).</summary>
    private static void NoteTakeSeriesConnection(FlexBase? rig)
    {
        string serial = rig?.TakeAdmissionFactsNow().Serial ?? "";
        if (serial.Length > 0) TakeSession.Series.Connected(serial);
    }

    /// <summary>
    /// Ask "start a new test or add to this one" when the test's last take was
    /// made with another mic input, computer microphone, mic profile or radio
    /// (Q7, D5), and the operator has not already said "add to this one" for
    /// this test. Called BEFORE the countdown, by Start, never while keyed;
    /// asking changes nothing on the radio. False when the operator backed out
    /// with Escape, or something keyed while the question was open: then
    /// nothing starts.
    /// </summary>
    private bool AdmitTakeBeforeCountdown(AudioCheckListenMethods method, PttSafetyController ptt)
    {
        if (!SavesTakeOf(method) || _rig == null) return true;
        var rig = _rig;
        string root = ListenbackTakes.Root;
        TakeAdmission admission;
        try
        {
            NoteTakeSeriesConnection(rig);
            admission = TakeSession.Series.Admit(root, rig.TakeAdmissionFactsNow());
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException
                                      or UnauthorizedAccessException)
        {
            // The test registry could not be read: the take still saves, in
            // a new test (TakeSession.Series.Reserve makes one), so nothing is
            // asked and nothing is lost.
            JJTrace.Tracing.TraceLine("Check your audio take admission: " + ex.Message, System.Diagnostics.TraceLevel.Warning);
            return true;
        }
        if (!admission.Ask) return true;
        JJTrace.Tracing.TraceLine("Check your audio take admission: asking, changed=" + string.Join(",", admission.Changed),
            System.Diagnostics.TraceLevel.Info);
        // DRAFT PROSE: awaiting Noel. "Start a new test or add to this one".
        var question = new ConfirmActionDialog(TakeAdmissionText.Title,
            TakeAdmissionText.Message(admission.Test, admission.Changed),
            question: TakeAdmissionText.Question(admission.Test),
            yesLabel: TakeAdmissionText.NewTestLabel, noLabel: TakeAdmissionText.AddLabel) { Owner = this };
        bool? answer = question.ShowModalDialog();
        if (!question.Answered)
        {
            ScreenReaderOutput.Speak(TakeAdmissionText.BackedOut, Radios.Speech.SpeechIntent.Interrupt, VerbosityLevel.Terse,
                subject: Radios.Speech.SpeechSubject.PlaybackTake);
            return false;
        }
        // The question was modal: something may have keyed, or the radio gone, meanwhile.
        if (!ReferenceEquals(rig, _rig) || ptt.IsTransmitting || rig.Transmit)
        {
            ScreenReaderOutput.Speak(Lexicon.Get("audio.already_transmitting"), VerbosityLevel.Critical, interrupt: true);
            return false;
        }
        try { TakeSession.Series.Answer(root, startNewTest: answer == true); }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or InvalidOperationException
                                      or UnauthorizedAccessException)
        {
            JJTrace.Tracing.TraceLine("Check your audio take admission answer: " + ex.Message, System.Diagnostics.TraceLevel.Warning);
        }
        return true;
    }

    /// <summary>Whether a check of <paramref name="method"/> makes a take that joins a test.</summary>
    private bool SavesTakeOf(AudioCheckListenMethods method) => SavesPlaybackTake(method);

    // ── The saved playback, from the check's side (part 4d) ──

    /// <summary>The take of the session that just ended, so a Looped back arrangement waits for it to drain.</summary>
    private PlaybackTakeRun? _endingTake;

    /// <summary>Set while a radio change ends the check: the take is ended, never waited for.</summary>
    private bool _skipTakeDrain;

    /// <summary>
    /// This window's last saved playback take of each method, on this radio:
    /// what Play last take prefers over the radio's slot (design section 4).
    /// A later check of the same method that saves nothing removes it, since
    /// the radio's slot is then the newer take.
    /// </summary>
    private readonly System.Collections.Generic.Dictionary<AudioCheckListenMethods, string> _savedTakes = new();

    /// <summary>The saved take Play last take would play for <paramref name="method"/>, if it is still on disk.</summary>
    private string? SavedTakeFor(AudioCheckListenMethods method) =>
        _savedTakes.TryGetValue(method, out string? folder) && System.IO.File.Exists(System.IO.Path.Combine(folder, "take.txt"))
            ? folder : null;

    /// <summary>Whether a saved playback can stand behind Play last take for <paramref name="method"/>.</summary>
    private bool SavedPlaybackOffered(AudioCheckListenMethods method) => SavesPlaybackTake(method) || SavedTakeFor(method) != null;

    /// <summary>
    /// Before the Loopback arrangement comes down (OnSessionEndedAsync): let a
    /// saved Looped back playback finish draining, bounded, tracked as a start
    /// so a teardown in the meantime waits for it. On a teardown or a radio
    /// change the take is ended instead and nothing waits.
    /// </summary>
    private async Task DrainEndingTakeAsync(FlexBase? rig)
    {
        var take = _endingTake;
        _endingTake = null;
        if (take == null || !take.Capturing || take.Finished.IsCompleted) return;
        if (rig?.LoopbackArranged != true) return;
        if (_tearingDown || _skipTakeDrain)
        {
            take.End("connection-ending");
            return;
        }
        using var tracking = rig.CheckRestorations.TrackStart("looped back take drain");
        take.StopRequested("check-ended");
        var bound = Task.Delay(PlaybackTakeRun.StopReportWait + TimeSpan.FromSeconds(1));
        if (await Task.WhenAny(take.Finished, bound) != take.Finished)
        {
            take.End("drain-timeout");
            await Task.WhenAny(take.Finished, Task.Delay(500));
        }
    }

    /// <summary>Play this window's saved playback take, locally, as Play takes plays one.</summary>
    private async Task PlaySavedTakeAsync(string folder)
    {
        try
        {
            var prepared = await ListenbackTakes.PreparePlaybackAsync(folder, 0);
            if (prepared.Trouble != null)
            {
                ScreenReaderOutput.Speak(prepared.Trouble, Radios.Speech.SpeechIntent.Interrupt, VerbosityLevel.Terse,
                    subject: Radios.Speech.SpeechSubject.PlaybackTake);
                return;
            }
            var rig = _rig;
            await ListenbackTakes.PlayAfterClickAsync(prepared.Path!, Lexicon.Get(prepared.AnnouncementKey), rig,
                r => !_closing && ReferenceEquals(r, _rig), offAirListeningGain: false);
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("Check your audio saved take playback: " + ex, System.Diagnostics.TraceLevel.Error);
        }
        SyncPlayTakeLabel();
    }

    // DRAFT PROSE: awaiting Noel.
    private static void SayNoSavedTake() => ScreenReaderOutput.Speak(Lexicon.Get("audio.takes.no_saved_take"),
        Radios.Speech.SpeechIntent.Interrupt, VerbosityLevel.Terse, subject: Radios.Speech.SpeechSubject.PlaybackTake);

    /// <summary>A take this window saved, for Play last take; on the UI thread.</summary>
    private void NoteSavedTake(AudioCheckListenMethods method, PlaybackTakeRun take)
    {
        if (take.DirectoryPath == null) return;
        _savedTakes[method] = take.DirectoryPath;
        SyncPlayTakeLabel();
    }

    /// <summary>The take part of the check session (Unified takes, part 4d).</summary>
    private sealed partial class AudioCheckSession
    {
        private PlaybackTakeRun? _take;
        private Task<bool>? _takeArm;
        private int _takeArmTicks;
        private bool _takeDrainAsked;
        private int _takeDrainTicks;

        /// <summary>
        /// At recording admission, once the radio's recorder has armed and before
        /// anything is keyed: when saving is on, the take's facts are taken now,
        /// as the radio records. Nothing is written until the playback.
        /// </summary>
        private void AdmitTake(FlexBase rig)
        {
            if (!_owner.SavesPlaybackTake(_method))
            {
                // This check's own take lives only in the radio's slot, which is now the newer one.
                _owner._savedTakes.Remove(_method);
                return;
            }
            if (!rig.ReceiveAudioRunning)
            {
                // Saving reads the radio's audio on this computer. The check runs; nothing is saved.
                ScreenReaderOutput.Speak(Lexicon.Get("audio.takes.needs_computer_audio"), Radios.Speech.SpeechIntent.Queue,
                    VerbosityLevel.Terse, subject: Radios.Speech.SpeechSubject.PlaybackTake);
                return;
            }
            try
            {
                _take = PlaybackTakeRun.Admit(rig, _method == AudioCheckListenMethods.Loopback ? TakeKind.LoopedBack : TakeKind.RadioRecording,
                    _recorderTarget);
            }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("Check your audio take admission: " + ex, System.Diagnostics.TraceLevel.Error);
                _take = null;
                return;
            }
            var take = _take;
            var method = _method;
            _ = take.Finished.ContinueWith(made =>
            {
                if (made.IsCompletedSuccessfully && made.Result)
                    _owner.Dispatcher.BeginInvoke(new Action(() => _owner.NoteSavedTake(method, take)));
            }, TaskScheduler.Default);
        }

        /// <summary>A radio change or the connection ending: the take closes incomplete now.</summary>
        internal void EndTake(string reason) => _take?.End(reason);

        /// <summary>
        /// Before the playback is asked for: arm the take (Radio recording mutes
        /// the other audible slices first). False while that is still going on;
        /// bounded, after which the playback goes ahead with nothing saved.
        /// </summary>
        private bool TakeReadyForPlayback(FlexBase rig)
        {
            var take = _take;
            if (take == null) return true;
            if (_takeArm == null)
            {
                _takeArm = take.ArmAsync(ListenbackTakes.Root, (writer, slice) => StartCapture(rig, writer, slice),
                    muteOthers: _method == AudioCheckListenMethods.RecordPlayback);
            }
            if (!_takeArm.IsCompleted)
            {
                if (++_takeArmTicks < 8) return false;
                take.End("arm-timeout");
            }
            bool armed = _takeArm.IsCompletedSuccessfully && _takeArm.Result;
            // DRAFT PROSE: awaiting Noel. Said before "Playing your take.".
            string? notice = !armed ? "audio.takes.not_saved" : !take.Mutes.Isolated ? "audio.takes.not_isolated" : null;
            if (notice != null)
                ScreenReaderOutput.Speak(Lexicon.Get(notice), Radios.Speech.SpeechIntent.Queue, VerbosityLevel.Terse,
                    subject: Radios.Speech.SpeechSubject.PlaybackTake);
            if (!armed) _take = null;
            return true;
        }

        /// <summary>
        /// The playback is over (or stopped, or at the cap): end only once the
        /// take's capture has closed on the radio's reported stop. A stop that
        /// is never reported closes it incomplete; bounded either way.
        /// </summary>
        private bool TakeDrained()
        {
            var take = _take;
            if (take == null || !take.Capturing || take.Finished.IsCompleted) return true;
            if (!_takeDrainAsked)
            {
                _takeDrainAsked = true;
                take.StopRequested("playback-ended");
            }
            if (++_takeDrainTicks > (int)PlaybackTakeRun.StopReportWait.TotalSeconds + 3)
            {
                take.End("drain-timeout");
                return true;
            }
            return false;
        }

        /// <summary>The recorder's second sink on the raw receive tap (round one's engine).</summary>
        private static PlaybackTakeCapture StartCapture(FlexBase rig, AudioTakeWriter writer, int slice) =>
            ComputerRecorder.Current is ComputerFlacRecorder recorder
                ? recorder.StartTakeCapture(rig, writer, slice)
                : throw new InvalidOperationException("no-recorder");
    }
}
