using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using Radios.Fixer.Evidence;

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

    /// <summary>
    /// The evidence layer: the run writes itself to disk as it goes (#251).
    /// </summary>
    /// <remarks>
    /// <b>The whole of the dialog's side of persistence is these six calls</b>
    /// — one at construction, one after each of the four things the engine
    /// records, and <c>End</c> on every close path. Nothing here can throw and
    /// nothing here can change what the run does; the kit swallows its own
    /// failures because a diagnostic must never be taken down by the machinery
    /// that files it. Until Sprint 37 this field did not exist and
    /// <c>FixerEvidenceKit.Begin</c> had no callers anywhere, so every store,
    /// journal, fingerprint and viewer downstream of it was working perfectly
    /// on nothing at all.
    /// </remarks>
    private readonly FixerEvidenceKit _evidence;

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

    /// <summary>
    /// Whether the page's how-to section opens by default (#378): true until
    /// a check run has ever been saved on this computer. A first-time
    /// operator needs the instructions open; a returning one wants them
    /// folded away; no stored preference is needed because the run store
    /// already knows which operator this is. Deleting every saved run brings
    /// the instructions back, which is about right. The operator's own
    /// toggle, reported on the explain wire, wins for the life of the window.
    /// </summary>
    private readonly bool _howToOpenByDefault;

    /// <summary>
    /// The verdict to speak once the next render lands (#373): pushed into
    /// the page's polite status line AFTER navigation completes, because a
    /// push before the render would announce into a document about to be
    /// replaced, and content already present at load is never announced by a
    /// live region. Focus goes to the next action; this is how the outcome is
    /// still heard — an announcement is not a focus position.
    /// </summary>
    private string? _pendingAnnouncement;

    /// <summary>
    /// The element the page must focus once the next render lands, named by
    /// the action that caused the render (#365).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null means "nothing has been pressed yet", and only the very first
    /// render is entitled to it</b> — that render goes to the h1, where the
    /// operator meets the summary, the test ID, how to drive the page and the
    /// Stop control before anything else. Every render after an action names
    /// its landing here.
    /// </para>
    /// <para>
    /// <b>Why a field rather than a parameter of Render.</b> Some renders are
    /// not caused by an action at all — the fallback inside
    /// <see cref="Notice"/> before the page is ready is one — and those should
    /// leave the operator where the last action put them rather than moving
    /// them somewhere new. Holding the target means "no new instruction"
    /// keeps the old landing instead of collapsing to the top of the page,
    /// which is the bug this whole field exists to end.
    /// </para>
    /// </remarks>
    private string? _focusElementId;

    /// <summary>True when this dialog believes a stage is running right now.</summary>
    private bool RunInProgress => _stageCancel != null;

    private FixerDialog(Func<FlexBase?> radio, FixerRunRecord? resumeFrom)
    {
        _radio = radio;
        _gate = new FixerTransmitGate();

        // The kill switch's alarm earcon lives in this assembly and the switch
        // lives in Radios, so somebody has to hand it over. Done here as well
        // as in the PTT controller's own constructor because this dialog can
        // open when no controller was ever built — and a kill that cannot make
        // a sound is half a kill. Idempotent.
        PttSafetyController.EnsureKillWiring();

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
            // The computer's half of the transmit chain walk (#400). The walk
            // itself is TransmitChainCheck's — the SAME definition the Audio
            // Workshop's transmit door calls, deliberately, so a rule added to
            // tx-chain-rules.txt reaches both with no second edit. What only
            // this layer can see is the Windows side of it: which microphone is
            // chosen, whether Windows has it muted, what its level is. The
            // radio layer cannot read any of that and must never invent it.
            pcChainFacts: ReadPcChainFacts,
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
            countdown: FixerCountdown.TransmitTone,
            // And WHEN the key-up falls inside that count — derived from the
            // sound itself, never copied. See CountdownKeyUpMs.
            countdownKeyUpAtMs: CountdownKeyUpMs(),
            // And how long that count SOUNDS, which is a different number and
            // 1.8 s later. The spoken stage waits it out before asking anyone
            // to talk — see WaitOutTheCountdown.
            countdownDurationMs: CountdownDurationMs());

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
                stopRequested: StageStopRequested,
                countdownKeyUpAtMs: CountdownKeyUpMs()),

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

            // WIRED. The receive walk, at stage 0 (#367) — and it is the SAME
            // call the Audio Workshop's receive door makes, deliberately, down
            // to the method name. One definition, two doors: add a rule to
            // rx-chain-rules.txt and it appears here and there with no second
            // edit. Anything less than a shared call is two homes for one idea.
            //
            // Nothing here decides what the receive facts mean; ReceiveAudioCheck
            // walks the rules and AudioSetupCheck folds the answer into the
            // stage, the same split every other stage keeps.
            ReadReceiveChain = () =>
            {
                FlexBase? rig;
                try { rig = _radio(); } catch { rig = null; }
                // A null radio is an ordinary input, not a reason to skip: the
                // walk's own first rule is "no radio is connected", and the
                // report should say that rather than say nothing.
                return Radios.ChainChecks.ReceiveAudioCheck.Run(rig!);
            },

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

        FixerStageSet set = TransmitStageSet.Build(hosts);

        // A resumed run keeps its Test ID, its start time and every result it
        // already had; a fresh one gets a new ID. Either way the evidence layer
        // opens around it here, and from this point the run is writing itself
        // to disk on every recording rather than living only in memory until
        // the window closes.
        if (resumeFrom == null)
        {
            _run = new FixerRun(set);
            _evidence = FixerEvidenceKit.Begin(_run, _radio);
        }
        else
        {
            _run = FixerRun.Resume(set, FixerRunRehydrator.Rehydrate(resumeFrom));
            _evidence = FixerEvidenceKit.Resume(_run, _radio, resumeFrom);
        }
        _gate.BeginRun(_run.RunId);

        // First run on this computer? Then the how-to section opens (#378).
        // Asked of the store by file names only — cheap — and an unreadable
        // store reads as "first run", because the operator who gets the
        // instructions unnecessarily loses seconds while the one who needed
        // them and did not get them loses the feature.
        bool anySaved = false;
        try
        {
            anySaved = !string.IsNullOrEmpty(RadioConfig.AppDataRoot)
                       && Radios.Fixer.Evidence.FixerRunStore.Default().HasAnyRecord();
        }
        catch { /* treat as first run */ }
        _howToOpenByDefault = !anySaved;

        // NAMED BY THE CHECK, NOT BY THE TOOL. Noel, 2026-08-25: the operator
        // came for a transmit check, not for a product noun, and the menu
        // already says it that way (Tools > Fix > Transmit problems). Derived
        // from the stage set so a future receive set reads "Receive checks"
        // with nothing to rename. "Fixer" stays as the internal name — same
        // split as jjflexible.exe over the JJFlexRadio internals.
        Title = _run.Set.Name + " tests — JJ Flexible";
        Width = 780;
        Height = 640;
        ResizeMode = ResizeMode.CanResize;

        AutomationProperties.SetName(_web, _run.Set.Name + " tests");
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
    /// When the key-up falls inside the transmit countdown, in milliseconds
    /// from the first tone — <b>derived from the sound that is actually going
    /// to play, never copied from it.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is computed rather than stated.</b> The number lived in
    /// <c>FixerTransmitAudioBoundary</c> as a constant with a remark naming the
    /// earcon's step length, and the earcon's step length changed. It read 300
    /// against a 600 ms third tone for months, so every keying stage raised RF
    /// during the SECOND dit of its own warning and then played the rest of the
    /// countdown at an operator who was already transmitting. Nothing failed:
    /// the two numbers live in assemblies that cannot see each other, so no
    /// build, no merge and no test could notice them parting company.
    /// </para>
    /// <para>
    /// <b>The rule, RULED BY NOEL 2026-08-30:</b> key up at the start of the
    /// LAST COUNTING DIT — <see cref="EarconPlayer.CountdownLastDitAtMs"/> —
    /// so that the radio is ALREADY TRANSMITTING when the landing sounds.
    /// </para>
    /// <para>
    /// <b>The distinction is the whole point, and it is easy to lose.</b> "Start
    /// transmitting at the ding" and "send the key-up at the ding" are different
    /// instants, because MOX does not engage the moment it is commanded. Keying
    /// a beat early spends the count's last second absorbing that latency, so
    /// the landing coincides with the radio actually coming up. The landing then
    /// MEANS "you are transmitting" rather than "you are about to" — which is
    /// what the Earcon Explorer has always told the operator ("the two seconds
    /// before that last count are your window to stop it") and what #261
    /// specified.
    /// </para>
    /// <para>
    /// <b>This derivation was wrong from the day it was written, and three
    /// artifacts said so.</b> It summed every element but the last, which put
    /// key-up 1,240 ms late — at the start of the landing's SECOND note, an
    /// instant nobody had ever described. `CountdownLastDitAtMs` already
    /// computed the right answer, carried the reasoning above in its own doc
    /// comment, and had ZERO CALLERS. The boundary's fallback agreed with the
    /// orphan. The Explorer's operator-facing sentence agreed with the orphan.
    /// Only the running code disagreed, and no test pinned either value, so
    /// nothing could fail. Found by the Sprint 42 integration pass.
    /// </para>
    /// <para>
    /// It stays shape-independent: the orphan derives from the count and the
    /// interval, so it survives a retune of the beat and a change in the number
    /// of counts without anyone remembering to follow it.
    /// </para>
    /// <para>
    /// <b>It errs LATE, never early.</b> An unexpected shape, an empty figure or
    /// a throw all fall back to the boundary's own conservative default: waiting
    /// too long costs a moment of silence, and keying too early costs the
    /// operator the window in which they could have stopped it.
    /// </para>
    /// </remarks>
    /// <summary>
    /// How long the transmit countdown SOUNDS, end to end. A different number
    /// from the key-up, and later than it.
    /// </summary>
    /// <remarks>
    /// <b>These two being different is the whole point.</b> The key-up goes out
    /// on the last counting dit so the radio is transmitting by the landing;
    /// the landing then sounds for another second and a half. A stage that
    /// treats MOX confirmation as "the countdown is over" talks over its own
    /// warning and measures the landing as the operator's voice. Derived, like
    /// the key-up, so retuning the beat moves both.
    /// </remarks>
    private static int CountdownDurationMs()
    {
        try
        {
            int ms = EarconPlayer.CountdownDurationMs(transmit: true);
            return ms > 0 ? ms : FixerTransmitAudioBoundary.DefaultCountdownDurationMs;
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: the countdown's length could not be read, so the "
                              + "spoken cue falls back to the conservative default — " + ex.Message,
                              TraceLevel.Warning);
            return FixerTransmitAudioBoundary.DefaultCountdownDurationMs;
        }
    }

    private static int CountdownKeyUpMs()
    {
        try
        {
            int at = EarconPlayer.CountdownLastDitAtMs;
            return at > 0 ? at : FixerTransmitAudioBoundary.DefaultCountdownKeyUpMs;
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: the countdown's pacing could not be read, so the "
                              + "key-up falls back to the conservative default — " + ex.Message,
                              TraceLevel.Warning);
            return FixerTransmitAudioBoundary.DefaultCountdownKeyUpMs;
        }
    }

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
                 + "it is right for your room is not judged by this test.";
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
        // Through StationConditions, not by reading TXFrequency here (#399).
        // That property is a cached echo and holds zero until the radio has
        // reported a transmit slice, and "0.000000 MHz" in a sentence about
        // where the radio is going to transmit is a plausible-looking lie. The
        // one reader owns the fallback and names it.
        s.Frequency = StationConditions.Frequency(rig);
        s.Mode = StationConditions.Mode(rig);
        return s;
    }

    /// <summary>
    /// This computer's half of the transmit chain facts, for the walk the keyed
    /// stages take (#400).
    /// </summary>
    /// <remarks>
    /// The same collector and the same settings path the Audio Workshop's
    /// transmit door uses, so the two doors read the same computer the same way.
    /// A failure here is honestly empty rather than fatal: the walk still runs
    /// on the radio's half, and its census says how much of the chain was
    /// actually seen.
    /// </remarks>
    private static System.Collections.Generic.IReadOnlyList<DiagnosticFact> ReadPcChainFacts()
    {
        try
        {
            string path = System.IO.Path.Combine(RadioConfig.AppDataRoot, "audioDevices.xml");
            return TxChainPcFacts.Collect(path);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: the computer's chain facts could not be read — "
                              + ex.Message, TraceLevel.Warning);
            return Array.Empty<DiagnosticFact>();
        }
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
    /// <param name="resumeFrom">A saved run to continue, from the saved test
    /// runs list. Null starts a fresh run.</param>
    public static void Show(Func<FlexBase?> radio, Window? owner = null,
                            FixerRunRecord? resumeFrom = null)
    {
        if (!HtmlInfoDialog.IsAvailable)
        {
            AdvisoryDialog.Show("Transmit tests — JJ Flexible",
                "The transmit tests need the Microsoft Edge WebView2 runtime, which is "
                + "not installed on this computer. Everything they would have checked can "
                + "still be reached from the Audio Workshop and from Diagnostics.");
            return;
        }

        // Refused BEFORE anything opens, not part-way through. A run recorded
        // against a different set of tests cannot be continued honestly, and
        // discovering that after the window is up would leave the operator in a
        // live run that is quietly not being recorded.
        if (resumeFrom != null && WhyItCannotBeResumed(resumeFrom) is { Length: > 0 } refusal)
        {
            AdvisoryDialog.Show("Transmit tests — JJ Flexible", refusal);
            return;
        }

        var dialog = new FixerDialog(radio, resumeFrom);
        if (owner != null) dialog.Owner = owner;
        dialog.ShowModalDialog();
    }

    /// <summary>
    /// Why a saved run cannot be continued, in words for the operator, or
    /// empty when it can.
    /// </summary>
    /// <remarks>
    /// The stage set is built with no hosts to read its shape — that
    /// construction is pure data and touches no radio and no audio device.
    /// </remarks>
    internal static string WhyItCannotBeResumed(FixerRunRecord record)
    {
        if (record == null) return "There is no saved run to continue.";

        try
        {
            FixerStageSet set = TransmitStageSet.Build(null);

            if (!string.Equals(record.StageSetId, set.Id, StringComparison.OrdinalIgnoreCase))
                return "Run " + record.RunId + " is a set of " + record.StageSetName
                     + " tests, and this window runs the " + set.Name + " tests. It can "
                     + "still be read and exported from the saved test runs list.";

            string was = string.Join(", ", record.Stages.Select(s => s.Id));
            string now = string.Join(", ", set.Stages.Select(s => s.Id));
            if (!string.Equals(was, now, StringComparison.OrdinalIgnoreCase))
                return "Run " + record.RunId + " was recorded with a different set of tests "
                     + "from the ones this version of JJ Flexible offers, so continuing it "
                     + "would mix measurements from two different runs. It can still be read "
                     + "and exported from the saved test runs list.";

            return "";
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: could not decide whether run " + record.RunId
                              + " is resumable — " + ex.Message, TraceLevel.Warning);
            return "JJ Flexible could not work out whether run " + record.RunId + " can be continued, so it has "
                 + "not been opened. It can still be read and exported from the "
                 + "saved test runs list.";
        }
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

        // Taken now, pushed after the focus script: the focus announcement
        // lands first, then the polite region speaks the verdict behind it.
        // Taken even if focusing fails — the verdict must be heard either way.
        string? announce = _pendingAnnouncement;
        _pendingAnnouncement = null;

        try
        {
            _web.Focus();
            // THE FIRST RENDER GETS THE h1 AND NOTHING ELSE. Before any
            // action there is nothing to come back to, and the top of the
            // document is where the operator meets the summary, the test ID,
            // the intro, the Stop everything control and how to use the
            // page. Naming
            // no candidate at all is what asks for it — the current-stage
            // fallback must not apply here, or opening the tool would skip
            // straight past all of that into stage 0.
            await _web.CoreWebView2.ExecuteScriptAsync(
                _focusElementId == null
                    ? FocusScript()
                    : FocusScript(_focusElementId, CurrentStageHeadingId()));
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: focus handoff failed — " + ex.Message,
                              TraceLevel.Warning);
        }

        if (!string.IsNullOrEmpty(announce)) ToPage("status", announce);
    }

    /// <summary>
    /// Put the caret where the operator should be after a re-render, trying
    /// each named element in turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A full re-render would otherwise dump them at the document head and
    /// make them navigate back — every time, for the length of the run
    /// (#365). Focus goes to the NEXT ACTION (#373): the Run control after a
    /// declaration is answered or the power window closes, the forward
    /// control after a stage completes, the first fix button when a stage
    /// found something we can fix. The verdict is spoken separately through
    /// the status line — an announcement is not a focus position. The one
    /// place focus still lands on a heading is arrival at a stage the
    /// operator has NOT read yet (the page's own Next control does that),
    /// because stages 2 through 4 key the transmitter and must be read
    /// before they are pressed.
    /// </para>
    /// <para>
    /// <b>Ordered candidates, because a landing that misses must not become a
    /// landing at the top.</b> The action's own target first, then the
    /// current stage's heading, then the h1. A page that renamed an id would
    /// otherwise silently reintroduce exactly the defect this replaced — the
    /// h1 fallback is the failure mode, not the design.
    /// </para>
    /// </remarks>
    private static string FocusScript(params string?[] candidateIds)
    {
        var list = new System.Text.StringBuilder();
        foreach (string? id in candidateIds)
        {
            if (string.IsNullOrEmpty(id)) continue;
            if (list.Length > 0) list.Append(',');
            list.Append(JsonEncode(id));
        }

        return @"
(function () {
    var want = [" + list + @"];
    var target = null;
    for (var i = 0; i < want.length && !target; i++) {
        target = document.getElementById(want[i]);
    }
    if (!target) target = document.querySelector('h1') || document.body;
    if (!target) return;
    target.setAttribute('tabindex', '-1');
    target.focus();
})();";
    }

    /// <summary>The page element id of a stage's heading — the one place the
    /// host spells this, so the host and <c>FixerPage</c> cannot drift apart
    /// about it.</summary>
    private static string StageHeadingId(string? stageId)
        => string.IsNullOrEmpty(stageId) ? "" : "stage-h-" + stageId;

    /// <summary>
    /// The heading of the stage the page is treating as current — resolved
    /// the SAME way <c>FixerPage.Render</c> resolves it, including its
    /// fall-back to the first stage, so the host cannot focus one stage while
    /// the page has marked another as current.
    /// </summary>
    private string CurrentStageHeadingId() => StageHeadingId(CurrentStageId());

    /// <summary>
    /// The stage the page is treating as current — resolved the SAME way
    /// <c>FixerPage.Render</c> resolves it, including its fall-back to the
    /// first stage, so the host cannot act on one stage while the page has
    /// marked another as current.
    /// </summary>
    private string CurrentStageId()
    {
        string id = _state.SelectedStageId ?? "";
        if (_run.Set.Find(id) == null)
            id = _run.Set.Stages.Count > 0 ? _run.Set.Stages[0].Id : "";
        return id;
    }

    /// <summary>
    /// The next action WITHIN a stage (#373): its first unanswered
    /// declaration when it has one — a question the operator has not dealt
    /// with must not be jumped over — otherwise its Run control, whose
    /// description reads the stage's question and what pressing it will do.
    /// The landing after a declaration is answered and after the power
    /// window closes.
    /// </summary>
    private string ForwardLandingIn(string stageId)
    {
        FixerStage? stage = _run.Set.Find(stageId ?? "");
        if (stage == null) return CurrentStageHeadingId();

        foreach (FixerRunDeclaration decl in stage.Declarations)
        {
            if (_declarations.ContainsKey(decl.Id)) continue;
            if (decl.Choices.Count == 0) continue;
            // The first choice's radio input, whose group legend asks the
            // question — the page's Declaration() spells these ids.
            return "decl-" + decl.Id + "-" + decl.Choices[0].Id;
        }
        return FixerPage.RunControlId(stageId);
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
                _evidence.DeclarationRecorded(TransmitStageSet.LoadDeclaration,
                                              m.Choice, m.Value);
                Tracing.TraceLine("FixerDialog: load declared as \"" + m.Value + "\" ("
                                  + _gate.LoadKind
                                  + (_gate.LoadDeclaredRemotely ? ", remote" : "") + ")",
                                  TraceLevel.Info);
                // FORWARD, with the answer spoken (#373). Sprint 39 landed on
                // the "You said" line — the read-back — which fixed "where am
                // I" and left "what now": the operator still walked forward
                // past their own answer to reach the next thing to do. Now
                // the answer is spoken through the status line and focus goes
                // to the current stage's next action — its unanswered
                // hearing question first, else its Run control. The page
                // still renders the "You said" line for re-reading.
                Render(ForwardLandingIn(CurrentStageId()), "You said: " + m.Value + ".");
                return;

            case FixerPageMessage.Kind.DeclareHearing:
                // The operator's own reading of the receive path (#243). The
                // choice id decides the fact; the words go on the page and
                // into the report. Fed into stage 0's facts on its next run.
                _hearing = TransmitStageSet.HearingFromChoice(m.Choice);
                _declarations[TransmitStageSet.HearingDeclaration] = m.Value;
                _evidence.DeclarationRecorded(TransmitStageSet.HearingDeclaration,
                                              m.Choice, m.Value);
                Tracing.TraceLine("FixerDialog: hearing declared as \"" + m.Value + "\" ("
                                  + _hearing + ")", TraceLevel.Info);
                // The same forward rule as the load declaration (#373). This
                // is the button Noel pressed at the bench on 2026-08-28; the
                // walk from his answer to stage 0's Run control is the walk
                // #373 exists to end. The declaration belongs to stage 0, so
                // stage 0 becomes current and its Run control — the next
                // action, now that the question is answered — is the
                // landing, with the answer spoken.
                _state.SelectedStageId = TransmitStageSet.AudioSetup;
                Render(ForwardLandingIn(TransmitStageSet.AudioSetup),
                       "You said: " + m.Value + ".");
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
                    Notice(m.StageId, "That test has already run, and its measurement is "
                                    + "kept. To measure again, choose Run this test again.");
                    return;
                }
                // Recorded to disk like any other result. A skip carries its
                // reason and the reason is evidence — the report says what was
                // not done and why, and that half must survive the window
                // closing as much as a measurement does.
                _evidence.StageRecorded(_run.SkipStage(m.StageId, m.Value));
                // FORWARD, with the cost spoken (#373). Sprint 39 kept the
                // operator on the skipped stage so its cost — "whether your
                // own voice would get through is left open" — was not carried
                // past unheard. That reasoning held while the only way to be
                // heard was to be the focus position; the verdict is spoken
                // through the status line now, cost included, and focus goes
                // to the forward control. A skip is a deliberate decision to
                // move on; the landing finally agrees with it.
                _state.SelectedStageId = m.StageId;
                Render(FixerPage.LandingAfterResult(_run, m.StageId),
                       FixerPage.SpokenVerdict(_run, m.StageId));
                return;

            case FixerPageMessage.Kind.CurrentStage:
                // The operator used the page's forward control. Recorded, no
                // re-render — the page has already moved them, and rebuilding
                // the document would move a screen reader mid-gesture for
                // nothing. Recording it is what stops the host focusing the
                // stage they LEFT when a render it did not cause arrives,
                // which is the shape of #365 on the one path where the
                // operator, not a button, did the moving.
                if (_run.Set.Find(m.StageId) != null) _state.SelectedStageId = m.StageId;
                return;

            case FixerPageMessage.Kind.ExplainToggled:
                // Page-local fact, recorded so the NEXT render honours it. No
                // re-render now — the page already shows the state it posted,
                // and a render here would move the reader mid-gesture.
                _explainOpen[m.StageId] = string.Equals(m.Value, "open", StringComparison.Ordinal);
                return;

            case FixerPageMessage.Kind.ApplyFix:
            {
                // The wire carries the FINDING id, which is what ApplyFix takes.
                FixerFixRecord applied = _run.ApplyFix(m.StageId, m.Value);
                _evidence.FixRecorded(applied);
                _state.SelectedStageId = m.StageId;

                // THE FIX'S OUTCOME HAS TO BE HEARD. Every action in
                // FixerFixActions reads its change back precisely so it can
                // report what the setting BECAME rather than that a setter
                // was called; for one sprint that sentence was written down
                // and never spoken, and for another it was the focus landing
                // (#366's fix). Now it is spoken through the status line
                // (#373) — recorded in the stage's notice slot too, so it can
                // be re-read — and focus goes to the next action, which after
                // a fix is running the check again to see whether it worked.
                string became = applied.Succeeded
                    ? applied.WhatItBecame
                    : "That fix did not succeed: " + applied.WhatItBecame;
                RecordNotice(m.StageId, became);
                Render(FixerPage.RunControlId(m.StageId), became);
                return;
            }

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
                //
                // Nothing is announced before it opens, on the same rule
                // OpenPowerDialog states: a screen reader flushes its queue
                // on a window change, so the arriving window carries its own
                // announcement. There WAS a notice here, addressed to stage
                // "" — an element the page never renders — so it was written
                // to nowhere and spoken by nobody, on the one surface built
                // to expose exactly that.
                OpenDevicePicker();
                return;

            case FixerPageMessage.Kind.OpenPowerDialog:
                // Power belongs to PowerDialog. The page asks; it does not
                // grow a number box of its own (#250).
                OpenPowerDialog();
                return;

            case FixerPageMessage.Kind.OpenFrequencyDialog:
                // Frequency belongs to FreqInputDialog, the app's own frequency
                // entry. Exactly the power hand-off, one setting along (#399).
                OpenFrequencyDialog();
                return;

            case FixerPageMessage.Kind.OpenModeDialog:
                // The frequency hand-off one step along (#411): the ruled four
                // transmit-audio modes, refused while keyed, confirmed from
                // the radio's own report.
                OpenModeDialog();
                return;
        }
    }

    // ---------------- running a stage ----------------

    private void RunStage(string stageId, bool again)
    {
        if (RunInProgress)
        {
            Notice(stageId, "Something is already running. Wait for it to finish, or press "
                          + "Stop everything.");
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
                        // RECORDED, not pushed: FinishStage re-renders on the
                        // next line, so a push would announce into a document
                        // about to be replaced. The render carries the notice
                        // and lands focus on it.
                        RecordNotice(stageId, "Something went wrong running that test. "
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

        // THE END-OF-TEST TONE, for every stage. Ruled 2026-08-30: "the same
        // sequence for all tests". This is the one place both the synchronous
        // keying stages and the off-thread microphone stage converge, so it is
        // one call rather than five wirings that could drift apart -- which is
        // exactly how the countdown's key-up moment ended up with two answers.
        //
        // Before the unkey accounting below and before any render: the tone
        // says the measurement is over, and it should not wait on bookkeeping.
        FixerCountdown.StageDone();

        // Belt and braces. The boundary already unkeys in a finally and the
        // gate's NoteUnkeyed is safe unmatched, so this cannot double-count
        // — and if a path ever escaped without unkeying, the accounting
        // would otherwise stay wrong for the rest of the run.
        _gate.NoteUnkeyed();

        if (r != null)
        {
            // Persisted HERE, the moment the engine returns the result — not
            // on close. The close path is also the abandon path, and a crash
            // loses everything either way; on the transmitting stages the
            // measurement was paid for with RF.
            _evidence.StageRecorded(r);

            Tracing.TraceLine("FixerDialog: stage " + stageId + " -> " + r.Status
                              + (again ? " (re-run)" : ""), TraceLevel.Info);

            AnnounceCriticals(r);

            // WHERE FOCUS LANDS IS DECIDED, NOT DISCOVERED — and the verdict
            // is SPOKEN, separately, because an announcement is not a focus
            // position (#373).
            //
            // The history, in two halves. A stage that passed used to move
            // focus to the NEXT stage's heading — which looked like forward
            // motion and was actually the loss of the result: Noel ran stage
            // 0 at the bench (Test ID 427-RAW), it ran clean, and the first
            // thing his reader announced was "Stage 1 — NOT YET RUN"; he
            // reasonably concluded the tool said he had not run it (#366).
            // Sprint 39 fixed that by landing on the completed stage's own
            // heading — the right answer to the wrong question: he heard the
            // verdict and then had to walk forward past everything he had
            // already dealt with, every press, for the whole run (#373).
            //
            // Both halves at once now: the verdict — the heading's words plus
            // the answer, which is the stage's whole product — goes out
            // through the status line once the render lands, and focus goes
            // to the NEXT ACTION: the first fixable finding's button when
            // there is one (its description reads the finding), the stage's
            // own Run control when it could not run, the forward control
            // otherwise. Moving to the next stage stays the operator's press,
            // and arrival there still lands on its heading, because unread
            // stages that transmit must be read before they are pressed
            // (#248).
            _state.SelectedStageId = stageId;
            Render(FixerPage.LandingAfterResult(_run, stageId),
                   FixerPage.SpokenVerdict(_run, stageId));
            return;
        }

        // No result at all — the off-thread path faulted, or the engine threw
        // out through the synchronous path's finally. Either way a notice
        // explaining it is recorded against this stage, so that is the
        // landing: the heading still says "not yet run" and would tell the
        // operator nothing about why.
        _state.SelectedStageId = stageId;
        Render(NoticeId(stageId));
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
        // The result count is what makes the question honest (#250), and the
        // kept flag is the evidence layer's OWN signal — Record is non-null
        // exactly when the journal is live. Never a constant true: the
        // journal can fail to set up, and a question that promises "saved"
        // over a journal that never opened is silent data loss with a
        // reassuring voice. Until Sprint 40 this passed a constant FALSE, so
        // the prompt threatened to discard results that were already safe on
        // disk (#376) — a warning that frightens an operator out of leaving
        // a window is just the other kind of lie.
        FixerAbort.Plan plan = FixerAbort.Decide(keyed, source, RunInProgress,
                                                 _run.ResultsInRunOrder.Count,
                                                 resultsAreKept: _evidence.Record != null);

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
                    switch (AskExit(plan))
                    {
                        case FixerExitPrompt.Choice.ResumeLater:
                            CloseKeepingRun();
                            break;
                        case FixerExitPrompt.Choice.DiscardAndExit:
                            CloseDiscardingRun();
                            break;
                        // Continue: stay exactly where they were.
                    }
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
    /// <para>
    /// Cancelling the stage is what normally stops it, because the runner
    /// unkeys in its own finally. The direct writes are the backstop for a
    /// runner that is wedged, and they are attempted independently: a
    /// transmitter left keyed is worse than any exception this could raise.
    /// </para>
    /// <para>
    /// <b>Every route into here needs the dispatcher, so none of them can
    /// arrive while a keying stage is running.</b> The page's Stop and its
    /// Escape are WebView2 messages; <c>OnPreviewKeyDown</c> is a WPF event;
    /// the closing handler is a window event. All of them queue behind the
    /// synchronous stage. That is why the kill also has a route that does not
    /// come through here at all — see <see cref="TransmitKillSwitch"/> — and
    /// why this hands the request to it rather than keeping a second unkey of
    /// its own. When a check is armed the switch owns the confirmation and the
    /// words; when nothing is armed it is a no-op and the direct writes below
    /// are all that happen, silently, which is right.
    /// </para>
    /// </remarks>
    private void UnkeyNow()
    {
        try { _stageCancel?.Cancel(); } catch { /* stopping is not optional */ }

        FlexBase? rig = null;
        try { rig = _radio(); } catch { }

        TransmitKillSwitch.DropCarrier(rig, TransmitKillSwitch.Carrier.Tune);
        TransmitKillSwitch.DropCarrier(rig, TransmitKillSwitch.Carrier.Mox);
        TransmitKillSwitch.Request(TransmitKillSwitch.Source.HostRequest);

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

    /// <summary>
    /// Ask the exit question as the three choices that actually exist (#376):
    /// exit without saving, continue, stop-and-resume-later. The third is
    /// offered only when the plan says the run is genuinely persisted and
    /// holds results. Each choice carries its COST in help text announced on
    /// focus — a transmit-stage measurement was paid for with RF, and the
    /// prompt is where that price is stated.
    /// </summary>
    private FixerExitPrompt.Choice AskExit(FixerAbort.Plan plan)
    {
        string question = plan.Announcement.Length > 0
            ? plan.Announcement : "Do you want to stop the test?";

        bool kept = _evidence.Record != null;
        string exitHelp = kept
            ? "Deletes this run's saved record. Everything recorded so far is gone "
              + "for good"
              + (_gate.TransmitCount > 0
                  ? ", including measurements that keyed the radio — taking those "
                    + "again costs real transmission."
                  : ".")
            : "Ends the test and closes the window. This run was not being saved, "
              + "so nothing is kept.";

        string? resumeHelp = plan.OffersResumeLater
            ? "Closes the window and keeps the run. Continue it later from View or "
              + "resume saved test runs, on the Fix menu — everything already recorded "
              + "stays"
              + (RunInProgress
                  ? ", though the test running right now stops and is not recorded"
                  : "")
              + ", and the report will say the tests were done in more than one "
              + "sitting."
            : null;

        return FixerExitPrompt.Ask(this, _run.Set.Name + " tests — JJ Flexible",
                                   question, exitHelp, resumeHelp);
    }

    /// <summary>Close, keeping the saved run — "Stop tests and resume later".
    /// The stamp says so; the saved-runs list offers the run for
    /// continuation, which is the whole point of choosing this.</summary>
    private void CloseKeepingRun()
    {
        _gate.AbortRun();
        _evidence.End("stopped to resume later");
        _closing = true;
        if (DialogResult == null) CloseWithResult(false);
        else Close();
    }

    /// <summary>Close, deleting the saved record — "Exit without saving".
    /// Runs persist as they go, so abandoning deliberately must actually
    /// remove the file; otherwise the option lies in the other direction
    /// (#376). A record that cannot be deleted stays in the saved list —
    /// traced, and the honest failure.</summary>
    private void CloseDiscardingRun()
    {
        _gate.AbortRun();
        _evidence.Discard();
        _closing = true;
        if (DialogResult == null) CloseWithResult(false);
        else Close();
    }

    private void Abandon()
    {
        _gate.AbortRun();
        // Stamped as abandoned, and the word matters: the saved-runs list
        // offers an abandoned run for resumption, which is what stops walking
        // away to look something up from destroying the sitting (#250). This
        // path is now only the no-ceremony closes — nothing recorded, or the
        // window already going away while keyed.
        _evidence.End("abandoned");
        _closing = true;
        if (DialogResult == null) CloseWithResult(false);
        else Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // End is idempotent and the first caller's word wins, so every close
        // path can call it without knowing about the others — including the
        // failed-init path, and the one where Abandon has already stamped its
        // own reason.
        if (_closing || _initFailed) { _evidence.End("closed"); return; }

        bool keyed = _gate.InFlight || RigIsKeyed();
        // The kept flag is the evidence layer's own signal, same as in
        // Stop() — see the note there. The window close button reaches the
        // SAME three-way ask as Escape and a stop, deliberately (#376): two
        // routes out of one window must not bargain differently.
        FixerAbort.Plan plan = FixerAbort.Decide(keyed, FixerAbort.Source.WindowClosing,
                                                 RunInProgress,
                                                 _run.ResultsInRunOrder.Count,
                                                 resultsAreKept: _evidence.Record != null);

        // Whatever else the plan says, if the carrier is up it comes down —
        // and it comes down here rather than after the window has gone.
        if (plan.UnkeysFirst) UnkeyNow();

        if (plan.Asks)
        {
            switch (AskExit(plan))
            {
                case FixerExitPrompt.Choice.Continue:
                    // The operator chose to stay. NOTHING is stamped: a run
                    // marked ended while it is still live would put a false
                    // close time on a sitting that is still going, which is
                    // the one thing a record of when measurements happened
                    // must never do.
                    e.Cancel = true;
                    return;

                case FixerExitPrompt.Choice.ResumeLater:
                    _gate.AbortRun();
                    _evidence.End("stopped to resume later");
                    return;

                case FixerExitPrompt.Choice.DiscardAndExit:
                    _gate.AbortRun();
                    _evidence.Discard();
                    return;
            }
        }

        _gate.AbortRun();
        _evidence.End("closed");
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

    /// <summary>
    /// Render, and say where the operator lands when it arrives (#365).
    /// </summary>
    /// <remarks>
    /// <b>Every host action that re-renders calls THIS one</b>, never the
    /// bare <see cref="Render()"/>. A re-render with no landing named is how
    /// the page came to throw the operator back to the document head on every
    /// press: the two declaration buttons and the return from the power
    /// window all rebuilt the page without ever telling it where the person
    /// pressing them had been. Naming the landing at the call site keeps the
    /// decision beside the action that earns it.
    /// </remarks>
    private void Render(string focusElementId)
    {
        if (!string.IsNullOrEmpty(focusElementId)) _focusElementId = focusElementId;
        Render();
    }

    /// <summary>
    /// Render, land focus, and SPEAK the outcome once the page arrives
    /// (#373). The two halves of the rule in one call: focus goes to the
    /// next action, and the verdict — which focus no longer reads — is
    /// pushed to the polite status line after navigation completes, where a
    /// live region genuinely announces it.
    /// </summary>
    private void Render(string focusElementId, string announce)
    {
        if (!string.IsNullOrEmpty(announce)) _pendingAnnouncement = announce;
        Render(focusElementId);
    }

    private void Render()
    {
        if (!_web.IsInitialized || _web.CoreWebView2 == null) return;

        _state.DeclarationAnswers = _declarations;
        _state.StageNotices = _notices;
        _state.ExplanationOpen = _explainOpen;
        _state.TransmitCount = _gate.TransmitCount;
        _state.HowToOpenByDefault = _howToOpenByDefault;
        // The how-to section's leaving bullet may only promise "pick it up
        // later" while something is really writing the run to disk.
        _state.RunIsSaved = _evidence.Record != null;

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
        // A NOTICE THAT NAMES NO STAGE HAS NO SLOT TO GO IN. The page renders
        // one notice paragraph per stage and nothing else, so "notice-" on
        // its own addresses an element that does not exist and the receive
        // channel drops it without a word. Those go to the polite status
        // line, which always exists and is live — the page's own home for
        // "something happened that belongs to no single stage".
        if (string.IsNullOrEmpty(stageId) || _run.Set.Find(stageId) == null)
        {
            if (_ready) ToPage("status", text);
            else Tracing.TraceLine("FixerDialog: notice with no stage arrived before the "
                                   + "page was ready and was not shown — " + text,
                                   TraceLevel.Warning);
            return;
        }

        RecordNotice(stageId, text);
        // Pushed through the receive channel rather than re-rendered, so the
        // operator is not moved mid-gesture. The slot is a polite live region
        // (Sprint 39), so the push is heard; before that a refusal was
        // written into the page and never spoken, which on the surface built
        // to expose silent failures was its own quiet one.
        if (_ready) ToPage("notice", text, stageId);
        else Render();
    }

    /// <summary>Record a notice against a stage WITHOUT pushing it to the
    /// page — for the callers that re-render immediately afterwards, where a
    /// push would announce into a document about to be replaced.</summary>
    private void RecordNotice(string stageId, string text)
        => _notices[stageId ?? ""] = text;

    /// <summary>The page element id of a stage's notice slot.</summary>
    private static string NoticeId(string? stageId)
        => string.IsNullOrEmpty(stageId) ? "" : "notice-" + stageId;

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
        //
        // And the SAME document the saved-runs list exports, once there is a
        // saved run to build it from: the radio's identity and the conditions
        // each measurement was taken under wrapped around the report (#217).
        // Two buttons called Copy that produce two different documents is how
        // an operator ends up sending the thin one to a support desk. Before
        // anything has been recorded there is no record to wrap, and the
        // report alone is the honest answer.
        try
        {
            FixerRunRecord? saved = _evidence.Record;
            string text = saved != null && saved.ReportText.Length > 0
                ? FixerRunExport.PlainText(saved)
                : FixerReport.PlainText(_run);
            Clipboard.SetText(text);
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
        // have moved — re-render so the page tells the truth. Focus returns
        // to the current stage's RUN control (#373): the operator changed
        // power in order to run the stage, they have already read it — the
        // power button renders after Run — and the Run control's description
        // reads the fresh "at N watts into ANT1" sentence, so the landing
        // itself confirms what the power window changed. This render landed
        // on the stage heading until Sprint 40, and named no landing at all
        // before Sprint 39 (#365).
        Render(ForwardLandingIn(CurrentStageId()));
    }

    /// <summary>
    /// Hand off to the app's frequency entry, apply what the operator typed,
    /// and come back with the run intact (#399).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Noel, 2026-08-29, running the Fixer into a real antenna for the first
    /// time:</b> <i>"We probably need to be able to change the frequency while
    /// testing to find a quiet signal … right now I'm in the fix window and I
    /// can't change frequency unless I kill the test."</i> He had walked the
    /// whole tool on his own radio that morning and every stage passed, because
    /// <b>on a dummy load the frequency is irrelevant</b> — nothing radiates and
    /// any frequency is as good as any other. The instrument that made the bench
    /// safe is the instrument that hid this.
    /// </para>
    /// <para>
    /// <b>The same hand-off as power, deliberately, and not a tuning UI of our
    /// own.</b> The Home surface tunes, has a key map the operator already
    /// knows, and is not somewhere a modal dialog can send them and get them
    /// back. FreqInputDialog is the app's frequency entry and it accepts the
    /// three forms the app has always accepted; growing a second one here would
    /// be a new thing to learn at the worst possible moment.
    /// </para>
    /// <para>
    /// <b>This is NOT #191.</b> That is the automated park-and-restore — check
    /// whether the frequency is busy, move somewhere safe, put it back
    /// afterwards, announce both transitions. This is the operator keeping the
    /// wheel in their own hands, which is the simpler thing and cannot get the
    /// judgement wrong. Nothing here moves the frequency on its own, and nothing
    /// here restores it, so there is no captured value to fail to put back.
    /// </para>
    /// </remarks>
    /// <summary>
    /// How long to wait for the radio to confirm a change — frequency or mode
    /// — before saying, honestly, that it has not. The writes are enqueued, so
    /// zero would always report failure and a long wait would freeze the page.
    /// One number for both hand-offs, on purpose: two constants for one
    /// promise is how they drift apart.
    /// </summary>
    private const int RadioConfirmMs = 1500;

    /// <summary>
    /// Poll until <paramref name="confirmed"/> says the radio agrees, or the
    /// confirm window closes. Shared by the frequency and mode hand-offs so
    /// the two cannot grow different ideas of what "confirmed" costs.
    /// </summary>
    private static bool ConfirmedWithin(int ms, Func<bool> confirmed)
    {
        for (int waited = 0; waited < ms; waited += 50)
        {
            if (confirmed()) return true;
            System.Threading.Thread.Sleep(50);
        }
        return false;
    }

    private void OpenFrequencyDialog()
    {
        // What we asked for, so the confirmation below can tell "the radio
        // agrees" from "the radio has not said so".
        ulong asked = 0UL;

        FlexBase? rig;
        try { rig = _radio(); } catch { rig = null; }
        if (rig == null)
        {
            ToPage("status", "No radio is connected, so there is no frequency to change.");
            return;
        }

        // NEVER WHILE KEYED. The transmitting stages block this thread for their
        // whole transmission so a press cannot arrive mid-stage, but a radio
        // keyed from anywhere else — the operator's own microphone, another
        // client, a stage that ended without the radio confirming — must not be
        // retuned underneath the carrier. Refused out loud rather than silently
        // ignored: a button that does nothing reads as a broken button. The
        // read fails CLOSED and lives in StationConditions, shared with the
        // mode hand-off (#411).
        if (StationConditions.KeyedFailClosed(rig))
        {
            ToPage("status", StationConditions.RefusedWhileKeyed("frequency"));
            return;
        }

        ulong before = StationConditions.FrequencyHz(rig);

        try
        {
            var dlg = new FreqInputDialog { Owner = this };
            // The window title is the first thing a screen reader announces on
            // arrival, and "Frequency Input" alone does not say WHICH frequency
            // or that the run is still standing behind it. Named for the thing
            // it moves.
            dlg.Title = "Change the transmit frequency — JJ Flexible";
            // Pre-filled with where the radio actually is, so a screen reader
            // entering the field reads the current frequency — the operator
            // finds out where they are and edits from there, rather than typing
            // into a blank box with no idea what they are leaving.
            if (before != 0UL) dlg.FreqBox.Text = StationConditions.Format(before)
                                                     .Replace(" MHz", "");
            dlg.ValidateFrequency = text
                => StationConditions.TryParse(text, out ulong hz)
                    ? hz.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null;
            dlg.ErrorMessage = "That is not a frequency this radio can be set to. Type "
                             + "megahertz with a decimal point, like 14.250, or kilohertz "
                             + "on their own, like 14250.";

            if (dlg.ShowDialog() != true)
            {
                // Cancelled. Nothing moved, and the render below still refreshes
                // the stage sentences, which is harmless and keeps one exit path.
                Render(ForwardLandingIn(CurrentStageId()));
                return;
            }

            if (!ulong.TryParse(dlg.ResultFrequency,
                                System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out ulong wanted) || wanted == 0UL)
            {
                ToPage("status", "That frequency could not be used, so nothing was changed.");
                return;
            }

            rig.TXFrequency = wanted;
            asked = wanted;
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: frequency change failed — " + ex.Message,
                              TraceLevel.Warning);
            ToPage("status", "The frequency could not be changed.");
            Render(ForwardLandingIn(CurrentStageId()));
            return;
        }

        // WHAT THE RADIO NOW REPORTS, not what we asked for. #164 found the
        // radio acknowledging a transmit-side write it did not apply, so this
        // line exists to be honest about exactly that.
        //
        // IT WAS READING BACK OUR OWN WRITE. Found by the Sprint 42 integration
        // pass. StationConditions.Frequency reads FlexBase.TXFrequency, which
        // the setter fills with the REQUESTED value immediately; the radio's
        // echo overwrites it later only if the change actually lands. So a
        // refused or ignored write left the request sitting there and this line
        // reported it as fact -- in the one place written to guard against that
        // very thing. The operator was told they had moved, and could key
        // believing it.
        //
        // TXFrequencyAsReported asks the slice instead. And because the write
        // is ENQUEUED, the answer is not instant: poll briefly, then say what is
        // actually true, including "we cannot confirm it" when that is the
        // honest answer. Erring towards an unconfirmed report is the direction
        // that costs an operator RF on a frequency they did not choose. The
        // poll and the sentences are shared with the mode hand-off (#411).
        ulong agreed = 0UL;
        if (ConfirmedWithin(RadioConfirmMs, () =>
            {
                ulong r = rig.TXFrequencyAsReported;
                if (r != 0UL && r == asked) { agreed = r; return true; }
                return false;
            }))
        {
            ToPage("status", StationConditions.ChangeAccepted("frequency"));
        }
        else
        {
            ulong reported = rig.TXFrequencyAsReported;
            Tracing.TraceLine("FixerDialog: the radio did not confirm " + asked
                              + " within " + RadioConfirmMs + " ms; it reports "
                              + reported, TraceLevel.Warning);
            ToPage("status", reported != 0UL
                ? StationConditions.ChangeNotAccepted("frequency")
                : StationConditions.ChangeNotAcceptedNothingReported("frequency"));
        }

        // Re-render for the same reason the power hand-off does: every
        // transmitting stage's "what Run will do" sentence names the frequency,
        // and it has just moved. Focus lands on the current stage's Run control,
        // whose description carries the fresh sentence — so the landing itself
        // confirms the change.
        Render(ForwardLandingIn(CurrentStageId()));
    }

    /// <summary>
    /// Hand off to the mode picker, apply what the operator chose, and come
    /// back with the run intact (#411).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The frequency hand-off one step along, and built out of its parts on
    /// purpose:</b> a transmit-audio test run in the wrong sideband is a valid
    /// measurement of the wrong thing, and on a real antenna it is also a
    /// transmission somebody else hears. Mode was already RECORDED in every
    /// stage's evidence (#399's StationConditions.Line) and offered nowhere.
    /// The keyed refusal, the confirm poll and the confirmation sentences are
    /// the same code the frequency hand-off runs — not the same shape, the
    /// same code — because the integration pass found the Fixer growing second
    /// copies of vocabularies Home already had, and this control is exactly
    /// where a third copy would have grown.
    /// </para>
    /// <para>
    /// <b>The list is ruled and it is exactly four</b> — LSB, USB, DIGU, DIGL
    /// (<see cref="TransmitStageSet.TransmitAudioModes"/>). A radio sitting in
    /// CW or FM is named in the picker's own words, never smuggled in as a
    /// fifth entry.
    /// </para>
    /// <para>
    /// Nothing here moves the mode on its own and nothing restores it
    /// afterwards — this is the operator keeping the wheel, not #191's
    /// park-and-restore.
    /// </para>
    /// </remarks>
    private void OpenModeDialog()
    {
        FlexBase? rig;
        try { rig = _radio(); } catch { rig = null; }
        if (rig == null)
        {
            ToPage("status", "No radio is connected, so there is no mode to change.");
            return;
        }

        // NEVER WHILE KEYED — the same shared read as the frequency hand-off,
        // and it fails closed. A mode change re-plumbs the transmit chain, and
        // under a live carrier that is a new transmission nobody chose.
        if (StationConditions.KeyedFailClosed(rig))
        {
            ToPage("status", StationConditions.RefusedWhileKeyed("mode"));
            return;
        }

        string asked;
        try
        {
            // The picker's header states what the radio NOW REPORTS — the live
            // slice read, not our cache and never a request (#164). If the
            // radio reports nothing, the picker says that too.
            asked = FixerModePrompt.Ask(this, rig.TXModeAsReported);
            if (asked.Length == 0)
            {
                // Kept what they had. Nothing moved; the render keeps one exit
                // path, exactly like the frequency hand-off's cancel.
                Render(ForwardLandingIn(CurrentStageId()));
                return;
            }

            rig.TXMode = asked;
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerDialog: mode change failed — " + ex.Message,
                              TraceLevel.Warning);
            ToPage("status", "The mode could not be changed.");
            Render(ForwardLandingIn(CurrentStageId()));
            return;
        }

        // WHAT THE RADIO NOW REPORTS, not what was asked for. The write is
        // enqueued and #164 measured this radio acknowledging a transmit-side
        // write it never applied, so the confirmation reads the slice back —
        // TXModeAsReported — through the same poll and the same sentences the
        // frequency hand-off uses. When the radio does not agree, the operator
        // is told plainly what it still reports, before they key in it.
        string agreed = "";
        if (ConfirmedWithin(RadioConfirmMs, () =>
            {
                string r = rig.TXModeAsReported;
                if (r.Length != 0 && string.Equals(r, asked, StringComparison.OrdinalIgnoreCase))
                {
                    agreed = r;
                    return true;
                }
                return false;
            }))
        {
            ToPage("status", StationConditions.ChangeAccepted("mode"));
        }
        else
        {
            string reported = rig.TXModeAsReported;
            Tracing.TraceLine("FixerDialog: the radio did not confirm mode " + asked
                              + " within " + RadioConfirmMs + " ms; it reports \""
                              + reported + "\"", TraceLevel.Warning);
            ToPage("status", reported.Length != 0
                ? StationConditions.ChangeNotAccepted("mode")
                : StationConditions.ChangeNotAcceptedNothingReported("mode"));
        }

        // Re-render so the Run control's sentence carries the mode the stage is
        // about to transmit in — the landing itself confirms the change, the
        // same as power and frequency.
        Render(ForwardLandingIn(CurrentStageId()));
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
