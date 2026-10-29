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
/// Audio Workshop, Diagnostics: the bench tools that prove the take machinery
/// works rather than check how you sound — the receive-only diagnostic take,
/// the ten-second IQ capacity check, and both "Prove it is real" detune
/// buttons.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why they are here (#835, ruled by Noel 2026-10-27 13:42: "I'd move all
/// those to diagnostics, yes").</b> They were on the Hear Yourself page beside
/// the check, and a screen-reader user tabbing through it met tools for
/// verifying the feature in the middle of the controls for using it. Hear
/// Yourself is Check your audio now, and it holds only ways of checking how you
/// sound.
/// </para>
/// <para>
/// <b>Nothing here keys the radio,</b> which keeps the Diagnostics page's
/// opening sentence true. The diagnostic take records straight through
/// whatever the operator transmits while it runs, and the capacity check and
/// the detune buttons never transmit at all.
/// </para>
/// <para>
/// <b>The diagnostic take's options live with it.</b> Separate listening pan,
/// full duplex and receive antenna are read only by this take; an off-air take
/// forces all three for itself, so they live here (agreed 2026-10-27).
/// </para>
/// <para>
/// <b>Not over SmartLink.</b> These are IQ tools, and SmartLink carries no IQ
/// (ruled 2026-10-22 08:08); the section is replaced by one sentence saying so.
/// </para>
/// </remarks>
public partial class AudioWorkshopDialog
{
    private GroupBox? _benchSection;
    private TextBlock? _benchUnavailable;
    private TextBox? _benchStatus;
    private ComboBox? _benchRate, _benchAntenna;
    private CheckBox? _benchSeparate, _benchFdx;
    private Button? _benchDiagnostic, _benchPreflight, _benchRecovery;
    private ListenbackRun? _benchRun;
    private FlexBase? _benchRunRig;
    private Func<RadioRecoverySnapshot>? _benchRecoveryReader;
    private CancellationTokenSource? _benchCancellation;
    private bool _benchBusy, _benchClosing;
    private readonly DispatcherTimer _benchWatch = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>True while the Workshop's diagnostic take is starting or running, so Check your audio refuses a check rather than keying into it.</summary>
    internal static bool ABenchTakeIsRunning => _instance != null && (_instance._benchRun != null || _instance._benchBusy);

    private void BuildBenchToolsSection()
    {
        // DRAFT PROSE (#835): awaiting Noel.
        _benchSection = AddRadioSection(DiagnosticsContent, "Bench tools");
        AddToSection(DiagnosticsContent, new TextBlock
        {
            Text = "Tools that prove the take machinery works, rather than check how you sound. "
                 + "None of them keys the radio. The diagnostic take records whatever you transmit "
                 + "while it runs; to check how you sound, use Check your audio.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 6),
        });

        _benchStatus = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2), MinLines = 2,
            Text = Lexicon.Get("audio.diagnostics.bench_idle") };
        AutomationProperties.SetName(_benchStatus, Lexicon.Get("audio.iq.status_label"));
        JJFlexHelp.SetText(_benchStatus, Lexicon.Get("audio.diagnostics.help.bench_status"));
        AddToSection(DiagnosticsContent, _benchStatus);

        _benchRate = new ComboBox { ItemsSource = new[] { Lexicon.Get("audio.iq.rate24"), Lexicon.Get("audio.iq.rate48"), Lexicon.Get("audio.iq.rate96") }, SelectedIndex = 0, Margin = new Thickness(2) };
        AutomationProperties.SetName(_benchRate, Lexicon.Get("audio.iq.rate_label"));
        JJFlexHelp.SetText(_benchRate, Lexicon.Get("audio.iq.rate_help"));
        AddToSection(DiagnosticsContent, _benchRate);
        _benchSeparate = new CheckBox { Content = Lexicon.Get("audio.iq.separate_pan"), Margin = new Thickness(2) };
        AutomationProperties.SetName(_benchSeparate, Lexicon.Get("audio.iq.separate_pan"));
        JJFlexHelp.SetText(_benchSeparate, Lexicon.Get("audio.diagnostics.help.separate_pan"));
        _benchFdx = new CheckBox { Content = Lexicon.Get("audio.iq.full_duplex"), Margin = new Thickness(2) };
        AutomationProperties.SetName(_benchFdx, Lexicon.Get("audio.iq.full_duplex"));
        JJFlexHelp.SetText(_benchFdx, Lexicon.Get("audio.diagnostics.help.full_duplex"));
        _benchSeparate.Checked += (_, _) => { if (!_polling) ToggleFeedback.Announce("Separate pan", true); };
        _benchSeparate.Unchecked += (_, _) => { if (!_polling) ToggleFeedback.Announce("Separate pan", false); };
        _benchFdx.Checked += (_, _) => { if (!_polling) ToggleFeedback.Announce("Full duplex", true); };
        _benchFdx.Unchecked += (_, _) => { if (!_polling) ToggleFeedback.Announce("Full duplex", false); };
        AddToSection(DiagnosticsContent, _benchSeparate);
        AddToSection(DiagnosticsContent, _benchFdx);
        _benchAntenna = new ComboBox { Margin = new Thickness(2) };
        AutomationProperties.SetName(_benchAntenna, Lexicon.Get("audio.iq.antenna_label"));
        JJFlexHelp.SetText(_benchAntenna, Lexicon.Get("audio.iq.antenna_help"));
        AddToSection(DiagnosticsContent, _benchAntenna);

        // Short labels, and Ctrl+F1 says what each does (ruled by Noel
        // 2026-10-28 20:48: "shorten them I'd say and make sure ctrl+f1
        // explains them"). The ten seconds, the never-keys promise and the
        // prove-it-is-real purpose moved from the labels into the explanations.
        // JJFlexHelp.Text, never HelpText, which NVDA reads on every focus (#91).
        _benchDiagnostic = BenchButton("diagnostic_start", "audio.diagnostics.help.diagnostic_take", () => _ = ToggleDiagnosticAsync());
        _benchPreflight = BenchButton("preflight_button", "audio.diagnostics.help.capacity", () => _ = PreflightAsync());
        BenchButton("detune_plus", "audio.diagnostics.help.detune_plus", () => _ = PlayDetunedAsync(1000));
        BenchButton("detune_minus", "audio.diagnostics.help.detune_minus", () => _ = PlayDetunedAsync(-1000));
        _benchRecovery = BenchButton("recovery_button", "audio.diagnostics.help.recovery", () => _ = RestoreBenchAsync());
        _benchRecovery.Visibility = Visibility.Collapsed;

        // The one sentence that stands in for the section over SmartLink.
        // Straight onto the page, not into the section that it replaces.
        _benchUnavailable = new TextBlock
        {
            Text = Lexicon.Get("audio.diagnostics.bench_smartlink"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 8, 2, 6),
            Visibility = Visibility.Collapsed,
            Focusable = true,
        };
        AutomationProperties.SetName(_benchUnavailable, _benchUnavailable.Text);
        JJFlexHelp.SetText(_benchUnavailable, Lexicon.Get("audio.diagnostics.help.bench_smartlink"));
        DiagnosticsContent.Children.Add(_benchUnavailable);

        _benchWatch.Tick += async (_, _) =>
        {
            if (_benchRun != null && _benchRun.Capture.Ended.IsCompleted && !_benchBusy)
                await FinishDiagnosticAsync("stream-ended");
        };
        UpdateBenchToolsAvailability();
    }

    /// <param name="key">The label's key under <c>audio.iq.</c>.</param>
    /// <param name="helpKey">Its Ctrl+F1 explanation's lexicon key, in full.</param>
    private Button BenchButton(string key, string helpKey, Action click)
    {
        string fullKey = "audio.iq." + key;
        string name = Lexicon.Get(fullKey);
        var button = new Button { Content = name, Margin = new Thickness(2), Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(button, name);
        JJFlexHelp.SetText(button, Lexicon.Get(helpKey));
        button.Click += (_, _) => click();
        AddToSection(DiagnosticsContent, button);
        return button;
    }

    /// <summary>Show the bench tools on local and VPN connections, the one sentence over SmartLink, and the receive antennas this radio lists.</summary>
    private void UpdateBenchToolsAvailability()
    {
        if (_benchSection == null) return;
        bool remote = _rig?.RemoteRig == true;
        if (remote && _benchSection.IsKeyboardFocusWithin) _benchUnavailable?.Focus();
        _benchSection.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
        if (_benchUnavailable != null) _benchUnavailable.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        if (_benchRecovery != null)
            _benchRecovery.Visibility = !remote && _rig?.PendingListenbackRecovery != null ? Visibility.Visible : Visibility.Collapsed;
        if (_benchAntenna == null || _benchRun != null || _benchBusy) return;
        var names = new[] { Lexicon.Get("audio.iq.antenna_unchanged") }.Concat(_rig?.ListenbackReceiveAntennas ?? Array.Empty<string>()).ToArray();
        if (!(_benchAntenna.ItemsSource is string[] old) || !old.SequenceEqual(names))
        { _benchAntenna.ItemsSource = names; _benchAntenna.SelectedIndex = 0; }
    }

    /// <summary>A radio is leaving or arriving: a running diagnostic take finishes on the radio it began on.</summary>
    private void BenchToolsRigChanging(FlexBase? next)
    {
        _benchCancellation?.Cancel();
        if (_benchRun != null) _ = FinishDiagnosticAsync("connection-changed");
        _benchRecoveryReader = null;
        if (_benchStatus != null)
            _benchStatus.Text = Lexicon.Get(next == null ? "audio.no_radio_connected" : "audio.diagnostics.bench_idle");
    }

    /// <summary>The take must not outlive the window: finish it, restore the radio, and say so after the close.</summary>
    private void CloseBenchTools()
    {
        _benchClosing = true;
        _benchCancellation?.Cancel();
        _benchWatch.Stop();
        if (!_benchBusy) _ = FinishDiagnosticAsync("dialog-closed");
    }

    private void BenchStatus(string text, bool failure = false)
    {
        if (_benchClosing)
        {
            if (failure) PostClosedBenchStatus(text, true);
            return;
        }
        if (_benchStatus != null) _benchStatus.Text = text;
        if (failure)
        {
            EarconPlayer.LeaderInvalidTone();
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (_benchClosing) PostClosedBenchStatus(text, true);
                else ScreenReaderOutput.Speak(text, VerbosityLevel.Critical, interrupt: true);
            }));
        }
        else ScreenReaderOutput.Speak(text, VerbosityLevel.Terse);
    }

    private void PostClosedBenchStatus(string text, bool failure) =>
        ListenbackTakes.PostClosedNotice(Dispatcher, _benchRecoveryReader ?? _rig?.CaptureListenbackRecoveryReader(), text, failure);

    private void AnnounceClosedBenchRecovery(bool restored) => PostClosedBenchStatus(
        Lexicon.Get(restored ? "audio.iq.restored" : "audio.iq.no_recovery"), !restored);

    private void BenchFailure(Exception ex)
    {
        ScreenReaderOutput.Supersede("audio.iq.preparing", "take preparation failed");
        JJTrace.Tracing.TraceLine("Listenback bench: " + ex, System.Diagnostics.TraceLevel.Error);
        BenchStatus(ListenbackTakes.FailureText(ex), true);
    }

    private void DiagnosticLabel(string key)
    {
        if (_benchDiagnostic == null) return;
        string fullKey = "audio.iq." + key;
        string name = Lexicon.Get(fullKey);
        _benchDiagnostic.Content = name;
        AutomationProperties.SetName(_benchDiagnostic, name);
    }

    private async Task ToggleDiagnosticAsync()
    {
        if (CheckYourAudioDialog.ACheckIsStartingOrActive) { BenchStatus(Lexicon.Get("audio.iq.stop_other"), true); return; }
        if (_benchBusy) { _benchCancellation?.Cancel(); return; }
        if (_benchRun != null) { await FinishDiagnosticAsync("operator-stop"); return; }
        if (_rig == null) { BenchStatus(Lexicon.Get("audio.iq.stop_other"), true); return; }
        var rig = _rig;
        _benchBusy = true;
        _benchRecoveryReader = rig.CaptureListenbackRecoveryReader();
        _benchCancellation = new CancellationTokenSource();
        DiagnosticLabel("cancel_start");
        BenchStatus(Lexicon.Get("audio.iq.preparing"));
        try
        {
            int rate = new[] { 24000, 48000, 96000 }[Math.Clamp(_benchRate?.SelectedIndex ?? 0, 0, 2)];
            string? antenna = _benchAntenna?.SelectedIndex > 0 ? _benchAntenna.SelectedItem as string : null;
            _benchRun = await rig.StartListenbackAsync(new(rate, _benchSeparate?.IsChecked == true,
                _benchFdx?.IsChecked == true, antenna), _benchCancellation.Token);
            _benchRunRig = rig;
            if (_benchClosing || _benchCancellation.IsCancellationRequested || !ReferenceEquals(rig, _rig))
            {
                await _benchRun.StopAsync("cancelled");
                if (_benchClosing) AnnounceClosedBenchRecovery(_benchRun.Restored);
                else if (ReferenceEquals(rig, _rig)) BenchStatus(Lexicon.Get(_benchRun.Restored ? "audio.iq.restored" : "audio.iq.recovery_pending"), !_benchRun.Restored);
                _benchRun = null; return;
            }
            DiagnosticLabel("diagnostic_stop");
            EarconPlayer.FeatureOnTone();
            BenchStatus(Lexicon.Get("audio.iq.recording_diagnostic"));
            _benchWatch.Start();
        }
        catch (Exception ex) { DiagnosticLabel("diagnostic_start"); if (ReferenceEquals(rig, _rig)) BenchFailure(ex); }
        finally { _benchBusy = false; _benchCancellation.Dispose(); _benchCancellation = null; }
    }

    private async Task FinishDiagnosticAsync(string reason)
    {
        ScreenReaderOutput.Supersede("audio.iq.preparing", "take finished");
        if (_benchBusy || _benchRun == null) return;
        _benchBusy = true;
        _benchWatch.Stop();
        var run = _benchRun;
        var runRig = _benchRunRig;
        EarconPlayer.FeatureOffTone();
        BenchStatus(Lexicon.Get("audio.iq.finishing"));
        try
        {
            await run.StopAsync(reason);
            if (!ReferenceEquals(runRig, _rig)) return;
            if (_benchClosing) { AnnounceClosedBenchRecovery(run.Restored); return; }
            var take = TakeReader.Inspect(run.DirectoryPath);
            if (!run.Restored) BenchStatus(Lexicon.Get("audio.iq.recovery_pending"), true);
            else if (!take.Playable) BenchStatus(Lexicon.Get("audio.iq.corrupt", ("check", take.Reason)), true);
            else
            {
                TakeAnalysisResult? measured = null;
                if (take.Get("mode") is ("USB" or "LSB"))
                {
                    await Task.Run(() => SsbTakeRenderer.Render(run.DirectoryPath));
                    measured = await Task.Run(() => TakeAnalysis.Run(run.DirectoryPath));
                }
                string result = Lexicon.Get("audio.iq.diagnostic_result", ("state", ListenbackTakes.StateText(take.State)),
                    ("seconds", ListenbackTakes.Seconds(take)),
                    ("antenna", take.Get("receive-antenna")), ("gain", take.Get("rf-gain")),
                    ("ratio", ListenbackResultText.Ratio(take.Get("pairs-per-arrival-span"))), ("gaps", take.Get("gaps") ?? "0"),
                    ("classifier", take.Get("classifier") ?? Lexicon.Get("audio.iq.unknown"))) +
                    (take.Get("startup-signature") == "detected" ? " " + Lexicon.Get("audio.iq.startup_signature") : "");
                BenchStatus(result);
                // The measurements follow in the same box, after the spoken
                // result: details, not something to keep the operator waiting on.
                var stats = await Task.Run(() => TakeStatistics.Measure(run.DirectoryPath, reuseRender: true));
                if (_benchClosing || _benchStatus == null) return;
                string details = ListenbackTakes.FormatStatistics("This take", stats);
                if (measured != null) details += Environment.NewLine + "Take analysis: " + ListenbackTakes.FormatAnalysisDetails(measured, take);
                _benchStatus.Text = result + Environment.NewLine + details;
            }
        }
        catch (Exception ex) { if (ReferenceEquals(runRig, _rig)) BenchFailure(ex); }
        finally { _benchRun = null; _benchRunRig = null; _benchBusy = false; DiagnosticLabel("diagnostic_start"); UpdateBenchToolsAvailability(); }
    }

    private async Task PreflightAsync()
    {
        if (_benchBusy) { _benchCancellation?.Cancel(); return; }
        var rig = _rig;
        if (rig == null || CheckYourAudioDialog.ACheckIsStartingOrActive || rig.RemoteRig || _benchRun != null)
        { BenchStatus(Lexicon.Get("audio.iq.stop_other"), true); return; }
        _benchBusy = true; _benchRecoveryReader = rig.CaptureListenbackRecoveryReader(); _benchCancellation = new CancellationTokenSource();
        BenchStatus(Lexicon.Get("audio.iq.preflight_running"));
        try
        {
            bool clean = await rig.RunListenbackPreflightAsync(_benchCancellation.Token);
            if (ReferenceEquals(rig, _rig)) BenchStatus(Lexicon.Get(clean ? "audio.iq.preflight_clean" : "audio.iq.preflight_loss"), !clean);
        }
        catch (Exception ex) { if (ReferenceEquals(rig, _rig)) BenchFailure(ex); }
        finally { _benchBusy = false; _benchCancellation.Dispose(); _benchCancellation = null; }
    }

    /// <summary>"Prove it is real": play the newest take with the tuning shifted, and listen for the pitch to move.</summary>
    private async Task PlayDetunedAsync(int detune)
    {
        if (RecordingPlayer.IsPlaying) { RecordingPlayer.Stop(); return; }
        if (_benchBusy || _benchRun != null || CheckYourAudioDialog.AnAudioCheckIsActive || _rig?.Transmit == true) return;
        string? directory = ListenbackTakes.NewestDirectory();
        if (directory == null) { BenchStatus(Lexicon.Get("audio.iq.no_take")); return; }
        _benchBusy = true;
        try
        {
            string? trouble = await ListenbackTakes.RenderAndPlayAsync(directory, detune, _rig,
                rig => !_benchClosing && ReferenceEquals(rig, _rig));
            if (trouble != null) BenchStatus(trouble, true);
        }
        catch (Exception ex) { BenchFailure(ex); }
        finally { _benchBusy = false; }
    }

    private async Task RestoreBenchAsync()
    {
        if (_rig == null || _benchBusy || _benchRun != null || CheckYourAudioDialog.ACheckIsStartingOrActive) return;
        try
        {
            _benchBusy = true;
            var result = await ListenbackRecoveryPrompt.ReviewAsync(_rig);
            if (result is { } notice) BenchStatus(Lexicon.Get(notice.Key), notice.Failure);
        }
        catch (Exception ex) { BenchFailure(ex); }
        finally { _benchBusy = false; UpdateBenchToolsAvailability(); }
    }
}
