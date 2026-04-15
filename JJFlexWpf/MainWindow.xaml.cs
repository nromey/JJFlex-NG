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
public partial class MainWindow : UserControl
{
    /// <summary>
    /// Flag to prevent re-entrant close attempts (mirrors Form1.Ending).
    /// </summary>
    private bool _isClosing;

    /// <summary>
    /// Callback to route UI mode changes to the WinForms MenuStripBuilder.
    /// Set by ShellForm constructor after building menus.
    /// </summary>
    public Action<UIMode>? MenuModeCallback { get; set; }

    /// <summary>
    /// Callback to persist UI mode changes to the operator profile.
    /// Set by ApplicationEvents.vb — routes to globals.ActiveUIMode setter.
    /// </summary>
    public Action<UIMode>? SaveUIModeCallback { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        Loaded += MainWindow_Loaded;

        // Focus-return context: when any JJFlexDialog closes, speak compact status
        JJFlexDialog.FocusReturnCallback = () =>
        {
            if (RigControl != null)
            {
                string status = Radios.RadioStatusBuilder.BuildSpokenStatus(RigControl);
                Radios.ScreenReaderOutput.Speak(status, Radios.VerbosityLevel.Chatty);
            }
        };

        // Wire braille display focus events
        FreqOut.GotKeyboardFocus += (s, e) => _brailleEngine.OnHomePositionFocused();
        FreqOut.LostKeyboardFocus += (s, e) => _brailleEngine.OnHomePositionBlurred();

        // Wire ScreenFieldsPanel Escape handler (Sprint 14) — once, not per-connect
        FieldsPanel.EscapePressed += (s, e) => FreqOut.FocusDisplay();
        FieldsPanel.ReturnFocusToFreqOut = () => FreqOut.FocusDisplay();

        // Wire MetersPanel Escape handler (Sprint 22 Phase 9)
        MetersPanel.EscapePressed += (s, e) => FreqOut.FocusDisplay();
        MetersPanel.ReturnFocusToFreqOut = () => FreqOut.FocusDisplay();
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

        // Welcome speech is now triggered by ShellForm.OnShown() calling SpeakWelcome(),
        // so it fires after the window is visible and the screen reader can hear it.

        // Update status
        StatusText.Text = "Ready — no radio connected";

        Tracing.TraceLine("MainWindow_Loaded: init complete", System.Diagnostics.TraceLevel.Info);
    }

    /// <summary>
    /// Called by ShellForm.OnShown() after the window is visible on screen.
    /// Screen reader speech only works reliably after the window is visible.
    /// </summary>
    public void SpeakWelcome()
    {
        // Focus FreqOut so cursor lands on the frequency display at startup
        FreqOut.FocusDisplay();

        string modeName = ActiveUIMode == UIMode.Classic ? "Classic" : "Modern";
        Radios.ScreenReaderOutput.Speak($"Welcome to JJ Flexible Radio Access, {modeName} tuning mode");
    }

    /// <summary>
    /// Sprint 22 Phase 8: Speak radio status after connect. Delayed 1.5s to let
    /// FlexLib populate slice data. Called at the end of PowerNowOn().
    /// </summary>
    private void SpeakConnectStatus()
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(1500);
            Dispatcher.Invoke(() =>
            {
                if (RigControl == null) return;

                string model = RigControl.RadioModel;
                string connType = RigControl.RemoteRig ? "SmartLink" : "local";
                string status = Radios.RadioStatusBuilder.BuildFullSliceStatus(RigControl);

                // The full status already includes frequency/mode/slice detail.
                // Prepend with connection info that BuildFullSliceStatus doesn't cover.
                string message = $"Connected to {model}, {connType}. {status}";
                Radios.ScreenReaderOutput.Speak(message, VerbosityLevel.Critical);
            });
        });
    }

    /// <summary>
    /// Called by the parent ShellForm before closing to run the VB-side exit sequence.
    /// Returns true to allow close, false to cancel (e.g., unsaved QSO).
    /// </summary>
    public bool RequestShutdown()
    {
        if (_isClosing)
            return true;

        Tracing.TraceLine("MainWindow.RequestShutdown: starting shutdown", System.Diagnostics.TraceLevel.Info);

        // Run VB-side exit sequence (prompts, cleanup, radio close)
        if (AppExitCallback != null && !AppExitCallback())
        {
            Tracing.TraceLine("MainWindow.RequestShutdown: exit cancelled by user", System.Diagnostics.TraceLevel.Info);
            return false;
        }

        _isClosing = true;

        try
        {
            UnwireRadioEvents();
            Tracing.TraceLine("MainWindow.RequestShutdown: shutdown complete", System.Diagnostics.TraceLevel.Info);
        }
        catch (System.Exception ex)
        {
            Tracing.TraceLine($"MainWindow.RequestShutdown error: {ex.Message}", System.Diagnostics.TraceLevel.Error);
        }

        return true;
    }

    /// <summary>
    /// Delegate to close the parent ShellForm. Set by ApplicationEvents.vb.
    /// </summary>
    public Action? CloseShellCallback { get; set; }

    /// <summary>
    /// Callback to wire FreqOutHandlers delegate properties from VB.NET globals.
    /// Set by ApplicationEvents.vb. Called when handlers are first created in SetupFreqout().
    /// </summary>
    public Action<FreqOutHandlers>? FreqOutHandlersWireCallback { get; set; }

    /// <summary>
    /// Callback to set filter presets on the NativeMenuBar.
    /// Set by ShellForm constructor. Called from FreqOutHandlersWireCallback
    /// so presets are available before the menu rebuild in PowerNowOn.
    /// </summary>
    public Action<Radios.FilterPresets>? SetNativeMenuFilterPresetsCallback { get; set; }

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
    /// PTT safety controller — manages TX hold, lock, timeout warnings, hard kill.
    /// Created when radio connects, disposed on disconnect.
    /// </summary>
    private PttSafetyController? _pttController;

    /// <summary>
    /// Guards against repeated PttDown calls from key-repeat.
    /// Set true on first Ctrl+Space down, cleared on key-up.
    /// </summary>
    private bool _pttKeyDown;

    /// <summary>
    /// Current PTT configuration. Set during radio connect, used by Settings dialog.
    /// </summary>
    internal PttConfig? CurrentPttConfig { get; private set; }

    /// <summary>
    /// Audio output configuration (earcon device, meter tones). Loaded at radio connect.
    /// </summary>
    public AudioOutputConfig? CurrentAudioConfig { get; set; }

    /// <summary>
    /// Returns PTT status text for the Speak Status hotkey, or null if PTT is idle.
    /// </summary>
    public string? GetPttStatusText() => _pttController?.GetSpokenStatus();
    public string? GetFilterEdgeStatus() => _freqOutHandlers?.FilterEdgeStatus;

    /// <summary>
    /// Returns tuning mode status for Speak Status, e.g. "coarse 1 kilohertz".
    /// </summary>
    public string? GetTuningModeStatus()
    {
        if (_freqOutHandlers == null) return null;
        if (ActiveUIMode == UIMode.Classic)
            return "classic tuning mode";
        string step = _freqOutHandlers.IsCoarseMode ? "coarse" : "fine";
        return $"modern tuning mode, {step}, {FreqOutHandlers.FormatStepForSpeech(_freqOutHandlers.CurrentTuneStep)}";
    }

    /// <summary>
    /// Returns "frequency readout off" if readout is disabled, null otherwise.
    /// Only report when off — "on" is the default and not notable.
    /// </summary>
    public string? GetFreqReadoutStatus()
    {
        if (_freqOutHandlers == null) return null;
        return _freqOutHandlers.FreqReadoutEnabled ? null : "frequency readout off";
    }

    /// <summary>
    /// Returns meter tone status for Speak Status, e.g. "meter tones on, RX Monitor".
    /// </summary>
    public string? GetMeterStatus()
    {
        if (!MeterToneEngine.Enabled) return null;
        return $"meter tones {MeterToneEngine.CurrentPreset}";
    }

    /// <summary>
    /// Returns active filter preset name, or null if not on a preset.
    /// </summary>
    public string? GetFilterPresetStatus() => _freqOutHandlers?.ActiveFilterPresetStatus;

    /// <summary>
    /// Apply settings changes from the Settings dialog.
    /// Propagates PttConfig to controller, tuning steps to handler, and saves to disk.
    /// </summary>
    internal void ApplySettingsChanges(int coarseStep, int fineStep)
    {
        // Update PttSafetyController with modified config
        if (CurrentPttConfig != null)
            _pttController?.UpdateConfig(CurrentPttConfig);

        // Apply tuning steps and band memory setting
        if (_freqOutHandlers != null)
        {
            _freqOutHandlers.CoarseTuneStep = coarseStep;
            _freqOutHandlers.FineTuneStep = fineStep;
            _freqOutHandlers.BandMemoryEnabled = CurrentPttConfig?.BandMemoryEnabled ?? true;
            _freqOutHandlers.FrequencyUnits = CurrentPttConfig?.FrequencyDisplayUnits ?? Radios.FrequencyUnits.Hz;
            _freqOutHandlers.SaveStepSizes?.Invoke(coarseStep, fineStep);
        }

        // Save PttConfig to disk
        if (CurrentPttConfig != null && OpenParms != null)
            CurrentPttConfig.Save(OpenParms.ConfigDirectory, OpenParms.GetOperatorName());

        // Save LicenseConfig to disk
        if (_freqOutHandlers?.License != null && OpenParms != null)
            _freqOutHandlers.License.Save(OpenParms.ConfigDirectory, OpenParms.GetOperatorName());

        // Save AudioOutputConfig to disk
        if (CurrentAudioConfig != null && OpenParms != null)
        {
            CurrentAudioConfig.CaptureFromEngine();
            CurrentAudioConfig.Save(OpenParms.ConfigDirectory);
        }

        // Reflect any "Show panadapter" change immediately (toggle acts live)
        ApplyPanadapterVisibility();
    }

    /// <summary>
    /// Sync PanadapterPanel.Visibility to CurrentAudioConfig.ShowPanadapter.
    /// Collapsed removes the panel from layout AND the tab order so users
    /// who don't use the waterfall aren't forced to Tab through it. Called
    /// at startup and after Settings OK.
    /// </summary>
    private void ApplyPanadapterVisibility()
    {
        bool show = CurrentAudioConfig?.ShowPanadapter ?? true;
        PanadapterPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Previous SWR text for change detection.
    /// </summary>
    private string _oldSwr = "";

    /// <summary>
    /// ATU tune timeout timer — stops progress earcon after 15 seconds if no result.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _atuTuneTimer;

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
                    RigControl.RigFields.RigUpdate?.Invoke();
                }

                // Update screen fields panel (Sprint 14)
                if (FieldsPanel.Visibility == Visibility.Visible)
                {
                    FieldsPanel.PollUpdate();
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
    /// User's preference for field panel visibility in Classic mode.
    /// Persisted to operator profile via SaveFieldsPanelVisibleCallback.
    /// Sprint 15 Track D.
    /// </summary>
    public bool FieldsPanelUserVisible { get; set; } = true;

    /// <summary>Callback to persist field panel visibility to operator profile.</summary>
    public Action<bool>? SaveFieldsPanelVisibleCallback { get; set; }

    /// <summary>Callback to load field panel visibility from operator profile. Called on mode switch.</summary>
    public Func<bool>? LoadFieldsPanelVisibleCallback { get; set; }

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

        // Route menu mode change to WinForms MenuStripBuilder
        MenuModeCallback?.Invoke(mode);

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
    /// Show Classic mode: radio controls visible, logging hidden.
    /// Rebuilds FreqOut with full field set if radio is connected.
    /// </summary>
    private void ShowClassicUI()
    {
        RadioControlsPanel.Visibility = Visibility.Visible;

        // Restore user's field panel preference (Sprint 15 Track D)
        if (LoadFieldsPanelVisibleCallback != null)
            FieldsPanelUserVisible = LoadFieldsPanelVisibleCallback();
        FieldsPanel.Visibility = FieldsPanelUserVisible ? Visibility.Visible : Visibility.Collapsed;

        SetTextAreasVisible(true);
        LoggingPanel.Visibility = Visibility.Collapsed;

        // Rebuild FreqOut with Classic field set if radio is connected
        if (RigControl != null && _radioPowerOn)
            SetupFreqoutClassic();
    }

    /// <summary>
    /// Show Modern mode: radio controls visible, logging hidden.
    /// Rebuilds FreqOut with simplified field set if radio is connected.
    /// </summary>
    private void ShowModernUI()
    {
        RadioControlsPanel.Visibility = Visibility.Visible;

        // Respect user's field panel preference (same as Classic)
        if (LoadFieldsPanelVisibleCallback != null)
            FieldsPanelUserVisible = LoadFieldsPanelVisibleCallback();
        FieldsPanel.Visibility = FieldsPanelUserVisible ? Visibility.Visible : Visibility.Collapsed;

        SetTextAreasVisible(true);
        LoggingPanel.Visibility = Visibility.Collapsed;

        // Rebuild FreqOut with Modern field set if radio is connected
        if (RigControl != null && _radioPowerOn)
            SetupFreqoutModern();
    }

    /// <summary>
    /// Show Logging mode: radio controls hidden, log panel visible.
    /// </summary>
    private void ShowLoggingUI()
    {
        RadioControlsPanel.Visibility = Visibility.Collapsed;
        FieldsPanel.Visibility = Visibility.Collapsed;
        SetTextAreasVisible(false);
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
    /// Toggle between Classic and Modern tuning modes.
    /// Menus are unified — this only changes tuning behavior (FreqOut field set).
    /// </summary>
    public void ToggleUIMode()
    {
        if (ActiveUIMode == UIMode.Logging)
        {
            return;
        }

        var newMode = ActiveUIMode == UIMode.Classic ? UIMode.Modern : UIMode.Classic;
        LastNonLogMode = newMode;
        ApplyUIMode(newMode);
        SaveUIModeCallback?.Invoke(newMode);
        Radios.ScreenReaderOutput.Speak($"{newMode} tuning mode", VerbosityLevel.Terse);
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

        Radios.ScreenReaderOutput.Speak("Entering Logging Mode, Call Sign", VerbosityLevel.Terse);
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
        FreqOut.FocusDisplay();

        Radios.ScreenReaderOutput.Speak($"Returning to {LastNonLogMode} tuning mode", VerbosityLevel.Terse);
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
    ///
    /// Alt and F10 are NOT handled here — they activate the native Win32 HMENU
    /// menu bar automatically via DefWindowProc in the WinForms message loop.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 0. Ctrl+Tab opens action toolbar
        var rawKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (rawKey == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowActionToolbar();
            e.Handled = true;
            return;
        }
        // Let regular Tab pass through to WPF default handling
        if (rawKey == Key.Tab)
            return;

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

            if (key == Key.F)
            {
                if (_freqOutHandlers != null)
                    _freqOutHandlers.ToggleFreqReadout();
                else
                    Radios.ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Critical, true);
                e.Handled = true;
                return;
            }

            // Category navigation hotkeys (Ctrl+Shift+N/U/R/X/A) moved to KeyCommands.vb
            // Sprint 23 Phase 2: Unified hotkey dispatch — all through scope system now
        }

        // 1b. Alt+Ctrl+F — read current filter values
        if (Keyboard.Modifiers == (ModifierKeys.Alt | ModifierKeys.Control) && rawKey == Key.F)
        {
            if (RigControl != null && _radioPowerOn)
            {
                int low = RigControl.FilterLow;
                int high = RigControl.FilterHigh;
                Radios.ScreenReaderOutput.Speak($"Filter {low} to {high}", VerbosityLevel.Terse, true);
            }
            else
            {
                Radios.ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Critical, true);
            }
            e.Handled = true;
            return;
        }

        // 2. Filter hotkeys (bracket keys) — Modern and Classic modes (not Logging)
        if (ActiveUIMode != UIMode.Logging && _freqOutHandlers != null && _radioPowerOn)
        {
            // Escape cancels filter edge selection mode
            if (rawKey == Key.Escape && _freqOutHandlers.InFilterEdgeMode)
            {
                _freqOutHandlers.CancelFilterEdgeMode();
                e.Handled = true;
                return;
            }
            if (rawKey == Key.OemOpenBrackets || rawKey == Key.OemCloseBrackets)
            {
                _freqOutHandlers.HandleFilterHotkey(e);
                if (e.Handled) return;
            }
        }

        // 3. PTT keys — Ctrl+Space (hold), Shift+Space (lock toggle), Escape (unlock)
        //    Only active when FreqOut has focus and radio is powered on.
        //    Requires modifier keys to prevent accidental transmit.
        if (_pttController != null && _radioPowerOn && FreqOut.IsKeyboardFocusWithin)
        {
            if (rawKey == Key.Space && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                _pttController.ToggleLock();
                e.Handled = true;
                return;
            }

            if (rawKey == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (!e.IsRepeat && !_pttKeyDown) // ignore key-repeat and redundant down events
                {
                    _pttKeyDown = true;
                    _pttController.PttDown();
                }
                e.Handled = true;
                return;
            }

            if (rawKey == Key.Escape && _pttController.IsTransmitting)
            {
                _pttController.EscapeUnlock();
                e.Handled = true;
                return;
            }
        }

        // 4. Route through scope-aware KeyCommands registry
        if (DoCommandHandler != null)
        {
            var winFormsKey = WpfKeyConverter.ToWinFormsKeys(e);
            if (winFormsKey != System.Windows.Forms.Keys.None && DoCommandHandler(winFormsKey))
            {
                e.Handled = true;
                return;
            }
        }

        // 5. Fall through to focused control (default WPF behavior)
    }

    /// <summary>
    /// PreviewKeyUp — handles Ctrl+Space release for PTT hold mode.
    /// Catches Space release regardless of Ctrl state (user may release Ctrl first).
    /// </summary>
    private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var rawKey = e.Key == Key.System ? e.SystemKey : e.Key;

        if (rawKey == Key.Space && _pttController != null && _pttController.State == PttSafetyController.PttState.PttHold)
        {
            _pttKeyDown = false;
            _pttController.PttUp();
            e.Handled = true;
        }
        else if (rawKey == Key.Space)
        {
            _pttKeyDown = false; // Clear flag even if PTT state changed (e.g., locked)
        }
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
    /// Save a SmartLink account email as the default for Remote connections.
    /// Wired by globals.vb since it needs BaseConfigDir + operator name.
    /// </summary>
    public Action<string>? SaveDefaultSmartLinkAccount { get; set; }

    /// <summary>
    /// Get the current default SmartLink account email.
    /// Wired by globals.vb since it needs BaseConfigDir + operator name.
    /// </summary>
    public Func<string>? GetDefaultSmartLinkEmail { get; set; }

    /// <summary>
    /// Update the shell form title bar with radio status.
    /// Wired by globals.vb to AppShellForm.Text.
    /// </summary>
    public Action<string>? UpdateTitleBar { get; set; }

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
    /// Callback to show past Connection Test results.
    /// </summary>
    public Action? ShowTestResultsCallback { get; set; }

    /// <summary>
    /// Callback to show a WinForms error dialog parented to ShellForm.
    /// Parameters: message, title. Falls back to unparented WPF MessageBox if not set.
    /// </summary>
    public Action<string, string>? ShowErrorCallback { get; set; }

    /// <summary>
    /// Callback to build the list of current key-action mappings for ShowKeysDialog.
    /// Set by ApplicationEvents.vb.
    /// </summary>
    public Func<List<Dialogs.KeyActionItem>>? GetKeyActionsCallback { get; set; }

    /// <summary>
    /// Callback to build the list of all available actions for SetupKeysDialog.
    /// Set by ApplicationEvents.vb.
    /// </summary>
    public Func<List<Dialogs.ActionItem>>? GetAvailableActionsCallback { get; set; }

    /// <summary>
    /// Callback to build the list of command finder items for CommandFinderDialog.
    /// Set by ApplicationEvents.vb.
    /// </summary>
    public Func<List<Dialogs.CommandFinderItem>>? GetCommandFinderItemsCallback { get; set; }

    /// <summary>
    /// Callback to execute a command by its tag (CommandValues enum value).
    /// Set by ApplicationEvents.vb.
    /// </summary>
    public Action<object>? ExecuteCommandCallback { get; set; }

    /// <summary>
    /// Callback to save updated key mappings from the ShowKeysDialog.
    /// Set by ApplicationEvents.vb.
    /// </summary>
    public Action<List<Dialogs.KeyActionItem>>? SaveKeyActionsCallback { get; set; }

    /// <summary>Callback to speak the current radio status summary. Set by ApplicationEvents.vb.</summary>
    public Action? SpeakStatusCallback { get; set; }

    /// <summary>Callback to show the status dialog. Set by ApplicationEvents.vb.</summary>
    public Action? ShowStatusDialogCallback { get; set; }

    /// <summary>Callback to open the PortAudio device picker. Set by globals.vb.</summary>
    public Action? AudioSetupCallback { get; set; }

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
    /// FreqOut field handler instance — manages all interactive tuning.
    /// </summary>
    private FreqOutHandlers? _freqOutHandlers;

    /// <summary>Braille display status line engine.</summary>
    private readonly BrailleStatusEngine _brailleEngine = new();

    /// <summary>CW Morse code notification engine for connection/status events.</summary>
    private readonly MorseNotifier _morseNotifier = new(new EarconCwOutput());

    /// <summary>
    /// Expose FreqOutHandlers for Settings dialog tuning step access.
    /// </summary>
    public FreqOutHandlers? FreqHandlers => _freqOutHandlers;

    /// <summary>
    /// Set up the frequency display fields with interactive handlers.
    /// Dispatches to Classic or Modern field set based on ActiveUIMode.
    /// </summary>
    private void SetupFreqout()
    {
        if (RigControl == null) return;

        if (ActiveUIMode == UIMode.Modern)
            SetupFreqoutModern();
        else
            SetupFreqoutClassic();
    }

    /// <summary>
    /// Classic mode: full field set — Slice, Mute, Volume, SMeter, Split, VOX, Freq, Offset, RIT, XIT.
    /// Position-based tuning via cursor placement within each field.
    /// </summary>
    private void SetupFreqoutClassic()
    {
        if (RigControl == null) return;
        Tracing.TraceLine("SetupFreqoutClassic", TraceLevel.Info);

        EnsureFreqOutHandlers();

        var fields = new List<FrequencyDisplay.DisplayField>();

        // Field order: Slice → SliceOps → Freq → Mute → Volume → SMeter → Split → VOX → Offset → RIT → XIT
        fields.Add(new FrequencyDisplay.DisplayField("Slice", 1, "", "") { Label = "Slice",
            HelpItems = new() { ("Up Down", "cycle slices"), ("Space", "next slice"), ("A-H or 0-7", "jump to slice"),
                ("T", "set transmit"), ("Period", "create slice"), ("Comma", "release slice") } });
        fields.Add(new FrequencyDisplay.DisplayField("SliceOps", 3, "", "") { Label = "Slice Audio",
            HelpItems = new() { ("Up Down", "volume"), ("Page Up Down", "pan"), ("M or Space", "mute toggle") } });
        fields.Add(new FrequencyDisplay.DisplayField("Freq", 12, "", "") { Label = "Frequency", DefaultCursorOffset = 8,
            HelpItems = new() { ("Up Down", "tune by cursor position"), ("Digits", "type frequency then Enter"),
                ("K", "round to nearest kilohertz"), ("C", "toggle coarse and fine"),
                ("Plus N", "set step multiplier"), ("F", "speak frequency") } });
        fields.Add(new FrequencyDisplay.DisplayField("Mute", 1, "", "") { Label = "Mute",
            HelpItems = new() { ("Space or M", "toggle mute") } });
        fields.Add(new FrequencyDisplay.DisplayField("Volume", 3, "", "") { Label = "Volume",
            HelpItems = new() { ("Up Down", "adjust volume") } });
        fields.Add(new FrequencyDisplay.DisplayField("SMeter", 4, "", "") { Label = "S Meter",
            HelpItems = new() { ("This field is read-only", "shows signal strength") } });
        fields.Add(new FrequencyDisplay.DisplayField("Split", 1, "", "") { Label = "Split",
            HelpItems = new() { ("Space", "toggle split mode") } });
        fields.Add(new FrequencyDisplay.DisplayField("VOX", 1, "", "") { Label = "VOX",
            HelpItems = new() { ("Space", "toggle VOX") } });
        fields.Add(new FrequencyDisplay.DisplayField("Offset", 1, "", "") { Label = "Offset",
            HelpItems = new() { ("Space", "cycle RIT XIT offset") } });
        fields.Add(new FrequencyDisplay.DisplayField("RIT", 5, "", "") { Label = "RIT", DefaultCursorOffset = 2,
            HelpItems = new() { ("Up Down", "adjust by cursor position"), ("Space", "toggle RIT on off"),
                ("Digits", "enter value") } });
        fields.Add(new FrequencyDisplay.DisplayField("XIT", 5, " ", "") { Label = "XIT", DefaultCursorOffset = 2,
            HelpItems = new() { ("Up Down", "adjust by cursor position"), ("Space", "toggle XIT on off"),
                ("Digits", "enter value") } });

        // Classic mode uses position-based step names (no override)
        FreqOut.StepNameOverride = null;
        FreqOut.IsModernMode = false;
        FreqOut.Populate(fields.ToArray());
        _firstFreqDisplay = true;
    }

    /// <summary>
    /// Modern mode: simplified field set — Freq + Slice + SMeter only.
    /// Tuning via modifier keys (Up/Down = coarse, Shift+Up/Down = fine).
    /// Other controls (Mute, Volume, Split, VOX, RIT, XIT) accessible via Slice menu.
    /// </summary>
    private void SetupFreqoutModern()
    {
        if (RigControl == null) return;
        Tracing.TraceLine("SetupFreqoutModern", TraceLevel.Info);

        EnsureFreqOutHandlers();

        var fields = new List<FrequencyDisplay.DisplayField>();

        // Simplified: Slice → SliceOps → Freq → SMeter
        fields.Add(new FrequencyDisplay.DisplayField("Slice", 1, "", "") { Label = "Slice",
            HelpItems = new() { ("Up Down", "cycle slices"), ("Space", "next slice"), ("A-H or 0-7", "jump to slice"),
                ("T", "set transmit"), ("Period", "create slice"), ("Comma", "release slice") } });
        fields.Add(new FrequencyDisplay.DisplayField("SliceOps", 3, "", "") { Label = "Slice Audio",
            HelpItems = new() { ("Up Down", "volume"), ("Page Up Down", "pan"), ("M or Space", "mute toggle") } });
        fields.Add(new FrequencyDisplay.DisplayField("Freq", 12, "", "") { Label = "Frequency", DefaultCursorOffset = 8,
            HelpItems = new() { ("Digits", "type frequency then Enter"), ("F", "speak frequency"),
                ("C", "toggle coarse and fine"), ("Page Up Down", "cycle step size") } });
        fields.Add(new FrequencyDisplay.DisplayField("SMeter", 4, " ", "") { Label = "S Meter",
            HelpItems = new() { ("This field is read-only", "shows signal strength") } });

        // Hidden fields still written by ShowFrequency but not displayed.
        // ShowFrequency checks if the field exists before writing, so only
        // the three above get updated.

        // Modern mode: Freq field uses modifier keys, not cursor position
        FreqOut.IsModernMode = true;
        // Modern mode: override step name with actual preset-based step
        FreqOut.StepNameOverride = (field) =>
        {
            if (field.Key == "Freq" && _freqOutHandlers != null)
            {
                string mode = _freqOutHandlers.IsCoarseMode ? "coarse" : "fine";
                return $"{mode} {FreqOutHandlers.FormatStepForSpeech(_freqOutHandlers.CurrentTuneStep)}";
            }
            return null;
        };
        FreqOut.Populate(fields.ToArray());
        _firstFreqDisplay = true;
    }

    /// <summary>
    /// Create FreqOutHandlers if not already initialized.
    /// </summary>
    private void EnsureFreqOutHandlers()
    {
        if (_freqOutHandlers == null)
        {
            _freqOutHandlers = new FreqOutHandlers(this);
            // Wire VB.NET globals delegates
            FreqOutHandlersWireCallback?.Invoke(_freqOutHandlers);
            // Wire FieldKeyDown event to route keys to the handler for the field under cursor
            FreqOut.FieldKeyDown += FreqOut_FieldKeyDown;
            // Typing sound and keyboard sounds applied later in PowerOn
            // after CurrentAudioConfig is loaded from disk.
        }
    }

    /// <summary>
    /// Route FieldKeyDown events to the appropriate FreqOutHandler method
    /// based on the field key under the cursor.
    /// Modern mode uses simplified Freq handler with coarse/fine tuning.
    /// </summary>
    private void FreqOut_FieldKeyDown(FrequencyDisplay.DisplayField field, System.Windows.Input.KeyEventArgs e)
    {
        if (_freqOutHandlers == null) return;

        // Modern mode: simplified routing for reduced field set
        if (ActiveUIMode == UIMode.Modern)
        {
            switch (field.Key)
            {
                case "Freq":
                    _freqOutHandlers.AdjustFreqModern(field, e);
                    break;
                case "Slice":
                    _freqOutHandlers.AdjustSlice(field, e);
                    break;
                case "SliceOps":
                    _freqOutHandlers.AdjustSliceOps(field, e);
                    break;
                case "SMeter":
                    _freqOutHandlers.AdjustSMeter(field, e);
                    break;
            }
            return;
        }

        // Classic mode: full field routing
        switch (field.Key)
        {
            case "Freq":
                _freqOutHandlers.AdjustFreq(field, e);
                break;
            case "Split":
                _freqOutHandlers.AdjustSplit(field, e);
                break;
            case "RIT":
                _freqOutHandlers.AdjustRit(field, e);
                break;
            case "XIT":
                _freqOutHandlers.AdjustXit(field, e);
                break;
            case "VOX":
                _freqOutHandlers.AdjustVox(field, e);
                break;
            case "SMeter":
                _freqOutHandlers.AdjustSMeter(field, e);
                break;
            case "Offset":
                _freqOutHandlers.AdjustOffset(field, e);
                break;
            case "Slice":
                _freqOutHandlers.AdjustSlice(field, e);
                break;
            case "SliceOps":
                _freqOutHandlers.AdjustSliceOps(field, e);
                break;
            case "Mute":
                _freqOutHandlers.AdjustMute(field, e);
                break;
            case "Volume":
                _freqOutHandlers.AdjustVolume(field, e);
                break;
        }
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
            // Frequency — skip writing during tuning speech suppression window to avoid
            // double-speaking (ShowFrequency and SpeakTuningDebounced both announce freq)
            ulong freq = RigControl.Transmit
                ? RigControl.TXFrequency
                : RigControl.VirtualRXFrequency;
            bool suppressFreqWrite = _freqOutHandlers != null &&
                DateTime.UtcNow < _freqOutHandlers.TuningSpeechUntil;
            if (freq > 0 && !suppressFreqWrite)
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

            // Slice indicator — shows current active slice number
            FreqOut.Write("Slice", RigControl.ActiveSliceLetter);

            // Dynamic label for SliceOps — "Slice A Operations: Volume" etc.
            FreqOut.SetFieldLabel("SliceOps", $"Slice {RigControl.ActiveSliceLetter} Operations: Volume");

            // Mute — current active slice mute state (GetVFOAudio true = audio on = not muted)
            FreqOut.Write("Mute", RigControl.GetVFOAudio(RigControl.RXVFO) ? " " : "M");

            // Volume — current active slice gain (0-100)
            int vol = RigControl.GetVFOGain(RigControl.RXVFO);
            FreqOut.Write("Volume", vol.ToString());

            // SliceOps — shows volume level for the slice audio field
            FreqOut.Write("SliceOps", vol.ToString());

            // Split
            bool isSplit = _freqOutHandlers?.GetSplitVFOs?.Invoke() == true;
            FreqOut.Write("Split", isSplit ? "S" : " ");

            // VOX
            FreqOut.Write("VOX", RigControl.Vox == FlexBase.OffOnValues.on ? "V" : " ");

            // Offset
            FreqOut.Write("Offset", RigControl.OffsetDirection switch
            {
                FlexBase.OffsetDirections.plus => "+",
                FlexBase.OffsetDirections.minus => "-",
                _ => " "
            });

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

            // Update title bar with compact status for Insert+T (screen reader reads window title)
            if (UpdateTitleBar != null)
            {
                string sliceLetter = RigControl.ActiveSliceLetter;
                string mode = RigControl.Mode ?? "";
                double freqMhz = (RigControl.Transmit ? RigControl.TXFrequency : RigControl.VirtualRXFrequency) / 1_000_000.0;
                UpdateTitleBar($"JJ Flexible Radio Access — Slice {sliceLetter}, {freqMhz:F3}, {mode}");
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
        _enableDisableControls.Remove(TuneToggleButton);
        _enableDisableControls.Remove(AntennaTuneButton);
        if (RigControl.MyCaps.HasCap(RigCaps.Caps.ManualTransmit))
        {
            _enableDisableControls.Add(TransmitButton);
            _enableDisableControls.Add(TuneToggleButton);
            _enableDisableControls.Add(AntennaTuneButton);
            TransmitButton.Visibility = Visibility.Visible;
            TuneToggleButton.Visibility = Visibility.Visible;
        }
        else
        {
            TransmitButton.Visibility = Visibility.Collapsed;
            TuneToggleButton.Visibility = Visibility.Collapsed;
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

        Radios.ScreenReaderOutput.Speak(msg, VerbosityLevel.Critical, true);

        if (ShowErrorCallback != null)
            ShowErrorCallback(msg, "Error");
        else
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
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => TransmitChangeHandler(sender, transmit));
            return;
        }

        Tracing.TraceLine($"TransmitChange: {transmit}", TraceLevel.Info);

        // Update Transmit button visual
        TransmitButton.Content = transmit ? "TX On" : "Transmit";

        // If TX turned off externally (CAT, SmartSDR), sync PTT controller to Idle
        if (!transmit && _pttController != null && _pttController.IsTransmitting)
        {
            _pttController.EscapeUnlock();
        }
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

        // Audio narrative for ATU tune cycle.
        // Progress earcons only for automatic ATU operations (the radio is doing its
        // own thing and the operator needs to know when it finishes).
        // Manual tune (Ctrl+Shift+T) never gets progress beeps — the operator controls
        // the carrier and uses meter tones/speech to monitor SWR, power, etc.
        bool isAutoATU = e.Type == "auto" && RigControl.HasATU;

        switch (e.Status)
        {
            case "InProgress":
                if (isAutoATU)
                {
                    EarconPlayer.StartATUProgressEarcon();
                    StartATUTimeout();
                }
                break;
            case "OK":
            case "Successful":
                StopATUTimeout();
                EarconPlayer.StopATUProgressEarcon();
                if (isAutoATU)
                    EarconPlayer.ATUSuccessTone();
                // SWR readback is useful for both auto and manual tune
                Radios.ScreenReaderOutput.Speak($"SWR {e.SWR}", VerbosityLevel.Terse);
                break;
            case "Fail":
            case "FailBypass":
                StopATUTimeout();
                EarconPlayer.StopATUProgressEarcon();
                if (isAutoATU)
                {
                    EarconPlayer.ATUFailTone();
                    Radios.ScreenReaderOutput.Speak($"Tune failed, SWR {e.SWR}", VerbosityLevel.Critical);
                }
                break;
            case "Bypass":
            case "ManualBypass":
                StopATUTimeout();
                EarconPlayer.StopATUProgressEarcon();
                if (isAutoATU)
                    Radios.ScreenReaderOutput.Speak("ATU bypassed", VerbosityLevel.Terse);
                break;
            case "NotStarted":
            case "Aborted":
                StopATUTimeout();
                EarconPlayer.StopATUProgressEarcon();
                break;
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
            Radios.ScreenReaderOutput.Speak("The radio disconnected", VerbosityLevel.Critical, true);

            if (ShowErrorCallback != null)
                ShowErrorCallback("The radio disconnected", "Error");
            else
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

        // Wire panadapter braille display
        WirePanDisplay();

        // Initialize screen fields panel (Sprint 14)
        if (RigControl != null)
        {
            FieldsPanel.Initialize(RigControl);
            _brailleEngine.SetRig(RigControl);
        }

        _radioPowerOn = true;
        StatusText.Text = "Radio connected — power on";

        // Initialize PTT safety controller (Sprint 15)
        if (RigControl != null && OpenParms != null)
        {
            CurrentPttConfig = PttConfig.Load(
                OpenParms.ConfigDirectory,
                OpenParms.GetOperatorName());
            _pttController = new PttSafetyController(
                () => RigControl,
                () => _radioPowerOn,
                CurrentPttConfig,
                text => StatusTx.Text = text);

            // Wire license-aware TX lockout (Sprint 17 Track C)
            _pttController.CanTransmitHereCheck = () =>
                _freqOutHandlers?.CanTransmitHere() ?? true;

            // Apply band memory and frequency units settings from config
            if (_freqOutHandlers != null)
            {
                _freqOutHandlers.BandMemoryEnabled = CurrentPttConfig.BandMemoryEnabled;
                _freqOutHandlers.FrequencyUnits = CurrentPttConfig.FrequencyDisplayUnits;
            }
        }

        // Load audio config and initialize meter tones
        if (OpenParms != null)
        {
            CurrentAudioConfig = AudioOutputConfig.Load(OpenParms.ConfigDirectory);
            MeterToneEngine.Initialize();
            CurrentAudioConfig.Apply();
            if (RigControl != null)
                MeterToneEngine.AttachToRadio(RigControl);

            // Apply braille config
            _brailleEngine.Enabled = CurrentAudioConfig.BrailleEnabled;
            _brailleEngine.CellCount = CurrentAudioConfig.BrailleCellCount;
            _brailleEngine.EnabledFields = (BrailleFields)CurrentAudioConfig.BrailleFields;
            _brailleEngine.UpdateTimerState();

            // Apply panadapter visibility — Collapsed removes the control from layout
            // AND the tab order, so users who don't use the waterfall aren't forced to
            // Tab through it. Pan callback suppresses Tolk.Braille when hidden too.
            ApplyPanadapterVisibility();

            // Apply CW notification config
            _morseNotifier.SidetoneHz = Math.Clamp(CurrentAudioConfig.CwSidetoneHz, 400, 1200);
            _morseNotifier.SpeedWpm = Math.Clamp(CurrentAudioConfig.CwSpeedWpm, 10, 30);
            Radios.ScreenReaderOutput.CwNotificationsEnabled = CurrentAudioConfig.CwNotificationsEnabled;
            Radios.ScreenReaderOutput.CwModeAnnounceEnabled = CurrentAudioConfig.CwModeAnnounce;
            Radios.ScreenReaderOutput.PlayCwAS = () => _morseNotifier.PlayAS();
            Radios.ScreenReaderOutput.PlayCwBT = () => _morseNotifier.PlayBT();
            Radios.ScreenReaderOutput.PlayCwSK = () => _morseNotifier.PlaySK();
            Radios.ScreenReaderOutput.PlayCwMode = (mode) => _morseNotifier.PlayString(mode);

            // MultiFlex client connect/disconnect earcons
            Radios.ScreenReaderOutput.PlayClientConnectedEarcon = () => EarconPlayer.PlayChirp(600, 900, 120, 0.2f);
            Radios.ScreenReaderOutput.PlayClientDisconnectedEarcon = () => EarconPlayer.PlayChirp(900, 600, 120, 0.2f);

            // Apply typing sound to FreqOutHandlers
            if (_freqOutHandlers != null)
            {
                _freqOutHandlers.TypingSound = CurrentAudioConfig.TypingSound;
                // Pre-load keyboard sounds if mechanical mode is unlocked
                if (!EarconPlayer.HasKeyboardSounds &&
                    FreqOutHandlers.IsCalibrationUnlocked(CalibrationEngine.Ref2, CurrentAudioConfig.TuningHash))
                {
                    CalibrationEngine.LoadKeyboardSounds();
                }
            }
        }

        // Initialize band tracking from current frequency so first tune doesn't
        // trigger a false "Entering extra phone" boundary notification
        if (_freqOutHandlers != null && RigControl != null)
        {
            ulong freq = RigControl.VirtualRXFrequency;
            if (freq > 0) _freqOutHandlers.InitializeBandTracking(freq);
        }

        // VB-side tasks (knob setup, tracing)
        PowerOnCallback?.Invoke();

        // Sprint 22 Phase 8: Announce radio status after connect
        SpeakConnectStatus();
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

        // Dispose PTT safety controller (Sprint 15) — stops TX if active
        _pttController?.Dispose();
        _pttController = null;

        // Detach screen fields panel (Sprint 14)
        FieldsPanel.Detach();

        if (!_isClosing)
        {
            FreqOut.Clear();
            WriteStatus("Power", "Off");
            EnableDisableWindowControls(false);
            StatusText.Text = "Radio connected — power off";
        }
    }

    // ── Panadapter Braille Display — Sprint 12 Phase 12.10 ──────

    /// <summary>
    /// Wire the panadapter braille display callback from WpfFilterAdapter.
    /// Called during PowerNowOn after the radio and filter adapter are set up.
    /// </summary>
    private void WirePanDisplay()
    {
        if (RigControl?.FilterControl is not WpfFilterAdapter adapter) return;

        // Wire pan display callback — updates PanDisplayBox with braille text
        adapter.PanDisplayCallback = (line, pos) =>
        {
            if (_isClosing) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing) return;
                PanDisplayBox.Text = line;
                // Only snap the caret to current-freq position when the user
                // is NOT focused here — while focused, the user owns the caret
                // and pan refreshes must not move it out from under them.
                // Combined with the focus-transition guard in PanNavTimer_Tick,
                // this keeps the radio from drifting on passive pan updates.
                if (!PanDisplayBox.IsKeyboardFocused && pos >= 0 && pos < line.Length)
                    PanDisplayBox.SelectionStart = pos;

                // Respect user's Show-panadapter preference. When hidden, the callback
                // still refreshes the backing Text (harmless on a Collapsed control) so
                // re-enabling is instant — but we skip auto-showing the panel and
                // suppress the braille push so we don't spam a braille display the user
                // isn't looking at.
                bool showPan = CurrentAudioConfig?.ShowPanadapter ?? true;
                if (showPan && PanadapterPanel.Visibility != Visibility.Visible)
                    PanadapterPanel.Visibility = Visibility.Visible;

                // Send to braille display if available and panadapter is shown
                if (showPan && Radios.ScreenReaderOutput.HasBraille)
                    Radios.Tolk.Braille(line);
            });
        };

        // Wire segment display callback for low/high frequency labels
        if (adapter.PanManager != null)
        {
            adapter.PanManager.SegmentDisplayCallback = (lowText, highText) =>
            {
                if (_isClosing) return;
                Dispatcher.BeginInvoke(() =>
                {
                    PanLowFreq.Text = lowText;
                    PanHighFreq.Text = highText;
                });
            };
        }

        Tracing.TraceLine("WirePanDisplay: callbacks connected", TraceLevel.Info);
    }

    /// <summary>
    /// Timer for pan navigation — tunes the radio to the frequency under the
    /// cursor when the user moves the caret with Left/Right. Only fires a
    /// tune when the cursor has actually moved since focus entered — prevents
    /// Tab/Shift+Tab focus transitions from mutating radio state (the caret
    /// is sticky across focus changes and without this guard a focus event
    /// alone would make the radio jump to wherever the caret happened to be).
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _panNavTimer;
    private int _panNavLastCursorPos = -1;

    private void PanDisplayBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (RigControl?.FilterControl is not WpfFilterAdapter adapter) return;
        if (adapter.PanManager == null) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.PageUp:
            case Key.PageDown:
                adapter.PanManager.checkForRangeJump(
                    key == Key.PageUp ? (int)System.Windows.Forms.Keys.PageUp
                                      : (int)System.Windows.Forms.Keys.PageDown);
                e.Handled = true;
                break;
            case Key.Left:
            case Key.Right:
                // Allow normal cursor movement, then tune after a brief pause
                // The pan nav timer handles tuning to frequency under cursor
                break;
        }
    }

    private void PanDisplayBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Seed the move-detection baseline with the current caret position so
        // the first timer tick doesn't interpret "focus entered" as "user
        // moved cursor" — without this, Tab-in would fire gotoFreq on the
        // stale SelectionStart and jerk the radio to wherever the caret was
        // last left (sometimes hundreds of kHz from the actual slice freq).
        _panNavLastCursorPos = PanDisplayBox.SelectionStart;

        if (_panNavTimer == null)
        {
            _panNavTimer = new System.Windows.Threading.DispatcherTimer();
            _panNavTimer.Interval = TimeSpan.FromMilliseconds(200);
            _panNavTimer.Tick += PanNavTimer_Tick;
        }
        _panNavTimer.Start();
    }

    private void PanDisplayBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _panNavTimer?.Stop();
        _panNavLastCursorPos = -1;
    }

    private void PanNavTimer_Tick(object? sender, EventArgs e)
    {
        if (RigControl?.FilterControl is not WpfFilterAdapter adapter) return;
        if (adapter.PanManager?.CurrentPanData == null) return;

        int cursorPos = PanDisplayBox.SelectionStart;
        // Only tune when the cursor has actually moved. Focus alone should
        // never cause a frequency change — see field doc comment above.
        if (cursorPos == _panNavLastCursorPos) return;
        _panNavLastCursorPos = cursorPos;

        var panData = adapter.PanManager.CurrentPanData;
        if (cursorPos >= 0 && cursorPos < panData.frequencies.Length)
        {
            double freq = panData.frequencies[cursorPos];
            if (freq > 0)
                adapter.PanManager.gotoFreq(freq);
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
    /// Toggle transmit state. Routes through PTT safety controller when available.
    /// Replaces Form1.toggleTransmit().
    /// </summary>
    private void ToggleTransmit()
    {
        if (!_radioPowerOn || RigControl == null)
        {
            Tracing.TraceLine("toggleTransmit: no power", TraceLevel.Error);
            return;
        }

        if (_pttController != null)
        {
            // Route through PTT safety controller for timeout/warning tracking
            _pttController.ToggleLock();
        }
        else
        {
            // Fallback — direct TX toggle (no safety controller)
            Tracing.TraceLine($"toggling transmit: {RigControl.Transmit}", TraceLevel.Info);
            RigControl.Transmit = !RigControl.Transmit;
        }
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

        // Show the dialog (owner set by JJFlexDialog base class)
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
    /// Tune toggle button — toggles TX tune carrier (Ctrl+Shift+T).
    /// </summary>
    private void TuneToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_radioPowerOn || RigControl == null) return;
        ToggleTuneCarrier();
    }

    /// <summary>
    /// Toggle the tune carrier on/off with audio feedback.
    /// Called from both the UI button and the Ctrl+Shift+T hotkey.
    /// Guards against key auto-repeat producing rapid on/off/on/off chirps.
    /// </summary>
    private long _lastTuneToggleTicks;
    public void ToggleTuneCarrier()
    {
        if (RigControl == null) return;

        // Debounce: ignore calls within 500ms (key auto-repeat protection)
        long now = Environment.TickCount64;
        if (now - _lastTuneToggleTicks < 500) return;
        _lastTuneToggleTicks = now;

        bool newState = !RigControl.TxTune;
        RigControl.TxTune = newState;
        TuneToggleButton.IsChecked = newState;
        if (newState)
        {
            EarconPlayer.TuneOnTone();
            MeterToneEngine.OnTuneStarted();
            Radios.ScreenReaderOutput.Speak("Tune on", VerbosityLevel.Terse, true);
        }
        else
        {
            // Stop ATU progress earcon in case auto-ATU started a tune cycle
            EarconPlayer.StopATUProgressEarcon();
            StopATUTimeout();
            EarconPlayer.TuneOffTone();
            MeterToneEngine.OnTuneStopped();
            Radios.ScreenReaderOutput.Speak("Tune off", VerbosityLevel.Terse, true);
        }
    }

    /// <summary>
    /// Toggle meters panel visibility and meter tones.
    /// Called from Ctrl+M hotkey.
    /// </summary>
    public void ToggleMetersPanel()
    {
        MetersPanel.Visibility = Visibility.Visible;
        MetersPanel.ToggleMeters();
    }

    /// <summary>
    /// Toggle a ScreenFields expander category by index.
    /// Called from KeyCommands.vb after Sprint 23 hotkey unification.
    /// 0=DSP, 1=Audio, 2=Receiver, 3=Transmission, 4=Antenna
    /// </summary>
    public void ToggleScreenFieldsCategory(int categoryIndex)
    {
        FieldsPanel.ToggleCategory(categoryIndex);
    }

    /// <summary>
    /// Start ATU tune cycle with audio feedback.
    /// Called from both the menu and the Ctrl+T hotkey.
    /// </summary>
    public void StartATUTuneCycle()
    {
        if (RigControl == null) return;
        if (!RigControl.MyCaps.HasCap(Radios.RigCaps.Caps.ATGet))
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak("No antenna tuner available", VerbosityLevel.Terse);
            return;
        }
        // ATU tune uses FlexTunerOn which handles auto/manual tuner logic
        _oldSwr = "";
        RigControl.FlexTunerOn = true;
        Radios.ScreenReaderOutput.Speak("ATU tuning", VerbosityLevel.Terse, true);
    }

    private void StartATUTimeout()
    {
        StopATUTimeout();
        _atuTuneTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _atuTuneTimer.Tick += (s, e) =>
        {
            StopATUTimeout();
            EarconPlayer.StopATUProgressEarcon();
            EarconPlayer.ATUFailTone();
            Radios.ScreenReaderOutput.Speak("ATU tune timed out", VerbosityLevel.Critical, true);
        };
        _atuTuneTimer.Start();
    }

    private void StopATUTimeout()
    {
        _atuTuneTimer?.Stop();
        _atuTuneTimer = null;
    }

    /// <summary>
    /// Transmit button — toggles PTT.
    /// </summary>
    private void TransmitButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleTransmit();
    }

    /// <summary>
    /// Show the action toolbar popup (Ctrl+Tab). Lightweight ListBox with common TX actions.
    /// Arrow keys navigate, Enter activates, Escape closes.
    /// </summary>
    private void ShowActionToolbar()
    {
        if (RigControl == null || !_radioPowerOn)
        {
            Radios.ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Critical, true);
            return;
        }

        var dlg = new JJFlexDialog
        {
            Title = "Actions",
            Width = 250,
            Height = 200,
            ShowInTaskbar = false,
            ResizeMode = System.Windows.ResizeMode.NoResize
        };

        var list = new System.Windows.Controls.ListBox
        {
            Margin = new System.Windows.Thickness(8),
        };
        System.Windows.Automation.AutomationProperties.SetName(list, "Actions");

        // Build action items based on radio state
        bool hasATU = RigControl.HasATU;
        bool canTx = RigControl.CanTransmit;
        bool isTx = RigControl.Transmit;

        if (hasATU)
            list.Items.Add("ATU Tune");
        if (canTx)
        {
            bool tuning = RigControl.TxTune;
            list.Items.Add(tuning ? "Stop Tune Carrier" : "Start Tune Carrier");
            list.Items.Add(isTx ? "Stop Transmit" : "Start Transmit");
        }
        list.Items.Add("Speak Status");
        list.Items.Add("Cancel");

        list.SelectedIndex = 0;
        dlg.Content = list;

        list.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && list.SelectedItem is string item)
            {
                dlg.Close();
                ExecuteActionToolbarItem(item);
                e.Handled = true;
            }
        };

        dlg.Loaded += (s, e) => list.Focus();
        dlg.ShowDialog();
    }

    private void ExecuteActionToolbarItem(string item)
    {
        if (RigControl == null) return;

        switch (item)
        {
            case "ATU Tune":
                RigControl.FlexTunerOn = !RigControl.FlexTunerOn;
                break;
            case "Start Tune Carrier":
            case "Stop Tune Carrier":
                ToggleTuneCarrier();
                break;
            case "Start Transmit":
            case "Stop Transmit":
                ToggleTransmit();
                break;
            case "Speak Status":
                SpeakStatusCallback?.Invoke();
                break;
        }
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

    #region Frequency Readback — Ctrl+Shift+F

    /// <summary>
    /// Speak the current frequency and active slice.
    /// Global hotkey — works in Classic, Modern, and Logging modes.
    /// </summary>
    public void SpeakFrequency()
    {
        if (RigControl == null || !_radioPowerOn || OpenParms?.FormatFreq == null)
        {
            Radios.ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Critical, true);
            return;
        }

        try
        {
            ulong freq = RigControl.Transmit
                ? RigControl.TXFrequency
                : RigControl.VirtualRXFrequency;
            string freqText = OpenParms.FormatFreq(freq);
            int slice = RigControl.RXVFO;
            string speech = $"Frequency {freqText}, slice {RigControl.VFOToLetter(slice)}";

            // In Modern mode, include the current tuning step and mode
            if (ActiveUIMode == UIMode.Modern && _freqOutHandlers != null)
            {
                string stepMode = _freqOutHandlers.IsCoarseMode ? "coarse" : "fine";
                speech += $", {stepMode} {FreqOutHandlers.FormatStepForSpeech(_freqOutHandlers.CurrentTuneStep)}";
            }

            Radios.ScreenReaderOutput.Speak(speech, VerbosityLevel.Terse, true);
        }
        catch
        {
            Radios.ScreenReaderOutput.Speak("Frequency unavailable", VerbosityLevel.Critical, true);
        }
    }

    /// <summary>
    /// Jump to a specific band. Delegates to FreqOutHandlers.BandJump().
    /// Called from KeyCommands band F-key handlers.
    /// </summary>
    public void BandJump(HamBands.Bands.BandNames band)
    {
        _freqOutHandlers?.BandJump(band);
    }

    /// <summary>
    /// Navigate to next (+1) or previous (-1) band.
    /// Called from KeyCommands BandUp/BandDown handlers.
    /// </summary>
    public void BandNavigate(int direction)
    {
        _freqOutHandlers?.BandNavigate(direction);
    }

    #region 60m Channel Navigation — Sprint 22 Phase 10

    private int _sixtyMeterChannelIndex;

    /// <summary>
    /// Navigate 60m channels: cycles through Channel 1-5 + Digi Segment.
    /// Alt+Shift+Up/Down parallels Alt+Up/Down for band navigation.
    /// </summary>
    public void SixtyMeterChannelNavigate(int direction)
    {
        if (RigControl == null || !_radioPowerOn) return;

        string country = _freqOutHandlers?.License?.Country ?? "US";
        var alloc = SixtyMeterChannels.GetAllocation(country);
        if (alloc == null)
        {
            ScreenReaderOutput.Speak("No 60 meter channels configured for this country", VerbosityLevel.Terse);
            return;
        }

        int stopCount = alloc.Value.Channels.Length + (alloc.Value.Digi != null ? 1 : 0);
        if (stopCount == 0) return;

        _sixtyMeterChannelIndex = (_sixtyMeterChannelIndex + direction + stopCount) % stopCount;

        if (_sixtyMeterChannelIndex < alloc.Value.Channels.Length)
        {
            // Channelized frequency
            var ch = alloc.Value.Channels[_sixtyMeterChannelIndex];
            ulong freqHz = (ulong)(ch.FrequencyMHz * 1_000_000.0 + 0.5);
            RigControl.Frequency = freqHz;
            RigControl.Mode = ch.Mode;
            ScreenReaderOutput.Speak($"{ch.Label}, {ch.FrequencyMHz:F4} megahertz, {ch.Mode}", VerbosityLevel.Terse);
        }
        else if (alloc.Value.Digi is { } digi)
        {
            // Digital segment — tune to start
            ulong freqHz = (ulong)(digi.StartMHz * 1_000_000.0 + 0.5);
            RigControl.Frequency = freqHz;
            ScreenReaderOutput.Speak($"60 meter digital and CW segment, {digi.StartMHz:F4} megahertz", VerbosityLevel.Terse);
        }
    }

    #endregion

    /// <summary>
    /// Common mode cycle list for F10/F11 hotkeys.
    /// Subset of RigCaps.ModeTable — just the frequently used modes.
    /// </summary>
    private static readonly string[] CommonModes = { "USB", "LSB", "CW", "DIGU", "DIGL", "AM", "FM" };

    /// <summary>
    /// Cycle to the next (+1) or previous (-1) mode in the common mode list.
    /// Called from KeyCommands mode handlers (F10/F11).
    /// </summary>
    public void CycleMode(int direction)
    {
        if (RigControl == null || !_radioPowerOn) return;

        string currentMode = RigControl.Mode ?? "USB";
        int idx = Array.IndexOf(CommonModes, currentMode);

        if (idx < 0)
        {
            // Current mode not in common list — jump to first or last
            idx = direction > 0 ? CommonModes.Length - 1 : 0;
        }

        int next = (idx + direction + CommonModes.Length) % CommonModes.Length;
        string newMode = CommonModes[next];
        RigControl.Mode = newMode;
        Radios.ScreenReaderOutput.Speak(newMode, VerbosityLevel.Terse, true);
    }

    /// <summary>
    /// Jump directly to a specific mode.
    /// Called from KeyCommands direct mode handlers (Alt+U, Alt+L, Alt+C).
    /// </summary>
    public void SetMode(string mode)
    {
        if (RigControl == null || !_radioPowerOn) return;

        string currentMode = RigControl.Mode ?? "";
        if (string.Equals(currentMode, mode, StringComparison.OrdinalIgnoreCase))
        {
            Radios.ScreenReaderOutput.Speak($"Already {mode}", VerbosityLevel.Terse, true);
            return;
        }
        RigControl.Mode = mode;
        Radios.ScreenReaderOutput.Speak(mode, VerbosityLevel.Terse, true);
    }

    #endregion

    #region SmartLink & Auto-Connect Management

    /// <summary>
    /// Show the SmartLink Account Manager dialog.
    /// Works without a radio connection — manages saved accounts (view, rename, delete).
    /// </summary>
    public void ShowSmartLinkAccountManager()
    {
        var mgr = new Radios.SmartLinkAccountManager();
        mgr.LoadAccounts();

        while (true)
        {
            var defaultEmail = GetDefaultSmartLinkEmail?.Invoke() ?? "";
            var callbacks = new Dialogs.SmartLinkAccountCallbacks
            {
                GetAccounts = () => mgr.Accounts.OrderByDescending(a => a.LastUsed)
                    .Select(a => new Dialogs.SmartLinkAccountInfo
                    {
                        FriendlyName = a.FriendlyName,
                        Email = a.Email,
                        LastUsed = a.LastUsed,
                        AccountData = a,
                        IsDefault = a.Email.Equals(defaultEmail, StringComparison.OrdinalIgnoreCase)
                    }).ToList(),
                RenameAccount = (oldName, newName) => mgr.RenameAccount(oldName, newName),
                DeleteAccount = (name) => { mgr.DeleteAccount(name); },
                ScreenReaderSpeak = (msg, interrupt) => Radios.ScreenReaderOutput.Speak(msg, interrupt)
            };

            var dialog = new Dialogs.SmartLinkAccountDialog(callbacks);
            var result = dialog.ShowDialog();

            if (result != true)
                break;

            if (dialog.NewLoginRequested)
            {
                // Launch Auth0 PKCE flow via WPF AuthDialog
                Radios.ScreenReaderOutput.Speak("Opening SmartLink login", VerbosityLevel.Terse, true);
                var authDialog = new Dialogs.AuthDialog(
                    trace: (msg, level) => JJTrace.Tracing.TraceLine(msg, (System.Diagnostics.TraceLevel)level),
                    screenReaderSpeak: (msg, interrupt) => Radios.ScreenReaderOutput.Speak(msg, interrupt));
                authDialog.ForceNewLogin = true;

                if (authDialog.ShowDialog() == true && !string.IsNullOrEmpty(authDialog.IdToken))
                {
                    // Determine friendly name from email or prompt
                    var friendlyName = !string.IsNullOrEmpty(authDialog.Email)
                        ? authDialog.Email
                        : "SmartLink Account";

                    var newAccount = new Radios.SmartLinkAccount
                    {
                        FriendlyName = friendlyName,
                        Email = authDialog.Email,
                        IdToken = authDialog.IdToken,
                        RefreshToken = authDialog.RefreshToken,
                        ExpiresAt = DateTime.UtcNow.AddSeconds(authDialog.ExpiresIn),
                        LastUsed = DateTime.UtcNow
                    };

                    mgr.SaveAccount(newAccount);
                    Radios.ScreenReaderOutput.Speak($"Account saved for {friendlyName}", VerbosityLevel.Terse, true);

                    // Loop back to show the account list with the new account
                    continue;
                }
                else
                {
                    Radios.ScreenReaderOutput.Speak("Login cancelled", VerbosityLevel.Terse, true);
                    // Loop back to show account list
                    continue;
                }
            }

            // User selected an existing account — save as default
            Tracing.TraceLine($"ShowSmartLinkAccountManager: result={result}, SelectedAccountData={dialog.SelectedAccountData?.GetType()?.Name ?? "null"}, NewLogin={dialog.NewLoginRequested}", TraceLevel.Info);
            if (dialog.SelectedAccountData is Radios.SmartLinkAccount selectedAcct)
            {
                SaveDefaultSmartLinkAccount?.Invoke(selectedAcct.Email);
                // Speech gets swallowed by focus changes — use Tolk directly with a delay
                System.Threading.Tasks.Task.Delay(200).ContinueWith(_ =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        Radios.ScreenReaderOutput.Speak($"Default account set to {selectedAcct.FriendlyName}", VerbosityLevel.Critical, true);
                    });
                });
            }
            break;
        }
    }

    /// <summary>Show the MultiFlex client management dialog.</summary>
    public void ShowMultiFlexDialog()
    {
        if (RigControl == null || !_radioPowerOn)
        {
            Radios.ScreenReaderOutput.Speak("MultiFlex requires an active radio connection", VerbosityLevel.Critical, true);
            return;
        }

        var rig = RigControl;

        var callbacks = new Dialogs.MultiFlexCallbacks
        {
            GetClients = () =>
            {
                return rig.GetGuiClients().Select(gc => new Dialogs.MultiFlexClientInfo
                {
                    Program = gc.program,
                    Station = gc.station,
                    Handle = gc.handle,
                    IsThisClient = gc.isThisClient,
                    OwnedSlices = gc.slices
                }).ToList();
            },
            DisconnectClient = (handle) => rig.DisconnectGuiClient(handle)
        };

        var dialog = new Dialogs.MultiFlexDialog(callbacks);
        dialog.ShowDialog();
    }

    // --- Auto-Connect callbacks (wired from ApplicationEvents.vb) ---

    /// <summary>Returns whether auto-connect is globally enabled.</summary>
    public Func<bool>? IsAutoConnectEnabled { get; set; }

    /// <summary>Returns the configured auto-connect radio name, or null if none.</summary>
    public Func<string?>? GetAutoConnectRadioName { get; set; }

    /// <summary>Sets the global auto-connect enabled flag and saves.</summary>
    public Action<bool>? SetAutoConnectEnabled { get; set; }

    /// <summary>Clears the auto-connect radio config and saves.</summary>
    public Action? ClearAutoConnectRadio { get; set; }

    /// <summary>
    /// Toggle the global auto-connect enabled flag.
    /// Returns speech message for caller to announce after menu closes.
    /// </summary>
    public string? ToggleAutoConnect()
    {
        if (IsAutoConnectEnabled == null || SetAutoConnectEnabled == null) return null;

        bool newState = !IsAutoConnectEnabled();
        SetAutoConnectEnabled(newState);
        return newState ? "Auto-connect enabled" : "Auto-connect disabled";
    }

    /// <summary>
    /// Clear the auto-connect radio configuration.
    /// Returns speech message for caller to announce after menu closes.
    /// </summary>
    public string? ClearAutoConnect()
    {
        if (ClearAutoConnectRadio == null) return null;

        string? radioName = GetAutoConnectRadioName?.Invoke();
        if (string.IsNullOrEmpty(radioName))
        {
            return "No auto-connect radio configured";
        }

        ClearAutoConnectRadio();
        return $"Auto-connect to {radioName} cleared";
    }

    #endregion

    #region Form1 Compatibility — Phase 9.1

    /// <summary>
    /// Display the key commands help dialog.
    /// Matches Form1.DisplayHelp().
    /// </summary>
    public void DisplayHelp()
    {
        // Context-sensitive help: check what has focus and show help dialog
        if (FreqOut.IsKeyboardFocusWithin)
        {
            var field = FreqOut.GetFocusedField();
            if (field?.HelpItems != null && field.HelpItems.Count > 0)
            {
                var dialog = new Dialogs.ShowHelpDialog
                {
                    Title = $"{field.Label ?? field.Key} Help",
                    HelpTitle = $"{field.Label ?? field.Key} Field",
                    HelpItems = field.HelpItems
                };
                dialog.ShowDialog();
                return;
            }
        }

        if (FieldsPanel.IsKeyboardFocusWithin)
        {
            var dialog = new Dialogs.ShowHelpDialog
            {
                Title = "Value Field Help",
                HelpText = "Value Field\r\n\r\n" +
                    "  Up / Down       Adjust value\r\n" +
                    "  Page Up / Down  Large step\r\n" +
                    "  Home            Set to minimum\r\n" +
                    "  End             Set to maximum\r\n" +
                    "  Enter           Type exact value\r\n" +
                    "  Escape          Cancel\r\n" +
                    "  Ctrl+Tab        Next category\r\n"
            };
            dialog.ShowDialog();
            return;
        }

        // Fall back to Command Finder
        ShowCommandFinder();
    }

    public void ShowCommandFinder()
    {
        var items = GetCommandFinderItemsCallback?.Invoke() ?? new List<Dialogs.CommandFinderItem>();
        var dialog = new Dialogs.CommandFinderDialog
        {
            GetCommands = () => items,
            ExecuteCommand = (tag) => ExecuteCommandCallback?.Invoke(tag),
            SpeakText = (msg) => Radios.ScreenReaderOutput.Speak(msg),
            CurrentMode = ActiveUIMode.ToString()
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Show the earcon scratchpad — easter egg triggered by typing "cqtest" in Ctrl+F.
    /// Mutes the radio while open so you can hear the sounds you're designing.
    /// </summary>
    /// <summary>
    /// Handle calibration reference entered via JJ Ctrl+F frequency input.
    /// Delegates to FreqOutHandlers if available, otherwise handles directly.
    /// </summary>
    public void HandleCalibrationFromFreqInput(string calibRef)
    {
        if (_freqOutHandlers != null)
        {
            // Use the existing handler which manages config, sounds, and speech
            _freqOutHandlers.HandleCalibrationPublic(calibRef);
        }
        else
        {
            // No handler — just play confirmation and speak
            CalibrationEngine.PlayVerificationTone(calibRef);
            Radios.ScreenReaderOutput.Speak("Calibration reference accepted", Radios.VerbosityLevel.Critical, true);
        }
    }

    public void ShowEarconScratchpad()
    {
        // Save and mute radio so earcon sounds are audible
        bool wasMuted = RigControl?.SliceMute ?? true;
        if (RigControl != null && !wasMuted)
            RigControl.SliceMute = true;

        try
        {
            var dialog = new Dialogs.EarconScratchpadDialog();
            dialog.ShowDialog();
        }
        finally
        {
            // Restore previous mute state
            if (RigControl != null && !wasMuted)
                RigControl.SliceMute = false;
        }
    }

    /// <summary>
    /// Bring the main window to front and focus the frequency display.
    /// Matches Form1.gotoHome().
    /// </summary>
    public void gotoHome()
    {
        FreqOut.FocusDisplay();
    }

    /// <summary>
    /// Callback to rebuild the native menu bar after radio connects.
    /// Set by ShellForm after creating NativeMenuBar.
    /// </summary>
    public Action? RebuildMenuCallback { get; set; }

    /// <summary>
    /// Rebuild the ScreenFields/Operations menus with live DSP controls.
    /// Called from PowerNowOn after the radio is ready.
    /// </summary>
    public void SetupOperationsMenu()
    {
        Tracing.TraceLine("MainWindow.SetupOperationsMenu: rebuilding menus", TraceLevel.Info);
        RebuildMenuCallback?.Invoke();
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
    /// Focuses this control; the parent ShellForm handles window activation.
    /// </summary>
    public new void BringToFront()
    {
        Focus();
    }

    #endregion
}
