using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    /// <summary>One de-esser lever: its key, label, range, step and unit (Sprint 49 Track I).</summary>
    public sealed record DeEsserLever(string Key, string LabelKey, double Min, double Max, double Step, string UnitKey)
    {
        public const string Frequency = "frequency";
        public const string Amount = "amount";
        public const string Sensitivity = "sensitivity";
    }

    /// <summary>
    /// The sound stages Sprint 49 Track I added, on the noise controller's one
    /// rule book (the Transmit menu, Home and the JJ key's noise layer all call
    /// these, so they agree by construction): the expander's side-chain
    /// filter, "Learn the room", the de-esser, and the parametric equaliser on
    /// either side. Every sentence is a DRAFT PROSE key for Codex.
    /// </summary>
    public sealed partial class NoiseController
    {
        // ────────────────────────────────────────────────────────────────
        //  The expander's side-chain filter (a switch, S2)
        // ────────────────────────────────────────────────────────────────

        /// <summary>The side-chain filter on or off, speaking its timing (held for the unkey while keyed).</summary>
        public void SetSideChainOn(bool on, string source)
        {
            using var owned = OwnTx(source);
            using var oneUtterance = OneUtterance();
            var g = TransmitGate;
            bool was = g.SideChainFilter;
            var timing = LayerNoOp(source, was == on) ? TxChangeResult.Now : g.SetSideChainFilter(on);
            if (was != on) NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), "gate", "side_chain", Words(was), Words(on));
            Say(L(on ? "audio.noise_surface.tx.side_chain_on" : "audio.noise_surface.tx.side_chain_off"), SpeechSubject.TransmitNoiseGate);
            SayAtUnkey(timing);
        }

        public void ToggleSideChain(string source) => SetSideChainOn(!TransmitGate.SideChainFilter, source);

        /// <summary>
        /// The expander's mode, JJ Flexible's or WDSP's (phase 2, ruled S6): a
        /// switch, so it speaks its timing (held for the unkey while keyed). Choosing
        /// WDSP's when it cannot run here is kept as the choice, and the sentence
        /// says why and what to do (listed, never hidden).
        /// </summary>
        public void SetExpanderModeWdsp(bool wdsp, string source)
        {
            if (GateLever(TxGateLever.Mode) is { } lever) SetGate(lever, wdsp ? 1 : 0, source);
        }

        public void ToggleExpanderMode(string source) => SetExpanderModeWdsp(!ExpanderModes.IsWdsp(TransmitGate.ExpanderMode), source);

        // ────────────────────────────────────────────────────────────────
        //  Learn the room (#1252; Noel's ruling 2026-11-23 11:12)
        // ────────────────────────────────────────────────────────────────

        private bool _roomLearnedHooked;

        /// <summary>
        /// "Learn the room": start a fresh measurement of the room at the
        /// expander's input, say that it has started (or why not), and say what
        /// it found when it finishes. Refuses in CW and digital and when no
        /// computer audio reaches the chain; asked while keyed it waits for the
        /// unkey (TxAudioConditioning.LearnRoom says why).
        /// </summary>
        public void LearnTheRoom(string source)
        {
            using var oneUtterance = OneUtterance();
            if (!_roomLearnedHooked)
            {
                _roomLearnedHooked = true;
                TxAudioConditioning.RoomLearned += r => Say(L(r.AutomaticThreshold ? "audio.noise_surface.tx.learn_done_auto" : "audio.noise_surface.tx.learn_done_hand",
                    ("floor", DbText(r.FloorDb)), ("threshold", DbText(r.ThresholdDb))), SpeechSubject.TransmitRoomLearn);
            }
            var outcome = TxAudioConditioning.LearnRoom();
            NoiseSettingsJournal.Record(NoiseJournalKind.Lever, TxSource(source), "gate", "learn_room", "", outcome.ToString());
            string key = outcome switch
            {
                TxAudioConditioning.LearnRoomOutcome.Started => "audio.noise_surface.tx.learn_started",
                TxAudioConditioning.LearnRoomOutcome.WaitsForUnkey => "audio.noise_surface.tx.learn_waits",
                TxAudioConditioning.LearnRoomOutcome.NotInThisMode => "audio.noise_surface.tx.learn_not_in_mode",
                TxAudioConditioning.LearnRoomOutcome.NoComputerAudio => "audio.noise_surface.tx.learn_no_audio",
                _ => "audio.noise_surface.tx.learn_no_radio",
            };
            Say(L(key), SpeechSubject.TransmitRoomLearn);
        }

        private static string DbText(float db) => db.ToString("0", CultureInfo.InvariantCulture);

        // ────────────────────────────────────────────────────────────────
        //  The de-esser (transmit only)
        // ────────────────────────────────────────────────────────────────

        /// <summary>The de-esser's levers: frequency, amount and sensitivity. Its band is applied clamped below the transmit filter.</summary>
        public static readonly DeEsserLever[] DeEsserLevers =
        {
            new(DeEsserLever.Frequency, "audio.noise_surface.tx.deesser.frequency", 1000, 12000, 250, "audio.unit.hertz"),
            new(DeEsserLever.Amount, "audio.noise_surface.tx.deesser.amount", 1, 24, 1, "audio.unit.decibels"),
            new(DeEsserLever.Sensitivity, "audio.noise_surface.tx.deesser.sensitivity", -60, -10, 1, "audio.unit.decibels"),
        };

        public bool DeEsserOn => TxAudioConditioning.DeEsserSetup.Enabled;

        /// <summary>The de-esser on or off, speaking its timing.</summary>
        public void SetDeEsserOn(bool on, string source)
        {
            using var owned = OwnTx(source);
            using var oneUtterance = OneUtterance();
            bool was = DeEsserOn;
            var timing = LayerNoOp(source, was == on) ? TxChangeResult.Now : TxAudioConditioning.SetDeEsserEnabled(on);
            if (was != on) NoiseSettingsJournal.Record(NoiseJournalKind.Switch, TxSource(source), "deesser", "", Words(was), Words(on));
            Say(L(on ? "audio.noise_surface.tx.deesser_on" : "audio.noise_surface.tx.deesser_off"), SpeechSubject.TransmitDeEsser);
            if (on && TxAudioConditioning.DeEsserBand is { Clamped: true } band)
                Say(L("audio.noise_surface.tx.deesser_clamped", ("frequency", band.AppliedHz.ToString("0", CultureInfo.InvariantCulture))), SpeechSubject.TransmitDeEsser);
            SayAtUnkey(timing);
        }

        public void ToggleDeEsser(string source) => SetDeEsserOn(!DeEsserOn, source);

        /// <summary>The noise layer's de-esser key in the receive direction: the de-esser is transmit only.</summary>
        public void SayDeEsserTransmitOnly()
        {
            using var oneUtterance = OneUtterance();
            Say(L("audio.noise_surface.tx.deesser_transmit_only"), SpeechSubject.TransmitDeEsser);
        }

        /// <summary>Say a de-esser lever and its value (the layer's Tab).</summary>
        public void SayDeEsserLever(DeEsserLever lever)
        {
            using var oneUtterance = OneUtterance();
            Say(L("audio.noise_surface.tx.gate_lever", ("lever", DeEsserLeverName(lever)), ("value", DeEsserValueText(lever, DeEsserNumber(lever)))), SpeechSubject.TransmitDeEsser);
        }

        /// <summary>Say an equaliser sentence from the layer, on the side's subject.</summary>
        public void SayInLayer(string sentence, DenoiseDirection side)
        {
            using var oneUtterance = OneUtterance();
            Say(sentence, EqSubject(side));
        }

        public string DeEsserLeverName(DeEsserLever lever) => Lexicon.Get(lever.LabelKey);

        public double DeEsserNumber(DeEsserLever lever)
        {
            var d = TxAudioConditioning.DeEsserSetup;
            return lever.Key switch
            {
                DeEsserLever.Frequency => Math.Round(d.FrequencyHz),
                DeEsserLever.Amount => Math.Round(d.AmountDb),
                _ => Math.Round(d.SensitivityDb),
            };
        }

        public string DeEsserValueText(DeEsserLever lever, double value) =>
            WithUnit(Math.Round(value).ToString("0", CultureInfo.InvariantCulture), lever.UnitKey);

        public IReadOnlyList<(double Value, string Text)> DeEsserSteps(DeEsserLever lever)
        {
            var list = new List<(double, string)>();
            for (double v = lever.Min; v <= lever.Max + 1e-9; v += lever.Step) list.Add((v, DeEsserValueText(lever, v)));
            return list;
        }

        public void AdjustDeEsser(DeEsserLever lever, int direction, string source)
        {
            double now = DeEsserNumber(lever);
            double next = Math.Clamp(now + lever.Step * Math.Sign(direction), lever.Min, lever.Max);
            if (next == now)
            {
                using var oneUtterance = OneUtterance();
                Say(L(direction > 0 ? "audio.noise_surface.lever_most" : "audio.noise_surface.lever_least",
                    ("lever", DeEsserLeverName(lever)), ("value", DeEsserValueText(lever, now))), SpeechSubject.TransmitDeEsser);
                return;
            }
            SetDeEsser(lever, next, source);
        }

        /// <summary>Set a de-esser lever (live, S15) and say it; the frequency also says when the band is clamped below the transmit filter.</summary>
        public void SetDeEsser(DeEsserLever lever, double value, string source)
        {
            using var owned = OwnTx(source);
            using var oneUtterance = OneUtterance();
            double was = DeEsserNumber(lever);
            double asked = Math.Clamp(value, lever.Min, lever.Max);
            var timing = TxAudioConditioning.SetDeEsserLevel(lever.Key, (float)asked);
            if (timing.Timing == TxChangeTiming.Refused)
            {
                Say(L("audio.noise_surface.lever_refused", ("lever", DeEsserLeverName(lever))), SpeechSubject.TransmitDeEsser);
                return;
            }
            NoiseSettingsJournal.Record(NoiseJournalKind.Lever, TxSource(source), "deesser", lever.Key,
                was.ToString(CultureInfo.InvariantCulture), asked.ToString(CultureInfo.InvariantCulture));
            Say(L("audio.noise_surface.tx.gate_lever", ("lever", DeEsserLeverName(lever)), ("value", DeEsserValueText(lever, asked))), SpeechSubject.TransmitDeEsser);
            if (lever.Key == DeEsserLever.Frequency && TxAudioConditioning.DeEsserBand is { Clamped: true } band)
                Say(L("audio.noise_surface.tx.deesser_clamped", ("frequency", band.AppliedHz.ToString("0", CultureInfo.InvariantCulture))), SpeechSubject.TransmitDeEsser);
            SayAtUnkey(timing);
        }

        // ────────────────────────────────────────────────────────────────
        //  The parametric equaliser, either side (ruled S2: both, one editor)
        // ────────────────────────────────────────────────────────────────

        /// <summary>The equaliser's setting on a side: a copy.</summary>
        public ParametricEqSetup EqualizerSetup(DenoiseDirection side) =>
            side == DenoiseDirection.Transmit ? TxAudioConditioning.EqSetup
                : _pipeline()?.EqualizerSetup ?? _config()?.ReceiveEq?.Clone() ?? new ParametricEqSetup();

        public bool EqualizerOn(DenoiseDirection side) => EqualizerSetup(side).Enabled;

        private string SideWords(DenoiseDirection side) =>
            Lexicon.Get(side == DenoiseDirection.Transmit ? "audio.eq.side.transmit" : "audio.eq.side.receive");

        private static string EqSubject(DenoiseDirection side) =>
            side == DenoiseDirection.Transmit ? SpeechSubject.TransmitEqualizer : SpeechSubject.ReceiveEqualizer;

        /// <summary>
        /// Write a whole equaliser setting on a side. Transmit: on or off and a
        /// preset are switches held for the unkey (S2), shape edits are live
        /// (S15). Receive: in place at the next buffer, faded, and saved app-wide.
        /// Null when there is nowhere to put it (receive with no radio).
        /// </summary>
        internal TxChangeResult? WriteEqualizer(DenoiseDirection side, ParametricEqSetup setup, bool isSwitch)
        {
            if (side == DenoiseDirection.Transmit)
            {
                var now = TxAudioConditioning.EqSetup;
                if (isSwitch && now.Enabled != setup.Enabled && SameShape(now, setup)) return TxAudioConditioning.SetEqEnabled(setup.Enabled);
                if (isSwitch && setup.PresetId.Length > 0 && ParametricEqPresets.Find(setup.PresetId) != null && now.PresetId != setup.PresetId)
                    return TxAudioConditioning.LoadEqPreset(setup.PresetId);
                return TxAudioConditioning.SetEqShape(setup.Bands, setup.OutputGainDb, setup.PresetId);
            }
            if (_pipeline() is not { } p) return null;
            // Sprint 49 Track I, repair 1 (B4, #873): an on-or-off or a preset is a
            // receive SWITCH and takes the operator's switch choices (cross-fade or
            // cut, and the Mute gap); a band edit is continuous and always fades.
            p.SetEqualizer(setup, isSwitch);
            _persist();
            return TxChangeResult.Now;
        }

        /// <summary>
        /// Put a whole equaliser setting back, its on or off included (Sprint 49
        /// Track I, repair 1, B2: the editor's Cancel). Transmit goes through ONE
        /// switch held for the unkey behind anything already held, so an on or
        /// off the editor queued while keyed is undone too, not left to land.
        /// </summary>
        internal TxChangeResult? RestoreEqualizer(DenoiseDirection side, ParametricEqSetup setup)
        {
            if (side == DenoiseDirection.Transmit) return TxAudioConditioning.SetEqWhole(setup);
            return WriteEqualizer(side, setup, isSwitch: true);
        }

        /// <summary>The layer's ownership for a write on a side: transmit's or receive's (repair 1, B1).</summary>
        private IDisposable? Own(DenoiseDirection side, string source) =>
            side == DenoiseDirection.Transmit ? OwnTx(source) : OwnRx(source);

        /// <summary>
        /// Say an equaliser switch. Transmit: on its own subject, as before.
        /// Receive: through the engine-switch road (repair 1, B4, #873), so the
        /// operator's switch beep and switch speech choices hold for it too.
        /// </summary>
        private void SayEqualizerSwitch(DenoiseDirection side, string sentence)
        {
            if (side == DenoiseDirection.Transmit) Say(sentence, EqSubject(side));
            else SayEngineSwitch(sentence);
        }

        private static bool SameShape(ParametricEqSetup a, ParametricEqSetup b)
        {
            var x = a.Clone(); var y = b.Clone();
            x.Enabled = y.Enabled = false;
            return new TxNoiseSetup { Eq = x }.SameContentAs(new TxNoiseSetup { Eq = y });
        }

        /// <summary>The equaliser on or off on a side, and say it.</summary>
        public void SetEqualizerOn(DenoiseDirection side, bool on, string source)
        {
            using var owned = Own(side, source);
            using var oneUtterance = OneUtterance();
            var setup = EqualizerSetup(side);
            bool was = setup.Enabled;
            setup.Enabled = on;
            var timing = LayerNoOp(source, was == on) ? TxChangeResult.Now : WriteEqualizer(side, setup, isSwitch: true);
            if (timing == null) { NoRadio(); return; }
            if (was != on) NoiseSettingsJournal.Record(NoiseJournalKind.Switch, side == DenoiseDirection.Transmit ? TxSource(source) : source, "equalizer", "", Words(was), Words(on));
            SayEqualizerSwitch(side, L(on ? "audio.eq.on" : "audio.eq.off", ("side", SideWords(side))));
            if (side == DenoiseDirection.Transmit) SayAtUnkey(timing);
        }

        public void ToggleEqualizer(DenoiseDirection side, string source) => SetEqualizerOn(side, !EqualizerOn(side), source);

        /// <summary>Load a preset's bands into a side's equaliser (its on or off is left as it was), and say what it now holds.</summary>
        public void LoadEqualizerPreset(DenoiseDirection side, string presetId, string source)
        {
            using var owned = Own(side, source);
            using var oneUtterance = OneUtterance();
            var preset = ParametricEqPresets.Find(presetId);
            if (preset == null) return;
            var setup = EqualizerSetup(side);
            setup.Bands = preset.Bands.Select(b => b.Clone()).ToList();
            setup.OutputGainDb = preset.OutputGainDb;
            setup.PresetId = preset.Id;
            var timing = WriteEqualizer(side, setup, isSwitch: true);
            if (timing == null) { NoRadio(); return; }
            NoiseSettingsJournal.Record(NoiseJournalKind.PresetApplied, side == DenoiseDirection.Transmit ? TxSource(source) : source, "equalizer", "", "", preset.Id);
            SayEqualizerSwitch(side, L("audio.eq.preset_loaded", ("preset", Lexicon.Get(preset.NameKey)), ("summary", EqualizerSummary(side, setup))));
            if (side == DenoiseDirection.Transmit) SayAtUnkey(timing);
        }

        /// <summary>A side's equaliser in one line: on or off, and its bands.</summary>
        public string EqualizerSummary(DenoiseDirection side) => EqualizerSummary(side, EqualizerSetup(side));

        private string EqualizerSummary(DenoiseDirection side, ParametricEqSetup setup)
        {
            if (setup.Bands.Count == 0) return L(setup.Enabled ? "audio.eq.summary_flat_on" : "audio.eq.summary_flat_off", ("side", SideWords(side)));
            return L(setup.Enabled ? "audio.eq.summary_on" : "audio.eq.summary_off", ("side", SideWords(side)),
                ("bands", string.Join("; ", setup.Bands.Select((b, i) => ParametricEqEditorModel.BandRow(b, i)))));
        }

        /// <summary>Say a side's equaliser.</summary>
        public void SayEqualizer(DenoiseDirection side)
        {
            using var oneUtterance = OneUtterance();
            Say(EqualizerSummary(side), EqSubject(side));
        }

        /// <summary>
        /// The equaliser's editing model for a side, shared by the editor window
        /// and the noise layer's equaliser keys (it works with no window, #488,
        /// #489). Each edit writes the whole setting through
        /// <see cref="WriteEqualizer"/>.
        /// </summary>
        public ParametricEqEditorModel EqualizerEditor(DenoiseDirection side, string source) =>
            new(side, () => EqualizerSetup(side), setup =>
            {
                using var owned = Own(side, source);
                return WriteEqualizer(side, setup, isSwitch: false);
            }, setup =>
            {
                using var owned = Own(side, source);
                return RestoreEqualizer(side, setup);
            });
    }
}
