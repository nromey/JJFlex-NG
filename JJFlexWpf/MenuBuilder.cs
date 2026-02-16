using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JJTrace;

namespace JJFlexWpf;

/// <summary>
/// Builds all three menu sets for MainWindow: Classic, Modern, and Logging.
/// Replaces Form1.Designer.vb MenuStrip1 + Form1.vb BuildModernMenus()/BuildLoggingMenus().
///
/// Design:
/// - Each menu set is a collection of top-level MenuItems
/// - Mode switching shows/hides top-level items via Visibility
/// - Help menu is shared across all modes (always visible)
/// - Stub items ("coming soon") remain enabled for screen reader access
///
/// Sprint 8 Phase 8.5.
/// </summary>
public class MenuBuilder
{
    private readonly MainWindow _window;

    // === Shared ===
    public MenuItem HelpMenu { get; private set; } = null!;

    // === Classic Mode Top-Level Menus ===
    public MenuItem ClassicActionsMenu { get; private set; } = null!;
    public MenuItem ClassicScreenFieldsMenu { get; private set; } = null!;
    public MenuItem ClassicOperationsMenu { get; private set; } = null!;

    // === Modern Mode Top-Level Menus ===
    public MenuItem ModernRadioMenu { get; private set; } = null!;
    public MenuItem ModernSliceMenu { get; private set; } = null!;
    public MenuItem ModernFilterMenu { get; private set; } = null!;
    public MenuItem ModernAudioMenu { get; private set; } = null!;
    public MenuItem ModernToolsMenu { get; private set; } = null!;

    // === Logging Mode Top-Level Menus ===
    public MenuItem LoggingLogMenu { get; private set; } = null!;
    public MenuItem LoggingNavigateMenu { get; private set; } = null!;
    public MenuItem LoggingModeMenu { get; private set; } = null!;

    // === Feature-gated items (updated by UpdateAdvancedFeatureMenus) ===
    public MenuItem? DiversityMenuItem { get; private set; }
    public MenuItem? EscMenuItem { get; private set; }

    public MenuBuilder(MainWindow window)
    {
        _window = window;
    }

    /// <summary>
    /// Build all three menu sets and populate the MainMenu control.
    /// Call once during MainWindow_Loaded.
    /// </summary>
    public void BuildAllMenus(Menu mainMenu)
    {
        Tracing.TraceLine("MenuBuilder.BuildAllMenus: starting", TraceLevel.Info);

        mainMenu.Items.Clear();

        // Build each menu set
        BuildClassicMenus();
        BuildModernMenus();
        BuildLoggingMenus();
        BuildHelpMenu();

        // Add all to the Menu control (visibility toggled per mode)
        mainMenu.Items.Add(ClassicActionsMenu);
        mainMenu.Items.Add(ClassicScreenFieldsMenu);
        mainMenu.Items.Add(ClassicOperationsMenu);

        mainMenu.Items.Add(ModernRadioMenu);
        mainMenu.Items.Add(ModernSliceMenu);
        mainMenu.Items.Add(ModernFilterMenu);
        mainMenu.Items.Add(ModernAudioMenu);
        mainMenu.Items.Add(ModernToolsMenu);

        mainMenu.Items.Add(LoggingLogMenu);
        mainMenu.Items.Add(LoggingNavigateMenu);
        mainMenu.Items.Add(LoggingModeMenu);

        // Help is always last and always visible
        mainMenu.Items.Add(HelpMenu);

        // Start with all hidden — ApplyUIMode will show the correct set
        SetClassicMenusVisible(false);
        SetModernMenusVisible(false);
        SetLoggingMenusVisible(false);

        Tracing.TraceLine("MenuBuilder.BuildAllMenus: complete", TraceLevel.Info);
    }

    #region Classic Menus

    private void BuildClassicMenus()
    {
        // === ACTIONS ===
        ClassicActionsMenu = CreateMenu("Actions");

        AddItem(ClassicActionsMenu, "List Operators", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Select Rig", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Manage Profiles", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Local PTT On", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Connected Stations", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Flex Knob Config", OnNotImplemented);
        AddItem(ClassicActionsMenu, "W2 Wattmeter", OnNotImplemented);

        // Logging submenu
        var loggingSub = CreateSubmenu(ClassicActionsMenu, "Logging");
        AddItem(loggingSub, "Log Characteristics", OnNotImplemented);
        AddItem(loggingSub, "Import Log", OnNotImplemented);
        AddItem(loggingSub, "Export Log", OnNotImplemented);
        AddItem(loggingSub, "LOTW Merge", OnNotImplemented);

        AddItem(ClassicActionsMenu, "Export Setup", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Show Bands and Frequencies", OnNotImplemented);

        ClassicActionsMenu.Items.Add(new Separator());

        DiversityMenuItem = AddItem(ClassicActionsMenu, "Toggle Diversity", OnNotImplemented);
        DiversityMenuItem.Visibility = Visibility.Collapsed; // Feature-gated
        EscMenuItem = AddItem(ClassicActionsMenu, "Open ESC Controls", OnNotImplemented);
        EscMenuItem.Visibility = Visibility.Collapsed; // Feature-gated
        AddItem(ClassicActionsMenu, "Feature Availability", OnNotImplemented);

        ClassicActionsMenu.Items.Add(new Separator());

        AddItem(ClassicActionsMenu, "Manage CW Messages", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Change Key Mapping", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Restore Default Key Mapping", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Show All Messages", OnNotImplemented);

        ClassicActionsMenu.Items.Add(new Separator());

        AddItem(ClassicActionsMenu, "Toggle Screen Saver", OnNotImplemented);
        AddItem(ClassicActionsMenu, "Exit", (_, _) => _window.CloseShellCallback?.Invoke());

        // === SCREENFIELDS ===
        ClassicScreenFieldsMenu = CreateMenu("ScreenFields");
        // Dynamic content — populated when radio connects (Phase 8.7+)
        AddStubItem(ClassicScreenFieldsMenu, "Connect a radio to see DSP controls");

        // === OPERATIONS ===
        ClassicOperationsMenu = CreateMenu("Operations");
        // Dynamic content — populated from KeyCommands (Phase 8.6+)
        AddStubItem(ClassicOperationsMenu, "Connect a radio to see operations");
    }

    #endregion

    #region Modern Menus

    private void BuildModernMenus()
    {
        // === RADIO ===
        ModernRadioMenu = CreateMenu("Radio");

        AddItem(ModernRadioMenu, "Connect to Radio", OnNotImplemented);
        AddItem(ModernRadioMenu, "Manage SmartLink Accounts", OnNotImplemented);
        AddItem(ModernRadioMenu, "Operators", OnNotImplemented);
        AddItem(ModernRadioMenu, "Profiles", OnNotImplemented);
        AddItem(ModernRadioMenu, "Connected Stations", OnNotImplemented);
        ModernRadioMenu.Items.Add(new Separator());
        AddItem(ModernRadioMenu, "Disconnect", OnNotImplemented);
        AddItem(ModernRadioMenu, "Exit", (_, _) => _window.CloseShellCallback?.Invoke());

        // === SLICE ===
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

        // === FILTER ===
        ModernFilterMenu = CreateMenu("Filter");
        AddItem(ModernFilterMenu, "Narrow", OnNotImplemented);
        AddItem(ModernFilterMenu, "Widen", OnNotImplemented);
        AddItem(ModernFilterMenu, "Shift Low Edge", OnNotImplemented);
        AddItem(ModernFilterMenu, "Shift High Edge", OnNotImplemented);
        AddStubItem(ModernFilterMenu, "Presets");
        AddStubItem(ModernFilterMenu, "Reset Filter");

        // === AUDIO ===
        ModernAudioMenu = CreateMenu("Audio");
        AddStubItem(ModernAudioMenu, "PC Audio Boost");
        AddStubItem(ModernAudioMenu, "Local Audio");
        AddStubItem(ModernAudioMenu, "Audio Test");
        AddStubItem(ModernAudioMenu, "Record/Playback");
        AddStubItem(ModernAudioMenu, "Route/DAX");

        // === TOOLS ===
        ModernToolsMenu = CreateMenu("Tools");
        AddItem(ModernToolsMenu, "Command Finder", OnNotImplemented);
        AddStubItem(ModernToolsMenu, "Speak Status");
        AddStubItem(ModernToolsMenu, "Status Dialog");
        AddItem(ModernToolsMenu, "Station Lookup", OnNotImplemented);
        ModernToolsMenu.Items.Add(new Separator());
        AddItem(ModernToolsMenu, "Enter Logging Mode", (_, _) =>
        {
            // Phase 8.8: _window.EnterLoggingMode()
            Radios.ScreenReaderOutput.Speak("Logging mode not yet available");
        });
        AddItem(ModernToolsMenu, "Switch to Classic UI", (_, _) =>
        {
            // Phase 8.6: _window.ToggleUIMode()
            Radios.ScreenReaderOutput.Speak("Mode switching not yet available");
        });
        ModernToolsMenu.Items.Add(new Separator());
        AddItem(ModernToolsMenu, "Hotkey Editor", OnNotImplemented);
        AddItem(ModernToolsMenu, "Band Plans", OnNotImplemented);
        AddItem(ModernToolsMenu, "Feature Availability", OnNotImplemented);
    }

    #endregion

    #region Logging Menus

    private void BuildLoggingMenus()
    {
        // === LOG ===
        LoggingLogMenu = CreateMenu("Log");
        AddItem(LoggingLogMenu, "New Entry", OnNotImplemented);
        AddItem(LoggingLogMenu, "Write Entry", OnNotImplemented);
        AddItem(LoggingLogMenu, "Search Log", OnNotImplemented);
        AddItem(LoggingLogMenu, "Full Log Form", OnNotImplemented);
        LoggingLogMenu.Items.Add(new Separator());
        AddItem(LoggingLogMenu, "Log Characteristics", OnNotImplemented);
        AddItem(LoggingLogMenu, "Import Log", OnNotImplemented);
        AddItem(LoggingLogMenu, "Export Log", OnNotImplemented);
        AddItem(LoggingLogMenu, "LOTW Merge", OnNotImplemented);
        LoggingLogMenu.Items.Add(new Separator());
        AddItem(LoggingLogMenu, "Log Statistics", OnNotImplemented);
        LoggingLogMenu.Items.Add(new Separator());
        AddItem(LoggingLogMenu, "Reset Confirmations", OnNotImplemented);

        // === NAVIGATE ===
        LoggingNavigateMenu = CreateMenu("Navigate");
        AddStubItem(LoggingNavigateMenu, "First Entry");
        AddStubItem(LoggingNavigateMenu, "Previous Entry");
        AddStubItem(LoggingNavigateMenu, "Next Entry");
        AddStubItem(LoggingNavigateMenu, "Last Entry");

        // === MODE ===
        LoggingModeMenu = CreateMenu("Mode");
        AddItem(LoggingModeMenu, "Switch to Classic", (_, _) =>
        {
            // Phase 8.8: _window.ExitLoggingMode() → Classic
            Radios.ScreenReaderOutput.Speak("Mode switching not yet available");
        });
        AddItem(LoggingModeMenu, "Switch to Modern", (_, _) =>
        {
            // Phase 8.8: _window.ExitLoggingMode() → Modern
            Radios.ScreenReaderOutput.Speak("Mode switching not yet available");
        });
    }

    #endregion

    #region Help Menu (shared)

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
    /// Show Classic menus, hide Modern and Logging.
    /// </summary>
    public void SetClassicMenusVisible(bool visible)
    {
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        ClassicActionsMenu.Visibility = vis;
        ClassicScreenFieldsMenu.Visibility = vis;
        ClassicOperationsMenu.Visibility = vis;
    }

    /// <summary>
    /// Show Modern menus, hide Classic and Logging.
    /// </summary>
    public void SetModernMenusVisible(bool visible)
    {
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        ModernRadioMenu.Visibility = vis;
        ModernSliceMenu.Visibility = vis;
        ModernFilterMenu.Visibility = vis;
        ModernAudioMenu.Visibility = vis;
        ModernToolsMenu.Visibility = vis;
    }

    /// <summary>
    /// Show Logging menus, hide Classic and Modern.
    /// </summary>
    public void SetLoggingMenusVisible(bool visible)
    {
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        LoggingLogMenu.Visibility = vis;
        LoggingNavigateMenu.Visibility = vis;
        LoggingModeMenu.Visibility = vis;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Create a top-level menu item.
    /// </summary>
    private static MenuItem CreateMenu(string header)
    {
        var menu = new MenuItem { Header = header };
        AutomationProperties.SetName(menu, header);
        return menu;
    }

    /// <summary>
    /// Create a submenu (folder) under a parent.
    /// </summary>
    private static MenuItem CreateSubmenu(MenuItem parent, string header)
    {
        var sub = new MenuItem { Header = header };
        AutomationProperties.SetName(sub, header);
        parent.Items.Add(sub);
        return sub;
    }

    /// <summary>
    /// Add a menu item with a click handler.
    /// </summary>
    private static MenuItem AddItem(MenuItem parent, string header, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header };
        AutomationProperties.SetName(item, header);
        item.Click += handler;
        parent.Items.Add(item);
        return item;
    }

    /// <summary>
    /// Add a stub item ("coming soon") that remains enabled for screen reader access.
    /// Matches Form1.AddModernStubItem pattern.
    /// </summary>
    private static MenuItem AddStubItem(MenuItem parent, string header)
    {
        var item = new MenuItem { Header = $"{header} - coming soon" };
        AutomationProperties.SetName(item, $"{header}, coming soon");
        AutomationProperties.SetHelpText(item, "Coming soon. Use Classic mode for full features.");
        item.Click += (_, _) =>
        {
            Radios.ScreenReaderOutput.Speak($"{header}, coming soon. Use Classic mode for full features.");
        };
        parent.Items.Add(item);
        return item;
    }

    /// <summary>
    /// Placeholder handler for menu items not yet wired to rig commands.
    /// Phase 8.4+ will replace these with real handlers.
    /// </summary>
    private static void OnNotImplemented(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi)
        {
            string name = mi.Header?.ToString() ?? "Unknown";
            Tracing.TraceLine($"Menu: {name} (not yet wired)", TraceLevel.Info);
            Radios.ScreenReaderOutput.Speak($"{name}, not yet connected to radio");
        }
    }

    #endregion
}
