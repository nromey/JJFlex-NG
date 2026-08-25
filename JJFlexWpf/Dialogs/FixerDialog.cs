using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using JJTrace;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Radios;
using Radios.ChainChecks;
using Radios.Fixer;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The JJ Flexible Fixer Tool: the host shell around the run engine and the
/// browsable page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Web content in a WebView2, and the reason is browse mode.</b> A screen
/// reader gives single-letter navigation — H between stages, B between buttons
/// — only in a document, never in a WPF dialog. Explanation text then costs
/// zero tab stops while staying fully readable, which a dialog cannot manage at
/// any length. That is the whole justification for the extra machinery here.
/// </para>
/// <para>
/// <b>This class owns the transmit boundary and the way out.</b> The engine
/// runs stages and the page renders them; neither can key a radio. Every
/// transmit goes through <see cref="FixerTransmitGate"/> by way of
/// <see cref="FixerTransmitBoundary"/>, and every stop goes through
/// <see cref="FixerAbort"/>.
/// </para>
/// <para>
/// <b>Escape is asymmetric, and it inverts the usual rule.</b> Keyed: drop the
/// carrier immediately, no confirmation, THEN ask whether to abandon. Unkeyed:
/// offer to stop. While transmitting the dangerous thing is the delay, not the
/// action — a prompt between the operator and stopping their own transmission
/// keeps RF going out while something waits for an answer.
/// </para>
/// </remarks>
public sealed class FixerDialog : JJFlexDialog
{
    /// <summary>
    /// How long to let a stage run before giving up on it. Generous: the tone
    /// ladder legitimately keys for many seconds, and a timeout that fires
    /// during honest work would abandon a measurement mid-carrier.
    /// </summary>
    private const int StageTimeoutMs = 120_000;

    private readonly WebView2 _web = new();
    private readonly FixerTransmitGate _gate;
    private readonly FixerRun _run;
    private readonly FixerPageState _state = new();
    private readonly Func<FlexBase?> _radio;

    private readonly System.Collections.Generic.Dictionary<string, string> _declarations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Generic.Dictionary<string, string> _notices =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _stageCancel;
    private bool _ready;
    private bool _initFailed;
    private bool _closing;

    /// <summary>True when this dialog believes a stage is running right now.</summary>
    private bool RunInProgress => _stageCancel != null;

    private FixerDialog(Func<FlexBase?> radio)
    {
        _radio = radio;
        _gate = new FixerTransmitGate();

        var hosts = new TransmitStageSet.Hosts
        {
            // WIRED. The transmit boundary, consulting the gate before anything
            // keys. This is the stage Don needs today.
            ProbeTransmitter = FixerTransmitBoundary.ProbeTransmitter(
                _gate,
                () => _radio() ?? null!,
                TransmitStageSet.TransmitterCheck),

            // WIRED. What the operator said the antenna socket is connected to.
            // Read from the gate rather than from our own copy, so the value in
            // the report and the value that opened the gate cannot differ.
            ReadLoadDeclaration = () => _gate.LoadDeclaration,

            // WIRED. What the audio system is ACTUALLY running, read live —
            // never from the configuration file, because the whole value of
            // this stage is catching the case where the two differ.
            //
            // Wrapped rather than used directly: three of its facts are about
            // the RADIO SESSION and cannot be read without a FlexBase, which
            // FixerHostWiring is structurally forbidden to touch. It leaves
            // them at their defaults and says so; the host, which does hold the
            // radio, fills them in here.
            ReadAudioSetup = WithRadioFacts(FixerHostWiring.AudioSetup()),

            // WIRED. Reuses the existing microphone probe rather than
            // measuring again.
            MeasureMicrophone = FixerHostWiring.Microphone(),

            // STILL NOT WIRED. Both need the injection pipeline, and a
            // stand-in would let a stage report a measurement nothing made.
            // Null is the engine's honest signal: it records the stage as
            // unable to run and says so in the report.
            //
            //   RunInjectedTransmit — needs the injection pipeline
            //   RunSpokenTransmit   — needs that plus a live microphone path
            //   the five fix actions
        };

        _run = new FixerRun(TransmitStageSet.Build(hosts));
        _gate.BeginRun(_run.RunId);

        Title = "JJ Flexible Fixer Tool";
        Width = 780;
        Height = 640;
        ResizeMode = ResizeMode.CanResize;

        AutomationProperties.SetName(_web, "Fixer Tool");
        Content = _web;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>
    /// Fill in the three audio-setup facts that need a radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FixerHostWiring</c> reads the audio system and nothing else — it
    /// never touches a <c>FlexBase</c>, which is what keeps it testable and
    /// keeps stages 0 and 1 genuinely RF-silent. But three of the facts stage 0
    /// reports are about the radio SESSION: whether PC audio is on, whether the
    /// radio is remote, and whether the microphone profile is empty.
    /// </para>
    /// <para>
    /// <b>Leaving them at their defaults is not harmless.</b> The defaults
    /// cannot raise a false finding — a local radio suppresses the PC-audio
    /// finding, and a non-empty profile raises nothing — but the EVIDENCE lines
    /// still print, saying "PC audio: off" and "Microphone profile: has
    /// settings" as though those had been read. That is a measurement nothing
    /// made, in a document written to be shown to FlexRadio.
    /// </para>
    /// <para>
    /// It also matters for the operator this was built for: Don's radio is
    /// REMOTE, and an empty microphone profile is the confirmed cause of a
    /// silent transmitter. Those are exactly the two findings the defaults
    /// suppress.
    /// </para>
    /// <para>
    /// Each read is guarded on its own. One unreadable fact must not cost the
    /// other eleven the wiring already gathered.
    /// </para>
    /// </remarks>
    private Func<AudioSetupFacts> WithRadioFacts(Func<AudioSetupFacts> inner)
    {
        if (inner == null) return null;

        return () =>
        {
            AudioSetupFacts f = inner();
            if (f == null) return null;

            FlexBase? rig;
            try { rig = _radio(); } catch { rig = null; }
            if (rig == null) return f;   // no radio: the defaults are honest

            try { f.RemoteRadio = rig.RemoteRig; }
            catch (Exception ex) { Note("RemoteRig", ex); }

            try { f.PcAudioOn = rig.PCAudio; }
            catch (Exception ex) { Note("PCAudio", ex); }

            try { f.MicProfileEmpty = rig.MicProfileSelectionEmpty; }
            catch (Exception ex) { Note("MicProfileSelectionEmpty", ex); }

            return f;
        };

        static void Note(string what, Exception ex) =>
            Tracing.TraceLine("FixerDialog: could not read " + what + " — "
                              + ex.Message, TraceLevel.Warning);
    }

    /// <summary>
    /// The page is a document and must be focused as one. The base class would
    /// focus the first control, which here is the WebView container — a screen
    /// reader then announces an embedded object rather than starting to read.
    /// Focus is handed to the document in <see cref="OnNavigationCompleted"/>
    /// instead, once there is a document to hand it to.
    /// </summary>
    protected override void FocusFirstControl()
    {
    }

    // ---------------- opening ----------------

    /// <summary>
    /// Open the Fixer Tool. Falls back to plain advice when WebView2 is absent:
    /// an operator whose transmit is broken must not also be told their browser
    /// runtime is missing and left with nothing.
    /// </summary>
    public static void Show(Func<FlexBase?> radio, Window? owner = null)
    {
        if (!HtmlInfoDialog.IsAvailable)
        {
            AdvisoryDialog.Show("JJ Flexible Fixer Tool",
                "The Fixer Tool needs the Microsoft Edge WebView2 runtime, which is not "
                + "installed on this computer. Everything it would have checked can still "
                + "be reached from the Audio Workshop and from Diagnostics.");
            return;
        }

        var dialog = new FixerDialog(radio);
        if (owner != null) dialog.Owner = owner;
        dialog.ShowModalDialog();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Shares the app's one WebView2 user-data folder, so every document
            // view runs in a single browser process.
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JJFlexRadio", "WebView2");

            CoreWebView2Environment env =
                await CoreWebView2Environment.CreateAsync(null, userData, null);
            await _web.EnsureCoreWebView2Async(env);

            CoreWebView2Settings s = _web.CoreWebView2.Settings;
            s.AreDevToolsEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;

            _web.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.NavigationCompleted += OnNavigationCompleted;

            // Registered on document creation, before any content exists, so
            // there is no window in which Escape is dead.
            await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(EscapeScript);

            Render();
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: WebView2 init failed — " + ex.Message,
                              TraceLevel.Error);
            _initFailed = true;

            // Never a bare DialogResult assignment from a Loaded path — it
            // throws for any caller that did not use ShowDialog, and this
            // handler is async, so it would surface as an unhandled dispatcher
            // exception rather than a failure to open.
            if (DialogResult == null) CloseWithResult(false);
            else Close();
        }
    }

    /// <summary>
    /// Escape inside the document never reaches WPF — the WebView2 island keeps
    /// it — so the page posts it out. Capture phase, so a control that would
    /// swallow it does not get the chance.
    /// </summary>
    private const string EscapeScript = @"
document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
        window.chrome.webview.postMessage(JSON.stringify({kind:'stop', source:'escape'}));
    }
}, true);";

    private static void OnNavigationStarting(object? sender,
                                             CoreWebView2NavigationStartingEventArgs e)
    {
        // Only the document we generated renders in here. A link outward opens
        // in the operator's own browser, where their screen reader is set up the
        // way they like it and there is a back button.
        if (e.Uri == null) return;
        if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
        if (e.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return;

        e.Cancel = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: could not open " + e.Uri + " — " + ex.Message,
                              TraceLevel.Warning);
        }
    }

    private async void OnNavigationCompleted(object? sender,
                                             CoreWebView2NavigationCompletedEventArgs e)
    {
        _ready = true;
        try
        {
            _web.Focus();
            await _web.CoreWebView2.ExecuteScriptAsync(FocusScript(_state.SelectedStageId));
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: focus handoff failed — " + ex.Message,
                              TraceLevel.Warning);
        }
    }

    /// <summary>
    /// Put the caret where the operator was, not back at the top.
    /// </summary>
    /// <remarks>
    /// A full re-render after a stage finishes would otherwise dump them at the
    /// document head and make them navigate back to the stage they just ran —
    /// every time. Focus goes to the selected stage's panel when there is one,
    /// and only falls back to the first heading on the very first render.
    /// </remarks>
    private static string FocusScript(string? stageId)
    {
        string id = JsonEncode(stageId ?? "");
        return @"
(function () {
    var target = null;
    var want = " + id + @";
    if (want) target = document.getElementById('panel-' + want);
    if (!target) target = document.querySelector('h1') || document.body;
    if (!target) return;
    target.setAttribute('tabindex', '-1');
    target.focus();
})();";
    }

    // ---------------- the page talks to us ----------------

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }   // not a string — nothing we sent

        FixerPageMessage m = FixerPageMessage.Parse(raw);
        if (!m.Usable)
        {
            // Traced, never silently dropped: a message quietly discarded is a
            // page bug that never gets found, on the surface that exists to
            // find bugs.
            Tracing.TraceLine("FixerDialog: ignored " + m.FaultDescription()
                              + " from the page", TraceLevel.Warning);
            return;
        }

        try { Handle(m); }
        catch (Exception ex)
        {
            // The tool only opens when something is already wrong. Falling over
            // here would take the diagnosis away at the moment it was wanted.
            Tracing.TraceLine("FixerDialog: handling " + m.What + " threw — " + ex.Message,
                              TraceLevel.Error);
            Notice(m.StageId, "Something went wrong handling that. Nothing was transmitted.");
        }
    }

    private void Handle(FixerPageMessage m)
    {
        switch (m.What)
        {
            case FixerPageMessage.Kind.Ready:
                return;

            case FixerPageMessage.Kind.DeclareLoad:
                // Recorded ONCE, by the gate. Our copy is only for redrawing the
                // page; the gate's copy is the one that decides anything.
                _gate.DeclareLoad(m.Value);
                _declarations[TransmitStageSet.LoadDeclaration] = m.Value;
                Tracing.TraceLine("FixerDialog: load declared as \"" + m.Value + "\"",
                                  TraceLevel.Info);
                Render();
                return;

            case FixerPageMessage.Kind.RunStage:
                RunStage(m.StageId, again: false);
                return;

            case FixerPageMessage.Kind.RunStageAgain:
                // The operator asked, deliberately and separately, so the gate's
                // once-per-stage flag is cleared. A double-fire never arrives
                // this way — that is the entire point of it being its own kind.
                _gate.AllowReRun(m.StageId);
                RunStage(m.StageId, again: true);
                return;

            case FixerPageMessage.Kind.SkipStage:
                _run.SkipStage(m.StageId, m.Value);
                _state.SelectedStageId = m.StageId;
                Render();
                return;

            case FixerPageMessage.Kind.ApplyFix:
                // The wire carries the FINDING id, which is what ApplyFix takes.
                _run.ApplyFix(m.StageId, m.Value);
                _state.SelectedStageId = m.StageId;
                Render();
                return;

            case FixerPageMessage.Kind.Stop:
                Stop(FixerAbort.SourceFrom(m.Value));
                return;

            case FixerPageMessage.Kind.CopyReport:
                CopyReport();
                return;

            case FixerPageMessage.Kind.OpenHelp:
                OpenHelp(m.Value);
                return;

            case FixerPageMessage.Kind.OpenDevicePicker:
                // The picker belongs to AudioDevicesDialog. The page asks; it
                // does not grow one of its own.
                Notice("", "Opening the audio device list.");
                OpenDevicePicker();
                return;
        }
    }

    // ---------------- running a stage ----------------

    private void RunStage(string stageId, bool again)
    {
        if (RunInProgress)
        {
            Notice(stageId, "Something is already running. Wait for it to finish, or press Stop.");
            return;
        }

        _state.SelectedStageId = stageId;
        _stageCancel = new CancellationTokenSource(StageTimeoutMs);

        try
        {
            // Synchronous on the UI thread, deliberately, for now: the stages
            // that key are short and bounded, and an operator who cannot press
            // Stop because we are busy is the one outcome that must not happen.
            // If a stage ever grows long enough for that to bite, the fix is to
            // move it to a worker — NOT to lengthen the timeout.
            FixerStageResult r = _run.RunStage(stageId, _stageCancel.Token);

            Tracing.TraceLine("FixerDialog: stage " + stageId + " -> " + r.Status
                              + (again ? " (re-run)" : ""), TraceLevel.Info);

            AnnounceCriticals(r);
        }
        finally
        {
            _stageCancel?.Dispose();
            _stageCancel = null;

            // Belt and braces. The boundary already unkeys in a finally and the
            // gate's NoteUnkeyed is safe unmatched, so this cannot double-count
            // — and if a path ever escaped without unkeying, the accounting
            // would otherwise stay wrong for the rest of the run.
            _gate.NoteUnkeyed();
        }

        Render();
    }

    /// <summary>
    /// Critical findings get the page's assertive region AND an app-side
    /// earcon.
    /// </summary>
    /// <remarks>
    /// Both, until pressing the keys proves the live region fires reliably
    /// under NVDA and JAWS. A blind operator has no other signal that something
    /// urgent appeared, and "the specification says it announces" is not the
    /// same claim as "it announced."
    /// </remarks>
    private void AnnounceCriticals(FixerStageResult r)
    {
        if (r?.Findings == null) return;

        foreach (FixerFinding f in r.Findings)
        {
            if (!f.Critical) continue;

            try { EarconPlayer.WarningAlarmTone(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerDialog: warning earcon failed — " + ex.Message,
                                  TraceLevel.Warning);
            }

            ToPage("critical", f.WhatIsWrong);
            return;   // one alarm per stage; a queue of them conveys nothing
        }
    }

    // ---------------- stopping ----------------

    private void Stop(FixerAbort.Source source)
    {
        bool keyed = _gate.InFlight || RigIsKeyed();
        FixerAbort.Plan plan = FixerAbort.Decide(keyed, source, RunInProgress);

        Tracing.TraceLine("FixerDialog: stop from " + source + ", keyed=" + keyed
                          + ", run=" + RunInProgress, TraceLevel.Info);

        foreach (FixerAbort.Step step in plan.Steps)
        {
            switch (step)
            {
                case FixerAbort.Step.UnkeyImmediately:
                    // FIRST, always, before anything is announced or asked.
                    UnkeyNow();
                    break;

                case FixerAbort.Step.AskAbandonOrContinue:
                    if (plan.Announcement.Length > 0) ToPage("status", plan.Announcement);
                    if (AskAbandon(plan.Announcement)) Abandon();
                    return;

                case FixerAbort.Step.AbandonNow:
                    Abandon();
                    return;
            }
        }

        if (plan.Announcement.Length > 0) ToPage("status", plan.Announcement);
    }

    /// <summary>
    /// Drop the carrier by every route we have, and never throw.
    /// </summary>
    /// <remarks>
    /// Cancelling the stage is what normally stops it, because the runner
    /// unkeys in its own finally. The direct writes are the backstop for a
    /// runner that is wedged, and they are attempted independently: a
    /// transmitter left keyed is worse than any exception this could raise.
    /// </remarks>
    private void UnkeyNow()
    {
        try { _stageCancel?.Cancel(); } catch { /* stopping is not optional */ }

        FlexBase? rig = null;
        try { rig = _radio(); } catch { }

        if (rig != null)
        {
            try { rig.TxTune = false; } catch { }
            try { rig.Transmit = false; } catch { }
        }

        _gate.NoteUnkeyed();
    }

    private bool RigIsKeyed()
    {
        FlexBase? rig;
        try { rig = _radio(); } catch { return false; }
        if (rig == null) return false;

        // Unreadable is treated as keyed: refusing to believe the radio is idle
        // costs a redundant unkey, and the opposite costs a carrier nobody stops.
        try { return rig.Transmit || rig.TxTune; } catch { return true; }
    }

    private bool AskAbandon(string question)
    {
        string q = question.Length > 0 ? question : "Do you want to stop the test?";
        return MessageBox.Show(this, q, "JJ Flexible Fixer Tool",
                               MessageBoxButton.YesNo, MessageBoxImage.Question)
               == MessageBoxResult.Yes;
    }

    private void Abandon()
    {
        _gate.AbortRun();
        _closing = true;
        if (DialogResult == null) CloseWithResult(false);
        else Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing || _initFailed) return;

        bool keyed = _gate.InFlight || RigIsKeyed();
        FixerAbort.Plan plan = FixerAbort.Decide(keyed, FixerAbort.Source.WindowClosing,
                                                 RunInProgress);

        // Whatever else the plan says, if the carrier is up it comes down —
        // and it comes down here rather than after the window has gone.
        if (plan.UnkeysFirst) UnkeyNow();

        if (plan.Asks && !AskAbandon(plan.Announcement))
        {
            e.Cancel = true;
            return;
        }

        _gate.AbortRun();
    }

    /// <summary>
    /// Escape at the WPF level, for the moments focus is not inside the
    /// document.
    /// </summary>
    /// <remarks>
    /// The base class closes on Escape, which is right for every other dialog
    /// and wrong for this one while a transmitter is keyed. Routed through the
    /// same decision as the page's Escape so the two cannot disagree.
    /// </remarks>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Stop(FixerAbort.Source.HostChord);
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // ---------------- we talk to the page ----------------

    private void Render()
    {
        if (!_web.IsInitialized || _web.CoreWebView2 == null) return;

        _state.DeclarationAnswers = _declarations;
        _state.StageNotices = _notices;

        try
        {
            _web.CoreWebView2.NavigateToString(FixerPage.Render(_run, _state));
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: render failed — " + ex.Message, TraceLevel.Error);
        }
    }

    /// <summary>
    /// Record a notice against a stage. A refusal is NOT a result — nothing ran
    /// — so it lives here rather than in the run's records, and the page renders
    /// it beside that stage's run control.
    /// </summary>
    private void Notice(string stageId, string text)
    {
        _notices[stageId ?? ""] = text;
        if (_ready) ToPage("notice", text, stageId);
        else Render();
    }

    private async void ToPage(string kind, string text, string? stageId = null)
    {
        if (!_ready || _web.CoreWebView2 == null) return;

        string payload = "{\"kind\":" + JsonEncode(kind)
                       + ",\"text\":" + JsonEncode(text)
                       + ",\"stage\":" + JsonEncode(stageId ?? "") + "}";
        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync(
                "window.jjflex && window.jjflex.receive(" + JsonEncode(payload) + ")");
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: could not reach the page — " + ex.Message,
                              TraceLevel.Warning);
        }
    }

    private static string JsonEncode(string s) => JsonSerializer.Serialize(s ?? "");

    // ---------------- the things the host owns ----------------

    private void CopyReport()
    {
        // Plain text, because the destination is an email to FlexRadio. Nobody
        // should have to paste rendered HTML into a mail client and hope.
        try
        {
            Clipboard.SetText(FixerReport.PlainText(_run));
            ToPage("status", "The report is on the clipboard, as plain text, "
                           + "ready to paste into an email.");
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: clipboard failed — " + ex.Message,
                              TraceLevel.Warning);
            ToPage("status", "The report could not be copied. Windows refused the clipboard.");
        }
    }

    private void OpenHelp(string topic)
    {
        // HelpLauncher takes a CHM context name, not a path. The page's topics
        // read like "fixer/transmit/microphone-check"; the last segment is the
        // context, and a topic with no mapping opens the help front page rather
        // than nothing at all — a dead help link on the surface an operator
        // reaches when confused is worse than a slightly wrong page.
        string context = (topic ?? "").Trim();
        int slash = context.LastIndexOf('/');
        if (slash >= 0 && slash < context.Length - 1) context = context.Substring(slash + 1);

        try { HelpLauncher.ShowHelp(context.Length > 0 ? context : null); }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: help topic " + topic + " failed — " + ex.Message,
                              TraceLevel.Warning);
            ToPage("status", "That help page could not be opened.");
        }
    }

    private void OpenDevicePicker()
    {
        // The picker needs the audio-devices file it edits; it is not a
        // no-argument dialog. Resolved through the same settings root
        // everything else uses, so a test run under JJFLEX_CONFIG_DIR does not
        // reach past the isolation into the operator's live configuration.
        try
        {
            string path = System.IO.Path.Combine(
                Radios.RadioConfig.AppDataRoot, "audioDevices.xml");
            AudioDevicesDialog.ShowPicker(this, path);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: device picker failed — " + ex.Message,
                              TraceLevel.Warning);
            ToPage("status", "The audio device list could not be opened.");
        }
    }
}
