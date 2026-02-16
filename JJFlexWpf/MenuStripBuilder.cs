using System;
using System.Diagnostics;
using JJTrace;
using WinForms = System.Windows.Forms;

namespace JJFlexWpf;

/// <summary>
/// Builds all three WinForms menu sets for ShellForm: Classic, Modern, and Logging.
/// Replaces WPF MenuBuilder — native Win32 HMENU menus work correctly with JAWS/NVDA.
/// Sprint 12 Phase 12.2.
/// </summary>
public class MenuStripBuilder
{
    private readonly MainWindow _window;

    // === Shared ===
    public WinForms.ToolStripMenuItem HelpMenu { get; private set; } = null!;

    // === Classic Mode Top-Level Menus ===
    public WinForms.ToolStripMenuItem ClassicActionsMenu { get; private set; } = null!;
    public WinForms.ToolStripMenuItem ClassicScreenFieldsMenu { get; private set; } = null!;
    public WinForms.ToolStripMenuItem ClassicOperationsMenu { get; private set; } = null!;

    // === Modern Mode Top-Level Menus ===
    public WinForms.ToolStripMenuItem ModernRadioMenu { get; private set; } = null!;
    public WinForms.ToolStripMenuItem ModernSliceMenu { get; private set; } = null!;
    public WinForms.ToolStripMenuItem ModernFilterMenu { get; private set; } = null!;
    public WinForms.ToolStripMenuItem ModernAudioMenu { get; private set; } = null!;
    public WinForms.ToolStripMenuItem ModernToolsMenu { get; private set; } = null!;

    // === Logging Mode Top-Level Menus ===
    public WinForms.ToolStripMenuItem LoggingLogMenu { get; private set; } = null!;
    public WinForms.ToolStripMenuItem LoggingNavigateMenu { get; private set; } = null!;
    public WinForms.ToolStripMenuItem LoggingModeMenu { get; private set; } = null!;

    // === Feature-gated items ===
    public WinForms.ToolStripMenuItem? DiversityMenuItem { get; private set; }
    public WinForms.ToolStripMenuItem? EscMenuItem { get; private set; }

    public MenuStripBuilder(MainWindow window)
    {
        _window = window;
    }

    /// <summary>
    /// Build all three menu sets and populate the MenuStrip control.
    /// </summary>
    public void BuildAllMenus(WinForms.MenuStrip menuStrip)
    {
        Tracing.TraceLine("MenuStripBuilder.BuildAllMenus: starting", TraceLevel.Info);

        menuStrip.Items.Clear();

        BuildClassicMenus();
        BuildModernMenus();
        BuildLoggingMenus();
        BuildHelpMenu();

        menuStrip.Items.Add(ClassicActionsMenu);
        menuStrip.Items.Add(ClassicScreenFieldsMenu);
        menuStrip.Items.Add(ClassicOperationsMenu);

        menuStrip.Items.Add(ModernRadioMenu);
        menuStrip.Items.Add(ModernSliceMenu);
        menuStrip.Items.Add(ModernFilterMenu);
        menuStrip.Items.Add(ModernAudioMenu);
        menuStrip.Items.Add(ModernToolsMenu);

        menuStrip.Items.Add(LoggingLogMenu);
        menuStrip.Items.Add(LoggingNavigateMenu);
        menuStrip.Items.Add(LoggingModeMenu);

        menuStrip.Items.Add(HelpMenu);

        // Start with all hidden — ApplyUIMode will show the correct set
        SetClassicMenusVisible(false);
        SetModernMenusVisible(false);
        SetLoggingMenusVisible(false);

        Tracing.TraceLine("MenuStripBuilder.BuildAllMenus: complete", TraceLevel.Info);
    }

    #region Classic Menus

    private void BuildClassicMenus()
    {
        ClassicActionsMenu = CreateMenu("Actions");

        AddItem(ClassicActionsMenu, "List Operators", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Select Rig", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Manage Profiles", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Local PTT On", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Connected Stations", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Flex Knob Config", OnNotImplemented);
        AddItem(ClassicActionsMenu, "W2 Wattmeter", OnNotImplemented);

        var loggingSub = CreateSubmenu(ClassicActionsMenu, "Logging");
        AddItem(loggingSub, "Log Characteristics", OnNotImplemented);
        AddItem(loggingSub, "Import Log", OnNotImplemented);
        AddItem(loggingSub, "Export Log", OnNotImplemented);
        AddItem(loggingSub, "LOTW Merge", OnNotImplemented);

        AddItem(ClassicActionsMenu, "Export Setup", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Show Bands and Frequencies", OnNotImplemented);

        ClassicActionsMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());

        DiversityMenuItem = AddItem(ClassicActionsMenu, "Toggle Diversity", OnNotImplemented);
        DiversityMenuItem.Visible = false;
        EscMenuItem = AddItem(ClassicActionsMenu, "Open ESC Controls", OnNotImplemented);
        EscMenuItem.Visible = false;
        AddItem(ClassicActionsMenu, "Feature Availability", OnNotImplemented);

        ClassicActionsMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());

        AddItem(ClassicActionsMenu, "Manage CW Messages", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Change Key Mapping", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Restore Default Key Mapping", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Show All Messages", OnNotImplemented);

        ClassicActionsMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());

        AddItem(ClassicActionsMenu, "Toggle Screen Saver", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Exit", (_, _) => _window.CloseShellCallback?.Invoke());

        ClassicScreenFieldsMenu = CreateMenu("ScreenFields");
        AddStubItem(ClassicScreenFieldsMenu, "Connect a radio to see DSP controls");

        ClassicOperationsMenu = CreateMenu("Operations");
        AddStubItem(ClassicOperationsMenu, "Connect a radio to see operations");
    }

    #endregion

    #region Modern Menus

    private void BuildModernMenus()
    {
        ModernRadioMenu = CreateMenu("Radio");

        AddItem(ModernRadioMenu, "Connect to Radio", OnNotImplemented);
        AddItem(ModernRadioMenu, "Manage SmartLink Accounts", OnNotImplemented);
        AddItem(ModernRadioMenu, "Operators", OnNotImplemented);
        AddItem(ModernRadioMenu, "Profiles", OnNotImplemented);
        AddItem(ModernRadioMenu, "Connected Stations", OnNotImplemented);
        ModernRadioMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
        AddItem(ModernRadioMenu, "Disconnect", OnNotImplemented);
        AddItem(ModernRadioMenu, "Exit", (_, _) => _window.CloseShellCallback?.Invoke());

        ModernSliceMenu = CreateMenu("Slice");

        var selSub = CreateSubmenu(ModernSliceMenu, "Selection");
        AddStubItem(selSub, "Select Slice");
        AddStubItem(selSub, "Next Slice");
        AddStubItem(selSub, "Previous Slice");
        AddStubItem(selSub, "Set TX Slice");

        var modeSub = CreateSubmenu(ModernSliceMenu, "Mode");
        AddStubItem(modeSub, "CW");
        AddStubItem(modeSub, "USB");
        AddStubItem(modeSub, "LSB");
        AddStubItem(modeSub, "AM");
        AddStubItem(modeSub, "FM");
        AddStubItem(modeSub, "DIGU");
        AddStubItem(modeSub, "DIGL");

        var audioSub = CreateSubmenu(ModernSliceMenu, "Audio");
        AddItem(audioSub, "Mute/Unmute", OnNotImplemented);
        AddItem(audioSub, "Volume Up", OnNotImplemented);
        AddItem(audioSub, "Volume Down", OnNotImplemented);
        AddItem(audioSub, "Pan Left", OnNotImplemented);
        AddItem(audioSub, "Pan Center", OnNotImplemented);
        AddItem(audioSub, "Pan Right", OnNotImplemented);

        var tuningSub = CreateSubmenu(ModernSliceMenu, "Tuning");
        AddStubItem(tuningSub, "RIT On/Off");
        AddStubItem(tuningSub, "RIT Value");
        AddStubItem(tuningSub, "XIT On/Off");
        AddStubItem(tuningSub, "XIT Value");
        AddStubItem(tuningSub, "Step Size");

        var rxSub = CreateSubmenu(ModernSliceMenu, "Receiver");
        AddStubItem(rxSub, "AGC Mode");
        AddStubItem(rxSub, "AGC Threshold");
        AddStubItem(rxSub, "Squelch On/Off");
        AddStubItem(rxSub, "Squelch Level");
        AddStubItem(rxSub, "RF Gain");

        var dspSub = CreateSubmenu(ModernSliceMenu, "DSP");

        var nrSub = CreateSubmenu(dspSub, "Noise Reduction");
        AddItem(nrSub, "Neural NR (RNN)", OnNotImplemented);
        AddItem(nrSub, "Spectral NR (NRS)", OnNotImplemented);
        AddItem(nrSub, "Legacy NR", OnNotImplemented);

        var anfSub = CreateSubmenu(dspSub, "Auto Notch");
        AddItem(anfSub, "FFT Auto-Notch", OnNotImplemented);
        AddItem(anfSub, "Legacy Auto-Notch", OnNotImplemented);

        AddItem(dspSub, "Noise Blanker (NB)", OnNotImplemented);
        AddItem(dspSub, "Wideband NB (WNB)", OnNotImplemented);
        AddItem(dspSub, "Audio Peak Filter (APF)", OnNotImplemented);

        var antSub = CreateSubmenu(ModernSliceMenu, "Antenna");
        AddStubItem(antSub, "RX Antenna");
        AddStubItem(antSub, "TX Antenna");
        AddStubItem(antSub, "Diversity On/Off");

        var fmSub = CreateSubmenu(ModernSliceMenu, "FM");
        AddStubItem(fmSub, "Repeater Offset");
        AddStubItem(fmSub, "Pre-De-Emphasis");
        AddStubItem(fmSub, "Tone");

        ModernFilterMenu = CreateMenu("Filter");
        AddItem(ModernFilterMenu, "Narrow", OnNotImplemented);
        AddItem(ModernFilterMenu, "Widen", OnNotImplemented);
        AddItem(ModernFilterMenu, "Shift Low Edge", OnNotImplemented);
        AddItem(ModernFilterMenu, "Shift High Edge", OnNotImplemented);
        AddStubItem(ModernFilterMenu, "Presets");
        AddStubItem(ModernFilterMenu, "Reset Filter");

        ModernAudioMenu = CreateMenu("Audio");
        AddStubItem(ModernAudioMenu, "PC Audio Boost");
        AddStubItem(ModernAudioMenu, "Local Audio");
        AddStubItem(ModernAudioMenu, "Audio Test");
        AddStubItem(ModernAudioMenu, "Record/Playback");
        AddStubItem(ModernAudioMenu, "Route/DAX");

        ModernToolsMenu = CreateMenu("Tools");
        AddItem(ModernToolsMenu, "Command Finder", OnNotImplemented);
        AddStubItem(ModernToolsMenu, "Speak Status");
        AddStubItem(ModernToolsMenu, "Status Dialog");
        AddItem(ModernToolsMenu, "Station Lookup", OnNotImplemented);
        ModernToolsMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
        AddItem(ModernToolsMenu, "Enter Logging Mode", (_, _) => _window.EnterLoggingMode());
        AddItem(ModernToolsMenu, "Switch to Classic UI", (_, _) => _window.ToggleUIMode());
        ModernToolsMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
        AddItem(ModernToolsMenu, "Hotkey Editor", OnNotImplemented);
        AddItem(ModernToolsMenu, "Band Plans", OnNotImplemented);
        AddItem(ModernToolsMenu, "Feature Availability", OnNotImplemented);
    }

    #endregion

    #region Logging Menus

    private void BuildLoggingMenus()
    {
        LoggingLogMenu = CreateMenu("Log");
        AddItem(LoggingLogMenu, "New Entry", OnNotImplemented);
        AddItem(LoggingLogMenu, "Write Entry", OnNotImplemented);
        AddItem(LoggingLogMenu, "Search Log", OnNotImplemented);
        AddItem(LoggingLogMenu, "Full Log Form", OnNotImplemented);
        LoggingLogMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
        AddItem(LoggingLogMenu, "Log Characteristics", OnNotImplemented);
        AddItem(LoggingLogMenu, "Import Log", OnNotImplemented);
        AddItem(LoggingLogMenu, "Export Log", OnNotImplemented);
        AddItem(LoggingLogMenu, "LOTW Merge", OnNotImplemented);
        LoggingLogMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
        AddItem(LoggingLogMenu, "Log Statistics", OnNotImplemented);
        LoggingLogMenu.DropDownItems.Add(new WinForms.ToolStripSeparator());
        AddItem(LoggingLogMenu, "Reset Confirmations", OnNotImplemented);

        LoggingNavigateMenu = CreateMenu("Navigate");
        AddStubItem(LoggingNavigateMenu, "First Entry");
        AddStubItem(LoggingNavigateMenu, "Previous Entry");
        AddStubItem(LoggingNavigateMenu, "Next Entry");
        AddStubItem(LoggingNavigateMenu, "Last Entry");

        LoggingModeMenu = CreateMenu("Mode");
        AddItem(LoggingModeMenu, "Switch to Classic", (_, _) =>
        {
            _window.LastNonLogMode = MainWindow.UIMode.Classic;
            _window.ExitLoggingMode();
        });
        AddItem(LoggingModeMenu, "Switch to Modern", (_, _) =>
        {
            _window.LastNonLogMode = MainWindow.UIMode.Modern;
            _window.ExitLoggingMode();
        });
    }

    #endregion

    #region Help Menu

    private void BuildHelpMenu()
    {
        HelpMenu = CreateMenu("Help");
        AddItem(HelpMenu, "Help Page", OnNotImplemented);
        AddItem(HelpMenu, "Key Assignments", OnNotImplemented);
        AddItem(HelpMenu, "Key Assignments (Alphabetical)", OnNotImplemented);
        AddItem(HelpMenu, "Key Assignments (By Function)", OnNotImplemented);
        AddItem(HelpMenu, "Tracing", OnNotImplemented);
        AddItem(HelpMenu, "About", OnNotImplemented);
    }

    #endregion

    #region Mode Switching

    /// <summary>
    /// Apply UI mode — called by MainWindow.MenuModeCallback.
    /// </summary>
    public void ApplyUIMode(MainWindow.UIMode mode)
    {
        switch (mode)
        {
            case MainWindow.UIMode.Classic:
                SetClassicMenusVisible(true);
                SetModernMenusVisible(false);
                SetLoggingMenusVisible(false);
                break;
            case MainWindow.UIMode.Modern:
                SetClassicMenusVisible(false);
                SetModernMenusVisible(true);
                SetLoggingMenusVisible(false);
                break;
            case MainWindow.UIMode.Logging:
                SetClassicMenusVisible(false);
                SetModernMenusVisible(false);
                SetLoggingMenusVisible(true);
                break;
        }
    }

    public void SetClassicMenusVisible(bool visible)
    {
        ClassicActionsMenu.Visible = visible;
        ClassicScreenFieldsMenu.Visible = visible;
        ClassicOperationsMenu.Visible = visible;
    }

    public void SetModernMenusVisible(bool visible)
    {
        ModernRadioMenu.Visible = visible;
        ModernSliceMenu.Visible = visible;
        ModernFilterMenu.Visible = visible;
        ModernAudioMenu.Visible = visible;
        ModernToolsMenu.Visible = visible;
    }

    public void SetLoggingMenusVisible(bool visible)
    {
        LoggingLogMenu.Visible = visible;
        LoggingNavigateMenu.Visible = visible;
        LoggingModeMenu.Visible = visible;
    }

    #endregion

    #region Helpers

    private static WinForms.ToolStripMenuItem CreateMenu(string text)
    {
        var menu = new WinForms.ToolStripMenuItem(text);
        menu.AccessibleName = text;
        return menu;
    }

    private static WinForms.ToolStripMenuItem CreateSubmenu(WinForms.ToolStripMenuItem parent, string text)
    {
        var sub = new WinForms.ToolStripMenuItem(text);
        sub.AccessibleName = text;
        parent.DropDownItems.Add(sub);
        return sub;
    }

    private static WinForms.ToolStripMenuItem AddItem(WinForms.ToolStripMenuItem parent, string text, EventHandler handler)
    {
        var item = new WinForms.ToolStripMenuItem(text);
        item.AccessibleName = text;
        item.Click += handler;
        parent.DropDownItems.Add(item);
        return item;
    }

    private static WinForms.ToolStripMenuItem AddStubItem(WinForms.ToolStripMenuItem parent, string text)
    {
        var item = new WinForms.ToolStripMenuItem($"{text} - coming soon");
        item.AccessibleName = $"{text}, coming soon";
        item.Click += (_, _) =>
        {
            Radios.ScreenReaderOutput.Speak($"{text}, coming soon. Use Classic mode for full features.");
        };
        parent.DropDownItems.Add(item);
        return item;
    }

    private static void OnNotImplemented(object? sender, EventArgs e)
    {
        if (sender is WinForms.ToolStripMenuItem mi)
        {
            string name = mi.Text ?? "Unknown";
            Tracing.TraceLine($"Menu: {name} (not yet wired)", TraceLevel.Info);
            Radios.ScreenReaderOutput.Speak($"{name}, not yet connected to radio");
        }
    }

    #endregion
}
