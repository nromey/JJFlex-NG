using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Flex.Smoothlake.FlexLib;
using HamBands;
using JJFlexUpdater;
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

    // Teardown guard (QB Track A, 2026-08-07, belt-and-suspenders from the
    // 8/05 ActiveSlice sweep): slice/connection events queue RebuildCurrentMenu
    // through the dispatcher, so a rebuild can land AFTER Dispose has
    // destroyed the menu bar — recreating Win32 menus against a dying window.
    private bool _disposed;

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
        if (_disposed || _hwnd == IntPtr.Zero) return;

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
        _disposed = true;
        if (_subscribedRig != null)
        {
            _subscribedRig.SliceCountChanged -= OnSliceCountChanged;
            // ConnectionStateChanged was subscribed alongside SliceCountChanged
            // but never unhooked here — the leaked handler was another way a
            // rebuild could fire against a disposed menu bar.
            _subscribedRig.ConnectionStateChanged -= OnConnectionStateChanged;
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
    /// Adjust an integer value by a step and speak the result. The optional
    /// unit rides the speech ("PC volume 12 dB") so the operator always hears
    /// which scale a value is on — plain numbers stay plain.
    /// </summary>
    private void AdjustValue(string label, Func<int> getter, Action<int> setter,
        int step, int min, int max, string unit = "")
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        int current = getter();
        int newVal = Math.Clamp(current + step, min, max);
        setter(newVal);
        string suffix = string.IsNullOrEmpty(unit) ? "" : " " + unit;
        SpeakAfterMenuClose($"{label} {newVal}{suffix}");
    }

    private void SpeakNoRadio()
    {
        Tracing.TraceLine("NativeMenuBar: no-radio guard fired", TraceLevel.Info);
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
            // "On-Radio" prefix (DSP controls track, 2026-08-11): these run in
            // the radio's own DSP and have PC-side namesakes two submenus
            // down — the names now say which side of the wire each one is.
            AddChecked(nrSub, "On-Radio Neural NR (RNN)\tCtrl+J, R", () =>
                ToggleDSP("On-Radio Neural NR", () => Rig.NeuralNoiseReduction, v => Rig.NeuralNoiseReduction = v),
                () => Rig?.NeuralNoiseReduction == FlexBase.OffOnValues.on);
            AddChecked(nrSub, "On-Radio Spectral NR (NRS)\tCtrl+J, S", () =>
                ToggleDSP("On-Radio Spectral NR", () => Rig.SpectralNoiseReduction, v => Rig.SpectralNoiseReduction = v),
                () => Rig?.SpectralNoiseReduction == FlexBase.OffOnValues.on);
            AddChecked(nrSub, "NR Filter (NRF)\tCtrl+J, Shift+N", () =>
                ToggleDSP("NR Filter", () => Rig.NoiseReductionFilter, v => Rig.NoiseReductionFilter = v),
                () => Rig?.NoiseReductionFilter == FlexBase.OffOnValues.on);
        }
        // Legacy NR always available
        AddChecked(nrSub, "Legacy NR", () =>
            ToggleDSP("Legacy NR", () => Rig.NoiseReductionLegacy, v => Rig.NoiseReductionLegacy = v),
            () => Rig?.NoiseReductionLegacy == FlexBase.OffOnValues.on);
        // QB Track I menu parity — the level under the toggle (matches the
        // ScreenFields NR Level field that appears when Legacy NR is on).
        AddWired(nrSub, "NR Level Up", () =>
            AdjustValue("NR Level", () => Rig.NoiseReductionLegacyLevel, v => Rig.NoiseReductionLegacyLevel = v, 1, 1, 15));
        AddWired(nrSub, "NR Level Down", () =>
            AdjustValue("NR Level", () => Rig.NoiseReductionLegacyLevel, v => Rig.NoiseReductionLegacyLevel = v, -1, 1, 15));

        // === Noise Blankers submenu ===
        var nbSub = AddSubmenu(parent, "Noise Blankers");
        AddChecked(nbSub, "Noise Blanker (NB)\tCtrl+J, B", () =>
            ToggleDSP("Noise Blanker", () => Rig.NoiseBlanker, v => Rig.NoiseBlanker = v),
            () => Rig?.NoiseBlanker == FlexBase.OffOnValues.on);
        AddWired(nbSub, "NB Level Up", () =>
            AdjustValue("NB Level", () => Rig.NoiseBlankerLevel, v => Rig.NoiseBlankerLevel = v, 5, 1, 100));
        AddWired(nbSub, "NB Level Down", () =>
            AdjustValue("NB Level", () => Rig.NoiseBlankerLevel, v => Rig.NoiseBlankerLevel = v, -5, 1, 100));
        AddChecked(nbSub, "Wideband NB (WNB)\tCtrl+J, W", () =>
            ToggleDSP("Wideband NB", () => Rig.WidebandNoiseBlanker, v => Rig.WidebandNoiseBlanker = v),
            () => Rig?.WidebandNoiseBlanker == FlexBase.OffOnValues.on);
        AddWired(nbSub, "WNB Level Up", () =>
            AdjustValue("WNB Level", () => Rig.WidebandNoiseBlankerLevel, v => Rig.WidebandNoiseBlankerLevel = v, 5, 1, 100));
        AddWired(nbSub, "WNB Level Down", () =>
            AdjustValue("WNB Level", () => Rig.WidebandNoiseBlankerLevel, v => Rig.WidebandNoiseBlankerLevel = v, -5, 1, 100));

        // === PC-side noise reduction (runs on the computer, ALL radios) ===
        // QB Track I menu parity — these existed only as ScreenFields
        // checkboxes; the menu is the addressable second door.
        var pcSub = AddSubmenu(parent, "PC Noise Reduction");
        AddChecked(pcSub, "PC Neural NR\tCtrl+J, Shift+R", () =>
        {
            var p = _window.FieldsPanel?.AudioPipeline;
            if (p == null) { SpeakAfterMenuClose("PC audio pipeline not available"); return; }
            p.RnnEnabled = !p.RnnEnabled;
            _window.PersistDspSettings();
            SpeakAfterMenuClose(p.RnnEnabled ? "PC Neural NR on" : "PC Neural NR off");
        }, () => _window.FieldsPanel?.AudioPipeline?.RnnEnabled == true);
        AddChecked(pcSub, "PC Spectral NR\tCtrl+J, Shift+S", () =>
        {
            var p = _window.FieldsPanel?.AudioPipeline;
            if (p == null) { SpeakAfterMenuClose("PC audio pipeline not available"); return; }
            p.SpectralEnabled = !p.SpectralEnabled;
            _window.PersistDspSettings();
            SpeakAfterMenuClose(!p.SpectralEnabled ? "PC Spectral NR off"
                : p.HasNoiseProfile ? "PC Spectral NR on"
                : "PC Spectral NR on, no noise profile loaded. Press Control J then Q to capture one.");
        }, () => _window.FieldsPanel?.AudioPipeline?.SpectralEnabled == true);

        // DSP controls track (2026-08-11) — the capture and the profile
        // room. The capture start is deferred past the menu close so its
        // spoken countdown isn't trampled by NVDA's menu-dismiss chatter
        // (same reasoning as SpeakAfterMenuClose's 500 ms).
        AddWired(pcSub, "Capture Noise Profile\tCtrl+J, Q", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            var p = _window.FieldsPanel?.AudioPipeline;
            if (p == null) { SpeakAfterMenuClose("PC audio pipeline not available"); return; }
            if (NoiseCaptureNarrator.IsRunning) { NoiseCaptureNarrator.Cancel(); return; }
            var rig = Rig;
            _window.Dispatcher.BeginInvoke(async () =>
            {
                await System.Threading.Tasks.Task.Delay(500);
                NoiseCaptureNarrator.Start(rig, p,
                    _window.CurrentAudioConfig?.SpectralSubSampleDuration ?? 3);
            });
        });
        AddWired(pcSub, "Noise Profiles...", () =>
        {
            new Dialogs.NoiseProfilesDialog(Rig, _window.FieldsPanel?.AudioPipeline,
                () => _window.CurrentAudioConfig, () => _window.PersistDspSettings())
                .ShowDialog();
        });
        AddWired(pcSub, "Open Noise Profiles Folder", () =>
        {
            SpeakAfterMenuClose(NoiseProfileStore.OpenFolder()
                ? "Profiles folder opened in File Explorer"
                : "Could not open the profiles folder");
        });

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
        if (Rig != null)
        {
            AddChecked(parent, "Mute/Unmute Slice", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                bool newMute = !Rig.SliceMute;
                Rig.SliceMute = newMute;
                SpeakAfterMenuClose(newMute ? "Muted" : "Unmuted");
            }, () => Rig?.SliceMute == true);

            AddWired(parent, "Mute/Unmute All Slices", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                bool target = !Rig.AllMySlicesMuted;
                Rig.SetAllMySlicesMute(target);
                if (target) EarconPlayer.MuteAllOnTone();
                else EarconPlayer.MuteAllOffTone();
                SpeakAfterMenuClose(target ? "All slices muted" : "All slices unmuted");
            });

            AddWired(parent, "Release All Extra Slices", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                int before = Rig.MyNumSlices;
                if (before <= 1) { SpeakAfterMenuClose("Only one slice active"); return; }
                if (Rig.ReleaseAllExtraSlices())
                {
                    EarconPlayer.MuteAllOnTone();
                    int removed = before - 1;
                    string keptLetter = Rig.VFOToLetter(Rig.RXVFO);
                    SpeakAfterMenuClose(
                        $"Released {removed} extra {(removed == 1 ? "slice" : "slices")}, slice {keptLetter} active");
                }
            });

            AddChecked(parent, "PC Audio On/Off", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                bool wanted = !Rig.PCAudio;
                Rig.PCAudio = wanted;
                // Threads Track (2026-08-12): remember the operator's choice
                // per radio, so remember-last can restore it on the next
                // connect. Intent, not outcome — a toggle that failed tonight
                // is still the wish worth carrying forward.
                RadioConfig.RecordPcAudioUserChoice(Rig.SelectedRadioSerial, wanted);
                // Read the radio back rather than the request. Turning PC audio
                // on can fail — no usable sound device — and the old code
                // announced the wish, not the outcome, so a failed toggle said
                // "PC audio on" while nothing played. QB Track B, 2026-08-07.
                bool actual = Rig.PCAudio;
                SpeakAfterMenuClose(
                    actual ? "PC audio on"
                    : wanted ? "PC audio could not start, still off"
                    : "PC audio off");
            }, () => Rig?.PCAudio == true);

            AddSep(parent);

            // === Levels dialogs (Audio Arc Track A-2, 2026-08-11) ===
            // Field feedback from Noel at the radio: a menu is the wrong
            // instrument for riding a value — it dismisses after each
            // activation, so five nudges meant five trips two menus deep.
            // The up/down PAIRS (Track A's PC Audio and On-Radio submenus,
            // plus the pre-Track-A flat Audio Gain and Pan pairs, which
            // duplicated Home's Volume and Pan fields) are retired; each
            // group is now a single door into a dialog that stays open
            // while you ride its levels with Up/Down. Two doors, not one:
            // the two sides of the wire stay two surfaces on purpose.
            // Slice Volume and Pan live on as arrow fields in Home's audio
            // expander (Ctrl+Shift+U) — they are per-slice controls, not
            // levels on either side of the wire. Ctrl+J, V volume mode is
            // the fast route to all of it and is unchanged.
            AddWired(parent, "PC Audio Levels (this computer)\tCtrl+J, V", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                new Dialogs.PcAudioLevelsDialog(Rig, _window.PersistPcOutputVolume)
                    .ShowDialog();
            });
            AddWired(parent, "On-Radio Levels (the radio's own jacks)\tCtrl+J, V", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                new Dialogs.OnRadioLevelsDialog(Rig).ShowDialog();
            });

            AddSep(parent);
        }

        // Device setup — always available (no radio required).
        // Renamed 2026-08-07 (QB Track B): this is one dialog covering every
        // sound device JJ Flex uses, not the old two-modals-in-a-row radio-only
        // picker. The menu entry survives because muscle memory and the help
        // pages both point at it; only the destination changed.
        AddWired(parent, "Audio Devices", () =>
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
    /// Sprint 22 Phase 6. QB Track I split the two halves into their own
    /// builders so the TX Antenna submenu can also live under Transmit
    /// (next to Power, where the XVTR/power relationship is taught) —
    /// these submenus existed for four sprints without the app's own
    /// author ever finding them, so they get a second door and mnemonics.
    /// </summary>
    private void BuildAntennaSelectItems(IntPtr parent)
    {
        BuildRxAntennaSubmenu(parent);
        BuildTxAntennaSubmenu(parent);
    }

    /// <summary>RX antenna selection submenu — checkmark on the current choice.</summary>
    private void BuildRxAntennaSubmenu(IntPtr parent)
    {
        if (Rig == null) return;

        var rxSub = AddSubmenu(parent, "&RX Antenna");
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
    }

    /// <summary>
    /// TX antenna selection submenu — checkmark on the current choice.
    /// Selecting the transverter port announces the power-semantics change:
    /// that's the moment the operator needs to learn that drive is now dBm.
    /// </summary>
    private void BuildTxAntennaSubmenu(IntPtr parent)
    {
        if (Rig == null) return;

        var txSub = AddSubmenu(parent, "&TX Antenna");
        foreach (var ant in Rig.TXAntennaList)
        {
            var antName = ant;
            AddChecked(txSub, antName, () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                Rig.TXAntennaName = antName;
                if (string.Equals(antName, "XVTR", StringComparison.OrdinalIgnoreCase))
                    SpeakAfterMenuClose("TX antenna XVTR. Power is now transverter drive, in d B m.");
                else
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

    /// <summary>
    /// QB Track I — build the Transmit submenu contents. ONE builder, two
    /// doors: Radio → Transmit (Alt+R, T — the addressable path Noel asked
    /// for) and Slice → Transmission both call this, so the two submenus can
    /// never drift apart. Covers the full ScreenFields Transmission expander
    /// (menu-parity audit finding class a: power had NO menu path anywhere).
    /// Explicit &amp; mnemonics are deliberate here — native Win32 menus render
    /// them as underlined access keys and NVDA reads them cleanly (the old
    /// "no ampersands" guideline was about WinForms MenuStrip labels).
    /// </summary>
    private void BuildTransmitItems(IntPtr parent)
    {
        if (Rig == null)
        {
            AddWired(parent, "Connect a radio first", SpeakNoRadio);
            return;
        }

        // --- Power cluster ---
        AddWired(parent, "&Power...", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            var dlg = new Dialogs.PowerDialog(Rig);
            dlg.ShowDialog();
        });
        AddChecked(parent, "Tune &Carrier\tCtrl+Shift+T", () =>
            _window.ToggleTuneCarrier(),
            () => Rig?.TxTune == true);

        AddSep(parent);

        // --- Antenna (the XVTR/power relationship lives here) ---
        BuildTxAntennaSubmenu(parent);

        AddSep(parent);

        // --- Voice chain ---
        AddChecked(parent, "&VOX On/Off", () =>
            ToggleDSP("VOX", () => Rig.Vox, v => Rig.Vox = v),
            () => Rig?.Vox == FlexBase.OffOnValues.on);
        AddWired(parent, "Mic Gain &Up", () =>
            AdjustValue("Mic Gain", () => Rig.MicGain, v => Rig.MicGain = v, 5, 0, 100));
        AddWired(parent, "Mic Gain &Down", () =>
            AdjustValue("Mic Gain", () => Rig.MicGain, v => Rig.MicGain = v, -5, 0, 100));
        AddChecked(parent, "Mic &Boost (+20 dB)", () =>
            ToggleDSP("Mic Boost", () => Rig.MicBoost, v => Rig.MicBoost = v),
            () => Rig?.MicBoost == FlexBase.OffOnValues.on);
        AddChecked(parent, "Mic B&ias (phantom power)", () =>
            ToggleDSP("Mic Bias", () => Rig.MicBias, v => Rig.MicBias = v),
            () => Rig?.MicBias == FlexBase.OffOnValues.on);
        AddChecked(parent, "Co&mpander", () =>
            ToggleDSP("Compander", () => Rig.Compander, v => Rig.Compander = v),
            () => Rig?.Compander == FlexBase.OffOnValues.on);
        AddWired(parent, "Compander Level Up", () =>
            AdjustValue("Compander Level", () => Rig.CompanderLevel, v => Rig.CompanderLevel = v, 5, 0, 100));
        AddWired(parent, "Compander Level Down", () =>
            AdjustValue("Compander Level", () => Rig.CompanderLevel, v => Rig.CompanderLevel = v, -5, 0, 100));
        AddChecked(parent, "&Speech Processor", () =>
            ToggleDSP("Speech Processor", () => Rig.ProcessorOn, v => Rig.ProcessorOn = v),
            () => Rig?.ProcessorOn == FlexBase.OffOnValues.on);
        AddWired(parent, "Processor Mode", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            // Cycle: Normal → DX → DX+ → Normal
            var next = (FlexBase.ProcessorSettings)(((int)Rig.ProcessorSetting + 1) % 3);
            Rig.ProcessorSetting = next;
            string label = next switch
            {
                FlexBase.ProcessorSettings.DX => "DX",
                FlexBase.ProcessorSettings.DXX => "DX plus",
                _ => "Normal"
            };
            SpeakAfterMenuClose($"Processor mode {label}");
        });

        AddSep(parent);

        // --- Monitor ---
        // Mnemonic O — "&TX Antenna" above owns T in this menu.
        AddChecked(parent, "TX M&onitor", () =>
            ToggleDSP("TX Monitor", () => Rig.Monitor, v => Rig.Monitor = v),
            () => Rig?.Monitor == FlexBase.OffOnValues.on);
        AddWired(parent, "Monitor Level Up", () =>
            AdjustValue("Monitor Level", () => Rig.SBMonitorLevel, v => Rig.SBMonitorLevel = v, 5, 0, 100));
        AddWired(parent, "Monitor Level Down", () =>
            AdjustValue("Monitor Level", () => Rig.SBMonitorLevel, v => Rig.SBMonitorLevel = v, -5, 0, 100));

        // --- TX filter ---
        var txFilterSub = AddSubmenu(parent, "TX &Filter");
        const int txFilterStep = 50;
        AddWired(txFilterSub, "Low Edge Up", () =>
            AdjustValue("TX filter low", () => Rig.TXFilterLow, v => Rig.TXFilterLow = v, txFilterStep, 0, 9950));
        AddWired(txFilterSub, "Low Edge Down", () =>
            AdjustValue("TX filter low", () => Rig.TXFilterLow, v => Rig.TXFilterLow = v, -txFilterStep, 0, 9950));
        AddWired(txFilterSub, "High Edge Up", () =>
            AdjustValue("TX filter high", () => Rig.TXFilterHigh, v => Rig.TXFilterHigh = v, txFilterStep, 50, 10000));
        AddWired(txFilterSub, "High Edge Down", () =>
            AdjustValue("TX filter high", () => Rig.TXFilterHigh, v => Rig.TXFilterHigh = v, -txFilterStep, 50, 10000));
        AddWired(txFilterSub, "Read TX Filter", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            SpeakAfterMenuClose($"TX filter {Rig.TXFilterLow} to {Rig.TXFilterHigh}");
        });

        AddSep(parent);

        // --- Safety ---
        AddChecked(parent, "Dummy &Load Mode", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            Rig.DummyLoadMode = !Rig.DummyLoadMode;
            if (Rig.DummyLoadMode)
                SpeakAfterMenuClose("Dummy load mode on. Power zero.");
            else
                SpeakAfterMenuClose($"Dummy load mode off. Power restored to {Rig.XmitPower}.");
        },
        () => Rig?.DummyLoadMode == true);
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
        AddWired(radio, "MultiFlex Clients", () => _window.ShowMultiFlexDialog());
        AddChecked(radio, "Auto-Connect Enabled",
            () => { var msg = _window.ToggleAutoConnect(); if (msg != null) SpeakAfterMenuClose(msg); },
            () => _window.IsAutoConnectEnabled?.Invoke() ?? false);
        AddWired(radio, "Clear Auto-Connect",
            () => { var msg = _window.ClearAutoConnect(); if (msg != null) SpeakAfterMenuClose(msg); });
        // QB Track A stub audit (2026-08-07): these three were AddNotImplemented
        // leftovers from the native-menu migration whose implementations
        // already existed — wire, don't rebuild.
        AddWired(radio, "Operators", () =>
        {
            if (_window.ShowOperatorsCallback != null)
                _window.ShowOperatorsCallback();
            else
                SpeakAfterMenuClose("Operator management is not available.");
        });
        AddWired(radio, "Profiles", () => ShowManageProfilesDialog());
        AddWired(radio, "Connected Stations", () => ShowConnectedStations());
        // LocalPTT is a one-way claim: FlexLib only supports granting local
        // PTT to this client, never releasing it from here — hence "On".
        AddChecked(radio, "Local PTT On", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            if (Rig.LocalPTT)
            {
                SpeakAfterMenuClose("Local PTT is already on");
                return;
            }
            Rig.LocalPTT = true;
            SpeakAfterMenuClose("Local PTT on");
        }, () => Rig?.LocalPTT == true);

        // QB Track I — the addressable transmit path Noel asked for:
        // Alt+R → T → P walks to the Power dialog with accelerators the whole
        // way. Same builder as Slice → Transmission (one room, two doors).
        var radioTxSub = AddSubmenu(radio, "&Transmit");
        BuildTransmitItems(radioTxSub);

        var loggingSub = AddSubmenu(radio, "Logging");
        AddNotImplemented(loggingSub, "Log Characteristics");
        AddNotImplemented(loggingSub, "Import Log");
        AddNotImplemented(loggingSub, "Export Log");
        AddNotImplemented(loggingSub, "LOTW Merge");

        // ── Maintenance ── (QB Track A, 2026-08-07, Noel's call)
        // Second home for the radio-maintenance actions that otherwise live
        // only inside Settings → Radio Setup. Reboot goes through the SAME
        // shared flow as the hotkey and the Radio Setup step-7 button
        // (RadioMaintenance owns the no-radio announcement, the confirmation
        // that names other connected stations, and the deliberately absent
        // presence gate); firmware update opens the existing Radio Setup
        // surface whose step 3 is the firmware updater — no new firmware UI.
        AddSep(radio);
        AddWired(radio, "Reboot Radio", () =>
            RadioMaintenance.RebootWithConfirmation(Rig, _window.powerNowOff));
        AddWired(radio, "Update Radio Firmware", () => ShowSettingsDialog("Radio Setup"));

        AddSep(radio);
        AddWired(radio, "Exit", () => _window.CloseShellCallback?.Invoke());

        // === Slice ===
        string sliceLabel = Rig != null
            ? $"&Slice ({Rig.TotalNumSlices} of {Rig.MaxSlices})"
            : "&Slice";
        var slice = AddPopup(bar, sliceLabel);

        if (Rig != null)
        {
            // Selection with active slice checkmark.
            // QB Track I fix (Track J audit): iterate OUR slices only —
            // mySlices holds just this client's slices, so positions beyond
            // MyNumSlices rendered as numeric labels that silently no-opped
            // on click under MultiFlex. Other stations' slices get one
            // honest, speaking summary entry instead of dead rows.
            var selSub = AddSubmenu(slice, "Selection");
            for (int i = 0; i < Math.Min(Rig.MyNumSlices, 8); i++)
            {
                int sliceNum = i;
                AddChecked(selSub, $"Slice {Rig.VFOToLetter(i)}",
                    () =>
                    {
                        if (Rig == null || !Rig.ValidVFO(sliceNum)) { SpeakNoRadio(); return; }
                        Rig.RXVFO = sliceNum;
                        SpeakAfterMenuClose($"Slice {Rig.VFOToLetter(sliceNum)} active");
                    },
                    () => Rig?.RXVFO == sliceNum);
            }
            int otherSlices = Rig.TotalNumSlices - Rig.MyNumSlices;
            if (otherSlices > 0)
            {
                AddWired(selSub, $"{otherSlices} in use by other stations", () =>
                    SpeakAfterMenuClose(
                        $"{otherSlices} {(otherSlices == 1 ? "slice is" : "slices are")} in use by other stations. " +
                        "See MultiFlex Clients on the Radio menu."));
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

            // Release Slice (only when WE have more than one slice — another
            // station's slices are not ours to release).
            // QB Track I fix (Track J audit): the old label baked the slice
            // letter in at menu-BUILD time while the handler acted on the
            // click-time RXVFO — label/action drift whenever the active slice
            // changed in between. The label no longer names a letter; the
            // click-time announcement speaks the letter actually released.
            if (Rig.MyNumSlices > 1)
            {
                AddWired(selSub, "Release Active Slice", () =>
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

            // QB Track I — Transmit Slice submenu, mirroring Selection: one
            // entry per slice with the checkmark on the current TX slice,
            // plus an explicit clear. This is the discoverable door for what
            // was previously only the hidden T keypress on the Slice field.
            // Letters come from Slice.Letter via VFOToLetter (the radio's
            // truth — Track J's identity rule), never positional arithmetic.
            if (Rig.CanTransmit)
            {
                // MyNumSlices, not TotalNumSlices — TX can only move between
                // OUR slices (same J-audit dead-entry class as Selection).
                var txSliceSub = AddSubmenu(slice, "Transmit S&lice");
                for (int i = 0; i < Math.Min(Rig.MyNumSlices, 8); i++)
                {
                    int sliceNum = i;
                    AddChecked(txSliceSub, $"Slice {Rig.VFOToLetter(i)}",
                        () =>
                        {
                            if (Rig == null || !Rig.ValidVFO(sliceNum)) { SpeakNoRadio(); return; }
                            Rig.TXVFO = sliceNum;
                            SpeakAfterMenuClose($"Slice {Rig.VFOToLetter(sliceNum)} transmit");
                        },
                        () => Rig?.TXVFO == sliceNum);
                }
                AddSep(txSliceSub);
                AddChecked(txSliceSub, "No Transmit Slice",
                    () =>
                    {
                        if (Rig == null) { SpeakNoRadio(); return; }
                        if (!Rig.HasTransmitSlice)
                        {
                            SpeakAfterMenuClose("No transmit slice is set");
                            return;
                        }
                        Rig.ClearTransmitSlice();
                        SpeakAfterMenuClose("Transmit slice cleared. No slice will key the radio.");
                    },
                    () => Rig?.HasTransmitSlice == false);
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
                    "AM" => "\tAlt+A",
                    "FM" => "\tAlt+F",
                    "DIGU" => "\tAlt+D",
                    "DIGL" => "\tAlt+Shift+D",
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
            // Audio Workshop belongs on the Audio menu, not just Tools — it's the
            // mic-setup/monitoring surface, so operators look for it here (Noel 2026-08-11).
            AddWired(audioSub, "Audio Workshop\tCtrl+Shift+W", () =>
                Dialogs.AudioWorkshopDialog.ShowOrFocus(Rig, 0));

            // Slice management
            BuildSliceItems(slice);

            // Tuning — Sprint 26 Phase 8 expansion per Don's 2026-04-20 request.
            // "JJ Flexible in threes" pattern: every action on this submenu is
            // also reachable as a field keypress (on the FreqOut) or a global
            // hotkey. The menu is the third, discovery-friendly surface.
            var tuningSub = AddSubmenu(slice, "Tuning");

            // Classic/Modern mode toggle — also on Tools menu; duplicated here
            // so operators looking for "tuning" don't have to know it lives
            // under Tools.
            AddChecked(tuningSub, "Classic Tuning Mode\tCtrl+Shift+M",
                () => _window.ToggleUIMode(),
                () => _window.ActiveUIMode == MainWindow.UIMode.Classic);

            // Modern-mode-only tuning action. Sprint 29 Track F (tuning unity)
            // dropped the coarse/fine toggle and the step-cycling pair — Up and
            // Shift+Up now do what their names suggest, and the step values
            // themselves live in Settings → Tuning. The remaining menu entry
            // is the "what are my steps right now?" announcement.
            AddWired(tuningSub, "Speak Current Step\tShift+S", () => _window.TuningMenuSpeakStep());

            AddSep(tuningSub);

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

            // Antenna — RX/TX select, ATU (if present), Diversity (if hardware
            // supports). QB Track I: mnemonic N ("Audio" owns first-letter A),
            // so exploration by accelerator reaches it directly.
            var antSub = AddSubmenu(slice, "A&ntenna");
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

            // Transmission (was "FM" — renamed for consistency with Classic
            // menu). QB Track I: contents now come from the shared
            // BuildTransmitItems builder — identical to Radio → Transmit, so
            // the two doors can never drift apart.
            var txSub = AddSubmenu(slice, "Transmission");
            BuildTransmitItems(txSub);
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
        var filter = AddPopup(bar, "Filt&er");
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
        var audio = AddPopup(bar, "Audi&o");
        BuildAudioItems(audio);
        // Parity with the Slice > Audio submenu (b4bd721f): the workshop is
        // the mic-setup/monitoring surface, and this top-level Audio menu is
        // the first place an operator looks for it.
        AddWired(audio, "Audio Workshop\tCtrl+Shift+W", () =>
            Dialogs.AudioWorkshopDialog.ShowOrFocus(Rig, 0));

        // === Tools ===
        var tools = AddPopup(bar, "&Tools");
        AddWired(tools, "Command Finder", () => ShowCommandFinderDialog());
        AddWired(tools, "Settings", () => ShowSettingsDialog());
        // Second door to the Settings Radios tab — per-radio configuration is
        // a destination of its own (rename a radio, pin its connection mode),
        // so it gets a direct entry rather than making users know which tab
        // holds it. Same one-concept-two-doors pattern as Radio Setup.
        AddWired(tools, "Configure Radio", () => ShowSettingsDialog("Radios"));
        // Sprint 29 Track D — manual update check. Lives next to Settings
        // since the Updates settings tab is its preference home; this entry
        // is the single-action trigger for the same flow.
        AddWired(tools, "Check for Updates", () => ShowCheckForUpdates());
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
        // QB Track A stub audit: the registered StationLookup command was
        // already working (Ctrl+L, Command Finder) — the menu item was the
        // only dead door. Route through ExecuteCommandCallback so the menu,
        // the hotkey, and the Command Finder share one dispatch path.
        AddWired(tools, "Station Lookup\tCtrl+L", () =>
        {
            if (_window.ExecuteCommandCallback != null)
                _window.ExecuteCommandCallback(CommandValues.StationLookup);
            else
                SpeakAfterMenuClose("Station lookup is not available.");
        });
        AddSep(tools);
        AddWired(tools, "Enter Logging Mode", () => _window.EnterLoggingMode());
        AddChecked(tools, "Classic Tuning Mode\tCtrl+Shift+M",
            () => _window.ToggleUIMode(),
            () => _window.ActiveUIMode == MainWindow.UIMode.Classic);
        AddSep(tools);
        // QB Track H (2026-08-07): the Hotkey Editor is the editable door into
        // the one Keys surface; Help → Key Assignments is the viewing door.
        AddWired(tools, "Hotkey Editor", () => ShowKeysSurface(editable: true));
        // QB Track A stub audit: band-plan data (HamBands.Bands) and the WPF
        // ShowBandsDialog both existed, unconnected. Works without a radio —
        // the band table is static data.
        AddWired(tools, "Band Plans", () => ShowBandPlansDialog());
        AddWired(tools, "Feature Availability", () => ShowFeatureAvailability());
        // GPS / GNSS status and reference-oscillator selection. Lives next to
        // Feature Availability because it answers the same shape of question:
        // what hardware does this radio actually have, and is it working.
        AddWired(tools, "GPS and Reference", () =>
        {
            // 2026-08-06: reported "No radio connected" from this item while
            // connected, which the connected-state trace contradicts. Three
            // paths speak near-identical words, so each one traces and speaks
            // distinctly until the repro names the guilty path.
            var gpsRig = Rig;
            Tracing.TraceLine($"Menu: GPS and Reference (rig={(gpsRig == null ? "null" : "present")})", TraceLevel.Info);
            if (gpsRig == null) { SpeakAfterMenuClose("GPS and Reference, no radio connected"); return; }
            try
            {
                new Dialogs.GpsStatusDialog(gpsRig).ShowDialog();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"Menu: GPS and Reference dialog failed: {ex}", TraceLevel.Error);
                SpeakAfterMenuClose("The GPS window could not be opened.");
            }
        });
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
        AddWired(help, "What's &New", () => HelpLauncher.ShowHelp("WhatsNew"));
        AddSep(help);
        // QB Track H (2026-08-07): ONE Key Assignments item (the old
        // Alphabetical / By Function duplicates opened the same dialog three
        // times over). Arrangement is a combo inside the surface now.
        AddWired(help, "Key Assignments", () => ShowKeysSurface(editable: false));
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
            // Restore focus to the JJ Flexible Home after the dialog closes.
            // Without this, WPF returns focus to whatever unnamed parent
            // container was last focused, and screen readers announce
            // generic role text like "pane" — leaving the user unsure of
            // where they are. Re-focusing FreqOut triggers the standard
            // home-arrival announcement via DisplayBox_GotKeyboardFocus,
            // restoring orientation cleanly. 2026-04-24 fix flagged by Noel
            // after Phase 8c-ii About dialog rework.
            _window.FreqOut.FocusDisplay();
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
            var confirm = new Dialogs.ConfirmActionDialog(
                "Connect to Another Radio",
                "You're already connected to a radio.",
                question: "Disconnect from this radio and connect to another radio?",
                yesLabel: "_Disconnect and choose");
            if (confirm.ShowDialog() != true) return;
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

    /// <summary>
    /// Add a menu item whose implementation is not wired yet. Speaks an honest
    /// "not yet implemented" — the old text claimed "not yet connected to
    /// radio", which was a lie for stubs and sent users hunting for a
    /// connection problem that didn't exist (QB Track A, 2026-08-07).
    /// </summary>
    private void AddNotImplemented(IntPtr popup, string text)
    {
        int id = _nextId++;
        AppendMenuW(popup, MF_STRING, (UIntPtr)id, text);
        _handlers[id] = () =>
        {
            Tracing.TraceLine($"Menu: {text} (not yet wired)", TraceLevel.Info);
            SpeakAfterMenuClose($"{text} is not yet implemented in this version.");
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
    /// Open the one Keys surface (QB Track H). Editable from Tools →
    /// Hotkey Editor; viewing from Help → Key Assignments. Works with or
    /// without a radio — key bindings are app state, not radio state.
    /// </summary>
    private void ShowKeysSurface(bool editable)
    {
        var commands = _window.KeyCommandsRef;
        if (commands == null)
        {
            SpeakAfterMenuClose("Key data not available");
            return;
        }
        var dialog = new Dialogs.KeysDialog(commands, editable);
        dialog.ShowDialog();
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
    /// Public deep-link entry: open Settings, optionally already sitting on a
    /// named tab ("Radio Setup", "Network", ...). Used by advisory dialogs so
    /// "Open Radio Setup" is a button, not directions.
    /// </summary>
    public void OpenSettings(string? tab = null) => ShowSettingsDialog(tab);

    /// <summary>
    /// Show the Settings dialog (PTT, Tuning, License, Audio tabs).
    /// </summary>
    private void ShowSettingsDialog(string? openAtTab = null)
    {
        var pttConfig = _window.CurrentPttConfig ?? new PttConfig();
        var handlers = _window.FreqHandlers;
        int coarseStep = handlers?.CoarseTuneStep ?? 1000;
        int fineStep = handlers?.FineTuneStep ?? 10;
        var licenseConfig = handlers?.License;

        // User-scope config lives at BaseConfigDir (root). The FreqHandlers
        // GetConfigDirectory delegate returns BaseConfigDir regardless of
        // connection state -- this is how user-global preferences (calibration
        // unlocks, typing sound) stay visible in Settings even when no radio
        // is connected.
        string? rootConfigDir = handlers?.GetConfigDirectory?.Invoke();

        AudioOutputConfig audioConfig;
        if (_window.OpenParms != null)
        {
            // Connected: load the per-radio config (radio-specific fields) and
            // merge user-global fields from root. Root is authoritative for
            // TuningHash and TypingSound -- the unlock handler always writes
            // them there, and changes made in Settings OK also persist there.
            audioConfig = AudioOutputConfig.Load(_window.OpenParms.ConfigDirectory);
            if (!string.IsNullOrEmpty(rootConfigDir))
            {
                var rootConfig = AudioOutputConfig.Load(rootConfigDir);
                if (!string.IsNullOrEmpty(rootConfig.TuningHash))
                    audioConfig.TuningHash = rootConfig.TuningHash;
                if (rootConfig.TypingSound != TypingSoundMode.Beep)
                    audioConfig.TypingSound = rootConfig.TypingSound;
            }
            _window.CurrentAudioConfig = audioConfig;
        }
        else if (!string.IsNullOrEmpty(rootConfigDir))
        {
            // Disconnected: load directly from root. TuningHash (unlock state)
            // and TypingSound preference are visible; radio-specific fields
            // show whatever was last saved there. Changes persist back to
            // root on Settings OK.
            audioConfig = AudioOutputConfig.Load(rootConfigDir);
        }
        else
        {
            audioConfig = new AudioOutputConfig();
        }

        var dialog = new Dialogs.SettingsDialog(pttConfig, coarseStep, fineStep, licenseConfig, audioConfig);
        dialog.FreqHandlers = _window.FreqHandlers;
        // The Audio tab's device picker needs audioDevices.xml. Prefer the path
        // the connect handed us; fall back to the one globals publishes at
        // startup, so the picker still works with no radio connected.
        dialog.AudioDevicesFile =
            _window.OpenParms?.AudioDevicesFile ?? _window.AudioDevicesFilePath;
        dialog.Rig = _window.RigControl;
        // Settings → Radio Setup → Restart shares the hotkey binding's reboot flow;
        // this is the one piece it cannot reach on its own, since the display state
        // reset lives on MainWindow.
        dialog.OnRebootInitiated = _window.powerNowOff;
        if (_window.OpenParms != null)
        {
            dialog.ConfigDirectory = _window.OpenParms.ConfigDirectory;
            dialog.OperatorName = _window.OpenParms.GetOperatorName?.Invoke();
        }
        if (openAtTab != null)
            dialog.SelectTabByHeader(openAtTab);
        var result = dialog.ShowDialog();
        if (result == true)
        {
            _window.ApplySettingsChanges(dialog.CoarseTuneStep, dialog.FineTuneStep);

            // Persist user-scope fields to root so they're available in any
            // subsequent session regardless of which radio is connected. When
            // connected, the per-radio config also gets the full save at
            // PowerOff (existing flow). When disconnected, this is the only
            // persist path for user changes made in Settings.
            //
            // CW fields matter at app startup BEFORE any connect -- the
            // MainWindow constructor loads these from root so CW delegates are
            // live + CwNotificationsEnabled is set in time for AS to fire at
            // connect-start. Without these being in root, AS was silently
            // skipping because the flag was false until per-radio PowerOn.
            if (!string.IsNullOrEmpty(rootConfigDir))
            {
                var rootConfig = AudioOutputConfig.Load(rootConfigDir);
                rootConfig.TuningHash = audioConfig.TuningHash;
                rootConfig.TypingSound = audioConfig.TypingSound;
                rootConfig.CwNotificationsEnabled = audioConfig.CwNotificationsEnabled;
                rootConfig.CwModeAnnounce = audioConfig.CwModeAnnounce;
                rootConfig.CwSidetoneHz = audioConfig.CwSidetoneHz;
                rootConfig.CwSpeedWpm = audioConfig.CwSpeedWpm;
                rootConfig.Save(rootConfigDir);
            }
        }
        // Always apply typing sound after Settings (config may have been saved even on Cancel)
        if (_window.FreqHandlers != null)
            _window.FreqHandlers.TypingSound = audioConfig.TypingSound;
    }

    /// <summary>
    /// Sprint 29 Track D — Tools → Check for Updates command. Manual
    /// trigger for the same flow Settings → Updates → Check now runs:
    /// fetch the manifest for the user's selected channel, compare
    /// against the running version, surface either an "up to date"
    /// confirmation or the Update available dialog.
    ///
    /// Defers when the user has an active radio session so we don't
    /// pull the rug out mid-QSO per the "no update prompts during
    /// active radio sessions" rule. They can still get the dialog
    /// from Settings → Updates if they want to force it.
    /// </summary>
    private void ShowCheckForUpdates()
    {
        if (Rig != null && Rig.IsConnected)
        {
            var confirm = new Dialogs.ConfirmActionDialog(
                "Check for Updates",
                "You're connected to a radio. JJ Flex usually doesn't prompt about updates during an active session — applying an update will close the app.",
                question: "Check anyway?",
                yesLabel: "_Check");
            if (confirm.ShowDialog() != true) return;
        }

        Radios.ScreenReaderOutput.Speak(
            "Checking for updates",
            Radios.VerbosityLevel.Terse, true);

        _ = RunUpdateCheckAsync();
    }

    private async System.Threading.Tasks.Task RunUpdateCheckAsync()
    {
        try
        {
            var settings = JJFlexUpdater.UpdaterSettings.Load();
            var service = new JJFlexUpdater.UpdaterService();
            var available = await service.CheckForUpdateAsync(settings.Channel)
                                         .ConfigureAwait(true);

            settings.LastCheckUtc = DateTimeOffset.UtcNow;
            settings.Save();

            if (available is null)
            {
                Radios.ScreenReaderOutput.Speak(
                    $"You're up to date on the {settings.Channel.ToDisplayString()} channel.",
                    Radios.VerbosityLevel.Critical, true);
                MessageBox.Show(
                    $"You're up to date on the {settings.Channel.ToDisplayString()} channel.",
                    "Check for updates",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // SkippedVersion: the user has already said "skip this one."
            // Honor that — they can still see it via Settings → Updates if
            // they want to reverse course.
            if (string.Equals(settings.SkippedVersion, available.AvailableVersion,
                              StringComparison.OrdinalIgnoreCase))
            {
                Radios.ScreenReaderOutput.Speak(
                    $"Version {available.AvailableVersion} is available but you've chosen to skip it. " +
                    "Visit Settings, Updates to install it.",
                    Radios.VerbosityLevel.Critical, true);
                return;
            }

            var dialog = new Dialogs.UpdateAvailableDialog(available);
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Radios.ScreenReaderOutput.Speak(
                "Couldn't reach the update server. Check your network connection.",
                Radios.VerbosityLevel.Critical, true);
            Dialogs.AdvisoryDialog.Show("Check for Updates",
                "Couldn't reach the update server. Check your network connection.\n\n" + ex.Message);
        }
    }

    /// <summary>
    /// Show the connected-stations list (QB Track A stub audit). The WPF
    /// ShowStationNamesDialog existed unused since the migration; FlexBase
    /// exposes the station list directly.
    /// </summary>
    private void ShowConnectedStations()
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        List<string> stations;
        try
        {
            stations = Rig.Stations
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        catch (Exception ex)
        {
            Tracing.TraceLine($"ShowConnectedStations: {ex.Message}", TraceLevel.Error);
            SpeakAfterMenuClose("The station list could not be read.");
            return;
        }
        if (stations.Count == 0)
        {
            // The dialog silently self-closes on an empty list — say why
            // nothing appeared instead of letting the click go silent.
            SpeakAfterMenuClose("No stations connected");
            return;
        }
        var dialog = new Dialogs.ShowStationNamesDialog { StationNames = stations };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Show the band-plans browser (QB Track A stub audit). Mirrors the old
    /// WinForms ShowBands form: pick a band (optionally license class and
    /// mode), get the band edges and division breakdown. Static data — works
    /// with no radio connected; when a radio is present the current band and
    /// a mode guess are preselected.
    /// </summary>
    private void ShowBandPlansDialog()
    {
        // Index 0 of the license/mode lists means "All" (no filter), matching
        // the enums' "none" member — the dialog treats index 0 the same way.
        var licenseNames = Enum.GetNames(typeof(Bands.Licenses))
            .Select(n => n == "none" ? "All" : n).ToArray();
        var modeNames = Enum.GetNames(typeof(Bands.Modes))
            .Select(n => n == "none" ? "All" : n).ToArray();
        var bandNames = Bands.TheBands.Select(b => b.Name).ToArray();

        int initialBand = -1;
        int initialMode = -1;
        var rig = Rig;
        if (rig != null)
        {
            var current = Bands.Query(rig.RXFrequency);
            if (current != null) initialBand = current.ID;
            string mode = rig.Mode ?? "";
            initialMode = mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase)
                ? (int)Bands.Modes.CW
                : (int)Bands.Modes.PhoneCW;
        }

        var dialog = new Dialogs.ShowBandsDialog
        {
            BandNames = bandNames,
            LicenseNames = licenseNames,
            ModeNames = modeNames,
            InitialBandIndex = initialBand,
            InitialLicenseIndex = 0,
            InitialModeIndex = initialMode,
            QueryBands = (bandIdx, licenseIdx, modeIdx) =>
            {
                if (bandIdx < 0) return null;
                var band = (Bands.BandNames)bandIdx;
                bool haveLicense = licenseIdx > 0;
                bool haveMode = modeIdx > 0;
                Bands.BandItem result;
                if (haveLicense && haveMode)
                    result = Bands.Query(band, (Bands.Licenses)licenseIdx, (Bands.Modes)modeIdx);
                else if (haveLicense)
                    result = Bands.Query(band, (Bands.Licenses)licenseIdx);
                else if (haveMode)
                    result = Bands.Query(band, (Bands.Modes)modeIdx);
                else
                    result = Bands.Query(band);
                return result == null ? "No band plan entry found." : FormatBandPlan(result);
            }
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Format a band-plan entry for the ShowBandsDialog result box.
    /// Frequencies in kHz, one division per line — screen-reader-friendly
    /// plain text, no tabs or columns.
    /// </summary>
    private static string FormatBandPlan(Bands.BandItem band)
    {
        static string KHz(ulong hz) => (hz / 1000).ToString();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{band.Name}: {KHz(band.Low)} to {KHz(band.High)} kilohertz");
        if (band.Divisions != null)
        {
            foreach (var d in band.Divisions)
            {
                var line = new System.Text.StringBuilder();
                line.Append($"{KHz(d.Low)} to {KHz(d.High)}");
                if (d.License != null && d.License.Length > 0)
                    line.Append(": " + string.Join(", ", d.License.Select(l => l.ToString())));
                if (d.Mode != null && d.Mode.Length > 0)
                    line.Append(" - " + string.Join(", ", d.Mode.Select(m => m.ToString())));
                sb.AppendLine(line.ToString());
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Show the Feature Availability tab of the RadioInfo dialog.
    /// QB Track L: the delegate is wired in MainWindow.OnRadioStarted; if it
    /// is somehow still null, say so — a menu item must never go silent.
    /// </summary>
    private void ShowFeatureAvailability()
    {
        if (Rig == null) { SpeakNoRadio(); return; }
        if (Rig.ShowRadioInfoDialog == null)
        {
            SpeakAfterMenuClose("Radio information is not available yet");
            return;
        }
        Rig.ShowRadioInfoDialog((int)Dialogs.RadioInfoTab.FeatureAvailability);
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
