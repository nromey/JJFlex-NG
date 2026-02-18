using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using JJTrace;

namespace JJFlexWpf;

/// <summary>
/// Native Win32 HMENU menu bar for ShellForm.
/// Uses P/Invoke to create real Win32 menus that screen readers (JAWS/NVDA)
/// navigate correctly — ROLE_SYSTEM_MENUBAR / ROLE_SYSTEM_MENUITEM with no
/// collapse/expand noise. Replaces the managed MenuStrip which announced
/// "collapsed"/"expanded" identically to the old WPF Menu.
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

        // === ScreenFields ===
        var screenFields = AddPopup(bar, "&ScreenFields");
        AddStub(screenFields, "Connect a radio to see DSP controls");

        // === Operations ===
        var operations = AddPopup(bar, "&Operations");
        AddStub(operations, "Connect a radio to see operations");

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

        var selSub = AddSubmenu(slice, "Selection");
        AddStub(selSub, "Select Slice");
        AddStub(selSub, "Next Slice");
        AddStub(selSub, "Previous Slice");
        AddStub(selSub, "Set TX Slice");

        var modeSub = AddSubmenu(slice, "Mode");
        AddStub(modeSub, "CW");
        AddStub(modeSub, "USB");
        AddStub(modeSub, "LSB");
        AddStub(modeSub, "AM");
        AddStub(modeSub, "FM");
        AddStub(modeSub, "DIGU");
        AddStub(modeSub, "DIGL");

        var audioSub = AddSubmenu(slice, "Audio");
        AddNotImplemented(audioSub, "Mute/Unmute");
        AddNotImplemented(audioSub, "Volume Up");
        AddNotImplemented(audioSub, "Volume Down");
        AddNotImplemented(audioSub, "Pan Left");
        AddNotImplemented(audioSub, "Pan Center");
        AddNotImplemented(audioSub, "Pan Right");

        var tuningSub = AddSubmenu(slice, "Tuning");
        AddStub(tuningSub, "RIT On/Off");
        AddStub(tuningSub, "RIT Value");
        AddStub(tuningSub, "XIT On/Off");
        AddStub(tuningSub, "XIT Value");
        AddStub(tuningSub, "Step Size");

        var rxSub = AddSubmenu(slice, "Receiver");
        AddStub(rxSub, "AGC Mode");
        AddStub(rxSub, "AGC Threshold");
        AddStub(rxSub, "Squelch On/Off");
        AddStub(rxSub, "Squelch Level");
        AddStub(rxSub, "RF Gain");

        var dspSub = AddSubmenu(slice, "DSP");

        var nrSub = AddSubmenu(dspSub, "Noise Reduction");
        AddNotImplemented(nrSub, "Neural NR (RNN)");
        AddNotImplemented(nrSub, "Spectral NR (NRS)");
        AddNotImplemented(nrSub, "Legacy NR");

        var anfSub = AddSubmenu(dspSub, "Auto Notch");
        AddNotImplemented(anfSub, "FFT Auto-Notch");
        AddNotImplemented(anfSub, "Legacy Auto-Notch");

        AddNotImplemented(dspSub, "Noise Blanker (NB)");
        AddNotImplemented(dspSub, "Wideband NB (WNB)");
        AddNotImplemented(dspSub, "Audio Peak Filter (APF)");

        var antSub = AddSubmenu(slice, "Antenna");
        AddStub(antSub, "RX Antenna");
        AddStub(antSub, "TX Antenna");
        AddStub(antSub, "Diversity On/Off");

        var fmSub = AddSubmenu(slice, "FM");
        AddStub(fmSub, "Repeater Offset");
        AddStub(fmSub, "Pre-De-Emphasis");
        AddStub(fmSub, "Tone");

        // === Filter ===
        var filter = AddPopup(bar, "&Filter");
        AddNotImplemented(filter, "Narrow");
        AddNotImplemented(filter, "Widen");
        AddNotImplemented(filter, "Shift Low Edge");
        AddNotImplemented(filter, "Shift High Edge");
        AddStub(filter, "Presets");
        AddStub(filter, "Reset Filter");

        // === Audio ===
        var audio = AddPopup(bar, "&Audio");
        AddStub(audio, "PC Audio Boost");
        AddStub(audio, "Local Audio");
        AddStub(audio, "Audio Test");
        AddStub(audio, "Record/Playback");
        AddStub(audio, "Route/DAX");

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
