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
    /// T, Ctrl+B and Ctrl+W are deliberately unbound: they belong to work that
    /// is held (transmit, and the computer blankers), and are refused like any
    /// unknown key.
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
            LegacyAutoNotchKey, FavouriteKey, DefaultsKey, NextPresetKey, PreviousPresetKey,
        };

        private const string Source = "noise layer";
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
            if (EngineKeys.TryGetValue(k, out string? id))
            {
                bool before = _noise.IsOn(id);
                _noise.Toggle(id, Source);
                bool after = _noise.IsOn(id);
                if (after != before) _tone?.Invoke(after);
                return true;
            }
            switch (k)
            {
                case Keys.Up: _noise.AdjustCurrent(+1, Source); return true;
                case Keys.Down: _noise.AdjustCurrent(-1, Source); return true;
                case Keys.Home: _noise.PlaceCurrent(-1, Source); return true;
                case Keys.End: _noise.PlaceCurrent(+1, Source); return true;
                case Keys.Tab: _noise.NextLever(+1); return true;
                case Keys.Tab | Keys.Shift: _noise.NextLever(-1); return true;
                case Keys.Space: _noise.SpeakActive(); return true;
                case Keys.OemCloseBrackets: _noise.Step(+1, Source); return true;
                case Keys.OemOpenBrackets: _noise.Step(-1, Source); return true;
                // 0: every computer slot off, and back (ruled, questions 17 and 3).
                case Keys.D0: _noise.ComputerOffAndBack(Source); return true;
                case FavouriteKey: WithCurrent(id2 => _noise.ToggleFavourite(id2, Source)); return true;
                case DefaultsKey: WithCurrent(id2 => _noise.ReturnToDefaults(id2, Source)); return true;
                case NextPresetKey: _noise.StepPreset(+1, Source); return true;
                case PreviousPresetKey: _noise.StepPreset(-1, Source); return true;
            }
            return false;
        }

        private void WithCurrent(Action<string> act)
        {
            if (_noise.Current is string id) act(id);
            else _noise.SpeakNoEngineYet();
        }
    }
}
