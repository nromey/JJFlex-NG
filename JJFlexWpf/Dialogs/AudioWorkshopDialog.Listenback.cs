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

public partial class AudioWorkshopDialog
{
    private TextBox? _takeStatus;
    private TextBox? _iqStatistics;
    private ComboBox? _iqRate, _iqAntenna;
    private CheckBox? _iqSeparate, _iqFdx, _iqHearSelfDuringTake, _iqAutoCompare;
    private Button? _iqDiagnostic, _iqPreflight, _sliceRecordButton, _slicePlayButton;
    private ListenbackRun? _iqRun;
    private FlexBase? _iqRunRig;
    private Func<RadioRecoverySnapshot>? _iqRecoveryReader;
    private CancellationTokenSource? _iqCancellation;
    private bool _iqBusy, _iqClosing;
    private readonly DispatcherTimer _iqWatch = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _lastIqDirectory;
    private bool _iqWasOffAir;
    private DateTime _radioTakeAt;
    private string? _pendingAutoPlayback, _pendingAutoPrevious;
    private string _iqAnalysisDetails = "";
    private string _iqComparisonMethod = "not compared yet";

    private static string IqStateText(string state)
    {
        string key = "audio.iq.state." + state;
        return Lexicon.Get(key);
    }
    private static string UnalignedKey(string reason) => reason == "not-keyed" ? "audio.iq.no_transmission" :
        reason == "sent-unavailable" ? "audio.iq.sent_unavailable" : "audio.iq.not_aligned";
    private Button IqButton(string key, Action click)
    {
        string fullKey = "audio.iq." + key;
        string name = Lexicon.Get(fullKey);
        var button = new Button { Content = name, Margin = new Thickness(2), Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        AddToSection(HearYourselfContent, button);
        return button;
    }
    private void BuildListenbackControls()
    {
        AddSectionHeader(HearYourselfContent, Lexicon.Get("audio.iq.heading"));
        _takeStatus = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2), Text = Lexicon.Get("audio.iq.idle") };
        AutomationProperties.SetName(_takeStatus, Lexicon.Get("audio.iq.status_label"));
        AddToSection(HearYourselfContent, _takeStatus);
        _iqRate = new ComboBox { ItemsSource = new[] { Lexicon.Get("audio.iq.rate24"), Lexicon.Get("audio.iq.rate48"), Lexicon.Get("audio.iq.rate96") }, SelectedIndex = 0, Margin = new Thickness(2) };
        AutomationProperties.SetName(_iqRate, Lexicon.Get("audio.iq.rate_label"));
        JJFlexHelp.SetText(_iqRate, Lexicon.Get("audio.iq.rate_help"));
        AddToSection(HearYourselfContent, _iqRate);
        _iqSeparate = new CheckBox { Content = Lexicon.Get("audio.iq.separate_pan"), Margin = new Thickness(2) };
        _iqFdx = new CheckBox { Content = Lexicon.Get("audio.iq.full_duplex"), Margin = new Thickness(2) };
        AddToSection(HearYourselfContent, _iqSeparate);
        AddToSection(HearYourselfContent, _iqFdx);
        _iqHearSelfDuringTake = new CheckBox { Content = Lexicon.Get("audio.iq.hear_during_take"),
            IsChecked = false, Margin = new Thickness(2) };
        AddToSection(HearYourselfContent, _iqHearSelfDuringTake);
        // The comparison box used to be built here, between "Hear yourself
        // during the take" and the antenna. It now sits in the Audio Check
        // section just before the listen method (BuildComparisonChoice).
        _iqAntenna = new ComboBox { Margin = new Thickness(2) };
        AutomationProperties.SetName(_iqAntenna, Lexicon.Get("audio.iq.antenna_label"));
        JJFlexHelp.SetText(_iqAntenna, Lexicon.Get("audio.iq.antenna_help"));
        AddToSection(HearYourselfContent, _iqAntenna);
        _iqDiagnostic = IqButton("diagnostic_start", () => _ = ToggleDiagnosticAsync());
        _iqPreflight = IqButton("preflight_button", () => _ = PreflightAsync());
        _iqPreflight.Visibility = Visibility.Collapsed; // SetRig reveals it for local and VPN connections.
        _sliceRecordButton = IqButton("record_slice", () => _ = ToggleSliceRecordAsync());
        _slicePlayButton = IqButton("play_recording", () => _ = ToggleSlicePlayAsync());
        IqButton("recovery_button", () => _ = RestoreIqAsync());
        IqButton("detune_plus", () => _ = PlayIqAsync(NewestIqDirectory(), 1000));
        IqButton("detune_minus", () => _ = PlayIqAsync(NewestIqDirectory(), -1000));
        IqButton("play_sent", () => _ = PlaySentAsync());
        IqButton("compare_previous", () => _ = ComparePreviousIqAsync());
        _iqStatistics = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = true,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(2),
            Text = Lexicon.Get("audio.iq.statistics_empty") };
        AutomationProperties.SetName(_iqStatistics, Lexicon.Get("audio.iq.statistics_label"));
        JJFlexHelp.SetText(_iqStatistics, Lexicon.Get("audio.iq.statistics_help"));
        AddToSection(HearYourselfContent, _iqStatistics);
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
    /// "Tell me how my sound changed compared to my last take": the choice
    /// that, after an off-air take has played back, compares it with the take
    /// before and says what changed. Built into the Audio Check section, just
    /// before the listen method (Noel at the round 14 bench, 2026-10-22 07:01,
    /// "yep"): it belongs to takes, not to the tone. Round 14 had first put it
    /// at the end of the Test Tone section (Noel, 2026-10-21 20:12, "before the
    /// listen source"), and at the bench it read as part of the tone. It still
    /// comes before the listen source, which is what that ruling asked.
    ///
    /// <para>The tick is remembered per radio with the Audio Check's other
    /// choices (<see cref="SavePerRadioPrefs"/>, <see cref="LoadPerRadioPrefs"/>):
    /// Noel, 18:55 the same evening, "I'd have it remember its setting yes".
    /// The tone checkbox one stop above is deliberately NOT remembered.</para>
    ///
    /// <para>Over SmartLink it is collapsed, out of the tab order
    /// (<see cref="UpdateIqAvailability"/>): it only ever acts after an
    /// OFF-AIR take, and an off-air take refuses over SmartLink, so there it is
    /// a control that can do nothing, and the house rule takes those out of
    /// the ring.</para>
    /// </summary>
    private void BuildComparisonChoice()
    {
        _iqAutoCompare = new CheckBox { Content = Lexicon.Get("audio.iq.auto_compare"),
            IsChecked = false, Margin = new Thickness(2) };
        AutomationProperties.SetName(_iqAutoCompare, Lexicon.Get("audio.iq.auto_compare"));
        _iqAutoCompare.Checked += (_, _) => { if (!_polling) SavePerRadioPrefs(); };
        _iqAutoCompare.Unchecked += (_, _) => { if (!_polling) SavePerRadioPrefs(); };
        AddToSection(HearYourselfContent, _iqAutoCompare);
    }
    private void UpdateIqAvailability()
    {
        SyncSliceRecorderUi();
        if (_iqPreflight != null)
            _iqPreflight.Visibility = _rig != null && !_rig.RemoteRig ? Visibility.Visible : Visibility.Collapsed;
        // Hidden over SmartLink, where the off-air take it follows cannot run.
        // With no radio it stays visible: the whole Test Tone section is then
        // disabled, which already keeps it out of the ring while leaving it
        // discoverable to a review cursor.
        if (_iqAutoCompare != null)
            _iqAutoCompare.Visibility = _rig?.RemoteRig == true ? Visibility.Collapsed : Visibility.Visible;
        if (_iqAntenna == null || _iqRun != null || _iqBusy) return;
        var names = new[] { Lexicon.Get("audio.iq.antenna_unchanged") }.Concat(_rig?.ListenbackReceiveAntennas ?? Array.Empty<string>()).ToArray();
        if (!(_iqAntenna.ItemsSource is string[] old) || !old.SequenceEqual(names))
        { _iqAntenna.ItemsSource = names; _iqAntenna.SelectedIndex = 0; }
        if (_takeStatus != null && _lastIqDirectory == null)
        {
            string reason = _rig?.OffAirListenbackRefusalReason ?? "no-radio";
            string reasonKey = "audio.iq.reason." + reason;
            _takeStatus.Text = _rig == null ? Lexicon.Get("audio.no_radio_connected") :
                reason.Length == 0 ? Lexicon.Get("audio.iq.idle") + " " + Lexicon.Get("audio.iq.capacity_optional") :
                Lexicon.Get(reasonKey);
        }
    }
    private bool SliceRecorderTargetsLoopback => !_iqWasOffAir && _session?.Active == true &&
        _session.IsLoopback && _rig?.LoopbackArranged == true;
    /// <summary>
    /// Which slice the Workshop's Record slice and Play recording buttons act
    /// on: the Loopback Check's listening slice while one runs, otherwise the
    /// active slice. These two buttons are doors to the one recorder (#787,
    /// <see cref="FlexBase.ToggleSliceRecordAsync"/>), the same one Home, the
    /// Slice menu and the JJ key use.
    /// </summary>
    private SliceRecorderTarget WorkshopRecorderTarget => SliceRecorderTargetsLoopback
        ? SliceRecorderTarget.LoopbackEars : SliceRecorderTarget.ActiveSlice;
    private void SyncSliceRecorderUi()
    {
        var target = WorkshopRecorderTarget;
        if (_sliceRecordButton != null)
        {
            _sliceRecordButton.IsEnabled = _rig != null;
            string label = _rig?.IsSliceRecording(target) == true
                ? Lexicon.Get("audio.iq.stop_slice_record") : Lexicon.Get("audio.iq.record_slice");
            _sliceRecordButton.Content = label;
            AutomationProperties.SetName(_sliceRecordButton, label);
        }
        if (_slicePlayButton != null)
        {
            // A running playback may always be stopped. New playback needs
            // the target slice's radio-reported PlayEnabled flag.
            bool playing = _rig?.IsSlicePlaying(target) == true;
            _slicePlayButton.IsEnabled = playing || _rig?.SliceHasRecording(target) == true;
            string label = RecorderControls.SlicePlayLabel(_rig, target);
            _slicePlayButton.Content = label;
            AutomationProperties.SetName(_slicePlayButton, label);
        }
    }
    private async Task ToggleSliceRecordAsync()
    {
        if (_rig == null) return;
        await RecorderControls.ToggleSliceRecordAsync(_rig, WorkshopRecorderTarget, owner: this);
        SyncSliceRecorderUi();
    }
    private async Task ToggleSlicePlayAsync()
    {
        if (_rig == null) return;
        await RecorderControls.ToggleSlicePlayAsync(_rig, WorkshopRecorderTarget, owner: this);
        SyncSliceRecorderUi();
    }
    private void IqStatus(string text, bool failure = false, string? subject = null,
        bool speak = true, string? spokenText = null)
    {
        if (_iqClosing)
        {
            if (failure) PostClosedIqStatus(text, true);
            return;
        }
        if (_takeStatus != null) _takeStatus.Text = text;
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

    private void PostClosedIqStatus(string text, bool failure)
    {
        var readRecovery = _iqRecoveryReader ?? _rig?.CaptureListenbackRecoveryReader();
        // Query at delivery, after the closing focus transition and after
        // cleanup. Cancellation or playback failure does not imply a record.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            ListenbackCloseNotice notice;
            try { notice = ListenbackCloseNotice.ForCurrentRecord(readRecovery?.Invoke(), text, failure); }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("Listenback close record read: " + ex, System.Diagnostics.TraceLevel.Error);
                notice = new(Lexicon.Get("audio.iq.recovery_state_unknown"), true);
            }
            if (notice.Failure) EarconPlayer.LeaderInvalidTone(); else EarconPlayer.ConfirmTone();
            ScreenReaderOutput.Speak(notice.Text, VerbosityLevel.Critical);
        }));
    }
    private void IqFailure(Exception ex)
    {
        ScreenReaderOutput.Supersede("audio.iq.preparing", "take preparation failed");
        JJTrace.Tracing.TraceLine("Listenback: " + ex, System.Diagnostics.TraceLevel.Error);
        string reason = ex is OperationCanceledException ? "cancelled" : ex.Message;
        string key = "audio.iq.reason." + reason;
        string text = Lexicon.Get(key);
        if (text == key) text = Lexicon.Get("audio.iq.reason.unexpected");
        IqStatus(Lexicon.Get("audio.iq.failed", ("reason", text)), true);
    }
    private void DiagnosticLabel(string key)
    {
        if (_iqDiagnostic == null) return;
        string fullKey = "audio.iq." + key;
        string name = Lexicon.Get(fullKey);
        _iqDiagnostic.Content = name;
        AutomationProperties.SetName(_iqDiagnostic, name);
    }
    private async Task ToggleDiagnosticAsync()
    {
        if (_checkStarts.IsBusy) { IqStatus(Lexicon.Get("audio.iq.stop_other"), true); return; }
        if (_iqBusy) { _iqCancellation?.Cancel(); return; }
        if (_iqRun != null) { await FinishIqAsync("operator-stop"); return; }
        if (_rig == null || _session?.Active == true) { IqStatus(Lexicon.Get("audio.iq.stop_other"), true); return; }
        var rig = _rig;
        _iqBusy = true;
        _iqWasOffAir = false;
        _iqRecoveryReader = rig.CaptureListenbackRecoveryReader();
        _iqCancellation = new CancellationTokenSource();
        DiagnosticLabel("cancel_start");
        IqStatus(Lexicon.Get("audio.iq.preparing"));
        try
        {
            int rate = new[] { 24000, 48000, 96000 }[Math.Clamp(_iqRate?.SelectedIndex ?? 0, 0, 2)];
            string? antenna = _iqAntenna?.SelectedIndex > 0 ? _iqAntenna.SelectedItem as string : null;
            _iqRun = await rig.StartListenbackAsync(new(rate, _iqSeparate?.IsChecked == true,
                _iqFdx?.IsChecked == true, antenna), _iqCancellation.Token);
            _iqRunRig = rig;
            if (_iqClosing || _iqCancellation.IsCancellationRequested || !ReferenceEquals(rig, _rig))
            {
                await _iqRun.StopAsync("cancelled");
                if (_iqClosing) AnnounceClosedRecovery(_iqRun.Restored);
                else if (ReferenceEquals(rig, _rig)) IqStatus(Lexicon.Get(_iqRun.Restored ? "audio.iq.restored" : "audio.iq.recovery_pending"), !_iqRun.Restored);
                _iqRun = null; return;
            }
            DiagnosticLabel("diagnostic_stop");
            EarconPlayer.FeatureOnTone();
            IqStatus(Lexicon.Get("audio.iq.recording_diagnostic"));
            _iqWatch.Start();
        }
        catch (Exception ex) { DiagnosticLabel("diagnostic_start"); if (ReferenceEquals(rig, _rig)) IqFailure(ex); }
        finally { _iqBusy = false; _iqCancellation.Dispose(); _iqCancellation = null; }
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
        IqStatus(Lexicon.Get("audio.iq.finishing"), speak: !_iqWasOffAir);
        try
        {
            await run.StopAsync(reason);
            if (!ReferenceEquals(runRig, _rig)) return;
            if (_iqClosing) { AnnounceClosedRecovery(run.Restored); return; }
            string? previousDirectory = PreviousIqDirectory(run.DirectoryPath);
            _lastIqDirectory = run.DirectoryPath;
            _iqAnalysisDetails = "";
            _iqComparisonMethod = "not compared yet";
            var take = TakeReader.Inspect(run.DirectoryPath);
            if (!run.Restored) IqStatus(Lexicon.Get("audio.iq.recovery_pending"), true);
            else if (!take.Playable) IqStatus(Lexicon.Get("audio.iq.corrupt", ("check", take.Reason)), true);
            else
            {
                TakeAnalysisResult? measured = null;
                if (take.Get("mode") is "USB" or "LSB")
                    await Task.Run(() => SsbTakeRenderer.Render(run.DirectoryPath));
                if (!_iqWasOffAir && take.Get("mode") is ("USB" or "LSB"))
                    measured = await Task.Run(() => TakeAnalysis.Run(run.DirectoryPath));
                string seconds = (double.Parse(take.Get("pairs-written") ?? "0", System.Globalization.CultureInfo.InvariantCulture) /
                    int.Parse(take.Get("rate-requested"), System.Globalization.CultureInfo.InvariantCulture)).ToString("F1");
                if (_iqWasOffAir && !run.HasRadioKeying) IqStatus(Lexicon.Get("audio.iq.never_keyed"), true);
                else IqStatus(Lexicon.Get(_iqWasOffAir ? "audio.iq.take_result" : "audio.iq.diagnostic_result", ("state", IqStateText(take.State)),
                    ("seconds", seconds),
                    ("antenna", take.Get("receive-antenna")), ("gain", take.Get("rf-gain")),
                    ("ratio", ListenbackResultText.Ratio(take.Get("pairs-per-arrival-span"))), ("gaps", take.Get("gaps") ?? "0"),
                    ("classifier", take.Get("classifier") ?? Lexicon.Get("audio.iq.unknown"))) +
                    (take.Get("startup-signature") == "detected" ? " " + Lexicon.Get("audio.iq.startup_signature") : ""),
                    speak: !_iqWasOffAir);
                if (_iqWasOffAir && run.HasRadioKeying && !_iqClosing && File.Exists(Path.Combine(run.DirectoryPath, "heard.wav")))
                {
                    string heardPath = Path.Combine(run.DirectoryPath, "heard.wav");
                    if (_iqAutoCompare?.IsChecked == true)
                    {
                        _pendingAutoPlayback = heardPath;
                        // Only a take from this session is compared automatically
                        // (Noel, 2026-10-22 07:10); the first take of a session gets none.
                        _pendingAutoPrevious = previousDirectory != null &&
                            TakeSession.IsThisSession(previousDirectory, SessionStartUtc) ? previousDirectory : null;
                    }
                    string shortSummary = Lexicon.Get("audio.iq.take_summary_short",
                        ("state", IqStateText(take.State)), ("seconds", seconds));
                    if (take.State != "clean") shortSummary += " " + Lexicon.Get("audio.iq.take_summary_gaps",
                        ("gaps", take.Get("gaps") ?? "0"));
                    if (!await PlayTakeAfterClickAsync(heardPath, shortSummary + " " + Lexicon.Get("audio.iq.playing"),
                        _rig, offAirListeningGain: true)) _pendingAutoPlayback = _pendingAutoPrevious = null;
                }
                // Analysis and measurements are useful details, but neither
                // must keep the listener waiting for the already-rendered audio.
                if (_iqWasOffAir && take.Get("mode") is ("USB" or "LSB"))
                    measured = await Task.Run(() => TakeAnalysis.Run(run.DirectoryPath));
                if (measured != null) _iqAnalysisDetails = FormatIqAnalysisDetails(measured, take);
                await UpdateIqStatisticsAsync(run.DirectoryPath, previousDirectory);
            }
        }
        catch (Exception ex) { if (ReferenceEquals(runRig, _rig)) IqFailure(ex); }
        finally { _iqRun = null; _iqRunRig = null; _iqBusy = false; _iqWasOffAir = false; DiagnosticLabel("diagnostic_start"); SetStartButtonLabel("Start Audio Check"); UpdateIqAvailability(); }
    }
    private async Task StartOffAirAsync(PttSafetyController ptt)
    {
        var rig = _rig;
        if (rig == null || _iqBusy || _iqRun != null) return;
        using var start = _checkStarts.TryBegin();
        if (start == null || _session?.Active == true) return;
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
            if (!start.IsCurrent || _iqClosing || _iqCancellation.IsCancellationRequested || !ReferenceEquals(rig, _rig) || ptt.IsTransmitting || rig.Transmit)
                throw new OperationCanceledException();
            var session = new AudioCheckSession(this, rig, ptt, AudioCheckListenMethods.OffAir,
                AudioCheckPowerModes.LowPower, 1, loopback: true);
            if (!await session.StartAsync(start)) throw new InvalidOperationException("keying-refused");
            _session = session;
            SetStartButtonLabel("Stop Audio Check");
            EarconPlayer.FeatureOnTone();
            if (_takeStatus != null) _takeStatus.Text = Lexicon.Get("audio.iq.recording_offair");
            _iqWatch.Start();
            _micGainControl?.Focus();
        }
        catch (Exception ex)
        {
            try { if (_iqRun != null) await _iqRun.StopAsync("start-failed"); }
            catch (Exception cleanup) { JJTrace.Tracing.TraceLine("Listenback cleanup: " + cleanup, System.Diagnostics.TraceLevel.Error); }
            _iqRun = null; _iqRunRig = null; _iqWasOffAir = false;
            SetStartButtonLabel("Start Audio Check"); if (ReferenceEquals(rig, _rig)) IqFailure(ex);
        }
        finally { _iqBusy = false; _iqCancellation.Dispose(); _iqCancellation = null; }
    }
    private static string? NewestIqDirectory()
    {
        string root = Path.Combine(RadioConfig.AppDataRoot, "Recordings", "takes");
        return !Directory.Exists(root) ? null : Directory.EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "heard.wav")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "take.txt"))).FirstOrDefault();
    }
    /// <summary>When this run of the app began: the start of the session the
    /// automatic comparison is confined to (<see cref="TakeSession"/>).</summary>
    private static readonly DateTime SessionStartUtc =
        System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private static string? PreviousIqDirectory(string current)
    {
        string root = Path.Combine(RadioConfig.AppDataRoot, "Recordings", "takes");
        return !Directory.Exists(root) ? null : Directory.EnumerateDirectories(root)
            .Where(d => d != current && File.Exists(Path.Combine(d, "heard.wav")) &&
                File.Exists(Path.Combine(d, "take.txt")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "take.txt"))).FirstOrDefault();
    }
    private async Task<bool> PlayTakeAfterClickAsync(string path, string announcement,
        FlexBase? rig, bool offAirListeningGain = false)
    {
        RecordingPlayer.Stop();
        bool priorMute = rig?.LocalReceiveMutedForTakePlayback ?? false;
        if (rig != null) rig.LocalReceiveMutedForTakePlayback = true;
        bool started = false;
        try
        {
            // confirm.wav lasts 200 ms. Mute live receive before this click,
            // then let the click finish before adding the take to the mixer.
            EarconPlayer.ConfirmTone();
            await Task.Delay(240);
            if (_iqClosing || !ReferenceEquals(rig, _rig)) return false;
            started = RecordingPlayer.Play(path, announcement, rig, offAirListeningGain,
                priorLiveMute: priorMute);
            return started;
        }
        finally { if (!started && rig != null) rig.LocalReceiveMutedForTakePlayback = priorMute; }
    }
    private static string FormatIqStatistics(string label, TakeStatistics? stats)
    {
        if (stats == null) return label + ": no earlier take.";
        if (!stats.Available) return label + ": " + stats.Reason + ".";
        return string.Join(Environment.NewLine, new[] {
            $"{label} integrated loudness: {stats.Lufs:F1} LUFS",
            $"{label} peak: {stats.PeakDbfs:F1} dBFS",
            $"{label} RMS: {stats.RmsDbfs:F1} dBFS",
            $"{label} brightness, highs versus lows: {stats.BrightnessDb:+0.0;-0.0;0.0} dB",
            $"{label} punch, crest: {stats.PunchDb:F1} dB",
            $"{label} splatter, out-of-filter: {stats.SplatterDbfs:F1} dBFS" });
    }
    private static string FormatIqAnalysisDetails(TakeAnalysisResult measured, TakeInspection take)
    {
        string details = measured.Alignment.Aligned ?
            Lexicon.Get("audio.iq.aligned", ("ms", measured.Alignment.LagMilliseconds(
                int.Parse(take.Get("rate-echoed"))).ToString("F1"))) :
            Lexicon.Get(UnalignedKey(measured.Alignment.Reason));
        if (measured.Difference.Comparable) details += " " + ReferenceDifferenceSentence(measured.Difference);
        if (measured.Tone.Reason != "no-recorded-keyed-tone") details += " " + Lexicon.Get(
            measured.Tone.Identified ? "audio.iq.tone_identified" : "audio.iq.tone_not_identified",
            ("hz", measured.Tone.OffsetHz.ToString("F1")), ("db", measured.Tone.AboveRestDb.ToString("F1")));
        return details;
    }
    private async Task UpdateIqStatisticsAsync(string current, string? previous, string? method = null)
    {
        if (method != null) _iqComparisonMethod = method;
        var values = await Task.Run(() => (Current: TakeStatistics.Measure(current, reuseRender: true),
            Previous: previous == null ? null : TakeStatistics.Measure(previous, reuseRender: true)));
        if (_iqClosing || _iqStatistics == null || _lastIqDirectory != current) return;
        _iqStatistics.Text = FormatIqStatistics("This take", values.Current) + Environment.NewLine +
            FormatIqStatistics("Last take", values.Previous) + Environment.NewLine +
            "Comparison method: " + _iqComparisonMethod +
            (_iqAnalysisDetails.Length == 0 ? "" : Environment.NewLine + "Take analysis: " + _iqAnalysisDetails);
    }
    private void OnIqPlaybackCompleted(string path)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_iqClosing || path != _pendingAutoPlayback) return;
            string? previous = _pendingAutoPrevious;
            _pendingAutoPlayback = _pendingAutoPrevious = null;
            _ = CompareIqPairAsync(previous, Path.GetDirectoryName(path)!, auto: true);
        }));
    }
    private bool TryPlayNewestIq(RecordingStore.RecordingFile? file, bool radioHasTake)
    {
        string? directory = NewestIqDirectory();
        if (directory == null) return false;
        DateTime time = File.GetLastWriteTimeUtc(Path.Combine(directory, "take.txt"));
        if (file != null && file.RecordedLocal.ToUniversalTime() > time) return false;
        if (radioHasTake && _radioTakeAt > time) return false;
        _ = PlayIqAsync(directory, 0);
        return true;
    }
    private async Task PlayIqAsync(string? directory, int detune)
    {
        if (RecordingPlayer.IsPlaying) { RecordingPlayer.Stop(); return; }
        if (_iqBusy || _iqRun != null || _session?.EscapeStopsTransmit == true || _rig?.Transmit == true) return;
        if (directory == null) { IqStatus(Lexicon.Get("audio.iq.no_take")); return; }
        _iqBusy = true;
        try
        {
            var take = await Task.Run(() => TakeReader.Inspect(directory));
            if (!take.Playable) { IqStatus(Lexicon.Get("audio.iq.corrupt", ("check", take.Reason)), true); return; }
            var rendered = await Task.Run(() => SsbTakeRenderer.Render(directory, detune));
            if (_iqClosing) return;
            string playbackKey = take.State == "incomplete" ? "audio.iq.playing_incomplete" : detune == 0 ? "audio.iq.playing" : "audio.iq.playing_detuned";
            await PlayTakeAfterClickAsync(rendered.Path, Lexicon.Get(playbackKey), _rig,
                offAirListeningGain: true);
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
            string? directory = NewestIqDirectory();
            if (directory == null || !File.Exists(Path.Combine(directory, "sent.wav")))
            { IqStatus(Lexicon.Get("audio.iq.sent_unavailable")); return; }
            string path = Path.Combine(directory, "sent.wav");
            var facts = TakeHeader.Read(Path.Combine(directory, "sent.txt"));
            if (facts.LastOrDefault(p => p.Key == "sha256").Value != await Task.Run(() => TakeHeader.Checksum(path)))
                throw new InvalidDataException("sent-checksum");
            await Task.Run(() => TakeAudio.ReadMono(path));
            if (_iqClosing) return;
            await PlayTakeAfterClickAsync(path, Lexicon.Get("audio.iq.playing_sent"), _rig);
        }
        catch (Exception ex) { IqFailure(ex); }
        finally { _iqBusy = false; }
    }
    private const double AudibleComparisonDb = 0.5;
    private static bool Changed(double db) => Math.Abs(db) >= AudibleComparisonDb;

    private static string LevelAndToneSentence(AudioDifference difference, bool sentReference = false)
    {
        bool levelChanged = Changed(difference.LevelDb), toneChanged = Changed(difference.TiltDb);
        string key = (sentReference, levelChanged, toneChanged) switch
        {
            (false, false, false) => "audio.iq.same_level_tone",
            (false, true, false) => "audio.iq.level_changed_tone_same",
            (false, false, true) => "audio.iq.level_same_tone_changed",
            (false, true, true) => "audio.iq.level_and_tone_changed",
            (true, false, false) => "audio.iq.reference_same_level_tone",
            (true, true, false) => "audio.iq.reference_level_changed_tone_same",
            (true, false, true) => "audio.iq.reference_level_same_tone_changed",
            _ => "audio.iq.reference_level_and_tone_changed"
        };
        return Lexicon.Get(key,
            ("level_word", Lexicon.Get(difference.LevelDb >= 0 ? "audio.iq.louder" : "audio.iq.quieter")),
            ("level", Math.Abs(difference.LevelDb).ToString("F1")),
            ("brightness_word", Lexicon.Get(difference.TiltDb >= 0 ? "audio.iq.brighter" : "audio.iq.darker")),
            ("high_word", Lexicon.Get(difference.TiltDb >= 0 ? "audio.iq.stronger" : "audio.iq.weaker")),
            ("tilt", Math.Abs(difference.TiltDb).ToString("F1")));
    }
    private static string AlignedDifferenceSentence(AudioDifference difference, bool sentReference)
    {
        bool crestChanged = Changed(difference.CrestDb), stopChanged = Changed(difference.StopbandDb);
        if (!Changed(difference.LevelDb) && !Changed(difference.TiltDb) &&
            !crestChanged && !stopChanged)
            return Lexicon.Get(sentReference ? "audio.iq.reference_all_measures_same" : "audio.iq.all_measures_same");
        string punch = crestChanged ? Lexicon.Get("audio.iq.punch_changed",
            ("crest_word", Lexicon.Get(difference.CrestDb < 0 ? "audio.iq.more_compressed" : "audio.iq.more_peaky")),
            ("crest", Math.Abs(difference.CrestDb).ToString("F1"))) : Lexicon.Get("audio.iq.punch_same");
        string stopband = stopChanged ? Lexicon.Get("audio.iq.stopband_changed",
            ("direction", Lexicon.Get(difference.StopbandDb >= 0 ? "audio.iq.rose" : "audio.iq.fell")),
            ("stop", Math.Abs(difference.StopbandDb).ToString("F1"))) : Lexicon.Get("audio.iq.stopband_same");
        return LevelAndToneSentence(difference, sentReference) + " " + punch + " " + stopband;
    }
    internal static string DifferenceSentence(AudioDifference difference) =>
        AlignedDifferenceSentence(difference, sentReference: false);
    internal static string ReferenceDifferenceSentence(AudioDifference difference) =>
        AlignedDifferenceSentence(difference, sentReference: true);
    internal static string AverageDifferenceSentence(AudioDifference difference) => LevelAndToneSentence(difference);
    /// <summary>
    /// The spoken result of the automatic comparison after an off-air take has
    /// played back: the intro, then (round 14) any setup difference named in
    /// front of the numbers, then the numbers. Assembled, as NVDA hears it:
    /// "Compared with last take. Your microphone profile was Default on your
    /// last take and is Heil PR 781 on this one. Your new take is quieter by
    /// 2.1 dB than your last take, with the same tone."
    /// </summary>
    internal static string AutoComparisonSentence(AudioDifference difference) =>
        Lexicon.Get("audio.iq.auto_result_intro") + " " + WithSetupChanges(difference, LevelAndToneSentence(difference));
    private static string WithSetupChanges(AudioDifference difference, string numbers)
    {
        string changes = ListenbackResultText.SetupChanges(difference);
        return changes.Length == 0 ? numbers : changes + " " + numbers;
    }
    private async Task ComparePreviousIqAsync()
    {
        if (_iqBusy || _iqRun != null || _rig?.Transmit == true) return;
        _iqBusy = true;
        try
        {
            string root = Path.Combine(RadioConfig.AppDataRoot, "Recordings", "takes");
            var pair = Directory.Exists(root) ? Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "heard.wav")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "take.txt"))).Take(2).ToArray() : Array.Empty<string>();
            if (pair.Length == 0) { IqStatus(Lexicon.Get("audio.iq.need_two")); return; }
            if (pair.Length == 1) { await CompareIqPairAsync(null, pair[0], auto: false); return; }
            await CompareIqPairAsync(pair[1], pair[0], auto: false);
        }
        catch (Exception ex) { IqFailure(ex); }
        finally { _iqBusy = false; }
    }
    private async Task CompareIqPairAsync(string? previous, string current, bool auto)
    {
        try
        {
            if (previous == null)
            {
                await UpdateIqStatisticsAsync(current, null, "not comparable; no last take");
                // The first take of a session is not compared and says nothing
                // about it (Noel, 2026-10-22 07:10); the status line still tells
                // a review cursor why there is no comparison.
                if (auto) IqStatus(Lexicon.Get("audio.iq.first_of_session"), speak: false);
                else IqStatus(Lexicon.Get("audio.iq.need_two"));
                return;
            }
            var difference = await Task.Run(() => TakeAnalysis.CompareTakes(previous, current,
                reuseRenders: true));
            if (_iqClosing || auto && _lastIqDirectory != current) return;
            string methodKey = difference.Reason == "overall-average" ? "audio.iq.compared_average" : "audio.iq.compared_aligned";
            string method = Lexicon.Get(methodKey);
            // Round 14: a setup difference the ruling says to compare anyway
            // (filter, power, profile, microphone) is named before the numbers.
            string numbers = WithSetupChanges(difference, difference.Reason == "overall-average"
                ? AverageDifferenceSentence(difference)
                : DifferenceSentence(difference));
            string changes = ListenbackResultText.SetupChanges(difference);
            await UpdateIqStatisticsAsync(current, previous,
                difference.Comparable ? method + (changes.Length == 0 ? "" : " " + changes) : "not comparable; " + difference.Reason);
            // A take of a different kind (tone against voice, either against
            // the reference) is refused in one full sentence that says which was
            // which and asks for another take, on both the automatic and the
            // manual road. Every other refusal keeps its older sentence.
            // A manual comparison may reach a take from an earlier session, and
            // then says so first (Noel, 2026-10-22 07:10).
            string session = !auto && !TakeSession.IsThisSession(previous, SessionStartUtc)
                ? TakeSession.EarlierSessionNote(TakeSession.MadeUtc(previous).ToLocalTime(), DateTime.Now) + " " : "";
            string refusal = difference.Reason == "unlike-takes:source" ? ListenbackResultText.UnlikeSource(difference)
                : Lexicon.Get(auto ? "audio.iq.auto_unavailable" : "audio.iq.unlike_or_unaligned");
            IqStatus(session + (difference.Comparable ? Lexicon.Get("audio.iq.previous_comparison") + " " + method + " " + numbers : refusal),
                !difference.Comparable && !auto,
                subject: auto ? "audio.iq.auto_comparison" : null,
                spokenText: auto && difference.Comparable ? AutoComparisonSentence(difference) : null);
        }
        catch (Exception ex)
        {
            if (auto) IqStatus(Lexicon.Get("audio.iq.auto_unavailable"),
                subject: "audio.iq.auto_comparison");
            else IqFailure(ex);
        }
    }
    private async Task PreflightAsync()
    {
        if (_iqBusy) { _iqCancellation?.Cancel(); return; }
        var rig = _rig;
        if (rig == null || _checkStarts.IsBusy || rig.RemoteRig || _iqRun != null || _session?.Active == true) { IqStatus(Lexicon.Get("audio.iq.stop_other"), true); return; }
        _iqBusy = true; _iqRecoveryReader = rig.CaptureListenbackRecoveryReader(); _iqCancellation = new CancellationTokenSource();
        IqStatus(Lexicon.Get("audio.iq.preflight_running"));
        try
        {
            bool clean = await rig.RunListenbackPreflightAsync(_iqCancellation.Token);
            if (ReferenceEquals(rig, _rig)) IqStatus(Lexicon.Get(clean ? "audio.iq.preflight_clean" : "audio.iq.preflight_loss"), !clean);
        }
        catch (Exception ex) { if (ReferenceEquals(rig, _rig)) IqFailure(ex); }
        finally { _iqBusy = false; _iqCancellation.Dispose(); _iqCancellation = null; }
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
        finally { _iqBusy = false; }
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
