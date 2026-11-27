using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace JJFlexWpf
{
    /// <summary>
    /// The noise layer's keys (Sprint 49 Track E, E7): which key does what,
    /// as data, and the one handler that sends each to <see cref="NoiseController"/>.
    /// The layer engine asks this through <c>HostKeys</c> and
    /// <c>ShiftAltKeys</c>; nothing here re-implements a rule the controller
    /// owns, so a key and its menu row say the same sentence by construction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four tiers (ruled 2026-11-07, questions 1 and 7): a plain letter is
    /// a radio engine, Ctrl and a letter a computer engine, Shift and a letter
    /// jumps to a slice (the host's, not this class's), and Shift with Alt is
    /// the rest. NO plain Alt chord is claimed: Noel ruled 2026-11-07 21:07 to
    /// 21:08 that the four Alt keys the design proposed move to Shift+Alt, so
    /// Alt+A (AM), Alt+F (FM), Alt+R and Alt+brackets keep passing through
    /// exactly as they do in every other layer, Alt+F4 included.
    /// </para>
    /// <para>
    /// <b>T switches the layer's direction</b> (ruled 2026-11-07, question 6:
    /// "t is fine, if, when we use it (in operation) it sucks we can add a
    /// layer"; built in Sprint 49 Track H2). After T the same Ctrl letters act
    /// on the transmit chain, and so do the arrows, Tab, Space, the brackets,
    /// 0, the favourite and defaults keys and the preset brackets, until T
    /// again or the layer closes. In the transmit direction a radio letter and
    /// Captured noise's Ctrl+S do nothing and say so (part seven).
    /// </para>
    /// <para>
    /// <b>The stage keys ruled 2026-11-26</b> (Sprint 49 noise keys): Ctrl+E
    /// is the radio's own graphic equaliser and Ctrl+Shift+E our parametric
    /// one, each in the layer's direction (#490); Ctrl+K our compressor,
    /// Ctrl+L our limiter and Ctrl+P the speech processor handoff setting,
    /// transmit only (#1278); Shift+Alt+L Learn the room, transmit only
    /// (#1252). A transmit-only key facing receive changes nothing and says
    /// so, as the de-esser's Ctrl+Y always has. Every one of them goes
    /// through the controller road the Transmit menu and Home take, and the
    /// layer's Escape puts back what each changed.
    /// </para>
    /// <para>
    /// Ctrl+B and Ctrl+W stay unbound in both directions: the computer
    /// blankers they were proposed for were dropped (#1027), so they are
    /// refused like any unknown key.
    /// </para>
    /// </remarks>
    public sealed class NoiseLayerKeys
    {
        /// <summary>Legacy Auto-Notch (Noel, 2026-11-07 21:07). One constant, so moving it is one line.</summary>
        public const Keys LegacyAutoNotchKey = Keys.A | Keys.Shift | Keys.Alt;

        /// <summary>Add or remove the engine last touched from favourites (Noel, 21:07).</summary>
        public const Keys FavouriteKey = Keys.F | Keys.Shift | Keys.Alt;

        /// <summary>Return the engine last touched to its defaults (Noel, 21:08).</summary>
        public const Keys DefaultsKey = Keys.R | Keys.Shift | Keys.Alt;

        /// <summary>Step to the next preset (Noel, 21:08).</summary>
        public const Keys NextPresetKey = Keys.OemCloseBrackets | Keys.Shift | Keys.Alt;

        /// <summary>Step to the previous preset (Noel, 21:08).</summary>
        public const Keys PreviousPresetKey = Keys.OemOpenBrackets | Keys.Shift | Keys.Alt;

        /// <summary>Switch the layer between receive and transmit (ruled 2026-11-07, question 6). One constant, so moving it is one line.</summary>
        public const Keys DirectionKey = Keys.T;

        // ── Sprint 49 Track I: the parametric equaliser and the de-esser on the
        //    layer (#488, #489: the equaliser works with no window open). PROPOSED
        //    keys, for Noel to rule; one constant each, so moving one is one line.
        //    Ctrl and a letter switches a computer stage, as Ctrl and a letter
        //    switches a computer engine here. ──

        /// <summary>
        /// OUR parametric equaliser on or off, in the layer's direction. RULED
        /// by Noel 2026-11-26 13:07 (#490): Ctrl+Shift+E, "the more advanced
        /// one", because the radio's own equaliser, the one most operators reach
        /// for, takes the plain Ctrl+E (<see cref="RadioEqualizerKey"/>). Track I
        /// had proposed Ctrl+E for this one.
        /// </summary>
        public const Keys EqualizerKey = Keys.E | Keys.Control | Keys.Shift;

        /// <summary>
        /// The RADIO's own nine-band graphic equaliser on or off, in the layer's
        /// direction, which also puts the arrows, Tab and Space on its bands
        /// (fixed: no add, no delete). Ruled with <see cref="EqualizerKey"/>.
        /// </summary>
        public const Keys RadioEqualizerKey = Keys.E | Keys.Control;

        /// <summary>The de-esser on or off, transmit (proposed: Y, the free letter nearest "sibilance"; S and D are taken).</summary>
        public const Keys DeEsserKey = Keys.Y | Keys.Control;

        // ── Sprint 49 noise keys: RULED by Noel 2026-11-26 (#1278, question 13:
        //    "yes to all three keys"), transmit direction. In the receive
        //    direction each changes nothing and says so, as the de-esser does. ──

        /// <summary>Our compressor on or off (transmit), which also puts the arrows, Tab and Space on its settings.</summary>
        public const Keys CompressorKey = Keys.K | Keys.Control;

        /// <summary>Our limiter on or off (transmit), which also puts the arrows, Tab and Space on its settings.</summary>
        public const Keys LimiterKey = Keys.L | Keys.Control;

        /// <summary>The setting "turn the radio's speech processor off while the compressor is on" (transmit).</summary>
        public const Keys ProcessorHandoffKey = Keys.P | Keys.Control;

        /// <summary>Learn the room (#1252; RULED by Noel 2026-11-26, question 12: a sixth Shift+Alt command). Transmit; in receive it says so.</summary>
        public const Keys LearnRoomKey = Keys.L | Keys.Shift | Keys.Alt;

        /// <summary>The equaliser's previous and next band, which also puts the arrows, Tab and Space on the equaliser: the radio's, while it is the one under the arrows, otherwise ours.</summary>
        public const Keys PreviousBandKey = Keys.Left, NextBandKey = Keys.Right;

        /// <summary>Add a band after the one you are on.</summary>
        public const Keys AddBandKey = Keys.Insert;

        /// <summary>Remove the band you are on.</summary>
        public const Keys RemoveBandKey = Keys.Delete;

        /// <summary>What the arrows, Tab and Space work: the engine last touched (as before), our equaliser, the radio's equaliser, the de-esser, the compressor or the limiter.</summary>
        private enum StageCursor { Engine, Equalizer, RadioEqualizer, DeEsser, Compressor, Limiter }

        private StageCursor _cursor = StageCursor.Engine;
        private int _deEsserLever;
        private int _radioBand;
        private string? _compressorLever, _limiterLever;
        private ParametricEqEditorModel? _eqReceive, _eqTransmit;

        private ParametricEqEditorModel Eq(bool transmit) =>
            transmit ? (_eqTransmit ??= _noise.EqualizerEditor(Radios.DenoiseDirection.Transmit, Source))
                     : (_eqReceive ??= _noise.EqualizerEditor(Radios.DenoiseDirection.Receive, Source));

        /// <summary>Every engine key, chord to catalog id. The H list, the explorer and the near miss read <c>KeyInventory.NoiseLayerCommands</c>, which names the same chords; <c>NoiseLayerKeysTests</c> holds the two together.</summary>
        public static readonly IReadOnlyDictionary<Keys, string> EngineKeys = new Dictionary<Keys, string>
        {
            [Keys.L] = "radio_nrl",
            [Keys.N] = "radio_nr",
            [Keys.R] = "radio_rnn",
            [Keys.S] = "radio_nrs",
            [Keys.F] = "radio_nrf",
            [Keys.B] = "radio_nb",
            [Keys.W] = "radio_wnb",
            [Keys.A] = "radio_anft",
            [LegacyAutoNotchKey] = "radio_anfl",
            [Keys.O] = "radio_anf",
            [Keys.P] = "radio_apf",
            [Keys.R | Keys.Control] = "rnnoise",
            [Keys.D | Keys.Control] = "deepfilternet3",
            [Keys.N | Keys.Control] = "nnr",
            [Keys.D2 | Keys.Control] = "nr2",
            [Keys.D4 | Keys.Control] = "nr4",
            [Keys.S | Keys.Control] = "spectral_capture",
            [Keys.G | Keys.Control] = "nvidia_afx",
        };

        /// <summary>The Shift+Alt chords this layer takes. Every other Alt chord passes through.</summary>
        public static readonly IReadOnlyList<Keys> ShiftAltChords = new[]
        {
            LegacyAutoNotchKey, FavouriteKey, DefaultsKey, NextPresetKey, PreviousPresetKey, LearnRoomKey,
        };

        private const string Source = NoiseController.LayerSource; // what makes a change the layer's own, for its Escape
        private readonly NoiseController _noise;
        private readonly Action<bool>? _tone;

        /// <param name="tone">The layer's on or off tone after a switch; null in tests.</param>
        public NoiseLayerKeys(NoiseController noise, Action<bool>? tone = null)
        {
            _noise = noise;
            _tone = tone;
        }

        /// <summary>Is this a Shift+Alt chord the layer takes? Asked by the engine before its Alt pass-through.</summary>
        public bool HandleShiftAlt(Keys k)
        {
            if (Array.IndexOf((Keys[])ShiftAltChords, k) < 0) return false;
            return Handle(k);
        }

        /// <summary>One key. True when the layer acted on it.</summary>
        public bool Handle(Keys k)
        {
            bool transmit = _noise.LayerDirection == Radios.DenoiseDirection.Transmit;
            if (HandleStage(k, transmit)) return true;
            if (EngineKeys.TryGetValue(k, out string? id))
            {
                _cursor = StageCursor.Engine;
                bool before = transmit ? _noise.TransmitIsOn(id) : _noise.IsOn(id);
                if (transmit) _noise.TransmitToggle(id, Source);
                else _noise.Toggle(id, Source);
                bool after = transmit ? _noise.TransmitIsOn(id) : _noise.IsOn(id);
                if (after != before) _tone?.Invoke(after);
                return true;
            }
            switch (k)
            {
                case DirectionKey: _cursor = StageCursor.Engine; _noise.SwitchLayerDirection(); return true;
                // The arrows, Home, End and Tab work the engine last touched, in
                // the layer's direction (the controller's lever cursor follows it).
                case Keys.Up: _noise.AdjustCurrent(+1, Source); return true;
                case Keys.Down: _noise.AdjustCurrent(-1, Source); return true;
                case Keys.Home: _noise.PlaceCurrent(-1, Source); return true;
                case Keys.End: _noise.PlaceCurrent(+1, Source); return true;
                case Keys.Tab: _noise.NextLever(+1); return true;
                case Keys.Tab | Keys.Shift: _noise.NextLever(-1); return true;
                case Keys.Space: _noise.SpeakActiveInLayerDirection(); return true;
                case Keys.OemCloseBrackets: if (transmit) _noise.TransmitStep(+1, Source); else _noise.Step(+1, Source); return true;
                case Keys.OemOpenBrackets: if (transmit) _noise.TransmitStep(-1, Source); else _noise.Step(-1, Source); return true;
                // 0: every computer slot off, and back (ruled, questions 17 and 3), in the layer's direction.
                case Keys.D0: if (transmit) _noise.TransmitComputerOffAndBack(Source); else _noise.ComputerOffAndBack(Source); return true;
                case FavouriteKey: WithCurrent(id2 => _noise.ToggleFavourite(id2, Source, _noise.LayerDirection)); return true;
                case DefaultsKey: WithCurrent(id2 => { if (transmit) _noise.TransmitReturnToDefaults(id2, Source); else _noise.ReturnToDefaults(id2, Source); }); return true;
                case NextPresetKey: if (transmit) _noise.StepTransmitPreset(+1, Source); else _noise.StepPreset(+1, Source); return true;
                case PreviousPresetKey: if (transmit) _noise.StepTransmitPreset(-1, Source); else _noise.StepPreset(-1, Source); return true;
                // Learn the room (#1252), through the one road the Home button
                // and the Transmit menu take. A transmit-chain action: in the
                // receive direction it changes nothing and says so, as the
                // de-esser's key does there.
                case LearnRoomKey: if (transmit) _noise.LearnTheRoom(Source); else _noise.SayLearnRoomTransmitOnly(); return true;
            }
            return false;
        }

        /// <summary>
        /// The stage keys: our equaliser and the de-esser (Sprint 49 Track I);
        /// the radio's equaliser, the compressor, the limiter and the handoff
        /// setting (Sprint 49 noise keys); and the arrows, Tab and Space while
        /// one of those stages is what the layer last put them on. True when
        /// handled.
        /// </summary>
        private bool HandleStage(Keys k, bool transmit)
        {
            var side = transmit ? Radios.DenoiseDirection.Transmit : Radios.DenoiseDirection.Receive;
            switch (k)
            {
                case EqualizerKey:
                {
                    bool before = _noise.EqualizerOn(side);
                    _noise.ToggleEqualizer(side, Source);
                    bool after = _noise.EqualizerOn(side);
                    if (after != before) _tone?.Invoke(after);
                    _cursor = StageCursor.Equalizer;
                    return true;
                }
                case RadioEqualizerKey:
                {
                    // On or off; then the arrows work its bands. Not reported yet,
                    // or no radio: said, and the arrows still go to it, so they
                    // find it once the radio answers.
                    bool? before = _noise.RadioEqualizerOn(side);
                    _noise.ToggleRadioEqualizer(side, Source);
                    bool? after = _noise.RadioEqualizerOn(side);
                    if (before is bool b && after is bool a && a != b) _tone?.Invoke(a);
                    _cursor = StageCursor.RadioEqualizer;
                    return true;
                }
                case DeEsserKey:
                {
                    if (!transmit) { _noise.SayDeEsserTransmitOnly(); return true; }
                    bool before = _noise.DeEsserOn;
                    _noise.ToggleDeEsser(Source);
                    bool after = _noise.DeEsserOn;
                    if (after != before) _tone?.Invoke(after);
                    _cursor = StageCursor.DeEsser;
                    return true;
                }
                case CompressorKey:
                {
                    if (!transmit) { _noise.SayCompressorTransmitOnly(); return true; }
                    // The Transmit menu's and Home's own road: held for the unkey
                    // while keyed, said with its timing, owned by the layer for Escape.
                    bool before = _noise.CompressorOn;
                    _noise.ToggleCompressor(Source);
                    bool after = _noise.CompressorOn;
                    if (after != before) _tone?.Invoke(after);
                    _cursor = StageCursor.Compressor;
                    return true;
                }
                case LimiterKey:
                {
                    if (!transmit) { _noise.SayLimiterTransmitOnly(); return true; }
                    bool before = _noise.LimiterOn;
                    _noise.ToggleLimiter(Source);
                    bool after = _noise.LimiterOn;
                    if (after != before) _tone?.Invoke(after);
                    _cursor = StageCursor.Limiter;
                    return true;
                }
                case ProcessorHandoffKey:
                {
                    // A setting, not a stage: the arrows stay where they were.
                    if (!transmit) { _noise.SayProcessorHandoffTransmitOnly(); return true; }
                    bool before = _noise.ProcessorHandoffSetting;
                    _noise.SetProcessorHandoffSetting(!before, Source);
                    bool after = _noise.ProcessorHandoffSetting;
                    if (after != before) _tone?.Invoke(after);
                    return true;
                }
            }
            // The radio's equaliser under the arrows: Left and Right walk its
            // bands, Up and Down move the band, Tab and Shift+Tab walk the bands
            // too (each band has one setting), Space says the band, and the
            // add and remove keys say its bands are fixed.
            if (_cursor == StageCursor.RadioEqualizer)
            {
                switch (k)
                {
                    case PreviousBandKey: case Keys.Tab | Keys.Shift: _radioBand = _noise.StepRadioEqualizerBand(side, _radioBand, -1); return true;
                    case NextBandKey: case Keys.Tab: _radioBand = _noise.StepRadioEqualizerBand(side, _radioBand, +1); return true;
                    case Keys.Up: _noise.AdjustRadioEqualizerBand(side, _radioBand, +1, Source); return true;
                    case Keys.Down: _noise.AdjustRadioEqualizerBand(side, _radioBand, -1, Source); return true;
                    case Keys.Space: _noise.SayRadioEqualizerBand(side, _radioBand); return true;
                    case AddBandKey: case RemoveBandKey: _noise.SayRadioEqualizerFixedBands(side); return true;
                }
            }
            switch (k)
            {
                case PreviousBandKey: _cursor = StageCursor.Equalizer; _noise.SayInLayer(Eq(transmit).SelectBand(-1), side); return true;
                case NextBandKey: _cursor = StageCursor.Equalizer; _noise.SayInLayer(Eq(transmit).SelectBand(+1), side); return true;
                case AddBandKey: _cursor = StageCursor.Equalizer; _noise.SayInLayer(Eq(transmit).AddBand(), side); return true;
                case RemoveBandKey: _cursor = StageCursor.Equalizer; _noise.SayInLayer(Eq(transmit).RemoveBand(), side); return true;
            }
            if (_cursor == StageCursor.Equalizer)
            {
                var eq = Eq(transmit);
                switch (k)
                {
                    case Keys.Up: _noise.SayInLayer(eq.Adjust(+1), side); return true;
                    case Keys.Down: _noise.SayInLayer(eq.Adjust(-1), side); return true;
                    case Keys.Tab: _noise.SayInLayer(eq.SelectField(+1), side); return true;
                    case Keys.Tab | Keys.Shift: _noise.SayInLayer(eq.SelectField(-1), side); return true;
                    case Keys.Space: _noise.SayInLayer(eq.SayBand(), side); return true;
                }
            }
            if (_cursor == StageCursor.DeEsser && transmit)
            {
                var levers = NoiseController.DeEsserLevers;
                switch (k)
                {
                    case Keys.Up: _noise.AdjustDeEsser(levers[_deEsserLever], +1, Source); return true;
                    case Keys.Down: _noise.AdjustDeEsser(levers[_deEsserLever], -1, Source); return true;
                    case Keys.Tab: _deEsserLever = (_deEsserLever + 1) % levers.Length; _noise.SayDeEsserLever(levers[_deEsserLever]); return true;
                    case Keys.Tab | Keys.Shift: _deEsserLever = (_deEsserLever + levers.Length - 1) % levers.Length; _noise.SayDeEsserLever(levers[_deEsserLever]); return true;
                }
            }
            if (_cursor == StageCursor.Compressor && transmit) return HandleDynamics(k, compressor: true);
            if (_cursor == StageCursor.Limiter && transmit) return HandleDynamics(k, compressor: false);
            return false;
        }

        /// <summary>
        /// The compressor's or the limiter's settings under the arrows, as the
        /// de-esser's are: Up and Down step the setting you are on, Tab and
        /// Shift+Tab move between its settings and say the one you reach, and
        /// Space says the one you are on. The compressor's split-band settings
        /// are in the list only while it is in split-band mode; the setting you
        /// are on is kept by name, so a mode change does not move you.
        /// </summary>
        private bool HandleDynamics(Keys k, bool compressor)
        {
            var levers = compressor
                ? Array.FindAll(NoiseController.CompressorLevers, l => !l.SplitBandOnly || _noise.CompressorSplitBand)
                : NoiseController.LimiterLevers;
            string? current = compressor ? _compressorLever : _limiterLever;
            int i = Math.Max(0, Array.FindIndex(levers, l => l.Key == current));
            switch (k)
            {
                case Keys.Up: _noise.AdjustDynamics(levers[i], +1, Source); break;
                case Keys.Down: _noise.AdjustDynamics(levers[i], -1, Source); break;
                case Keys.Tab: i = (i + 1) % levers.Length; _noise.SayDynamicsLever(levers[i]); break;
                case Keys.Tab | Keys.Shift: i = (i + levers.Length - 1) % levers.Length; _noise.SayDynamicsLever(levers[i]); break;
                case Keys.Space: _noise.SayDynamicsLever(levers[i]); break;
                default: return false;
            }
            if (compressor) _compressorLever = levers[i].Key; else _limiterLever = levers[i].Key;
            return true;
        }

        private void WithCurrent(Action<string> act)
        {
            if (_noise.CurrentInLayer is string id) act(id);
            else _noise.SpeakNoEngineYet();
        }
    }
}
