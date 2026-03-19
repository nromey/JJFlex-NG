using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Flex.Smoothlake.FlexLib;
using HamBands;
using JJTrace;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// Native Win32 HMENU menu bar for ShellForm.
/// Uses P/Invoke to create real Win32 menus that screen readers (JAWS/NVDA)
/// navigate correctly — ROLE_SYSTEM_MENUBAR / ROLE_SYSTEM_MENUITEM with no
/// collapse/expand noise. Replaces the managed MenuStrip which announced
/// "collapsed"/"expanded" identically to the old WPF Menu.
///
/// Sprint 13C/13D: Shared handlers for DSP toggles, value adjustments, and
/// filter controls. Used by both Classic (ScreenFields/Operations) and
/// Modern (Slice/Filter/Audio) menu bars.
/// </summary>
public class NativeMenuBar : IDisposable
{
    #region Win32 P/Invoke

    [DllImport("user32.dll")]
    private static extern IntPtr CreateMenu();

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetMenu(IntPtr hWnd, IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool DrawMenuBar(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

    [DllImport("user32.dll")]
    private static extern uint CheckMenuItem(IntPtr hMenu, uint uIDCheckItem, uint uCheck);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ModifyMenuW(IntPtr hMenu, uint uPosition, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    private const uint MF_STRING = 0x0000;
    private const uint MF_POPUP = 0x0010;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_BYCOMMAND = 0x0000;
    private const uint MF_CHECKED = 0x0008;
    private const uint MF_UNCHECKED = 0x0000;

    public const int WM_COMMAND = 0x0111;
    public const int WM_INITMENUPOPUP = 0x0117;

    #endregion

    private readonly MainWindow _window;
    private IntPtr _hwnd;
    private IntPtr _currentMenuBar;
    private readonly Dictionary<int, Action> _handlers = new();
    // Items with dynamic checkmarks: menu item ID → (parent HMENU, state getter)
    private readonly List<(IntPtr popup, int id, Func<bool> stateGetter, string baseText)> _checkItems = new();
    // Top-level popup handle → menu name (for screen reader announcement on open)
    private readonly Dictionary<IntPtr, string> _popupNames = new();
    private int _nextId;

    // Feature gate state (persisted across rebuilds)
    private bool _diversityAvailable;
    private bool _escAvailable;

    // Slice event subscription tracking (to trigger menu rebuild on slice add/remove)
    private FlexBase? _subscribedRig;

    public NativeMenuBar(MainWindow window)
    {
        _window = window;
    }

    private FlexBase? Rig => _window.RigControl;

    /// <summary>
    /// Filter presets for current operator. Set by ApplicationEvents.vb during radio connect.
    /// </summary>
    public FilterPresets? FilterPresets { get; set; }

    /// <summary>
    /// Attach to the form's HWND and apply the initial menu using the current UI mode.
    /// Call from ShellForm.HandleCreated.
    /// </summary>
    public void AttachTo(IntPtr hwnd)
    {
        _hwnd = hwnd;
        ApplyUIMode(_window.ActiveUIMode);
    }

    /// <summary>
    /// Rebuild the menu bar for the specified UI mode.
    /// Destroys the old menu bar and creates a fresh one with only that mode's menus.
    /// </summary>
    public void ApplyUIMode(MainWindow.UIMode mode)
    {
        if (_hwnd == IntPtr.Zero) return;

        Tracing.TraceLine($"NativeMenuBar.ApplyUIMode: {mode}", TraceLevel.Info);

        // Destroy old menu bar (cascades to all submenus)
        var oldMenu = _currentMenuBar;

        // Reset handler tracking for fresh build
        _handlers.Clear();
        _checkItems.Clear();
        _popupNames.Clear();
        _nextId = 1000;

        // Build new menu bar for this mode
        _currentMenuBar = mode switch
        {
            MainWindow.UIMode.Logging => BuildLoggingMenuBar(),
            _ => BuildMenuBar()
        };

        // Swap: set new menu first, then destroy old
        SetMenu(_hwnd, _currentMenuBar);
        DrawMenuBar(_hwnd);

        if (oldMenu != IntPtr.Zero)
            DestroyMenu(oldMenu);

        // Subscribe to slice count changes so menu label stays accurate
        EnsureSliceEventSubscription();

        Tracing.TraceLine($"NativeMenuBar.ApplyUIMode: {mode} complete, {_handlers.Count} items", TraceLevel.Info);
    }

    private void EnsureSliceEventSubscription()
    {
        var rig = Rig;
        if (rig == _subscribedRig) return;
        if (_subscribedRig != null)
        {
            _subscribedRig.SliceCountChanged -= OnSliceCountChanged;
            _subscribedRig.ConnectionStateChanged -= OnConnectionStateChanged;
        }
        if (rig != null)
        {
            rig.SliceCountChanged += OnSliceCountChanged;
            rig.ConnectionStateChanged += OnConnectionStateChanged;
        }
        _subscribedRig = rig;
    }

    private void OnSliceCountChanged()
    {
        _window.Dispatcher.BeginInvoke(new Action(() => RebuildCurrentMenu()));
    }

    private void OnConnectionStateChanged(bool connected)
    {
        _window.Dispatcher.BeginInvoke(new Action(() => RebuildCurrentMenu()));
    }

    /// <summary>
    /// Rebuild the current mode's menu bar (e.g., after radio connects and DSP is available).
    /// Called from MainWindow.SetupOperationsMenu().
    /// </summary>
    public void RebuildCurrentMenu()
    {
        ApplyUIMode(_window.ActiveUIMode);
    }

    /// <summary>
    /// Handle WM_COMMAND from ShellForm.WndProc. Returns true if the command was handled.
    /// </summary>
    public bool HandleWmCommand(IntPtr wParam)
    {
        int id = wParam.ToInt32() & 0xFFFF;
        if (_handlers.TryGetValue(id, out var handler))
        {
            handler();
            _window.Focus();  // Return focus to WPF content after menu action
            return true;
        }
        return false;
    }

    /// <summary>
    /// Handle WM_INITMENUPOPUP — update checkmarks before the menu is shown.
    /// Call from ShellForm.WndProc.
    /// </summary>
    public void HandleInitMenuPopup(IntPtr wParam)
    {
        IntPtr popup = wParam;

        // Only update checkmarks that belong to this specific popup —
        // updating ALL checkmarks on every popup caused NVDA to stutter.
        foreach (var (itemPopup, id, stateGetter, baseText) in _checkItems)
        {
            if (itemPopup != popup) continue;
            try
            {
                bool isOn = stateGetter();
                CheckMenuItem(itemPopup, (uint)id, MF_BYCOMMAND | (isOn ? MF_CHECKED : MF_UNCHECKED));
                // Update text with state suffix so screen readers always announce on/off
                string stateText = isOn ? "On" : "Off";
                ModifyMenuW(itemPopup, (uint)id, MF_BYCOMMAND | MF_STRING, (UIntPtr)id, $"{baseText}: {stateText}");
            }
            catch { /* don't let state read errors block menu display */ }
        }
    }

    /// <summary>
    /// Update feature gate state. Applied during next Classic mode rebuild.
    /// </summary>
    public void UpdateFeatureGates(bool diversityAvailable, bool escAvailable)
    {
        _diversityAvailable = diversityAvailable;
        _escAvailable = escAvailable;
    }

    public void Dispose()
    {
        if (_subscribedRig != null)
        {
            _subscribedRig.SliceCountChanged -= OnSliceCountChanged;
            _subscribedRig = null;
        }
        if (_currentMenuBar != IntPtr.Zero)
        {
            if (_hwnd != IntPtr.Zero)
                SetMenu(_hwnd, IntPtr.Zero);
            DestroyMenu(_currentMenuBar);
            _currentMenuBar = IntPtr.Zero;
        }
        _handlers.Clear();
    }

    #region Shared DSP/Control Handlers — Sprint 13C

    /// <summary>
    /// Get mode-specific filter bounds for boundary detection.
    /// Matches Slice.UpdateFilter() clamping logic.
    /// </summary>
    private (int lowMin, int highMax) GetFilterBounds()
    {
        if (Rig == null) return (0, 12000);
        string mode = Rig.Mode?.ToUpperInvariant() ?? "USB";
        return mode switch
        {
            "LSB" or "DIGL" => (-12000, 0),
            "CW" => (-12000, 12000),
            "USB" or "DIGU" or "FDV" => (0, 12000),
            _ => (-12000, 12000)
        };
    }

    /// <summary>
    /// Toggle an OffOnValues property and speak the result.
    /// Used by NR, NB, ANF, APF, VOX, Squelch, etc.
    /// </summary>
    private void ToggleDSP(string label, Func<FlexBase.OffOnValues> getter, Action<FlexBase.OffOnValues> setter)
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        var current = getter();
        var newVal = Rig.ToggleOffOn(current);
        setter(newVal);
        SpeakAfterMenuClose($"{label} {(newVal == FlexBase.OffOnValues.on ? "on" : "off")}");
    }

    /// <summary>
    /// Adjust an integer value by a step and speak the result.
    /// </summary>
    private void AdjustValue(string label, Func<int> getter, Action<int> setter,
        int step, int min, int max)
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        int current = getter();
        int newVal = Math.Clamp(current + step, min, max);
        setter(newVal);
        SpeakAfterMenuClose($"{label} {newVal}");
    }

    /// <summary>
    /// Toggle a bool property and speak the result.
    /// </summary>
    private void ToggleBool(string label, Func<bool> getter, Action<bool> setter)
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        bool newVal = !getter();
        setter(newVal);
        SpeakAfterMenuClose($"{label} {(newVal ? "on" : "off")}");
    }

    private void SpeakNoRadio()
    {
        SpeakAfterMenuClose("No radio connected");
    }

    /// <summary>
    /// Build ScreenFields DSP submenu (shared between Classic and Modern DSP menus).
    /// </summary>
    private void BuildDSPItems(IntPtr parent)
    {
        if (Rig == null) return;

        // === Noise Reduction submenu ===
        var nrSub = AddSubmenu(parent, "Noise Reduction");
        // NRF, NRS, RNN all require 8000-series/Aurora DSP hardware
        if (Rig.NeuralNRHardwareSupported)
        {
            AddChecked(nrSub, "Neural NR (RNN)\tCtrl+J, R", () =>
                ToggleDSP("Neural NR", () => Rig.NeuralNoiseReduction, v => Rig.NeuralNoiseReduction = v),
                () => Rig?.NeuralNoiseReduction == FlexBase.OffOnValues.on);
            AddChecked(nrSub, "Spectral NR (NRS)\tCtrl+J, S", () =>
                ToggleDSP("Spectral NR", () => Rig.SpectralNoiseReduction, v => Rig.SpectralNoiseReduction = v),
                () => Rig?.SpectralNoiseReduction == FlexBase.OffOnValues.on);
            AddChecked(nrSub, "NR Filter (NRF)\tCtrl+J, Shift+N", () =>
                ToggleDSP("NR Filter", () => Rig.NoiseReductionFilter, v => Rig.NoiseReductionFilter = v),
                () => Rig?.NoiseReductionFilter == FlexBase.OffOnValues.on);
        }
        // Legacy NR always available
        AddChecked(nrSub, "Legacy NR", () =>
            ToggleDSP("Legacy NR", () => Rig.NoiseReductionLegacy, v => Rig.NoiseReductionLegacy = v),
            () => Rig?.NoiseReductionLegacy == FlexBase.OffOnValues.on);

        // === Noise Blankers submenu ===
        var nbSub = AddSubmenu(parent, "Noise Blankers");
        AddChecked(nbSub, "Noise Blanker (NB)\tCtrl+J, B", () =>
            ToggleDSP("Noise Blanker", () => Rig.NoiseBlanker, v => Rig.NoiseBlanker = v),
            () => Rig?.NoiseBlanker == FlexBase.OffOnValues.on);
        AddChecked(nbSub, "Wideband NB (WNB)\tCtrl+J, W", () =>
            ToggleDSP("Wideband NB", () => Rig.WidebandNoiseBlanker, v => Rig.WidebandNoiseBlanker = v),
            () => Rig?.WidebandNoiseBlanker == FlexBase.OffOnValues.on);

        // === Auto Notch ===
        var anfSub = AddSubmenu(parent, "Auto Notch");
        AddChecked(anfSub, "FFT Auto-Notch\tCtrl+J, A", () =>
            ToggleDSP("FFT Auto-Notch", () => Rig.AutoNotchFFT, v => Rig.AutoNotchFFT = v),
            () => Rig?.AutoNotchFFT == FlexBase.OffOnValues.on);
        AddChecked(anfSub, "Legacy Auto-Notch", () =>
            ToggleDSP("Legacy Auto-Notch", () => Rig.AutoNotchLegacy, v => Rig.AutoNotchLegacy = v),
            () => Rig?.AutoNotchLegacy == FlexBase.OffOnValues.on);

        // === Audio Peak Filter (CW only) ===
        AddChecked(parent, "Audio Peak Filter (APF)\tCtrl+J, P", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            string? mode = Rig.Mode;
            if (mode != null && !mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase))
            {
                SpeakAfterMenuClose("Audio Peak Filter is CW only");
                return;
            }
            ToggleDSP("Audio Peak Filter", () => Rig.APF, v => Rig.APF = v);
        }, () => Rig?.APF == FlexBase.OffOnValues.on);

        AddSep(parent);

        // === Meter Tones ===
        var meterSub = AddSubmenu(parent, "Meter Tones");
        AddChecked(meterSub, "Meter Tones On/Off\tCtrl+Alt+M", () =>
        {
            _window.ToggleMetersPanel();
        }, () => MeterToneEngine.Enabled);

        AddWired(meterSub, "Cycle Preset", () =>
        {
            MeterToneEngine.CyclePreset();
            SpeakAfterMenuClose($"Meter preset: {MeterToneEngine.CurrentPreset}");
        });

        AddWired(meterSub, "Speak Meters", () =>
        {
            MeterToneEngine.SpeakMeters();
        });

        AddChecked(meterSub, "Peak Watcher", () =>
        {
            MeterToneEngine.PeakWatcherEnabled = !MeterToneEngine.PeakWatcherEnabled;
            SpeakAfterMenuClose($"Peak Watcher {(MeterToneEngine.PeakWatcherEnabled ? "on" : "off")}");
        }, () => MeterToneEngine.PeakWatcherEnabled);
    }

    /// <summary>
    /// Build filter control items (shared between Classic ScreenFields and Modern Filter menu).
    /// </summary>
    private void BuildFilterItems(IntPtr parent)
    {
        if (Rig == null) return;

        const int filterStep = 50;

        // All filter operations use SetFilter() to set both edges atomically.
        // Setting FilterLow and FilterHigh separately through the command queue
        // causes a race condition: FlexLib clamps each edge against the other's
        // stale value, creating a death spiral to 0-10 Hz bandwidth.

        AddWired(parent, "Narrow Filter", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            int low = Rig.FilterLow;
            int high = Rig.FilterHigh;
            int newLow = low + filterStep;
            int newHigh = high - filterStep;
            if (newHigh - newLow >= 50)
            {
                Rig.SetFilter(newLow, newHigh);
                SpeakAfterMenuClose($"Filter {newLow} to {newHigh}");
            }
            else
            {
                SpeakAfterMenuClose("Filter at minimum");
            }
        });
        AddWired(parent, "Widen Filter", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            var (lowMin, highMax) = GetFilterBounds();
            int low = Rig.FilterLow;
            int high = Rig.FilterHigh;
            int newLow = Math.Max(low - filterStep, lowMin);
            int newHigh = Math.Min(high + filterStep, highMax);
            if (newLow == low && newHigh == high)
            {
                SpeakAfterMenuClose("Filter at maximum");
            }
            else
            {
                Rig.SetFilter(newLow, newHigh);
                SpeakAfterMenuClose($"Filter {newLow} to {newHigh}");
            }
        });
        AddWired(parent, "Shift Low Edge Up", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            int newLow = Rig.FilterLow + filterStep;
            int high = Rig.FilterHigh;
            if (high - newLow >= 10)
            {
                Rig.SetFilter(newLow, high);
                SpeakAfterMenuClose($"Low edge {newLow}");
            }
            else
            {
                SpeakAfterMenuClose("Filter at minimum");
            }
        });
        AddWired(parent, "Shift Low Edge Down", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            var (lowMin, _) = GetFilterBounds();
            int low = Rig.FilterLow;
            int newLow = Math.Max(low - filterStep, lowMin);
            if (newLow == low)
            {
                SpeakAfterMenuClose("Beginning");
            }
            else
            {
                Rig.SetFilter(newLow, Rig.FilterHigh);
                SpeakAfterMenuClose($"Low edge {newLow}");
            }
        });
        AddWired(parent, "Shift High Edge Up", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            var (_, highMax) = GetFilterBounds();
            int high = Rig.FilterHigh;
            int newHigh = Math.Min(high + filterStep, highMax);
            if (newHigh == high)
            {
                SpeakAfterMenuClose("End");
            }
            else
            {
                Rig.SetFilter(Rig.FilterLow, newHigh);
                SpeakAfterMenuClose($"High edge {newHigh}");
            }
        });
        AddWired(parent, "Shift High Edge Down", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            int low = Rig.FilterLow;
            int newHigh = Rig.FilterHigh - filterStep;
            if (newHigh - low >= 10)
            {
                Rig.SetFilter(low, newHigh);
                SpeakAfterMenuClose($"High edge {newHigh}");
            }
            else
            {
                SpeakAfterMenuClose("Filter at minimum");
            }
        });

        AddWired(parent, "Read Filter", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            SpeakAfterMenuClose($"Filter {Rig.FilterLow} to {Rig.FilterHigh}");
        });

        // Filter presets submenu
        if (FilterPresets != null && Rig != null)
        {
            AddSep(parent);
            string mode = Rig.Mode ?? "USB";
            var presets = FilterPresets.GetPresetsForMode(mode);
            int activeIdx = FilterPresets.FindActivePreset(mode, Rig.FilterLow, Rig.FilterHigh);

            for (int i = 0; i < presets.Count; i++)
            {
                var preset = presets[i];
                string label = $"{preset.Name} ({preset.FormatForSpeech()})";
                if (i == activeIdx)
                    label = $"\u2713 {label}"; // Unicode checkmark prefix
                AddWired(parent, label, () =>
                {
                    if (Rig == null) { SpeakNoRadio(); return; }
                    string currentMode = Rig.Mode ?? "USB";
                    var (mLow, mHigh) = FilterPresets.MirrorForMode(currentMode, preset.Low, preset.High);
                    Rig.SetFilter(mLow, mHigh);
                    SpeakAfterMenuClose($"{preset.Name}, {preset.FormatForSpeech()}");
                });
            }
        }

        AddSep(parent);
        AddWired(parent, "Filter Calculator", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            var dialog = new Dialogs.FilterCalculatorDialog();
            if (dialog.ShowDialog() == true && dialog.ResultLow.HasValue && dialog.ResultHigh.HasValue)
            {
                Rig.SetFilter(dialog.ResultLow.Value, dialog.ResultHigh.Value);
            }
        });
    }

    /// <summary>
    /// Build audio control items (shared between Classic Operations and Modern Audio/Slice menus).
    /// Radio-dependent items are guarded; non-radio items (earcon device) are always available.
    /// </summary>
    private void BuildAudioItems(IntPtr parent)
    {
        const int gainStep = 10;

        if (Rig != null)
        {
            AddChecked(parent, "Mute/Unmute Slice", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                bool newMute = !Rig.SliceMute;
                Rig.SliceMute = newMute;
                SpeakAfterMenuClose(newMute ? "Muted" : "Unmuted");
            }, () => Rig?.SliceMute == true);

            AddChecked(parent, "PC Audio On/Off", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                Rig.PCAudio = !Rig.PCAudio;
                SpeakAfterMenuClose(Rig.PCAudio ? "PC audio on" : "PC audio off");
            }, () => Rig?.PCAudio == true);

            AddSep(parent);

            AddWired(parent, "Audio Gain Up", () =>
                AdjustValue("Audio Gain", () => Rig.AudioGain, v => Rig.AudioGain = v, gainStep, 0, 100));
            AddWired(parent, "Audio Gain Down", () =>
                AdjustValue("Audio Gain", () => Rig.AudioGain, v => Rig.AudioGain = v, -gainStep, 0, 100));

            AddWired(parent, "Pan Left", () =>
                AdjustValue("Pan", () => Rig.AudioPan, v => Rig.AudioPan = v, -10, 0, 100));
            AddWired(parent, "Pan Right", () =>
                AdjustValue("Pan", () => Rig.AudioPan, v => Rig.AudioPan = v, 10, 0, 100));

            AddSep(parent);

            AddWired(parent, "Headphone Level Up", () =>
                AdjustValue("Headphone", () => Rig.HeadphoneGain, v => Rig.HeadphoneGain = v, gainStep, 0, 100));
            AddWired(parent, "Headphone Level Down", () =>
                AdjustValue("Headphone", () => Rig.HeadphoneGain, v => Rig.HeadphoneGain = v, -gainStep, 0, 100));

            AddWired(parent, "Line Out Level Up", () =>
                AdjustValue("Line Out", () => Rig.LineoutGain, v => Rig.LineoutGain = v, gainStep, 0, 100));
            AddWired(parent, "Line Out Level Down", () =>
                AdjustValue("Line Out", () => Rig.LineoutGain, v => Rig.LineoutGain = v, -gainStep, 0, 100));

            AddSep(parent);
        }

        // Device setup — always available (no radio required)
        AddWired(parent, "Radio Audio Device", () =>
            _window.AudioSetupCallback?.Invoke());
        AddWired(parent, "Earcon Scratchpad", () =>
        {
            var dlg = new Dialogs.EarconScratchpadDialog();
            dlg.ShowDialog();
        });
    }

    /// <summary>
    /// Build slice management items (Create/Release Slice).
    /// Sprint 22 Phase 7.
    /// </summary>
    private void BuildSliceItems(IntPtr parent)
    {
        if (Rig == null) return;

        AddWired(parent, "Create Slice", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            int countBefore = Rig.MyNumSlices;
            if (Rig.NewSlice())
                SpeakAfterMenuClose($"Slice created, {countBefore + 1} active");
            else
                SpeakAfterMenuClose("Maximum slices reached");
        });

        AddWired(parent, "Release Slice", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            int numSlices = Rig.MyNumSlices;
            if (numSlices <= 1)
            {
                SpeakAfterMenuClose("Cannot release the only slice");
                return;
            }
            int toRemove = numSlices - 1;
            string letter = Rig.VFOToLetter(toRemove);
            if (Rig.RemoveSlice(toRemove))
                SpeakAfterMenuClose($"Slice {letter} released, {numSlices - 1} active");
            else
                SpeakAfterMenuClose("Could not release slice");
        });
    }

    /// <summary>
    /// Build RX/TX antenna selection submenus. Dynamic — reads antenna lists from the radio.
    /// Sprint 22 Phase 6.
    /// </summary>
    private void BuildAntennaSelectItems(IntPtr parent)
    {
        if (Rig == null) return;

        // RX Antenna submenu
        var rxSub = AddSubmenu(parent, "RX Antenna");
        foreach (var ant in Rig.RXAntennaList)
        {
            var antName = ant; // capture for closure
            AddChecked(rxSub, antName, () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                Rig.RXAntennaName = antName;
                SpeakAfterMenuClose($"RX antenna {antName}");
            }, () => string.Equals(Rig?.RXAntennaName, antName, StringComparison.OrdinalIgnoreCase));
        }

        // TX Antenna submenu
        var txSub = AddSubmenu(parent, "TX Antenna");
        foreach (var ant in Rig.TXAntennaList)
        {
            var antName = ant;
            AddChecked(txSub, antName, () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                Rig.TXAntennaName = antName;
                SpeakAfterMenuClose($"TX antenna {antName}");
            }, () => string.Equals(Rig?.TXAntennaName, antName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Build ATU (Antenna Tuner) control items.
    /// </summary>
    private void BuildATUItems(IntPtr parent)
    {
        if (Rig == null) return;

        AddChecked(parent, "ATU On/Off", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            bool isOn = Rig.FlexTunerType != FlexBase.FlexTunerTypes.none;
            Rig.FlexTunerType = isOn ? FlexBase.FlexTunerTypes.none : FlexBase.FlexTunerTypes.auto;
            SpeakAfterMenuClose($"ATU {(isOn ? "off" : "on")}");
        }, () => Rig?.FlexTunerType != FlexBase.FlexTunerTypes.none);

        AddWired(parent, "ATU Mode", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            // Cycle: none → manual → auto → none
            var mode = Rig.FlexTunerType;
            var newMode = mode switch
            {
                FlexBase.FlexTunerTypes.none => FlexBase.FlexTunerTypes.manual,
                FlexBase.FlexTunerTypes.manual => FlexBase.FlexTunerTypes.auto,
                FlexBase.FlexTunerTypes.auto => FlexBase.FlexTunerTypes.none,
                _ => FlexBase.FlexTunerTypes.auto
            };
            Rig.FlexTunerType = newMode;
            SpeakAfterMenuClose($"ATU mode {newMode}");
        });

        AddWired(parent, "ATU Memories", () =>
        {
            if (Rig?.ShowMemoriesDialog != null)
                Rig.ShowMemoriesDialog();
            else
                SpeakAfterMenuClose("ATU memories not available");
        });
    }

    /// <summary>
    /// Build diversity items with proper feature gating.
    /// </summary>
    private void BuildDiversityItems(IntPtr parent)
    {
        if (Rig == null) return;

        if (Rig.DiversityReady)
        {
            AddChecked(parent, "Toggle Diversity", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                Rig.ToggleDiversity();
                SpeakAfterMenuClose(Rig.DiversityOn ? "Diversity on" : "Diversity off");
            }, () => Rig?.DiversityOn == true);
        }
        else
        {
            string gateMsg = Rig.DiversityGateMessage;
            if (!string.IsNullOrEmpty(gateMsg))
            {
                AddWired(parent, "Diversity unavailable", () =>
                    SpeakAfterMenuClose(Rig?.DiversityGateMessage ?? "Diversity unavailable"));
            }
        }
    }

    /// <summary>
    /// Build receiver controls (AGC, Squelch, RF Gain) — shared between menus.
    /// </summary>
    private void BuildReceiverItems(IntPtr parent)
    {
        if (Rig == null) return;

        AddWired(parent, "AGC Mode", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            // Cycle: Off → Slow → Medium → Fast → Off
            var mode = Rig.AGCSpeed;
            var newMode = mode switch
            {
                AGCMode.Off => AGCMode.Slow,
                AGCMode.Slow => AGCMode.Medium,
                AGCMode.Medium => AGCMode.Fast,
                AGCMode.Fast => AGCMode.Off,
                _ => AGCMode.Medium
            };
            Rig.AGCSpeed = newMode;
            SpeakAfterMenuClose($"AGC {newMode}");
        });

        AddWired(parent, "AGC Threshold Up", () =>
            AdjustValue("AGC Threshold", () => Rig.AGCThreshold, v => Rig.AGCThreshold = v,
                FlexBase.AGCThresholdIncrement, FlexBase.AGCThresholdMin, FlexBase.AGCThresholdMax));
        AddWired(parent, "AGC Threshold Down", () =>
            AdjustValue("AGC Threshold", () => Rig.AGCThreshold, v => Rig.AGCThreshold = v,
                -FlexBase.AGCThresholdIncrement, FlexBase.AGCThresholdMin, FlexBase.AGCThresholdMax));

        AddSep(parent);

        AddChecked(parent, "Squelch On/Off", () =>
            ToggleDSP("Squelch", () => Rig.Squelch, v => Rig.Squelch = v),
            () => Rig?.Squelch == FlexBase.OffOnValues.on);
        AddWired(parent, "Squelch Level Up", () =>
            AdjustValue("Squelch", () => Rig.SquelchLevel, v => Rig.SquelchLevel = v,
                FlexBase.SquelchLevelIncrement, FlexBase.SquelchLevelMin, FlexBase.SquelchLevelMax));
        AddWired(parent, "Squelch Level Down", () =>
            AdjustValue("Squelch", () => Rig.SquelchLevel, v => Rig.SquelchLevel = v,
                -FlexBase.SquelchLevelIncrement, FlexBase.SquelchLevelMin, FlexBase.SquelchLevelMax));

        AddSep(parent);

        AddWired(parent, "RF Gain Up", () =>
            AdjustValue("RF Gain", () => Rig.RFGain, v => Rig.RFGain = v,
                Rig.RFGainIncrement, Rig.RFGainMin, Rig.RFGainMax));
        AddWired(parent, "RF Gain Down", () =>
            AdjustValue("RF Gain", () => Rig.RFGain, v => Rig.RFGain = v,
                -Rig.RFGainIncrement, Rig.RFGainMin, Rig.RFGainMax));
    }

    #endregion

    #region Main Menu Bar

    private IntPtr BuildMenuBar()
    {
        var bar = CreateMenu();

        // === Radio ===
        var radio = AddPopup(bar, "&Radio");
        if (Rig != null && Rig.IsConnected)
            AddWired(radio, "Disconnect", () => _window.CloseRadioCallback?.Invoke());
        else
            AddWired(radio, "Connect to Radio", () => ConnectWithConfirmation());
        AddWired(radio, "Manage SmartLink Accounts", () => _window.ShowSmartLinkAccountManager());
        AddChecked(radio, "Auto-Connect Enabled",
            () => { var msg = _window.ToggleAutoConnect(); if (msg != null) SpeakAfterMenuClose(msg); },
            () => _window.IsAutoConnectEnabled?.Invoke() ?? false);
        AddWired(radio, "Clear Auto-Connect",
            () => { var msg = _window.ClearAutoConnect(); if (msg != null) SpeakAfterMenuClose(msg); });
        AddNotImplemented(radio, "Operators");
        AddWired(radio, "Profiles", () => ShowManageProfilesDialog());
        AddNotImplemented(radio, "Connected Stations");
        AddNotImplemented(radio, "Local PTT On");

        var loggingSub = AddSubmenu(radio, "Logging");
        AddNotImplemented(loggingSub, "Log Characteristics");
        AddNotImplemented(loggingSub, "Import Log");
        AddNotImplemented(loggingSub, "Export Log");
        AddNotImplemented(loggingSub, "LOTW Merge");

        AddSep(radio);
        AddWired(radio, "Exit", () => _window.CloseShellCallback?.Invoke());

        // === Slice ===
        string sliceLabel = Rig != null
            ? $"&Slice ({Rig.TotalNumSlices} of {Rig.MaxSlices})"
            : "&Slice";
        var slice = AddPopup(bar, sliceLabel);

        if (Rig != null)
        {
            // Selection with active slice checkmark
            var selSub = AddSubmenu(slice, "Selection");
            for (int i = 0; i < Math.Min(Rig.TotalNumSlices, 8); i++)
            {
                int sliceNum = i;
                AddChecked(selSub, $"Slice {Rig.VFOToLetter(i)}",
                    () =>
                    {
                        if (Rig == null || !Rig.ValidVFO(sliceNum)) return;
                        Rig.RXVFO = sliceNum;
                        SpeakAfterMenuClose($"Slice {Rig.VFOToLetter(sliceNum)} active");
                    },
                    () => Rig?.RXVFO == sliceNum);
            }

            AddSep(selSub);

            // New Slice
            AddWired(selSub, "New Slice", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                int countBefore = Rig.MyNumSlices;
                if (Rig.NewSlice())
                    SpeakAfterMenuClose($"Slice created, {countBefore + 1} active");
                else
                    SpeakAfterMenuClose("Cannot create slice, maximum reached");
            });

            // Release Slice (only when more than 1 slice exists)
            if (Rig.TotalNumSlices > 1)
            {
                int activeSlice = Rig.RXVFO;
                AddWired(selSub, $"Release Slice {Rig.VFOToLetter(activeSlice)}", () =>
                {
                    if (Rig == null) { SpeakNoRadio(); return; }
                    int toRemove = Rig.RXVFO;
                    if (Rig.MyNumSlices <= 1)
                    {
                        SpeakAfterMenuClose("Cannot release last slice");
                        return;
                    }
                    int switchTo = -1;
                    for (int j = 0; j < Rig.MyNumSlices; j++)
                    {
                        if (j != toRemove) { switchTo = j; break; }
                    }
                    if (switchTo >= 0)
                    {
                        string removedLetter = Rig.VFOToLetter(toRemove);
                        int countBefore = Rig.MyNumSlices;
                        if (Rig.CanTransmit && toRemove == Rig.TXVFO)
                            Rig.TXVFO = switchTo;
                        Rig.RXVFO = switchTo;
                        if (Rig.RemoveSlice(toRemove))
                            SpeakAfterMenuClose($"Slice {removedLetter} released, {countBefore - 1} active");
                        else
                            SpeakAfterMenuClose("Cannot release this slice");
                    }
                });
            }

            // Mode
            var modeSub = AddSubmenu(slice, "Mode");
            foreach (string modeName in RigCaps.ModeTable)
            {
                string m = modeName;
                // Add accelerator hints for modes with hotkeys
                string accel = m switch
                {
                    "USB" => "\tAlt+U",
                    "LSB" => "\tAlt+L",
                    "CW" => "\tAlt+C",
                    _ => ""
                };
                AddWired(modeSub, m + accel, () =>
                {
                    if (Rig == null) { SpeakNoRadio(); return; }
                    Rig.Mode = m;
                    SpeakAfterMenuClose($"Mode {m}");
                });
            }
            AddSep(modeSub);
            AddWired(modeSub, "Next Mode\tAlt+M", () => _window.CycleMode(1));
            AddWired(modeSub, "Previous Mode\tAlt+Shift+M", () => _window.CycleMode(-1));

            // Audio
            var audioSub = AddSubmenu(slice, "Audio");
            BuildAudioItems(audioSub);

            // Slice management
            BuildSliceItems(slice);

            // Tuning
            var tuningSub = AddSubmenu(slice, "Tuning");
            AddWired(tuningSub, "RIT On/Off", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                var rit = new FlexBase.RITData(Rig.RIT);
                rit.Active = !rit.Active;
                Rig.RIT = rit;
                SpeakAfterMenuClose($"RIT {(rit.Active ? "on" : "off")}");
            });
            AddWired(tuningSub, "XIT On/Off", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                var xit = new FlexBase.RITData(Rig.XIT);
                xit.Active = !xit.Active;
                Rig.XIT = xit;
                SpeakAfterMenuClose($"XIT {(xit.Active ? "on" : "off")}");
            });

            // Receiver
            var rxSub = AddSubmenu(slice, "Receiver");
            BuildReceiverItems(rxSub);

            // DSP
            var dspSub = AddSubmenu(slice, "DSP");
            BuildDSPItems(dspSub);

            // Antenna — RX/TX select, ATU (if present), Diversity (if hardware supports)
            var antSub = AddSubmenu(slice, "Antenna");
            BuildAntennaSelectItems(antSub);
            if (Rig.HasATU)
            {
                AddSep(antSub);
                BuildATUItems(antSub);
                AddSep(antSub);
                AddWired(antSub, "ATU Tune\tCtrl+T", () => _window.StartATUTuneCycle());
            }
            if (Rig.DiversityHardwareSupported)
            {
                AddSep(antSub);
                BuildDiversityItems(antSub);
            }

            // Transmission (was "FM" — renamed for consistency with Classic menu)
            var txSub = AddSubmenu(slice, "Transmission");
            AddChecked(txSub, "Tune Carrier\tCtrl+Shift+T", () =>
                _window.ToggleTuneCarrier(),
                () => Rig?.TxTune == true);
            AddSep(txSub);
            AddChecked(txSub, "VOX On/Off", () =>
                ToggleDSP("VOX", () => Rig.Vox, v => Rig.Vox = v),
                () => Rig?.Vox == FlexBase.OffOnValues.on);
            AddSep(txSub);
            AddChecked(txSub, "Dummy Load Mode", () =>
            {
                Rig.DummyLoadMode = !Rig.DummyLoadMode;
                if (Rig.DummyLoadMode)
                    SpeakAfterMenuClose("Dummy load mode on. Power zero.");
                else
                    SpeakAfterMenuClose($"Dummy load mode off. Power restored to {Rig.XmitPower}.");
            },
            () => Rig?.DummyLoadMode == true);
        }
        else
        {
            AddWired(slice, "Connect a radio first", SpeakNoRadio);
        }

        // === Band ===
        var band = AddPopup(bar, "&Band");
        if (Rig != null)
        {
            BuildBandItems(band);
        }
        else
        {
            AddWired(band, "Connect a radio first", SpeakNoRadio);
        }

        // === Filter ===
        var filter = AddPopup(bar, "&Filter");
        if (Rig != null)
        {
            BuildFilterItems(filter);
        }
        else
        {
            AddWired(filter, "Connect a radio first", SpeakNoRadio);
        }

        // === ScreenFields (Panel Navigation) ===
        var screenFields = AddPopup(bar, "Scree&nFields");
        AddChecked(screenFields, "Show Field Panel", () =>
        {
            var panel = _window.FieldsPanel;
            bool newVisible = panel.Visibility != Visibility.Visible;
            panel.Visibility = newVisible ? Visibility.Visible : Visibility.Collapsed;
            _window.FieldsPanelUserVisible = newVisible;
            _window.SaveFieldsPanelVisibleCallback?.Invoke(newVisible);
            SpeakAfterMenuClose(newVisible ? "Field panel shown" : "Field panel hidden");
        }, () => _window.FieldsPanel.Visibility == Visibility.Visible);
        AddSep(screenFields);
        AddWired(screenFields, "Noise Reduction and DSP\tCtrl+Shift+N",
            () => _window.FieldsPanel.ToggleCategory(0));
        AddWired(screenFields, "Audio\tCtrl+Shift+U",
            () => _window.FieldsPanel.ToggleCategory(1));
        AddWired(screenFields, "Receiver\tCtrl+Shift+R",
            () => _window.FieldsPanel.ToggleCategory(2));
        AddWired(screenFields, "Transmission\tCtrl+Shift+X",
            () => _window.FieldsPanel.ToggleCategory(3));
        AddWired(screenFields, "Antenna\tCtrl+Shift+A",
            () => _window.FieldsPanel.ToggleCategory(4));

        // === Audio ===
        var audio = AddPopup(bar, "&Audio");
        BuildAudioItems(audio);

        // === Tools ===
        var tools = AddPopup(bar, "&Tools");
        AddWired(tools, "Command Finder", () => ShowCommandFinderDialog());
        AddWired(tools, "Settings", () => ShowSettingsDialog());
        AddWired(tools, "Speak Status\tCtrl+Shift+S", () =>
        {
            // Delay speech so it fires after menu close + focus return,
            // otherwise NVDA's focus announcement stomps on the status.
            _window.Dispatcher.BeginInvoke(async () =>
            {
                await System.Threading.Tasks.Task.Delay(250);
                _window.SpeakStatusCallback?.Invoke();
            });
        });
        AddWired(tools, "Status Dialog\tCtrl+Alt+S", () =>
            _window.ShowStatusDialogCallback?.Invoke());
        AddNotImplemented(tools, "Station Lookup");
        AddSep(tools);
        AddWired(tools, "Enter Logging Mode", () => _window.EnterLoggingMode());
        AddChecked(tools, "Classic Tuning Mode\tCtrl+Shift+M",
            () => _window.ToggleUIMode(),
            () => _window.ActiveUIMode == MainWindow.UIMode.Classic);
        AddSep(tools);
        AddNotImplemented(tools, "Hotkey Editor");
        AddNotImplemented(tools, "Band Plans");
        AddWired(tools, "Feature Availability", () => ShowFeatureAvailability());
        AddWired(tools, "Profile Report", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            var report = ProfileReporter.GenerateReport(Rig);
            var path = ProfileReporter.SaveReport(report);
            SpeakAfterMenuClose($"Profile report saved to {path}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        });
        AddSep(tools);
        AddWired(tools, "Export Profiles", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            bool success = Rig.ExportProfileDatabase();
            if (!success)
                SpeakAfterMenuClose("Profile export cancelled or failed");
        });
        AddWired(tools, "Import Profiles", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            bool success = Rig.ImportProfileDatabase();
            if (!success)
                SpeakAfterMenuClose("Profile import cancelled or failed");
        });
        AddSep(tools);
        AddWired(tools, "View Test Results", () => _window.ShowTestResultsCallback?.Invoke());
        AddNotImplemented(tools, "Manage CW Messages");
        AddSep(tools);
        AddWired(tools, "Audio Workshop\tCtrl+Shift+W", () =>
            Dialogs.AudioWorkshopDialog.ShowOrFocus(Rig, 0));

        // === Help (shared) ===
        BuildHelpPopup(bar);

        return bar;
    }

    private void BuildBandItems(IntPtr parent)
    {
        // Helper: check if current RX frequency is in the given band
        Func<Bands.BandNames, bool> isOnBand = (band) =>
        {
            if (Rig == null) return false;
            var bi = Bands.Query(Rig.RXFrequency);
            return bi != null && bi.Band == band;
        };

        // Main bands (F3-F9)
        AddChecked(parent, "160m\tF3", () => _window.BandJump(Bands.BandNames.m160), () => isOnBand(Bands.BandNames.m160));
        AddChecked(parent, "80m\tF4", () => _window.BandJump(Bands.BandNames.m80), () => isOnBand(Bands.BandNames.m80));
        AddChecked(parent, "40m\tF5", () => _window.BandJump(Bands.BandNames.m40), () => isOnBand(Bands.BandNames.m40));
        AddChecked(parent, "20m\tF6", () => _window.BandJump(Bands.BandNames.m20), () => isOnBand(Bands.BandNames.m20));
        AddChecked(parent, "15m\tF7", () => _window.BandJump(Bands.BandNames.m15), () => isOnBand(Bands.BandNames.m15));
        AddChecked(parent, "10m\tF8", () => _window.BandJump(Bands.BandNames.m10), () => isOnBand(Bands.BandNames.m10));
        AddChecked(parent, "6m\tF9", () => _window.BandJump(Bands.BandNames.m6), () => isOnBand(Bands.BandNames.m6));
        AddSep(parent);

        // WARC bands (Shift+F3-F6)
        AddChecked(parent, "60m\tShift+F3", () => _window.BandJump(Bands.BandNames.m60), () => isOnBand(Bands.BandNames.m60));
        AddChecked(parent, "30m\tShift+F4", () => _window.BandJump(Bands.BandNames.m30), () => isOnBand(Bands.BandNames.m30));
        AddChecked(parent, "17m\tShift+F5", () => _window.BandJump(Bands.BandNames.m17), () => isOnBand(Bands.BandNames.m17));
        AddChecked(parent, "12m\tShift+F6", () => _window.BandJump(Bands.BandNames.m12), () => isOnBand(Bands.BandNames.m12));
        AddSep(parent);

        // Navigation
        AddWired(parent, "Band Up\tAlt+Up", () => _window.BandNavigate(1));
        AddWired(parent, "Band Down\tAlt+Down", () => _window.BandNavigate(-1));
        AddSep(parent);

        // 60m channel navigation
        AddWired(parent, "60m Channel Up\tAlt+Shift+Up", () => _window.SixtyMeterChannelNavigate(1));
        AddWired(parent, "60m Channel Down\tAlt+Shift+Down", () => _window.SixtyMeterChannelNavigate(-1));
    }

    #endregion

    #region Logging Menu Bar

    private IntPtr BuildLoggingMenuBar()
    {
        var bar = CreateMenu();

        // === Log ===
        var log = AddPopup(bar, "&Log");
        AddNotImplemented(log, "New Entry");
        AddNotImplemented(log, "Write Entry");
        AddNotImplemented(log, "Search Log");
        AddNotImplemented(log, "Full Log Form");
        AddSep(log);
        AddNotImplemented(log, "Log Characteristics");
        AddNotImplemented(log, "Import Log");
        AddNotImplemented(log, "Export Log");
        AddNotImplemented(log, "LOTW Merge");
        AddSep(log);
        AddNotImplemented(log, "Log Statistics");
        AddSep(log);
        AddNotImplemented(log, "Reset Confirmations");

        // === Navigate ===
        var navigate = AddPopup(bar, "&Navigate");
        AddStub(navigate, "First Entry");
        AddStub(navigate, "Previous Entry");
        AddStub(navigate, "Next Entry");
        AddStub(navigate, "Last Entry");

        // === Mode ===
        var mode = AddPopup(bar, "&Mode");
        AddWired(mode, "Exit to Classic Tuning", () =>
        {
            _window.LastNonLogMode = MainWindow.UIMode.Classic;
            _window.ExitLoggingMode();
        });
        AddWired(mode, "Exit to Modern Tuning", () =>
        {
            _window.LastNonLogMode = MainWindow.UIMode.Modern;
            _window.ExitLoggingMode();
        });

        // === Help (shared) ===
        BuildHelpPopup(bar);

        return bar;
    }

    #endregion

    #region Help Menu (shared)

    private void BuildHelpPopup(IntPtr bar)
    {
        var help = AddPopup(bar, "&Help");
        AddWired(help, "Help Topics\tF1", () => HelpLauncher.ShowHelp());
        AddWired(help, "Keyboard Reference", () => HelpLauncher.ShowHelp("CommandFinder"));
        AddSep(help);
        AddWired(help, "Key Assignments", () => ShowKeysDialog());
        AddWired(help, "Key Assignments (Alphabetical)", () => ShowKeysDialog());
        AddWired(help, "Key Assignments (By Function)", () => ShowKeysDialog());
        AddWired(help, "Tracing", () =>
        {
            var dialog = new Dialogs.TraceAdminDialog
            {
                InitialFilePath = Tracing.TraceFile ?? "",
                DefaultLevel = (int)(Tracing.TheSwitch?.Level ?? System.Diagnostics.TraceLevel.Info),
                StartTracing = (filePath, levelIndex) =>
                {
                    Tracing.On = false;
                    Tracing.TraceFile = filePath;
                    Tracing.TheSwitch.Level = (System.Diagnostics.TraceLevel)levelIndex;
                    Tracing.On = true;
                    Tracing.TraceLine($"User started tracing at level {Tracing.TheSwitch.Level}");
                },
                StopTracing = () =>
                {
                    Tracing.TraceLine("User stopped tracing");
                    Tracing.On = false;
                }
            };
            dialog.ShowDialog();
        });
        AddSep(help);
        AddWired(help, "Earcon Explorer", () =>
            Dialogs.AudioWorkshopDialog.ShowOrFocus(Rig, 2));
        AddSep(help);
        AddWired(help, "About", () =>
        {
            var dialog = new Dialogs.AboutDialog
            {
                Rig = Rig,
                SpeakCallback = (msg, interrupt) => Radios.ScreenReaderOutput.Speak(msg, interrupt)
            };
            dialog.ShowDialog();
        });
    }

    #endregion

    #region Helpers

    /// <summary>
    /// BUG-023: If already connected, confirm before connecting to a different radio.
    /// </summary>
    private void ConnectWithConfirmation()
    {
        if (Rig != null && Rig.IsConnected)
        {
            var result = MessageBox.Show(
                "You're already connected to a radio. Disconnect from this radio and connect to another radio?",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }
        _window.SelectRadioCallback?.Invoke();
    }

    /// <summary>Add a popup (dropdown) menu to the menu bar.</summary>
    private IntPtr AddPopup(IntPtr menuBar, string text)
    {
        var popup = CreatePopupMenu();
        AppendMenuW(menuBar, MF_POPUP, (UIntPtr)popup, text);
        // Track name for screen reader announcement (strip & accelerator prefix)
        _popupNames[popup] = text.Replace("&", "");
        return popup;
    }

    /// <summary>Add a submenu to a parent popup menu.</summary>
    private IntPtr AddSubmenu(IntPtr parentPopup, string text)
    {
        var sub = CreatePopupMenu();
        AppendMenuW(parentPopup, MF_POPUP, (UIntPtr)sub, text);
        return sub;
    }

    /// <summary>Add a menu item with a specific handler.</summary>
    private void AddWired(IntPtr popup, string text, Action handler)
    {
        int id = _nextId++;
        AppendMenuW(popup, MF_STRING, (UIntPtr)id, text);
        _handlers[id] = handler;
    }

    /// <summary>Add a checkable menu item — checkmark updated dynamically via WM_INITMENUPOPUP.</summary>
    private void AddChecked(IntPtr popup, string text, Action handler, Func<bool> stateGetter)
    {
        int id = _nextId++;
        AppendMenuW(popup, MF_STRING, (UIntPtr)id, text);
        _handlers[id] = handler;
        _checkItems.Add((popup, id, stateGetter, text));
    }

    /// <summary>Add a menu item that speaks "not yet connected to radio".</summary>
    private void AddNotImplemented(IntPtr popup, string text)
    {
        int id = _nextId++;
        AppendMenuW(popup, MF_STRING, (UIntPtr)id, text);
        _handlers[id] = () =>
        {
            Tracing.TraceLine($"Menu: {text} (not yet wired)", TraceLevel.Info);
            SpeakAfterMenuClose($"{text}, not yet connected to radio");
        };
    }

    /// <summary>Add a stub menu item that speaks "coming soon".</summary>
    private void AddStub(IntPtr popup, string text)
    {
        int id = _nextId++;
        AppendMenuW(popup, MF_STRING, (UIntPtr)id, $"{text} - coming soon");
        _handlers[id] = () =>
        {
            SpeakAfterMenuClose($"{text}, coming soon. Use Classic mode for full features.");
        };
    }

    /// <summary>
    /// Speak a message after the menu closes. Uses a 150ms delay so the screen reader
    /// picks up the speech after the menu closes and focus returns to the main window.
    /// </summary>
    private void SpeakAfterMenuClose(string message)
    {
        _window.Dispatcher.BeginInvoke(async () =>
        {
            // 500ms: NVDA needs time to process menu-close event internally.
            // Interrupt: cut off NVDA's window title re-announcement so user hears
            // the actual result (e.g., "Antenna 1") instead of the title first.
            await System.Threading.Tasks.Task.Delay(500);
            Radios.ScreenReaderOutput.Speak(message, Radios.VerbosityLevel.Terse, interrupt: true);
        });
    }

    /// <summary>Add a separator line.</summary>
    private void AddSep(IntPtr popup)
    {
        AppendMenuW(popup, MF_SEPARATOR, UIntPtr.Zero, null);
    }

    #endregion

    #region Dialog Launchers — Sprint 16 Track C

    /// <summary>
    /// Show the Key Assignments dialog populated with current key bindings.
    /// </summary>
    private void ShowKeysDialog()
    {
        var keyActions = _window.GetKeyActionsCallback?.Invoke();
        if (keyActions == null)
        {
            SpeakAfterMenuClose("Key data not available");
            return;
        }
        var dialog = new Dialogs.ShowKeysDialog
        {
            KeyActions = keyActions,
            AvailableKeys = keyActions, // Same list — the dialog filters configured vs available
            AvailableActions = _window.GetAvailableActionsCallback?.Invoke()
        };
        if (dialog.ShowDialog() == true)
        {
            _window.SaveKeyActionsCallback?.Invoke(dialog.KeyActions);
        }
    }

    /// <summary>
    /// Show the Command Finder dialog with all available commands.
    /// </summary>
    private void ShowCommandFinderDialog()
    {
        var dialog = new Dialogs.CommandFinderDialog
        {
            GetCommands = () => _window.GetCommandFinderItemsCallback?.Invoke()
                ?? new List<Dialogs.CommandFinderItem>(),
            ExecuteCommand = (tag) => _window.ExecuteCommandCallback?.Invoke(tag),
            SpeakText = (msg) => Radios.ScreenReaderOutput.Speak(msg),
            CurrentMode = _window.ActiveUIMode.ToString()
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Show the Settings dialog (PTT, Tuning, License, Audio tabs).
    /// </summary>
    private void ShowSettingsDialog()
    {
        var pttConfig = _window.CurrentPttConfig ?? new PttConfig();
        var handlers = _window.FreqHandlers;
        int coarseStep = handlers?.CoarseTuneStep ?? 1000;
        int fineStep = handlers?.FineTuneStep ?? 10;
        var licenseConfig = handlers?.License;

        // Reload audio config from disk to pick up any changes (e.g., calibration unlocks)
        // Calibration unlocks save to the root config dir (BaseConfigDir), but the main
        // audio config loads from the Radios subdirectory. Merge TuningHash from root.
        var audioConfig = _window.CurrentAudioConfig;
        if (audioConfig != null && _window.OpenParms != null)
        {
            audioConfig = AudioOutputConfig.Load(_window.OpenParms.ConfigDirectory);
            // Merge TuningHash from root config (where calibration unlock saves)
            string rootDir = System.IO.Path.GetDirectoryName(_window.OpenParms.ConfigDirectory) ?? "";
            if (!string.IsNullOrEmpty(rootDir))
            {
                var rootConfig = AudioOutputConfig.Load(rootDir);
                if (!string.IsNullOrEmpty(rootConfig.TuningHash))
                    audioConfig.TuningHash = rootConfig.TuningHash;
                if (rootConfig.TypingSound != TypingSoundMode.Beep)
                    audioConfig.TypingSound = rootConfig.TypingSound;
            }
            _window.CurrentAudioConfig = audioConfig;
        }
        audioConfig ??= new AudioOutputConfig();
        var dialog = new Dialogs.SettingsDialog(pttConfig, coarseStep, fineStep, licenseConfig, audioConfig);
        if (dialog.ShowDialog() == true)
        {
            _window.ApplySettingsChanges(dialog.CoarseTuneStep, dialog.FineTuneStep);
        }
    }

    /// <summary>
    /// Show the Feature Availability tab of the RadioInfo dialog.
    /// </summary>
    private void ShowFeatureAvailability()
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        Rig.ShowRadioInfoDialog?.Invoke((int)Dialogs.RadioInfoTab.FeatureAvailability);
    }

    /// <summary>
    /// Show the Manage Profiles dialog.
    /// </summary>
    private void ShowManageProfilesDialog()
    {
        if (Rig == null) { SpeakNoRadio(); return; }

        var callbacks = new Dialogs.ProfileDialogCallbacks
        {
            GetDisplayItems = () =>
            {
                var items = new List<Dialogs.ProfileDisplayItem>();
                var types = new[] {
                    Radios.ProfileTypes.global,
                    Radios.ProfileTypes.tx,
                    Radios.ProfileTypes.mic
                };
                foreach (var ptype in types)
                {
                    var profiles = Rig.GetProfilesByType(ptype);
                    if (profiles == null) continue;
                    foreach (var p in profiles)
                    {
                        string suffix = p.Default ? " (default)" : "";
                        string typeLabel = ptype.ToString().ToUpperInvariant();
                        items.Add(new Dialogs.ProfileDisplayItem
                        {
                            DisplayText = $"[{typeLabel}] {p.Name}{suffix}",
                            ProfileData = p
                        });
                    }
                }
                return items;
            },
            GetProfileTypeNames = () => new[] { "Global", "TX", "MIC" },
            GetProfileNamesByType = (typeIndex) =>
            {
                var ptype = typeIndex switch
                {
                    0 => Radios.ProfileTypes.global,
                    1 => Radios.ProfileTypes.tx,
                    2 => Radios.ProfileTypes.mic,
                    _ => Radios.ProfileTypes.global
                };
                var profiles = Rig.GetProfilesByType(ptype);
                return profiles?.Select(p => p.Name) ?? Enumerable.Empty<string>();
            },
            OnAdd = (result) =>
            {
                // Not implemented yet — profile creation requires radio-specific API calls
                SpeakAfterMenuClose("Profile creation not yet available");
            },
            OnUpdate = (originalData, result) =>
            {
                SpeakAfterMenuClose("Profile update not yet available");
            },
            OnDelete = (profileData) =>
            {
                if (profileData is Radios.Profile_t profile)
                {
                    bool ok = Rig.DeleteProfile(profile);
                    return ok ? null : "Could not delete profile";
                }
                return "Invalid profile data";
            },
            OnSelect = (profileData) =>
            {
                if (profileData is Radios.Profile_t profile)
                {
                    bool ok = Rig.SelectProfile(profile);
                    if (ok)
                        SpeakAfterMenuClose($"Profile {profile.Name} selected");
                    return ok ? null : "Could not select profile";
                }
                return "Invalid profile data";
            },
            OnSave = (profileData) =>
            {
                if (profileData is Radios.Profile_t profile)
                {
                    Rig.SaveProfile(profile, immediately: true);
                    SpeakAfterMenuClose($"Profile {profile.Name} saved");
                }
            },
            IsGlobalProfile = (profileData) =>
                profileData is Radios.Profile_t p && p.ProfileType == Radios.ProfileTypes.global,
            GetProfileEditData = (profileData) =>
            {
                if (profileData is Radios.Profile_t p)
                {
                    int typeIndex = p.ProfileType switch
                    {
                        Radios.ProfileTypes.global => 0,
                        Radios.ProfileTypes.tx => 1,
                        Radios.ProfileTypes.mic => 2,
                        _ => 0
                    };
                    return (p.Name, typeIndex, p.Default);
                }
                return ("", 0, false);
            }
        };

        var dialog = new Dialogs.ProfileDialog(callbacks);
        dialog.ShowDialog();
    }

    #endregion
}
