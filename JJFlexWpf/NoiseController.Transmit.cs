using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    /// <summary>
    /// The transmit noise gate and the cleanup monitor as the noise controller
    /// reaches them (Sprint 49 Track H2): an interface, so the controller's
    /// rules run windowless in tests against a fake, and in the app against
    /// <see cref="TxAudioConditioning"/>'s held settings (#1077: held with or
    /// without a radio).
    /// </summary>
    public interface ITransmitGate
    {
        /// <summary>Whether the gate is on (the operator's intent).</summary>
        bool Enabled { get; }

        /// <summary>
        /// Turn the gate on or off. Returns when it takes effect: Track H's
        /// repair round 1 (2026-11-18) makes the gate's on and off wait for the
        /// unkey while keyed, like an engine change, so the caller speaks the
        /// timing it gets back and never guesses it.
        /// </summary>
        TxChangeResult SetEnabled(bool on);
        /// <summary>The threshold follows the measured room floor plus <see cref="MarginDb"/>; off, <see cref="ThresholdDb"/> is used as set.</summary>
        bool AutoThreshold { get; }
        float MarginDb { get; }
        float ThresholdDb { get; }
        float AttackMs { get; }
        float HoldMs { get; }
        float ReleaseMs { get; }
        float RangeDb { get; }

        // ── The downward expander's own settings (Sprint 49 Track I) ──
        // Default bodies so a test's fake gate needs none of them.

        /// <summary>The expander's ratio, n:1 (used when <see cref="GateSetting"/> is false).</summary>
        float RatioToOne => JJPortaudio.TxNoiseGate.DefaultRatioToOne;

        /// <summary>Whether the expander is at its top setting, the gate.</summary>
        bool GateSetting => false;

        /// <summary>Whether the detector hears the voice band only.</summary>
        bool SideChainFilter => true;

        float SideChainLowHz => JJPortaudio.TxNoiseGate.DefaultSideChainLowHz;

        float SideChainHighHz => JJPortaudio.TxNoiseGate.DefaultSideChainHighHz;

        float LookAheadMs => JJPortaudio.TxNoiseGate.DefaultLookAheadMs;

        /// <summary>The expander's strength: a ratio, or the gate. A continuous lever (S15).</summary>
        TxChangeResult SetStrength(float ratio, bool gate) => new(TxChangeTiming.Refused, "no-expander");

        /// <summary>The side-chain filter on or off: a switch, held for the unkey while keyed (S2).</summary>
        TxChangeResult SetSideChainFilter(bool on) => new(TxChangeTiming.Refused, "no-expander");

        // ── The expander's mode (Sprint 49 Track I phase 2, ruled S6) ──

        /// <summary>The expander's mode: <see cref="ExpanderModes.Jj"/> or <see cref="ExpanderModes.Wdsp"/>.</summary>
        string ExpanderMode => ExpanderModes.Jj;

        /// <summary>The WDSP mode's strength, its depth in dB (DEXP has no ratio).</summary>
        float WdspDepthDb => ExpanderModes.DefaultWdspDepthDb;

        /// <summary>Whether WDSP's expander can run here; never starts the helper.</summary>
        Unavailability WdspAvailability => Unavailability.None;

        /// <summary>The mode: a switch, held for the unkey while keyed (S2).</summary>
        TxChangeResult SetExpanderMode(string mode) => new(TxChangeTiming.Refused, "no-expander");

        /// <summary>The WDSP mode's depth; it reaches the helper at the unkey.</summary>
        TxChangeResult SetWdspDepth(float db) => new(TxChangeTiming.Refused, "no-expander");

        /// <summary>
        /// The automatic threshold on or off, in order behind anything held
        /// (repair round 2, item 1). Returns when it takes effect.
        /// </summary>
        TxChangeResult SetAutoThreshold(bool on);

        /// <summary>
        /// One gate level (<see cref="TxGateLever"/>'s keys: margin, threshold,
        /// attack, hold, release, depth). Returns when it takes effect, so the
        /// controller speaks the value asked for and its timing rather than a
        /// getter that still reads the old value (repair round 2, item 2).
        /// </summary>
        TxChangeResult SetLevel(string key, float value);

        /// <summary>Whether a radio's transmit chain is attached, so the cleanup monitor can play (it needs the chain's tap).</summary>
        bool MonitorAvailable { get; }

        /// <summary>The cleanup monitor: 0 off, 1 what goes out, 2 what was removed, 3 both (<c>TxAudioConditioner.MonitorModes</c>).</summary>
        int MonitorMode { get; set; }

        /// <summary>The cleanup monitor's volume, 0 to 1.</summary>
        float MonitorVolume { get; set; }

        /// <summary>The gate's settings as a setup carries them.</summary>
        TxNoiseGateSetup Capture();

        /// <summary>
        /// Put a setup's gate settings in place NOW, its on or off included.
        /// The controller calls this only inside one change it holds through
        /// <see cref="TxNoiseChain.RunOrDefer"/> (repair round 1, item 4): a
        /// whole setup or preset, or the gate's defaults, waits for the unkey
        /// as ONE change, so nothing in here may defer a second time.
        /// </summary>
        void ApplyNow(TxNoiseGateSetup gate);

        /// <summary>
        /// The stages after the gate as a setup carries them (Sprint 49 Track I):
        /// the equaliser, the de-esser and the stage order. A gate that has none
        /// (a test's fake) answers with them off and the default order.
        /// </summary>
        (ParametricEqSetup Eq, TxDeEsserSetup DeEsser, string[] Order) CaptureStages() =>
            (new ParametricEqSetup(), new TxDeEsserSetup(), JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder);

        /// <summary>
        /// Put a setup's equaliser, de-esser and stage order in place NOW with
        /// its gate (or the gate as it is, when null), as ONE staged record. The
        /// same contract as <see cref="ApplyNow"/>: only inside one change the
        /// controller holds.
        /// </summary>
        void ApplyStagesNow(ParametricEqSetup eq, TxDeEsserSetup deEsser, string[] order, TxNoiseGateSetup? gate)
        {
            if (gate != null) ApplyNow(gate);
        }
    }

    /// <summary>The app's gate: <see cref="TxAudioConditioning"/>, the session's one transmit chain.</summary>
    public sealed class AppTransmitGate : ITransmitGate
    {
        public bool Enabled => TxAudioConditioning.GateEnabled;

        // Track H repair round 1: the gate's on and off wait for the unkey while
        // keyed, and say so; the Workshop's old toggle did this, and it lives here now.
        public TxChangeResult SetEnabled(bool on) => TxAudioConditioning.SetGateEnabled(on);
        public bool AutoThreshold => TxAudioConditioning.AutoThreshold;
        public float MarginDb => TxAudioConditioning.ThresholdMarginDb;
        public float ThresholdDb => TxAudioConditioning.GateThresholdDb;
        public float AttackMs => TxAudioConditioning.GateAttackMs;
        public float HoldMs => TxAudioConditioning.GateHoldMs;
        public float ReleaseMs => TxAudioConditioning.GateReleaseMs;
        public float RangeDb => TxAudioConditioning.GateRangeDb;
        public TxChangeResult SetAutoThreshold(bool on) => TxAudioConditioning.SetAutoThreshold(on);
        public TxChangeResult SetLevel(string key, float value) => TxAudioConditioning.SetGateLevel(key, value);
        public float RatioToOne => TxAudioConditioning.ExpanderRatio;
        public bool GateSetting => TxAudioConditioning.ExpanderIsGate;
        public bool SideChainFilter => TxAudioConditioning.SideChainFilter;
        public float SideChainLowHz => TxAudioConditioning.SideChainCorners.Low;
        public float SideChainHighHz => TxAudioConditioning.SideChainCorners.High;
        public float LookAheadMs => TxAudioConditioning.LookAheadMs;
        public TxChangeResult SetStrength(float ratio, bool gate) => TxAudioConditioning.SetExpanderStrength(ratio, gate);
        public TxChangeResult SetSideChainFilter(bool on) => TxAudioConditioning.SetSideChainFilter(on);
        public string ExpanderMode => TxAudioConditioning.ExpanderMode;
        public float WdspDepthDb => TxAudioConditioning.WdspDepthDb;
        public Unavailability WdspAvailability => TxAudioConditioning.WdspExpanderAvailability;
        public TxChangeResult SetExpanderMode(string mode) => TxAudioConditioning.SetExpanderMode(mode);
        public TxChangeResult SetWdspDepth(float db) => TxAudioConditioning.SetWdspDepth(db);
        public bool MonitorAvailable => TxAudioConditioning.Conditioner != null;
        public int MonitorMode
        {
            get => (int)TxAudioConditioning.MonitorMode;
            set => TxAudioConditioning.MonitorMode = (JJPortaudio.TxAudioConditioner.MonitorModes)value;
        }
        public float MonitorVolume { get => TxAudioConditioning.MonitorVolume; set => TxAudioConditioning.MonitorVolume = value; }
        public TxNoiseGateSetup Capture() => TxAudioConditioning.CaptureSetup("").Gate.Clone();
        // Track H's repair made a setup with a null slot mean "that slot's
        // default, off", so a gate-only setup through ApplySetup would reset the
        // engines. The gate's own half, now, inside the controller's one change.
        public void ApplyNow(TxNoiseGateSetup gate) => TxAudioConditioning.ApplyGateNow(gate);
        public (ParametricEqSetup Eq, TxDeEsserSetup DeEsser, string[] Order) CaptureStages()
        {
            var s = TxAudioConditioning.CaptureSetup("");
            return (s.Eq, s.DeEsser, s.StageOrder);
        }
        public void ApplyStagesNow(ParametricEqSetup eq, TxDeEsserSetup deEsser, string[] order, TxNoiseGateSetup? gate) =>
            TxAudioConditioning.ApplyStagesNow(eq, deEsser, order, gate);
    }

    /// <summary>One of the gate's levers: what it is, its range, its step and its unit.</summary>
    public sealed record TxGateLever(string Key, string LabelKey, double Min, double Max, double Step, string? UnitKey, bool Advanced)
    {
        public const string Margin = "margin";
        public const string Threshold = "threshold";
        public const string Attack = "attack";
        public const string Hold = "hold";
        public const string Release = "release";
        public const string Depth = "depth";
        public const string MonitorVolume = "monitor_volume";
        // Sprint 49 Track I: the downward expander's levers.
        /// <summary>The ratio, as stops; the top stop is the gate. Its value is the stop's index.</summary>
        public const string Strength = "strength";
        public const string SideChainLow = "sidechain_low";
        public const string SideChainHigh = "sidechain_high";
        public const string LookAhead = "lookahead";
        // Phase 2: the expander's mode, JJ Flexible's (0) or WDSP's (1).
        public const string Mode = "mode";
    }

    /// <summary>
    /// The transmit half of the noise controller (Sprint 49 Track H2, design
    /// part seven). The same rule book as receive, on the transmit chain Track
    /// H built (<see cref="TxNoiseChain"/>): the Transmit menu, Home's
    /// Transmission fields and the noise layer's transmit direction all call
    /// these, so they agree by construction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every change is an intent through the chain's own calls</b>
    /// (<see cref="TxNoiseChain.SetEnabled"/>, <see cref="TxNoiseChain.ChooseEngine"/>,
    /// <see cref="TxNoiseChain.SetSetting"/>, <see cref="TxNoiseChain.SetStrength"/>),
    /// and the timing the chain answers with is spoken, never guessed: a change
    /// made while keyed says it waits for the unkey (ruled S2, 2026-11-18).
    /// </para>
    /// <para>
    /// <b>Every sentence is the receive sentence with "for transmit" after the
    /// engine's name</b> (the brief), on the transmit SpeechSubject twins, so a
    /// receive sentence and a transmit one never expire each other. The
    /// engine's name is decorated by one placeholder,
    /// <c>audio.noise_surface.tx.engine</c>, for Codex to word.
    /// </para>
    /// <para>
    /// <b>No per-mode memory on transmit</b> (part seven): nothing here reads or
    /// writes the mode groups. CW and digital bypass the whole transmit chain
    /// (<see cref="TxAudioConditioning"/>'s mode policy), which the summary says.
    /// </para>
    /// </remarks>
    public sealed partial class NoiseController
    {
        private readonly Func<TxNoiseChain?> _txChain;

        /// <summary>The gate and the cleanup monitor (the app's by default; a fake in tests).</summary>
        public ITransmitGate TransmitGate { get; set; } = new AppTransmitGate();

        /// <summary>Where this radio's transmit noise setups live; the app's settings root by default, null when there is none.</summary>
        public Func<TxNoiseSetupStore?> TransmitSetupStore { get; set; } = TxNoiseSetupStore.Default;

        /// <summary>
        /// After a setup is renamed, point every microphone that named it at the
        /// new name (Track H's contract: <c>MicrophoneProfileStore.RepointNoiseSetup</c>,
        /// then save the microphone store). Set by the app. Returns how many
        /// microphones were repointed, or <see cref="RepointNotSaved"/> when the
        /// microphone store could not be saved (repair round 1, item 10).
        /// </summary>
        public Func<string, string, string, int>? RepointMicrophones { get; set; }

        /// <summary><see cref="RepointMicrophones"/>' answer when the microphone store's save did not land.</summary>
        public const int RepointNotSaved = -1;

        /// <summary>The source every transmit road journals under, added to the road's own.</summary>
        private static string TxSource(string source) => source + ", transmit";

        // ────────────────────────────────────────────────────────────────
        //  The layer's direction (T)
        // ────────────────────────────────────────────────────────────────

        /// <summary>Which chain the noise layer's keys act on. Receive until T; back to receive at every layer session.</summary>
        public DenoiseDirection LayerDirection { get; private set; } = DenoiseDirection.Receive;

        /// <summary>
        /// The noise layer's T (ruled 2026-11-07, question 6): switch between
        /// receive and transmit and say which. The engine last touched is
        /// forgotten, so the arrows never move the other direction's lever by
        /// surprise; the next engine key picks one.
        /// </summary>
        public void SwitchLayerDirection()
        {
            LayerDirection = LayerDirection == DenoiseDirection.Receive ? DenoiseDirection.Transmit : DenoiseDirection.Receive;
            Current = null; CurrentLeverIndex = 0;
            _cursorEpoch++;
            _txStepIndex = -1;
            _stepIndex = -1;
            Say(L(LayerDirection == DenoiseDirection.Transmit ? "audio.noise_surface.tx.layer_transmit" : "audio.noise_surface.tx.layer_receive"),
                SpeechSubject.NoiseLayerDirection);
        }

        // ── The lever cursor's ownership (repair round 1, item 2) ──
        //
        // Current is one cursor shared by both directions, and work that runs
        // later (an engine field's settle) used to assign it after T or a new
        // session had cleared it, so the layer's Up then moved the other
        // direction's lever (Codex: receive RNNoise .90 to .85 with no receive
        // engine chosen). Now the cursor records the direction it points in, and
        // a delayed assignment lands only if no session started and T was not
        // pressed since the work was scheduled. The work itself still happens:
        // a Home choice is the operator's intent whatever the layer does.

        /// <summary>The direction <see cref="Current"/> points in; the layer's arrows act only when it is the layer's.</summary>
        private DenoiseDirection _currentDirection = DenoiseDirection.Receive;

        /// <summary>Moves on at every layer session and every T: a delayed cursor assignment from before is stale.</summary>
        private long _cursorEpoch;

        /// <summary>Point the cursor at an engine in a direction (an operator's action, now).</summary>
        private void Point(string? id, DenoiseDirection direction)
        {
            if (NotInThisVersion(id)) return;   // never the cursor, and its direction is not taken either (bench round 2)
            Current = id;
            _currentDirection = direction;
        }

        /// <summary>Point the cursor from work scheduled at <paramref name="epoch"/>, only if nothing has moved the cursor's ownership since.</summary>
        private void PointLater(string id, DenoiseDirection direction, long epoch)
        {
            if (epoch != _cursorEpoch || NotInThisVersion(id)) return;
            Point(id, direction);
            CurrentLeverIndex = 0;
        }

        /// <summary>The cursor's engine when it points in the layer's direction, else null: the layer never adjusts the other direction's lever.</summary>
        private string? CurrentInLayerDirection => _currentDirection == LayerDirection ? Current : null;

        /// <summary>
        /// The layer's "choose an engine first" hint in its direction (words
        /// round): after T the radio letters are refused, so receive's hint, which
        /// names them, would send the operator to keys that do nothing.
        /// </summary>
        private string NoEngineYetKey => LayerDirection == DenoiseDirection.Transmit
            ? "audio.noise_surface.tx.no_engine_yet" : "audio.noise_surface.no_engine_yet";

        /// <summary>The layer's switches-only hint (no engine to pick) in its direction, for the layer engine's own hints.</summary>
        public string SwitchesOnlyKey => LayerDirection == DenoiseDirection.Transmit
            ? "audio.noise_layer.tx.switches_only" : "audio.noise_layer.switches_only";

        /// <summary>The engine the layer's favourite and defaults keys act on: the cursor, only when it points in the layer's direction.</summary>
        public string? CurrentInLayer => CurrentInLayerDirection;

        /// <summary>A receive subject, or its transmit twin while the layer is in the transmit direction.</summary>
        private string LayerSubject(string receiveSubject) =>
            LayerDirection == DenoiseDirection.Transmit ? TxSubject(receiveSubject) : receiveSubject;

        /// <summary>The transmit twin of a receive noise subject, so the two directions never expire each other.</summary>
        internal static string TxSubject(string receiveSubject) => receiveSubject switch
        {
            SpeechSubject.NoiseEngineSwitch => SpeechSubject.TransmitNoiseEngineSwitch,
            SpeechSubject.NoiseLevel => SpeechSubject.TransmitNoiseLevel,
            SpeechSubject.NoiseUnavailable => SpeechSubject.TransmitNoiseUnavailable,
            SpeechSubject.NoiseActiveSummary => SpeechSubject.TransmitNoiseActiveSummary,
            SpeechSubject.NoiseDefaults => SpeechSubject.TransmitNoiseDefaults,
            _ => receiveSubject,
        };

        /// <summary>An engine's name as a transmit sentence says it: "NNR for transmit" (placeholder for Codex).</summary>
        private string TxName(string id) => L("audio.noise_surface.tx.engine", ("engine", NameOf(id)));

        // ────────────────────────────────────────────────────────────────
        //  The engine list on transmit
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether transmit offers an engine (rulings 2026-11-18 08:26, S7, S9):
        /// every computer reducer the catalog says serves transmit. Not the
        /// radio's engines, not the blankers and notches (part seven, question
        /// 10 after the rulings: a microphone has no pulse noise), and not
        /// Captured noise, which the catalog marks receive only.
        /// </summary>
        private static bool OffersTransmit(DenoiseEngineEntry e) =>
            e.Side == DenoiseSide.Pc && e.Family != null && e.Group == DenoiseGroup.NoiseReduction && e.Serves(DenoiseDirection.Transmit);

        /// <summary>An engine's slot in a direction: the receive pipeline's, or the transmit chain's for an engine transmit offers.</summary>
        private PcDenoiseSlot? SlotFor(DenoiseEngineEntry e, DenoiseDirection direction) =>
            direction == DenoiseDirection.Receive ? SlotFor(e)
                : OffersTransmit(e) && _txChain() is { } chain ? chain.SlotOf(e.Family!.Value) : null;

        /// <summary>The transmit engines in surface order, spectral first then neural; one family when asked.</summary>
        public IReadOnlyList<DenoiseEngineEntry> TransmitEngines(DenoiseFamily? family = null) =>
            _catalog.InSurfaceOrder().Where(OffersTransmit).Where(e => family == null || e.Family == family).ToList();

        /// <summary>Whether an engine is transmit's: the operator has its slot on with it chosen (the intent, which the chain adopts at the next unkeyed buffer).</summary>
        public bool TransmitIsOn(string id)
        {
            if (_catalog.Find(id) is not { } e || !OffersTransmit(e) || _txChain() is not { } chain) return false;
            var family = e.Family!.Value;
            return chain.WantsEnabled(family) && chain.ChosenEngineId(family) == id;
        }

        /// <summary>Whether a transmit slot is on (the operator's intent).</summary>
        public bool TransmitSlotOn(DenoiseFamily family) => _txChain()?.WantsEnabled(family) == true;

        /// <summary>The engine a transmit slot's field should show: a pending, unsettled choice first, else the chain's.</summary>
        public string TransmitSlotEngine(DenoiseFamily family) =>
            _txPendingChoice.TryGetValue(family, out var id) ? id : _txChain()?.ChosenEngineId(family) ?? "";

        /// <summary>An engine's name with its availability marker on transmit, as the transmit lists show it.</summary>
        public string TransmitMarkedName(string id)
        {
            var e = _catalog.Find(id);
            if (e == null) return id;
            string? marker = e.Factory == null ? Lexicon.Get("audio.noise_surface.marker.not_in_version")
                : TransmitAvailability(e) is var reason && reason != Unavailability.None ? Lexicon.Get(DenoiseEngineCatalog.UnavailabilityKey(reason)) : null;
            string name = NameOf(id);
            return string.IsNullOrEmpty(marker) ? name : name + ", " + char.ToLowerInvariant(marker[0]) + marker.Substring(1);
        }

        /// <summary>Whether an engine can run on transmit, judged on the model its TRANSMIT slot holds (an RNNoise model is per direction).</summary>
        private Unavailability TransmitAvailability(DenoiseEngineEntry e)
        {
            if (e.Settings.FirstOrDefault(d => d.Name == "model") is { Kind: EngineSettingKind.Choice } model
                && model.Options?.Any(o => o.Probe != null) == true)
            {
                string? chosen = SlotFor(e, DenoiseDirection.Transmit) is { } slot ? slot.GetSetting(e.Id, "model").Choice : model.Default.Choice;
                if (model.Options.FirstOrDefault(o => o.Name == chosen)?.Probe is { } probe) return SafeProbe(probe, e.Id + "." + chosen + " (transmit)");
            }
            try { return e.Availability(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("NoiseController: transmit probe for " + e.Id + " threw " + ex.Message); return Unavailability.InitFailed; }
        }

        // ────────────────────────────────────────────────────────────────
        //  Switching
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The one transmit switch (the layer's Ctrl letters after T, the Transmit
        /// menu's On rows): the receive rule exactly, on the transmit chain. On
        /// turns it off; another engine of the family switches to this one and
        /// says what it replaced; an off slot turns on. An engine transmit does
        /// not offer (a radio letter, a blanker, Captured noise) does nothing and
        /// says so.
        /// </summary>
        public void TransmitToggle(string id, string source)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            using var oneUtterance = OneUtterance();
            var e = _catalog.Find(id);
            if (e == null) return;
            if (!OffersTransmit(e))
            {
                Say(L("audio.noise_surface.tx.receive_only", ("engine", NameOf(id))), SpeechSubject.TransmitNoiseUnavailable);
                return;
            }
            // An engine this version cannot run is refused and never becomes the
            // arrows' engine (bench round, item 9): its levers and favourite would
            // then act on something that is not there.
            if (e.Factory != null) { Point(id, DenoiseDirection.Transmit); CurrentLeverIndex = 0; }
            if (TransmitIsOn(id)) TxTurnOff(e, source, speak: true);
            else TxTurnOn(e, source, speak: true, model: null, out _);
        }

        /// <summary>Turn a transmit engine on or off outright (a check item, Home's On box when its engine is chosen).</summary>
        public void TransmitSetOn(string id, bool on, string source)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            using var oneUtterance = OneUtterance();
            if (TransmitIsOn(id) == on)
            {
                Point(id, DenoiseDirection.Transmit);
                Say(L(on ? "audio.noise_surface.on" : "audio.noise_surface.off", ("engine", TxName(id))), SpeechSubject.TransmitNoiseEngineSwitch);
                return;
            }
            TransmitToggle(id, source);
        }

        /// <summary>
        /// Home's On box (repair round 1, item 1): it acts on the engine the
        /// fields SHOW. On turns that engine on (switching the slot to it now, so
        /// a settle still pending for the field cannot later put another engine
        /// back); off turns the slot off. The chain's previous choice and an
        /// unsettled one never decide which engine starts.
        /// </summary>
        public void TransmitSetShownOn(DenoiseFamily family, string shownId, bool on, string source)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            using var oneUtterance = OneUtterance();
            CancelTransmitSettle(family);
            if (!on) { TransmitSetSlotOn(family, false, source); return; }
            if (_catalog.Find(shownId) is not { } e || e.Family != family) return;
            TransmitSetOn(shownId, true, source);
        }

        /// <summary>A transmit slot on or off without naming an engine: on starts the slot's chosen engine.</summary>
        public void TransmitSetSlotOn(DenoiseFamily family, bool on, string source)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            using var oneUtterance = OneUtterance();
            if (_txChain() is not { } chain) return;
            CancelTransmitSettle(family);
            string id = chain.ChosenEngineId(family);
            var e = _catalog.Find(id);
            if (!on)
            {
                if (!chain.WantsEnabled(family)) return;
                if (e != null) TxTurnOff(e, source, speak: true);
                else SayAtUnkey(chain.SetEnabled(family, false));
                return;
            }
            if (chain.WantsEnabled(family) && e != null) { Say(L("audio.noise_surface.on", ("engine", TxName(id))), SpeechSubject.TransmitNoiseEngineSwitch); return; }
            if (e == null) return;
            Point(id, DenoiseDirection.Transmit);
            TxTurnOn(e, source, speak: true, model: null, out _);
        }

        /// <summary>A newer action on a slot wins over an engine field's unsettled choice for it: the settle is cancelled and the pending choice forgotten.</summary>
        private void CancelTransmitSettle(DenoiseFamily family)
        {
            if (_txSettling.TryGetValue(family, out var pending)) { pending.Dispose(); _txSettling.Remove(family); }
            _txPendingChoice.Remove(family);
        }

        private TxChangeResult TxTurnOff(DenoiseEngineEntry e, string source, bool speak)
        {
            if (_txChain() is not { } chain) return TxChangeResult.Now;
            CancelTransmitSettle(e.Family!.Value);
            var result = chain.SetEnabled(e.Family!.Value, false);
            NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), e.Id, "", "on", "off");
            if (!speak) return result;
            Say(L("audio.noise_surface.off", ("engine", TxName(e.Id))), SpeechSubject.TransmitNoiseEngineSwitch);
            SayAtUnkey(result);
            return result;
        }

        /// <summary>
        /// Turn a transmit engine on in its family's slot, replacing whatever
        /// engine of that family was on there. Returns the id it replaced, and in
        /// <paramref name="timing"/> what the chain answered (repair round 1,
        /// item 4: a caller that speaks for itself aggregates this, never the
        /// key state). An engine that cannot run is still chosen, as an intent,
        /// and says why.
        /// </summary>
        private string? TxTurnOn(DenoiseEngineEntry e, string source, bool speak, string? model, out TxChangeResult timing)
        {
            timing = TxChangeResult.Now;
            if (_txChain() is not { } chain) return null;
            var family = e.Family!.Value;
            CancelTransmitSettle(family);
            if (e.Factory == null)
            {
                if (speak) Say(L("audio.noise_surface.not_in_version", ("engine", TxName(e.Id))), SpeechSubject.TransmitNoiseUnavailable);
                return null;
            }
            string chosen = chain.ChosenEngineId(family);
            string? replaced = chain.WantsEnabled(family) && chosen != e.Id && chosen.Length > 0 ? chosen : null;
            if (model != null) timing = Later(timing, chain.SetSetting(e.Id, "model", new EngineSettingValue(0, model)));
            var choose = chain.ChooseEngine(family, e.Id);
            if (choose.Timing == TxChangeTiming.Refused)
            {
                if (speak) Say(L("audio.noise_surface.not_in_version", ("engine", TxName(e.Id))), SpeechSubject.TransmitNoiseUnavailable);
                return null;
            }
            var enable = chain.SetEnabled(family, true);
            timing = Later(timing, Later(choose, enable));
            if (replaced == null)
                NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), e.Id, "", "off", "on");
            else
                NoiseSettingsJournal.Record(new NoiseJournalEntry(NoiseJournalKind.Switch, TxSource(source), false, new[]
                {
                    new NoiseJournalChange(e.Id, "", "off", "on"),
                    new NoiseJournalChange(replaced, "", "on", "off"),
                }));
            if (speak)
            {
                var reason = e.Factory == null ? Unavailability.InitFailed : TransmitAvailability(e);
                if (reason != Unavailability.None) SpeakUnavailable(e, reason, kept: true, replaced: replaced, direction: DenoiseDirection.Transmit);
                else
                {
                    string sentence = replaced == null
                        ? L("audio.noise_surface.on", ("engine", TxName(e.Id)))
                        : L("audio.noise_surface.on_replacing", ("engine", TxName(e.Id)), ("other", TxName(replaced)));
                    if (TransmitDelayClause(e) is string delay) sentence += " " + delay;
                    Say(sentence, SpeechSubject.TransmitNoiseEngineSwitch);
                }
                SayAtUnkey(timing);
            }
            return replaced;
        }

        /// <summary>The later of two timings: a change waits if either part of it waits.</summary>
        private static TxChangeResult Later(TxChangeResult a, TxChangeResult b) =>
            a.Timing == TxChangeTiming.AtUnkey || b.Timing == TxChangeTiming.AtUnkey ? TxChangeResult.AtUnkey : TxChangeResult.Now;

        /// <summary>
        /// The chain's answer, spoken as it came (never second-guessed): a change
        /// made while keyed takes effect at the unkey, and the operator is told
        /// (Track H's placeholder, on its own subject). Inside an action it joins
        /// the action's one utterance, after the change's own sentence.
        /// </summary>
        private void SayAtUnkey(TxChangeResult result)
        {
            if (result.Timing == TxChangeTiming.AtUnkey)
                Say(Lexicon.Get("audio.denoise.tx.at_unkey"), SpeechSubject.TransmitNoiseChange);
        }

        /// <summary>
        /// The transmit delay clause (part seven's first bullet; "Say delays when
        /// choosing an engine", departure D3, governs it as it does receive's): on
        /// transmit EVERY engine says its delay at Chatty, in-process ones
        /// included, because delay matters more to a transmitted voice.
        /// </summary>
        private string? TransmitDelayClause(DenoiseEngineEntry e)
        {
            // Repair round 1, item 7: Terse gets the short form; Critical says no delay.
            if (_speaker.Verbosity < VerbosityLevel.Terse || _config()?.NoiseSayDelays == false) return null;
            if (!HasDelay(e.Id, out int? ms, DenoiseDirection.Transmit) || ms is not int value || value <= 0) return null;
            return L(Chatty ? "audio.noise_surface.tx.delay_clause" : "audio.noise_surface.tx.delay_clause_short", ("ms", value));
        }

        // ────────────────────────────────────────────────────────────────
        //  Choosing a slot's engine (Home's Engine field), with the settle
        // ────────────────────────────────────────────────────────────────

        private readonly Dictionary<DenoiseFamily, IDisposable> _txSettling = new();
        private readonly Dictionary<DenoiseFamily, string> _txPendingChoice = new();

        public void TransmitNoteSlotChoicePending(DenoiseFamily family, string id) => _txPendingChoice[family] = id;

        /// <summary>
        /// A transmit slot's engine chosen from a list the operator arrows
        /// through: applied only once the choice has settled for
        /// <see cref="EngineSettleMs"/> (#769), as on receive. On, it switches
        /// (said, with the delay); off, it is kept for when the slot turns on.
        /// </summary>
        public void TransmitChooseSlotEngine(DenoiseFamily family, string id, string source)
        {
            if (_txSettling.TryGetValue(family, out var pending)) { pending.Dispose(); _txSettling.Remove(family); }
            long epoch = _cursorEpoch;
            IDisposable? handle = null;
            handle = _scheduler.After(TimeSpan.FromMilliseconds(EngineSettleMs), () =>
            {
                // A newer action on this slot cancelled this settle; a timer that
                // fires anyway must not undo that action (repair round 1, item 1).
                if (!_txSettling.TryGetValue(family, out var mine) || !ReferenceEquals(mine, handle)) return;
                _txSettling.Remove(family);
                ApplyTransmitSlotChoice(family, id, source, epoch);
            });
            _txSettling[family] = handle;
        }

        private void ApplyTransmitSlotChoice(DenoiseFamily family, string id, string source, long epoch)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            using var oneUtterance = OneUtterance();
            _txPendingChoice.Remove(family);
            var e = _catalog.Find(id);
            if (_txChain() is not { } chain || e == null || !OffersTransmit(e) || e.Family != family || chain.ChosenEngineId(family) == id) return;
            PointLater(id, DenoiseDirection.Transmit, epoch);
            if (chain.WantsEnabled(family)) { TxTurnOn(e, source, speak: true, model: null, out _); return; }
            if (e.Factory == null)
            {
                Say(L("audio.noise_surface.not_in_version", ("engine", TxName(id))), SpeechSubject.TransmitNoiseUnavailable);
                return;
            }
            // Off: kept for when the slot turns on. Said only when it waits (item 4).
            SayAtUnkey(chain.ChooseEngine(family, id));
        }

        // ────────────────────────────────────────────────────────────────
        //  The 0 key on transmit
        // ────────────────────────────────────────────────────────────────

        private List<(DenoiseFamily Family, string EngineId)>? _txZeroRestore;

        /// <summary>The 0 key in the transmit direction: both transmit slots off, and back (question 17's reasoning, on the microphone).</summary>
        public void TransmitComputerOffAndBack(string source)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            using var oneUtterance = OneUtterance();
            if (_txChain() is not { } chain) return;
            var on = new List<(DenoiseFamily, string)>();
            foreach (var family in new[] { DenoiseFamily.Spectral, DenoiseFamily.Neural })
                if (chain.WantsEnabled(family)) on.Add((family, chain.ChosenEngineId(family)));
            var timing = TxChangeResult.Now;
            if (on.Count > 0)
            {
                foreach (var (family, id) in on)
                {
                    CancelTransmitSettle(family);
                    timing = Later(timing, chain.SetEnabled(family, false));
                    NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), id, "", "on", "off");
                }
                _txZeroRestore = on;
                Say(L("audio.noise_surface.tx.zero_off", ("engines", JoinNames(on.Select(x => x.Item2)))), SpeechSubject.TransmitNoiseEngineSwitch);
                SayAtUnkey(timing);
                return;
            }
            if (_txZeroRestore is { Count: > 0 } back)
            {
                foreach (var (family, id) in back)
                {
                    CancelTransmitSettle(family);
                    if (chain.ChosenEngineId(family) != id) timing = Later(timing, chain.ChooseEngine(family, id));
                    timing = Later(timing, chain.SetEnabled(family, true));
                    NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), id, "", "off", "on");
                }
                _txZeroRestore = null;
                Say(L("audio.noise_surface.tx.zero_on", ("engines", JoinNames(back.Select(x => x.EngineId)))), SpeechSubject.TransmitNoiseEngineSwitch);
                SayAtUnkey(timing);
                return;
            }
            Say(L("audio.noise_surface.tx.zero_already"), SpeechSubject.TransmitNoiseEngineSwitch);
        }

        // ────────────────────────────────────────────────────────────────
        //  Levers (the lever carries its direction; Levers.cs routes here)
        // ────────────────────────────────────────────────────────────────

        private void MoveTransmit(NoiseLever lever, DenoiseEngineEntry e, string source, Func<EngineSettingValue, EngineSettingValue?> to, int direction)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            if (!OffersTransmit(e) || _txChain() is null)
            {
                Say(L("audio.noise_surface.tx.receive_only", ("engine", NameOf(e.Id))), SpeechSubject.TransmitNoiseUnavailable);
                return;
            }
            var before = Raw(lever);
            var after = to(before);
            if (after == null)
            {
                string end = direction > 0 ? "audio.noise_surface.lever_most" : "audio.noise_surface.lever_least";
                Say(L(end, ("lever", LeverName(lever)), ("value", ValueText(lever))), SpeechSubject.TransmitNoiseLevel);
                return;
            }
            var result = WriteTransmit(lever, e, after.Value, source);
            if (result.Timing == TxChangeTiming.Refused)
            {
                Say(L("audio.noise_surface.lever_refused", ("lever", LeverName(lever))), SpeechSubject.TransmitNoiseLevel);
                return;
            }
            // A value held for the unkey is not yet what the getters read: say the
            // value asked for, then that it waits (repair round 1, part 2).
            SpeakTransmitLever(lever, e, result.Timing == TxChangeTiming.AtUnkey ? HeldValueText(lever, after.Value) : null);
            SayAtUnkey(result);
        }

        /// <summary>A lever's value as spoken, from the value written rather than read back (a held write).</summary>
        private string HeldValueText(NoiseLever lever, EngineSettingValue value)
        {
            if (lever.Descriptor is { Kind: EngineSettingKind.Choice } d)
                return d.Options?.FirstOrDefault(o => o.Name == value.Choice) is { } option ? OptionText(option) : value.Choice ?? "";
            double shown = lever.Kind == NoiseLeverKind.Floor ? value.Number : ToShown(lever, value.Number);
            string number = shown.ToString(lever.Descriptor is { Step: < 0.1 } x && !IsPercent(x) ? "0.###" : "0.#", CultureInfo.InvariantCulture);
            return WithUnit(number, UnitOf(lever));
        }

        /// <summary>One transmit lever's write, through the chain's intent calls: the floor and every in-place setting live, a rebuild setting at the unkey when keyed.</summary>
        private TxChangeResult WriteTransmit(NoiseLever lever, DenoiseEngineEntry e, EngineSettingValue value, string source)
        {
            var chain = _txChain()!;
            switch (lever.Kind)
            {
                case NoiseLeverKind.Floor:
                {
                    float was = chain.GetStrength(e.Id);
                    float want = NoiseFloor.ToStrength(value.Number);
                    // The chain answers when it takes effect (repair round 1, part 2):
                    // live, or held behind something already waiting. Spoken as given.
                    var timing = chain.SetStrength(e.Id, want);
                    if (timing.Timing != TxChangeTiming.Refused)
                        NoiseSettingsJournal.Record(NoiseJournalKind.Floor, TxSource(source), e.Id, FloorKey, FloorText(was), FloorText(want));
                    return timing;
                }
                case NoiseLeverKind.Setting:
                {
                    var d = lever.Descriptor!;
                    var slot = chain.SlotFor(e.Id)!;
                    var was = slot.GetSetting(e.Id, d.Name);
                    var result = chain.SetSetting(e.Id, d.Name, value);
                    if (result.Timing != TxChangeTiming.Refused)
                        NoiseSettingsJournal.Record(d.Name == "model" ? NoiseJournalKind.Model : NoiseJournalKind.Lever, TxSource(source), e.Id, d.Name,
                            Describe(was), Describe(value));
                    return result;
                }
                default:
                    return new TxChangeResult(TxChangeTiming.Refused, "not-a-transmit-lever");
            }
        }

        private void SpeakTransmitLever(NoiseLever lever, DenoiseEngineEntry e, string? heldValue = null)
        {
            string name = TxName(e.Id), leverName = LeverName(lever), value = heldValue ?? ValueText(lever);
            bool on = TransmitIsOn(e.Id);
            if (lever.Key == "model" && ChosenOption(lever)?.Probe is { } probe && SafeProbe(probe, e.Id) is var reason && reason != Unavailability.None)
            {
                SpeakUnavailable(e, reason, kept: true, direction: DenoiseDirection.Transmit);
                return;
            }
            string key;
            // The transmit twin (words round, Codex's finding): transmit never
            // cross-fades between engines, so receive's model sentence would promise
            // something that does not happen.
            if (lever.Key == "model") key = on ? "audio.noise_surface.tx.lever_model" : "audio.noise_surface.lever_choice_off";
            else if (lever.Descriptor is { Kind: EngineSettingKind.Choice }) key = on ? "audio.noise_surface.lever_choice" : "audio.noise_surface.lever_choice_off";
            else key = on ? "audio.noise_surface.lever" : "audio.noise_surface.lever_off";
            Say(L(key, ("engine", name), ("lever", leverName), ("lever_lower", leverName.ToLowerInvariant()), ("value", value)), SpeechSubject.TransmitNoiseLevel);
        }

        /// <summary>Whether an engine's Advanced levers are revealed in a direction (remembered per engine and direction).</summary>
        public bool IsAdvanced(string id, DenoiseDirection direction) =>
            direction == DenoiseDirection.Receive ? IsAdvanced(id) : AdvancedShown(TxAdvancedKey(id));

        /// <summary>Reveal or hide an engine's transmit Advanced levers on Home, remember it, and say so.</summary>
        public void SetAdvanced(string id, bool on, DenoiseDirection direction)
        {
            if (direction == DenoiseDirection.Receive) { SetAdvanced(id, on); return; }
            ShowAdvanced(TxAdvancedKey(id), on);
            Say(on ? L("audio.noise_surface.advanced_on", ("engine", TxName(id))) : L("audio.noise_surface.advanced_off"), SpeechSubject.TransmitNoiseLevel);
        }

        private static string TxAdvancedKey(string id) => "tx:" + id;

        // ────────────────────────────────────────────────────────────────
        //  Return to defaults on transmit
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A transmit engine back to its defaults: its floor to the transmit
        /// default (<see cref="NoiseFloor.DefaultStrength"/>, RNNoise 20, every
        /// other engine its receive floor), every setting to the descriptor's
        /// default, on or off left as it was. A rebuild setting waits for the
        /// unkey, and is said to.
        /// </summary>
        public void TransmitReturnToDefaults(string id, string source)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            using var oneUtterance = OneUtterance();
            var e = _catalog.Find(id);
            if (e == null) return;
            if (!OffersTransmit(e) || _txChain() is not { } chain)
            {
                Say(L("audio.noise_surface.tx.receive_only", ("engine", NameOf(id))), SpeechSubject.TransmitNoiseUnavailable);
                return;
            }
            Point(id, DenoiseDirection.Transmit);
            var slot = chain.SlotFor(id)!;
            var changes = new List<NoiseJournalChange>();
            float strength = NoiseFloor.DefaultStrength(id, DenoiseDirection.Transmit);
            if (Math.Abs(chain.GetStrength(id) - strength) > 0.0001)
                changes.Add(new NoiseJournalChange(id, FloorKey, FloorText(chain.GetStrength(id)), FloorText(strength)));
            var timing = chain.SetStrength(id, strength);
            foreach (var d in e.Settings)
            {
                var was = slot.GetSetting(id, d.Name);
                if (Same(was, d.Default)) continue;
                var result = chain.SetSetting(id, d.Name, d.Default);
                if (result.Timing == TxChangeTiming.Refused) continue;
                timing = Later(timing, result);
                changes.Add(new NoiseJournalChange(id, d.Name, Describe(was), Describe(d.Default)));
            }
            NoiseSettingsJournal.Record(new NoiseJournalEntry(NoiseJournalKind.ReturnToDefaults, TxSource(source), false, changes, NameOf(id)));
            // The defaults ASKED FOR (repair round 2, item 2): a held part has not
            // reached the getters yet, so reading them back named the old values.
            string DefaultText(NoiseLever l) => HeldValueText(l, l.Kind == NoiseLeverKind.Floor
                ? new EngineSettingValue(NoiseFloor.DefaultFloor(id, DenoiseDirection.Transmit)) : l.Descriptor!.Default);
            var said = Levers(id, includeAdvanced: false, DenoiseDirection.Transmit).Select(l => LeverName(l).ToLowerInvariant() + " " + DefaultText(l)).ToList();
            if (FloorLeverOf(id, DenoiseDirection.Transmit) is { Advanced: true } advancedFloor)
                said.Add(L("audio.noise_surface.defaults_floor", ("value", DefaultText(advancedFloor))));
            Say(L("audio.noise_surface.defaults", ("engine", TxName(id)), ("values", string.Join(", ", said))), SpeechSubject.TransmitNoiseDefaults);
            SayAtUnkey(timing);
        }

        // ────────────────────────────────────────────────────────────────
        //  Say what transmit cleanup is active
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The transmit summary (the Transmit menu's first row, Home's Active
        /// readout in the Transmission expander, the layer's Space after T): each
        /// slot that is on with its engine and main levers, then the gate. In CW
        /// and digital the whole chain is bypassed (part seven's mode policy),
        /// and it says so rather than naming engines that are not running.
        /// </summary>
        public string TransmitActiveSummary()
        {
            var items = new List<string>();
            bool offAtUnkey = false;
            // Bypassed by the TRANSMIT slice's mode (slice-mode round): the engines
            // are named as chosen, never "not running yet", and the bypass is said.
            bool bypassed = _radio() is { } tr && TxAudioConditioning.BypassedIn(tr.TransmitMode);
            var chain = _txChain();
            if (chain != null)
                foreach (var family in new[] { DenoiseFamily.Spectral, DenoiseFamily.Neural })
                {
                    if (!chain.WantsEnabled(family))
                    {
                        // Switched off while keyed, still running to the unkey
                        // (bench round, item 7): named as on, with the held clause.
                        var running = chain.SlotOf(family);
                        if (chain.IsKeyed && running.Enabled && _catalog.Find(running.RunningEngineId) is { } runningEntry)
                        {
                            items.Add(Lexicon.Get(runningEntry.LabelKey));
                            offAtUnkey = true;
                        }
                        continue;
                    }
                    var e = _catalog.Find(chain.ChosenEngineId(family));
                    if (e == null) continue;
                    string name = Lexicon.Get(e.LabelKey);
                    var normal = Levers(e.Id, includeAdvanced: false, DenoiseDirection.Transmit);
                    var details = normal.Select(l => l.Descriptor is { Kind: EngineSettingKind.Choice } ? ValueText(l) : LeverName(l).ToLowerInvariant() + " " + ValueText(l)).ToList();
                    string item = details.Count == 0 || !Chatty ? name : name + ", " + string.Join(", ", details);
                    // Chosen is not running (repair round 1, item 9; receive's e3
                    // distinction): never name an engine as working when it is
                    // not. Which reason, in order: it cannot run here; the change
                    // is waiting for the unkey; it has not processed audio yet
                    // (still building, no microphone audio, no radio).
                    var slot = chain.SlotOf(family);
                    var reason = e.Factory == null ? Unavailability.InitFailed : TransmitAvailability(e);
                    if (reason != Unavailability.None)
                        item = L("audio.noise_surface.tx.active.cannot_run", ("engine", name));
                    else if (chain.IsKeyed && (chain.HasDeferredChanges || !slot.Enabled || !slot.RunningIsCurrent(e.Id)))
                        item = L("audio.noise_surface.tx.active.at_unkey", ("engine", name));
                    else if (!bypassed && slot.ProcessingEngineId != e.Id)
                        item = L("audio.noise_surface.active.waiting", ("engine", name));
                    items.Add(item);
                }
            var gate = TransmitGate;
            if (gate.Enabled)
                items.Add(gate.AutoThreshold
                    ? L("audio.noise_surface.tx.active.gate_auto", ("margin", GateValueText(GateLever(TxGateLever.Margin)!)))
                    : L("audio.noise_surface.tx.active.gate_fixed", ("threshold", GateValueText(GateLever(TxGateLever.Threshold)!))));
            // All off now, with a whole change still held: say both (repair round 2, item 5).
            if (items.Count == 0)
                return chain != null && chain.HasDeferredChanges
                    ? L("audio.noise_surface.tx.active.none") + " " + L("audio.noise_surface.tx.active.held")
                    : L("audio.noise_surface.tx.active.none");
            string summary = L("audio.noise_surface.tx.active", ("list", string.Join("; ", items)));
            if (chain != null && (chain.HasDeferredChanges || offAtUnkey))
                summary += " " + L("audio.noise_surface.tx.active.held");
            if (bypassed)
                summary += " " + L("audio.noise_surface.tx.active.bypassed");
            return summary;
        }

        public void SpeakTransmitActive() => Say(TransmitActiveSummary(), SpeechSubject.TransmitNoiseActiveSummary);

        /// <summary>Space in the layer: what is active in the layer's direction.</summary>
        public void SpeakActiveInLayerDirection()
        {
            if (LayerDirection == DenoiseDirection.Transmit) SpeakTransmitActive();
            else SpeakActive();
        }

        // ────────────────────────────────────────────────────────────────
        //  The brackets on transmit: one favourites list, stepped in transmit
        // ────────────────────────────────────────────────────────────────

        private int _txStepIndex = -1;

        /// <summary>
        /// The list the brackets walk in the transmit direction: the favourites
        /// transmit offers (one list, ruled; a radio engine, a blanker or Captured
        /// noise is skipped), or every transmit engine when none of them is.
        /// </summary>
        public IReadOnlyList<NoiseFavourite> TransmitStepList()
        {
            // An engine this version cannot run is skipped, not stopped on and
            // refused (bench round, item 9).
            var offered = Favourites.Where(f => _catalog.Find(f.EngineId) is { Factory: not null } e && OffersTransmit(e)).ToList();
            return offered.Count > 0 ? offered : TransmitEngines().Where(e => e.Factory != null).Select(e => new NoiseFavourite { EngineId = e.Id }).ToList();
        }

        /// <summary>
        /// ] and [ after T: turn the next (or previous) transmit favourite on and
        /// the one stepped from off (every transmit engine is a reducer, so the
        /// same-subject rule always turns it off, a clean A against B). Two
        /// favourites of one engine change its model in place.
        /// </summary>
        public void TransmitStep(int direction, string source)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            using var oneUtterance = OneUtterance();
            var list = TransmitStepList();
            if (list.Count == 0 || _txChain() is not { } chain) return;
            if (Favourites.Count == 0 && !_toldNoFavourites)
            {
                _toldNoFavourites = true;
                Say(Lexicon.Get("audio.noise_surface.no_favourites", VerbosityLevel.Chatty), SpeechSubject.NoiseFavourite, VerbosityLevel.Chatty);
            }
            if (_txStepIndex < 0 || _txStepIndex >= list.Count)
                _txStepIndex = CurrentInLayerDirection is not string at ? -1 : list.ToList().FindIndex(f => f.EngineId == at);
            NoiseFavourite? from = _txStepIndex >= 0 && _txStepIndex < list.Count ? list[_txStepIndex] : null;
            int next = _txStepIndex < 0 ? (direction > 0 ? 0 : list.Count - 1) : ((_txStepIndex + Math.Sign(direction)) % list.Count + list.Count) % list.Count;
            _txStepIndex = next;
            var to = list[next];
            var e = _catalog.Find(to.EngineId);
            if (e == null) return;
            Point(e.Id, DenoiseDirection.Transmit); CurrentLeverIndex = 0;
            if (e.Factory == null)
            {
                Say(L("audio.noise_surface.not_in_version", ("engine", TxName(e.Id))), SpeechSubject.TransmitNoiseUnavailable);
                return;
            }
            // Repair round 1, item 4: every part's returned timing, aggregated;
            // the key state is never sampled again afterwards.
            var timing = TxChangeResult.Now;
            string sentence;
            if (from != null && from.EngineId == to.EngineId)
            {
                if (to.Model != null) timing = Later(timing, chain.SetSetting(e.Id, "model", new EngineSettingValue(0, to.Model)));
                if (!TransmitIsOn(e.Id)) { TxTurnOn(e, source, speak: false, model: null, out var on); timing = Later(timing, on); }
                sentence = L("audio.noise_surface.on", ("engine", TxName(e.Id)));
            }
            else
            {
                var fromEntry = from == null ? null : _catalog.Find(from.EngineId);
                bool turnOffFrom = fromEntry != null && TransmitIsOn(fromEntry.Id);
                string? replaced = null;
                if (!TransmitIsOn(e.Id)) { replaced = TxTurnOn(e, source, speak: false, model: to.Model, out var on); timing = Later(timing, on); }
                if (turnOffFrom && fromEntry!.Id != replaced && fromEntry.Id != e.Id) timing = Later(timing, TxTurnOff(fromEntry, source, speak: false));
                string? offId = turnOffFrom ? fromEntry!.Id : replaced;
                sentence = offId == null ? L("audio.noise_surface.on", ("engine", TxName(e.Id)))
                    : L("audio.noise_surface.on_replacing", ("engine", TxName(e.Id)), ("other", TxName(offId)));
            }
            // Item 7: a choosing road says the delay too.
            var reason = TransmitAvailability(e);
            if (reason != Unavailability.None) SpeakUnavailable(e, reason, kept: true, direction: DenoiseDirection.Transmit);
            else
            {
                if (TransmitDelayClause(e) is string delay) sentence += " " + delay;
                Say(sentence, SpeechSubject.TransmitNoiseEngineSwitch);
            }
            SayAtUnkey(timing);
        }

        /// <summary>The engine favourite key in the layer's direction: the favourite is the engine in the model its own direction's slot holds.</summary>
        public string? CurrentModel(string id, DenoiseDirection direction)
        {
            if (direction == DenoiseDirection.Receive) return CurrentModel(id);
            var e = _catalog.Find(id);
            if (e == null || !e.Settings.Any(d => d.Name == "model")) return null;
            if (SlotFor(e, direction) is { } slot) return slot.GetSetting(id, "model").Choice;
            return e.Settings.First(d => d.Name == "model").Default.Choice;
        }

        /// <summary>Whether an engine, in the model its slot in <paramref name="direction"/> holds, is a favourite.</summary>
        public bool IsFavourite(string id, DenoiseDirection direction) => Favourites.Any(f => f.SameAs(id, CurrentModel(id, direction)));

        /// <summary>The engine a preset's transmit section mainly belongs to: its neural slot's engine if on, else its spectral slot's.</summary>
        public static string? TransmitMainEngineOf(AudioChainPreset preset)
        {
            var t = preset.TransmitNoise;
            if (t == null) return null;
            if (t.Neural is { Enabled: true, EngineId.Length: > 0 }) return t.Neural.EngineId;
            if (t.Spectral is { Enabled: true, EngineId.Length: > 0 }) return t.Spectral.EngineId;
            return null;
        }

        /// <summary>The presets whose transmit section mainly belongs to this engine (a transmit engine's Presets submenu).</summary>
        public IReadOnlyList<AudioChainPreset> TransmitPresetsFor(string engineId) =>
            TransmitNoisePresets().Where(p => TransmitMainEngineOf(p) == engineId).ToList();

        // ────────────────────────────────────────────────────────────────
        //  The gate
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The expander's strength stops, gentlest first; the last is the gate
        /// (one item, Noel 2026-11-23 11:32). 2:1 is a new expander's start (S7).
        /// </summary>
        // Declared BEFORE AllGateLevers on purpose: static fields initialise in
        // textual order, and the lever table reads this one's length.
        public static readonly (float Ratio, bool Gate)[] StrengthStops =
        {
            (1.5f, false), (2f, false), (3f, false), (4f, false), (6f, false), (10f, false), (0f, true),
        };

        /// <summary>
        /// The strength lever's stops in WDSP's mode (phase 2): its depth, dB,
        /// gentlest first. DEXP has no ratio, only a fixed depth, so in that mode
        /// the ONE strength lever means depth, as the gate's range does, and says
        /// so; as many stops as the ratio's, so the lever's range is the same.
        /// 20 dB is the helper's own default.
        /// </summary>
        public static readonly float[] WdspDepthStops = { 6f, 10f, 15f, 20f, 25f, 30f, 40f };

        /// <summary>Every gate lever, in the order Home lists them: Margin is normal; the rest are Advanced (part seven).</summary>
        private static readonly TxGateLever[] AllGateLevers =
        {
            new(TxGateLever.Margin, "audio.noise_surface.tx.gate.margin", 3, 15, 1, "audio.unit.decibels", false),
            // Sprint 49 Track I: the expander's strength, normal (it is the stage's
            // main lever); its value is a stop's index, see StrengthStops.
            new(TxGateLever.Strength, "audio.noise_surface.tx.gate.strength", 0, StrengthStops.Length - 1, 1, null, false),
            new(TxGateLever.Threshold, "audio.noise_surface.tx.gate.threshold", -80, -10, 1, "audio.unit.decibels", true),
            new(TxGateLever.Attack, "audio.noise_surface.tx.gate.attack", 1, 20, 1, "audio.unit.milliseconds", true),
            new(TxGateLever.Hold, "audio.noise_surface.tx.gate.hold", 50, 1000, 25, "audio.unit.milliseconds", true),
            new(TxGateLever.Release, "audio.noise_surface.tx.gate.release", 50, 1000, 25, "audio.unit.milliseconds", true),
            new(TxGateLever.Depth, "audio.noise_surface.tx.gate.depth", 6, 40, 1, "audio.unit.decibels", true),
            new(TxGateLever.SideChainLow, "audio.noise_surface.tx.gate.sidechain_low", 50, 500, 25, "audio.unit.hertz", true),
            new(TxGateLever.SideChainHigh, "audio.noise_surface.tx.gate.sidechain_high", 2000, 8000, 250, "audio.unit.hertz", true),
            new(TxGateLever.LookAhead, "audio.noise_surface.tx.gate.lookahead", 0, 10, 1, "audio.unit.milliseconds", true),
            new(TxGateLever.Mode, "audio.noise_surface.tx.gate.mode", 0, 1, 1, null, true),
            new(TxGateLever.MonitorVolume, "audio.noise_surface.tx.gate.monitor_volume", 0, 100, 5, "audio.noise_surface.unit.percent", true),
        };

        /// <summary>The stop nearest what the expander holds now.</summary>
        private int StrengthIndex()
        {
            var g = TransmitGate;
            if (ExpanderModes.IsWdsp(g.ExpanderMode)) return NearestDepthStop(g.WdspDepthDb);
            if (g.GateSetting) return StrengthStops.Length - 1;
            int best = 0;
            for (int i = 1; i < StrengthStops.Length - 1; i++)
                if (Math.Abs(StrengthStops[i].Ratio - g.RatioToOne) < Math.Abs(StrengthStops[best].Ratio - g.RatioToOne)) best = i;
            return best;
        }

        /// <summary>The WDSP depth stop nearest a depth.</summary>
        private static int NearestDepthStop(float db)
        {
            int best = 0;
            for (int i = 1; i < WdspDepthStops.Length; i++)
                if (Math.Abs(WdspDepthStops[i] - db) < Math.Abs(WdspDepthStops[best] - db)) best = i;
            return best;
        }

        /// <summary>A strength stop as said in a mode: in WDSP's, its depth (DRAFT PROSE key); otherwise <see cref="StrengthText(int)"/>.</summary>
        private static string StrengthText(int index, bool wdsp) => wdsp
            ? Lexicon.Get("audio.noise_surface.tx.gate.strength_depth",
                ("depth", WdspDepthStops[Math.Clamp(index, 0, WdspDepthStops.Length - 1)].ToString("0", CultureInfo.InvariantCulture)))
            : StrengthText(index);

        /// <summary>The mode lever's value as said: JJ Flexible's, or WDSP's, and when WDSP's cannot run here, why and what to do (listed, never hidden).</summary>
        private string ModeText(int value)
        {
            if (value == 0) return Lexicon.Get("audio.noise_surface.tx.gate.mode_jj");
            var reason = TransmitGate.WdspAvailability;
            return reason == Unavailability.None ? Lexicon.Get("audio.noise_surface.tx.gate.mode_wdsp")
                : Lexicon.Get("audio.noise_surface.tx.gate.mode_wdsp_unavailable", ("reason", Lexicon.Get(DenoiseEngineCatalog.UnavailabilityKey(reason))));
        }

        /// <summary>A strength stop as said: "2 to 1", or the gate (DRAFT PROSE keys).</summary>
        private static string StrengthText(int index)
        {
            var stop = StrengthStops[Math.Clamp(index, 0, StrengthStops.Length - 1)];
            return stop.Gate ? Lexicon.Get("audio.noise_surface.tx.gate.strength_gate")
                : Lexicon.Get("audio.noise_surface.tx.gate.strength_ratio", ("ratio", stop.Ratio.ToString("0.#", CultureInfo.InvariantCulture)));
        }

        /// <summary>A gate lever by its key, or null.</summary>
        public TxGateLever? GateLever(string key) => AllGateLevers.FirstOrDefault(l => l.Key == key);

        /// <summary>The gate's levers: Margin, and with Advanced the hand threshold (only while the automatic threshold is off), attack, hold, release and depth. The cleanup monitor's volume is its own field.</summary>
        public IReadOnlyList<TxGateLever> GateLevers(bool includeAdvanced) =>
            AllGateLevers.Where(l => l.Key != TxGateLever.MonitorVolume && (!l.Advanced || includeAdvanced)
                && !(l.Key == TxGateLever.Threshold && TransmitGate.AutoThreshold)).ToList();

        public string GateLeverName(TxGateLever lever) => Lexicon.Get(lever.LabelKey);

        /// <summary>A gate lever's value on its own scale (decibels, milliseconds, percent).</summary>
        public double GateNumber(TxGateLever lever)
        {
            var g = TransmitGate;
            return lever.Key switch
            {
                TxGateLever.Margin => Math.Round(g.MarginDb),
                TxGateLever.Threshold => Math.Round(g.ThresholdDb),
                TxGateLever.Attack => Math.Round(g.AttackMs),
                TxGateLever.Hold => Math.Round(g.HoldMs),
                TxGateLever.Release => Math.Round(g.ReleaseMs),
                TxGateLever.Depth => Math.Round(g.RangeDb),
                TxGateLever.MonitorVolume => Math.Round(g.MonitorVolume * 100),
                TxGateLever.Strength => StrengthIndex(),
                TxGateLever.Mode => ExpanderModes.IsWdsp(g.ExpanderMode) ? 1 : 0,
                TxGateLever.SideChainLow => Math.Round(g.SideChainLowHz),
                TxGateLever.SideChainHigh => Math.Round(g.SideChainHighHz),
                TxGateLever.LookAhead => Math.Round(g.LookAheadMs),
                _ => 0,
            };
        }

        /// <summary>One gate lever written, with the gate's own answer about when (repair round 2, item 2). The monitor's volume is the listening tool's, live.</summary>
        private TxChangeResult WriteGate(TxGateLever lever, double value)
        {
            var g = TransmitGate;
            float v = (float)value;
            if (lever.Key == TxGateLever.MonitorVolume) { g.MonitorVolume = v / 100f; return TxChangeResult.Now; }
            if (lever.Key == TxGateLever.Mode)
                return g.SetExpanderMode(Math.Round(value) >= 1 ? ExpanderModes.Wdsp : ExpanderModes.Jj);
            if (lever.Key == TxGateLever.Strength && ExpanderModes.IsWdsp(g.ExpanderMode))
                // WDSP's mode: the same lever is its depth (its own setting, kept apart from the ratio).
                return g.SetWdspDepth(WdspDepthStops[Math.Clamp((int)Math.Round(value), 0, WdspDepthStops.Length - 1)]);
            if (lever.Key == TxGateLever.Strength)
            {
                var stop = StrengthStops[Math.Clamp((int)Math.Round(value), 0, StrengthStops.Length - 1)];
                // The ratio is kept when the gate is chosen, so stepping back down returns to it.
                return g.SetStrength(stop.Gate ? g.RatioToOne : stop.Ratio, stop.Gate);
            }
            return g.SetLevel(lever.Key, v);
        }

        /// <summary>A gate lever's value as spoken from a value asked for (a held write reads the old one back).</summary>
        private string GateValueTextOf(TxGateLever lever, double value) =>
            lever.Key == TxGateLever.Strength ? StrengthText((int)Math.Round(value), ExpanderModes.IsWdsp(TransmitGate.ExpanderMode))
                : lever.Key == TxGateLever.Mode ? ModeText((int)Math.Round(value))
                : WithUnit(Math.Round(value).ToString("0", CultureInfo.InvariantCulture), lever.UnitKey);

        /// <summary>A gate lever's value as spoken, its unit at Chatty.</summary>
        public string GateValueText(TxGateLever lever) => GateValueTextOf(lever, GateNumber(lever));

        /// <summary>Every value a gate lever takes, least to most, with its unit: a Home field arrows through exactly what the menu and the layer step.</summary>
        public IReadOnlyList<(double Value, string Text)> GateSteps(TxGateLever lever)
        {
            var list = new List<(double, string)>();
            if (lever.Key == TxGateLever.Strength)
            {
                bool wdsp = ExpanderModes.IsWdsp(TransmitGate.ExpanderMode);
                for (int i = 0; i < StrengthStops.Length; i++) list.Add((i, StrengthText(i, wdsp)));
                return list;
            }
            if (lever.Key == TxGateLever.Mode)
            {
                list.Add((0, ModeText(0)));
                list.Add((1, ModeText(1)));
                return list;
            }
            for (double v = lever.Min; v <= lever.Max + 1e-9; v += lever.Step)
                list.Add((v, v.ToString("0", CultureInfo.InvariantCulture) + (lever.UnitKey is string u ? " " + Lexicon.Get(u) : "")));
            return list;
        }

        /// <summary>The gate on or off, speaking the timing the gate answers with (at the unkey while keyed, after Track H's repair round 1).</summary>
        public void SetGateOn(bool on, string source)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            using var oneUtterance = OneUtterance();
            var g = TransmitGate;
            bool was = g.Enabled;
            // The layer acts on what it reads (Escape ownership round 2): asking it
            // for the state the gate already has changes nothing, so nothing is
            // held and its Escape has nothing of this to withdraw or report.
            var timing = LayerNoOp(source, was == on) ? TxChangeResult.Now : g.SetEnabled(on);
            if (was != on) NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), "gate", "", Words(was), Words(on));
            Say(L(on ? "audio.noise_surface.tx.gate_on" : "audio.noise_surface.tx.gate_off"), SpeechSubject.TransmitNoiseGate);
            SayAtUnkey(timing);
        }

        public void ToggleGate(string source) => SetGateOn(!TransmitGate.Enabled, source);

        /// <summary>A layer request for the value it already reads: no write, so Escape has no change of it to withdraw (Escape ownership round 2). Other roads keep their writes.</summary>
        private static bool LayerNoOp(string source, bool same) => same && source == LayerSource;

        /// <summary>The automatic threshold on or off: on, the gate opens a margin above the measured room floor; off, at the threshold set by hand.</summary>
        public void SetGateAutoThreshold(bool on, string source)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            using var oneUtterance = OneUtterance();
            var g = TransmitGate;
            bool was = g.AutoThreshold;
            // In order behind anything held (repair round 2, item 1), and said with
            // its timing; nothing at all when the layer asks for what it reads (round 2).
            var timing = LayerNoOp(source, was == on) ? TxChangeResult.Now : g.SetAutoThreshold(on);
            if (was != on || timing.Timing == TxChangeTiming.AtUnkey)
                NoiseSettingsJournal.Record(NoiseJournalKind.Lever, TxSource(source), "gate", "auto_threshold", Words(was), Words(on));
            Say(L(on ? "audio.noise_surface.tx.gate_auto_on" : "audio.noise_surface.tx.gate_auto_off"), SpeechSubject.TransmitNoiseGate);
            SayAtUnkey(timing);
        }

        /// <summary>A gate lever one step up or down; at an end it stays and says so.</summary>
        public void AdjustGate(TxGateLever lever, int direction, string source)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            double now = GateNumber(lever);
            double next = Math.Clamp(now + lever.Step * Math.Sign(direction), lever.Min, lever.Max);
            if (next == now)
            {
                using var oneUtterance = OneUtterance();
                Say(L(direction > 0 ? "audio.noise_surface.lever_most" : "audio.noise_surface.lever_least",
                    ("lever", GateLeverName(lever)), ("value", GateValueText(lever))), SpeechSubject.TransmitNoiseGate);
                return;
            }
            SetGate(lever, next, source);
        }

        /// <summary>Set a gate lever (a Home field's value) and say it.</summary>
        public void SetGate(TxGateLever lever, double value, string source)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            using var oneUtterance = OneUtterance();
            double was = GateNumber(lever);
            double asked = Math.Clamp(value, lever.Min, lever.Max);
            var timing = WriteGate(lever, asked);
            if (timing.Timing == TxChangeTiming.Refused)
            {
                Say(L("audio.noise_surface.lever_refused", ("lever", GateLeverName(lever))), SpeechSubject.TransmitNoiseGate);
                return;
            }
            NoiseSettingsJournal.Record(NoiseJournalKind.Lever, TxSource(source), "gate", lever.Key,
                was.ToString(CultureInfo.InvariantCulture), asked.ToString(CultureInfo.InvariantCulture));
            // The value asked for, and when it takes effect: a held write is not yet what the getter reads.
            Say(L("audio.noise_surface.tx.gate_lever", ("lever", GateLeverName(lever)), ("value", GateValueTextOf(lever, asked))), SpeechSubject.TransmitNoiseGate);
            SayAtUnkey(timing);
        }

        /// <summary>
        /// The gate's Return to defaults (the Audio Workshop's "Reset cleanup to
        /// recommended", moved here as part seven rules): every gate setting to
        /// the shipped values, the threshold derived from the room again, the
        /// gate left on or off as it was. The engines are untouched; each has
        /// its own Return to defaults.
        /// </summary>
        public void GateReturnToDefaults(string source)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            using var oneUtterance = OneUtterance();
            var g = TransmitGate;
            var fresh = new TxNoiseGateSetup { Enabled = g.Enabled };
            var timing = ApplyWholeTransmit(null, null, fresh);
            NoiseSettingsJournal.Record(NoiseJournalKind.ReturnToDefaults, TxSource(source), "gate", "", "", "defaults");
            // The defaults asked for, not the getters, which a held change has not reached yet (item 2).
            var said = AllGateLevers.Where(l => !l.Advanced).Select(l => GateLeverName(l).ToLowerInvariant() + " "
                + (l.Key == TxGateLever.Strength ? StrengthText((int)GateSetupValue(fresh, l), ExpanderModes.IsWdsp(fresh.ExpanderMode)) : GateValueTextOf(l, GateSetupValue(fresh, l))));
            Say(L("audio.noise_surface.tx.gate_defaults", ("values", string.Join(", ", said))), SpeechSubject.TransmitNoiseGate);
            SayAtUnkey(timing);
        }

        /// <summary>A gate lever's value in a setup record.</summary>
        private static double GateSetupValue(TxNoiseGateSetup g, TxGateLever lever) => lever.Key switch
        {
            TxGateLever.Margin => g.ThresholdMarginDb,
            TxGateLever.Threshold => g.ThresholdDb,
            TxGateLever.Attack => g.AttackMs,
            TxGateLever.Hold => g.HoldMs,
            TxGateLever.Release => g.ReleaseMs,
            TxGateLever.Depth => g.RangeDb,
            TxGateLever.Mode => ExpanderModes.IsWdsp(g.ExpanderMode) ? 1 : 0,
            TxGateLever.Strength when ExpanderModes.IsWdsp(g.ExpanderMode) => NearestDepthStop(g.WdspDepthDb),
            TxGateLever.Strength => g.GateSetting ? StrengthStops.Length - 1
                : Array.FindIndex(StrengthStops, s => !s.Gate && Math.Abs(s.Ratio - g.RatioToOne) < 0.01f) is int i && i >= 0 ? i : 1,
            TxGateLever.SideChainLow => g.SideChainLowCutHz,
            TxGateLever.SideChainHigh => g.SideChainHighCutHz,
            TxGateLever.LookAhead => g.LookAheadMs,
            _ => 0,
        };

        /// <summary>The cleanup monitor's four choices, in <c>TxAudioConditioner.MonitorModes</c> order.</summary>
        public static readonly string[] ListenToCleanupKeys =
        {
            "audio.noise_surface.tx.listen.off", "audio.noise_surface.tx.listen.out",
            "audio.noise_surface.tx.listen.removed", "audio.noise_surface.tx.listen.both",
        };

        /// <summary>
        /// "Listen to cleanup" (today's monitor tap, moved here from the Audio
        /// Workshop, part seven): Off, What goes out, What was removed, Both. It
        /// needs a radio's transmit chain to tap; with none it changes nothing
        /// and says so. Never saved on: it is a listening tool.
        /// </summary>
        public void SetListenToCleanup(int mode, string source)
        {
            using var oneUtterance = OneUtterance();
            var g = TransmitGate;
            mode = Math.Clamp(mode, 0, ListenToCleanupKeys.Length - 1);
            if (!g.MonitorAvailable && mode != 0)
            {
                Say(L("audio.noise_surface.tx.listen_needs_radio"), SpeechSubject.TransmitNoiseGate);
                return;
            }
            g.MonitorMode = mode;
            Say(L("audio.noise_surface.tx.listen", ("mode", Lexicon.Get(ListenToCleanupKeys[mode]))), SpeechSubject.TransmitNoiseGate);
            // The one thing a mode's name cannot carry: what hearing something there means.
            if (mode == 2) Say(Lexicon.Get("audio.cleanup.residual_monitor_meaning"), SpeechSubject.TransmitNoiseGate);
        }

        // ────────────────────────────────────────────────────────────────
        //  Transmit noise setups (Track H's store; part seven's submenu)
        // ────────────────────────────────────────────────────────────────

        /// <summary>The connected radio's serial, the key its setups live under; null with no radio.</summary>
        public string? TransmitSetupRadioId => _radio() is { Serial.Length: > 0 } r ? r.Serial : null;

        /// <summary>This radio's setups, in saved order; empty with no radio or no settings root.</summary>
        public IReadOnlyList<string> TransmitSetupNames() =>
            TransmitSetupRadioId is string radio && TransmitSetupStore() is { } store ? store.Names(radio) : Array.Empty<string>();

        private bool SetupsReady(out string radio, out TxNoiseSetupStore store)
        {
            radio = TransmitSetupRadioId ?? "";
            store = TransmitSetupStore()!;
            if (radio.Length > 0 && store != null) return true;
            Say(L("audio.noise_surface.tx.setups_need_radio"), SpeechSubject.TransmitNoiseSetup);
            return false;
        }

        /// <summary>
        /// Whether setups can be kept now, saying why not when they cannot (bench
        /// round, item 9): "Save current as setup..." asks this before the name
        /// dialog opens, so nobody types a name for nothing.
        /// </summary>
        public bool TransmitSetupsReadyOrSay()
        {
            using var oneUtterance = OneUtterance();
            return SetupsReady(out _, out _);
        }

        /// <summary>Raised after this radio's setups changed here (saved, renamed or deleted), so a list built from them can follow (bench round, item 3).</summary>
        public event Action? TransmitSetupsChanged;

        /// <summary>Apply one of this radio's setups: both slots and the gate. A slot change made while keyed waits for the unkey, and is said to.</summary>
        public void ApplyTransmitSetup(string name, string source)
        {
            using var owned = OwnTx(source); // the layer's own change, for its Escape (Escape ownership round)
            using var oneUtterance = OneUtterance();
            if (!SetupsReady(out string radio, out var store)) return;
            var setup = store.Find(radio, name);
            if (setup == null)
            {
                Say(Lexicon.Get("audio.denoise.tx.setup_missing", ("setup", name)), SpeechSubject.TransmitNoiseSetup);
                return;
            }
            var timing = ApplyWholeTransmit(setup.Spectral, setup.Neural, setup.Gate ?? new TxNoiseGateSetup(), setup);
            NoiseSettingsJournal.Record(NoiseJournalKind.PresetApplied, TxSource(source), "transmit setup", "", "", setup.Name);
            // Held for the unkey, the live state is not yet the setup's: name the
            // setup and say it waits, never a summary of what it will replace.
            if (timing.Timing == TxChangeTiming.AtUnkey)
                Say(L("audio.noise_surface.tx.setup_waiting", ("setup", setup.Name)), SpeechSubject.TransmitNoiseSetup);
            else
                Say(L("audio.noise_surface.tx.setup_applied", ("setup", setup.Name), ("summary", TransmitActiveSummary())), SpeechSubject.TransmitNoiseSetup);
            SayAtUnkey(timing);
        }

        /// <summary>
        /// A whole transmit setup, a preset's transmit section, the layer's
        /// Escape, or the gate's defaults, as ONE change (repair round 1, item 4):
        /// the slots (replaced, as Track H's repair defines a setup) and the gate,
        /// parameters included, made now when unkeyed, or held together until the
        /// unkey when keyed, behind anything already held. A null slot pair leaves
        /// the slots alone (the gate's defaults); a null gate leaves the gate
        /// alone (Escape). Every unsettled engine field choice is superseded.
        /// </summary>
        private TxChangeResult ApplyWholeTransmit(TxNoiseSlotSetup? spectral, TxNoiseSlotSetup? neural, TxNoiseGateSetup? gate,
            TxNoiseSetup? stages = null)
        {
            bool slots = spectral != null || neural != null;
            var chain = _txChain();
            // Sprint 49 Track I: a whole setup or preset carries the equaliser,
            // the de-esser and the stage order too, applied with the gate as one
            // staged record; null leaves them as they are (the gate's defaults,
            // the layer's Escape).
            var eqCopy = stages?.Eq?.Clone();
            var deCopy = stages?.DeEsser?.Clone();
            var orderCopy = stages?.StageOrder == null ? null : (string[])stages.StageOrder.Clone();
            void ApplyGateAndStages(TxNoiseGateSetup? g)
            {
                if (stages != null) TransmitGate.ApplyStagesNow(eqCopy ?? new ParametricEqSetup(), deCopy ?? new TxDeEsserSetup(),
                    orderCopy ?? JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder, g);
                else if (g != null) TransmitGate.ApplyNow(g);
            }
            if (chain == null)
            {
                ApplyGateAndStages(gate);
                return TxChangeResult.Now;
            }
            // A whole request identical to what is in place, with nothing waiting
            // ahead of it and no engine field choice still settling, changes
            // nothing (bench round 2, Codex's P3): it is not queued, so a layer's
            // Escape has nothing to withdraw and does not report a restoration.
            // With anything waiting it is queued as before, because it may be
            // overriding a different intent that would otherwise land.
            if (!chain.HasDeferredChanges && _txSettling.Count == 0 && WholeAlreadyInPlace(chain, slots, spectral, neural, gate)
                && (stages == null || SameStages(TransmitGate.CaptureStages(), stages)))
                return TxChangeResult.Now;
            if (slots) { CancelTransmitSettle(DenoiseFamily.Spectral); CancelTransmitSettle(DenoiseFamily.Neural); }
            var spectralCopy = spectral?.Clone();
            var neuralCopy = neural?.Clone();
            var gateCopy = gate?.Clone();
            return chain.RunOrDefer(() =>
            {
                if (slots) chain.ApplySlotsNow(spectralCopy, neuralCopy);
                ApplyGateAndStages(gateCopy);
            });
        }

        /// <summary>Whether the equaliser, the de-esser and the order in place are a setup's (compared through the record's own serialized form, so a field added later is compared too).</summary>
        private static bool SameStages((ParametricEqSetup Eq, TxDeEsserSetup DeEsser, string[] Order) now, TxNoiseSetup want) =>
            new TxNoiseSetup { Eq = now.Eq, DeEsser = now.DeEsser, StageOrder = now.Order }
                .SameContentAs(new TxNoiseSetup { Eq = want.Eq ?? new(), DeEsser = want.DeEsser ?? new(), StageOrder = want.StageOrder ?? JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder });

        /// <summary>Whether a whole request would leave both slots (when it names them) and the gate (when it names one) exactly as they are.</summary>
        private bool WholeAlreadyInPlace(TxNoiseChain chain, bool slots, TxNoiseSlotSetup? spectral, TxNoiseSlotSetup? neural, TxNoiseGateSetup? gate)
        {
            if (slots)
            {
                var (spectralNow, neuralNow) = chain.CaptureSlots();
                if (DescribeTx(spectralNow) != DescribeTx(spectral ?? new TxNoiseSlotSetup())
                    || DescribeTx(neuralNow) != DescribeTx(neural ?? new TxNoiseSlotSetup())) return false;
            }
            return gate == null || SameGate(TransmitGate.Capture(), gate);
        }

        /// <summary>The live transmit chain and gate as a setup of this name.</summary>
        public TxNoiseSetup CaptureTransmitSetup(string name)
        {
            var setup = new TxNoiseSetup { Name = name ?? "", Gate = TransmitGate.Capture() };
            if (_txChain() is { } chain) (setup.Spectral, setup.Neural) = chain.CaptureSlots();
            (setup.Eq, setup.DeEsser, setup.StageOrder) = TransmitGate.CaptureStages();
            return setup;
        }

        /// <summary>
        /// Save the live transmit chain and gate as one of this radio's setups.
        /// A name another setup holds is refused unless <paramref name="replace"/>
        /// (the dialog asks first). Returns the store's answer.
        /// </summary>
        public TxNoiseSetupResult SaveTransmitSetup(string name, bool replace, string source)
        {
            using var oneUtterance = OneUtterance();
            name = (name ?? "").Trim();
            if (!SetupsReady(out string radio, out var store)) return new TxNoiseSetupResult(TxNoiseSetupOutcome.BadRadio, name);
            if (name.Length == 0) return new TxNoiseSetupResult(TxNoiseSetupOutcome.BadName, name);
            if (!replace && store.Find(radio, name) != null)
            {
                Say(L("audio.noise_surface.tx.setup_name_taken", ("setup", name)), SpeechSubject.TransmitNoiseSetup);
                return new TxNoiseSetupResult(TxNoiseSetupOutcome.NameTaken, name);
            }
            var result = store.Save(radio, CaptureTransmitSetup(name));
            Say(result.Succeeded ? L("audio.noise_surface.tx.setup_saved", ("setup", name)) : L("audio.noise_surface.tx.setup_failed", ("setup", name)),
                SpeechSubject.TransmitNoiseSetup);
            if (result.Succeeded)
            {
                NoiseSettingsJournal.Record(NoiseJournalKind.PresetApplied, TxSource(source), "transmit setup", "saved", "", name);
                TransmitSetupsChanged?.Invoke();
            }
            return result;
        }

        /// <summary>Rename one of this radio's setups, then point every microphone that named it at the new name (Track H's contract).</summary>
        public TxNoiseSetupResult RenameTransmitSetup(string from, string to)
        {
            using var oneUtterance = OneUtterance();
            if (!SetupsReady(out string radio, out var store)) return new TxNoiseSetupResult(TxNoiseSetupOutcome.BadRadio, from);
            var result = store.Rename(radio, from, (to ?? "").Trim());
            if (result.Succeeded)
            {
                // Two stores, one operation (repair round 1, item 10). If the
                // microphones that named the setup cannot be repointed, the rename
                // is put back, so no saved microphone is left pointing at a name
                // that is gone; the operator hears that nothing changed.
                int repointed = RepointMicrophones?.Invoke(radio, from, result.Name) ?? 0;
                if (repointed == RepointNotSaved)
                {
                    var back = store.Rename(radio, result.Name, from);
                    System.Diagnostics.Trace.WriteLine($"NoiseController: renaming transmit setup '{from}' to '{result.Name}' could not repoint the microphones; "
                        + (back.Succeeded ? "the rename was put back." : "putting the rename back failed too (" + back.Outcome + ")."));
                    Say(L(back.Succeeded ? "audio.noise_surface.tx.setup_rename_undone" : "audio.noise_surface.tx.setup_rename_half",
                        ("old", from), ("new", result.Name)), SpeechSubject.TransmitNoiseSetup);
                    return back.Succeeded ? new TxNoiseSetupResult(TxNoiseSetupOutcome.NotSaved, from) : new TxNoiseSetupResult(TxNoiseSetupOutcome.NotSaved, result.Name);
                }
                Say(L("audio.noise_surface.tx.setup_renamed", ("old", from), ("new", result.Name)), SpeechSubject.TransmitNoiseSetup);
                TransmitSetupsChanged?.Invoke();
            }
            else Say(L(result.Outcome == TxNoiseSetupOutcome.NameTaken ? "audio.noise_surface.tx.setup_name_taken" : "audio.noise_surface.tx.setup_failed",
                ("setup", (to ?? "").Trim())), SpeechSubject.TransmitNoiseSetup);
            return result;
        }

        /// <summary>Delete one of this radio's setups. A microphone that named it then reads it as missing, said once (Track H).</summary>
        public TxNoiseSetupResult DeleteTransmitSetup(string name)
        {
            using var oneUtterance = OneUtterance();
            if (!SetupsReady(out string radio, out var store)) return new TxNoiseSetupResult(TxNoiseSetupOutcome.BadRadio, name);
            var result = store.Delete(radio, name);
            Say(result.Succeeded ? L("audio.noise_surface.tx.setup_deleted", ("setup", name)) : L("audio.noise_surface.tx.setup_failed", ("setup", name)),
                SpeechSubject.TransmitNoiseSetup);
            if (result.Succeeded) TransmitSetupsChanged?.Invoke();
            return result;
        }

        /// <summary>
        /// "Copy this setup to another radio..." (ruled S10, 2026-11-18 08:28:
        /// nothing automatic): one of this radio's setups, copied to a radio by
        /// serial. A name the other radio already holds is refused unless
        /// <paramref name="replace"/>.
        /// </summary>
        public TxNoiseSetupResult CopyTransmitSetupToRadio(string name, string toRadio, string toRadioName, bool replace)
        {
            using var oneUtterance = OneUtterance();
            if (!SetupsReady(out string radio, out var store)) return new TxNoiseSetupResult(TxNoiseSetupOutcome.BadRadio, name);
            var result = store.CopyToRadio(radio, name, toRadio, replace);
            Say(result.Succeeded ? L("audio.noise_surface.tx.setup_copied", ("setup", name), ("radio", toRadioName))
                : result.Outcome == TxNoiseSetupOutcome.NameTaken ? L("audio.noise_surface.tx.setup_copy_taken", ("setup", name), ("radio", toRadioName))
                : L("audio.noise_surface.tx.setup_failed", ("setup", name)), SpeechSubject.TransmitNoiseSetup);
            return result;
        }

        // ────────────────────────────────────────────────────────────────
        //  The layer's Escape on transmit
        // ────────────────────────────────────────────────────────────────

        /// <summary>A transmit slot pair as a comparable string (the snapshot's change test).</summary>
        private static string DescribeTx(TxNoiseSlotSetup? s) =>
            s == null ? "" : (s.Enabled ? "on " : "off ") + s.EngineId + " " + Describe((s.Engines ?? new()).ToArray());
    }
}
