using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Radios;
using Radios.Listenback;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Check your audio, the "Hear what you transmitted, received off the air"
/// method: an IQ take of what actually went out, demodulated and played back
/// after you stop, with its measurements in the reading box.
/// </summary>
/// <remarks>
/// <para>
/// Moved from the Audio Workshop's Hear Yourself listen-back section in #835.
/// What did NOT move with it: the receive-only diagnostic take, the ten-second
/// IQ capacity check and both "Prove it is real" detune buttons, which went to
/// the Workshop's Diagnostics page (ruled 13:42); the Record and Play
/// active-slice buttons and "Compare the last two IQ takes", which are gone
/// (13:57). The rules both windows share live in <see cref="ListenbackTakes"/>.
/// </para>
/// <para>
/// <b>Its options are the two an off-air take reads:</b> the IQ rate and "Hear
/// yourself during the take". An off-air take forces its own separate pan, full
/// duplex and receive antenna inside <c>FlexBase.StartListenbackAsync</c>, so
/// those three boxes would do nothing here; they live with the diagnostic take,
/// the one take that reads them (agreed 2026-10-27).
/// </para>
/// </remarks>
public partial class CheckYourAudioDialog
{
    private ComboBox? _iqRate;
    private CheckBox? _iqHearSelfDuringTake, _iqAutoCompare;
    private Button? _playSentButton, _recoveryButton;
    private ListenbackRun? _iqRun;
    private FlexBase? _iqRunRig;
    private Func<RadioRecoverySnapshot>? _iqRecoveryReader;
    private CancellationTokenSource? _iqCancellation;
    private bool _iqBusy, _iqClosing;
    private readonly DispatcherTimer _iqWatch = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _lastIqDirectory;
    private bool _iqWasOffAir;
    private string? _pendingAutoPlayback, _pendingAutoPrevious;

    /// <summary>The take's own sentence (preparing, recording, its result), shown in the method's status line while Off the air is chosen.</summary>
    private string _offAirStatus = "";

    /// <summary>The last take's measurements, assembled for the reading box.</summary>
    private string _offAirReading = "";
    private string _iqAnalysisDetails = "";
    private string _iqComparisonSentence = "";

    /// <summary>The rate and "hear yourself during the take", in the Method section, shown only while Off the air is chosen.</summary>
    private void BuildOffAirOptions()
    {
        _iqRate = new ComboBox { ItemsSource = new[] { Lexicon.Get("audio.iq.rate24"), Lexicon.Get("audio.iq.rate48"), Lexicon.Get("audio.iq.rate96") }, SelectedIndex = 0, Margin = new Thickness(2) };
        AutomationProperties.SetName(_iqRate, Lexicon.Get("audio.iq.rate_label"));
        JJFlexHelp.SetText(_iqRate, Lexicon.Get("audio.iq.rate_help"));
        _sections.Add(CheckContent, _iqRate);
        _iqHearSelfDuringTake = new CheckBox { Content = Lexicon.Get("audio.iq.hear_during_take"),
            IsChecked = false, Margin = new Thickness(2) };
        AutomationProperties.SetName(_iqHearSelfDuringTake, Lexicon.Get("audio.iq.hear_during_take"));
        _iqHearSelfDuringTake.Checked += (_, _) => { if (!_polling) ToggleFeedback.Announce("Hear yourself during the take", true); };
        _iqHearSelfDuringTake.Unchecked += (_, _) => { if (!_polling) ToggleFeedback.Announce("Hear yourself during the take", false); };
        _sections.Add(CheckContent, _iqHearSelfDuringTake);

        RecordingPlayer.PlaybackCompleted += OnIqPlaybackCompleted;
        Closed += (_, _) => RecordingPlayer.PlaybackCompleted -= OnIqPlaybackCompleted;
        _iqWatch.Tick += async (_, _) =>
        {
            if (_iqRun != null && _iqRun.Capture.Ended.IsCompleted && !_iqBusy)
            {
                if (_iqWasOffAir && _session?.Active == true) _session.StopCheck();
                else await FinishIqAsync("stream-ended");
            }
        };
    }

    /// <summary>
    /// Off the air's results, in the Results section: "Play what I sent", the
    /// comparison choice, and "Review unrestored take settings". Each appears
    /// only when it can act (13:57).
    /// </summary>
    private void BuildOffAirResults()
    {
        _playSentButton = OffAirButton("play_sent", () => _ = PlaySentAsync());

        // "Tell me how my sound changed compared to my last take". Remembered
        // per radio with the check's other choices (Noel, 2026-10-22 18:55, "I'd
        // have it remember its setting yes"). It acts only after an off-air take
        // has played back, so it is shown only for Off the air once a take
        // exists, and never over SmartLink, where no off-air take can be made.
        _iqAutoCompare = new CheckBox { Content = Lexicon.Get("audio.iq.auto_compare"),
            IsChecked = false, Margin = new Thickness(2), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_iqAutoCompare, Lexicon.Get("audio.iq.auto_compare"));
        _iqAutoCompare.Checked += (_, _) => { if (!_polling) { SavePerRadioPrefs(methodChosen: false); ToggleFeedback.Announce("Compare with previous take", true); } };
        _iqAutoCompare.Unchecked += (_, _) => { if (!_polling) { SavePerRadioPrefs(methodChosen: false); ToggleFeedback.Announce("Compare with previous take", false); } };
        _sections.Add(CheckContent, _iqAutoCompare);

        _recoveryButton = OffAirButton("recovery_button", () => _ = RestoreIqAsync());
    }

    private Button OffAirButton(string key, Action click)
    {
        string fullKey = "audio.iq." + key;
        string name = Lexicon.Get(fullKey);
        var button = new Button { Content = name, Margin = new Thickness(2), Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        _sections.Add(CheckContent, button);
        return button;
    }

    /// <summary>Show Off the air's options and results only while they can act.</summary>
    private void ApplyOffAirLayout(AudioCheckListenMethods method)
    {
        bool offAir = method == AudioCheckListenMethods.OffAir;
        bool local = _rig != null && !_rig.RemoteRig;
        var options = offAir ? Visibility.Visible : Visibility.Collapsed;
        if (!offAir && (_iqRate?.IsKeyboardFocusWithin == true || _iqHearSelfDuringTake?.IsKeyboardFocusWithin == true))
            _listenMethodControl?.Focus();
        if (_iqRate != null) _iqRate.Visibility = options;
        if (_iqHearSelfDuringTake != null) _iqHearSelfDuringTake.Visibility = options;

        string? newest = offAir && local ? ListenbackTakes.NewestDirectory() : null;
        SetShown(_iqAutoCompare, newest != null);
        SetShown(_playSentButton, newest != null && File.Exists(Path.Combine(newest, "sent.wav")));
        // Something to restore is shown whatever the method, because it blocks
        // every off-air take until it is dealt with; never over SmartLink.
        SetShown(_recoveryButton, local && _rig?.PendingListenbackRecovery != null);
    }

    private void SetShown(UIElement? element, bool shown)
    {
        if (element == null) return;
        if (!shown && element.IsKeyboardFocusWithin) _startCheckButton?.Focus();
        element.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The take's sentence. Shown in the status line rather than a box of its
    /// own (13:57: one status line), and spoken unless the caller says not.
    /// </summary>
    private void IqStatus(string text, bool failure = false, string? subject = null,
        bool speak = true, string? spokenText = null)
    {
        if (_iqClosing)
        {
            if (failure) PostClosedIqStatus(text, true);
            return;
        }
        _offAirStatus = text;
        UpdateMethodStatus();
        if (!speak) return;
        string said = spokenText ?? text;
        if (failure) EarconPlayer.LeaderInvalidTone();
        if (failure)
        {
            // Let label/focus updates finish first; keep the copyable status
            // immediately, then interrupt incidental slice-count chatter.
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (_iqClosing) PostClosedIqStatus(text, true);
                else ScreenReaderOutput.Speak(said, VerbosityLevel.Critical, interrupt: true);
            }));
        }
        else if (subject != null)
            ScreenReaderOutput.Speak(said, Radios.Speech.SpeechIntent.Queue,
                VerbosityLevel.Terse, subject: subject);
        else ScreenReaderOutput.Speak(said, VerbosityLevel.Terse);
    }

    private void AnnounceClosedRecovery(bool restored) => PostClosedIqStatus(
        Lexicon.Get(restored ? "audio.iq.restored" : "audio.iq.no_recovery"), !restored);

    private void PostClosedIqStatus(string text, bool failure) =>
        ListenbackTakes.PostClosedNotice(Dispatcher, _iqRecoveryReader ?? _rig?.CaptureListenbackRecoveryReader(), text, failure);

    private void IqFailure(Exception ex)
    {
        ScreenReaderOutput.Supersede("audio.iq.preparing", "take preparation failed");
        JJTrace.Tracing.TraceLine("Listenback: " + ex, System.Diagnostics.TraceLevel.Error);
        IqStatus(ListenbackTakes.FailureText(ex), true);
    }

    private bool StillWantsPlayback(FlexBase? rig) => !_iqClosing && ReferenceEquals(rig, _rig);

    /// <summary>
    /// The off-air take restores its own snapshot when it stops. Registered with
    /// the radio's restore owner, held, so a teardown still stops it (and waits
    /// for the restore) after this window has closed (fix round 3). The stop is
    /// idempotent, so FinishIqAsync's own stop and this one share one run.
    /// </summary>
    private void TrackOffAirRun(FlexBase rig, ListenbackRun run) =>
        _iqRunRestore = rig.CheckRestorations.Register("off-air take", async () =>
        {
            // The stop completes normally even when the snapshot could not be
            // put back; Restored is the real answer (fix round 4).
            await run.StopAsync("connection-ending");
            return run.Restored;
        }, held: true);

    /// <summary>The take's entry in the radio's restore owner; released once this window has stopped the take itself.</summary>
    private CheckRestorations.Entry? _iqRunRestore;

    private void ReleaseOffAirRun(FlexBase rig)
    {
        rig.CheckRestorations.Release(_iqRunRestore);
        _iqRunRestore = null;
    }

    private async Task FinishIqAsync(string reason)
    {
        ScreenReaderOutput.Supersede("audio.iq.preparing", "take finished");
        if (_iqBusy || _iqRun == null) return;
        _iqBusy = true;
        _iqWatch.Stop();
        var run = _iqRun;
        var runRig = _iqRunRig;
        EarconPlayer.FeatureOffTone();
        // Key-up is already spoken by the session; this long interim line
        // otherwise competes with the short result at playback start.
        IqStatus(Lexicon.Get("audio.iq.finishing"), speak: false);
        try
        {
            await run.StopAsync(reason);
            // Stopped here; the owner's own stop of the same run now just joins it.
            runRig?.CheckRestorations.Release(_iqRunRestore);
            _iqRunRestore = null;
            if (!ReferenceEquals(runRig, _rig)) return;
            // A take ending because the radio is about to go restores and
            // says so; it does not render and play (fix round 2).
            if (_iqClosing || _tearingDown) { AnnounceClosedRecovery(run.Restored); return; }
            string? previousDirectory = ListenbackTakes.PreviousDirectory(run.DirectoryPath);
            _lastIqDirectory = run.DirectoryPath;
            _iqAnalysisDetails = "";
            _iqComparisonSentence = "";
            var take = TakeReader.Inspect(run.DirectoryPath);
            if (!run.Restored) IqStatus(Lexicon.Get("audio.iq.recovery_pending"), true);
            else if (!take.Playable) IqStatus(Lexicon.Get("audio.iq.corrupt", ("check", take.Reason)), true);
            else
            {
                TakeAnalysisResult? measured = null;
                if (take.Get("mode") is "USB" or "LSB")
                    await Task.Run(() => SsbTakeRenderer.Render(run.DirectoryPath));
                string seconds = ListenbackTakes.Seconds(take);
                if (!run.HasRadioKeying) IqStatus(Lexicon.Get("audio.iq.never_keyed"), true);
                else IqStatus(Lexicon.Get("audio.iq.take_result", ("state", ListenbackTakes.StateText(take.State)),
                    ("seconds", seconds),
                    ("antenna", take.Get("receive-antenna")), ("gain", take.Get("rf-gain")),
                    ("ratio", ListenbackResultText.Ratio(take.Get("pairs-per-arrival-span"))), ("gaps", take.Get("gaps") ?? "0"),
                    ("classifier", take.Get("classifier") ?? Lexicon.Get("audio.iq.unknown"))) +
                    (take.Get("startup-signature") == "detected" ? " " + Lexicon.Get("audio.iq.startup_signature") : ""),
                    speak: false);
                if (run.HasRadioKeying && !_iqClosing && File.Exists(Path.Combine(run.DirectoryPath, "heard.wav")))
                {
                    string heardPath = Path.Combine(run.DirectoryPath, "heard.wav");
                    if (_iqAutoCompare?.IsChecked == true)
                    {
                        _pendingAutoPlayback = heardPath;
                        // Only a take from this session is compared automatically
                        // (Noel, 2026-10-22 07:10); the first take of a session gets none.
                        _pendingAutoPrevious = previousDirectory != null &&
                            TakeSession.IsThisSession(previousDirectory, ListenbackTakes.SessionStartUtc) ? previousDirectory : null;
                    }
                    string shortSummary = Lexicon.Get("audio.iq.take_summary_short",
                        ("state", ListenbackTakes.StateText(take.State)), ("seconds", seconds));
                    if (take.State != "clean") shortSummary += " " + Lexicon.Get("audio.iq.take_summary_gaps",
                        ("gaps", take.Get("gaps") ?? "0"));
                    if (!await ListenbackTakes.PlayAfterClickAsync(heardPath, shortSummary + " " + Lexicon.Get("audio.iq.playing"),
                        _rig, StillWantsPlayback, offAirListeningGain: true)) _pendingAutoPlayback = _pendingAutoPrevious = null;
                }
                // Analysis and measurements are useful details, but neither
                // must keep the listener waiting for the already-rendered audio.
                if (take.Get("mode") is ("USB" or "LSB"))
                    measured = await Task.Run(() => TakeAnalysis.Run(run.DirectoryPath));
                if (measured != null) _iqAnalysisDetails = ListenbackTakes.FormatAnalysisDetails(measured, take);
                await UpdateOffAirReadingAsync(run.DirectoryPath, previousDirectory);
            }
        }
        catch (Exception ex) { if (ReferenceEquals(runRig, _rig)) IqFailure(ex); }
        finally { _iqRun = null; _iqRunRig = null; _iqBusy = false; _iqWasOffAir = false; SetStartButtonLabel("Start Audio Check"); ApplyMethodLayout(); }
    }

    private async Task StartOffAirAsync(PttSafetyController ptt)
    {
        var rig = _rig;
        if (rig == null || _iqBusy || _iqRun != null) return;
        using var start = _checkStarts.TryBegin();
        if (start == null || _session?.Active == true) return;
        // A teardown that begins mid-start waits for this, rollback included.
        using var tracking = TrackStart(rig, "off-air check start");
        _iqBusy = true; _iqWasOffAir = true; _iqRecoveryReader = rig.CaptureListenbackRecoveryReader();
        _iqCancellation = new CancellationTokenSource();
        SetStartButtonLabel(Lexicon.Get("audio.iq.cancel_start"));
        IqStatus(Lexicon.Get("audio.iq.preparing"), subject: "audio.iq.preparing");
        try
        {
            if (rig.OffAirListenbackRefusalReason is { Length: > 0 } refusal)
                throw new InvalidOperationException(refusal);
            int rate = new[] { 24000, 48000, 96000 }[Math.Clamp(_iqRate?.SelectedIndex ?? 0, 0, 2)];
            _iqRun = await rig.StartListenbackAsync(new ListenbackOptions(Rate: rate, OffAir: true,
                HearSelfDuringOffAir: _iqHearSelfDuringTake?.IsChecked == true), _iqCancellation.Token);
            _iqRunRig = rig;
            TrackOffAirRun(rig, _iqRun);
            if (!start.IsCurrent || _iqClosing || _iqCancellation.IsCancellationRequested || !ReferenceEquals(rig, _rig) || ptt.IsTransmitting || rig.Transmit)
                throw new OperationCanceledException();
            var session = new AudioCheckSession(this, rig, ptt, AudioCheckListenMethods.OffAir,
                AudioCheckPowerModes.LowPower, 1, loopback: true);
            if (!await session.StartAsync(start)) throw new InvalidOperationException("keying-refused");
            _session = session;
            SetStartButtonLabel("Stop Audio Check");
            EarconPlayer.FeatureOnTone();
            IqStatus(Lexicon.Get("audio.iq.recording_offair"), speak: false);
            _iqWatch.Start();
            FocusGain();
        }
        catch (Exception ex)
        {
            try { if (_iqRun != null) await _iqRun.StopAsync("start-failed"); }
            catch (Exception cleanup) { JJTrace.Tracing.TraceLine("Listenback cleanup: " + cleanup, System.Diagnostics.TraceLevel.Error); }
            ReleaseOffAirRun(rig);
            _iqRun = null; _iqRunRig = null; _iqWasOffAir = false;
            SetStartButtonLabel("Start Audio Check"); if (ReferenceEquals(rig, _rig)) IqFailure(ex);
        }
        finally { _iqBusy = false; _iqCancellation.Dispose(); _iqCancellation = null; }
    }

    /// <summary>
    /// Off the air's measurements, in the reading box under the mic verdict and
    /// in the same shape: a label, a colon, the readings, a full stop (ruled
    /// 13:57). They were a separate "Take measurements" box until #835.
    /// </summary>
    private async Task UpdateOffAirReadingAsync(string current, string? previous)
    {
        var values = await Task.Run(() => (Current: TakeStatistics.Measure(current, reuseRender: true),
            Previous: previous == null ? null : TakeStatistics.Measure(previous, reuseRender: true)));
        if (_iqClosing || _lastIqDirectory != current) return;
        var lines = new System.Collections.Generic.List<string>
        {
            OffAirMeasuresSentence(Lexicon.Get("audio.check.offair_this_take"), values.Current),
            OffAirMeasuresSentence(Lexicon.Get("audio.check.offair_last_take"), values.Previous),
        };
        if (_iqComparisonSentence.Length > 0) lines.Add(_iqComparisonSentence);
        if (_iqAnalysisDetails.Length > 0) lines.Add(Lexicon.Get("audio.check.offair_sent_heard", ("details", _iqAnalysisDetails)));
        _offAirReading = string.Join(Environment.NewLine, lines);
        UpdateMicReading();
    }

    /// <summary>One take's readings as one sentence, in the reading box's shape.</summary>
    internal static string OffAirMeasuresSentence(string label, TakeStatistics? stats)
    {
        // DRAFT PROSE (#835): awaiting Noel.
        if (stats == null) return Lexicon.Get("audio.check.offair_no_take", ("label", label));
        if (!stats.Available) return Lexicon.Get("audio.check.offair_unavailable", ("label", label), ("reason", stats.Reason));
        return Lexicon.Get("audio.check.offair_measures", ("label", label),
            ("lufs", stats.Lufs.ToString("F1")), ("peak", stats.PeakDbfs.ToString("F1")),
            ("rms", stats.RmsDbfs.ToString("F1")), ("brightness", stats.BrightnessDb.ToString("+0.0;-0.0;0.0")),
            ("punch", stats.PunchDb.ToString("F1")), ("splatter", stats.SplatterDbfs.ToString("F1")));
    }

    private void OnIqPlaybackCompleted(string path)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_iqClosing || path != _pendingAutoPlayback) return;
            string? previous = _pendingAutoPrevious;
            _pendingAutoPlayback = _pendingAutoPrevious = null;
            _ = CompareIqPairAsync(previous, Path.GetDirectoryName(path)!);
        }));
    }

    /// <summary>Play the newest off-air take, through the shared render-and-play rule.</summary>
    private async Task PlayIqAsync(string? directory, int detune)
    {
        if (RecordingPlayer.IsPlaying) { RecordingPlayer.Stop(); return; }
        if (_iqBusy || _iqRun != null || _session?.EscapeStopsTransmit == true || _rig?.Transmit == true) return;
        if (directory == null) { IqStatus(Lexicon.Get("audio.iq.no_take")); return; }
        _iqBusy = true;
        try
        {
            string? trouble = await ListenbackTakes.RenderAndPlayAsync(directory, detune, _rig, StillWantsPlayback);
            if (trouble != null) IqStatus(trouble, true);
        }
        catch (Exception ex) { IqFailure(ex); }
        finally { _iqBusy = false; }
    }

    private async Task PlaySentAsync()
    {
        if (RecordingPlayer.IsPlaying) { RecordingPlayer.Stop(); return; }
        if (_iqBusy || _iqRun != null || _rig?.Transmit == true) return;
        _iqBusy = true;
        try
        {
            string? directory = ListenbackTakes.NewestDirectory();
            if (directory == null || !File.Exists(Path.Combine(directory, "sent.wav")))
            { IqStatus(Lexicon.Get("audio.iq.sent_unavailable")); return; }
            string path = Path.Combine(directory, "sent.wav");
            var facts = TakeHeader.Read(Path.Combine(directory, "sent.txt"));
            if (facts.LastOrDefault(p => p.Key == "sha256").Value != await Task.Run(() => TakeHeader.Checksum(path)))
                throw new InvalidDataException("sent-checksum");
            await Task.Run(() => TakeAudio.ReadMono(path));
            if (_iqClosing) return;
            await ListenbackTakes.PlayAfterClickAsync(path, Lexicon.Get("audio.iq.playing_sent"), _rig, StillWantsPlayback);
        }
        catch (Exception ex) { IqFailure(ex); }
        finally { _iqBusy = false; }
    }

    /// <summary>
    /// The automatic comparison after an off-air take has played back, when
    /// "compare with my last take" is ticked. The one way to compare: the
    /// "Compare the last two IQ takes" button is gone (13:57).
    /// </summary>
    private async Task CompareIqPairAsync(string? previous, string current)
    {
        try
        {
            if (previous == null)
            {
                // The first take of a session is not compared and says nothing
                // about it (Noel, 2026-10-22 07:10); the status line still tells
                // a review cursor why there is no comparison.
                IqStatus(Lexicon.Get("audio.iq.first_of_session"), speak: false);
                return;
            }
            var difference = await Task.Run(() => TakeAnalysis.CompareTakes(previous, current,
                reuseRenders: true));
            if (_iqClosing || _lastIqDirectory != current) return;
            string methodKey = difference.Reason == "overall-average" ? "audio.iq.compared_average" : "audio.iq.compared_aligned";
            string method = Lexicon.Get(methodKey);
            // Round 14: a setup difference the ruling says to compare anyway
            // (filter, power, profile, microphone) is named before the numbers.
            string numbers = ListenbackTakes.WithSetupChanges(difference, difference.Reason == "overall-average"
                ? ListenbackTakes.AverageDifferenceSentence(difference)
                : ListenbackTakes.DifferenceSentence(difference));
            // A take of a different kind (tone against voice, either against the
            // reference) is refused in one full sentence that says which was
            // which and asks for another take.
            string refusal = difference.Reason == "unlike-takes:source" ? ListenbackResultText.UnlikeSource(difference)
                : Lexicon.Get("audio.iq.auto_unavailable");
            _iqComparisonSentence = difference.Comparable
                ? Lexicon.Get("audio.iq.previous_comparison") + " " + method + " " + numbers : refusal;
            await UpdateOffAirReadingAsync(current, previous);
            IqStatus(_iqComparisonSentence, false,
                subject: "audio.iq.auto_comparison",
                spokenText: difference.Comparable ? ListenbackTakes.AutoComparisonSentence(difference) : null);
        }
        catch (Exception)
        {
            IqStatus(Lexicon.Get("audio.iq.auto_unavailable"), subject: "audio.iq.auto_comparison");
        }
    }

    private async Task RestoreIqAsync()
    {
        if (_rig == null || _checkStarts.IsBusy || _iqBusy || _iqRun != null) return;
        try
        {
            _iqBusy = true;
            var result = await ListenbackRecoveryPrompt.ReviewAsync(_rig);
            if (result is { } notice) IqStatus(Lexicon.Get(notice.Key), notice.Failure);
        }
        catch (Exception ex) { IqFailure(ex); }
        finally { _iqBusy = false; ApplyMethodLayout(); }
    }

    private void CloseListenback()
    {
        CancelCheckStart();
        _iqClosing = true;
        _iqCancellation?.Cancel();
        _iqWatch.Stop();
        _ = CloseListenbackAsync(_rig);
    }

    private async Task CloseListenbackAsync(FlexBase? rig)
    {
        if (rig != null) await rig.StopSliceRecorderOwnedByAsync(this);
        if (!_iqBusy) await FinishIqAsync("dialog-closed");
    }
}
