using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

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
/// <b>How a run is said and shown</b> lives in <see cref="EngineSpeedSession"/>
/// (Track L2): every line heard, in the order it was made, "finished" last.
/// </para>
/// <para>
/// Every word here is a lexicon placeholder until the words round.
/// </para>
/// </remarks>
public partial class AudioWorkshopDialog
{
    private TextBox? _speedBox;
    private EngineSpeedSession? _speedSession;

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
        var start = new Button
        {
            Content = label,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 0, 2, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(start, label);
        // JJFlexHelp.Text, never HelpText, which NVDA reads on every focus.
        JJFlexHelp.SetText(start, Lexicon.Get("audio.diagnostics.speed.help.start"));
        AddToSection(DiagnosticsContent, start);

        _speedBox = MakeReportBox(Lexicon.Get("audio.diagnostics.speed.result_name"), 4, 16);
        JJFlexHelp.SetText(_speedBox, Lexicon.Get("audio.diagnostics.speed.help.result"));
        _speedBox.Text = Lexicon.Get("audio.diagnostics.speed.idle");
        AddToSection(DiagnosticsContent, _speedBox);

        // The catalog names the engines; the test reads the helper's figures on
        // its own thread (the store may wait on another process's lock, and the
        // running helper's handshake is read under the host's lock). Neither
        // starts a helper.
        var catalog = new DenoiseEngineCatalog();
        string Name(string id) => catalog.Find(id) is { } row ? Lexicon.Get(row.LabelKey) : id;
        var session = new EngineSpeedSession(Dispatcher,
            show: text => { if (_speedBox != null) _speedBox.Text = text; },
            competing: () => _rig?.ReceiveAudioRunning == true,
            test: (onResult, cancel) => EngineSpeedTest.Run(catalog, HelperSpeedFigures.Current(), onResult, cancel: cancel),
            words: r => EngineSpeedTest.Words(r, Name));
        _speedSession = session;
        start.Click += (_, _) => _ = session.Start();
        Closed += (_, _) => session.Stop();
    }
}
