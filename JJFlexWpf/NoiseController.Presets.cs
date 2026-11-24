using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    /// <summary>Where the operator's presets live: <c>{operator}_audioPresets.xml</c> in the app (ruled 2026-11-07, question 10); memory in tests.</summary>
    public interface INoisePresetStore
    {
        AudioChainPresets Load();
        bool Save(AudioChainPresets store);
    }

    /// <summary>The transmit chain's half of a preset: applied and captured through the radio by the app, absent in tests.</summary>
    public interface ITransmitChainAccess
    {
        void Apply(AudioChainPreset preset);
        AudioChainPreset Capture(string name);
    }

    /// <summary>The app's preset store, per operator.</summary>
    public sealed class FilePresetStore : INoisePresetStore
    {
        private readonly Func<(string Dir, string Operator)?> _where;
        public FilePresetStore(Func<(string Dir, string Operator)?> where) => _where = where;
        public AudioChainPresets Load() => _where() is { } w ? AudioChainPresets.Load(w.Dir, w.Operator) : AudioChainPresets.CreateDefaults();
        public bool Save(AudioChainPresets store) => _where() is { } w && store.Save(w.Dir, w.Operator);
    }

    /// <summary>A collision on save: replace the preset of that name, or keep both.</summary>
    public enum PresetCollision { Ask, Replace, KeepBoth }

    public sealed partial class NoiseController
    {
        /// <summary>The operator's preset store; null until the app supplies one (no presets, said so).</summary>
        public INoisePresetStore? PresetStore { get; set; }

        /// <summary>The transmit chain, for presets that hold it; null where there is no radio to apply one to.</summary>
        public ITransmitChainAccess? TransmitChain { get; set; }

        /// <summary>
        /// Raised after the operator's preset store changed here (a preset saved,
        /// renamed or deleted), so every list built from it can follow: the
        /// receive and transmit Presets submenus rebuild on it the way the
        /// transmit setups submenu rebuilds on <see cref="TransmitSetupsChanged"/>
        /// (Sprint 49 Track N2; a preset saved from the Audio menu was missing
        /// from that menu until something else rebuilt it). Not raised by a
        /// refused or failed change, nor by the one-time offer of the starter.
        /// </summary>
        public event Action? PresetsChanged;

        /// <summary>
        /// The Audio Workshop writes the same store itself (its Load picker's
        /// Delete, its chain preset Save, Import): the app says so here after a
        /// write that landed, so the menus follow those roads too.
        /// </summary>
        public void PresetsChangedElsewhere() => PresetsChanged?.Invoke();

        /// <summary>The one shipped starter (ruled, departure D3): today's "Apply Recommended Levels" both-on values.</summary>
        public const string BalancedCaptureName = "Balanced capture";

        public static AudioChainPreset BalancedCapture() => new(BalancedCaptureName)
        {
            SchemaVersion = AudioChainPreset.CurrentSchemaVersion,
            HoldsTransmitChain = false,
            ReceiveNoise = new ReceiveNoisePresetSection
            {
                Spectral = new NoisePresetSlot
                {
                    On = true, EngineId = "spectral_capture",
                    Settings = new DenoiseEngineSettings
                    {
                        EngineId = "spectral_capture", Direction = DenoiseDirection.Receive, Strength = 1f,
                        Values = new() { new() { Name = "subtraction_amount", Number = 0.45 }, new() { Name = "spectral_floor", Number = 0.04 } },
                    },
                },
                Neural = new NoisePresetSlot
                {
                    On = true, EngineId = "rnnoise",
                    Settings = new DenoiseEngineSettings { EngineId = "rnnoise", Direction = DenoiseDirection.Receive, Strength = 0.65f },
                },
            },
        };

        private AudioChainPresets LoadPresets()
        {
            var store = PresetStore?.Load() ?? new AudioChainPresets();
            if (!store.BalancedCaptureOffered)
            {
                store.BalancedCaptureOffered = true;
                if (AllPresets(store).All(p => p.Name != BalancedCaptureName)) store.NoisePresets.Add(BalancedCapture());
                PresetStore?.Save(store);
            }
            return store;
        }

        private static IEnumerable<AudioChainPreset> AllPresets(AudioChainPresets store) => store.Presets.Concat(store.NoisePresets);

        /// <summary>Every preset that holds receive noise reduction, in the order the menu and the layer's brackets list them.</summary>
        public IReadOnlyList<AudioChainPreset> NoisePresets() => AllPresets(LoadPresets()).Where(p => p.ReceiveNoise != null).ToList();

        /// <summary>
        /// The engine a preset mainly belongs to, which decides whose Presets
        /// submenu lists it: the neural slot's engine if on, else the spectral
        /// slot's, else the first radio engine it switches on.
        /// </summary>
        public static string? MainEngineOf(AudioChainPreset preset)
        {
            var n = preset.ReceiveNoise;
            if (n == null) return null;
            if (n.Neural.On && n.Neural.EngineId.Length > 0) return n.Neural.EngineId;
            if (n.Spectral.On && n.Spectral.EngineId.Length > 0) return n.Spectral.EngineId;
            return n.Radio.FirstOrDefault(r => r.On)?.EngineId;
        }

        /// <summary>The presets whose main engine is this one.</summary>
        public IReadOnlyList<AudioChainPreset> PresetsFor(string engineId) => NoisePresets().Where(p => MainEngineOf(p) == engineId).ToList();

        /// <summary>
        /// Apply a preset: only the sections it holds. One journal entry and
        /// one spoken summary naming every engine it switched.
        /// </summary>
        public void ApplyPreset(string name, string source) => ApplyPreset(name, source, DenoiseDirection.Receive);

        /// <summary>
        /// Apply a preset from a road in <paramref name="road"/>'s direction
        /// (Track H2 repair round 1, item 8): the Transmit menu's presets and the
        /// layer's preset brackets after T answer on the transmit preset subject,
        /// so a receive preset answer never replaces an unheard transmit one.
        /// Every section the preset holds is applied, whichever road (part five),
        /// and the summary names each direction it changed.
        /// </summary>
        public void ApplyPreset(string name, string source, DenoiseDirection road)
        {
            using var oneUtterance = OneUtterance();
            var preset = AllPresets(LoadPresets()).FirstOrDefault(p => p.Name == name);
            if (preset == null) return;
            var p = _pipeline();
            // A preset holding only the transmit section needs no receive
            // pipeline: transmit is held with or without a radio (#1077, Track H2).
            if (p == null && (preset.ReceiveNoise != null || preset.TransmitNoise == null)) { NoRadio(); return; }
            var changes = new List<NoiseJournalChange>();
            var phrases = new List<string>();
            var txPhrases = new List<string>();
            var transmitTiming = ApplyNoiseSections(preset, source, changes, phrases, txPhrases);
            if (preset.HoldsTransmitChain) TransmitChain?.Apply(preset);
            NoiseSettingsJournal.Record(new NoiseJournalEntry(NoiseJournalKind.PresetApplied, source, false,
                changes.Count > 0 ? changes : new List<NoiseJournalChange> { new("preset", "", "", name) }, name));
            _persist();
            Say(L("audio.noise_surface.preset_applied", ("name", name), ("summary", SectionSummary(phrases, txPhrases))),
                road == DenoiseDirection.Transmit ? SpeechSubject.TransmitNoisePreset : SpeechSubject.NoisePreset);
            SayAtUnkey(transmitTiming);
        }

        /// <summary>
        /// Each direction a preset changed, named (repair round 1, item 5): the
        /// receive phrases as they always were, then the transmit part; "nothing
        /// to change" when neither changed anything.
        /// </summary>
        private string SectionSummary(List<string> phrases, List<string> txPhrases)
        {
            var parts = new List<string>();
            if (phrases.Count > 0) parts.Add(string.Join(", ", phrases));
            if (txPhrases.Count > 0) parts.Add(L("audio.noise_surface.tx.preset_part", ("list", string.Join(", ", txPhrases))));
            return parts.Count == 0 ? L("audio.noise_surface.preset_nothing_changed") : string.Join("; ", parts);
        }

        /// <summary>
        /// The Audio Workshop's Load (repair round 1, item 6): the preset's noise
        /// sections, after the Workshop has applied its transmit chain itself.
        /// Nothing is spoken here; the Workshop says this with its own "loaded"
        /// sentence. Returns "" for a preset that holds no noise section (every
        /// preset made before the sections existed applies exactly as before),
        /// otherwise the summary, with the at-unkey sentence when the transmit
        /// part waits.
        /// </summary>
        public string ApplyNoiseSectionsForWorkshop(AudioChainPreset preset, string source)
        {
            if (preset == null || (preset.ReceiveNoise == null && preset.TransmitNoise == null)) return "";
            var changes = new List<NoiseJournalChange>();
            var phrases = new List<string>();
            var txPhrases = new List<string>();
            var timing = ApplyNoiseSections(preset, source, changes, phrases, txPhrases);
            NoiseSettingsJournal.Record(new NoiseJournalEntry(NoiseJournalKind.PresetApplied, source, false,
                changes.Count > 0 ? changes : new List<NoiseJournalChange> { new("preset", "", "", preset.Name) }, preset.Name));
            _persist();
            string summary = SectionSummary(phrases, txPhrases);
            return timing.Timing == TxChangeTiming.AtUnkey ? summary + " " + Lexicon.Get("audio.denoise.tx.at_unkey") : summary;
        }

        /// <summary>The noise sections a preset holds, applied: receive (with a pipeline), then transmit. Returns the transmit part's timing.</summary>
        private TxChangeResult ApplyNoiseSections(AudioChainPreset preset, string source, List<NoiseJournalChange> changes, List<string> phrases, List<string> txPhrases)
        {
            var p = _pipeline();
            var transmitTiming = TxChangeResult.Now;
            if (preset.ReceiveNoise is { } noise && p != null)
            {
                using var ownedRx = OwnRx(source); // the layer's own receive change, for its Escape (#1150)
                RetireHelperNotice("a noise preset was applied");
                ApplySlot(p.SpectralSlot, noise.Spectral, changes, phrases, source);
                ApplySlot(p.NeuralSlot, noise.Neural, changes, phrases, source);
                if (_radio() is { } r)
                    foreach (var re in noise.Radio)
                    {
                        if (_catalog.Find(re.EngineId) is not { Side: DenoiseSide.Radio } e) continue;
                        if (RadioRefusal(e, r, out _) != null) continue;
                        // Only a level that differs is written (bench round, item 9): a
                        // same-value write changed nothing yet journalled a change and
                        // set off the radio's "will not survive disconnect" receipt.
                        if (re.Level is int level && e.Level != null && ReadRadioLevel(e) != level)
                        {
                            var lever = Levers(e.Id, false).First();
                            Write(lever, e, new EngineSettingValue(level), source);
                        }
                        if (IsOn(e.Id) != re.On)
                        {
                            WriteRadioSwitch(e, r, re.On, source, byApp: false);
                            changes.Add(new(e.Id, "", Words(!re.On), Words(re.On)));
                            phrases.Add(L(re.On ? "audio.noise_surface.phrase_on" : "audio.noise_surface.phrase_off", ("engine", Lexicon.Get(e.LabelKey))));
                        }
                    }
                // Sprint 49 Track I, repair 1 (N2): the receive equaliser, when the
                // preset carries one (an older preset does not, and changes nothing
                // here). A switch: it takes the receive switch choices (#873).
                if (noise.Eq is { } eq && !SameEq(p.EqualizerSetup, eq))
                {
                    p.SetEqualizer(eq, isSwitch: true);
                    changes.Add(new("equalizer", "", "", "preset"));
                    phrases.Add(L("audio.noise_surface.phrase_eq"));
                }
                RememberModeState();
            }
            if (preset.TransmitNoise is { } transmit)
                using (OwnTx(source)) transmitTiming = ApplyTransmitSection(transmit, changes, txPhrases); // the layer's own, for its Escape
            return transmitTiming;
        }

        /// <summary>
        /// A preset's transmit section (Track H2): both transmit slots through the
        /// chain (a slot change waits for the unkey when keyed, and is said to)
        /// and the gate. Every transmit engine switched on or off is named with
        /// "for transmit", so the one summary covers both directions.
        /// </summary>
        private TxChangeResult ApplyTransmitSection(TxNoiseSetup section, List<NoiseJournalChange> changes, List<string> phrases)
        {
            if (_txChain() is not { } chain) return TxChangeResult.Now;
            // What it WILL change, judged against what the section holds before it
            // is applied (repair round 1, item 5): a section held for the unkey has
            // not changed anything yet, and it still says what it will do. Switches
            // and engines, then each engine's floor and settings, then the gate.
            var (spectralNow, neuralNow) = chain.CaptureSlots();
            foreach (var (family, now, want) in new[] { (DenoiseFamily.Spectral, spectralNow, section.Spectral), (DenoiseFamily.Neural, neuralNow, section.Neural) })
            {
                var target = want ?? new TxNoiseSlotSetup();
                bool wasOn = chain.WantsEnabled(family), on = target.Enabled;
                string wasEngine = chain.ChosenEngineId(family);
                string engine = string.IsNullOrWhiteSpace(target.EngineId) ? TxNoiseChain.DefaultEngine(family) : target.EngineId;
                bool switched = !(wasOn == on && (!on || wasEngine == engine));
                if (switched && wasOn && wasEngine.Length > 0 && (!on || wasEngine != engine))
                {
                    changes.Add(new("tx:" + wasEngine, "", "on", "off"));
                    phrases.Add(L("audio.noise_surface.phrase_off", ("engine", TxName(wasEngine))));
                }
                if (switched && on && engine.Length > 0)
                {
                    changes.Add(new("tx:" + engine, "", "off", "on"));
                    phrases.Add(L("audio.noise_surface.phrase_on", ("engine", TxName(engine))));
                }
                // An off slot whose chosen engine changes is a change too (repair
                // round 2, item 4): it is the engine the slot starts on when turned on.
                if (!switched && !on && wasEngine != engine)
                {
                    changes.Add(new("tx:" + family, "engine", wasEngine, engine));
                    phrases.Add(L("audio.noise_surface.tx.phrase_engine_choice", ("engine", TxName(engine))));
                    continue;
                }
                // The setup replaces every engine's stored values, so any engine
                // whose values differ changes. Named once, by the slot's engine.
                if (!switched && TxEngineValues(now) != TxEngineValues(target))
                {
                    changes.Add(new("tx:" + engine, "settings", "", "preset"));
                    phrases.Add(L("audio.noise_surface.tx.phrase_settings", ("engine", TxName(engine))));
                }
            }
            if (section.Gate is { } gate && !SameGate(TransmitGate.Capture(), gate))
            {
                changes.Add(new("tx:gate", "", "", "preset"));
                phrases.Add(L("audio.noise_surface.tx.phrase_gate"));
            }
            // Sprint 49 Track I, repair 1 (B3): the stages the section also applies
            // (ApplyWholeTransmit puts a missing one back to off), so a preset that
            // changes only these never says "nothing to change".
            var (eqNow, deNow, orderNow) = TransmitGate.CaptureStages();
            if (!SameEq(eqNow, section.Eq ?? new ParametricEqSetup()))
            {
                changes.Add(new("tx:equalizer", "", "", "preset"));
                phrases.Add(L("audio.noise_surface.tx.phrase_eq"));
            }
            if (!new TxNoiseSetup { DeEsser = deNow }.SameContentAs(new TxNoiseSetup { DeEsser = section.DeEsser ?? new TxDeEsserSetup() }))
            {
                changes.Add(new("tx:deesser", "", "", "preset"));
                phrases.Add(L("audio.noise_surface.tx.phrase_deesser"));
            }
            if (!orderNow.SequenceEqual(section.StageOrder ?? JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder))
                changes.Add(new("tx:order", "", "", "preset")); // journal only: this build runs one order
            return ApplyWholeTransmit(section.Spectral ?? new TxNoiseSlotSetup(), section.Neural ?? new TxNoiseSlotSetup(), section.Gate, section);
        }

        /// <summary>
        /// A transmit slot's stored engine values as comparable text. An entry
        /// with no values and its engine's TRANSMIT default floor is the same as
        /// no entry: reading a floor creates one, and reading is not a change.
        /// </summary>
        private static string TxEngineValues(TxNoiseSlotSetup slot) =>
            string.Join("|", (slot.Engines ?? new()).Where(s => s != null
                    && (s.Values.Count > 0 || Math.Abs(s.Strength - NoiseFloor.DefaultStrength(s.EngineId, DenoiseDirection.Transmit)) > 1e-6))
                .OrderBy(s => s.EngineId, StringComparer.Ordinal)
                .Select(s => s.EngineId + ":" + s.Strength.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":"
                    + string.Join(",", s.Values.OrderBy(v => v.Name, StringComparer.Ordinal)
                        .Select(v => v.Name + "=" + (v.Choice ?? v.Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture))))));

        /// <summary>
        /// Whether two gate settings act the same: the hand threshold counts only
        /// while the automatic threshold is off, and every number to the
        /// hundredth. Compared through the record's own serialized form, so a
        /// field added later is compared without anyone remembering to add it
        /// here (phase 2 repair 1, B1: this list was written by hand and missed
        /// the expander's mode and WDSP's depth, so a preset or a named setup
        /// differing only in those did nothing, and the layer's Escape could not
        /// put them back). Presets, the whole-transmit no-op check, the preset
        /// summary and the layer's ownership all ask this one question.
        /// </summary>
        internal static bool SameGate(TxNoiseGateSetup a, TxNoiseGateSetup b) => GateKey(a) == GateKey(b);

        private static readonly System.Xml.Serialization.XmlSerializer GateSerializer = new(typeof(TxNoiseGateSetup));

        private static readonly System.Reflection.PropertyInfo[] GateFloats = typeof(TxNoiseGateSetup).GetProperties()
            .Where(p => p.PropertyType == typeof(float) && p.CanRead && p.CanWrite).ToArray();

        private static string GateKey(TxNoiseGateSetup g)
        {
            var c = g.Clone();
            foreach (var p in GateFloats) p.SetValue(c, MathF.Round((float)p.GetValue(c)!, 2));
            if (c.AutoThreshold) c.ThresholdDb = 0f;
            using var w = new System.IO.StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            GateSerializer.Serialize(w, c);
            return w.ToString();
        }

        /// <summary>Every preset that holds transmit noise reduction (the Transmit menu's Presets, the layer's preset brackets after T).</summary>
        public IReadOnlyList<AudioChainPreset> TransmitNoisePresets() => AllPresets(LoadPresets()).Where(p => p.TransmitNoise != null).ToList();

        private int _txPresetIndex = -1;

        /// <summary>Shift+Alt+brackets in the noise layer after T: apply the next or previous preset that holds transmit noise reduction.</summary>
        public void StepTransmitPreset(int direction, string source)
        {
            var list = TransmitNoisePresets();
            if (list.Count == 0) { Say(L("audio.noise_surface.tx.no_presets"), SpeechSubject.TransmitNoisePreset); return; }
            _txPresetIndex = _txPresetIndex < 0 ? (direction > 0 ? 0 : list.Count - 1)
                : ((_txPresetIndex + Math.Sign(direction)) % list.Count + list.Count) % list.Count;
            ApplyPreset(list[_txPresetIndex].Name, source, DenoiseDirection.Transmit);
        }

        private void ApplySlot(PcDenoiseSlot slot, NoisePresetSlot want, List<NoiseJournalChange> changes, List<string> phrases, string source)
        {
            bool wasOn = slot.Enabled; string wasId = slot.ChosenEngineId;
            string target = want.EngineId.Length > 0 && _catalog.Find(want.EngineId) is { Factory: not null } ? want.EngineId : wasId;
            var records = want.Settings != null ? new[] { want.Settings } : Array.Empty<DenoiseEngineSettings>();
            var before = records.Select(r => (r.EngineId, Values: SettingValues(slot, r.EngineId))).ToList();
            // The mode's rule first (B4), then the settings and the choice,
            // rebuilding an engine already chosen when a rebuild setting
            // changed (B3), so what you hear is what the preset holds.
            slot.RestoreAndSelect(AdmitForMode(slot, records, target, source), target);
            slot.Enabled = want.On;
            if (!(wasOn == slot.Enabled && (!slot.Enabled || wasId == slot.ChosenEngineId)))
            {
                if (slot.Enabled && wasOn && wasId.Length > 0 && wasId != slot.ChosenEngineId)
                {
                    // A swap in an enabled slot: the engine pushed out is named
                    // and journalled too (repair round 1, #1018 item 2).
                    changes.Add(new(slot.ChosenEngineId, "", "off", "on"));
                    changes.Add(new(wasId, "", "on", "off"));
                    phrases.Add(L("audio.noise_surface.phrase_off", ("engine", NameOf(wasId))));
                    phrases.Add(L("audio.noise_surface.phrase_on", ("engine", NameOf(slot.ChosenEngineId))));
                }
                else if (slot.Enabled)
                {
                    changes.Add(new(slot.ChosenEngineId, "", "off", "on"));
                    phrases.Add(L("audio.noise_surface.phrase_on", ("engine", NameOf(slot.ChosenEngineId))));
                }
                else
                {
                    changes.Add(new(wasId, "", "on", "off"));
                    phrases.Add(L("audio.noise_surface.phrase_off", ("engine", NameOf(wasId))));
                }
            }
            // A preset that changes only settings changes something, and says
            // what (B3): one journal change per setting moved, always, and one
            // phrase per setting moved on an engine whose switch this preset
            // left alone. An engine it turns on or off is already named, and
            // reading every lever after "RNNoise on" would bury the switch.
            var switched = changes.Where(c => c.Setting.Length == 0).Select(c => c.EngineId).ToHashSet(StringComparer.Ordinal);
            foreach (var (engineId, was) in before)
                foreach (var (name, now) in SettingValues(slot, engineId))
                {
                    if (was.TryGetValue(name, out string? old) && old == now) continue;
                    changes.Add(new(engineId, name, old ?? "", now));
                    if (!switched.Contains(engineId) && SettingPhrase(slot, engineId, name) is string phrase) phrases.Add(phrase);
                }
        }

        /// <summary>An engine's noise floor and every setting as words, for the before-and-after of a preset (#1019: the floor, never the stored Strength).</summary>
        private Dictionary<string, string> SettingValues(PcDenoiseSlot slot, string engineId)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_catalog.Find(engineId) is not { Side: DenoiseSide.Pc } e || e.Family != slot.Family) return values;
            values[FloorKey] = FloorText(slot.GetStrength(engineId));
            foreach (var d in slot.SettingsFor(engineId))
                values[d.Name] = Describe(slot.GetSetting(engineId, d.Name));
            return values;
        }

        /// <summary>One moved setting as the preset summary says it: "NNR model, Standard voice".</summary>
        private string? SettingPhrase(PcDenoiseSlot slot, string engineId, string name)
        {
            if (_catalog.Find(engineId) is not { } e) return null;
            var lever = name == FloorKey
                ? FloorLeverOf(engineId)
                : e.Settings.FirstOrDefault(d => d.Name == name) is { } d ? new NoiseLever(engineId, d.Name, d.LabelKey, NoiseLeverKind.Setting, d) : null;
            if (lever == null) return null;
            return L("audio.noise_surface.phrase_setting", ("engine", Lexicon.Get(e.LabelKey)),
                ("lever", LeverName(lever).ToLowerInvariant()), ("value", ValueText(lever)));
        }

        /// <summary>What a save of the current settings would hold.</summary>
        public ReceiveNoisePresetSection CaptureReceiveNoise()
        {
            var section = new ReceiveNoisePresetSection();
            if (_pipeline() is { } p)
            {
                section.Spectral = CaptureSlot(p.SpectralSlot);
                section.Neural = CaptureSlot(p.NeuralSlot);
                section.Eq = p.EqualizerSetup; // Sprint 49 Track I, repair 1 (N2)
            }
            if (_radio() is { } r)
                foreach (var e in Engines().Where(e => e.Side == DenoiseSide.Radio))
                    section.Radio.Add(new NoisePresetRadioEngine { EngineId = e.Id, On = IsOn(e.Id), Level = e.Level != null ? ReadRadioLevel(e) : null });
            return section;
        }

        private static NoisePresetSlot CaptureSlot(PcDenoiseSlot slot) => new()
        {
            On = slot.Enabled,
            EngineId = slot.ChosenEngineId,
            Settings = slot.ExportSettings().FirstOrDefault(s => s.EngineId == slot.ChosenEngineId)
                ?? new DenoiseEngineSettings { EngineId = slot.ChosenEngineId, Direction = DenoiseDirection.Receive, Strength = slot.GetStrength(slot.ChosenEngineId) },
        };

        /// <summary>Every preset in the operator's store, the transmit-chain ones and the noise ones, by name.</summary>
        public IReadOnlyList<string> AllPresetNames() => AllPresets(LoadPresets()).Select(p => p.Name).ToList();

        /// <summary>Whether a preset of this name exists already (the Save dialog asks before replacing).</summary>
        public bool PresetExists(string name) => AllPresets(LoadPresets()).Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Save the current settings as a preset. A noise-only preset is
        /// stored where an older build cannot read it. A collision replaces
        /// or keeps both ("Name 2"), as asked. Returns the name saved under,
        /// or null when nothing was saved.
        /// </summary>
        public string? SavePreset(string name, bool includeNoise, bool includeChain, PresetCollision collision, string source) =>
            SavePreset(name, includeNoise, includeChain, includeTransmitNoise: false, collision, source);

        /// <summary>
        /// As <see cref="SavePreset(string, bool, bool, PresetCollision, string)"/>, with
        /// the transmit noise section too (Track H2; design part five's third
        /// checkbox, "Include transmit noise reduction"): both transmit slots and
        /// the gate, as a transmit noise setup records them.
        /// </summary>
        public string? SavePreset(string name, bool includeNoise, bool includeChain, bool includeTransmitNoise, PresetCollision collision, string source)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || (!includeNoise && !includeChain && !includeTransmitNoise)) return null;
            var store = LoadPresets();
            var existing = AllPresets(store).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && collision == PresetCollision.Ask) return null;
            if (existing != null && collision == PresetCollision.KeepBoth)
            {
                int n = 2;
                while (AllPresets(store).Any(p => string.Equals(p.Name, name + " " + n, StringComparison.OrdinalIgnoreCase))) n++;
                name = name + " " + n;
                existing = null;
            }
            if (existing != null) { store.Presets.Remove(existing); store.NoisePresets.Remove(existing); }
            AudioChainPreset preset = includeChain && TransmitChain != null ? TransmitChain.Capture(name) : new AudioChainPreset(name);
            preset.SchemaVersion = AudioChainPreset.CurrentSchemaVersion;
            preset.HoldsTransmitChain = includeChain && TransmitChain != null;
            preset.ReceiveNoise = includeNoise ? CaptureReceiveNoise() : null;
            preset.TransmitNoise = includeTransmitNoise ? CaptureTransmitSetup(name) : null;
            if (!preset.HoldsTransmitChain && preset.ReceiveNoise == null && preset.TransmitNoise == null) return null;
            (preset.HoldsTransmitChain ? store.Presets : store.NoisePresets).Add(preset);
            if (PresetStore?.Save(store) != true) return null;
            NoiseSettingsJournal.Record(NoiseJournalKind.PresetApplied, source, "preset", "saved", "", name);
            PresetsChanged?.Invoke();
            // A preset that holds only transmit noise reduction is a transmit answer (item 8).
            bool transmitOnly = preset.TransmitNoise != null && preset.ReceiveNoise == null && !preset.HoldsTransmitChain;
            Say(L("audio.noise_surface.preset_saved", ("name", name), ("what", L(PresetWhatKey(preset)))),
                transmitOnly ? SpeechSubject.TransmitNoisePreset : SpeechSubject.NoisePreset);
            return name;
        }

        /// <summary>
        /// Which sections a saved preset holds, as the saved sentence names them.
        /// Every combination has its own key, so the coverage scan sees each one
        /// and Codex words each one (the three with a transmit section are Track
        /// H2's placeholders).
        /// </summary>
        private static string PresetWhatKey(AudioChainPreset preset) =>
            (preset.ReceiveNoise != null, preset.TransmitNoise != null, preset.HoldsTransmitChain) switch
            {
                (true, false, true) => "audio.noise_surface.preset_what.both",
                (false, false, true) => "audio.noise_surface.preset_what.chain",
                (true, false, false) => "audio.noise_surface.preset_what.noise",
                (false, true, false) => "audio.noise_surface.preset_what.tx_noise",
                (true, true, false) => "audio.noise_surface.preset_what.rx_tx_noise",
                (false, true, true) => "audio.noise_surface.preset_what.tx_noise_chain",
                _ => "audio.noise_surface.preset_what.everything",
            };

        /// <summary>Rename a preset; refused (false) when the new name is taken or empty.</summary>
        public bool RenamePreset(string oldName, string newName)
        {
            newName = (newName ?? "").Trim();
            var store = LoadPresets();
            var preset = AllPresets(store).FirstOrDefault(p => p.Name == oldName);
            if (preset == null || newName.Length == 0 || AllPresets(store).Any(p => p != preset && string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
                return false;
            preset.Name = newName;
            if (PresetStore?.Save(store) != true) return false;
            Say(L("audio.noise_surface.preset_renamed", ("old", oldName), ("new", newName)), SpeechSubject.NoisePreset);
            PresetsChanged?.Invoke();
            return true;
        }

        public bool DeletePreset(string name)
        {
            var store = LoadPresets();
            var preset = AllPresets(store).FirstOrDefault(p => p.Name == name);
            if (preset == null) return false;
            store.Presets.Remove(preset); store.NoisePresets.Remove(preset);
            if (PresetStore?.Save(store) != true) return false;
            Say(L("audio.noise_surface.preset_deleted", ("name", name)), SpeechSubject.NoisePreset);
            PresetsChanged?.Invoke();
            return true;
        }

        private int _presetIndex = -1;

        /// <summary>Shift+Alt+brackets in the noise layer: apply the next or previous preset that holds receive noise reduction.</summary>
        public void StepPreset(int direction, string source)
        {
            var list = NoisePresets();
            if (list.Count == 0) { Say(L("audio.noise_surface.no_presets"), SpeechSubject.NoisePreset); return; }
            _presetIndex = _presetIndex < 0 ? (direction > 0 ? 0 : list.Count - 1)
                : ((_presetIndex + Math.Sign(direction)) % list.Count + list.Count) % list.Count;
            ApplyPreset(list[_presetIndex].Name, source);
        }

        /// <summary>The layer's favourite or defaults key with no engine chosen, on the layer's direction's subject (Track H2 repair round 1, item 8).</summary>
        public void SpeakNoEngineYet() => Say(L(NoEngineYetKey), LayerSubject(SpeechSubject.NoiseLevel));
    }
}
