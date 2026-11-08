using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    public sealed partial class NoiseController
    {
        // ────────────────────────────────────────────────────────────────
        //  Per-mode memory (E6): replaces the retired "Voice Modes Only" gate
        // ────────────────────────────────────────────────────────────────

        public NoiseModeMemoryConfig? Memory => _config()?.NoiseModeMemory;

        /// <summary>The mode group the controller believes the radio is in.</summary>
        public NoiseModeGroup CurrentGroup => _group ?? NoiseModeRules.GroupOf(_radio()?.Mode);

        /// <summary>
        /// Connect: the saved settings are in the slots; now the current mode's
        /// group is entered as any mode change enters one, so a group with
        /// nothing remembered gets its starting point if a slot is on (ruled
        /// 2026-11-07 21:18, question 6). Listen to what was removed is
        /// cleared on every connect.
        /// </summary>
        public void Attach(string mode)
        {
            _group = null;
            WriteRadioIntents("connect");
            _zeroRestore = null;
            ClearListening();
            EnterGroup(mode, attaching: true);
        }

        /// <summary>
        /// Write every held intent for the four levels the radio never reports
        /// back (ruled 2026-11-07, question 16), on connect and when the
        /// active slice changes: those levels are per slice, and the app cannot
        /// otherwise make a level it cannot read true on the new slice. An
        /// intent never set writes nothing. Each write is traced.
        /// </summary>
        public void WriteRadioIntents(string why)
        {
            var r = _radio();
            if (r == null || r.Serial.Length == 0) return;
            foreach (var e in _catalog.Entries.Where(e => e.Side == DenoiseSide.Radio && e.Level is { ReportedBack: false }))
            {
                if (_intents.Get(r.Serial, e.Id) is not int level) continue;
                if (RadioRefusal(e, r, out _) != null) continue;
                level = Math.Clamp(level, e.Level!.Min, e.Level.Max);
                r.WriteLevel(e, level);
                System.Diagnostics.Trace.WriteLine($"NoiseController: wrote {e.Id} level {level} from the held intent ({why})");
            }
        }

        private int? _activeSlice;

        /// <summary>
        /// The active slice, reported by the surfaces' poll and by the layer's
        /// slice jump (LayerSliceJump, the moment it happens). A change writes
        /// the held intents to the new slice, forgets what was believed about
        /// the old slice's switches and levels, and, while the noise layer is
        /// open, reads the radio part of its Escape picture afresh on the new
        /// slice (repair round 1, B1): leaving a slice confirms that slice's
        /// work, so Escape never writes the old slice's settings onto this one.
        /// </summary>
        public void NoteActiveSlice(int slice)
        {
            if (_activeSlice == slice) return;
            bool first = _activeSlice == null;
            _activeSlice = slice;
            if (first)
            {
                if (_live is { Slice: null } unread) unread.Slice = slice;
                return;
            }
            // A shadow is a value written on the slice that was active then.
            _switchShadow.Clear();
            _levelShadow.Clear();
            WriteRadioIntents("the active slice changed");
            if (_live is { } snap) ReadRadioPart(snap);
        }

        public void Detach()
        {
            _activeSlice = null;
            _live = null;
            _group = null;
            _zeroRestore = null;
            foreach (var pending in _settling.Values) pending.Dispose();
            _settling.Clear();
            _pendingChoice.Clear();
            _switchShadow.Clear();
            _levelShadow.Clear();
            Current = null;
        }

        /// <summary>
        /// A mode change. Entering a group the operator has used puts back
        /// what it remembers, and says so only if something changed. Entering
        /// a group with nothing remembered while a computer slot is on applies
        /// that group's starting point, and says so. NNR's model and NR2's
        /// noise tracking follow CW.
        /// </summary>
        public void OnModeChanged(string mode) => EnterGroup(mode, attaching: false);

        private void EnterGroup(string mode, bool attaching)
        {
            var g = NoiseModeRules.GroupOf(mode);
            var old = _group;
            if (old == g) return;
            _group = g;
            var mem = Memory;
            var p = _pipeline();
            if (mem == null || p == null) return;

            // At connect nothing has been entered in this session, but the
            // slots hold what was saved, in the group the memory names (B6).
            // Coming back to that group is not a change of group: CW's own
            // NR2 tracking, saved as the current setting, stays CW's and is
            // never written over the voice one. A config from before the
            // group was recorded falls back to treating connect as no group.
            NoiseModeGroup? from = old ?? (attaching ? StoredGroup(mem) : null);
            if (from != null || g == NoiseModeGroup.Cw) SwapNr2Tracking(from, g, mem, p);
            var gm = mem.For(g);
            if (gm.Known) RestoreGroup(g, gm, p);
            else if (p.SpectralSlot.Enabled || p.NeuralSlot.Enabled) ApplyStartingPoint(g, "mode change", onlyNeural: false);
            if (g == NoiseModeGroup.Cw) EnsureCwModel(speak: true);
            else if (from == NoiseModeGroup.Cw) GiveBackVoiceModel(speak: true);
            mem.LastGroup = GroupWord(g);
            _persist();
        }

        private static NoiseModeGroup? StoredGroup(NoiseModeMemoryConfig mem) => mem.LastGroup switch
        {
            "voice" => NoiseModeGroup.Voice,
            "cw" => NoiseModeGroup.Cw,
            "digital" => NoiseModeGroup.Digital,
            _ => null,
        };

        private void RestoreGroup(NoiseModeGroup g, NoiseGroupMemory gm, RxAudioPipeline p)
        {
            var phrases = new List<string>();
            var changes = new List<NoiseJournalChange>();
            void Apply(PcDenoiseSlot slot, NoiseSlotMemory want)
            {
                string beforeId = slot.ChosenEngineId; bool beforeOn = slot.Enabled;
                if (want.EngineId.Length > 0 && want.EngineId != slot.ChosenEngineId && _catalog.Find(want.EngineId) is { Factory: not null })
                    slot.Select(want.EngineId, out _);
                slot.Enabled = want.On;
                if (beforeOn == slot.Enabled && (!slot.Enabled || beforeId == slot.ChosenEngineId)) return;
                if (beforeOn && beforeId != slot.ChosenEngineId && slot.Enabled)
                {
                    phrases.Add(L("audio.noise_surface.phrase_on", ("engine", NameOf(slot.ChosenEngineId))));
                    changes.Add(new(slot.ChosenEngineId, "", "off", "on"));
                }
                else if (slot.Enabled)
                {
                    phrases.Add(L("audio.noise_surface.phrase_on", ("engine", NameOf(slot.ChosenEngineId))));
                    changes.Add(new(slot.ChosenEngineId, "", "off", "on"));
                }
                else
                {
                    phrases.Add(L("audio.noise_surface.phrase_off", ("engine", NameOf(beforeId))));
                    changes.Add(new(beforeId, "", "on", "off"));
                }
            }
            Apply(p.SpectralSlot, gm.Spectral);
            Apply(p.NeuralSlot, gm.Neural);
            if (phrases.Count == 0) return;
            NoiseSettingsJournal.Record(new NoiseJournalEntry(NoiseJournalKind.ModeRestore, "mode change", true, changes));
            Say(L("audio.noise_surface.mode_restore", ("group", L(g switch { NoiseModeGroup.Cw => "audio.noise_surface.group.cw", NoiseModeGroup.Digital => "audio.noise_surface.group.digital", _ => "audio.noise_surface.group.voice" })),
                ("changes", string.Join(", ", phrases))), SpeechSubject.NoiseModeStart);
        }

        private static string GroupWord(NoiseModeGroup g) => g switch
        {
            NoiseModeGroup.Cw => "cw",
            NoiseModeGroup.Digital => "digital",
            _ => "voice",
        };

        /// <summary>
        /// A group's starting point, applied by JJ Flexible: voice,
        /// DeepFilterNet3 (RNNoise where it cannot run), spectral off; CW, NNR
        /// on a CW model chosen by the measured rule, spectral off; digital,
        /// the computer's engines off (ruling 5, plan ruling 6). With
        /// <paramref name="onlyNeural"/> (the operator turned the neural slot
        /// on) the spectral slot is left as it is.
        /// </summary>
        private void ApplyStartingPoint(NoiseModeGroup g, string source, bool onlyNeural)
        {
            var p = _pipeline();
            var mem = Memory;
            if (p == null) return;
            var changes = new List<NoiseJournalChange>();
            if (!onlyNeural && p.SpectralSlot.Enabled)
            {
                changes.Add(new(p.SpectralSlot.ChosenEngineId, "", "on", "off"));
                p.SpectralSlot.Enabled = false;
            }
            string? sentence = null;
            switch (g)
            {
                case NoiseModeGroup.Voice:
                {
                    var dfn = _catalog.Find("deepfilternet3");
                    bool dfnRuns = dfn?.Factory != null && SafeAvailability(dfn) == Unavailability.None;
                    string id = dfnRuns ? "deepfilternet3" : "rnnoise";
                    if (_catalog.Find(id) is not { Factory: not null } e) break;
                    StartNeural(p, e, changes);
                    sentence = dfnRuns
                        ? L("audio.noise_surface.start_voice", ("limit", ValueText(new NoiseLever("deepfilternet3", "attenuation_limit_db", "", NoiseLeverKind.Setting,
                            e.Settings.First(d => d.Name == "attenuation_limit_db")))))
                        : L("audio.noise_surface.start_voice_rnnoise");
                    break;
                }
                case NoiseModeGroup.Cw:
                {
                    if (_catalog.Find("nnr") is not { Factory: not null } e) break;
                    var (model, measured, fast) = NoiseModeRules.ChooseCwModel(_cwCost);
                    string current = p.NeuralSlot.GetSetting("nnr", "model").Choice ?? "standard";
                    if (mem != null && !NoiseModeRules.IsCwModel(current)) mem.NnrVoiceModelOwed = current;
                    p.NeuralSlot.TrySetSetting("nnr", "model", new EngineSettingValue(0, model));
                    StartNeural(p, e, changes);
                    var reason = SafeAvailability(e);
                    if (reason != Unavailability.None) { SpeakUnavailable(e, reason, kept: true); sentence = null; break; }
                    sentence = L(!measured ? "audio.noise_surface.start_cw_unmeasured"
                        : fast ? "audio.noise_surface.start_cw_fast" : "audio.noise_surface.start_cw_slow");
                    break;
                }
                default:
                    if (p.NeuralSlot.Enabled)
                    {
                        changes.Add(new(p.NeuralSlot.ChosenEngineId, "", "on", "off"));
                        p.NeuralSlot.Enabled = false;
                    }
                    if (changes.Count > 0) sentence = L("audio.noise_surface.start_digital");
                    break;
            }
            if (mem != null) { var gm = mem.For(g); gm.Known = true; Capture(gm, p); }
            NoiseSettingsJournal.Record(new NoiseJournalEntry(NoiseJournalKind.StartingPoint, source, true, changes, GroupWord(g)));
            _persist();
            if (sentence != null) Say(sentence, SpeechSubject.NoiseModeStart);
        }

        private void StartNeural(RxAudioPipeline p, DenoiseEngineEntry e, List<NoiseJournalChange> changes)
        {
            string before = p.NeuralSlot.Enabled ? p.NeuralSlot.ChosenEngineId : "";
            p.NeuralSlot.Select(e.Id, out _);
            p.NeuralSlot.Enabled = true;
            Current = e.Id; CurrentLeverIndex = 0;
            if (before != e.Id) changes.Add(new(e.Id, "", "off", "on"));
        }

        private static void Capture(NoiseGroupMemory gm, RxAudioPipeline p)
        {
            gm.Spectral = new NoiseSlotMemory { On = p.SpectralSlot.Enabled, EngineId = p.SpectralSlot.ChosenEngineId };
            gm.Neural = new NoiseSlotMemory { On = p.NeuralSlot.Enabled, EngineId = p.NeuralSlot.ChosenEngineId };
        }

        /// <summary>The current group remembers the slots as they are now: called after every operator change to a computer slot.</summary>
        private void RememberModeState()
        {
            if (Memory is not { } mem || _pipeline() is not { } p) return;
            var gm = mem.For(CurrentGroup);
            gm.Known = true;
            Capture(gm, p);
        }

        /// <summary>
        /// NR2's noise tracking is the one engine setting that depends on the
        /// mode (S17): CW keeps its own, Steady until the operator chooses
        /// another; every other mode shares the app-wide one.
        /// </summary>
        private void SwapNr2Tracking(NoiseModeGroup? old, NoiseModeGroup now, NoiseModeMemoryConfig mem, RxAudioPipeline p)
        {
            if (_catalog.Find("nr2") == null) return;
            string current;
            try { current = p.SpectralSlot.GetSetting("nr2", "noise_estimator").Choice ?? ""; }
            catch (ArgumentException) { return; }
            if (now == NoiseModeGroup.Cw && old != NoiseModeGroup.Cw)
            {
                mem.Nr2OtherTracking = current;
                string cw = mem.Nr2CwTracking.Length > 0 ? mem.Nr2CwTracking : NoiseModeRules.Nr2CwTrackingDefault;
                mem.Nr2CwTracking = cw;
                p.SpectralSlot.TrySetSetting("nr2", "noise_estimator", new EngineSettingValue(0, cw));
            }
            else if (old == NoiseModeGroup.Cw && now != NoiseModeGroup.Cw)
            {
                mem.Nr2CwTracking = current;
                if (mem.Nr2OtherTracking.Length > 0)
                    p.SpectralSlot.TrySetSetting("nr2", "noise_estimator", new EngineSettingValue(0, mem.Nr2OtherTracking));
            }
        }

        private void RememberNr2Tracking()
        {
            if (Memory is not { } mem || _pipeline() is not { } p) return;
            string current = p.SpectralSlot.GetSetting("nr2", "noise_estimator").Choice ?? "";
            if (CurrentGroup == NoiseModeGroup.Cw) mem.Nr2CwTracking = current; else mem.Nr2OtherTracking = current;
        }

        private void AfterSettingChanged(DenoiseEngineEntry e, EngineSettingDescriptor d, EngineSettingValue value)
        {
            if (e.Id == "nr2" && d.Name == "noise_estimator") RememberNr2Tracking();
            if (e.Id == "nnr" && d.Name == "model" && Memory is { } mem && CurrentGroup != NoiseModeGroup.Cw)
                mem.NnrVoiceModelOwed = ""; // an operator's own choice outside CW is not owed back
        }

        private void SetModelQuietly(DenoiseEngineEntry e, PcDenoiseSlot slot, string model)
        {
            if (e.Settings.Any(d => d.Name == "model")) slot.TrySetSetting(e.Id, "model", new EngineSettingValue(0, model));
        }

        /// <summary>Choosing an engine applies the mode's rules first: NNR in CW takes a CW model.</summary>
        private void ApplyModeRulesForChoice(DenoiseEngineEntry e, PcDenoiseSlot slot, bool speak)
        {
            if (e.Id == "nnr" && CurrentGroup == NoiseModeGroup.Cw) EnsureCwModel(speak, choosing: true);
        }

        /// <summary>In CW, NNR offers only its CW models: a stored voice model swaps to the CW model the measured rule picks, and is owed back on leaving CW.</summary>
        private void EnsureCwModel(bool speak, bool choosing = false)
        {
            var p = _pipeline();
            if (p == null || _catalog.Find("nnr") == null) return;
            if (p.NeuralSlot.ChosenEngineId != "nnr" && !choosing) return;
            string current = p.NeuralSlot.GetSetting("nnr", "model").Choice ?? "standard";
            if (NoiseModeRules.IsCwModel(current)) return;
            string model = NoiseModeRules.ChooseCwModel(_cwCost).Model;
            if (Memory is { } mem) mem.NnrVoiceModelOwed = current;
            p.NeuralSlot.TrySetSetting("nnr", "model", new EngineSettingValue(0, model));
            NoiseSettingsJournal.Record(NoiseJournalKind.Model, "mode change", "nnr", "model", current, model, byJjFlexible: true);
            if (speak && (p.NeuralSlot.Enabled || choosing))
                Say(L("audio.noise_surface.cw_model", ("model", ModelName(model))), SpeechSubject.NoiseModeStart);
        }

        private void GiveBackVoiceModel(bool speak)
        {
            var p = _pipeline();
            if (p == null || Memory is not { } mem || mem.NnrVoiceModelOwed.Length == 0 || _catalog.Find("nnr") == null) return;
            string owed = mem.NnrVoiceModelOwed;
            mem.NnrVoiceModelOwed = "";
            string current = p.NeuralSlot.GetSetting("nnr", "model").Choice ?? "";
            if (!NoiseModeRules.IsCwModel(current)) return;
            p.NeuralSlot.TrySetSetting("nnr", "model", new EngineSettingValue(0, owed));
            NoiseSettingsJournal.Record(NoiseJournalKind.Model, "mode change", "nnr", "model", current, owed, byJjFlexible: true);
            if (speak && p.NeuralSlot.Enabled && p.NeuralSlot.ChosenEngineId == "nnr")
                Say(L("audio.noise_surface.voice_model_back", ("model", ModelName(owed))), SpeechSubject.NoiseModeStart);
        }

        private string ModelName(string model) =>
            DenoiseEngineCatalog.NnrMirror[0].Options!.FirstOrDefault(o => o.Name == model) is { } o ? Lexicon.Get(o.LabelKey) : model;
    }
}
