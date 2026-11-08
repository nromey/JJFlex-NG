using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    public sealed partial class NoiseController
    {
        // ────────────────────────────────────────────────────────────────
        //  Levers
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// An engine's levers in order: the primary first (a radio engine's
        /// Level, a computer engine's Strength), then the normal levers, then,
        /// when asked, the Advanced ones and Listen to what was removed. A
        /// lever that means nothing at the moment (NR2's trained values while
        /// the gain curve is not Trained) is left out.
        /// </summary>
        public IReadOnlyList<NoiseLever> Levers(string id, bool includeAdvanced)
        {
            var e = _catalog.Find(id);
            var list = new List<NoiseLever>();
            if (e == null) return list;
            if (e.Side == DenoiseSide.Radio)
            {
                if (e.Level != null) list.Add(new NoiseLever(id, "level", "audio.noise_surface.lever.level", NoiseLeverKind.RadioLevel));
                return list;
            }
            list.Add(new NoiseLever(id, "strength", "audio.noise_surface.lever.strength", NoiseLeverKind.Strength));
            foreach (var d in e.Settings.Where(d => !d.Advanced && Shown(e, d)))
                list.Add(new NoiseLever(id, d.Name, d.LabelKey, NoiseLeverKind.Setting, d));
            if (includeAdvanced)
            {
                foreach (var d in e.Settings.Where(d => d.Advanced && Shown(e, d)))
                    list.Add(new NoiseLever(id, d.Name, d.LabelKey, NoiseLeverKind.Setting, d, Advanced: true));
                list.Add(new NoiseLever(id, "listen_removed", "audio.noise_surface.lever.listen_removed", NoiseLeverKind.ListenRemoved, Advanced: true));
            }
            return list;
        }

        private bool Shown(DenoiseEngineEntry e, EngineSettingDescriptor d)
        {
            if (d.ShownWhen is not (string setting, string choice)) return true;
            var slot = SlotFor(e);
            if (slot == null) return false;
            try { return slot.GetSetting(e.Id, setting).Choice == choice; }
            catch (ArgumentException) { return false; }
        }

        /// <summary>The options a choice lever offers here: NNR's model offers only the two CW models in CW (ruled 2026-11-04 20:23).</summary>
        public IReadOnlyList<EngineSettingOption> OptionsFor(NoiseLever lever)
        {
            var options = lever.Descriptor?.Options ?? Array.Empty<EngineSettingOption>();
            if (lever.EngineId == "nnr" && lever.Key == "model")
            {
                if (CurrentGroup == NoiseModeGroup.Cw) return options.Where(o => NoiseModeRules.IsCwModel(o.Name)).ToList();
            }
            return options;
        }

        /// <summary>A lever's value as a number on its own scale (a level, a percent, decibels, Depth); null for a choice.</summary>
        public double? NumberOf(NoiseLever lever)
        {
            var e = _catalog.Find(lever.EngineId);
            if (e == null) return null;
            switch (lever.Kind)
            {
                case NoiseLeverKind.RadioLevel:
                    return ReadRadioLevel(e);
                case NoiseLeverKind.Strength:
                    return SlotFor(e) is { } s ? Math.Round(s.GetStrength(e.Id) * 100) : null;
                case NoiseLeverKind.ListenRemoved:
                    return SlotFor(e)?.ListenToRemoved == true ? 1 : 0;
                default:
                    if (SlotFor(e) is not { } slot || lever.Descriptor is not { } d || d.Kind == EngineSettingKind.Choice) return null;
                    double v = slot.GetSetting(e.Id, d.Name).Number;
                    return ToShown(lever, v);
            }
        }

        /// <summary>A choice lever's current choice name, or null.</summary>
        public string? ChoiceOf(NoiseLever lever)
        {
            var e = _catalog.Find(lever.EngineId);
            if (e == null || lever.Descriptor is not { Kind: EngineSettingKind.Choice } d || SlotFor(e) is not { } slot) return null;
            return slot.GetSetting(e.Id, d.Name).Choice;
        }

        private static bool IsPercent(EngineSettingDescriptor d) =>
            d.Name is "subtraction_amount" or "spectral_floor" or "smoothing" or "whitening" or "masking_depth" or "suppression";

        private static double ToShown(NoiseLever lever, double v)
        {
            if (lever.Inverted) return -v;
            if (lever.Descriptor is { } d && IsPercent(d)) return Math.Round(v * 100);
            return v;
        }

        private static double FromShown(NoiseLever lever, double shown)
        {
            if (lever.Inverted) return -shown;
            if (lever.Descriptor is { } d && IsPercent(d)) return Math.Round(shown / 100, 6);
            return shown;
        }

        /// <summary>The unit key a lever's number is spoken with, or null.</summary>
        public static string? UnitOf(NoiseLever lever) => lever.Kind switch
        {
            NoiseLeverKind.Strength => "audio.noise_surface.unit.percent",
            NoiseLeverKind.Setting when lever.Descriptor is { } d && IsPercent(d) => "audio.noise_surface.unit.percent",
            NoiseLeverKind.Setting when lever.Descriptor is { UnitKey: "audio.denoise.db" or "audio.unit.decibels" } => "audio.unit.decibels",
            _ => null,
        };

        /// <summary>The lever's value as it is spoken and shown, with its unit at Chatty.</summary>
        public string ValueText(NoiseLever lever, bool withUnit = true)
        {
            if (lever.Kind == NoiseLeverKind.ListenRemoved)
                return Lexicon.Get(NumberOf(lever) == 1 ? "audio.noise_surface.state.on" : "audio.noise_surface.state.off");
            if (lever.Descriptor is { Kind: EngineSettingKind.Choice } d)
            {
                string? choice = ChoiceOf(lever);
                var option = d.Options?.FirstOrDefault(o => o.Name == choice);
                return option != null ? Lexicon.Get(option.LabelKey) : choice ?? "";
            }
            double? n = NumberOf(lever);
            if (n == null) return "";
            if (lever.EngineId == "deepfilternet3" && lever.Key == "attenuation_limit_db" && n.Value >= 100)
                return Lexicon.Get("audio.noise_surface.unlimited");
            string number = n.Value.ToString(lever.Descriptor is { Step: < 0.1 } x && !IsPercent(x) ? "0.###" : "0.#", CultureInfo.InvariantCulture);
            return withUnit ? WithUnit(number, UnitOf(lever)) : number;
        }

        /// <summary>
        /// Every value a lever can take, least to most, as a field lists them:
        /// the number on the lever's own scale (what <see cref="Set"/> takes)
        /// or the choice, and its text with its unit. A Home field arrows
        /// through exactly what the layer's Up and Down step through.
        /// </summary>
        public IReadOnlyList<(double? Shown, string? Choice, string Text)> LeverSteps(NoiseLever lever)
        {
            var list = new List<(double?, string?, string)>();
            if (_catalog.Find(lever.EngineId) is null) return list;
            if (lever.Descriptor is { Kind: EngineSettingKind.Choice } d)
            {
                foreach (var o in OptionsFor(lever)) list.Add((null, o.Name, Lexicon.Get(o.LabelKey)));
                return list;
            }
            EngineSettingValue? at = Edge(lever, -1);
            int guard = 0;
            while (at is { } v && guard++ < 1000)
            {
                double shown = lever.Kind is NoiseLeverKind.RadioLevel or NoiseLeverKind.Strength or NoiseLeverKind.ListenRemoved
                    ? v.Number : ToShown(lever, v.Number);
                list.Add((shown, null, FormatShown(lever, shown)));
                at = Next(lever, v, +1);
            }
            return list;
        }

        /// <summary>A shown number as a field shows it, unit always included ("25 decibels", "unlimited").</summary>
        public string FormatShown(NoiseLever lever, double shown)
        {
            if (lever.Kind == NoiseLeverKind.ListenRemoved)
                return Lexicon.Get(shown == 1 ? "audio.noise_surface.state.on" : "audio.noise_surface.state.off");
            if (lever.EngineId == "deepfilternet3" && lever.Key == "attenuation_limit_db" && shown >= 100)
                return Lexicon.Get("audio.noise_surface.unlimited");
            string number = shown.ToString(lever.Descriptor is { Step: < 0.1 } x && !IsPercent(x) ? "0.###" : "0.#", CultureInfo.InvariantCulture);
            return UnitOf(lever) is string unit ? number + " " + Lexicon.Get(unit) : number;
        }

        /// <summary>Whether an engine's Advanced levers are revealed on Home (remembered per engine).</summary>
        public bool IsAdvanced(string id) => _config()?.NoiseAdvancedEngines.Contains(id) == true;

        /// <summary>Reveal or hide an engine's Advanced levers on Home, remember it, and say so.</summary>
        public void SetAdvanced(string id, bool on)
        {
            var config = _config();
            if (config != null)
            {
                config.NoiseAdvancedEngines.Remove(id);
                if (on) config.NoiseAdvancedEngines.Add(id);
                _persist();
            }
            Say(on ? L("audio.noise_surface.advanced_on", ("engine", NameOf(id))) : L("audio.noise_surface.advanced_off"), SpeechSubject.NoiseLevel);
        }

        /// <summary>
        /// The engine's Delay readout (#873): what the running engine delays
        /// the audio by, from its own latency; a helper engine's expected
        /// figure until it runs; empty for a radio engine.
        /// </summary>
        public string DelayText(string id)
        {
            var e = _catalog.Find(id);
            if (e == null || e.Side == DenoiseSide.Radio) return "";
            int? ms = null;
            if (SlotFor(e) is { } slot && slot.RunningEngineId == id && slot.RunningLatencySamples > 0)
                ms = (int)Math.Round(slot.RunningLatencySamples / 48.0);
            else
            {
                string? model = e.Settings.Any(d => d.Name == "model") ? SlotFor(e)?.GetSetting(id, "model").Choice : null;
                ms = DenoiseEngineCatalog.ExpectedHelperDelayMs(id, model);
            }
            return ms == null ? Lexicon.Get("audio.noise_surface.delay_unknown") : Lexicon.Get("audio.noise_surface.delay", ("ms", ms.Value));
        }

        /// <summary>
        /// The lexicon key of a lever's Ctrl+F1 line. Strength and Listen to
        /// what was removed share one line each; a radio level has one per
        /// engine (four of them are held intents); every setting has its own.
        /// </summary>
        public static string HelpKey(NoiseLever lever) => lever.Kind switch
        {
            NoiseLeverKind.Strength => "audio.noise_help.strength",
            NoiseLeverKind.ListenRemoved => "audio.noise_help.listen_removed",
            NoiseLeverKind.RadioLevel => "audio.noise_help.level." + lever.EngineId,
            _ => "audio.noise_help." + lever.EngineId + "." + lever.Key,
        };

        /// <summary>Feature Availability's reason for a computer engine, as a whole lexicon key (never built by concatenation, so the coverage scan can see it).</summary>
        private static string AvailabilityReasonKey(Unavailability reason) => reason switch
        {
            Unavailability.LibraryMissing => "audio.noise_availability.reason.librarymissing",
            Unavailability.ModelMissing => "audio.noise_availability.reason.modelmissing",
            Unavailability.HelperNotRunning => "audio.noise_availability.reason.helpernotrunning",
            Unavailability.HelperMissing => "audio.noise_availability.reason.helpermissing",
            Unavailability.HelperVersionMismatch => "audio.noise_availability.reason.helperversionmismatch",
            Unavailability.NoSupportedGpu => "audio.noise_availability.reason.nosupportedgpu",
            Unavailability.GpuTooOld => "audio.noise_availability.reason.gputooold",
            Unavailability.NotDownloaded => "audio.noise_availability.reason.notdownloaded",
            Unavailability.LicenceNotAccepted => "audio.noise_availability.reason.licencenotaccepted",
            Unavailability.NotOn32Bit => "audio.noise_availability.reason.noton32bit",
            _ => "audio.noise_availability.reason.initfailed",
        };

        /// <summary>The lever's spoken name.</summary>
        public string LeverName(NoiseLever lever) => Lexicon.Get(lever.LabelKey);

        /// <summary>
        /// Move a lever one step up (+1) or down (-1), say the new value, and
        /// journal it. At an end the lever stays and says so. A radio level
        /// whose engine is gated refuses with the switch's own ladder.
        /// </summary>
        public void Adjust(NoiseLever lever, int direction, string source) => Move(lever, source, current => Next(lever, current, direction), direction);

        /// <summary>Place a lever at its least (-1) or most (+1) value: Home and End.</summary>
        public void Place(NoiseLever lever, int edge, string source) => Move(lever, source, _ => Edge(lever, edge), edge);

        /// <summary>Set a lever to a shown number (a Home value field) or a choice name.</summary>
        public void Set(NoiseLever lever, double? shown, string? choice, string source) =>
            Move(lever, source, _ => choice != null ? new EngineSettingValue(0, choice)
                : lever.Kind is NoiseLeverKind.RadioLevel or NoiseLeverKind.Strength ? new EngineSettingValue(shown ?? 0)
                : new EngineSettingValue(FromShown(lever, shown ?? 0)), 0);

        private EngineSettingValue? Next(NoiseLever lever, EngineSettingValue current, int direction)
        {
            var e = _catalog.Find(lever.EngineId)!;
            switch (lever.Kind)
            {
                case NoiseLeverKind.RadioLevel:
                {
                    int v = (int)current.Number, next = e.Level!.Step(v, direction);
                    return next == v ? null : new EngineSettingValue(next);
                }
                case NoiseLeverKind.Strength:
                {
                    double next = Math.Clamp(current.Number + 5 * Math.Sign(direction), 0, 100);
                    return next == current.Number ? null : new EngineSettingValue(next);
                }
                case NoiseLeverKind.ListenRemoved:
                    return new EngineSettingValue(current.Number == 1 ? 0 : 1);
                default:
                {
                    var d = lever.Descriptor!;
                    if (d.Kind == EngineSettingKind.Choice)
                    {
                        var options = OptionsFor(lever);
                        int at = options.ToList().FindIndex(o => o.Name == current.Choice);
                        int next = at + Math.Sign(direction);
                        if (at < 0) next = direction > 0 ? 0 : options.Count - 1;
                        if (next < 0 || next >= options.Count) return null;
                        return new EngineSettingValue(0, options[next].Name);
                    }
                    var raw = d.StepFrom(current, lever.Inverted ? -direction : direction);
                    return raw;
                }
            }
        }

        private EngineSettingValue Edge(NoiseLever lever, int edge)
        {
            var e = _catalog.Find(lever.EngineId)!;
            switch (lever.Kind)
            {
                case NoiseLeverKind.RadioLevel: return new EngineSettingValue(edge < 0 ? e.Level!.Min : e.Level!.Max);
                case NoiseLeverKind.Strength: return new EngineSettingValue(edge < 0 ? 0 : 100);
                case NoiseLeverKind.ListenRemoved: return new EngineSettingValue(edge < 0 ? 0 : 1);
                default:
                    var d = lever.Descriptor!;
                    if (d.Kind == EngineSettingKind.Choice)
                    {
                        var options = OptionsFor(lever);
                        return options.Count == 0 ? d.Default : new EngineSettingValue(0, (edge < 0 ? options[0] : options[^1]).Name);
                    }
                    return d.Edge(lever.Inverted ? -edge : edge);
            }
        }

        /// <summary>The value a lever holds, in the form <see cref="Next"/> works on: a radio level or Strength as shown, a setting raw.</summary>
        private EngineSettingValue Raw(NoiseLever lever)
        {
            var e = _catalog.Find(lever.EngineId)!;
            return lever.Kind switch
            {
                NoiseLeverKind.RadioLevel => new EngineSettingValue(ReadRadioLevel(e) ?? e.Level!.Min),
                NoiseLeverKind.Strength => new EngineSettingValue(NumberOf(lever) ?? 100),
                NoiseLeverKind.ListenRemoved => new EngineSettingValue(NumberOf(lever) ?? 0),
                _ => SlotFor(e)!.GetSetting(e.Id, lever.Descriptor!.Name),
            };
        }

        private void Move(NoiseLever lever, string source, Func<EngineSettingValue, EngineSettingValue?> to, int direction)
        {
            var e = _catalog.Find(lever.EngineId);
            if (e == null) return;
            Current = e.Id;
            if (e.Side == DenoiseSide.Radio)
            {
                var r = _radio();
                if (r == null) { NoRadio(); return; }
                if (RefuseRadio(e, r)) return;
            }
            else if (SlotFor(e) == null) { NoRadio(); return; }

            var before = Raw(lever);
            var after = to(before);
            if (after == null)
            {
                string end = direction > 0 ? "audio.noise_surface.lever_most" : "audio.noise_surface.lever_least";
                Say(L(end, ("lever", LeverName(lever)), ("value", ValueText(lever))), SpeechSubject.NoiseLevel);
                return;
            }
            if (!Write(lever, e, after.Value, source))
            {
                Say(L("audio.noise_surface.lever_refused", ("lever", LeverName(lever))), SpeechSubject.NoiseLevel);
                return;
            }
            SpeakLever(lever, e);
        }

        private bool Write(NoiseLever lever, DenoiseEngineEntry e, EngineSettingValue value, string source)
        {
            switch (lever.Kind)
            {
                case NoiseLeverKind.RadioLevel:
                {
                    var r = _radio()!;
                    int v = Math.Clamp((int)Math.Round(value.Number), e.Level!.Min, e.Level.Max);
                    int? was = ReadRadioLevel(e);
                    r.WriteLevel(e, v);
                    _levelShadow[e.Id] = (v, _clock() + ShadowMs);
                    NoteRadioTouched(e.Id, level: true);
                    if (!e.Level.ReportedBack && r.Serial.Length > 0) _intents.Set(r.Serial, e.Id, v);
                    NoiseSettingsJournal.Record(NoiseJournalKind.Lever, source, e.Id, "level", was?.ToString() ?? "", v.ToString());
                    return true;
                }
                case NoiseLeverKind.Strength:
                {
                    var slot = SlotFor(e)!;
                    float was = slot.GetStrength(e.Id);
                    slot.SetStrength(e.Id, (float)(Math.Clamp(value.Number, 0, 100) / 100));
                    NoiseSettingsJournal.Record(NoiseJournalKind.Strength, source, e.Id, "strength", Pct(was), Pct(slot.GetStrength(e.Id)));
                    _persist();
                    return true;
                }
                case NoiseLeverKind.ListenRemoved:
                {
                    var slot = SlotFor(e)!;
                    slot.ListenToRemoved = value.Number == 1;
                    return true;
                }
                default:
                {
                    var slot = SlotFor(e)!;
                    var d = lever.Descriptor!;
                    var was = slot.GetSetting(e.Id, d.Name);
                    if (!slot.TrySetSetting(e.Id, d.Name, value)) return false;
                    NoiseSettingsJournal.Record(d.Name == "model" ? NoiseJournalKind.Model : NoiseJournalKind.Lever, source, e.Id, d.Name,
                        Describe(was), Describe(value));
                    AfterSettingChanged(e, d, value);
                    _persist();
                    return true;
                }
            }
        }

        private static string Pct(float strength) => Math.Round(strength * 100).ToString(CultureInfo.InvariantCulture);
        private static string Describe(EngineSettingValue v) => v.Choice ?? v.Number.ToString(CultureInfo.InvariantCulture);

        private void SpeakLever(NoiseLever lever, DenoiseEngineEntry e)
        {
            string name = Lexicon.Get(e.LabelKey), leverName = LeverName(lever), value = ValueText(lever);
            bool on = IsOn(e.Id);
            if (lever.Kind == NoiseLeverKind.ListenRemoved)
            {
                bool listening = NumberOf(lever) == 1;
                Say(listening ? L("audio.noise_surface.listen_on", ("engine", name)) : L("audio.noise_surface.listen_off"), SpeechSubject.NoiseLevel);
                return;
            }
            string key;
            if (lever.Kind == NoiseLeverKind.RadioLevel) key = on ? "audio.noise_surface.level" : "audio.noise_surface.level_off";
            else if (lever.Key == "model") key = on ? "audio.noise_surface.lever_model" : "audio.noise_surface.lever_choice_off";
            else if (lever.Descriptor is { Kind: EngineSettingKind.Choice }) key = on ? "audio.noise_surface.lever_choice" : "audio.noise_surface.lever_choice_off";
            else key = on ? "audio.noise_surface.lever" : "audio.noise_surface.lever_off";
            Say(L(key, ("engine", name), ("lever", leverName), ("lever_lower", leverName.ToLowerInvariant()), ("value", value)), SpeechSubject.NoiseLevel);
        }

        /// <summary>Say a lever's name and value, changing nothing (the layer's Tab, a Home field gaining focus).</summary>
        public void SpeakLeverName(NoiseLever lever) =>
            Say(L("audio.noise_surface.lever_named", ("lever", LeverName(lever)), ("value", ValueText(lever))), SpeechSubject.NoiseLevel);

        private int? ReadRadioLevel(DenoiseEngineEntry e)
        {
            var r = _radio();
            if (r == null || e.Level == null) return null;
            if (_levelShadow.TryGetValue(e.Id, out var shadow) && _clock() < shadow.Until) return shadow.Level;
            if (!e.Level.ReportedBack && r.Serial.Length > 0 && _intents.Get(r.Serial, e.Id) is int intent)
                return Math.Clamp(intent, e.Level.Min, e.Level.Max);
            return Math.Clamp(r.ReadLevel(e), e.Level.Min, e.Level.Max);
        }

        // ── The layer's lever cursor ──

        /// <summary>The lever the layer's arrows adjust, or null when no engine has been touched or the engine has no lever.</summary>
        public NoiseLever? CurrentLever
        {
            get
            {
                if (Current == null) return null;
                var levers = Levers(Current, includeAdvanced: true);
                if (levers.Count == 0) return null;
                return levers[Math.Clamp(CurrentLeverIndex, 0, levers.Count - 1)];
            }
        }

        /// <summary>Tab and Shift+Tab: move to the current engine's next or previous lever (Advanced included) and say its name and value. Wraps.</summary>
        public void NextLever(int direction)
        {
            if (Current == null) { Say(L("audio.noise_surface.no_engine_yet"), SpeechSubject.NoiseLevel); return; }
            var levers = Levers(Current, includeAdvanced: true);
            if (levers.Count == 0) { Say(L("audio.noise_surface.no_levers", ("engine", NameOf(Current))), SpeechSubject.NoiseLevel); return; }
            CurrentLeverIndex = ((CurrentLeverIndex + direction) % levers.Count + levers.Count) % levers.Count;
            SpeakLeverName(levers[CurrentLeverIndex]);
        }

        /// <summary>Up and Down on the current lever.</summary>
        public void AdjustCurrent(int direction, string source)
        {
            if (CurrentLever is { } lever) { Adjust(lever, direction, source); return; }
            Say(Current == null ? L("audio.noise_surface.no_engine_yet") : L("audio.noise_surface.no_levers", ("engine", NameOf(Current))), SpeechSubject.NoiseLevel);
        }

        /// <summary>Home and End on the current lever.</summary>
        public void PlaceCurrent(int edge, string source)
        {
            if (CurrentLever is { } lever) { Place(lever, edge, source); return; }
            Say(Current == null ? L("audio.noise_surface.no_engine_yet") : L("audio.noise_surface.no_levers", ("engine", NameOf(Current))), SpeechSubject.NoiseLevel);
        }

        /// <summary>Clear every slot's Listen to what was removed (the layer or expander was left; a connect). Never saved on.</summary>
        public void ClearListening()
        {
            if (_pipeline() is { } p) { p.SpectralSlot.ListenToRemoved = false; p.NeuralSlot.ListenToRemoved = false; }
        }

        // ────────────────────────────────────────────────────────────────
        //  Return to defaults (ruled, departure D4)
        // ────────────────────────────────────────────────────────────────

        /// <summary>Whether an engine offers Return to defaults at all: every engine but the switch-only radio ones, which have no level and so no default.</summary>
        public bool OffersDefaults(string id) => _catalog.Find(id) is { } e && !e.IsSwitchOnly;

        /// <summary>
        /// A computer engine: every lever and Strength back to the descriptor
        /// defaults (NR2's tracking to its mode default), the engine left on
        /// or off as it was, one journal entry, one sentence. A radio engine
        /// with a level: no factory level is known anywhere (verified, open
        /// fact 16), so it says why and writes nothing. A switch-only engine:
        /// there is nothing to return.
        /// </summary>
        public void ReturnToDefaults(string id, string source)
        {
            var e = _catalog.Find(id);
            if (e == null) return;
            Current = id;
            string name = Lexicon.Get(e.LabelKey);
            if (e.Side == DenoiseSide.Radio)
            {
                Say(L(e.IsSwitchOnly ? "audio.noise_surface.defaults_switch" : "audio.noise_surface.defaults_radio", ("engine", name)), SpeechSubject.NoiseDefaults);
                return;
            }
            var slot = SlotFor(e);
            if (slot == null) { NoRadio(); return; }
            var changes = new List<NoiseJournalChange>();
            var said = new List<string>();
            float strength = DefaultStrength(id);
            if (Math.Abs(slot.GetStrength(id) - strength) > 0.0001)
                changes.Add(new NoiseJournalChange(id, "strength", Pct(slot.GetStrength(id)), Pct(strength)));
            slot.SetStrength(id, strength);
            foreach (var d in e.Settings)
            {
                var target = DefaultFor(e, d);
                if (d.Name == "model" && CurrentGroup == NoiseModeGroup.Cw) target = new EngineSettingValue(0, NoiseModeRules.ChooseCwModel(_cwCost).Model);
                var was = slot.GetSetting(id, d.Name);
                if (!Same(was, target))
                {
                    slot.TrySetSetting(id, d.Name, target);
                    changes.Add(new NoiseJournalChange(id, d.Name, Describe(was), Describe(target)));
                }
            }
            NoiseSettingsJournal.Record(new NoiseJournalEntry(NoiseJournalKind.ReturnToDefaults, source, false, changes, name));
            if (e.Id == "nr2") RememberNr2Tracking();
            _persist();
            foreach (var lever in Levers(id, includeAdvanced: false))
                said.Add(LeverName(lever).ToLowerInvariant() + " " + ValueText(lever));
            Say(L("audio.noise_surface.defaults", ("engine", name), ("values", string.Join(", ", said))), SpeechSubject.NoiseDefaults);
        }

        private EngineSettingValue DefaultFor(DenoiseEngineEntry e, EngineSettingDescriptor d) =>
            e.Id == "nr2" && d.Name == "noise_estimator" && CurrentGroup == NoiseModeGroup.Cw
                ? new EngineSettingValue(0, NoiseModeRules.Nr2CwTrackingDefault) : d.Default;

        private static bool Same(EngineSettingValue a, EngineSettingValue b) =>
            string.Equals(a.Choice, b.Choice, StringComparison.Ordinal) && (a.Choice != null || Math.Abs(a.Number - b.Number) < 1e-9);

        /// <summary>A slot's Strength default per engine: RNNoise starts at 80 percent, every other at 100 (design part one).</summary>
        public static float DefaultStrength(string id) => id == "rnnoise" ? 0.8f : 1f;

        // ────────────────────────────────────────────────────────────────
        //  Say what is active
        // ────────────────────────────────────────────────────────────────

        /// <summary>The summary every road speaks: the Space key, the menu item, Home's Active readout.</summary>
        public string ActiveSummary()
        {
            var radioItems = new List<string>();
            if (_radio() != null)
                foreach (var e in Engines().Where(e => e.Side == DenoiseSide.Radio && IsOn(e.Id)))
                {
                    string name = Lexicon.Get(e.LabelKey);
                    radioItems.Add(e.Level != null && ReadRadioLevel(e) is int level
                        ? L("audio.noise_surface.active.radio_item", ("engine", name), ("level", level)) : name);
                }
            var pcItems = new List<string>();
            foreach (var family in new[] { DenoiseFamily.Spectral, DenoiseFamily.Neural })
            {
                var slot = SlotOf(family);
                if (slot == null || !slot.Enabled) continue;
                var e = _catalog.Find(slot.ChosenEngineId);
                if (e == null) continue;
                string name = Lexicon.Get(e.LabelKey);
                var normal = Levers(e.Id, includeAdvanced: false).Where(l => l.Kind == NoiseLeverKind.Setting).ToList();
                var details = normal.Select(l => l.Descriptor!.Kind == EngineSettingKind.Choice ? ValueText(l) : LeverName(l).ToLowerInvariant() + " " + ValueText(l)).ToList();
                string item = details.Count == 0 || !Chatty ? name : name + ", " + string.Join(", ", details);
                if (slot.RunningEngineId != e.Id && SafeAvailability(e) != Unavailability.None)
                    item = L("audio.noise_surface.active.waiting", ("engine", name));
                pcItems.Add(item);
            }
            if (radioItems.Count == 0 && pcItems.Count == 0) return L("audio.noise_surface.active.none");
            var parts = new List<string>();
            if (radioItems.Count > 0) parts.Add(L("audio.noise_surface.active.radio", ("list", string.Join("; ", radioItems))));
            if (pcItems.Count > 0) parts.Add(L("audio.noise_surface.active.computer", ("list", string.Join("; ", pcItems))));
            return string.Join(" ", parts);
        }

        public void SpeakActive() => Say(ActiveSummary(), SpeechSubject.NoiseActiveSummary);

        // ────────────────────────────────────────────────────────────────
        //  Feature Availability (E12)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The "Computer noise reduction" group the Feature Availability
        /// window appends after the radio's rows: a heading line, then one line
        /// per computer engine from the catalog's probe, in the window's
        /// "Feature: status - reason" form, with the action where there is one.
        /// The probes start nothing.
        /// </summary>
        public IReadOnlyList<string> ComputerAvailabilityLines()
        {
            var lines = new List<string> { Lexicon.Get("audio.noise_availability.heading") };
            foreach (var e in Engines(DenoiseGroup.NoiseReduction).Where(e => e.Side == DenoiseSide.Pc))
            {
                string name = Lexicon.Get(e.LabelKey);
                if (e.Factory == null)
                {
                    lines.Add(Lexicon.Get("audio.noise_availability.line", ("engine", name), ("status", Lexicon.Get("audio.noise_availability.unavailable")),
                        ("reason", Lexicon.Get("audio.noise_availability.reason.notinversion"))));
                    continue;
                }
                var reason = SafeAvailability(e);
                if (reason == Unavailability.None)
                {
                    lines.Add(Lexicon.Get("audio.noise_availability.available_line", ("engine", name), ("status", Lexicon.Get("audio.noise_availability.available"))));
                    continue;
                }
                lines.Add(Lexicon.Get("audio.noise_availability.line", ("engine", name), ("status", Lexicon.Get("audio.noise_availability.unavailable")),
                    ("reason", Lexicon.Get(AvailabilityReasonKey(reason)))));
            }
            return lines;
        }
    }
}
