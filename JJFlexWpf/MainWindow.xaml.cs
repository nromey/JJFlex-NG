using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using JJTrace;

namespace JJFlexWpf;

/// <summary>
/// Bridge interface for LogPanel commands — allows KeyCommands to call
/// LogPanel methods through WpfMainWindow without direct type dependency.
/// The main app implements this on its LogPanel and sets the LoggingLogPanel property.
/// </summary>
public interface ILogPanelCommands
{
    void FocusField(string fieldName);
    void NewEntry();
    bool WriteEntry();
}

/// <summary>
/// JJFlexRadio main window — WPF replacement for WinForms Form1.
/// Sprint 8: Full WPF conversion, code-behind pattern (not MVVM).
///
/// Layout:
///   DockPanel
///     ├─ Menu (Top)           — 3 mode-specific menu sets (Classic/Modern/Logging)
///     ├─ StatusBar (Bottom)   — Radio status fields (Power, Memories, Scan, Knob, LogFile)
///     └─ Grid (Fill)
///          Row 0: RadioControlsPanel  — Frequency, mode, tuner, TX/antenna buttons
///          Row 1: ContentArea         — Received/Sent text, rig fields display
///          Row 2: LoggingPanel        — Logging Mode overlay (collapsed by default)
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Flag to prevent re-entrant close attempts (mirrors Form1.Ending).
    /// </summary>
    private bool _isClosing;

    /// <summary>
    /// Menu builder — constructs and manages all 3 menu sets.
    /// </summary>
    private MenuBuilder _menuBuilder = null!;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    /// <summary>
    /// Main initialization sequence — replaces Form1_Load.
    /// Called once when the window first renders.
    ///
    /// Init order (matches Form1_Load):
    ///   1. Screen reader greeting
    ///   2. Status bar setup              — Phase 8.2
    ///   3. Config load (GetConfigInfo)   — Phase 8.1 (wired here)
    ///   4. UI Mode upgrade prompt        — Phase 8.5
    ///   5. Station name / window title   — Phase 8.1 (wired here)
    ///   6. Operator change handler       — Phase 8.1
    ///   7. Radio open                    — Phase 8.2+
    ///   8. Menu construction             — Phase 8.5
    ///   9. Logging panel build           — Phase 8.8
    ///  10. Apply UI mode                 — Phase 8.5
    /// </summary>
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Tracing.TraceLine("MainWindow_Loaded: starting init", System.Diagnostics.TraceLevel.Info);

        // 1. Screen reader greeting (matches Form1_Load line 206)
        Radios.ScreenReaderOutput.Speak("Welcome to JJ Flex");

        // 8. Menu construction (Phase 8.5)
        _menuBuilder = new MenuBuilder(this);
        _menuBuilder.BuildAllMenus(MainMenu);

        // 10. Apply UI mode — show correct menu set
        // Phase 8.6+: Read saved mode from CurrentOp.ActiveUIMode
        // For now, default to Modern mode
        ApplyUIMode(UIMode.Modern);

        // Update status
        StatusText.Text = "Ready — no radio connected";

        Tracing.TraceLine("MainWindow_Loaded: init complete", System.Diagnostics.TraceLevel.Info);
    }

    /// <summary>
    /// Clean shutdown — replaces Form1_FormClosing + FileExitToolStripMenuItem_Click.
    /// Prompts to save unsaved work, disconnects radio, cleans up resources.
    /// </summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
            return;

        _isClosing = true;
        Tracing.TraceLine("MainWindow_Closing: starting shutdown", System.Diagnostics.TraceLevel.Info);

        try
        {
            // Stop polling
            PollTimerEnabled = false;

            // Phase 8.8: Check logging panel for unsaved QSO
            // Phase 8.4+: Disconnect radio, close cluster, etc.

            Tracing.TraceLine("MainWindow_Closing: shutdown complete", System.Diagnostics.TraceLevel.Info);
        }
        catch (System.Exception ex)
        {
            Tracing.TraceLine($"MainWindow_Closing error: {ex.Message}", System.Diagnostics.TraceLevel.Error);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #region PollTimer — Phase 8.4

    /// <summary>
    /// Poll interval in milliseconds. Matches Form1.pollTimerInterval (100ms = 10 FPS).
    /// </summary>
    private const int PollTimerIntervalMs = 100;

    /// <summary>
    /// WPF DispatcherTimer replacing System.Windows.Forms.Timer.
    /// Fires on UI thread — no InvokeRequired checks needed.
    /// </summary>
    private DispatcherTimer? _pollTimer;

    /// <summary>
    /// List of RadioComboBox controls to poll each tick.
    /// Matches Form1.combos list pattern.
    /// </summary>
    private readonly List<RadioComboBox> _comboControls = new();

    /// <summary>
    /// List of controls to enable/disable based on radio power state.
    /// Matches Form1.enableDisableControls pattern.
    /// </summary>
    private readonly List<UIElement> _enableDisableControls = new();

    /// <summary>
    /// Track whether radio power is on — gates the update cycle.
    /// </summary>
    private bool _radioPowerOn;

    /// <summary>
    /// Previous SWR text for change detection.
    /// </summary>
    private string _oldSwr = "";

    /// <summary>
    /// Start or stop the poll timer.
    /// Matches Form1.PollTimer property pattern.
    /// </summary>
    public bool PollTimerEnabled
    {
        get => _pollTimer?.IsEnabled ?? false;
        set
        {
            if (value)
            {
                if (_pollTimer == null)
                {
                    _pollTimer = new DispatcherTimer(DispatcherPriority.Normal)
                    {
                        Interval = TimeSpan.FromMilliseconds(PollTimerIntervalMs)
                    };
                    _pollTimer.Tick += PollTimer_Tick;
                }
                _pollTimer.Start();
                Tracing.TraceLine("PollTimer: started", TraceLevel.Info);
            }
            else
            {
                if (_pollTimer != null)
                {
                    _pollTimer.Stop();
                    _pollTimer.Tick -= PollTimer_Tick;
                    _pollTimer = null;
                    Tracing.TraceLine("PollTimer: stopped", TraceLevel.Info);
                }
            }
        }
    }

    /// <summary>
    /// 100ms poll tick — calls UpdateStatus().
    /// Mirrors Form1.PollTimer_Tick.
    /// </summary>
    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        UpdateStatus();
    }

    /// <summary>
    /// Main status update — called every 100ms when radio is connected.
    /// Reads current rig state and updates all UI controls.
    ///
    /// Matches Form1.UpdateStatus() flow:
    /// 1. Check advanced feature menus (Diversity/ESC)
    /// 2. Gate on power status
    /// 3. Update frequency display
    /// 4. Update combo boxes (Mode, Tuner)
    /// 5. Update rig-specific fields (DSP/filters via RigFields.RigUpdate)
    /// 6. Update SWR display (if manual tuning)
    /// 7. Refresh status bar if changed
    ///
    /// Radio wiring connects in Phase 8.4+ when RigControl is available.
    /// </summary>
    public void UpdateStatus()
    {
        Tracing.TraceLine("UpdateStatus", TraceLevel.Verbose);

        if (_isClosing)
            return;

        // Phase 8.5+: UpdateAdvancedFeatureMenus() — check Diversity/ESC availability

        // Phase 8.4+: Check RigControl connection
        // if (RigControl == null || !RigControl.IsConnected) return;

        try
        {
            if (_radioPowerOn)
            {
                // Phase 8.4+: showFrequency() — update FreqOut display

                // Update all combo controls (Mode, Tuner, etc.)
                Tracing.TraceLine("UpdateStatus: doing combos", TraceLevel.Verbose);
                foreach (var combo in _comboControls)
                {
                    if (combo.IsEnabled)
                    {
                        combo.UpdateDisplay();
                    }
                }

                // Phase 8.4+: RigControl.RigFields.RigUpdate() — DSP/filter controls

                // Phase 8.4+: SWR meter update for manual tuner
            }

            // Update status bar if FreqOut (used as StatusBox) has changes
            if (FreqOut.Changed)
            {
                FreqOut.Display();
            }
        }
        catch (Exception ex)
        {
            if (!_radioPowerOn)
            {
                Tracing.TraceLine("UpdateStatus: power is off", TraceLevel.Error);
            }
            else
            {
                Tracing.TraceLine($"UpdateStatus error: {ex.Message}", TraceLevel.Error);
                // Phase 8.4+: powerNowOff() — emergency shutdown on critical error
            }
        }

        Tracing.TraceLine("UpdateStatus: done", TraceLevel.Verbose);
    }

    /// <summary>
    /// Enable or disable radio-dependent controls based on power state.
    /// Matches Form1.enableDisableWindowControls().
    /// </summary>
    public void EnableDisableWindowControls(bool enabled)
    {
        _radioPowerOn = enabled;
        foreach (var control in _enableDisableControls)
        {
            control.IsEnabled = enabled;
        }
        Tracing.TraceLine($"EnableDisableWindowControls: {enabled}", TraceLevel.Info);
    }

    #endregion

    #region UI Mode Management — Phase 8.5

    /// <summary>
    /// UI mode enum — mirrors globals.vb UIMode.
    /// </summary>
    public enum UIMode
    {
        Classic,
        Modern,
        Logging
    }

    /// <summary>
    /// Current active UI mode.
    /// </summary>
    public UIMode ActiveUIMode { get; private set; } = UIMode.Modern;

    /// <summary>
    /// Last non-logging mode (Classic or Modern). Restored when exiting Logging Mode.
    /// Matches globals.vb LastNonLogMode.
    /// </summary>
    public UIMode LastNonLogMode { get; set; } = UIMode.Modern;

    /// <summary>
    /// Apply the specified UI mode — show/hide menus and panels.
    /// Central dispatcher matching Form1.ApplyUIMode().
    /// </summary>
    public void ApplyUIMode(UIMode mode)
    {
        ActiveUIMode = mode;
        Tracing.TraceLine($"ApplyUIMode: {mode}", TraceLevel.Info);

        switch (mode)
        {
            case UIMode.Classic:
                ShowClassicUI();
                break;
            case UIMode.Modern:
                ShowModernUI();
                break;
            case UIMode.Logging:
                ShowLoggingUI();
                break;
        }
    }

    /// <summary>
    /// Show Classic mode: Classic menus, radio controls visible, logging hidden.
    /// </summary>
    private void ShowClassicUI()
    {
        _menuBuilder.SetClassicMenusVisible(true);
        _menuBuilder.SetModernMenusVisible(false);
        _menuBuilder.SetLoggingMenusVisible(false);

        RadioControlsPanel.Visibility = Visibility.Visible;
        SetTextAreasVisible(true);
        LoggingPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Show Modern mode: Modern menus, radio controls visible, logging hidden.
    /// </summary>
    private void ShowModernUI()
    {
        _menuBuilder.SetClassicMenusVisible(false);
        _menuBuilder.SetModernMenusVisible(true);
        _menuBuilder.SetLoggingMenusVisible(false);

        RadioControlsPanel.Visibility = Visibility.Visible;
        SetTextAreasVisible(true);
        LoggingPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Show Logging mode: Logging menus, radio controls hidden, log panel visible.
    /// </summary>
    private void ShowLoggingUI()
    {
        _menuBuilder.SetClassicMenusVisible(false);
        _menuBuilder.SetModernMenusVisible(false);
        _menuBuilder.SetLoggingMenusVisible(true);

        // Hide standard radio controls (tab stop off for accessibility)
        RadioControlsPanel.Visibility = Visibility.Collapsed;
        SetTextAreasVisible(false);

        // Show logging panel (Phase 8.8 will populate with LogEntryControl + RadioPane)
        LoggingPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Toggle between Classic and Modern modes.
    /// Matches Form1.ToggleUIMode().
    /// </summary>
    public void ToggleUIMode()
    {
        if (ActiveUIMode == UIMode.Logging)
        {
            // Phase 8.8: ExitLoggingMode() first
            return;
        }

        var newMode = ActiveUIMode == UIMode.Classic ? UIMode.Modern : UIMode.Classic;
        LastNonLogMode = newMode;
        ApplyUIMode(newMode);
        Radios.ScreenReaderOutput.Speak($"Switched to {newMode} mode");
    }

    /// <summary>
    /// Enter Logging Mode from either Classic or Modern.
    /// Matches Form1.EnterLoggingMode().
    ///
    /// In pure WPF, this is beautifully simple:
    /// - Set Visibility on the Grid rows (no ElementHost container issues)
    /// - Focus moves naturally to LogEntryControl.CallSignBox
    /// - No "unknown" announcement (no intermediate containers)
    /// - No focus trapping (Visibility.Collapsed removes from tab order)
    /// </summary>
    public void EnterLoggingMode()
    {
        if (ActiveUIMode == UIMode.Logging)
            return;

        LastNonLogMode = ActiveUIMode;
        ApplyUIMode(UIMode.Logging);

        // Update RadioPane with current radio state
        LoggingRadioPane.UpdateFromRadio();

        // Focus the CallSign field in LogEntryControl.
        // In pure WPF, this just works — no BeginInvoke, no ElementHost dance.
        LoggingLogEntry.FocusCallSign();

        Radios.ScreenReaderOutput.Speak("Entering Logging Mode, Call Sign");
    }

    /// <summary>
    /// Exit Logging Mode, returning to last non-logging mode.
    /// Matches Form1.ExitLoggingMode().
    ///
    /// The ElementHost bugs that Sprint 7 fought are GONE:
    /// - No Keyboard.ClearFocus() needed (no WPF focus retained in hidden host)
    /// - No two-step focus dance (move WinForms first, then hide)
    /// - Just Visibility.Collapsed → focus moves naturally to visible content
    /// </summary>
    public void ExitLoggingMode()
    {
        if (ActiveUIMode != UIMode.Logging)
            return;

        // Phase 8.8+: Check for unsaved QSO, save LogPanel state

        ApplyUIMode(LastNonLogMode);

        // Focus FreqOut display (the primary control in Classic/Modern modes)
        FreqOut.Focus();
        Keyboard.Focus(FreqOut);

        Radios.ScreenReaderOutput.Speak($"Returning to {LastNonLogMode} mode");
    }

    #endregion

    #region Keyboard Routing — Phase 8.6

    /// <summary>
    /// Delegate for routing keyboard commands to the VB.NET KeyCommands system.
    /// Set by ApplicationEvents.vb after creating the KeyCommands instance.
    /// Takes a WinForms Keys value, returns true if the key was consumed.
    /// </summary>
    public Func<System.Windows.Forms.Keys, bool>? DoCommandHandler { get; set; }

    /// <summary>
    /// Window-level PreviewKeyDown — intercepts ALL keys before child controls.
    /// Replaces Form1.ProcessCmdKey override.
    ///
    /// Priority order:
    /// 1. Hard-wired meta-commands (Ctrl+Shift+M/L for mode switching)
    /// 2. Scope-aware KeyCommands.DoCommand() via DoCommandHandler delegate
    /// 3. Pass through to focused control (WPF default behavior)
    ///
    /// This is THE fix for the ElementHost keyboard forwarding hack.
    /// In pure WPF, PreviewKeyDown on the Window sees every key, period.
    /// No more PreviewKeyDown→BeginInvoke→Form1 chain.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 1. Hard-wired meta-commands (always active, any mode)
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.M)
            {
                ToggleUIMode();
                e.Handled = true;
                return;
            }

            if (key == Key.L)
            {
                if (ActiveUIMode == UIMode.Logging)
                    ExitLoggingMode();
                else
                    EnterLoggingMode();
                e.Handled = true;
                return;
            }
        }

        // 2. Route through scope-aware KeyCommands registry
        if (DoCommandHandler != null)
        {
            var winFormsKey = WpfKeyConverter.ToWinFormsKeys(e);
            if (winFormsKey != System.Windows.Forms.Keys.None && DoCommandHandler(winFormsKey))
            {
                e.Handled = true;
                return;
            }
        }

        // 3. Fall through to focused control (default WPF behavior)
    }

    #endregion

    #region Radio Control Button Handlers — Phase 8.2

    /// <summary>
    /// Antenna Tune button — toggles FlexTunerOn.
    /// Wired to RigControl in Phase 8.4+.
    /// </summary>
    private void AntennaTuneButton_Click(object sender, RoutedEventArgs e)
    {
        Tracing.TraceLine("AntennaTuneButton_Click", System.Diagnostics.TraceLevel.Info);
        // Phase 8.4+: RigControl.FlexTunerOn = !RigControl.FlexTunerOn
    }

    /// <summary>
    /// Show SWR/tuner status on hover (matches WinForms Enter/Leave pattern).
    /// </summary>
    private void AntennaTuneButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Phase 8.4+: Update button text with SWR or tuner status
    }

    private void AntennaTuneButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Phase 8.4+: Restore button text to "Tune"
        AntennaTuneButton.Content = "Tune";
    }

    /// <summary>
    /// Transmit button — toggles PTT.
    /// Wired to RigControl in Phase 8.4+.
    /// </summary>
    private void TransmitButton_Click(object sender, RoutedEventArgs e)
    {
        Tracing.TraceLine("TransmitButton_Click", System.Diagnostics.TraceLevel.Info);
        // Phase 8.4+: toggleTransmit()
    }

    #endregion

    #region Text Area Support — Phase 8.3

    /// <summary>
    /// Window IDs matching Form1.WindowIDs enum for text routing.
    /// </summary>
    public enum WindowIDs
    {
        ReceiveDataOut,
        SendDataOut
    }

    /// <summary>
    /// Map a WindowID to its TextBox control.
    /// Matches Form1.TBIDToTB().
    /// </summary>
    private TextBox WindowIdToTextBox(WindowIDs id)
    {
        return id switch
        {
            WindowIDs.ReceiveDataOut => ReceivedTextBox,
            WindowIDs.SendDataOut => SentTextBox,
            _ => ReceivedTextBox
        };
    }

    /// <summary>
    /// Write text to a text area. Thread-safe via Dispatcher.
    /// Matches Form1.WriteText/WriteTextX pattern.
    /// </summary>
    /// <param name="id">Which text area to write to</param>
    /// <param name="text">Text to write or append</param>
    /// <param name="cursor">Cursor position:
    ///   -1 = preserve current position,
    ///    0 = move to end,
    ///   &gt;0 = set to specific position,
    ///   &lt;-1 = buffer limit (negative of max length, trims from start)</param>
    /// <param name="clearFirst">True to replace all text, false to append</param>
    public void WriteText(WindowIDs id, string text, int cursor = 0, bool clearFirst = false)
    {
        if (_isClosing)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => WriteText(id, text, cursor, clearFirst));
            return;
        }

        try
        {
            var tb = WindowIdToTextBox(id);
            WriteToTextBox(tb, text, cursor, clearFirst);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine($"WriteText error: {ex.Message}", System.Diagnostics.TraceLevel.Error);
        }
    }

    /// <summary>
    /// Core text writing logic — matches Form1.toTextbox().
    /// Handles append, replace, cursor positioning, and buffer limiting.
    /// </summary>
    private static void WriteToTextBox(TextBox tb, string text, int cursor, bool clearFirst)
    {
        string finalText;
        if (clearFirst)
        {
            finalText = text;
        }
        else
        {
            finalText = tb.Text + text; // Append
        }

        // Handle cursor positioning
        if (cursor == -1)
        {
            // Preserve current position
            cursor = tb.SelectionStart;
        }
        else if (cursor < -1)
        {
            // Buffer limit: negative value = max length
            int maxLen = -cursor;
            if (finalText.Length > maxLen)
            {
                finalText = finalText.Substring(finalText.Length - maxLen);
            }
            cursor = finalText.Length;
        }
        else if (cursor == 0)
        {
            // Move to end
            cursor = finalText.Length;
        }

        tb.Text = finalText;

        // Set cursor and scroll into view
        if (cursor <= tb.Text.Length)
        {
            tb.SelectionStart = cursor;
            tb.SelectionLength = 0;
        }

        // Scroll to cursor (WPF equivalent of ScrollToCaret)
        tb.ScrollToLine(tb.GetLineIndexFromCharacterIndex(
            Math.Min(tb.SelectionStart, Math.Max(0, tb.Text.Length - 1))));
    }

    /// <summary>
    /// Set visibility of the text areas (hidden in Logging Mode).
    /// Matches Form1 show/hide pattern for ReceivedTextBox + SentTextBox.
    /// </summary>
    public void SetTextAreasVisible(bool visible)
    {
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        ReceiveLabel.Visibility = vis;
        ReceivedTextBox.Visibility = vis;
        SendLabel.Visibility = vis;
        SentTextBox.Visibility = vis;

        // Tab stop matches visibility
        ReceivedTextBox.IsTabStop = visible;
        SentTextBox.IsTabStop = visible;
    }

    #endregion

    #region Text Area Event Handlers — Phase 8.3

    /// <summary>
    /// ReceivedTextBox keyboard handler — forwards function keys and modified keys.
    /// Clipboard operations (Ctrl+C, Ctrl+X) handled naturally by WPF TextBox.
    /// </summary>
    private void ReceivedTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Forward function keys to keyboard routing (Phase 8.6)
        if (e.Key >= Key.F1 && e.Key <= Key.F24)
        {
            // Phase 8.6: Commands.DoCommand(e)
            e.Handled = true;
        }
    }

    /// <summary>
    /// SentTextBox keyboard handler — handles CW shortcuts and function keys.
    /// Matches Form1.SentTextBox_KeyDown.
    /// </summary>
    private void SentTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Forward function keys to keyboard routing (Phase 8.6)
        if (e.Key >= Key.F1 && e.Key <= Key.F24)
        {
            // Phase 8.6: Commands.DoCommand(e)
            e.Handled = true;
        }

        // Phase 8.4+: Ctrl+Enter sends CW, other shortcuts
    }

    /// <summary>
    /// SentTextBox text input handler — for CW character transmission.
    /// Matches Form1.SentTextBox_KeyPress for direct CW send.
    /// </summary>
    private void SentTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Phase 8.4+: Route typed characters to CW transmit
    }

    #endregion

    #region Form1 Compatibility — Phase 9.1

    /// <summary>
    /// Display the key commands help dialog.
    /// Matches Form1.DisplayHelp().
    /// </summary>
    public void DisplayHelp()
    {
        // Phase 9.5+: ShowHelp dialog will be called directly here
        Tracing.TraceLine("MainWindow.DisplayHelp: stub — wiring in Phase 9.5", TraceLevel.Info);
    }

    /// <summary>
    /// Bring the main window to front and focus the frequency display.
    /// Matches Form1.gotoHome().
    /// </summary>
    public void gotoHome()
    {
        Activate();
        FreqOut.Focus();
        Keyboard.Focus(FreqOut);
    }

    /// <summary>
    /// Rebuild the Operations menu.
    /// Matches Form1.SetupOperationsMenu().
    /// </summary>
    public void SetupOperationsMenu()
    {
        // Phase 9.5+: Rebuild Operations menu via MenuBuilder
        Tracing.TraceLine("MainWindow.SetupOperationsMenu: stub — wiring in Phase 9.5", TraceLevel.Info);
    }

    /// <summary>
    /// Handle "Log Contact" result from Station Lookup.
    /// Matches Form1.HandleLogContactResult().
    /// </summary>
    public void HandleLogContactResult()
    {
        // Phase 9.5+: Check LookupStation.WantsLogContact, enter Logging Mode, pre-fill
        Tracing.TraceLine("MainWindow.HandleLogContactResult: stub — wiring in Phase 9.5", TraceLevel.Info);
    }

    /// <summary>
    /// Emergency power-off — disconnect and clean up.
    /// Matches Form1.powerNowOff().
    /// </summary>
    public void powerNowOff()
    {
        // Phase 9.5+: Full power-off sequence (remove handlers, clear window, update status)
        Tracing.TraceLine("MainWindow.powerNowOff: stub — wiring in Phase 9.5", TraceLevel.Info);
    }

    /// <summary>
    /// Toggle focus between Log pane and Radio pane in Logging Mode.
    /// Matches Form1.ToggleLoggingPaneFocusForHotkey().
    /// </summary>
    public void ToggleLoggingPaneFocusForHotkey()
    {
        // Phase 9.5+: ToggleLoggingPaneFocus()
        Tracing.TraceLine("MainWindow.ToggleLoggingPaneFocusForHotkey: stub — wiring in Phase 9.5", TraceLevel.Info);
    }

    /// <summary>
    /// Show log characteristics in Logging Mode.
    /// Matches Form1.LogCharacteristicsForHotkey().
    /// </summary>
    public void LogCharacteristicsForHotkey()
    {
        // Phase 9.5+: LogCharacteristicsMenuItem_Click(Nothing, EventArgs.Empty)
        Tracing.TraceLine("MainWindow.LogCharacteristicsForHotkey: stub — wiring in Phase 9.5", TraceLevel.Info);
    }

    /// <summary>
    /// Open full log entry form in Logging Mode.
    /// Matches Form1.OpenFullLogEntryForHotkey().
    /// </summary>
    public void OpenFullLogEntryForHotkey()
    {
        // Phase 9.5+: OpenFullLogEntry()
        Tracing.TraceLine("MainWindow.OpenFullLogEntryForHotkey: stub — wiring in Phase 9.5", TraceLevel.Info);
    }

    /// <summary>
    /// LogPanel bridge for KeyCommands access.
    /// Set by the main app when the logging panel is created.
    /// Phase 9.5: Move LogPanel creation to MainWindow.
    /// </summary>
    public ILogPanelCommands? LoggingLogPanel { get; set; }

    /// <summary>
    /// WinForms-compatible Visible property.
    /// Maps to WPF Visibility for KeyCommands.vb compatibility.
    /// </summary>
    public bool Visible
    {
        get => Visibility == Visibility.Visible;
        set => Visibility = value ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>
    /// WinForms-compatible BringToFront.
    /// Maps to WPF Activate() for KeyCommands.vb compatibility.
    /// </summary>
    public void BringToFront()
    {
        Activate();
    }

    #endregion
}
