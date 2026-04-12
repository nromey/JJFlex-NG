using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows.Threading;
using JJTrace;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// Pushes a compact radio status line to a connected braille display via Tolk.Braille().
    /// Focus-aware: only pushes when FreqOut (home position) has keyboard focus.
    /// Priority-based field packing fits available cells.
    /// Sprint 25 Phase 8.
    /// </summary>
    public class BrailleStatusEngine
    {
        private FlexBase? _rig;
        private readonly DispatcherTimer _timer;
        private bool _homePositionFocused;
        private string _lastPushed = "";

        /// <summary>Whether braille status line is enabled.</summary>
        public bool Enabled { get; set; }

        /// <summary>Braille display cell count (20, 32, 40, 80).</summary>
        public int CellCount { get; set; } = 40;

        /// <summary>Which fields to include on the braille display.</summary>
        public BrailleFields EnabledFields { get; set; } = BrailleFields.All;

        public BrailleStatusEngine()
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += (s, e) => PushStatus();
        }

        /// <summary>
        /// Wire to a connected radio. Call after radio connection.
        /// </summary>
        public void SetRig(FlexBase? rig)
        {
            _rig = rig;
        }

        /// <summary>
        /// Call when FreqOut gains keyboard focus (home position).
        /// Starts the braille push timer.
        /// </summary>
        public void OnHomePositionFocused()
        {
            _homePositionFocused = true;
            if (Enabled && !_timer.IsEnabled)
            {
                PushStatus(); // immediate update
                _timer.Start();
            }
        }

        /// <summary>
        /// Call when focus leaves FreqOut (user navigating menus, dialogs, etc.).
        /// Stops the braille push timer to let NVDA own the display.
        /// </summary>
        public void OnHomePositionBlurred()
        {
            _homePositionFocused = false;
            _timer.Stop();
        }

        /// <summary>
        /// Build and push the braille status string.
        /// </summary>
        private void PushStatus()
        {
            if (!Enabled || !_homePositionFocused || _rig == null) return;

            try
            {
                if (!ScreenReaderOutput.HasBraille) return;

                string status = BuildBrailleStatus();
                if (status != _lastPushed)
                {
                    Tolk.Braille(status);
                    _lastPushed = status;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"BrailleStatusEngine.PushStatus: {ex.Message}");
            }
        }

        /// <summary>
        /// Build a display-size-aware braille status string.
        /// Content and formatting adapt to CellCount:
        ///   20 cells: freq + mode (critical minimum)
        ///   32 cells: freq + mode + meter + slice
        ///   40 cells: all fields, compact labels (current default behavior)
        ///   80 cells: all fields, full labels with extra spacing
        /// </summary>
        private string BuildBrailleStatus()
        {
            if (_rig == null) return "";

            var parts = new List<string>();
            bool tx = _rig.Transmit;
            bool wide = CellCount >= 80;
            string sep = wide ? "  " : " "; // double spacing for wide displays

            // --- Mandatory: Frequency + Mode (all profiles) ---
            ulong freq = tx ? _rig.TXFrequency : _rig.VirtualRXFrequency;
            if (freq > 0)
            {
                double mhz = freq / 1_000_000.0;
                // Wide displays get 2 extra decimal places
                parts.Add(wide ? mhz.ToString("F5") : mhz.ToString("F3"));
            }

            string mode = _rig.Mode ?? "";
            if (!string.IsNullOrEmpty(mode))
                parts.Add(mode);

            // --- 20-cell profile: stop here (freq + mode only) ---
            if (CellCount <= 20)
                return PackParts(parts, sep);

            // --- 32+ cells: add primary meter + slice ---
            if (!tx && EnabledFields.HasFlag(BrailleFields.SMeter))
            {
                int s = _rig.SMeter;
                if (wide)
                    parts.Add(s <= 9 ? $"S{s}" : $"S9+{(s - 9) * 6}dB");
                else
                    parts.Add(s <= 9 ? $"SM{s}" : $"SM9+{(s - 9) * 6}");
            }

            if (EnabledFields.HasFlag(BrailleFields.Slice) && _rig.MyNumSlices > 1)
            {
                string letter = _rig.ActiveSliceLetter;
                if (!string.IsNullOrEmpty(letter))
                    parts.Add(wide ? $"Slice {letter}" : letter);
            }

            // --- 32-cell profile: stop here (freq + mode + meter + slice) ---
            if (CellCount <= 32)
                return PackParts(parts, sep);

            // --- 40+ cells: add TX meters, DSP flags ---
            if (tx && EnabledFields.HasFlag(BrailleFields.SWR))
            {
                float swr = _rig.SWRValue;
                if (swr > 0)
                    parts.Add(wide ? $"SWR {swr:F1}" : $"SW{swr:F1}");
            }

            if (tx && EnabledFields.HasFlag(BrailleFields.Power))
            {
                float dbm = _rig.PowerDBM;
                int watts = (int)(Math.Pow(10.0, dbm / 10.0) / 1000.0 + 0.5);
                if (watts > 0)
                    parts.Add(wide ? $"{watts}W" : $"PW{watts}");
            }

            if (tx && EnabledFields.HasFlag(BrailleFields.ALC))
            {
                float alc = _rig.ALC;
                parts.Add(wide ? $"ALC {(int)alc}" : $"AL{(int)alc}");
            }

            if (EnabledFields.HasFlag(BrailleFields.DSPFlags))
            {
                if (_rig.NoiseReduction == FlexBase.OffOnValues.on)
                    parts.Add("NR");
                if (_rig.NoiseBlanker == FlexBase.OffOnValues.on)
                    parts.Add("NB");
            }

            return PackParts(parts, sep);
        }

        /// <summary>
        /// Pack parts into exactly CellCount characters, truncating if needed.
        /// </summary>
        private string PackParts(List<string> parts, string separator)
        {
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                if (sb.Length > 0)
                {
                    if (sb.Length + separator.Length + part.Length > CellCount)
                        break;
                    sb.Append(separator);
                }
                sb.Append(part);
            }

            while (sb.Length < CellCount)
                sb.Append(' ');

            return sb.ToString(0, CellCount);
        }

        /// <summary>
        /// Start/stop the timer based on current Enabled state.
        /// </summary>
        public void UpdateTimerState()
        {
            if (Enabled && _homePositionFocused)
            {
                if (!_timer.IsEnabled) _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    /// <summary>
    /// Flags for which fields to show on braille display.
    /// </summary>
    [Flags]
    public enum BrailleFields
    {
        None = 0,
        SMeter = 1,
        SWR = 2,
        Power = 4,
        ALC = 8,
        DSPFlags = 16,
        Slice = 32,
        Compression = 64,
        Voltage = 128,
        All = SMeter | SWR | Power | ALC | DSPFlags | Slice
    }
}
