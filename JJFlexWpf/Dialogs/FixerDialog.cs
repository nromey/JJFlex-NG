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

    // Which explanation disclosures the operator has opened or closed, as the
    // page reports them. Held here so a full re-render honours the choice
    // instead of springing the prose back open — the page itself is stateless.
    private readonly System.Collections.Generic.Dictionary<string, bool> _explainOpen =
        new(StringComparer.OrdinalIgnoreCase);

    // The operator's hearing affirmation (#243), fed into stage 0's facts on
    // its next run. Per-run like every declaration: a fresh dialog asks
    // afresh, because the station may have changed since.
    private HeardRadio _hearing = HeardRadio.NotAsked;

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

        // #236's middle option: every gate-keyed transmit arms the PTT safety
        // controller's LIVE reflected-power watch for its duration. The gate
        // still owns whether a transmit may START; the controller watches what
        // happens WHILE it is up — the half these checks never had. Resolved
        // per call through the same live path the Audio Workshop uses (the
        // controller is recreated on operator switch and must not be cached).
        // Whether the checks' keying should ride the controller entirely is
        // still Noel's open call; neither stack is weakened meanwhile.
        _gate.OnKeyed = () =>
        {
            try { AudioWorkshopDialog.PttControllerSource?.Invoke()
                      ?.BeginExternalTransmitWatch(); }
            catch (Exception ex)
            { Tracing.TraceLine("FixerDialog: could not arm the transmit watch — "
                                + ex.Message, TraceLevel.Warning); }
        };
        _gate.OnUnkeyed = () =>
        {
            try { AudioWorkshopDialog.PttControllerSource?.Invoke()
                      ?.EndExternalTransmitWatch(); }
            catch (Exception ex)
            { Tracing.TraceLine("FixerDialog: could not disarm the transmit watch — "
                                + ex.Message, TraceLevel.Warning); }
        };

        // The transmit-audio boundary — one instance, like the gate, because
        // stage 4 is read against stage 3's meter capture and somebody has to
        // hold it between the two runs. Everything it transmits goes through
        // the same gate as the transmitter probe.
        FixerTransmitAudioBoundary? audio = FixerTransmitAudioBoundary.Create(
            _gate,
            () => _radio() ?? null!,
            prepareVoice: PrepareReferenceVoice,
            pcMicrophone: DescribePcMicrophone,
            // The dialog's stage-timeout token, polled — the host delegate
            // signatures carry no CancellationToken, and this is the only
            // road the 120-second ceiling has into a keyed stage.
            stopRequested: StageStopRequested,
            // Spoken while the UI thread is blocked inside the stage, which
            // is exactly why it is speech: the page cannot render a notice
            // until the stage is over, and the moment to speak is now. Every
            // stage cue goes through Cue() — one helper, so a new stage
            // cannot grow its own wiring beside it (#255).
            speakNow: Cue("audio.fixer.speak_now"),
            speakDone: Cue("audio.fixer.speak_done"),
            // The transmit countdown (#261): tones, not speech, so the count
            // cannot be flushed by the spoken cue's interrupt. The sound is
            // Track G's; FixerCountdown is the seam.
            countdown: FixerCountdown.TransmitTone);

        var hosts = new TransmitStageSet.Hosts
        {
            // WIRED. The transmit boundary, consulting the gate before anything
            // keys. This is the stage Don needs today.
            //
            // With the same cues as the other keying stages (#255): stage 2
            // used to key in total silence while blocking the UI thread — the
            // speak pair was built for stage 4, the countdown for stages 3
            // and 4, and this stage got neither. The countdown is the warning
            // that RF is imminent (Noel's ruling for stage 3 applies with the
            // same force here: the operator does nothing, and the count is
            // the only cue before a live transmitter); the spoken pair says
            // it is a tune carrier and that nothing is wanted of them.
            ProbeTransmitter = FixerTransmitBoundary.ProbeTransmitter(
                _gate,
                () => _radio() ?? null!,
                TransmitStageSet.TransmitterCheck,
                speakNow: Cue("audio.fixer.tune_now"),
                speakDone: Cue("audio.fixer.speak_done"),
                countdown: FixerCountdown.TransmitTone,
                stopRequested: StageStopRequested),

            // WIRED. What the operator said the antenna socket is connected to.
            // Read from the gate rather than from our own copy, so the value in
            // the report and the value that opened the gate cannot differ. The
            // report form carries the remote provenance (#247), because a Flex
            // support reader weights a declaration made over a remote session
            // differently from one made in the room.
            ReadLoadDeclaration = () => _gate.LoadDeclarationForReport,

            // WIRED. The live facts for "what pressing Run will do" — tune
            // power, RF power, TX antenna port, remoteness (#250). Read fresh
            // at every render, because a re-render happens after every host
            // action and these move under it.
            ReadStation = ReadStationNow,

            // WIRED. What the audio system is ACTUALLY running, read live —
            // never from the configuration file, because the whole value of
            // this stage is catching the case where the two differ.
            //
            // Wrapped rather than used directly: three of its facts are about
            // the RADIO SESSION and cannot be read without a FlexBase, which
            // FixerHostWiring is structurally forbidden to touch. It leaves
            // them at their defaults and says so; the host, which does hold the
            // radio, fills them in here.
            ReadAudioSetup = WithHearing(WithRadioFacts(FixerHostWiring.AudioSetup())),

            // WIRED. Reuses the existing microphone probe rather than
            // measuring again — now with the count-in and the end signal
            // (#255, #261): the record countdown from Track G's earcon, the
            // spoken "listening" at the moment the speech window opens, and
            // "finished" when it closes, so nobody talks into silence. The
            // gate-derivation wrapper adds the one sentence #262 carves out:
            // the transmit noise gate's threshold, and the floor it came
            // from, stated as a fact.
            MeasureMicrophone = WithGateDerivation(FixerHostWiring.Microphone(
                new FixerHostWiring.MicCueHooks
                {
                    Countdown = FixerCountdown.RecordTone,
                    SpeakListenNow = Cue("audio.fixer.listen_now"),
                    SpeakListenDone = Cue("audio.fixer.listen_done"),
                })),

            // WIRED. The five fixes stage 0 offers at the point of detection.
            // Each applies its change and then READS IT BACK, reporting what
            // the setting became in the operator's words — never "done". A fix
            // that cannot be verified did not succeed, and one that changed
            // nothing says so rather than claiming credit.
            SwitchToWasapi = FixerFixActions.SwitchToWasapi(),
            UseSuggestedInput = FixerFixActions.UseSuggestedInput(),
            EnablePcAudio = FixerFixActions.EnablePcAudio(_radio),
            FillMicProfile = FixerFixActions.FillMicProfile(_radio),
            ReopenConfiguredAudio = FixerFixActions.ReopenConfiguredAudio(_radio),

            // WIRED. The two transmit-audio stages, through the shared
            // boundary: the injected probes ride the same injection pipeline
            // the Audio Workshop's test tone and reference recording use, and
            // the spoken check listens to the same SC_MIC meter — nothing
            // measured here was invented for the Fixer.
            //
            // One boundary instance serves both, because stage 4 is read
            // against stage 3's capture and somebody has to hold it between
            // runs. That is the whole point of the differential.
            RunInjectedTransmit = audio?.InjectedTransmit(TransmitStageSet.InjectedTransmit),
            RunSpokenTransmit = audio?.SpokenTransmit(TransmitStageSet.SpokenTransmit),

            // Every stage and every fix is now supplied. The engine's
            // null-means-could-not-run signal remains the honest fallback if
            // any of them ever goes missing again.
        };

        _run = new FixerRun(TransmitStageSet.Build(hosts));
        _gate.BeginRun(_run.RunId);

        // NAMED BY THE CHECK, NOT BY THE TOOL. Noel, 2026-08-25: the operator
        // came for a transmit check, not for a product noun, and the menu
        // already says it that way (Tools > Fix > Transmit problems). Derived
        // from the stage set so a future receive set reads "Receive checks"
        // with nothing to rename. "Fixer" stays as the internal name — same
        // split as jjflexible.exe over the JJFlexRadio internals.
        Title = _run.Set.Name + " checks — JJ Flexible";
        Width = 780;
        Height = 640;
        ResizeMode = ResizeMode.CanResize;

        AutomationProperties.SetName(_web, _run.Set.Name + " checks");
        Content = _web;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>
    /// One spoken cue for a blocking stage, from the lexicon, at Critical with
    /// interrupt — the register every stage cue uses.
    /// </summary>
    /// <remarks>
    /// The one shared helper #255 asked for. The speak pair was invented for
    /// stage 4, then re-wired by hand for stage 1, and stage 2 got nothing —
    /// the fifth instance in one session of "the machinery exists and the next
    /// author built beside it". A stage cue that is not a <c>Cue(key)</c> call
    /// is now visibly odd in a diff, which is the point.
    /// </remarks>
    private static Action Cue(string lexiconKey)
        => () => ScreenReaderOutput.Speak(Lexicon.Get(lexiconKey),
                                          VerbosityLevel.Critical, interrupt: true);

    /// <summary>
    /// Has this stage's cancellation fired? Polled by the boundaries — the
    /// host delegate signatures carry no CancellationToken, and this is the
    /// only road the 120-second ceiling has into a keyed stage. One method,
    /// shared by every boundary, so their answers cannot differ.
    /// </summary>
    private bool StageStopRequested()
    {
        CancellationTokenSource? c = _stageCancel;
        try { return c != null && c.IsCancellationRequested; }
        catch (ObjectDisposedException) { return true; }
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
    /// Fold the operator's hearing affirmation (#243) into stage 0's facts.
    /// Separate from the radio facts because it needs no radio — "no radio is
    /// connected" is one of its answers.
    /// </summary>
    private Func<AudioSetupFacts> WithHearing(Func<AudioSetupFacts> inner)
    {
        if (inner == null) return null;
        return () =>
        {
            AudioSetupFacts f = inner();
            if (f != null) f.OperatorHearsRadio = _hearing;
            return f;
        };
    }

    /// <summary>
    /// Append the one sentence #262 carves out of Sprint 36: the transmit
    /// noise gate's threshold is DERIVED — floor plus margin, from the same
    /// loudness profile machinery the microphone check reports — and until
    /// now the derivation was silent. Stated as a fact, not a verdict:
    /// whether the threshold is RIGHT is exactly what #262 exists to test.
    /// An operator who can see where the number came from can question it
    /// and notice it going wrong; one who cannot, cannot.
    /// </summary>
    private Func<MicCheckFacts>? WithGateDerivation(Func<MicCheckFacts>? inner)
    {
        if (inner == null) return null;
        return () =>
        {
            MicCheckFacts f = inner();
            if (f == null) return f!;
            string line = DescribeGateDerivation();
            if (line.Length > 0)
                f.Detail = (f.Detail.Length > 0 ? f.Detail + " " : "") + line;
            return f;
        };
    }

    private string DescribeGateDerivation()
    {
        try
        {
            FlexBase? rig = _radio();
            if (rig == null) return "";

            var gate = rig.TxConditioner.Gate;
            if (!gate.Enabled)
                return "The transmit noise gate is currently off, so no threshold applies.";

            float threshold = gate.ThresholdDb;
            var profile = rig.TxLoudnessProfile;
            bool derived = profile.IsValid
                           && profile.NoiseFloorLufs > JJPortaudio.LufsMeter.Floor;

            if (!derived)
                return "The transmit noise gate is holding its deliberately low default "
                     + "threshold of "
                     + threshold.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                     + " dB, because no transmitted speech has taught it your room's noise "
                     + "floor yet.";

            return "Your transmit noise gate's threshold is currently "
                 + threshold.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                 + " dB, derived from the noise floor measured in your own transmitted "
                 + "audio ("
                 + profile.NoiseFloorLufs.ToString("0.#",
                       System.Globalization.CultureInfo.InvariantCulture)
                 + " LUFS, plus a "
                 + TxAudioConditioning.ThresholdMarginDb.ToString("0.#",
                       System.Globalization.CultureInfo.InvariantCulture)
                 + " dB margin). Stated here so you can see where it came from; whether "
                 + "it is right for your room is not judged by this check.";
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: gate derivation could not be described — "
                              + ex.Message, TraceLevel.Warning);
            return "";
        }
    }

    /// <summary>
    /// The station as it stands right now, for the stage sentences that say
    /// what pressing Run will do. Each fact is guarded on its own — one
    /// unreadable value must not cost the sentence the others — and a fact
    /// that cannot be read is simply omitted, never guessed.
    /// </summary>
    private TransmitStageSet.StationNow? ReadStationNow()
    {
        FlexBase? rig;
        try { rig = _radio(); } catch { rig = null; }
        if (rig == null) return null;

        var s = new TransmitStageSet.StationNow();
        try { s.TunePowerWatts = rig.TunePower; } catch { }
        try { s.RfPowerWatts = rig.XmitPower; } catch { }
        try { s.AntennaPort = rig.TXAntennaName ?? ""; } catch { }
        try { s.RemoteRadio = rig.RemoteRig; } catch { }
        return s;
    }

    /// <summary>
    /// Make the reference voice ready to transmit: whatever recording the
    /// operator already loaded (their personal baseline, from the Audio
    /// Workshop) if one is in the player, else the shipped reference. Returns
    /// empty when ready, otherwise why not — the boundary turns that into an
    /// honestly Unavailable voice probe rather than a stage failure.
    /// </summary>
    private static string PrepareReferenceVoice(FlexBase rig)
    {
        try
        {
            if (rig.TxFilePlayer.HasContent) return "";
            if (!ReferenceVoice.IsInstalled)
                return "no reference recording is installed on this computer";
            return TxAudioFile.TryLoadInto(rig, ReferenceVoice.FilePath, out _, out string trouble)
                ? "" : trouble;
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: reference voice preparation failed — " + ex.Message,
                              TraceLevel.Warning);
            return "the reference recording could not be prepared: " + ex.Message;
        }
    }

    /// <summary>
    /// The PC microphone the transmit stream captures from, for the spoken
    /// stage's evidence — resolved through the SAME resolver the microphone
    /// check (stage 1) uses, so the two stages that are read against each
    /// other name the same device the same way.
    /// </summary>
    private static (string device, string hostApi) DescribePcMicrophone()
    {
        try
        {
            JJPortaudio.Devices.DeviceInfo? row = RecordingNarrator.ResolveMicrophone(out _);
            return (row?.Name ?? "", row?.HostApiName ?? "");
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: microphone description failed — " + ex.Message,
                              TraceLevel.Warning);
            return ("", "");
        }
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
            AdvisoryDialog.Show("Transmit checks — JJ Flexible",
                "The transmit checks need the Microsoft Edge WebView2 runtime, which is "
                + "not installed on this computer. Everything they would have checked can "
                + "still be reached from the Audio Workshop and from Diagnostics.");
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
    /// Put the caret where the operator should be after a re-render.
    /// </summary>
    /// <remarks>
    /// A full re-render would otherwise dump them at the document head and
    /// make them navigate back — every time. Focus goes to the current
    /// stage's HEADING, never a Run button: the heading announces name and
    /// state ("Stage 2: Transmitter check — not yet run"), and the operator
    /// then chooses to Tab one step. That step is the difference between
    /// proceeding and being funnelled, and stages 2 through 4 key the
    /// transmitter. Falls back to the h1 on the very first render.
    /// </remarks>
    private static string FocusScript(string? stageId)
    {
        string id = JsonEncode(stageId ?? "");
        return @"
(function () {
    var target = null;
    var want = " + id + @";
    if (want) target = document.getElementById('stage-h-' + want);
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
                // page; the gate's copy is the one that decides anything. The
                // choice id maps to a load KIND — an unknown id fails closed —
                // and remoteness is read from the radio at the moment of
                // declaration, so the report can weight a declaration made a
                // thousand miles from the socket (#244, #247).
                _gate.DeclareLoad(m.Value,
                                  TransmitStageSet.LoadKindFromChoice(m.Choice),
                                  declaredRemotely: RigIsRemote());
                _declarations[TransmitStageSet.LoadDeclaration] = m.Value;
                Tracing.TraceLine("FixerDialog: load declared as \"" + m.Value + "\" ("
                                  + _gate.LoadKind
                                  + (_gate.LoadDeclaredRemotely ? ", remote" : "") + ")",
                                  TraceLevel.Info);
                Render();
                return;

            case FixerPageMessage.Kind.DeclareHearing:
                // The operator's own reading of the receive path (#243). The
                // choice id decides the fact; the words go on the page and
                // into the report. Fed into stage 0's facts on its next run.
                _hearing = TransmitStageSet.HearingFromChoice(m.Choice);
                _declarations[TransmitStageSet.HearingDeclaration] = m.Value;
                Tracing.TraceLine("FixerDialog: hearing declared as \"" + m.Value + "\" ("
                                  + _hearing + ")", TraceLevel.Info);
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
                // The engine refuses a skip over a completed measurement
                // (#249); this pre-check exists so the operator gets a plain
                // sentence at the stage rather than a swallowed exception. A
                // message can still arrive for a completed stage — a stale
                // page, a double-fire — because the page stops OFFERING skip
                // there, it cannot stop a caller SENDING it.
                if (_run.ResultFor(m.StageId)?.Status == FixerStageStatus.Ran)
                {
                    Notice(m.StageId, "That check has already run, and its measurement is "
                                    + "kept. To measure again, choose Run this check again.");
                    return;
                }
                _run.SkipStage(m.StageId, m.Value);
                // Skipping is a decision about this stage; the operator's next
                // question is the following stage, so the current-stage marker
                // moves forward with them.
                _state.SelectedStageId = NextStageId(m.StageId) ?? m.StageId;
                Render();
                return;

            case FixerPageMessage.Kind.ExplainToggled:
                // Page-local fact, recorded so the NEXT render honours it. No
                // re-render now — the page already shows the state it posted,
                // and a render here would move the reader mid-gesture.
                _explainOpen[m.StageId] = string.Equals(m.Value, "open", StringComparison.Ordinal);
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

            case FixerPageMessage.Kind.OpenPowerDialog:
                // Power belongs to PowerDialog. The page asks; it does not
                // grow a number box of its own (#250).
                OpenPowerDialog();
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

        FixerStage? stage = _run.Set.Find(stageId);
        if (stage?.OffUiThread == true)
        {
            // Off the UI thread — the Sprint 35 ruling, and ONLY for stages
            // marked for it (today: the microphone check, which keys nothing).
            // Blocking there bought no safety and cost a frozen page that
            // read as a hang (#255). The transmitting stages stay synchronous
            // on this thread deliberately: the blocked thread is currently
            // the only thing preventing anything else starting while the
            // radio is keyed, and that guard does not come off before #236
            // gives them a real abort path. RunInProgress spans the whole
            // async run, so a second stage cannot start underneath it.
            CancellationToken token = _stageCancel.Token;
            System.Threading.Tasks.Task
                .Run(() => _run.RunStage(stageId, token))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Tracing.TraceLine("FixerDialog: stage " + stageId + " faulted — "
                            + t.Exception?.GetBaseException().Message, TraceLevel.Error);
                        Notice(stageId, "Something went wrong running that check. "
                                      + "Nothing was transmitted.");
                        FinishStage(stageId, again, null);
                        return;
                    }
                    FinishStage(stageId, again, t.Result);
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        // Synchronous on the UI thread, deliberately, for the stages that
        // key: they are short and bounded, and an operator who cannot press
        // Stop because we are busy is the one outcome that must not happen.
        // If a keying stage ever grows long enough for that to bite, the fix
        // is a real abort path (#236) — NOT a longer timeout.
        FixerStageResult? result = null;
        try
        {
            result = _run.RunStage(stageId, _stageCancel.Token);
        }
        finally
        {
            FinishStage(stageId, again, result);
        }
    }

    /// <summary>
    /// Everything that happens after a stage finishes, however it ran:
    /// cleanup, the critical announcements, the focus decision, the render.
    /// One home, so the synchronous and off-thread paths cannot drift.
    /// </summary>
    private void FinishStage(string stageId, bool again, FixerStageResult? r)
    {
        _stageCancel?.Dispose();
        _stageCancel = null;

        // Belt and braces. The boundary already unkeys in a finally and the
        // gate's NoteUnkeyed is safe unmatched, so this cannot double-count
        // — and if a path ever escaped without unkeying, the accounting
        // would otherwise stay wrong for the rest of the run.
        _gate.NoteUnkeyed();

        if (r != null)
        {
            Tracing.TraceLine("FixerDialog: stage " + stageId + " -> " + r.Status
                              + (again ? " (re-run)" : ""), TraceLevel.Info);

            AnnounceCriticals(r);

            // Where focus lands next is decided, not discovered (the design
            // doc's phrase). A stage that PASSED moves the current-stage
            // marker forward, so the re-render focuses the next stage's
            // heading — name and state in one announcement, one Tab from its
            // controls. A stage with findings, or one that could not run,
            // keeps focus at ITS OWN heading (which now speaks the new
            // status), because moving on would bury the findings the
            // operator has not heard yet.
            bool passed = r.Status == FixerStageStatus.Ran
                          && (r.Findings == null || r.Findings.Count == 0);
            _state.SelectedStageId = passed ? (NextStageId(stageId) ?? stageId) : stageId;
        }

        Render();
    }

    /// <summary>The stage after this one in the set's order, or null at the
    /// end.</summary>
    private string? NextStageId(string stageId)
    {
        var stages = _run.Set.Stages;
        for (int i = 0; i < stages.Count - 1; i++)
            if (string.Equals(stages[i].Id, stageId, StringComparison.OrdinalIgnoreCase))
                return stages[i + 1].Id;
        return null;
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
        // The result count is what makes the question honest (#250): a stop
        // between stages used to end the run and every recorded measurement
        // silently.
        //
        // MERGE NOTE (Sprint 37): resultsAreKept is FALSE on this branch
        // because nothing here persists the run. The persistence track wires
        // FixerEvidenceKit into this dialog in the same sprint; once its
        // field lands, pass the kit's own signal (its Record property is
        // non-null exactly when the journal is live) so the question stops
        // threatening a discard that will not happen — and never a constant
        // true, because the journal can fail to set up.
        FixerAbort.Plan plan = FixerAbort.Decide(keyed, source, RunInProgress,
                                                 _run.ResultsInRunOrder.Count,
                                                 resultsAreKept: false);

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

    /// <summary>
    /// Is this a remote session, at the moment of asking? False when nothing
    /// can be read — claiming "remote" without evidence would be invention,
    /// and the flag only ever ADDS a caveat to the report.
    /// </summary>
    private bool RigIsRemote()
    {
        try { return _radio()?.RemoteRig == true; } catch { return false; }
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
        return MessageBox.Show(this, q, _run.Set.Name + " checks — JJ Flexible",
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
        // MERGE NOTE (Sprint 37): resultsAreKept false for the same reason as
        // in Stop() — see the note there.
        FixerAbort.Plan plan = FixerAbort.Decide(keyed, FixerAbort.Source.WindowClosing,
                                                 RunInProgress,
                                                 _run.ResultsInRunOrder.Count,
                                                 resultsAreKept: false);

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
        _state.ExplanationOpen = _explainOpen;
        _state.TransmitCount = _gate.TransmitCount;

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
            ToPage("status", Lexicon.Get("audio.fixer.copy_refused"));
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

    private void OpenPowerDialog()
    {
        // #250: the transmitting stages name the power they will use, and the
        // next thing an operator wants is to change it. The Fixer is modal, so
        // "go to the main window" used to cost the whole run. PowerDialog is
        // the app's one home for power — XVTR-aware, limit-checked, and it
        // applies live and speaks each change — so the page hands off to it
        // exactly as it hands off to the device picker.
        //
        // Nothing is spoken before it opens: a screen reader flushes its
        // queue on a window change, so the arriving window carries its own
        // announcement.
        FlexBase? rig;
        try { rig = _radio(); } catch { rig = null; }
        if (rig == null)
        {
            ToPage("status", "No radio is connected, so there is no power to change.");
            return;
        }

        try
        {
            var dlg = new PowerDialog(rig) { Owner = this };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: power dialog failed — " + ex.Message,
                              TraceLevel.Warning);
            ToPage("status", "The power window could not be opened.");
        }

        // The stage sentences carry tune and RF power, and either may just
        // have moved — re-render so the page tells the truth, and focus
        // returns to the current stage's heading.
        Render();
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
