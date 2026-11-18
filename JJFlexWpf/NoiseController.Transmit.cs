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
        bool AutoThreshold { get; set; }
        float MarginDb { get; set; }
        float ThresholdDb { get; set; }
        float AttackMs { get; set; }
        float HoldMs { get; set; }
        float ReleaseMs { get; set; }
        float RangeDb { get; set; }

        /// <summary>Whether a radio's transmit chain is attached, so the cleanup monitor can play (it needs the chain's tap).</summary>
        bool MonitorAvailable { get; }

        /// <summary>The cleanup monitor: 0 off, 1 what goes out, 2 what was removed, 3 both (<c>TxAudioConditioner.MonitorModes</c>).</summary>
        int MonitorMode { get; set; }

        /// <summary>The cleanup monitor's volume, 0 to 1.</summary>
        float MonitorVolume { get; set; }

        /// <summary>The gate's settings as a setup carries them.</summary>
        TxNoiseGateSetup Capture();

        /// <summary>Put a setup's gate settings in place. Returns when its on or off takes effect (at the unkey while keyed, after Track H's repair round 1).</summary>
        TxChangeResult Apply(TxNoiseGateSetup gate);
    }

    /// <summary>The app's gate: <see cref="TxAudioConditioning"/>, the session's one transmit chain.</summary>
    public sealed class AppTransmitGate : ITransmitGate
    {
        public bool Enabled => TxAudioConditioning.GateEnabled;

        // Track H repair round 1: the gate's on and off wait for the unkey while
        // keyed, and say so; the Workshop's old toggle did this, and it lives here now.
        public TxChangeResult SetEnabled(bool on) => TxAudioConditioning.SetGateEnabled(on);
        public bool AutoThreshold { get => TxAudioConditioning.AutoThreshold; set => TxAudioConditioning.AutoThreshold = value; }
        public float MarginDb { get => TxAudioConditioning.ThresholdMarginDb; set => TxAudioConditioning.ThresholdMarginDb = value; }
        public float ThresholdDb { get => TxAudioConditioning.GateThresholdDb; set => TxAudioConditioning.GateThresholdDb = value; }
        public float AttackMs { get => TxAudioConditioning.GateAttackMs; set => TxAudioConditioning.GateAttackMs = value; }
        public float HoldMs { get => TxAudioConditioning.GateHoldMs; set => TxAudioConditioning.GateHoldMs = value; }
        public float ReleaseMs { get => TxAudioConditioning.GateReleaseMs; set => TxAudioConditioning.GateReleaseMs = value; }
        public float RangeDb { get => TxAudioConditioning.GateRangeDb; set => TxAudioConditioning.GateRangeDb = value; }
        public bool MonitorAvailable => TxAudioConditioning.Conditioner != null;
        public int MonitorMode
        {
            get => (int)TxAudioConditioning.MonitorMode;
            set => TxAudioConditioning.MonitorMode = (JJPortaudio.TxAudioConditioner.MonitorModes)value;
        }
        public float MonitorVolume { get => TxAudioConditioning.MonitorVolume; set => TxAudioConditioning.MonitorVolume = value; }
        public TxNoiseGateSetup Capture() => TxAudioConditioning.CaptureSetup("").Gate.Clone();
        // A setup with no slots applies the gate alone (TxNoiseChain.ApplySlots skips a null slot).
        public TxChangeResult Apply(TxNoiseGateSetup gate) =>
            TxAudioConditioning.ApplySetup(new TxNoiseSetup { Spectral = null!, Neural = null!, Gate = gate.Clone() });
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
        /// then save the microphone store). Set by the app; returns how many
        /// microphones were repointed.
        /// </summary>
        public Func<string, string, string, int>? RepointMicrophones { get; set; }

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
            _txStepIndex = -1;
            _stepIndex = -1;
            Say(L(LayerDirection == DenoiseDirection.Transmit ? "audio.noise_surface.tx.layer_transmit" : "audio.noise_surface.tx.layer_receive"),
                SpeechSubject.NoiseLayerDirection);
        }

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
            using var oneUtterance = OneUtterance();
            var e = _catalog.Find(id);
            if (e == null) return;
            if (!OffersTransmit(e))
            {
                Say(L("audio.noise_surface.tx.receive_only", ("engine", NameOf(id))), SpeechSubject.TransmitNoiseUnavailable);
                return;
            }
            Current = id; CurrentLeverIndex = 0;
            if (TransmitIsOn(id)) TxTurnOff(e, source, speak: true);
            else TxTurnOn(e, source, speak: true, model: null);
        }

        /// <summary>Turn a transmit engine on or off outright (a check item, Home's On box when its engine is chosen).</summary>
        public void TransmitSetOn(string id, bool on, string source)
        {
            using var oneUtterance = OneUtterance();
            if (TransmitIsOn(id) == on)
            {
                Current = id;
                Say(L(on ? "audio.noise_surface.on" : "audio.noise_surface.off", ("engine", TxName(id))), SpeechSubject.TransmitNoiseEngineSwitch);
                return;
            }
            TransmitToggle(id, source);
        }

        /// <summary>A transmit slot on or off without naming an engine (Home's On box): on starts the slot's chosen engine.</summary>
        public void TransmitSetSlotOn(DenoiseFamily family, bool on, string source)
        {
            using var oneUtterance = OneUtterance();
            if (_txChain() is not { } chain) return;
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
            Current = id;
            TxTurnOn(e, source, speak: true, model: null);
        }

        private void TxTurnOff(DenoiseEngineEntry e, string source, bool speak)
        {
            if (_txChain() is not { } chain) return;
            var result = chain.SetEnabled(e.Family!.Value, false);
            NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), e.Id, "", "on", "off");
            if (!speak) return;
            Say(L("audio.noise_surface.off", ("engine", TxName(e.Id))), SpeechSubject.TransmitNoiseEngineSwitch);
            SayAtUnkey(result);
        }

        /// <summary>
        /// Turn a transmit engine on in its family's slot, replacing whatever
        /// engine of that family was on there. Returns the id it replaced. An
        /// engine that cannot run is still chosen, as an intent, and says why.
        /// </summary>
        private string? TxTurnOn(DenoiseEngineEntry e, string source, bool speak, string? model)
        {
            if (_txChain() is not { } chain) return null;
            var family = e.Family!.Value;
            if (e.Factory == null)
            {
                if (speak) Say(L("audio.noise_surface.not_in_version", ("engine", TxName(e.Id))), SpeechSubject.TransmitNoiseUnavailable);
                return null;
            }
            string chosen = chain.ChosenEngineId(family);
            string? replaced = chain.WantsEnabled(family) && chosen != e.Id && chosen.Length > 0 ? chosen : null;
            if (model != null) chain.SetSetting(e.Id, "model", new EngineSettingValue(0, model));
            var choose = chain.ChooseEngine(family, e.Id);
            if (choose.Timing == TxChangeTiming.Refused)
            {
                if (speak) Say(L("audio.noise_surface.not_in_version", ("engine", TxName(e.Id))), SpeechSubject.TransmitNoiseUnavailable);
                return null;
            }
            var enable = chain.SetEnabled(family, true);
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
                SayAtUnkey(Later(choose, enable));
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
            if (!Chatty || _config()?.NoiseSayDelays == false) return null;
            if (!HasDelay(e.Id, out int? ms, DenoiseDirection.Transmit) || ms is not int value || value <= 0) return null;
            return L("audio.noise_surface.tx.delay_clause", ("ms", value));
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
            _txSettling[family] = _scheduler.After(TimeSpan.FromMilliseconds(EngineSettleMs), () =>
            {
                _txSettling.Remove(family);
                ApplyTransmitSlotChoice(family, id, source);
            });
        }

        private void ApplyTransmitSlotChoice(DenoiseFamily family, string id, string source)
        {
            using var oneUtterance = OneUtterance();
            _txPendingChoice.Remove(family);
            var e = _catalog.Find(id);
            if (_txChain() is not { } chain || e == null || !OffersTransmit(e) || e.Family != family || chain.ChosenEngineId(family) == id) return;
            Current = id; CurrentLeverIndex = 0;
            if (chain.WantsEnabled(family)) { TxTurnOn(e, source, speak: true, model: null); return; }
            if (e.Factory == null)
            {
                Say(L("audio.noise_surface.not_in_version", ("engine", TxName(id))), SpeechSubject.TransmitNoiseUnavailable);
                return;
            }
            chain.ChooseEngine(family, id);
        }

        // ────────────────────────────────────────────────────────────────
        //  The 0 key on transmit
        // ────────────────────────────────────────────────────────────────

        private List<(DenoiseFamily Family, string EngineId)>? _txZeroRestore;

        /// <summary>The 0 key in the transmit direction: both transmit slots off, and back (question 17's reasoning, on the microphone).</summary>
        public void TransmitComputerOffAndBack(string source)
        {
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
            SpeakTransmitLever(lever, e);
            SayAtUnkey(result);
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
                    chain.SetStrength(e.Id, NoiseFloor.ToStrength(value.Number));
                    NoiseSettingsJournal.Record(NoiseJournalKind.Floor, TxSource(source), e.Id, FloorKey, FloorText(was), FloorText(chain.GetStrength(e.Id)));
                    return TxChangeResult.Now;
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

        private void SpeakTransmitLever(NoiseLever lever, DenoiseEngineEntry e)
        {
            string name = TxName(e.Id), leverName = LeverName(lever), value = ValueText(lever);
            bool on = TransmitIsOn(e.Id);
            if (lever.Key == "model" && ChosenOption(lever)?.Probe is { } probe && SafeProbe(probe, e.Id) is var reason && reason != Unavailability.None)
            {
                SpeakUnavailable(e, reason, kept: true, direction: DenoiseDirection.Transmit);
                return;
            }
            string key;
            if (lever.Key == "model") key = on ? "audio.noise_surface.lever_model" : "audio.noise_surface.lever_choice_off";
            else if (lever.Descriptor is { Kind: EngineSettingKind.Choice }) key = on ? "audio.noise_surface.lever_choice" : "audio.noise_surface.lever_choice_off";
            else key = on ? "audio.noise_surface.lever" : "audio.noise_surface.lever_off";
            Say(L(key, ("engine", name), ("lever", leverName), ("lever_lower", leverName.ToLowerInvariant()), ("value", value)), SpeechSubject.TransmitNoiseLevel);
        }

        /// <summary>Whether an engine's Advanced levers are revealed in a direction (remembered per engine and direction).</summary>
        public bool IsAdvanced(string id, DenoiseDirection direction) =>
            direction == DenoiseDirection.Receive ? IsAdvanced(id) : _config()?.NoiseAdvancedEngines.Contains(TxAdvancedKey(id)) == true;

        /// <summary>Reveal or hide an engine's transmit Advanced levers on Home, remember it, and say so.</summary>
        public void SetAdvanced(string id, bool on, DenoiseDirection direction)
        {
            if (direction == DenoiseDirection.Receive) { SetAdvanced(id, on); return; }
            var config = _config();
            if (config != null)
            {
                config.NoiseAdvancedEngines.Remove(TxAdvancedKey(id));
                if (on) config.NoiseAdvancedEngines.Add(TxAdvancedKey(id));
                _persist();
            }
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
            using var oneUtterance = OneUtterance();
            var e = _catalog.Find(id);
            if (e == null) return;
            if (!OffersTransmit(e) || _txChain() is not { } chain)
            {
                Say(L("audio.noise_surface.tx.receive_only", ("engine", NameOf(id))), SpeechSubject.TransmitNoiseUnavailable);
                return;
            }
            Current = id;
            var slot = chain.SlotFor(id)!;
            var changes = new List<NoiseJournalChange>();
            float strength = NoiseFloor.DefaultStrength(id, DenoiseDirection.Transmit);
            if (Math.Abs(chain.GetStrength(id) - strength) > 0.0001)
                changes.Add(new NoiseJournalChange(id, FloorKey, FloorText(chain.GetStrength(id)), FloorText(strength)));
            chain.SetStrength(id, strength);
            var timing = TxChangeResult.Now;
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
            var said = Levers(id, includeAdvanced: false, DenoiseDirection.Transmit).Select(l => LeverName(l).ToLowerInvariant() + " " + ValueText(l)).ToList();
            if (FloorLeverOf(id, DenoiseDirection.Transmit) is { Advanced: true } advancedFloor)
                said.Add(L("audio.noise_surface.defaults_floor", ("value", ValueText(advancedFloor))));
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
            if (_txChain() is { } chain)
                foreach (var family in new[] { DenoiseFamily.Spectral, DenoiseFamily.Neural })
                {
                    if (!chain.WantsEnabled(family)) continue;
                    var e = _catalog.Find(chain.ChosenEngineId(family));
                    if (e == null) continue;
                    string name = Lexicon.Get(e.LabelKey);
                    var normal = Levers(e.Id, includeAdvanced: false, DenoiseDirection.Transmit);
                    var details = normal.Select(l => l.Descriptor is { Kind: EngineSettingKind.Choice } ? ValueText(l) : LeverName(l).ToLowerInvariant() + " " + ValueText(l)).ToList();
                    items.Add(details.Count == 0 || !Chatty ? name : name + ", " + string.Join(", ", details));
                }
            var gate = TransmitGate;
            if (gate.Enabled)
                items.Add(gate.AutoThreshold
                    ? L("audio.noise_surface.tx.active.gate_auto", ("margin", GateValueText(GateLever(TxGateLever.Margin)!)))
                    : L("audio.noise_surface.tx.active.gate_fixed", ("threshold", GateValueText(GateLever(TxGateLever.Threshold)!))));
            if (items.Count == 0) return L("audio.noise_surface.tx.active.none");
            string summary = L("audio.noise_surface.tx.active", ("list", string.Join("; ", items)));
            if (_radio() is { } r && NoiseModeRules.GroupOf(r.Mode) != NoiseModeGroup.Voice)
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
            var offered = Favourites.Where(f => _catalog.Find(f.EngineId) is { } e && OffersTransmit(e)).ToList();
            return offered.Count > 0 ? offered : TransmitEngines().Select(e => new NoiseFavourite { EngineId = e.Id }).ToList();
        }

        /// <summary>
        /// ] and [ after T: turn the next (or previous) transmit favourite on and
        /// the one stepped from off (every transmit engine is a reducer, so the
        /// same-subject rule always turns it off, a clean A against B). Two
        /// favourites of one engine change its model in place.
        /// </summary>
        public void TransmitStep(int direction, string source)
        {
            using var oneUtterance = OneUtterance();
            var list = TransmitStepList();
            if (list.Count == 0 || _txChain() is not { } chain) return;
            if (Favourites.Count == 0 && !_toldNoFavourites)
            {
                _toldNoFavourites = true;
                Say(Lexicon.Get("audio.noise_surface.no_favourites", VerbosityLevel.Chatty), SpeechSubject.NoiseFavourite, VerbosityLevel.Chatty);
            }
            if (_txStepIndex < 0 || _txStepIndex >= list.Count)
                _txStepIndex = Current == null ? -1 : list.ToList().FindIndex(f => f.EngineId == Current);
            NoiseFavourite? from = _txStepIndex >= 0 && _txStepIndex < list.Count ? list[_txStepIndex] : null;
            int next = _txStepIndex < 0 ? (direction > 0 ? 0 : list.Count - 1) : ((_txStepIndex + Math.Sign(direction)) % list.Count + list.Count) % list.Count;
            _txStepIndex = next;
            var to = list[next];
            var e = _catalog.Find(to.EngineId);
            if (e == null) return;
            Current = e.Id; CurrentLeverIndex = 0;
            if (e.Factory == null)
            {
                Say(L("audio.noise_surface.not_in_version", ("engine", TxName(e.Id))), SpeechSubject.TransmitNoiseUnavailable);
                return;
            }
            var timing = TxChangeResult.Now;
            if (from != null && from.EngineId == to.EngineId)
            {
                if (to.Model != null) timing = Later(timing, chain.SetSetting(e.Id, "model", new EngineSettingValue(0, to.Model)));
                if (!TransmitIsOn(e.Id)) TxTurnOn(e, source, speak: false, model: null);
                Say(L("audio.noise_surface.on", ("engine", TxName(e.Id))), SpeechSubject.TransmitNoiseEngineSwitch);
                SayAtUnkey(Later(timing, chain.IsKeyed ? TxChangeResult.AtUnkey : TxChangeResult.Now));
                return;
            }
            var fromEntry = from == null ? null : _catalog.Find(from.EngineId);
            bool turnOffFrom = fromEntry != null && TransmitIsOn(fromEntry.Id);
            string? replaced = TransmitIsOn(e.Id) ? null : TxTurnOn(e, source, speak: false, model: to.Model);
            if (turnOffFrom && fromEntry!.Id != replaced && fromEntry.Id != e.Id) TxTurnOff(fromEntry, source, speak: false);
            string? offId = turnOffFrom ? fromEntry!.Id : replaced;
            Say(offId == null ? L("audio.noise_surface.on", ("engine", TxName(e.Id)))
                              : L("audio.noise_surface.on_replacing", ("engine", TxName(e.Id)), ("other", TxName(offId))),
                SpeechSubject.TransmitNoiseEngineSwitch);
            SayAtUnkey(chain.IsKeyed ? TxChangeResult.AtUnkey : TxChangeResult.Now);
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

        /// <summary>Every gate lever, in the order Home lists them: Margin is normal; the rest are Advanced (part seven).</summary>
        private static readonly TxGateLever[] AllGateLevers =
        {
            new(TxGateLever.Margin, "audio.noise_surface.tx.gate.margin", 3, 15, 1, "audio.unit.decibels", false),
            new(TxGateLever.Threshold, "audio.noise_surface.tx.gate.threshold", -80, -10, 1, "audio.unit.decibels", true),
            new(TxGateLever.Attack, "audio.noise_surface.tx.gate.attack", 1, 20, 1, "audio.unit.milliseconds", true),
            new(TxGateLever.Hold, "audio.noise_surface.tx.gate.hold", 50, 1000, 25, "audio.unit.milliseconds", true),
            new(TxGateLever.Release, "audio.noise_surface.tx.gate.release", 50, 1000, 25, "audio.unit.milliseconds", true),
            new(TxGateLever.Depth, "audio.noise_surface.tx.gate.depth", 6, 40, 1, "audio.unit.decibels", true),
            new(TxGateLever.MonitorVolume, "audio.noise_surface.tx.gate.monitor_volume", 0, 100, 5, "audio.noise_surface.unit.percent", true),
        };

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
                _ => 0,
            };
        }

        private void WriteGate(TxGateLever lever, double value)
        {
            var g = TransmitGate;
            float v = (float)value;
            switch (lever.Key)
            {
                case TxGateLever.Margin: g.MarginDb = v; break;
                case TxGateLever.Threshold: g.ThresholdDb = v; break;
                case TxGateLever.Attack: g.AttackMs = v; break;
                case TxGateLever.Hold: g.HoldMs = v; break;
                case TxGateLever.Release: g.ReleaseMs = v; break;
                case TxGateLever.Depth: g.RangeDb = v; break;
                case TxGateLever.MonitorVolume: g.MonitorVolume = v / 100f; break;
            }
        }

        /// <summary>A gate lever's value as spoken, its unit at Chatty.</summary>
        public string GateValueText(TxGateLever lever) =>
            WithUnit(GateNumber(lever).ToString("0", CultureInfo.InvariantCulture), lever.UnitKey);

        /// <summary>Every value a gate lever takes, least to most, with its unit: a Home field arrows through exactly what the menu and the layer step.</summary>
        public IReadOnlyList<(double Value, string Text)> GateSteps(TxGateLever lever)
        {
            var list = new List<(double, string)>();
            for (double v = lever.Min; v <= lever.Max + 1e-9; v += lever.Step)
                list.Add((v, v.ToString("0", CultureInfo.InvariantCulture) + (lever.UnitKey is string u ? " " + Lexicon.Get(u) : "")));
            return list;
        }

        /// <summary>The gate on or off, speaking the timing the gate answers with (at the unkey while keyed, after Track H's repair round 1).</summary>
        public void SetGateOn(bool on, string source)
        {
            using var oneUtterance = OneUtterance();
            var g = TransmitGate;
            bool was = g.Enabled;
            var timing = g.SetEnabled(on);
            if (was != on) NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), "gate", "", Words(was), Words(on));
            Say(L(on ? "audio.noise_surface.tx.gate_on" : "audio.noise_surface.tx.gate_off"), SpeechSubject.TransmitNoiseGate);
            SayAtUnkey(timing);
        }

        public void ToggleGate(string source) => SetGateOn(!TransmitGate.Enabled, source);

        /// <summary>The automatic threshold on or off: on, the gate opens a margin above the measured room floor; off, at the threshold set by hand.</summary>
        public void SetGateAutoThreshold(bool on, string source)
        {
            using var oneUtterance = OneUtterance();
            var g = TransmitGate;
            bool was = g.AutoThreshold;
            g.AutoThreshold = on;
            if (was != on) NoiseSettingsJournal.Record(NoiseJournalKind.Lever, TxSource(source), "gate", "auto_threshold", Words(was), Words(on));
            Say(L(on ? "audio.noise_surface.tx.gate_auto_on" : "audio.noise_surface.tx.gate_auto_off"), SpeechSubject.TransmitNoiseGate);
        }

        /// <summary>A gate lever one step up or down; at an end it stays and says so.</summary>
        public void AdjustGate(TxGateLever lever, int direction, string source)
        {
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
            using var oneUtterance = OneUtterance();
            double was = GateNumber(lever);
            WriteGate(lever, Math.Clamp(value, lever.Min, lever.Max));
            NoiseSettingsJournal.Record(NoiseJournalKind.Lever, TxSource(source), "gate", lever.Key,
                was.ToString(CultureInfo.InvariantCulture), GateNumber(lever).ToString(CultureInfo.InvariantCulture));
            Say(L("audio.noise_surface.tx.gate_lever", ("lever", GateLeverName(lever)), ("value", GateValueText(lever))), SpeechSubject.TransmitNoiseGate);
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
            using var oneUtterance = OneUtterance();
            var g = TransmitGate;
            var fresh = new TxNoiseGateSetup { Enabled = g.Enabled };
            var timing = g.Apply(fresh);
            NoiseSettingsJournal.Record(NoiseJournalKind.ReturnToDefaults, TxSource(source), "gate", "", "", "defaults");
            var said = GateLevers(includeAdvanced: false).Select(l => GateLeverName(l).ToLowerInvariant() + " " + GateValueText(l));
            Say(L("audio.noise_surface.tx.gate_defaults", ("values", string.Join(", ", said))), SpeechSubject.TransmitNoiseGate);
            SayAtUnkey(timing);
        }

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

        /// <summary>Apply one of this radio's setups: both slots and the gate. A slot change made while keyed waits for the unkey, and is said to.</summary>
        public void ApplyTransmitSetup(string name, string source)
        {
            using var oneUtterance = OneUtterance();
            if (!SetupsReady(out string radio, out var store)) return;
            var setup = store.Find(radio, name);
            if (setup == null)
            {
                Say(Lexicon.Get("audio.denoise.tx.setup_missing", ("setup", name)), SpeechSubject.TransmitNoiseSetup);
                return;
            }
            var timing = _txChain()?.ApplySlots(setup.Spectral, setup.Neural) ?? TxChangeResult.Now;
            timing = Later(timing, TransmitGate.Apply(setup.Gate ?? new TxNoiseGateSetup()));
            NoiseSettingsJournal.Record(NoiseJournalKind.PresetApplied, TxSource(source), "transmit setup", "", "", setup.Name);
            Say(L("audio.noise_surface.tx.setup_applied", ("setup", setup.Name), ("summary", TransmitActiveSummary())), SpeechSubject.TransmitNoiseSetup);
            SayAtUnkey(timing);
        }

        /// <summary>The live transmit chain and gate as a setup of this name.</summary>
        public TxNoiseSetup CaptureTransmitSetup(string name)
        {
            var setup = new TxNoiseSetup { Name = name ?? "", Gate = TransmitGate.Capture() };
            if (_txChain() is { } chain) (setup.Spectral, setup.Neural) = chain.CaptureSlots();
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
            if (result.Succeeded) NoiseSettingsJournal.Record(NoiseJournalKind.PresetApplied, TxSource(source), "transmit setup", "saved", "", name);
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
                RepointMicrophones?.Invoke(radio, from, result.Name);
                Say(L("audio.noise_surface.tx.setup_renamed", ("old", from), ("new", result.Name)), SpeechSubject.TransmitNoiseSetup);
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
