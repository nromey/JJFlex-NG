using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;
using Radios.Speech;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Audio Workshop, Diagnostics: the speed test for the receive noise engines
/// (Sprint 49 Track L, #866). One Start button and a read-only result box:
/// each engine, and each spectral then neural pairing, on its own line,
/// saying whether it keeps up on this computer and with how much room.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own section, beside Bench tools, not inside it</b> (ruled by Noel
/// 2026-11-19, X4). Bench tools is a radio section and is replaced by one
/// sentence over SmartLink; this test needs no radio and works over SmartLink,
/// so it is opened with <c>AddSectionHeader</c>, never <c>AddRadioSection</c>.
/// </para>
/// <para>
/// <b>Off the UI thread and off the receive audio thread.</b> It runs on a
/// thread of its own and competes with a radio's receive audio for the
/// processor while one is running; the result says so when that is the case.
/// It opens no audio device, plays nothing, and never keys a radio.
/// </para>
/// <para>
/// Every word here is a lexicon placeholder until the words round.
/// </para>
/// </remarks>
public partial class AudioWorkshopDialog
{
    private Button? _speedStart;
    private TextBox? _speedBox;
    private CancellationTokenSource? _speedCancel;
    private bool _speedRunning;

    private void BuildEngineSpeedSection()
    {
        AddSectionHeader(DiagnosticsContent, Lexicon.Get("audio.diagnostics.speed.section"));
        AddToSection(DiagnosticsContent, new TextBlock
        {
            Text = Lexicon.Get("audio.diagnostics.speed.intro"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 6),
        });

        string label = Lexicon.Get("audio.diagnostics.speed.start");
        _speedStart = new Button
        {
            Content = label,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 0, 2, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(_speedStart, label);
        // JJFlexHelp.Text, never HelpText, which NVDA reads on every focus.
        JJFlexHelp.SetText(_speedStart, Lexicon.Get("audio.diagnostics.speed.help.start"));
        _speedStart.Click += (_, _) => _ = RunEngineSpeedTestAsync();
        AddToSection(DiagnosticsContent, _speedStart);

        _speedBox = MakeReportBox(Lexicon.Get("audio.diagnostics.speed.result_name"), 4, 16);
        JJFlexHelp.SetText(_speedBox, Lexicon.Get("audio.diagnostics.speed.help.result"));
        _speedBox.Text = Lexicon.Get("audio.diagnostics.speed.idle");
        AddToSection(DiagnosticsContent, _speedBox);

        Closed += (_, _) => _speedCancel?.Cancel();
    }

    private static void SpeakSpeed(string text, SpeechIntent intent) =>
        ScreenReaderOutput.Speak(text, intent, VerbosityLevel.Terse, subject: SpeechSubject.EngineSpeedTest);

    private async Task RunEngineSpeedTestAsync()
    {
        if (_speedBox == null) return;
        if (_speedRunning)
        {
            SpeakSpeed(Lexicon.Get("audio.diagnostics.speed.busy"), SpeechIntent.Interrupt);
            return;
        }
        _speedRunning = true;
        var cancel = new CancellationTokenSource();
        _speedCancel = cancel;
        var text = new StringBuilder();
        void Line(string line)
        {
            text.AppendLine(line);
            if (_speedBox != null) _speedBox.Text = text.ToString();
        }
        string started = Lexicon.Get("audio.diagnostics.speed.started");
        Line(started);
        SpeakSpeed(started, SpeechIntent.Interrupt);
        // Read on the UI thread, before the test starts: whether the radio's
        // receive audio shares the processor with the test.
        bool competing = _rig?.ReceiveAudioRunning == true;
        var catalog = new DenoiseEngineCatalog();
        string Name(string id) => catalog.Find(id) is { } row ? Lexicon.Get(row.LabelKey) : id;
        try
        {
            // The helper's figures are read on the test's own thread: the store
            // may wait on another process's lock, and the running helper's
            // handshake is read under the host's lock. Neither starts a helper.
            await Task.Factory.StartNew(() => EngineSpeedTest.Run(catalog, HelperSpeedFigures.Current(), r =>
            {
                string words = EngineSpeedTest.Words(r, Name);
                System.Diagnostics.Trace.WriteLine("AudioWorkshop speed test: " + string.Join(" then ", r.EngineIds)
                    + (r.Model != null ? " model " + r.Model : "") + ": " + r.Verdict
                    + (r.Figures is { } f ? FormattableString.Invariant($", mean {f.MeanMs:F3} ms, 99th percentile {f.P99Ms:F3} ms, worst {f.WorstMs:F3} ms over {f.Frames} packets") : "")
                    + (r.Detail != null ? ", " + r.Detail : ""));
                Dispatcher.BeginInvoke(() =>
                {
                    if (cancel.IsCancellationRequested) return;
                    Line(words);
                    SpeakSpeed(words, SpeechIntent.Queue);
                });
            }, cancel: cancel.Token), cancel.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (cancel.IsCancellationRequested) return;
            Line(Lexicon.Get("audio.diagnostics.speed.defaults_note"));
            Line(Lexicon.Get("audio.diagnostics.speed.helper_pairs_note"));
            if (competing) Line(Lexicon.Get("audio.diagnostics.speed.competes"));
            string finished = Lexicon.Get("audio.diagnostics.speed.finished");
            Line(finished);
            SpeakSpeed(finished, SpeechIntent.Queue);
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Trace.WriteLine("AudioWorkshop speed test: stopped, the window closed");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("AudioWorkshop speed test: failed: " + ex.Message);
            if (!cancel.IsCancellationRequested)
            {
                string failed = Lexicon.Get("audio.diagnostics.speed.test_failed");
                Line(failed);
                SpeakSpeed(failed, SpeechIntent.Queue);
            }
        }
        finally
        {
            _speedRunning = false;
            if (ReferenceEquals(_speedCancel, cancel)) _speedCancel = null;
            cancel.Dispose();
        }
    }
}
