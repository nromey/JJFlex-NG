using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using JJTrace;
using Radios;

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
    /// Clean shutdown — runs VB-side exit sequence via AppExitCallback.
    /// If exit is cancelled (unsaved QSO), the close is cancelled.
    /// </summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
            return;

        Tracing.TraceLine("MainWindow_Closing: starting shutdown", System.Diagnostics.TraceLevel.Info);

        // Run VB-side exit sequence (prompts, cleanup, radio close)
        if (AppExitCallback != null && !AppExitCallback())
        {
            // Exit was cancelled (e.g., unsaved QSO)
            e.Cancel = true;
            Tracing.TraceLine("MainWindow_Closing: exit cancelled by user", System.Diagnostics.TraceLevel.Info);
            return;
        }

        _isClosing = true;

        try
        {
            // Stop polling and unwire events (may already be done by ExitApplication)
            UnwireRadioEvents();

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
    /// Matches Form1.UpdateStatus() flow.
    /// </summary>
    public void UpdateStatus()
    {
        Tracing.TraceLine("UpdateStatus", TraceLevel.Verbose);

        if (_isClosing)
            return;

        if (RigControl == null || !RigControl.IsConnected)
            return;

        try
        {
            if (_radioPowerOn)
            {
                // Update frequency display
                ShowFrequency();

                // Update all combo controls (Mode, Tuner, etc.)
                Tracing.TraceLine("UpdateStatus: doing combos", TraceLevel.Verbose);
                foreach (var combo in _comboControls)
                {
                    if (combo.IsEnabled)
                    {
                        combo.UpdateDisplay();
                    }
                }

                // Update rig-dependent fields (DSP controls via WpfFilterAdapter)
                if (RigControl.RigFields != null)
                {
                    Tracing.TraceLine("UpdateStatus: doing RigFields", TraceLevel.Verbose);
                    RigControl.RigFields.RigUpdate?.Invoke();
                }

                // SWR update during manual tuning
                if (OpenParms?.GetSWRText != null &&
                    RigControl.FlexTunerOn &&
                    RigControl.FlexTunerType == FlexBase.FlexTunerTypes.manual)
                {
                    string swrText = OpenParms.GetSWRText();
                    if (swrText != _oldSwr)
                    {
                        _oldSwr = swrText;
                        SetButtonText(AntennaTuneButton, _oldSwr);
                    }
                }
            }

            // Update status bar if FreqOut has changes
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
                PowerNowOffInternal();
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
    /// Toggle Logging Mode on/off.
    /// Matches Form1.ToggleLoggingMode().
    /// </summary>
    public void ToggleLoggingMode()
    {
        if (ActiveUIMode == UIMode.Logging)
            ExitLoggingMode();
        else
            EnterLoggingMode();
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

    #region Radio Wiring — Phase 11.6

    /// <summary>
    /// The active radio control instance. Set by VB-side openTheRadio().
    /// </summary>
    public FlexBase? RigControl { get; set; }

    /// <summary>
    /// Current radio's open parameters. Set by VB-side, used for SWR text and frequency formatting.
    /// </summary>
    public FlexBase.OpenParms? OpenParms { get; set; }

    /// <summary>
    /// Callback to close the radio from VB-side (CloseTheRadio).
    /// Set by globals module since it involves VB module state.
    /// </summary>
    public Action? CloseRadioCallback { get; set; }

    /// <summary>
    /// Callback for VB-side exit sequence. Returns true to proceed, false to cancel.
    /// Set by ApplicationEvents.vb, called from MainWindow_Closing.
    /// </summary>
    public Func<bool>? AppExitCallback { get; set; }

    /// <summary>
    /// Callback to select/connect to a radio. Used by menu "Connect to Radio" item.
    /// Set by ApplicationEvents.vb, routes to globals.SelectRadio().
    /// </summary>
    public Action? SelectRadioCallback { get; set; }

    /// <summary>
    /// Callback for VB-side power-on tasks (knob setup, tracing).
    /// </summary>
    public Action? PowerOnCallback { get; set; }

    /// <summary>
    /// Antenna tune button base text, matching Form1 pattern.
    /// </summary>
    private const string AntennaTuneButtonBaseText = "Ant Tune";

    private string AntennaTuneButtonText
    {
        get
        {
            var text = AntennaTuneButtonBaseText;
            if (RigControl != null && RigControl.FlexTunerUsingMemoryNow)
                text += " mem";
            return text;
        }
    }

    /// <summary>
    /// Wire MainWindow event handlers to RigControl.
    /// Called by VB-side openTheRadio() BEFORE RigControl.Start().
    /// </summary>
    public void WireRadioEvents()
    {
        if (RigControl == null) return;
        RigControl.PowerStatus += PowerStatusHandler;
        RigControl.NoSliceError += NoSliceErrorHandler;
        RigControl.FeatureLicenseChanged += FeatureLicenseChangedHandler;
    }

    /// <summary>
    /// Post-Start radio setup. Called by VB-side after RigControl.Start() succeeds.
    /// Replaces Form1's post-start wiring (setupBoxes, menus, poll timer).
    /// </summary>
    public void OnRadioStarted()
    {
        Tracing.TraceLine("MainWindow.OnRadioStarted", TraceLevel.Info);

        SetupBoxes();

        // Wire memory dialog delegate
        if (RigControl != null)
        {
            RigControl.ShowMemoriesDialog = ShowMemoriesDialog;
        }

        // Disable controls initially — PowerNowOn enables them when radio powers on
        EnableDisableWindowControls(false);

        // Start polling
        PollTimerEnabled = true;

        StatusText.Text = "Radio connected — waiting for power on";
    }

    /// <summary>
    /// Unwire event handlers when closing the radio.
    /// Called by VB-side CloseTheRadio().
    /// </summary>
    public void UnwireRadioEvents()
    {
        PollTimerEnabled = false;
        _radioPowerOn = false;

        if (RigControl != null)
        {
            RigControl.PowerStatus -= PowerStatusHandler;
            RigControl.NoSliceError -= NoSliceErrorHandler;
            RigControl.FeatureLicenseChanged -= FeatureLicenseChangedHandler;
            RigControl.TransmitChange -= TransmitChangeHandler;
            RigControl.FlexAntTunerStartStop -= FlexAntTuneStartStopHandler;
            RigControl.ConnectedEvent -= ConnectedEventHandler;
        }

        FreqOut.Clear();
        _comboControls.Clear();
        _enableDisableControls.Clear();

        StatusText.Text = "Ready — no radio connected";
    }

    /// <summary>
    /// Set up combo controls and enable/disable lists after radio Start().
    /// Replaces Form1.setupBoxes().
    /// </summary>
    private void SetupBoxes()
    {
        Tracing.TraceLine("SetupBoxes", TraceLevel.Info);
        if (RigControl == null)
        {
            Tracing.TraceLine("SetupBoxes: no rig", TraceLevel.Error);
            return;
        }

        FreqOut.Clear();

        _comboControls.Clear();
        _enableDisableControls.Clear();

        // Mode control
        ModeControl.IsEnabled = true;
        ModeControl.ClearCache();
        ModeControl.TheList = null;
        var modeList = new ArrayList();
        foreach (string val in RigCaps.ModeTable)
        {
            modeList.Add(val);
        }
        ModeControl.TheList = modeList;
        ModeControl.UpdateDisplayFunction = () => RigControl.Mode;
        ModeControl.UpdateRigFunction = (v) =>
        {
            if (!_radioPowerOn)
            {
                Tracing.TraceLine("mode: no power", TraceLevel.Error);
                return;
            }
            RigControl.Mode = v?.ToString();
        };
        _comboControls.Add(ModeControl);
        _enableDisableControls.Add(ModeControl);

        // SentTextBox in enable/disable collection
        _enableDisableControls.Add(SentTextBox);
    }

    /// <summary>
    /// Set up the frequency display fields.
    /// Simplified version of Form1.setupFreqout() for WPF FrequencyDisplay.
    /// </summary>
    private void SetupFreqout()
    {
        if (RigControl == null) return;
        Tracing.TraceLine("SetupFreqout", TraceLevel.Info);

        var fields = new List<FrequencyDisplay.DisplayField>();

        // Slice indicators (one character each)
        int vfos = RigControl.TotalNumSlices;
        for (int i = 0; i < vfos; i++)
        {
            fields.Add(new FrequencyDisplay.DisplayField(i.ToString(), 1, "", ""));
        }

        // Fixed fields: S-meter, Split, VOX, VFO, Freq, Offset, RIT, XIT
        fields.Add(new FrequencyDisplay.DisplayField("SMeter", 4, "", ""));
        fields.Add(new FrequencyDisplay.DisplayField("Split", 1, "", ""));
        fields.Add(new FrequencyDisplay.DisplayField("VOX", 1, "", ""));
        fields.Add(new FrequencyDisplay.DisplayField("VFO", 1, "", ""));
        fields.Add(new FrequencyDisplay.DisplayField("Freq", 12, "", ""));
        fields.Add(new FrequencyDisplay.DisplayField("Offset", 1, "", ""));
        fields.Add(new FrequencyDisplay.DisplayField("RIT", 5, "", ""));
        fields.Add(new FrequencyDisplay.DisplayField("XIT", 5, " ", ""));

        FreqOut.Populate(fields.ToArray());
        _firstFreqDisplay = true;
    }

    private bool _firstFreqDisplay = true;

    /// <summary>
    /// Update the frequency display from current rig state.
    /// Simplified version of Form1.showFrequency() that reads directly from the rig.
    /// </summary>
    private void ShowFrequency()
    {
        if (RigControl == null || OpenParms?.FormatFreq == null) return;

        try
        {
            // Frequency
            ulong freq = RigControl.Transmit
                ? RigControl.TXFrequency
                : RigControl.VirtualRXFrequency;
            if (freq > 0)
            {
                FreqOut.Write("Freq", OpenParms.FormatFreq(freq));
            }

            // S-meter (raw value)
            int smeter = (int)RigControl.SMeter;
            if (RigControl.Transmit)
            {
                FreqOut.Write("SMeter", smeter.ToString());
            }
            else
            {
                // S-units
                if (RigControl.SmeterInDBM)
                {
                    FreqOut.Write("SMeter", smeter.ToString());
                }
                else if (smeter > 9)
                {
                    FreqOut.Write("SMeter", "+" + (smeter - 9).ToString());
                }
                else
                {
                    FreqOut.Write("SMeter", smeter.ToString());
                }
            }

            // VFO indicator
            FreqOut.Write("VFO", RigControl.RXVFO.ToString());

            // RIT
            var rit = RigControl.RIT;
            if (rit.Active)
            {
                string ritText = (rit.Value < 0 ? "-" : "+") + Math.Abs(rit.Value).ToString("d4");
                FreqOut.Write("RIT", ritText);
            }
            else
            {
                FreqOut.Write("RIT", " rrrr");
            }

            // XIT
            var xit = RigControl.XIT;
            if (xit.Active)
            {
                string xitText = (xit.Value < 0 ? "-" : "+") + Math.Abs(xit.Value).ToString("d4");
                FreqOut.Write("XIT", xitText);
            }
            else
            {
                FreqOut.Write("XIT", " xxxx");
            }

            if (FreqOut.Changed)
            {
                FreqOut.Display();
            }
        }
        catch (Exception ex)
        {
            Tracing.TraceLine($"ShowFrequency error: {ex.Message}", TraceLevel.Error);
        }
    }

    /// <summary>
    /// Configure variable controls based on rig capabilities.
    /// Replaces Form1.configVariableControls().
    /// </summary>
    private void ConfigureVariableControls()
    {
        if (RigControl == null) return;

        _enableDisableControls.Remove(TransmitButton);
        if (RigControl.MyCaps.HasCap(RigCaps.Caps.ManualTransmit))
        {
            _enableDisableControls.Add(TransmitButton);
            TransmitButton.Visibility = Visibility.Visible;
        }
        else
        {
            TransmitButton.Visibility = Visibility.Collapsed;
        }
    }

    // ── Event Handlers ──────────────────────────────────────

    private void PowerStatusHandler(object sender, bool powerOn)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => PowerStatusHandler(sender, powerOn));
            return;
        }

        if (powerOn) PowerNowOn();
        else PowerNowOffInternal();
    }

    private void NoSliceErrorHandler(object sender, string msg)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => NoSliceErrorHandler(sender, msg));
            return;
        }

        System.Windows.MessageBox.Show(msg, "Error", MessageBoxButton.OK);
        CloseRadioCallback?.Invoke();
    }

    private void FeatureLicenseChangedHandler(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => FeatureLicenseChangedHandler(sender, e));
            return;
        }

        // Menu updates for Diversity/ESC availability
        // Full implementation when WPF menus support advanced feature gating
        Tracing.TraceLine("FeatureLicenseChanged", TraceLevel.Info);
    }

    private void TransmitChangeHandler(object sender, bool transmit)
    {
        // Reset S-meter peak on transmit change
        Tracing.TraceLine($"TransmitChange: {transmit}", TraceLevel.Info);
    }

    private void FlexAntTuneStartStopHandler(FlexBase.FlexAntTunerArg e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => FlexAntTuneStartStopHandler(e));
            return;
        }

        if (RigControl == null) return;

        if (RigControl.FlexTunerType == FlexBase.FlexTunerTypes.manual)
        {
            if (e.Status == "OK")
            {
                SetButtonText(AntennaTuneButton, e.SWR);
            }
        }
        else
        {
            SetButtonText(AntennaTuneButton, e.Status);
        }
    }

    private void ConnectedEventHandler(object sender, FlexBase.ConnectedArg e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ConnectedEventHandler(sender, e));
            return;
        }

        Tracing.TraceLine($"ConnectedEvent: Power={_radioPowerOn} Connected={e.Connected}", TraceLevel.Info);
        if (_radioPowerOn && !e.Connected)
        {
            PowerNowOffInternal();
            System.Windows.MessageBox.Show("The radio disconnected", "Error", MessageBoxButton.OK);
        }
    }

    // ── Power On/Off ──────────────────────────────────────

    private void PowerNowOn()
    {
        Tracing.TraceLine("MainWindow PowerNowOn", TraceLevel.Info);

        // Setup frequency display
        SetupFreqout();

        // Setup operations menu
        SetupOperationsMenu();

        // Configure controls based on rig caps
        ConfigureVariableControls();

        // Enable all controls
        EnableDisableWindowControls(true);

        // Wire additional event handlers
        if (RigControl != null)
        {
            RigControl.TransmitChange += TransmitChangeHandler;
            RigControl.FlexAntTunerStartStop += FlexAntTuneStartStopHandler;
            RigControl.ConnectedEvent += ConnectedEventHandler;
        }

        // Status updates
        WriteStatus("Power", "On");
        if (RigControl != null)
            WriteStatus("Memories", RigControl.NumberOfMemories.ToString());

        _radioPowerOn = true;
        StatusText.Text = "Radio connected — power on";

        // VB-side tasks (knob setup, tracing)
        PowerOnCallback?.Invoke();
    }

    /// <summary>
    /// Internal power-off handler. Implements the full power-off sequence.
    /// </summary>
    private void PowerNowOffInternal()
    {
        Tracing.TraceLine("MainWindow PowerNowOff", TraceLevel.Info);

        if (RigControl != null)
        {
            RigControl.TransmitChange -= TransmitChangeHandler;
            RigControl.FlexAntTunerStartStop -= FlexAntTuneStartStopHandler;
            RigControl.ConnectedEvent -= ConnectedEventHandler;
        }

        _radioPowerOn = false;

        if (!_isClosing)
        {
            FreqOut.Clear();
            WriteStatus("Power", "Off");
            EnableDisableWindowControls(false);
            StatusText.Text = "Radio connected — power off";
        }
    }

    // ── Helpers ──────────────────────────────────────

    /// <summary>
    /// Thread-safe button text update. Replaces Form1.setButtonText().
    /// </summary>
    private void SetButtonText(Button button, string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetButtonText(button, text));
            return;
        }

        button.Content = text;
        System.Windows.Automation.AutomationProperties.SetName(button, text);
    }

    /// <summary>
    /// Toggle transmit state. Replaces Form1.toggleTransmit().
    /// </summary>
    private void ToggleTransmit()
    {
        if (!_radioPowerOn || RigControl == null)
        {
            Tracing.TraceLine("toggleTransmit: no power", TraceLevel.Error);
            return;
        }

        Tracing.TraceLine($"toggling transmit: {RigControl.Transmit}", TraceLevel.Info);
        RigControl.Transmit = !RigControl.Transmit;
    }

    /// <summary>
    /// Show the WPF MemoriesDialog with delegates wired to the current radio.
    /// Sprint 11 Phase 11.7: Replaces FlexMemories.ShowDialog() call in KeyCommands.
    /// </summary>
    private void ShowMemoriesDialog()
    {
        if (RigControl?.RigFields?.Memories == null) return;

        var memories = RigControl.RigFields.Memories;
        var dialog = new Dialogs.MemoriesDialog();

        // Wire sorted memory list
        dialog.GetSortedMemories = () =>
        {
            var items = new List<Dialogs.MemoriesDialog.MemoryDisplayItem>();
            foreach (var mem in memories.SortedMemories)
            {
                var m = mem.Value;
                string name = string.IsNullOrEmpty(m.Name) ? m.Freq.ToString("F6") : m.Name;
                string group = string.IsNullOrEmpty(m.Group) ? "" : m.Group + '.';
                items.Add(new Dialogs.MemoriesDialog.MemoryDisplayItem
                {
                    FullName = group + name,
                    MemoryRef = m
                });
            }
            return items;
        };

        // Wire select memory (tune to it)
        dialog.SelectMemory = (memRef) =>
        {
            if (memRef is Flex.Smoothlake.FlexLib.Memory mem)
            {
                mem.Select();
            }
        };

        // Wire per-memory property getters
        dialog.FormatFrequency = (memRef) =>
        {
            if (memRef is Flex.Smoothlake.FlexLib.Memory mem && OpenParms?.FormatFreq != null)
                return OpenParms.FormatFreq((ulong)(mem.Freq * 1e6));
            return "";
        };

        dialog.GetMode = (memRef) =>
            memRef is Flex.Smoothlake.FlexLib.Memory mem ? mem.Mode ?? "" : "";

        dialog.GetName = (memRef) =>
            memRef is Flex.Smoothlake.FlexLib.Memory mem ? mem.Name ?? "" : "";

        dialog.GetOwner = (memRef) =>
            memRef is Flex.Smoothlake.FlexLib.Memory mem ? mem.Owner ?? "" : "";

        dialog.GetGroup = (memRef) =>
            memRef is Flex.Smoothlake.FlexLib.Memory mem ? mem.Group ?? "" : "";

        dialog.GetFilterLow = (memRef) =>
            memRef is Flex.Smoothlake.FlexLib.Memory mem ? mem.RXFilterLow : 0;

        dialog.GetFilterHigh = (memRef) =>
            memRef is Flex.Smoothlake.FlexLib.Memory mem ? mem.RXFilterHigh : 0;

        // Mode list from rig caps
        dialog.ModeList = new List<string>(RigCaps.ModeTable);

        // Show the dialog
        dialog.Owner = this;
        dialog.ShowDialog();

        // If user selected a memory via Enter key, go home
        if (dialog.ShowFreq)
        {
            gotoHome();
        }
    }

    #endregion

    #region Radio Control Button Handlers — Phase 8.2

    /// <summary>
    /// Antenna Tune button — toggles FlexTunerOn.
    /// </summary>
    private void AntennaTuneButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_radioPowerOn || RigControl == null)
        {
            Tracing.TraceLine("antennaTune: no power", TraceLevel.Error);
            return;
        }
        _oldSwr = "";
        RigControl.FlexTunerOn = !RigControl.FlexTunerOn;
    }

    /// <summary>
    /// Show SWR/tuner status on hover.
    /// </summary>
    private void AntennaTuneButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_radioPowerOn)
        {
            Tracing.TraceLine("antennaTune: no power", TraceLevel.Error);
            return;
        }
        SetButtonText(AntennaTuneButton, AntennaTuneButtonText);
    }

    private void AntennaTuneButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_radioPowerOn) return;
        SetButtonText(AntennaTuneButton, AntennaTuneButtonText);
    }

    /// <summary>
    /// Transmit button — toggles PTT.
    /// </summary>
    private void TransmitButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleTransmit();
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

    #region StatusBar + ScanTimer — Sprint 10 Phase 10.1

    /// <summary>
    /// Write a named field to the WPF StatusBar.
    /// Replaces Form1.StatusBox.Write(key, value) — the RadioBoxes.MainBox API.
    /// Supported keys: "Power", "Memory", "Scan", "LogFile" (matching StatusBar TextBlocks).
    /// </summary>
    public void WriteStatus(string key, string value)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => WriteStatus(key, value));
            return;
        }

        switch (key)
        {
            case "Power":
                StatusPower.Text = value;
                break;
            case "Memories":
                StatusMemory.Text = value;
                break;
            case "Scan":
                StatusScan.Text = value;
                break;
            case "LogFile":
                StatusLogFile.Text = value;
                break;
            case "Knob":
                // Knob status not in current StatusBar layout — ignore for now
                break;
            default:
                Tracing.TraceLine($"MainWindow.WriteStatus: unknown key '{key}'", TraceLevel.Warning);
                break;
        }
    }

    /// <summary>
    /// Scan timer — DispatcherTimer replacing Form1.ScanTmr (WinForms Timer).
    /// Used by scan.vb and MemoryScan.vb via globals.scanTimer property.
    /// </summary>
    private DispatcherTimer? _scanTimer;

    /// <summary>
    /// Gets the scan timer, creating it on first access.
    /// Tick event fires ScanTimerTick which the main app wires to
    /// scan.ScanTimer_Tick / MemoryScan.ScanTimer_Tick (replaces Form1 Handles clause).
    /// </summary>
    public DispatcherTimer ScanTimer
    {
        get
        {
            if (_scanTimer == null)
            {
                _scanTimer = new DispatcherTimer();
                _scanTimer.Interval = TimeSpan.FromMilliseconds(500); // default, overridden by scan code
                _scanTimer.Tick += (s, e) => ScanTimerTick?.Invoke(s, e);
            }
            return _scanTimer;
        }
    }

    /// <summary>
    /// Event raised on each scan timer tick. Wire this in ApplicationEvents.vb to dispatch
    /// to scan.ScanTimer_Tick or MemoryScan.ScanTimer_Tick based on scanstate.
    /// </summary>
    public event EventHandler? ScanTimerTick;

    /// <summary>
    /// Whether the scan timer is currently running.
    /// Replaces Form1.ScanTmr.Enabled check in globals.ScanInProcess.
    /// </summary>
    public bool ScanTimerEnabled
    {
        get => _scanTimer?.IsEnabled ?? false;
        set
        {
            if (value)
                ScanTimer.Start();
            else
                _scanTimer?.Stop();
        }
    }

    #endregion

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
    /// Public power-off entry point — callable from VB side.
    /// Matches Form1.powerNowOff().
    /// </summary>
    public void powerNowOff()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(powerNowOff);
            return;
        }
        PowerNowOffInternal();
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
