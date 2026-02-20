using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
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

    private const uint MF_STRING = 0x0000;
    private const uint MF_POPUP = 0x0010;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_BYCOMMAND = 0x0000;

    public const int WM_COMMAND = 0x0111;

    #endregion

    private readonly MainWindow _window;
    private IntPtr _hwnd;
    private IntPtr _currentMenuBar;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId;

    // Feature gate state (persisted across rebuilds)
    private bool _diversityAvailable;
    private bool _escAvailable;

    public NativeMenuBar(MainWindow window)
    {
        _window = window;
    }

    private FlexBase? Rig => _window.RigControl;

    /// <summary>
    /// Attach to the form's HWND and apply the initial menu (Modern mode default).
    /// Call from ShellForm.HandleCreated.
    /// </summary>
    public void AttachTo(IntPtr hwnd)
    {
        _hwnd = hwnd;
        ApplyUIMode(MainWindow.UIMode.Modern);
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
        _nextId = 1000;

        // Build new menu bar for this mode
        _currentMenuBar = mode switch
        {
            MainWindow.UIMode.Classic => BuildClassicMenuBar(),
            MainWindow.UIMode.Modern => BuildModernMenuBar(),
            MainWindow.UIMode.Logging => BuildLoggingMenuBar(),
            _ => BuildModernMenuBar()
        };

        // Swap: set new menu first, then destroy old
        SetMenu(_hwnd, _currentMenuBar);
        DrawMenuBar(_hwnd);

        if (oldMenu != IntPtr.Zero)
            DestroyMenu(oldMenu);

        Tracing.TraceLine($"NativeMenuBar.ApplyUIMode: {mode} complete, {_handlers.Count} items", TraceLevel.Info);
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
    /// Update feature gate state. Applied during next Classic mode rebuild.
    /// </summary>
    public void UpdateFeatureGates(bool diversityAvailable, bool escAvailable)
    {
        _diversityAvailable = diversityAvailable;
        _escAvailable = escAvailable;
    }

    public void Dispose()
    {
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
        AddWired(nrSub, "Neural NR (RNN)", () =>
            ToggleDSP("Neural NR", () => Rig.NeuralNoiseReduction, v => Rig.NeuralNoiseReduction = v));
        AddWired(nrSub, "Spectral NR (NRS)", () =>
            ToggleDSP("Spectral NR", () => Rig.SpectralNoiseReduction, v => Rig.SpectralNoiseReduction = v));
        AddWired(nrSub, "Legacy NR", () =>
            ToggleDSP("Legacy NR", () => Rig.NoiseReductionLegacy, v => Rig.NoiseReductionLegacy = v));

        // === Noise Blankers submenu ===
        var nbSub = AddSubmenu(parent, "Noise Blankers");
        AddWired(nbSub, "Noise Blanker (NB)", () =>
            ToggleDSP("Noise Blanker", () => Rig.NoiseBlanker, v => Rig.NoiseBlanker = v));
        AddWired(nbSub, "Wideband NB (WNB)", () =>
            ToggleDSP("Wideband NB", () => Rig.WidebandNoiseBlanker, v => Rig.WidebandNoiseBlanker = v));

        // === Auto Notch ===
        var anfSub = AddSubmenu(parent, "Auto Notch");
        AddWired(anfSub, "FFT Auto-Notch", () =>
            ToggleDSP("FFT Auto-Notch", () => Rig.AutoNotchFFT, v => Rig.AutoNotchFFT = v));
        AddWired(anfSub, "Legacy Auto-Notch", () =>
            ToggleDSP("Legacy Auto-Notch", () => Rig.AutoNotchLegacy, v => Rig.AutoNotchLegacy = v));

        // === Audio Peak Filter (CW only) ===
        AddWired(parent, "Audio Peak Filter (APF)", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            string? mode = Rig.Mode;
            if (mode != null && !mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase))
            {
                SpeakAfterMenuClose("Audio Peak Filter is CW only");
                return;
            }
            ToggleDSP("Audio Peak Filter", () => Rig.APF, v => Rig.APF = v);
        });
    }

    /// <summary>
    /// Build filter control items (shared between Classic ScreenFields and Modern Filter menu).
    /// </summary>
    private void BuildFilterItems(IntPtr parent)
    {
        if (Rig == null) return;

        const int filterStep = 50;

        AddWired(parent, "Narrow Filter", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            Rig.FilterLow += filterStep;
            Rig.FilterHigh -= filterStep;
            SpeakAfterMenuClose($"Filter {Rig.FilterLow} to {Rig.FilterHigh}");
        });
        AddWired(parent, "Widen Filter", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            Rig.FilterLow = Math.Max(0, Rig.FilterLow - filterStep);
            Rig.FilterHigh += filterStep;
            SpeakAfterMenuClose($"Filter {Rig.FilterLow} to {Rig.FilterHigh}");
        });
        AddWired(parent, "Shift Low Edge Up", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            Rig.FilterLow += filterStep;
            SpeakAfterMenuClose($"Low edge {Rig.FilterLow}");
        });
        AddWired(parent, "Shift Low Edge Down", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            Rig.FilterLow = Math.Max(0, Rig.FilterLow - filterStep);
            SpeakAfterMenuClose($"Low edge {Rig.FilterLow}");
        });
        AddWired(parent, "Shift High Edge Up", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            Rig.FilterHigh += filterStep;
            SpeakAfterMenuClose($"High edge {Rig.FilterHigh}");
        });
        AddWired(parent, "Shift High Edge Down", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            Rig.FilterHigh = Math.Max(0, Rig.FilterHigh - filterStep);
            SpeakAfterMenuClose($"High edge {Rig.FilterHigh}");
        });
    }

    /// <summary>
    /// Build audio control items (shared between Classic Operations and Modern Audio/Slice menus).
    /// </summary>
    private void BuildAudioItems(IntPtr parent)
    {
        if (Rig == null) return;

        const int gainStep = 10;

        AddWired(parent, "Mute/Unmute Slice", () =>
            ToggleBool("Mute", () => Rig.SliceMute, v => Rig.SliceMute = v));

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
    }

    /// <summary>
    /// Build ATU (Antenna Tuner) control items.
    /// </summary>
    private void BuildATUItems(IntPtr parent)
    {
        if (Rig == null) return;

        AddWired(parent, "ATU On/Off", () =>
        {
            if (Rig == null) { SpeakNoRadio(); return; }
            Rig.FlexTunerOn = !Rig.FlexTunerOn;
            SpeakAfterMenuClose($"ATU {(Rig.FlexTunerOn ? "on" : "off")}");
        });

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
            AddWired(parent, "Toggle Diversity", () =>
            {
                if (Rig == null) { SpeakNoRadio(); return; }
                Rig.ToggleDiversity();
                // Read back the state after toggle
                SpeakAfterMenuClose("Diversity toggled");
            });
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

        AddWired(parent, "Squelch On/Off", () =>
            ToggleDSP("Squelch", () => Rig.Squelch, v => Rig.Squelch = v));
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

    #region Classic Menu Bar

    private IntPtr BuildClassicMenuBar()
    {
        var bar = CreateMenu();

        // === Actions ===
        var actions = AddPopup(bar, "&Actions");

        AddNotImplemented(actions, "List Operators");
        AddWired(actions, "Select Rig", () => _window.SelectRadioCallback?.Invoke());
        AddNotImplemented(actions, "Manage Profiles");
        AddNotImplemented(actions, "Local PTT On");
        AddNotImplemented(actions, "Connected Stations");
        AddNotImplemented(actions, "Flex Knob Config");
        AddNotImplemented(actions, "W2 Wattmeter");

        var loggingSub = AddSubmenu(actions, "Logging");
        AddNotImplemented(loggingSub, "Log Characteristics");
        AddNotImplemented(loggingSub, "Import Log");
        AddNotImplemented(loggingSub, "Export Log");
        AddNotImplemented(loggingSub, "LOTW Merge");

        AddNotImplemented(actions, "Export Setup");
        AddNotImplemented(actions, "Show Bands and Frequencies");

        AddSep(actions);

        if (_diversityAvailable)
            AddNotImplemented(actions, "Toggle Diversity");
        if (_escAvailable)
            AddNotImplemented(actions, "Open ESC Controls");
        AddNotImplemented(actions, "Feature Availability");

        AddSep(actions);

        AddNotImplemented(actions, "Manage CW Messages");
        AddNotImplemented(actions, "Change Key Mapping");
        AddNotImplemented(actions, "Restore Default Key Mapping");
        AddNotImplemented(actions, "Show All Messages");

        AddSep(actions);

        AddNotImplemented(actions, "Toggle Screen Saver");
        AddWired(actions, "Exit", () => _window.CloseShellCallback?.Invoke());

        // === ScreenFields (DSP Controls) ===
        var screenFields = AddPopup(bar, "&ScreenFields");
        if (Rig != null)
        {
            BuildDSPItems(screenFields);

            AddSep(screenFields);

            // Filter controls
            var filterSub = AddSubmenu(screenFields, "Filter Controls");
            BuildFilterItems(filterSub);

            AddSep(screenFields);

            // Diversity
            BuildDiversityItems(screenFields);
        }
        else
        {
            AddWired(screenFields, "Connect a radio to see DSP controls", SpeakNoRadio);
        }

        // === Operations ===
        var operations = AddPopup(bar, "&Operations");
        if (Rig != null)
        {
            // Audio
            var audioSub = AddSubmenu(operations, "Audio");
            BuildAudioItems(audioSub);

            // VOX / Transmission
            var txSub = AddSubmenu(operations, "Transmission");
            AddWired(txSub, "VOX On/Off", () =>
                ToggleDSP("VOX", () => Rig.Vox, v => Rig.Vox = v));

            // Antenna Tuner
            var atuSub = AddSubmenu(operations, "Antenna Tuner");
            BuildATUItems(atuSub);

            // Receiver
            var rxSub = AddSubmenu(operations, "Receiver");
            BuildReceiverItems(rxSub);
        }
        else
        {
            AddWired(operations, "Connect a radio to see operations", SpeakNoRadio);
        }

        // === Help (shared) ===
        BuildHelpPopup(bar);

        return bar;
    }

    #endregion

    #region Modern Menu Bar

    private IntPtr BuildModernMenuBar()
    {
        var bar = CreateMenu();

        // === Radio ===
        var radio = AddPopup(bar, "&Radio");
        AddWired(radio, "Connect to Radio", () => _window.SelectRadioCallback?.Invoke());
        AddNotImplemented(radio, "Manage SmartLink Accounts");
        AddNotImplemented(radio, "Operators");
        AddNotImplemented(radio, "Profiles");
        AddNotImplemented(radio, "Connected Stations");
        AddSep(radio);
        AddWired(radio, "Disconnect", () => _window.CloseRadioCallback?.Invoke());
        AddWired(radio, "Exit", () => _window.CloseShellCallback?.Invoke());

        // === Slice ===
        var slice = AddPopup(bar, "&Slice");

        if (Rig != null)
        {
            // Selection
            var selSub = AddSubmenu(slice, "Selection");
            for (int i = 0; i < Math.Min(Rig.TotalNumSlices, 8); i++)
            {
                int sliceNum = i;
                AddWired(selSub, $"Slice {i}", () =>
                {
                    if (Rig == null || !Rig.ValidVFO(sliceNum)) return;
                    Rig.RXVFO = sliceNum;
                    SpeakAfterMenuClose($"Slice {sliceNum} active");
                });
            }

            // Mode
            var modeSub = AddSubmenu(slice, "Mode");
            foreach (string modeName in RigCaps.ModeTable)
            {
                string m = modeName;
                AddWired(modeSub, m, () =>
                {
                    if (Rig == null) { SpeakNoRadio(); return; }
                    Rig.Mode = m;
                    SpeakAfterMenuClose($"Mode {m}");
                });
            }

            // Audio
            var audioSub = AddSubmenu(slice, "Audio");
            BuildAudioItems(audioSub);

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

            // Antenna
            var antSub = AddSubmenu(slice, "Antenna");
            BuildDiversityItems(antSub);

            // FM
            var fmSub = AddSubmenu(slice, "FM");
            AddWired(fmSub, "VOX On/Off", () =>
                ToggleDSP("VOX", () => Rig.Vox, v => Rig.Vox = v));
        }
        else
        {
            AddWired(slice, "Connect a radio first", SpeakNoRadio);
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

        // === Audio ===
        var audio = AddPopup(bar, "&Audio");
        if (Rig != null)
        {
            BuildAudioItems(audio);
        }
        else
        {
            AddWired(audio, "Connect a radio first", SpeakNoRadio);
        }

        // === Tools ===
        var tools = AddPopup(bar, "&Tools");
        AddNotImplemented(tools, "Command Finder");
        AddStub(tools, "Speak Status");
        AddStub(tools, "Status Dialog");
        AddNotImplemented(tools, "Station Lookup");
        AddSep(tools);
        AddWired(tools, "Enter Logging Mode", () => _window.EnterLoggingMode());
        AddWired(tools, "Switch to Classic UI", () => _window.ToggleUIMode());
        AddSep(tools);
        AddNotImplemented(tools, "Hotkey Editor");
        AddNotImplemented(tools, "Band Plans");
        AddNotImplemented(tools, "Feature Availability");

        // === Help (shared) ===
        BuildHelpPopup(bar);

        return bar;
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
        AddWired(mode, "Switch to Classic", () =>
        {
            _window.LastNonLogMode = MainWindow.UIMode.Classic;
            _window.ExitLoggingMode();
        });
        AddWired(mode, "Switch to Modern", () =>
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
        AddNotImplemented(help, "Help Page");
        AddNotImplemented(help, "Key Assignments");
        AddNotImplemented(help, "Key Assignments (Alphabetical)");
        AddNotImplemented(help, "Key Assignments (By Function)");
        AddNotImplemented(help, "Tracing");
        AddNotImplemented(help, "About");
    }

    #endregion

    #region Helpers

    /// <summary>Add a popup (dropdown) menu to the menu bar.</summary>
    private IntPtr AddPopup(IntPtr menuBar, string text)
    {
        var popup = CreatePopupMenu();
        AppendMenuW(menuBar, MF_POPUP, (UIntPtr)popup, text);
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
    /// Speak a message after a short delay so NVDA finishes announcing the
    /// focus change (back to main window) before we speak our feedback.
    /// Uses Task.Delay + Dispatcher.Invoke to fire reliably from WinForms WndProc context.
    /// </summary>
    private void SpeakAfterMenuClose(string message)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            _window.Dispatcher.Invoke(() =>
                Radios.ScreenReaderOutput.Speak(message, interrupt: true));
        });
    }

    /// <summary>Add a separator line.</summary>
    private void AddSep(IntPtr popup)
    {
        AppendMenuW(popup, MF_SEPARATOR, UIntPtr.Zero, null);
    }

    #endregion
}
