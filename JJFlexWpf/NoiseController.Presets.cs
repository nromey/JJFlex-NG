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
        public void ApplyPreset(string name, string source)
        {
            var preset = AllPresets(LoadPresets()).FirstOrDefault(p => p.Name == name);
            if (preset == null) return;
            if (_pipeline() is not { } p) { NoRadio(); return; }
            var changes = new List<NoiseJournalChange>();
            var phrases = new List<string>();
            if (preset.ReceiveNoise is { } noise)
            {
                ApplySlot(p.SpectralSlot, noise.Spectral, changes, phrases);
                ApplySlot(p.NeuralSlot, noise.Neural, changes, phrases);
                if (_radio() is { } r)
                    foreach (var re in noise.Radio)
                    {
                        if (_catalog.Find(re.EngineId) is not { Side: DenoiseSide.Radio } e) continue;
                        if (RadioRefusal(e, r, out _) != null) continue;
                        if (re.Level is int level && e.Level != null)
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
                RememberModeState();
            }
            if (preset.HoldsTransmitChain) TransmitChain?.Apply(preset);
            NoiseSettingsJournal.Record(new NoiseJournalEntry(NoiseJournalKind.PresetApplied, source, false,
                changes.Count > 0 ? changes : new List<NoiseJournalChange> { new("preset", "", "", name) }, name));
            _persist();
            Say(L("audio.noise_surface.preset_applied", ("name", name),
                ("summary", phrases.Count == 0 ? L("audio.noise_surface.preset_nothing_changed") : string.Join(", ", phrases))), SpeechSubject.NoisePreset);
        }

        private void ApplySlot(PcDenoiseSlot slot, NoisePresetSlot want, List<NoiseJournalChange> changes, List<string> phrases)
        {
            bool wasOn = slot.Enabled; string wasId = slot.ChosenEngineId;
            string target = want.EngineId.Length > 0 && _catalog.Find(want.EngineId) is { Factory: not null } ? want.EngineId : wasId;
            var records = want.Settings != null ? new[] { want.Settings } : Array.Empty<DenoiseEngineSettings>();
            var before = records.Select(r => (r.EngineId, Values: SettingValues(slot, r.EngineId))).ToList();
            // The mode's rule first (B4), then the settings and the choice,
            // rebuilding an engine already chosen when a rebuild setting
            // changed (B3), so what you hear is what the preset holds.
            slot.RestoreAndSelect(AdmitForMode(slot, records, target), target);
            slot.Enabled = want.On;
            if (!(wasOn == slot.Enabled && (!slot.Enabled || wasId == slot.ChosenEngineId)))
            {
                if (slot.Enabled)
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

        /// <summary>An engine's Strength and every setting as words, for the before-and-after of a preset.</summary>
        private Dictionary<string, string> SettingValues(PcDenoiseSlot slot, string engineId)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_catalog.Find(engineId) is not { Side: DenoiseSide.Pc } e || e.Family != slot.Family) return values;
            values["strength"] = Pct(slot.GetStrength(engineId));
            foreach (var d in slot.SettingsFor(engineId))
                values[d.Name] = Describe(slot.GetSetting(engineId, d.Name));
            return values;
        }

        /// <summary>One moved setting as the preset summary says it: "NNR model, Standard voice".</summary>
        private string? SettingPhrase(PcDenoiseSlot slot, string engineId, string name)
        {
            if (_catalog.Find(engineId) is not { } e) return null;
            var lever = name == "strength"
                ? new NoiseLever(engineId, "strength", "audio.noise_surface.lever.strength", NoiseLeverKind.Strength)
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
        public string? SavePreset(string name, bool includeNoise, bool includeChain, PresetCollision collision, string source)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || (!includeNoise && !includeChain)) return null;
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
            if (!preset.HoldsTransmitChain && preset.ReceiveNoise == null) return null;
            (preset.HoldsTransmitChain ? store.Presets : store.NoisePresets).Add(preset);
            if (PresetStore?.Save(store) != true) return null;
            NoiseSettingsJournal.Record(NoiseJournalKind.PresetApplied, source, "preset", "saved", "", name);
            string what = preset.HoldsTransmitChain && preset.ReceiveNoise != null ? "both" : preset.HoldsTransmitChain ? "chain" : "noise";
            Say(L("audio.noise_surface.preset_saved", ("name", name), ("what", L("audio.noise_surface.preset_what." + what))), SpeechSubject.NoisePreset);
            return name;
        }

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

        public void SpeakNoEngineYet() => Say(L("audio.noise_surface.no_engine_yet"), SpeechSubject.NoiseLevel);
    }
}
