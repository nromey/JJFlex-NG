using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JJFlexWpf.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Audio Workshop: non-modal WPF dialog for TX audio sculpting, live meters,
/// and earcon exploration. Three tabs with real-time feedback.
/// </summary>
public partial class AudioWorkshopDialog : JJFlexDialog
{
    private FlexBase? _rig;
    private bool _polling;
    private readonly DispatcherTimer _meterTimer;

    // Singleton instance for non-modal Show()
    private static AudioWorkshopDialog? _instance;

    // ── Audio Check session (QB Track G, 2026-08-07) ──

    /// <summary>
    /// Live path to the PTT safety controller. Set by MainWindow when the
    /// controller is created; resolved per use because the controller is
    /// recreated on operator switch and nulled at power-off. EVERY keying
    /// path in this dialog rides the controller — the warning ladder, the
    /// license lockout, and the 15-minute hard kill all apply unchanged.
    /// Never set rig.Transmit directly from here.
    /// </summary>
    public static Func<PttSafetyController?>? PttControllerSource { get; set; }

    private AudioCheckSession? _session;
    private TextBox? _micReadingBox;
    private Button? _startCheckButton;
    private CycleFieldControl? _listenMethodControl;
    private CycleFieldControl? _checkPowerControl;
    private ValueFieldControl? _checkWattsControl;
    private Button? _playTakeButton;
    private Button? _loopbackButton;
    private TextBlock? _loopbackInfo;
    private CycleFieldControl? _micSourceControl;
    private TextBlock? _monitorHeader;

    // ── Test tone (Audio Track C) ──

    /// <summary>
    /// Per-operator app settings store (AudioOutputConfig). Set by MainWindow
    /// when the config loads. The tone frequency/level/monitor persist here,
    /// NOT in the serial-keyed per-radio config — the frequency is an
    /// accessibility choice and hearing does not change when you switch rigs.
    /// </summary>
    public static Func<AudioOutputConfig?>? AudioConfigSource { get; set; }

    /// <summary>Immediate save of the app settings store. Set by MainWindow.</summary>
    public static Action? AudioConfigSave { get; set; }

    // ── This Computer section (2026-08-12) ──

    /// <summary>
    /// Opens the Audio Devices picker. Set by MainWindow, which forwards to
    /// the callback globals.vb owns. Resolved per call, never captured — the
    /// underlying callback is set during startup and this dialog can be
    /// constructed before that finishes.
    /// </summary>
    public static Action? OpenAudioDevices { get; set; }

    /// <summary>
    /// Full path to audioDevices.xml, so this dialog can NAME the chosen input
    /// device rather than offering a bare button. Set by MainWindow from the
    /// path globals.vb owns — a handoff, not a second place that knows how to
    /// build it, matching <see cref="MainWindow.AudioDevicesFilePath"/>.
    /// </summary>
    public static Func<string?>? AudioDevicesPath { get; set; }

    private TextBox? _deviceReadingBox;

    private CheckBox? _toneCheck;
    private CycleFieldControl? _tonePresetControl;
    private ValueFieldControl? _toneFreqControl;
    private ValueFieldControl? _toneLevelControl;
    private CheckBox? _toneMonitorCheck;
    private TextBlock? _toneInfo;
    private ContinuousToneSampleProvider? _toneMonitorProvider;
    private bool _toneMonitorSounding;
    private bool _toneOutsideWarned; // edge trigger: filter moved out from under an armed tone
    private static readonly int[] TonePresetHz = { 440, 700, 1000 }; // index 3 = Custom

    // Per-radio preferences (serial-keyed store). Loaded on SetRig.
    private RadioConfig? _radioCfg;
    private string _radioCfgSerial = "";

    #region TX Audio Controls

    private ValueFieldControl? _micGainControl;
    private CheckBox? _micBoostCheck;
    private CheckBox? _micBiasCheck;
    private CheckBox? _companderCheck;
    private ValueFieldControl? _companderLevelControl;
    private CheckBox? _processorCheck;
    private CycleFieldControl? _processorSettingControl;
    private ValueFieldControl? _txFilterLowControl;
    private ValueFieldControl? _txFilterHighControl;
    private TextBlock? _filterWidthLabel;
    private CheckBox? _monitorCheck;
    private ValueFieldControl? _monitorLevelControl;
    private ValueFieldControl? _monitorPanControl;

    #endregion

    #region Live Meter Labels

    private TextBlock? _sMeterLabel;
    private TextBlock? _fwdPowerLabel;
    private TextBlock? _swrLabel;
    private TextBlock? _alcLabel;      // TX drive, SW ALC
    private TextBlock? _ampAlcLabel;   // external-amplifier ALC (HWALC), for amp users
    private TextBlock? _micAudioLabel; // transmit mic audio, SC_MIC (honest for PC + analog)
    private TextBlock? _paTempLabel;
    private TextBlock? _voltsLabel;

    #endregion

    // Preset callbacks (wired from outside).
    //
    // STATIC as of 2026-08-12, and that is the whole bug fix. They were
    // instance properties that nothing ever assigned: the dialog is
    // constructed in two places (ShowOrFocus and Settings' Audio Workshop
    // button) and neither wired them, so Load always answered "No presets
    // available" — the three built-in defaults included — and Save captured a
    // valid preset, handed it to a null callback, and announced "Preset saved"
    // over the top of dropping it on the floor. Every other cross-boundary
    // hook in this dialog (PttControllerSource, AudioConfigSource) is static
    // and wired once in MainWindow, which is why those work; these were the
    // odd ones out. The null-conditional invoke is what let it fail silently.
    //
    // Save RETURNS A BOOL, and that is not decoration. As an Action it could
    // fail — MainWindow no-ops when there is no operator to own the file — and
    // the dialog had no way to know, so every caller announced success
    // regardless. That is the same lying receipt in a second costume: the
    // first version dropped presets because the callback was null, this one
    // would drop them because the callback declined, and both said "saved".
    // A save that did not happen must never be announced as one.
    public static Func<AudioChainPresets>? GetPresetsCallback { get; set; }
    public static Func<AudioChainPresets, bool>? SavePresetsCallback { get; set; }

    /// <summary>
    /// Persist the preset collection, reporting whether it actually landed.
    /// False when nothing is wired to save it or the store declined — never
    /// treat it as success. Speaks nothing itself: each caller knows what it
    /// was trying to do and says so in its own words.
    /// </summary>
    private static bool PersistPresets(AudioChainPresets presets)
    {
        return SavePresetsCallback?.Invoke(presets) ?? false;
    }

    /// <summary>
    /// What to say when a preset change could not be written. Names the cause
    /// rather than the symptom: with no operator loaded there is no per-operator
    /// file to write, and telling someone their preset "could not be saved" and
    /// stopping there gives them nothing to do about it.
    /// </summary>
    private const string PresetSaveFailed =
        "It could not be saved — there is no operator loaded, so there is no "
        + "place to keep it yet.";

    public AudioWorkshopDialog()
    {
        InitializeComponent();

        // Non-modal: show in taskbar, allow resize, independent of main window.
        // Clear Owner so Alt+Tab works properly — owned windows steal focus
        // from their owner in WinForms/WPF interop.
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanResize;
        new System.Windows.Interop.WindowInteropHelper(this).Owner = IntPtr.Zero;

        BuildTxAudioTab();
        ApplyTxAudioTabOrder();
        BuildLiveMetersTab();
        BuildEarconExplorerTab();

        // Meter poll timer at ~2 Hz
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _meterTimer.Tick += MeterTimer_Tick;

        Closed += (s, e) =>
        {
            // A session must never outlive its dialog: unkey (through the
            // controller), restore every changed state, stop playback.
            _session?.ForceEnd("Audio check ended");
            _session = null;
            // The test tone must never outlive its dialog either — closing
            // the workshop restores the microphone (the tone is armed only
            // while the workshop is open, and arming is never persisted).
            DisarmTone(speak: false);
            _meterTimer.Stop();
            _instance = null;
        };
    }

    /// <summary>
    /// Two-stage Escape while an Audio Check is transmitting: the first press
    /// unkeys ("Transmit off") and STAYS in the dialog; the second press
    /// closes it. Escape never leaves you transmitting — this extends the
    /// house Escape rule rather than bending it. Class handler runs before
    /// JJFlexDialog's instance handler, so we can consume the first press.
    ///
    /// Workshop-local document keys (Noel, 2026-08-11): Ctrl+S saves a
    /// preset, Ctrl+O loads one — standard document verbs, learnable
    /// because universal. Ctrl+S also fixes a live defect: Save Preset
    /// used to answer to its Alt+S button mnemonic, which (WPF access keys
    /// match with Shift held) shadowed the GLOBAL Alt+Shift+S Speak
    /// Transmit Status chord in the one dialog where an operator most
    /// needs to query their audio. Ctrl+Enter starts or stops the Audio
    /// Check from anywhere in the dialog, so the adjust-and-hear loop
    /// never requires hunting the button. All three are LOCAL to this
    /// dialog — none is a global binding — and each requires exactly the
    /// Ctrl modifier so chords like Ctrl+Shift+S pass through untouched.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _session != null && _session.EscapeStopsTransmit)
        {
            _session.StopCheck();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.S:
                    SavePreset_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.O:
                    LoadPreset_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.Enter:
                    ToggleAudioCheck();
                    e.Handled = true;
                    return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// Initial focus lands on Start Audio Check (Noel, 2026-08-11): set up,
    /// or just loaded a profile? Press Enter and you are running — zero
    /// navigation for the common case. The tab ring then runs Start,
    /// mic reading, Mic Gain (Threads Track, 2026-08-12): during a check
    /// focus sits on Mic Gain, so the reading is one Shift+Tab back and
    /// the Stop button one more. Falls back to the base first-control
    /// behaviour when the workshop opens on another tab, where the button
    /// isn't visible to take focus.
    ///
    /// Exception added 2026-08-12 with the walk-through reorder: when no
    /// input device has been chosen on this computer, focus lands on step
    /// one instead. Those are the only two states worth optimising for and
    /// they do not overlap — an operator with no microphone selected cannot
    /// run a meaningful check, and landing them on a button that keys the
    /// transmitter is the least useful thing this dialog could do. Everyone
    /// else gets the express lane, unchanged. This is deliberately NOT a
    /// preference: it reads the one fact that distinguishes the two cases.
    /// </summary>
    protected override void FocusFirstControl()
    {
        if (MainTabs.SelectedIndex == 0)
        {
            if (NoInputDeviceChosen() && _deviceReadingBox != null
                && _deviceReadingBox.Focus())
                return;
            if (_startCheckButton != null && _startCheckButton.Focus())
                return;
        }
        base.FocusFirstControl();
    }

    /// <summary>
    /// Explicit tab order for the TX Audio tab (Threads Track, 2026-08-12):
    /// Start Audio Check first, the live mic reading second, Mic Gain third,
    /// then every remaining control in build order. Mic Gain stays put
    /// VISUALLY (it belongs to the Microphone section) but joins the check
    /// cluster in the ring, because a running check is an adjust-and-listen
    /// loop between exactly these three stops: forward tab does things,
    /// backward tab inspects what just happened.
    ///
    /// Noel also asked about Ctrl+Tab section navigation. Deliberately NOT
    /// added: Ctrl+Tab already switches tabs in this window (standard WPF
    /// TabControl behaviour, documented in the help), and overloading it
    /// for section movement inside a tab would collide with that.
    /// </summary>
    private void ApplyTxAudioTabOrder()
    {
        int idx = 1;
        if (_startCheckButton != null) KeyboardNavigation.SetTabIndex(_startCheckButton, idx++);
        if (_micReadingBox != null) KeyboardNavigation.SetTabIndex(_micReadingBox, idx++);
        if (_micGainControl != null) KeyboardNavigation.SetTabIndex(_micGainControl, idx++);
        foreach (object child in TxAudioContent.Children)
        {
            if (child is not UIElement el) continue;
            if (ReferenceEquals(el, _startCheckButton) || ReferenceEquals(el, _micReadingBox)
                || ReferenceEquals(el, _micGainControl)) continue;
            KeyboardNavigation.SetTabIndex(el, idx++);
        }
    }

    /// <summary>
    /// Show or bring to front the singleton Audio Workshop dialog.
    /// </summary>
    public static void ShowOrFocus(FlexBase? rig, int tabIndex = 0)
    {
        if (_instance == null || !_instance.IsLoaded)
        {
            _instance = new AudioWorkshopDialog();
            _instance.SetRig(rig);
            _instance.Show();
            // Non-modal WPF windows in a WinForms app don't receive keyboard input
            // without this — the WinForms message loop doesn't route keys to WPF.
            System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_instance);
        }
        _instance.FocusTab(tabIndex);
        _instance.Activate();
    }

    public void SetRig(FlexBase? rig)
    {
        var oldRig = _rig;
        _rig = rig;
        if (rig != null)
        {
            LoadPerRadioPrefs();
            LoadToneSettings();
            // Reflect the new rig's actual tone state (a fresh rig is never
            // armed — arming does not survive a radio switch by design).
            SetToneCheckSilently(rig.TxToneEngaged);
            UpdateToneStatus(speakIfNewlyOutside: false);
            UpdateLoopbackAvailability();
            PollTxAudio();
            _meterTimer.Start();
        }
        else
        {
            // Radio gone: end any live session (nothing to restore on a dead
            // radio — the session skips rig writes when the rig is null).
            _session?.ForceEnd("Radio disconnected, audio check ended");
            _session = null;
            // And release the test tone (the generator died with the rig, but
            // the static key-down hook and local monitor are ours to clear).
            DisarmTone(speak: false, rig: oldRig);
            SetToneCheckSilently(false);
            // Clear a stale loopback arrangement flag on the departing rig so
            // a reconnect on the same FlexBase can arrange again. Writes are
            // internally guarded when the underlying radio is gone.
            if (oldRig != null && oldRig.LoopbackArranged)
                oldRig.EndLoopbackArrangement();
            _meterTimer.Stop();
            // The poll is dead now — leave the reading honest, not stale.
            UpdateMicReading();
        }
    }

    /// <summary>
    /// Command Finder path for "check my transmit audio": open (or focus) the
    /// workshop and start an Audio Check session immediately.
    /// </summary>
    public static void ShowOrFocusAndStartCheck(FlexBase? rig)
    {
        ShowOrFocus(rig, 0);
        _instance?.ToggleAudioCheck();
    }

    /// <summary>
    /// Load per-radio Audio Check preferences (listen method, low power) from
    /// the serial-keyed RadioConfig store and reflect them in the controls.
    /// </summary>
    private void LoadPerRadioPrefs()
    {
        if (_rig == null) return;
        string serial = _rig.SelectedRadioSerial;
        if (string.IsNullOrEmpty(serial)) return;
        if (_radioCfg != null && _radioCfgSerial == serial) return;

        _radioCfg = RadioConfig.LoadForRadio(serial);
        _radioCfgSerial = serial;

        if (_listenMethodControl != null)
        {
            _listenMethodControl.SuppressEvents = true;
            _listenMethodControl.SelectedIndex = (int)_radioCfg.AudioCheckListenMethod;
            _listenMethodControl.SuppressEvents = false;
        }
        if (_checkPowerControl != null)
        {
            _checkPowerControl.SuppressEvents = true;
            _checkPowerControl.SelectedIndex = (int)_radioCfg.AudioCheckPowerMode;
            _checkPowerControl.SuppressEvents = false;
        }
        if (_checkWattsControl != null)
        {
            _checkWattsControl.SuppressEvents = true;
            _checkWattsControl.Value = Math.Clamp(_radioCfg.AudioCheckLowPowerWatts, 1, 100);
            _checkWattsControl.SuppressEvents = false;
        }
        UpdateCheckWattsVisibility();
    }

    private void SavePerRadioPrefs()
    {
        if (_radioCfg == null || string.IsNullOrEmpty(_radioCfgSerial)) return;
        if (_listenMethodControl != null)
            _radioCfg.AudioCheckListenMethod = (AudioCheckListenMethods)_listenMethodControl.SelectedIndex;
        if (_checkPowerControl != null)
            _radioCfg.AudioCheckPowerMode = (AudioCheckPowerModes)_checkPowerControl.SelectedIndex;
        if (_checkWattsControl != null)
            _radioCfg.AudioCheckLowPowerWatts = _checkWattsControl.Value;
        _radioCfg.SaveForRadio(_radioCfgSerial);
    }

    /// <summary>
    /// The watts field only exists while Low power is the selected check
    /// mode — a collapsed control is out of the tab order (house rule for
    /// controls that currently do nothing).
    /// </summary>
    private void UpdateCheckWattsVisibility()
    {
        if (_checkWattsControl == null) return;
        bool lowPower = _checkPowerControl?.SelectedIndex == (int)AudioCheckPowerModes.LowPower;
        _checkWattsControl.Visibility = lowPower ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Radio teardown notification. The workshop is a non-modal singleton that
    /// outlives the radio: before this hook, nothing stopped the 2 Hz poll
    /// timer when the rig died, and the tick raced Disconnect() nulling
    /// theRadio (crash zip JJFlexError-20260807-153513, NRE in get_MicGain
    /// during app close). MainWindow's power-off path calls this; safe from
    /// any thread, no-op when the dialog isn't open.
    /// </summary>
    public static void NotifyRigGone()
    {
        var inst = _instance;
        if (inst == null) return;
        if (inst.Dispatcher.CheckAccess())
            inst.SetRig(null);
        else
            inst.Dispatcher.BeginInvoke(() => inst.SetRig(null));
    }

    public void FocusTab(int tabIndex)
    {
        if (tabIndex >= 0 && tabIndex < MainTabs.Items.Count)
            MainTabs.SelectedIndex = tabIndex;
    }

    #region Tab 1: TX Audio

    private void BuildTxAudioTab()
    {
        // Section order is the setup walk-through, running outward from this
        // computer to the radio to the air (Noel, 2026-08-12): choose a
        // microphone, tell the radio to listen to it, shape it, band-limit
        // it, decide how you hear yourself, then put a signal out — the test
        // tone first because it is a known quantity, then the keyed
        // end-to-end check.
        //
        // It ran the other way round until 2026-08-12: Audio Check stood at
        // the top and every control it proves came after it. That order
        // taught a first-time operator nothing, and the first thing in the
        // window keyed the transmitter. The express lane for an operator who
        // is already set up survives in focus and tab order rather than in
        // layout — see FocusFirstControl and ApplyTxAudioTabOrder.
        BuildDeviceSection();

        // Microphone section
        AddSectionHeader(TxAudioContent, "Microphone");

        // Mic source picker — the precondition for every honest measurement
        // this dialog makes. Verified live (2026-08-07): MicGain acts on the
        // SELECTED input; with a hand-mic PTT override the knob silently
        // tunes an idle PC stream. The picker reads and sets the radio's own
        // selection; jack-only controls annotate themselves when TX audio is
        // PC-sourced (see PollTxAudio).
        _micSourceControl = MakeCycle("Transmit audio from", new[] { "(waiting for radio)" });
        _micSourceControl.SelectionChanged += (s, idx) =>
        {
            if (_rig == null || _polling) return;
            string choice = _micSourceControl!.SelectedOption;
            if (!string.IsNullOrEmpty(choice) && choice[0] != '(')
                _rig.MicSource = choice;
        };
        TxAudioContent.Children.Add(_micSourceControl);

        _micGainControl = MakeValue("Mic Gain", 0, 100, 1);
        _micGainControl.ValueChanged += (s, v) =>
        {
            if (_rig != null && !_polling)
            {
                _rig.MicGain = v;
                ScreenReaderOutput.Speak($"Mic gain {v}", VerbosityLevel.Terse);
            }
        };
        TxAudioContent.Children.Add(_micGainControl);

        _micBoostCheck = MakeToggle("Mic Boost (+20 dB)");
        _micBoostCheck.Checked += (s, e) => SetToggle("Mic Boost", v => { if (_rig != null) _rig.MicBoost = v; }, true);
        _micBoostCheck.Unchecked += (s, e) => SetToggle("Mic Boost", v => { if (_rig != null) _rig.MicBoost = v; }, false);
        TxAudioContent.Children.Add(_micBoostCheck);

        _micBiasCheck = MakeToggle("Mic Bias (phantom power)");
        _micBiasCheck.Checked += (s, e) => SetToggle("Mic Bias", v => { if (_rig != null) _rig.MicBias = v; }, true);
        _micBiasCheck.Unchecked += (s, e) => SetToggle("Mic Bias", v => { if (_rig != null) _rig.MicBias = v; }, false);
        TxAudioContent.Children.Add(_micBiasCheck);

        // Processing section
        AddSectionHeader(TxAudioContent, "Processing");

        _companderCheck = MakeToggle("Compander");
        _companderCheck.Checked += (s, e) =>
        {
            SetToggle("Compander", v => { if (_rig != null) _rig.Compander = v; }, true);
            if (_companderLevelControl != null) _companderLevelControl.Visibility = Visibility.Visible;
        };
        _companderCheck.Unchecked += (s, e) =>
        {
            SetToggle("Compander", v => { if (_rig != null) _rig.Compander = v; }, false);
            if (_companderLevelControl != null) _companderLevelControl.Visibility = Visibility.Collapsed;
        };
        TxAudioContent.Children.Add(_companderCheck);

        _companderLevelControl = MakeValue("Compander Level", 0, 100, 5);
        _companderLevelControl.Visibility = Visibility.Collapsed;
        _companderLevelControl.ValueChanged += (s, v) =>
        {
            if (_rig != null && !_polling)
            {
                _rig.CompanderLevel = v;
                ScreenReaderOutput.Speak($"Compander level {v}", VerbosityLevel.Terse);
            }
        };
        TxAudioContent.Children.Add(_companderLevelControl);

        _processorCheck = MakeToggle("Speech Processor");
        _processorCheck.Checked += (s, e) =>
        {
            SetToggle("Speech Processor", v => { if (_rig != null) _rig.ProcessorOn = v; }, true);
            if (_processorSettingControl != null) _processorSettingControl.Visibility = Visibility.Visible;
        };
        _processorCheck.Unchecked += (s, e) =>
        {
            SetToggle("Speech Processor", v => { if (_rig != null) _rig.ProcessorOn = v; }, false);
            if (_processorSettingControl != null) _processorSettingControl.Visibility = Visibility.Collapsed;
        };
        TxAudioContent.Children.Add(_processorCheck);

        _processorSettingControl = MakeCycle("Processor Mode", new[] { "Normal", "DX", "DX+" });
        _processorSettingControl.Visibility = Visibility.Collapsed;
        _processorSettingControl.SelectionChanged += (s, idx) =>
        {
            if (_rig != null && !_polling)
            {
                _rig.ProcessorSetting = (FlexBase.ProcessorSettings)idx;
                string[] names = { "Normal", "DX", "DX Plus" };
                ScreenReaderOutput.Speak($"Processor mode {names[Math.Min(idx, 2)]}", VerbosityLevel.Terse);
            }
        };
        TxAudioContent.Children.Add(_processorSettingControl);

        // TX Filter section
        AddSectionHeader(TxAudioContent, "TX Filter");

        _txFilterLowControl = MakeValue("TX Filter Low", 0, 9950, 50);
        _txFilterLowControl.ValueChanged += (s, v) =>
        {
            if (_rig != null && !_polling)
            {
                _rig.TXFilterLow = v;
                UpdateFilterWidth();
                ScreenReaderOutput.Speak($"TX low {v}", VerbosityLevel.Terse);
            }
        };
        TxAudioContent.Children.Add(_txFilterLowControl);

        _txFilterHighControl = MakeValue("TX Filter High", 50, 10000, 50);
        _txFilterHighControl.ValueChanged += (s, v) =>
        {
            if (_rig != null && !_polling)
            {
                _rig.TXFilterHigh = v;
                UpdateFilterWidth();
                ScreenReaderOutput.Speak($"TX high {v}", VerbosityLevel.Terse);
            }
        };
        TxAudioContent.Children.Add(_txFilterHighControl);

        _filterWidthLabel = new TextBlock
        {
            Text = "Width: --",
            Margin = new Thickness(2, 4, 2, 4),
            FontSize = 12
        };
        AutomationProperties.SetName(_filterWidthLabel, "TX filter width");
        AutomationProperties.SetLiveSetting(_filterWidthLabel, AutomationLiveSetting.Polite);
        TxAudioContent.Children.Add(_filterWidthLabel);

        // Monitor section. The header names the mode in phone modes so the
        // screen reader user knows which knob family they're on; in CW mode
        // today's behavior is unchanged (the CW monitor work is deferred
        // behind the CW pipeline rewrite).
        _monitorHeader = AddSectionHeader(TxAudioContent, "TX Monitor");

        _monitorCheck = MakeToggle("TX Monitor");
        _monitorCheck.Checked += (s, e) =>
        {
            SetToggle("TX Monitor", v => { if (_rig != null) _rig.Monitor = v; }, true);
            if (_monitorLevelControl != null) _monitorLevelControl.Visibility = Visibility.Visible;
            if (_monitorPanControl != null) _monitorPanControl.Visibility = Visibility.Visible;
        };
        _monitorCheck.Unchecked += (s, e) =>
        {
            SetToggle("TX Monitor", v => { if (_rig != null) _rig.Monitor = v; }, false);
            if (_monitorLevelControl != null) _monitorLevelControl.Visibility = Visibility.Collapsed;
            if (_monitorPanControl != null) _monitorPanControl.Visibility = Visibility.Collapsed;
        };
        TxAudioContent.Children.Add(_monitorCheck);

        _monitorLevelControl = MakeValue("Monitor Level", 0, 100, 5);
        _monitorLevelControl.Visibility = Visibility.Collapsed;
        _monitorLevelControl.ValueChanged += (s, v) =>
        {
            if (_rig != null && !_polling)
            {
                _rig.SBMonitorLevel = v;
                ScreenReaderOutput.Speak($"Monitor level {v}", VerbosityLevel.Terse);
            }
        };
        TxAudioContent.Children.Add(_monitorLevelControl);

        _monitorPanControl = MakeValue("Monitor Pan", 0, 100, 5);
        _monitorPanControl.Visibility = Visibility.Collapsed;
        _monitorPanControl.ValueChanged += (s, v) =>
        {
            if (_rig != null && !_polling)
            {
                _rig.SBMonitorPan = v;
                ScreenReaderOutput.Speak($"Monitor pan {v}", VerbosityLevel.Terse);
            }
        };
        TxAudioContent.Children.Add(_monitorPanControl);

        // Built-in test tone — the mic replacement (Audio Track C). Late in
        // the walk: it is the first thing here that reaches the air, and it
        // is what the Audio Check below sends when you have no voice to send.
        BuildTestToneSection();

        // Audio Check session — the "hear yourself" loop (QB Track G). Last,
        // because it keys the transmitter and proves everything above it.
        BuildAudioCheckSection();
    }

    /// <summary>
    /// Step one of the walk: which microphone this computer is listening to.
    /// </summary>
    /// <remarks>
    /// The Audio Devices picker was reachable from the Audio menu, Settings'
    /// Audio tab and a key command, but not from the Workshop — the one
    /// surface whose entire job is getting your audio right (Noel,
    /// 2026-08-12). Every measurement below this section is downstream of the
    /// answer, so the answer belongs at the top and in words: a bare "Audio
    /// Devices..." button would make an operator open a dialog to find out
    /// what they already chose.
    ///
    /// The name is read from audioDevices.xml via LoadSavedSelection, which
    /// does NOT enumerate — naming the device costs a file read, not a
    /// Pa_Initialize/Pa_Terminate cycle.
    /// </remarks>
    private void BuildDeviceSection()
    {
        AddSectionHeader(TxAudioContent, "This Computer");

        // Read-only EDIT rather than a label, same reasoning as the mic
        // reading below: focusable, review-readable, and the screen reader's
        // own read-current-control command speaks it without an app hotkey.
        _deviceReadingBox = new TextBox
        {
            Text = "Microphone: checking",
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            Margin = new Thickness(2),
            MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(_deviceReadingBox, "Microphone this computer is using");
        TxAudioContent.Children.Add(_deviceReadingBox);

        var deviceButton = new Button
        {
            Content = "Change Audio Devices...",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetName(deviceButton, "Change Audio Devices");
        deviceButton.Click += (s, e) =>
        {
            var open = OpenAudioDevices;
            if (open == null)
            {
                ScreenReaderOutput.Speak(
                    "Audio devices cannot be opened from here yet.",
                    VerbosityLevel.Critical);
                return;
            }
            open();
            // The picker may have changed the selection — say what it is now
            // rather than leaving a stale name sitting above the controls.
            RefreshDeviceReading(announce: true);
        };
        TxAudioContent.Children.Add(deviceButton);

        RefreshDeviceReading(announce: false);
    }

    /// <summary>
    /// Re-read the chosen input device and update the "This Computer" line.
    /// Never guesses: when the path or the file is missing it says so, because
    /// a confidently wrong device name here is worse than an admission.
    /// </summary>
    private void RefreshDeviceReading(bool announce)
    {
        if (_deviceReadingBox == null) return;

        string text;
        try
        {
            string? path = AudioDevicesPath?.Invoke();
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                text = "Microphone: none chosen yet";
            }
            else
            {
                var devices = new JJPortaudio.Devices(path);
                devices.LoadSavedSelection();
                string? name = devices.InputDevice?.Name;
                text = string.IsNullOrWhiteSpace(name)
                    ? "Microphone: none chosen yet"
                    : "Microphone: " + name;
            }
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine(
                "AudioWorkshop: could not read the chosen input device — "
                + ex.Message, System.Diagnostics.TraceLevel.Warning);
            text = "Microphone: could not be read";
        }

        _deviceReadingBox.Text = text;
        AutomationProperties.SetName(_deviceReadingBox, text);
        if (announce) ScreenReaderOutput.Speak(text, VerbosityLevel.Terse);
    }

    /// <summary>
    /// True when no input device has been chosen on this computer — the
    /// first-run state, and the one where the walk-through is worth
    /// presenting. See <see cref="FocusFirstControl"/>.
    /// </summary>
    private static bool NoInputDeviceChosen()
    {
        try
        {
            string? path = AudioDevicesPath?.Invoke();
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                return true;
            var devices = new JJPortaudio.Devices(path);
            devices.LoadSavedSelection();
            return string.IsNullOrWhiteSpace(devices.InputDevice?.Name);
        }
        catch
        {
            // Unreadable is not the same as unconfigured, and sending a
            // set-up operator to step one on a file-read hiccup would be the
            // more annoying of the two mistakes.
            return false;
        }
    }

    #region Audio Check Section

    private void BuildAudioCheckSection()
    {
        AddSectionHeader(TxAudioContent, "Audio Check");

        // Order here is Start button first, then the live reading —
        // reordered by the Threads Track (2026-08-12, from Noel's field
        // report): during a check, focus sits on Mic Gain, and the old
        // reading-before-button placement left the reading several
        // Shift+Tabs away during the one activity that needs it. The tab
        // ring is now Start, reading, Mic Gain (see ApplyTxAudioTabOrder),
        // so the reading sits between the two controls a running check
        // actually uses and is one key from either.
        _startCheckButton = new Button
        {
            Content = "Start Audio Check",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetName(_startCheckButton, "Start Audio Check");
        AutomationProperties.SetAcceleratorKey(_startCheckButton, "Ctrl+Enter");
        _startCheckButton.Click += (s, e) => ToggleAudioCheck();
        TxAudioContent.Children.Add(_startCheckButton);

        // The live mic reading, as a read-only EDIT (Noel, 2026-08-11). An
        // edit is focusable and review-readable where a label gets skipped;
        // and because the value lives somewhere focusable, the screen
        // reader's own read-current-control command IS the "speak my
        // level" feature — no app hotkey needed.
        _micReadingBox = new TextBox
        {
            Text = "Mic audio: transmit to measure",
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            Margin = new Thickness(2),
            FontSize = 12
        };
        // Static accessible name, set ONCE. The 2 Hz refresh touches only
        // the text — no name changes, no live region — so NVDA stays quiet
        // while the value moves and a review command always reads fresh.
        // Same lesson Track A learned on the Home expander field.
        AutomationProperties.SetName(_micReadingBox, "Mic audio reading");
        TxAudioContent.Children.Add(_micReadingBox);

        _listenMethodControl = MakeCycle("Listen method",
            new[] { "Monitor", "Record and play back" });
        _listenMethodControl.SelectionChanged += (s, idx) =>
        {
            if (_polling) return;
            SavePerRadioPrefs();
            // The control already spoke the new value; add the remote
            // advisory only where it matters.
            if (idx == (int)AudioCheckListenMethods.Monitor && _rig?.RemoteRig == true)
                ScreenReaderOutput.Speak(
                    "Note: over remote, monitor audio arrives delayed. Record and play back is recommended.",
                    VerbosityLevel.Terse);
        };
        TxAudioContent.Children.Add(_listenMethodControl);

        // Track C-2 (Noel at the radio, 2026-08-11: "you have it at 10
        // watts. If you have no antenna, that's a bit high"): the check
        // defaults to DUMMY LOAD, not low power. Every meter the check
        // reads sits upstream of the power amplifier — proven live, a tone
        // at -10 dBFS read -11 on SC_MIC at zero watts — and with a tone
        // armed, a transmitting check puts a steady carrier on whatever
        // frequency the operator is tuned to. Low power remains for the
        // separate, deliberate act of confirming RF leaves the radio, with
        // the cap finally choosable ("so I can change it to 1 if I need
        // to").
        _checkPowerControl = MakeCycle("Transmit power during checks",
            new[] { "Dummy load, no RF", "Low power" });
        _checkPowerControl.SelectionChanged += (s, idx) =>
        {
            if (_polling) return;
            UpdateCheckWattsVisibility();
            SavePerRadioPrefs();
        };
        TxAudioContent.Children.Add(_checkPowerControl);

        _checkWattsControl = new ValueFieldControl();
        _checkWattsControl.Setup("Low power level", 1, 100, 1, 10, 0, "watts");
        _checkWattsControl.Visibility = Visibility.Collapsed; // dummy load is the default
        _checkWattsControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            SavePerRadioPrefs();
        };
        TxAudioContent.Children.Add(_checkWattsControl);

        _playTakeButton = new Button
        {
            Content = "Play last take",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetName(_playTakeButton, "Play last take");
        _playTakeButton.Click += (s, e) => PlayLastTake();
        TxAudioContent.Children.Add(_playTakeButton);

        // Loopback check — real RF through the transverter port, inside one
        // radio, no antennas. Doubles as a transmitter self-test: "check my
        // audio" and "is my radio actually transmitting" are the same button.
        // Hidden (out of tab order) on radios that can't do it; the info line
        // below explains why.
        _loopbackButton = new Button
        {
            Content = "Loopback Check (transverter port)",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2),
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(_loopbackButton, "Loopback Check, transverter port");
        _loopbackButton.Click += (s, e) => StartLoopbackCheck();
        TxAudioContent.Children.Add(_loopbackButton);

        _loopbackInfo = new TextBlock
        {
            Text = "",
            Margin = new Thickness(2, 2, 2, 4),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        TxAudioContent.Children.Add(_loopbackInfo);
    }

    /// <summary>
    /// Show the loopback button on capable radios; on the rest, show a
    /// de-emphasized explanation instead (the button stays out of the tab
    /// order — house rule for unsupported controls).
    /// </summary>
    private void UpdateLoopbackAvailability()
    {
        if (_loopbackButton == null || _loopbackInfo == null) return;
        bool supported = _rig?.LoopbackSupported == true;
        _loopbackButton.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        if (supported)
        {
            _loopbackInfo.Visibility = Visibility.Collapsed;
        }
        else
        {
            string reason = _rig?.LoopbackUnavailableReason ?? "No radio connected";
            string text = $"Loopback check not available: {reason}.";
            if (_loopbackInfo.Text != text)
            {
                _loopbackInfo.Text = text;
                AutomationProperties.SetName(_loopbackInfo, text);
            }
            _loopbackInfo.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// The Loopback Check: apply the verified recipe (full duplex on, TX to
    /// the XVT port, ears slice on the same port, 1 watt, monitor off), then
    /// key through the same PttSafetyController path as every other check.
    /// Teardown restores every saved value and removes the ears slice.
    /// </summary>
    private void StartLoopbackCheck()
    {
        if (_session != null && _session.Active)
        {
            ScreenReaderOutput.Speak("Stop the current check first.",
                VerbosityLevel.Critical, interrupt: true);
            return;
        }
        if (_rig == null)
        {
            ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Critical);
            return;
        }
        var ptt = PttControllerSource?.Invoke();
        if (ptt == null)
        {
            ScreenReaderOutput.Speak("Radio is not powered on", VerbosityLevel.Critical);
            return;
        }
        if (ptt.IsTransmitting)
        {
            ScreenReaderOutput.Speak("Already transmitting. Stop transmitting first.",
                VerbosityLevel.Critical, interrupt: true);
            return;
        }

        if (!_rig.StartLoopbackArrangement())
        {
            string reason = _rig.LoopbackUnavailableReason;
            if (string.IsNullOrEmpty(reason)) reason = "no free slice for the listening receiver";
            ScreenReaderOutput.Speak($"Loopback check could not be set up: {reason}.",
                VerbosityLevel.Critical, interrupt: true);
            return;
        }

        // Power mode is irrelevant here — the loopback arrangement owns
        // power (1 W into the transverter port) and the session's power
        // handling is bypassed entirely in loopback mode.
        var session = new AudioCheckSession(this, _rig, ptt,
            AudioCheckListenMethods.Monitor, AudioCheckPowerModes.LowPower, 1,
            loopback: true);
        if (session.Start())
        {
            _session = session;
            SetStartButtonLabel("Stop Audio Check");
            _micGainControl?.Focus();
        }
        else
        {
            // Keying refused — take the arrangement back down.
            string trouble = _rig.EndLoopbackArrangement();
            if (!string.IsNullOrEmpty(trouble))
                ScreenReaderOutput.Speak(trouble, VerbosityLevel.Terse);
        }
    }

    /// <summary>
    /// Start button / Command Finder entry point. Starts a session when idle,
    /// stops the current one when active.
    /// </summary>
    public void ToggleAudioCheck()
    {
        if (_session != null && _session.Active)
        {
            _session.StopCheck();
            return;
        }

        if (_rig == null)
        {
            ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Critical);
            return;
        }

        var ptt = PttControllerSource?.Invoke();
        if (ptt == null)
        {
            ScreenReaderOutput.Speak("Radio is not powered on", VerbosityLevel.Critical);
            return;
        }
        if (ptt.IsTransmitting)
        {
            ScreenReaderOutput.Speak("Already transmitting. Stop transmitting first.",
                VerbosityLevel.Critical, interrupt: true);
            return;
        }

        var method = (AudioCheckListenMethods)(_listenMethodControl?.SelectedIndex ?? 0);
        var powerMode = (AudioCheckPowerModes)(_checkPowerControl?.SelectedIndex
            ?? (int)AudioCheckPowerModes.DummyLoad);
        int lowPowerWatts = Math.Clamp(_checkWattsControl?.Value ?? 10, 1, 100);

        var session = new AudioCheckSession(this, _rig, ptt, method, powerMode, lowPowerWatts);
        if (session.Start())
        {
            _session = session;
            SetStartButtonLabel("Stop Audio Check");
            // Land on Mic Gain — arrows adjust immediately, one Shift+Tab
            // reads the live mic reading, one more reaches Stop (the
            // three-stop check cluster, see ApplyTxAudioTabOrder).
            _micGainControl?.Focus();
        }
    }

    private void SetStartButtonLabel(string label)
    {
        if (_startCheckButton == null) return;
        _startCheckButton.Content = label;
        AutomationProperties.SetName(_startCheckButton, label);
    }

    /// <summary>
    /// Session ended (any path) — restore the button label, and if a
    /// loopback arrangement is up, tear it down and say so. Runs on every
    /// exit path because every exit path funnels through the session's End.
    /// </summary>
    private void OnSessionEnded()
    {
        SetStartButtonLabel("Start Audio Check");
        if (_rig != null && _rig.LoopbackArranged)
        {
            string trouble = _rig.EndLoopbackArrangement();
            ScreenReaderOutput.Speak(
                "Loopback ended. Antenna, power, monitor and duplex settings restored. " + trouble,
                VerbosityLevel.Terse);
        }
    }

    private void PlayLastTake()
    {
        if (_rig == null)
        {
            ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Critical);
            return;
        }
        if (_session != null && _session.EscapeStopsTransmit)
        {
            ScreenReaderOutput.Speak("Still transmitting. Stop the check first.",
                VerbosityLevel.Critical, interrupt: true);
            return;
        }
        if (_rig.SlicePlayOn)
        {
            _rig.SlicePlayOn = false;
            ScreenReaderOutput.Speak("Playback stopped", VerbosityLevel.Terse, interrupt: true);
            return;
        }
        if (!_rig.SlicePlayEnabled)
        {
            ScreenReaderOutput.Speak("No recording yet. Run an audio check with record and play back.",
                VerbosityLevel.Terse, interrupt: true);
            return;
        }
        _rig.SlicePlayOn = true;
        ScreenReaderOutput.Speak("Playing your take", VerbosityLevel.Terse, interrupt: true);
    }

    #endregion

    #region Test Tone Section (Audio Track C)

    /// <summary>
    /// The built-in TX test tone: a known tone at a known level that REPLACES
    /// the microphone in the PC-audio transmit path (the mic is muted while it
    /// runs — never mixed, no room bleed). Frequency is an accessibility
    /// choice, not a convenience: a test tone the operator cannot hear is
    /// useless for confirming the check is running, so it is adjustable, with
    /// named presets plus free entry, and persists per-operator in app
    /// settings. Passband policy is allow-and-warn (flexibility principle) —
    /// see UpdateToneStatus and BuildToneAnnouncement for the warning ladder
    /// that keeps it unmissable.
    /// </summary>
    private void BuildTestToneSection()
    {
        AddSectionHeader(TxAudioContent, "Test Tone");

        _toneCheck = MakeToggle("Test tone instead of microphone");
        _toneCheck.Checked += (s, e) => ToneArmChanged(true);
        _toneCheck.Unchecked += (s, e) => ToneArmChanged(false);
        TxAudioContent.Children.Add(_toneCheck);

        _tonePresetControl = MakeCycle("Tone frequency", new[]
        {
            "440 hertz reference",
            "700 hertz CW tone",
            "1000 hertz standard test",
            "Custom frequency"
        });
        _tonePresetControl.SelectionChanged += (s, idx) =>
        {
            if (_polling) return;
            bool custom = idx >= TonePresetHz.Length;
            if (_toneFreqControl != null)
                _toneFreqControl.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            ToneParamsChanged(speakPassband: true);
        };
        TxAudioContent.Children.Add(_tonePresetControl);

        _toneFreqControl = new ValueFieldControl();
        _toneFreqControl.Setup("Custom frequency", 50, 10000, 10, 440, 0, "hertz");
        _toneFreqControl.Visibility = Visibility.Collapsed;
        _toneFreqControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            ToneParamsChanged(speakPassband: true);
        };
        TxAudioContent.Children.Add(_toneFreqControl);

        _toneLevelControl = new ValueFieldControl();
        _toneLevelControl.Setup("Tone level", -40, 0, 1, -10, 0, "dBFS");
        _toneLevelControl.ValueChanged += (s, v) =>
        {
            if (_polling) return;
            ToneParamsChanged(speakPassband: false);
        };
        TxAudioContent.Children.Add(_toneLevelControl);

        _toneMonitorCheck = MakeToggle("Hear the tone while it transmits");
        _toneMonitorCheck.IsChecked = true;
        _toneMonitorCheck.Checked += (s, e) => ToneMonitorChanged(true);
        _toneMonitorCheck.Unchecked += (s, e) => ToneMonitorChanged(false);
        TxAudioContent.Children.Add(_toneMonitorCheck);

        _toneInfo = new TextBlock
        {
            Text = "",
            Margin = new Thickness(2, 2, 2, 4),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(_toneInfo, "Test tone passband status");
        AutomationProperties.SetLiveSetting(_toneInfo, AutomationLiveSetting.Polite);
        TxAudioContent.Children.Add(_toneInfo);
    }

    /// <summary>The effective tone frequency: preset value, or the custom field.</summary>
    private int CurrentToneFrequencyHz()
    {
        int idx = _tonePresetControl?.SelectedIndex ?? 0;
        if (idx >= 0 && idx < TonePresetHz.Length) return TonePresetHz[idx];
        return _toneFreqControl?.Value ?? 440;
    }

    /// <summary>
    /// Reflect persisted per-operator tone settings (app settings store) in
    /// the controls, and push them to the rig's generator.
    /// </summary>
    private void LoadToneSettings()
    {
        var cfg = AudioConfigSource?.Invoke();
        int freq = Math.Clamp(cfg?.TxToneFrequencyHz ?? 440, 50, 10000);
        int level = Math.Clamp(cfg?.TxToneLevelDb ?? -10, -40, 0);
        bool monitor = cfg?.TxToneLocalMonitor ?? true;

        int presetIdx = Array.IndexOf(TonePresetHz, freq);
        if (presetIdx < 0) presetIdx = TonePresetHz.Length; // Custom

        _polling = true;
        try
        {
            if (_tonePresetControl != null)
            {
                _tonePresetControl.SuppressEvents = true;
                _tonePresetControl.SelectedIndex = presetIdx;
                _tonePresetControl.SuppressEvents = false;
            }
            if (_toneFreqControl != null)
            {
                _toneFreqControl.SuppressEvents = true;
                _toneFreqControl.Value = freq;
                _toneFreqControl.SuppressEvents = false;
                _toneFreqControl.Visibility = presetIdx >= TonePresetHz.Length
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_toneLevelControl != null)
            {
                _toneLevelControl.SuppressEvents = true;
                _toneLevelControl.Value = level;
                _toneLevelControl.SuppressEvents = false;
            }
            if (_toneMonitorCheck != null)
                _toneMonitorCheck.IsChecked = monitor;
        }
        finally
        {
            _polling = false;
        }

        if (_rig != null)
        {
            _rig.TxToneFrequency = freq;
            _rig.TxToneLevelDb = level;
        }
    }

    /// <summary>Persist tone settings to the per-operator app settings store.</summary>
    private void SaveToneSettings()
    {
        var cfg = AudioConfigSource?.Invoke();
        if (cfg == null) return;
        cfg.TxToneFrequencyHz = CurrentToneFrequencyHz();
        cfg.TxToneLevelDb = _toneLevelControl?.Value ?? -10;
        cfg.TxToneLocalMonitor = _toneMonitorCheck?.IsChecked == true;
        AudioConfigSave?.Invoke();
    }

    /// <summary>
    /// Frequency or level changed: push to the generator, persist, refresh the
    /// passband status, and — for frequency changes — speak the passband
    /// verdict when there is trouble. The field control already spoke the new
    /// value; the warning queues right behind it.
    /// </summary>
    private void ToneParamsChanged(bool speakPassband)
    {
        int freq = CurrentToneFrequencyHz();
        if (_rig != null)
        {
            _rig.TxToneFrequency = freq;
            _rig.TxToneLevelDb = _toneLevelControl?.Value ?? -10;
        }
        SaveToneSettings();
        UpdateToneStatus(speakIfNewlyOutside: false);
        if (speakPassband)
        {
            string trouble = PassbandCheck(freq, out bool outside);
            if (!string.IsNullOrEmpty(trouble))
            {
                if (outside) EarconPlayer.Warning2Beep();
                ScreenReaderOutput.Speak(trouble,
                    outside ? VerbosityLevel.Critical : VerbosityLevel.Terse);
            }
            _toneOutsideWarned = outside;
        }
    }

    /// <summary>
    /// The passband trap check. SSB transmit filters typically pass roughly
    /// 100-2900 Hz; a tone moved to where the operator hears best can land
    /// outside the filter and transmit NOTHING — silently, while they believe
    /// they are testing. Policy is allow-and-warn (never remove the choice),
    /// so this must be unmissable: it speaks at set time, at arm time, at
    /// every key-down, and when the filter later moves out from under the
    /// tone. Returns the plain-language warning, a near-edge note, or "".
    /// </summary>
    private string PassbandCheck(int freqHz, out bool outside)
    {
        outside = false;
        var rig = _rig;
        if (rig == null) return "";
        int low = rig.TXFilterLow;
        int high = rig.TXFilterHigh;
        if (high <= low) return ""; // filter unknown — nothing honest to say
        if (freqHz < low || freqHz > high)
        {
            outside = true;
            return $"Warning: {freqHz} hertz is outside your transmit filter, " +
                $"which passes {low} to {high} hertz. The tone will not go out. " +
                "Pick a frequency inside the filter, or widen the TX filter below.";
        }
        if (freqHz - low < 50 || high - freqHz < 50)
        {
            return $"Note: {freqHz} hertz is within 50 hertz of your transmit " +
                $"filter edge ({low} to {high} hertz). The tone may go out reduced.";
        }
        return "";
    }

    /// <summary>
    /// Refresh the visible passband status line, and — when asked — speak an
    /// edge-triggered warning if the TX filter has moved out from under an
    /// armed tone (the operator can change the filter at any time, including
    /// while the tone transmits; that must not fail quietly).
    /// </summary>
    private void UpdateToneStatus(bool speakIfNewlyOutside)
    {
        if (_toneInfo == null) return;
        var rig = _rig;
        int freq = CurrentToneFrequencyHz();
        string text;
        bool outside = false;
        if (rig == null)
        {
            text = "No radio connected; the tone cannot be checked against a transmit filter.";
        }
        else
        {
            string trouble = PassbandCheck(freq, out outside);
            text = string.IsNullOrEmpty(trouble)
                ? $"{freq} hertz is inside your transmit filter ({rig.TXFilterLow} to {rig.TXFilterHigh} hertz)."
                : trouble;
        }
        if (_toneInfo.Text != text)
        {
            _toneInfo.Text = text;
            AutomationProperties.SetName(_toneInfo, text);
        }
        if (speakIfNewlyOutside)
        {
            if (outside && !_toneOutsideWarned)
            {
                _toneOutsideWarned = true;
                EarconPlayer.Warning2Beep();
                ScreenReaderOutput.Speak(text, VerbosityLevel.Critical, interrupt: true);
            }
            else if (!outside)
            {
                _toneOutsideWarned = false;
            }
        }
    }

    /// <summary>
    /// Arm or release the tone. Arming refuses out loud when the tone cannot
    /// reach the transmitter at all (PC audio off, transmit input not PC, CW
    /// mode) — that is not a choice being removed, it is a trap being named:
    /// with the wrong path armed "successfully", something OTHER than the tone
    /// keeps transmitting while the operator believes they are testing.
    /// Passband trouble, by contrast, arms anyway and warns (allow-and-warn).
    /// </summary>
    private void ToneArmChanged(bool armed)
    {
        if (_polling) return;

        if (!armed)
        {
            DisarmTone(speak: true);
            return;
        }

        var rig = _rig;
        if (rig == null)
        {
            SetToneCheckSilently(false);
            ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Critical, interrupt: true);
            return;
        }
        string pathTrouble = rig.TxTonePathTrouble;
        if (!string.IsNullOrEmpty(pathTrouble))
        {
            SetToneCheckSilently(false);
            EarconPlayer.Warning2Beep();
            ScreenReaderOutput.Speak("Test tone not armed. " + pathTrouble,
                VerbosityLevel.Critical, interrupt: true);
            return;
        }

        int freq = CurrentToneFrequencyHz();
        int level = _toneLevelControl?.Value ?? -10;
        rig.TxToneFrequency = freq;
        rig.TxToneLevelDb = level;
        rig.TxToneStart();

        // Every key-down anywhere in the app now says the tone is riding it.
        PttSafetyController.KeyDownAnnouncementExtra = () => _instance?.BuildToneAnnouncement();

        var line = new StringBuilder();
        line.Append($"Test tone armed: {freq} hertz at {level} dBFS. ");
        line.Append("It replaces your microphone while you transmit.");
        string pb = PassbandCheck(freq, out bool outside);
        if (!string.IsNullOrEmpty(pb)) line.Append(' ').Append(pb);
        _toneOutsideWarned = outside;
        if (outside) EarconPlayer.Warning2Beep();
        ScreenReaderOutput.Speak(line.ToString(), VerbosityLevel.Critical, interrupt: true);
        UpdateToneStatus(speakIfNewlyOutside: false);
    }

    /// <summary>
    /// Release the tone and restore the microphone: stop the generator, clear
    /// the key-down announcement hook, and silence the local monitor. Runs on
    /// operator unarm, dialog close, and radio teardown (pass the departing
    /// rig for the teardown case, where _rig is already null).
    /// </summary>
    private void DisarmTone(bool speak, FlexBase? rig = null)
    {
        (rig ?? _rig)?.TxToneStop();
        PttSafetyController.KeyDownAnnouncementExtra = null;
        EarconPlayer.StopTxToneMonitor();
        _toneMonitorSounding = false;
        _toneMonitorProvider = null;
        if (speak)
            ScreenReaderOutput.Speak("Test tone off. Microphone restored.",
                VerbosityLevel.Critical, interrupt: true);
    }

    /// <summary>Set the arm checkbox without firing its handlers.</summary>
    private void SetToneCheckSilently(bool value)
    {
        if (_toneCheck == null) return;
        _polling = true;
        try { _toneCheck.IsChecked = value; }
        finally { _polling = false; }
    }

    /// <summary>
    /// Keep the arm checkbox honest against the ENGINE's tone state. The
    /// Ctrl+J, G leader binding (Keys Track, 2026-08-11) arms and disarms
    /// the tone by driving FlexBase directly, so the workshop no longer
    /// owns every state change. Rides the existing meter poll — no second
    /// timer — and syncs silently: the leader already announced the
    /// change, so re-speaking here would double-talk. The key-down
    /// announcement hook follows the same truth (it is how EVERY transmit
    /// path warns that the tone is riding it, so an externally armed tone
    /// must set it too); the local monitor and passband status already
    /// derive from engine state on this same tick.
    /// </summary>
    private void SyncToneArmUi()
    {
        var rig = _rig;
        if (rig == null || _toneCheck == null) return;
        bool engaged = rig.TxToneEngaged;
        if ((_toneCheck.IsChecked == true) == engaged) return;

        SetToneCheckSilently(engaged);
        if (engaged)
            PttSafetyController.KeyDownAnnouncementExtra = () => _instance?.BuildToneAnnouncement();
        else
            PttSafetyController.KeyDownAnnouncementExtra = null;
        UpdateToneStatus(speakIfNewlyOutside: false);
    }

    private void ToneMonitorChanged(bool on)
    {
        if (_polling) return;
        SaveToneSettings();
        ScreenReaderOutput.Speak($"Tone monitor {(on ? "on" : "off")}",
            VerbosityLevel.Terse, interrupt: true);
        SyncToneMonitor(); // apply immediately, not at the next timer tick
    }

    /// <summary>
    /// Keep the local monitor honest: it sounds ONLY while the tone is armed
    /// AND the radio is actually transmitting (a monitor that sounds while
    /// unkeyed would imply the tone is going out when it is not), and only
    /// when the operator wants it. Runs on every meter-timer tick regardless
    /// of the selected tab, and follows live frequency changes.
    /// </summary>
    private void SyncToneMonitor()
    {
        var rig = _rig;
        bool shouldSound = rig != null && rig.TxToneEngaged && rig.Transmit
            && _toneMonitorCheck?.IsChecked == true;
        if (shouldSound && !_toneMonitorSounding)
        {
            _toneMonitorProvider = EarconPlayer.StartTxToneMonitor(rig!.TxToneFrequency);
            _toneMonitorSounding = true;
        }
        else if (!shouldSound && _toneMonitorSounding)
        {
            EarconPlayer.StopTxToneMonitor();
            _toneMonitorSounding = false;
            _toneMonitorProvider = null;
        }
        else if (shouldSound && _toneMonitorProvider != null)
        {
            _toneMonitorProvider.Frequency = rig!.TxToneFrequency;
        }
    }

    /// <summary>
    /// The spoken line for a transmission the tone is riding. Used by the
    /// PTT controller's key-down hook (every transmit path) and by the Audio
    /// Check session's safety line. Re-checks the path and the passband at
    /// the moment of key-down, because both can have changed since arming.
    /// Returns null when the tone is not engaged.
    /// </summary>
    internal string? BuildToneAnnouncement()
    {
        var rig = _rig;
        if (rig == null || !rig.TxToneEngaged) return null;
        int freq = (int)rig.TxToneFrequency;
        string pathTrouble = rig.TxTonePathTrouble;
        if (!string.IsNullOrEmpty(pathTrouble))
            return "The test tone is armed but is not going out. " + pathTrouble;
        string line = $"Sending the {freq} hertz test tone instead of your microphone.";
        string pb = PassbandCheck(freq, out _);
        if (!string.IsNullOrEmpty(pb)) line += " " + pb;
        return line;
    }

    #endregion

    private void UpdateFilterWidth()
    {
        if (_txFilterLowControl == null || _txFilterHighControl == null || _filterWidthLabel == null) return;
        int low = _txFilterLowControl.Value;
        int high = _txFilterHighControl.Value;
        int width = high - low;
        string widthStr = width >= 1000 ? $"{width / 1000.0:0.#} kHz" : $"{width} Hz";
        _filterWidthLabel.Text = $"Width: {low} to {high} Hz ({widthStr})";
    }

    private void PollTxAudio()
    {
        if (_rig == null) return;
        _polling = true;
        try
        {
            PollMicSource();

            if (_micGainControl != null) _micGainControl.Value = _rig.MicGain;
            if (_micBoostCheck != null) _micBoostCheck.IsChecked = _rig.MicBoost == FlexBase.OffOnValues.on;
            if (_micBiasCheck != null) _micBiasCheck.IsChecked = _rig.MicBias == FlexBase.OffOnValues.on;

            bool companderOn = _rig.Compander == FlexBase.OffOnValues.on;
            if (_companderCheck != null) _companderCheck.IsChecked = companderOn;
            if (_companderLevelControl != null)
            {
                _companderLevelControl.Visibility = companderOn ? Visibility.Visible : Visibility.Collapsed;
                if (companderOn) _companderLevelControl.Value = _rig.CompanderLevel;
            }

            bool processorOn = _rig.ProcessorOn == FlexBase.OffOnValues.on;
            if (_processorCheck != null) _processorCheck.IsChecked = processorOn;
            if (_processorSettingControl != null)
            {
                _processorSettingControl.Visibility = processorOn ? Visibility.Visible : Visibility.Collapsed;
                if (processorOn) _processorSettingControl.SelectedIndex = (int)_rig.ProcessorSetting;
            }

            if (_txFilterLowControl != null) _txFilterLowControl.Value = _rig.TXFilterLow;
            if (_txFilterHighControl != null) _txFilterHighControl.Value = _rig.TXFilterHigh;
            UpdateFilterWidth();

            // Mode-aware monitor header — phone modes only. CW mode keeps
            // today's behavior untouched (CW monitor work is deferred behind
            // the CW pipeline rewrite).
            if (_monitorHeader != null)
            {
                string mode = _rig.Mode ?? "";
                bool isCW = mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase);
                string hdr = (isCW || string.IsNullOrEmpty(mode))
                    ? "TX Monitor"
                    : $"TX Monitor — {mode}";
                if (_monitorHeader.Text != hdr)
                {
                    _monitorHeader.Text = hdr;
                    AutomationProperties.SetName(_monitorHeader, hdr);
                }
            }

            bool monitorOn = _rig.Monitor == FlexBase.OffOnValues.on;
            if (_monitorCheck != null) _monitorCheck.IsChecked = monitorOn;
            if (_monitorLevelControl != null)
            {
                _monitorLevelControl.Visibility = monitorOn ? Visibility.Visible : Visibility.Collapsed;
                if (monitorOn) _monitorLevelControl.Value = _rig.SBMonitorLevel;
            }
            if (_monitorPanControl != null)
            {
                _monitorPanControl.Visibility = monitorOn ? Visibility.Visible : Visibility.Collapsed;
                if (monitorOn) _monitorPanControl.Value = _rig.SBMonitorPan;
            }
        }
        finally
        {
            _polling = false;
        }
    }

    private string[] _micSourceOptions = Array.Empty<string>();
    private bool _jackAnnotated;

    /// <summary>
    /// Keep the mic source picker synced with the radio-reported input list
    /// and selection, and annotate jack-only controls (Mic Boost, Mic Bias)
    /// when TX audio is PC-sourced — de-emphasized in the label, never
    /// hidden. Runs inside PollTxAudio's _polling guard.
    /// </summary>
    private void PollMicSource()
    {
        if (_rig == null || _micSourceControl == null) return;

        var list = _rig.MicSourceList;
        if (list.Count > 0)
        {
            bool changed = list.Count != _micSourceOptions.Length;
            if (!changed)
            {
                for (int i = 0; i < list.Count; i++)
                    if (!string.Equals(list[i], _micSourceOptions[i], StringComparison.Ordinal))
                    { changed = true; break; }
            }
            if (changed)
            {
                _micSourceOptions = list.ToArray();
                _micSourceControl.SuppressEvents = true;
                _micSourceControl.Options = _micSourceOptions;
                _micSourceControl.SuppressEvents = false;
            }

            string current = _rig.MicSource;
            int idx = Array.FindIndex(_micSourceOptions,
                o => string.Equals(o, current, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && idx != _micSourceControl.SelectedIndex)
            {
                _micSourceControl.SuppressEvents = true;
                _micSourceControl.SelectedIndex = idx;
                _micSourceControl.SuppressEvents = false;
            }
        }

        // Jack-only annotation follows the SELECTED source (that is what the
        // controls act on — verified live 2026-08-07).
        bool pcSource = string.Equals(_rig.MicSource, "PC", StringComparison.OrdinalIgnoreCase);
        if (pcSource != _jackAnnotated)
        {
            _jackAnnotated = pcSource;
            string suffix = pcSource ? " — radio mic jack only, not in use" : "";
            SetToggleLabel(_micBoostCheck, "Mic Boost (+20 dB)" + suffix);
            SetToggleLabel(_micBiasCheck, "Mic Bias (phantom power)" + suffix);
        }
    }

    private static void SetToggleLabel(CheckBox? cb, string label)
    {
        if (cb == null) return;
        cb.Content = label;
        AutomationProperties.SetName(cb, label);
    }

    #endregion

    #region Tab 2: Live Meters

    private void BuildLiveMetersTab()
    {
        AddSectionHeader(LiveMetersContent, "Receiver");

        _sMeterLabel = MakeMeterLabel("S-Meter: --");
        LiveMetersContent.Children.Add(_sMeterLabel);

        AddSectionHeader(LiveMetersContent, "Transmit");

        _fwdPowerLabel = MakeMeterLabel("Forward Power: --");
        LiveMetersContent.Children.Add(_fwdPowerLabel);

        _swrLabel = MakeMeterLabel("SWR: --");
        LiveMetersContent.Children.Add(_swrLabel);

        _micAudioLabel = MakeMeterLabel("Mic audio: --");
        LiveMetersContent.Children.Add(_micAudioLabel);

        _alcLabel = MakeMeterLabel("TX drive (ALC): --");
        LiveMetersContent.Children.Add(_alcLabel);

        _ampAlcLabel = MakeMeterLabel("Amp ALC: --");
        LiveMetersContent.Children.Add(_ampAlcLabel);

        AddSectionHeader(LiveMetersContent, "Hardware");

        _paTempLabel = MakeMeterLabel("PA Temperature: --");
        LiveMetersContent.Children.Add(_paTempLabel);

        _voltsLabel = MakeMeterLabel("Supply Voltage: --");
        LiveMetersContent.Children.Add(_voltsLabel);
    }

    private void MeterTimer_Tick(object? sender, EventArgs e)
    {
        if (_rig == null) return;

        // Test tone housekeeping runs on EVERY tick regardless of tab: the
        // arm checkbox must follow the engine (Ctrl+J, G can change it from
        // outside this dialog), the local monitor must track actual
        // transmit state, and the passband warning must fire if the TX
        // filter moves out from under an armed tone — the operator may be
        // on any tab (or in another window) when that happens, and it must
        // not fail quietly.
        SyncToneArmUi();
        SyncToneMonitor();
        UpdateToneStatus(speakIfNewlyOutside: _rig.TxToneEngaged);

        // The mic reading refreshes on every tick regardless of tab so a
        // review command always reads fresh the moment the operator lands
        // on it.
        UpdateMicReading();

        // Only update meters when the Live Meters tab is selected
        if (MainTabs.SelectedIndex == 1)
            PollMeters();

        // Also refresh TX Audio tab values when visible
        if (MainTabs.SelectedIndex == 0)
            PollTxAudio();
    }

    private void PollMeters()
    {
        if (_rig == null) return;

        if (_sMeterLabel != null)
        {
            int sVal = _rig.SMeter;
            string sText = sVal <= 9 ? $"S{sVal}" : $"S9+{(sVal - 9) * 6} dB";
            _sMeterLabel.Text = $"S-Meter: {sText}";
        }

        if (_fwdPowerLabel != null)
            _fwdPowerLabel.Text = $"Forward Power: {_rig.PowerDBM:F1} dBm";

        if (_swrLabel != null)
            _swrLabel.Text = $"SWR: {_rig.SWRValue:F1}";

        // TX drive is SW ALC, not HWALC (the external-amp jack the old readout
        // showed — always ~0). Mic audio is SC_MIC, honest for PC audio AND the
        // analog mic, where the old "Mic Level" (COD-/MIC) read -120 for PC.
        if (_alcLabel != null)
            _alcLabel.Text = $"TX drive (ALC): {_rig.SwAlcDb:F1} dBFS";

        if (_ampAlcLabel != null)
            _ampAlcLabel.Text = $"Amp ALC: {_rig.ALC:F2}";

        if (_micAudioLabel != null)
            _micAudioLabel.Text = $"Mic audio: {_rig.ScMicDb:F1} dBFS ({MicAudioVerdict(_rig.ScMicMaxDb)})";

        if (_paTempLabel != null)
            _paTempLabel.Text = $"PA Temperature: {_rig.PATemp:F1} °C";

        if (_voltsLabel != null)
            _voltsLabel.Text = $"Supply Voltage: {_rig.Volts:F1} V";
    }

    /// <summary>
    /// Refresh the read-only mic reading edit. Text only — the accessible
    /// name was set once at build time and live-region notifications are
    /// deliberately absent, so a value moving twice a second never floods
    /// NVDA; the operator's review command reads the fresh text on demand.
    /// Live recent-peak while transmitting (it follows a level back down),
    /// the whole-transmit peak after unkey, honest wording before any
    /// transmit. Mirrors the Home expander's verdict field (Track A).
    /// </summary>
    private void UpdateMicReading()
    {
        if (_micReadingBox == null) return;
        var rig = _rig;
        string text;
        if (rig == null)
        {
            text = "Mic audio: no radio connected";
        }
        else
        {
            float recent = rig.ScMicRecentDb;
            float max = rig.ScMicMaxDb;
            if (rig.Transmit && recent > -140f)
                text = $"Mic audio now: {MicAudioVerdict(recent)}, peak {recent:F0} dBFS";
            else if (max > -140f)
                text = $"Mic audio last transmit: {MicAudioVerdict(max)}, peak {max:F0} dBFS";
            else
                text = "Mic audio: transmit to measure";
        }
        // Assign only on change so an unchanged reading doesn't reset the
        // review cursor twice a second.
        if (_micReadingBox.Text != text)
            _micReadingBox.Text = text;
    }

    /// <summary>
    /// Plain-language mic-drive verdict from the SC_MIC peak-hold over the
    /// current transmit (dBFS). Thresholds are first-pass and tunable — to be
    /// calibrated on the bench with the audio-workshop loopback (JJSmartAudio
    /// will replace this heuristic with a gated LUFS measure).
    /// </summary>
    internal static string MicAudioVerdict(float scMicPeakDb)
    {
        if (scMicPeakDb < -30f) return "turn it up";
        if (scMicPeakDb > -6f) return "coming in hot";
        return "just right";
    }

    #endregion

    #region Tab 3: Earcon Explorer

    private void BuildEarconExplorerTab()
    {
        // Meter Tones
        AddSectionHeader(EarconExplorerContent, "Meter Tones");
        AddEarconButton(EarconExplorerContent, "Beep", () => EarconPlayer.Beep());
        AddEarconButton(EarconExplorerContent, "Warning Beep", () => EarconPlayer.Warning1Beep());
        AddEarconButton(EarconExplorerContent, "Warning 2 Beep", () => EarconPlayer.Warning2Beep());
        AddEarconButton(EarconExplorerContent, "Oh Crap Beep", () => EarconPlayer.OhCrapBeep());
        AddEarconButton(EarconExplorerContent, "Confirm Tone", () => EarconPlayer.ConfirmTone());

        // PTT & Transmission
        AddSectionHeader(EarconExplorerContent, "PTT and Transmission");
        AddEarconButton(EarconExplorerContent, "TX Start Tone", () => EarconPlayer.TxStartTone());
        AddEarconButton(EarconExplorerContent, "TX Stop Tone", () => EarconPlayer.TxStopTone());
        AddEarconButton(EarconExplorerContent, "Hard Kill Tone", () => EarconPlayer.HardKillTone());

        // Filter Sounds
        AddSectionHeader(EarconExplorerContent, "Filter Sounds");
        AddEarconButton(EarconExplorerContent, "Filter Edge Enter", () => EarconPlayer.FilterEdgeEnterTone());
        AddEarconButton(EarconExplorerContent, "Filter Edge Exit", () => EarconPlayer.FilterEdgeExitTone());
        AddEarconButton(EarconExplorerContent, "Filter Edge Move", () => EarconPlayer.FilterEdgeMoveTone());
        AddEarconButton(EarconExplorerContent, "Filter Boundary Hit (Low)", () => EarconPlayer.FilterBoundaryHitTone(true));
        AddEarconButton(EarconExplorerContent, "Filter Boundary Hit (High)", () => EarconPlayer.FilterBoundaryHitTone(false));
        AddEarconButton(EarconExplorerContent, "Filter Squeeze", () => EarconPlayer.FilterSqueezeTone());
        AddEarconButton(EarconExplorerContent, "Filter Stretch", () => EarconPlayer.FilterStretchTone());

        // Alerts
        AddSectionHeader(EarconExplorerContent, "Alerts");
        AddEarconButton(EarconExplorerContent, "Band Boundary Beep", () => EarconPlayer.BandBoundaryBeep());
        AddEarconButton(EarconExplorerContent, "Chirp (400 to 800 Hz)", () => EarconPlayer.Chirp(400, 800, 200));
        AddEarconButton(EarconExplorerContent, "Chirp (800 to 400 Hz)", () => EarconPlayer.Chirp(800, 400, 200));
    }

    private static void AddEarconButton(StackPanel parent, string label, Action playAction)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

        var button = new Button
        {
            Content = $"Play: {label}",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(button, $"Play {label}");
        button.Click += (s, e) =>
        {
            ScreenReaderOutput.Speak(label, VerbosityLevel.Terse);
            playAction();
        };

        panel.Children.Add(button);
        parent.Children.Add(panel);
    }

    #endregion

    #region Toolbar Handlers

    private void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        var presets = GetPresetsCallback?.Invoke();
        if (presets == null || presets.Presets.Count == 0)
        {
            ScreenReaderOutput.Speak("No presets available", VerbosityLevel.Terse);
            return;
        }

        // Build a simple picker dialog
        var picker = new JJFlexDialog { Title = "Load Audio Preset", Width = 350, Height = 300 };
        picker.ResizeMode = ResizeMode.NoResize;
        var panel = new DockPanel { Margin = new Thickness(12) };

        var listBox = new ListBox { Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(listBox, "Audio presets");
        foreach (var p in presets.Presets)
            listBox.Items.Add(p.Name);
        if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
        DockPanel.SetDock(listBox, Dock.Top);
        panel.Children.Add(listBox);

        // Delete lives here rather than on the toolbar because this is where
        // the selection is — the help page has promised it for a while and the
        // action never existed anywhere. Confirmed before doing anything: a
        // preset is small but there is no undo, and the built-in three only
        // come back by deleting the whole preset file.
        void DeleteSelected()
        {
            int idx = listBox.SelectedIndex;
            if (idx < 0) return;
            var preset = presets.Presets[idx];

            var confirm = new ConfirmActionDialog(
                "Delete Preset",
                $"This deletes {preset.FormatForSpeech()} from your saved presets. " +
                "There is no undo — to get it back you would save or import it again. " +
                "Nothing on the radio changes.",
                question: "Delete it?",
                yesLabel: "_Delete");
            if (confirm.ShowDialog() != true)
                return;

            presets.Presets.RemoveAt(idx);
            listBox.Items.RemoveAt(idx);

            // A delete that could not be written is a delete that undoes itself
            // the next time the list is read, so it must not be announced as
            // done. The list still updates — the operator asked for it and
            // seeing it linger would be its own lie — but the words say what
            // will actually be true tomorrow.
            bool saved = PersistPresets(presets);
            string outcome = saved
                ? $"Preset {preset.Name} deleted"
                : $"Preset {preset.Name} removed from the list, but " + PresetSaveFailed
                  + " It will be back next time.";
            var level = saved ? VerbosityLevel.Terse : VerbosityLevel.Critical;

            if (listBox.Items.Count == 0)
            {
                // Nothing left to load — the picker has no job now.
                ScreenReaderOutput.Speak(
                    saved ? outcome + ". No presets left." : outcome, level);
                picker.Close();
                return;
            }
            listBox.SelectedIndex = Math.Min(idx, listBox.Items.Count - 1);
            listBox.Focus();
            ScreenReaderOutput.Speak(outcome, level);
        }

        // The Delete key on the list is the primary route; the button is the
        // discoverable one. The button carries NO Alt mnemonic on purpose —
        // WPF access keys match with Shift held, and Alt+D would shadow the
        // global Alt+Shift+D chord (same trap the toolbar's old Alt+S sprang
        // on Speak Transmit Status).
        listBox.PreviewKeyDown += (s2, e2) =>
        {
            if (e2.Key == Key.Delete)
            {
                DeleteSelected();
                e2.Handled = true;
            }
        };

        var okBtn = new Button { Content = "OK", MinWidth = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var deleteBtn = new Button { Content = "Delete", MinWidth = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80, Height = 28, IsCancel = true };
        AutomationProperties.SetName(okBtn, "OK");
        AutomationProperties.SetName(deleteBtn, "Delete preset");
        AutomationProperties.SetAcceleratorKey(deleteBtn, "Delete");
        AutomationProperties.SetName(cancelBtn, "Cancel");
        okBtn.Click += (s2, e2) =>
        {
            if (listBox.SelectedIndex >= 0 && _rig != null)
            {
                var preset = presets.Presets[listBox.SelectedIndex];
                preset.ApplyTo(_rig);
                PollTxAudio();
                ScreenReaderOutput.Speak($"Preset {preset.Name} loaded", VerbosityLevel.Terse);
            }
            picker.Close();
        };
        deleteBtn.Click += (s2, e2) => DeleteSelected();
        cancelBtn.Click += (s2, e2) => picker.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(deleteBtn);
        buttons.Children.Add(cancelBtn);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        picker.Content = panel;
        picker.ShowDialog();
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_rig == null)
        {
            ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Critical);
            return;
        }

        // Prompt for name with a simple input dialog
        var inputDialog = new JJFlexDialog { Title = "Save Audio Preset", Width = 350, Height = 180 };
        inputDialog.ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(12) };

        var prompt = new TextBlock { Text = "Enter a name for this preset:", Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(prompt, "Enter a name for this preset");
        panel.Children.Add(prompt);

        var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(nameBox, "Preset name");
        panel.Children.Add(nameBox);

        var okBtn = new Button { Content = "OK", MinWidth = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80, Height = 28, IsCancel = true };
        AutomationProperties.SetName(okBtn, "OK");
        AutomationProperties.SetName(cancelBtn, "Cancel");
        okBtn.Click += (s2, e2) =>
        {
            string name = nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                ScreenReaderOutput.Speak("Please enter a name", VerbosityLevel.Terse);
                return;
            }
            var preset = AudioChainPreset.CaptureFrom(_rig, name);
            var presets = GetPresetsCallback?.Invoke() ?? AudioChainPresets.CreateDefaults();
            presets.Presets.Add(preset);
            if (PersistPresets(presets))
                ScreenReaderOutput.Speak($"Preset {name} saved", VerbosityLevel.Terse);
            else
                ScreenReaderOutput.Speak($"Preset {name}. " + PresetSaveFailed,
                    VerbosityLevel.Critical);
            inputDialog.Close();
        };
        cancelBtn.Click += (s2, e2) => inputDialog.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);

        inputDialog.Content = panel;
        inputDialog.ShowDialog();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_rig == null)
        {
            ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Critical);
            return;
        }

        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Audio Preset (*.xml)|*.xml",
            DefaultExt = ".xml",
            FileName = "audio_preset.xml"
        };

        if (sfd.ShowDialog() == true)
        {
            var preset = AudioChainPreset.CaptureFrom(_rig, System.IO.Path.GetFileNameWithoutExtension(sfd.FileName));
            preset.Save(sfd.FileName);
            ScreenReaderOutput.Speak($"Preset exported to {System.IO.Path.GetFileName(sfd.FileName)}", VerbosityLevel.Terse);
        }
    }

    /// <summary>
    /// Import a preset file into the saved collection — the missing half of
    /// Export, which for a while produced files nothing could read back,
    /// including on the friend's machine that is the whole point of exporting.
    /// Deliberately does NOT apply the preset to the radio: importing a file is
    /// not a request to retune a live transmitter. No rig required either — a
    /// preset is a file, and the callbacks are wired radio-or-not (see the
    /// MainWindow wiring note on GetPresetsCallback).
    /// </summary>
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio Preset (*.xml)|*.xml|All Files (*.*)|*.*",
            DefaultExt = ".xml"
        };
        if (ofd.ShowDialog() != true) return;

        if (!AudioChainPreset.TryLoad(ofd.FileName, out var preset))
        {
            // Honest failure: a bad file must never quietly become a blank
            // preset in the list.
            ScreenReaderOutput.Speak(
                $"{System.IO.Path.GetFileName(ofd.FileName)} could not be read as an audio preset. Nothing was imported.",
                VerbosityLevel.Critical);
            return;
        }

        if (string.IsNullOrWhiteSpace(preset.Name))
            preset.Name = System.IO.Path.GetFileNameWithoutExtension(ofd.FileName);

        var presets = GetPresetsCallback?.Invoke() ?? AudioChainPresets.CreateDefaults();

        // Two presets with one name are indistinguishable by ear in the Load
        // picker, so a colliding import gets a numbered name instead.
        string baseName = preset.Name;
        int n = 2;
        while (presets.Presets.Exists(p => p.Name == preset.Name))
            preset.Name = $"{baseName} {n++}";

        presets.Presets.Add(preset);
        if (PersistPresets(presets))
            ScreenReaderOutput.Speak(
                $"Imported {preset.FormatForSpeech()}. Added to your saved presets; the radio is unchanged until you load it.",
                VerbosityLevel.Terse);
        else
            ScreenReaderOutput.Speak(
                $"{preset.Name} was read from the file, but " + PresetSaveFailed,
                VerbosityLevel.Critical);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_rig == null)
        {
            ScreenReaderOutput.Speak("No radio connected", VerbosityLevel.Critical);
            return;
        }

        var defaults = new AudioChainPreset();
        defaults.ApplyTo(_rig);
        PollTxAudio();
        ScreenReaderOutput.Speak("Audio settings reset to defaults", VerbosityLevel.Terse);
    }

    #endregion

    #region Control Factories

    private static TextBlock AddSectionHeader(StackPanel parent, string text)
    {
        var header = new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 8, 0, 4),
            FontSize = 13
        };
        AutomationProperties.SetName(header, text);
        parent.Children.Add(header);
        return header;
    }

    private static CheckBox MakeToggle(string label)
    {
        var cb = new CheckBox
        {
            Content = label,
            Margin = new Thickness(2),
            FontSize = 12
        };
        AutomationProperties.SetName(cb, label);
        return cb;
    }

    private static ValueFieldControl MakeValue(string label, int min, int max, int step)
    {
        var ctl = new ValueFieldControl();
        ctl.Setup(label, min, max, step);
        return ctl;
    }

    private static CycleFieldControl MakeCycle(string label, string[] options)
    {
        var ctl = new CycleFieldControl();
        ctl.Setup(label, options);
        return ctl;
    }

    private static TextBlock MakeMeterLabel(string initialText)
    {
        var label = new TextBlock
        {
            Text = initialText,
            Margin = new Thickness(2, 4, 2, 4),
            FontSize = 12
        };
        AutomationProperties.SetName(label, initialText);
        AutomationProperties.SetLiveSetting(label, AutomationLiveSetting.Polite);
        return label;
    }

    private void SetToggle(string label, Action<FlexBase.OffOnValues> setter, bool isOn)
    {
        if (_polling || _rig == null) return;
        setter(isOn ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off);
        if (isOn) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
        ScreenReaderOutput.Speak($"{label} {(isOn ? "on" : "off")}", VerbosityLevel.Terse, interrupt: true);
    }

    #endregion

    #region Audio Check Session engine

    /// <summary>
    /// One "hear yourself" session: key the transmitter through the PTT
    /// safety controller, hold the adjust-and-listen loop open, and restore
    /// every state the session changed on the way out — unconditionally, on
    /// every exit path (Escape, Stop button, timeout, hard kill, ALC
    /// release, dialog close, radio disconnect).
    ///
    /// Safety architecture: the controller owns ALL keying and safety timers
    /// (warning ladder, license lockout, 15-minute hard kill). This class
    /// adds only session bookkeeping on a 1-second watcher: spoken elapsed
    /// reminders, the record/auto-play flow, state restoration, and the
    /// hardware-keying honesty check. It never keys the radio itself and
    /// never builds a second safety timer stack — the 3-minute check timeout
    /// rides the controller's SessionTimeoutOverrideSeconds hook.
    /// </summary>
    private sealed class AudioCheckSession
    {
        private enum Phase { Idle, Keyed, AwaitPlayback, Done }

        private readonly AudioWorkshopDialog _owner;
        private readonly PttSafetyController _ptt;
        private readonly AudioCheckListenMethods _method;
        private readonly AudioCheckPowerModes _powerMode;
        private readonly int _lowPowerWatts;
        private readonly bool _loopback;
        private readonly DispatcherTimer _watcher;

        private Phase _phase = Phase.Idle;
        private DateTime _keyedAt;
        private int _lastMinuteSpoken;
        private bool _bufferWarned;
        private bool _noKeyWarned;
        private bool _hardwareWarned;
        private int _postUnkeyTicks;
        private int _awaitPlaybackTicks;

        // State the session changed and must restore.
        private int _savedPower;
        private bool _powerTouched;
        private bool _monitorTouched; // we only ever turn monitor ON; restore = off
        private bool _dummyEngaged;   // WE turned dummy load on; disable restores power
        private int _dummySavedPower; // pre-engage watts, for the spoken restore line

        private const int CheckTimeoutSeconds = 180;   // 3-minute soft timeout
        private const int RecordBufferSeconds = 120;   // verified live cap

        public bool Active { get; private set; }

        /// <summary>
        /// True while Escape must unkey rather than close the dialog. Covers
        /// the hardware-keying edge: if a hardware PTT line holds the rig in
        /// TX after software unkey, Escape keeps warning instead of closing.
        /// </summary>
        public bool EscapeStopsTransmit =>
            Active && (_ptt.IsTransmitting || Rig?.Transmit == true);

        private FlexBase? Rig => _owner._rig;

        public AudioCheckSession(AudioWorkshopDialog owner, FlexBase rig,
            PttSafetyController ptt, AudioCheckListenMethods method,
            AudioCheckPowerModes powerMode, int lowPowerWatts,
            bool loopback = false)
        {
            _owner = owner;
            _ptt = ptt;
            _method = method;
            _powerMode = powerMode;
            _lowPowerWatts = Math.Clamp(lowPowerWatts, 1, 100);
            _loopback = loopback;
            _watcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _watcher.Tick += Watcher_Tick;
        }

        /// <summary>
        /// Start the check. Returns false (with everything restored and a
        /// spoken explanation) when keying did not happen.
        /// </summary>
        public bool Start()
        {
            var rig = Rig;
            if (rig == null) return false;

            // Loopback mode: the arrangement (FlexBase) already owns power
            // (1 W), monitor (off), antennas and full duplex — the session
            // must not double-manage them.
            bool monitorTurnedOn = false;
            bool recorderAlreadyRunning = false;
            int effectivePower = _loopback ? 1 : rig.XmitPower;

            if (!_loopback)
            {
                // Power handling BEFORE keying. Dummy load (the default)
                // rides FlexBase.DummyLoadMode — it zeroes transmit and
                // tune power and restores both on disable, and the PTT
                // safety controller already skips the ALC auto-release
                // while it is active. If the operator engaged dummy load
                // themselves (the Transmit menu toggle), we leave it
                // theirs: the check neither re-engages nor releases it.
                int currentPower = rig.XmitPower;
                if (_powerMode == AudioCheckPowerModes.DummyLoad)
                {
                    if (!rig.DummyLoadMode)
                    {
                        _dummySavedPower = currentPower;
                        rig.DummyLoadMode = true;
                        _dummyEngaged = true;
                    }
                    effectivePower = 0;
                }
                else if (!rig.DummyLoadMode && currentPower > _lowPowerWatts)
                {
                    // Low power is a CAP: it only ever lowers power, never
                    // raises it — and it cannot override an active dummy
                    // load (touching XmitPower under dummy load would
                    // corrupt its saved-power restore).
                    _savedPower = currentPower;
                    _powerTouched = true;
                    rig.XmitPower = _lowPowerWatts;
                    effectivePower = _lowPowerWatts;
                }

                if (_method == AudioCheckListenMethods.Monitor &&
                    rig.Monitor != FlexBase.OffOnValues.on)
                {
                    _monitorTouched = true;
                    monitorTurnedOn = true;
                    rig.Monitor = FlexBase.OffOnValues.on;
                }

                if (_method == AudioCheckListenMethods.RecordPlayback)
                {
                    // Re-arm race guard (a live re-arm nearly wiped an
                    // operator's takes): stop playback before arming, and
                    // never blind-toggle a recorder that is already running.
                    if (rig.SlicePlayOn) rig.SlicePlayOn = false;
                    if (rig.SliceRecordOn)
                        recorderAlreadyRunning = true;
                    else
                        rig.SliceRecordOn = true;
                }
            }

            // 3-minute soft timeout for the check — the controller's ladder
            // and hard kill continue to apply unchanged.
            _ptt.SessionTimeoutOverrideSeconds = CheckTimeoutSeconds;

            // Key through the controller. Its own key-down announcement fires
            // first; the full safety line follows with interrupt so the
            // complete utterance the operator hears is frequency, power,
            // source, and how to stop. Key-down is never silent even if this
            // class faults between the two.
            _ptt.ToggleLock();
            if (!_ptt.IsTransmitting)
            {
                // Refused (license lockout spoke its own warning, or no
                // power). Roll back everything we touched.
                _ptt.SessionTimeoutOverrideSeconds = null;
                RestoreChangedState(rig, speak: false);
                if (!_loopback && _method == AudioCheckListenMethods.RecordPlayback && !recorderAlreadyRunning)
                    rig.SliceRecordOn = false;
                ScreenReaderOutput.Speak("Audio check could not start.",
                    VerbosityLevel.Critical);
                return false;
            }

            var line = new StringBuilder();
            if (_loopback)
            {
                // HONESTY (ratified product framing): this is real RF through
                // a massively overloaded receiver. It proves presence,
                // processing, and rough shape — never claim a faithful
                // off-air listen. An SDR on a real antenna is ground truth.
                line.Append($"Loopback check. Transmitting at one watt into the transverter port on {FormatMHz(rig.TXFrequency)}, audio from {SourceFriendlyName(rig.MicSource)}.");
                line.Append(" You will hear your own signal through an overloaded receiver: it proves your audio is present and processed, not exactly how you sound on the air.");
                if (rig.LoopbackDriveManaged)
                    line.Append(" Transverter drive reduced for a cleaner listen.");
                line.Append(" This also proves your transmitter chain end to end.");
            }
            else
            {
                // The safety line names the MODE, not just the number
                // (Noel, 2026-08-11): "transmitting at zero watts" is
                // technically true under dummy load and genuinely
                // confusing — it invites the operator to wonder what
                // failed. Checked against the rig's live DummyLoadMode,
                // not our own flag, so an operator-engaged dummy load is
                // named just as honestly as one this check engaged.
                if (rig.DummyLoadMode)
                {
                    line.Append($"Audio check, dummy load, no RF. Keyed on {FormatMHz(rig.TXFrequency)}, audio from {SourceFriendlyName(rig.MicSource)}.");
                }
                else
                {
                    line.Append($"Audio check, transmitting on {FormatMHz(rig.TXFrequency)} at {effectivePower} {(effectivePower == 1 ? "watt" : "watts")}, audio from {SourceFriendlyName(rig.MicSource)}.");
                    if (_powerTouched)
                        line.Append($" Power reduced from {_savedPower} watts for the check.");
                }
                if (monitorTurnedOn)
                    line.Append(" Monitor on.");
                if (recorderAlreadyRunning)
                    line.Append(" Recorder was already running; using it.");
                else if (_method == AudioCheckListenMethods.RecordPlayback)
                    line.Append(" Recording; your take plays back when you stop.");
                if (rig.RemoteRig && _method == AudioCheckListenMethods.Monitor)
                    line.Append(" Over remote, monitor audio arrives delayed; record and play back is recommended.");
            }
            // Audio Track C: when the test tone is riding this transmission,
            // the safety line says so — including the passband warning if the
            // tone sits outside the TX filter (a check that transmits nothing
            // must never sound like a check).
            string? toneLine = _owner.BuildToneAnnouncement();
            if (!string.IsNullOrEmpty(toneLine))
                line.Append(' ').Append(toneLine);
            line.Append(" Escape stops.");
            ScreenReaderOutput.Speak(line.ToString(), VerbosityLevel.Critical, interrupt: true);

            _keyedAt = DateTime.UtcNow;
            _phase = Phase.Keyed;
            Active = true;
            _watcher.Start();
            return true;
        }

        /// <summary>
        /// Operator-initiated stop (Escape stage one, Stop button). Unkeys
        /// through the controller and restores session state. The dialog
        /// stays open.
        /// </summary>
        public void StopCheck()
        {
            if (!Active) return;
            if (_phase == Phase.Keyed)
                HandleUnkey(external: false);
        }

        /// <summary>
        /// Terminal teardown: dialog closing or radio gone. Unkeys if still
        /// keyed, restores what can be restored, stops playback, ends the
        /// session. Safe to call repeatedly.
        /// </summary>
        public void ForceEnd(string reason)
        {
            if (!Active)
            {
                _watcher.Stop();
                return;
            }

            var rig = Rig;
            if (_phase == Phase.Keyed)
            {
                if (_ptt.IsTransmitting) _ptt.EscapeUnlock();
                ScreenReaderOutput.Speak("Transmit off.", VerbosityLevel.Critical);
                if (rig != null && _method == AudioCheckListenMethods.RecordPlayback)
                    rig.SliceRecordOn = false;
            }
            if (rig != null)
            {
                RestoreChangedState(rig, speak: false);
                if (rig.SlicePlayOn) rig.SlicePlayOn = false;
            }
            End(reason);
        }

        private void End(string? reason)
        {
            _phase = Phase.Done;
            Active = false;
            _watcher.Stop();
            _ptt.SessionTimeoutOverrideSeconds = null;
            if (!string.IsNullOrEmpty(reason))
                ScreenReaderOutput.Speak(reason, VerbosityLevel.Terse);
            _owner.OnSessionEnded();
        }

        /// <summary>
        /// The unkey path every stop flows through. Announces key-up
        /// unconditionally (safety-critical, not polish — live wire-keying
        /// once left an operator transmitting unaware), restores power and
        /// monitor, and hands record sessions to the auto-play flow.
        /// </summary>
        private void HandleUnkey(bool external)
        {
            if (!external && _ptt.IsTransmitting)
                _ptt.EscapeUnlock(); // controller speaks its own key-up line

            var rig = Rig;
            var msg = new StringBuilder("Transmit off.");

            if (rig != null)
            {
                if (_dummyEngaged)
                {
                    // Disabling dummy load restores transmit AND tune power
                    // inside FlexBase; the spoken value is the pre-engage
                    // reading (the live getter may not have echoed yet).
                    rig.DummyLoadMode = false;
                    msg.Append($" Dummy load released, power back to {_dummySavedPower} watts.");
                    _dummyEngaged = false;
                }
                if (_powerTouched)
                {
                    rig.XmitPower = _savedPower;
                    msg.Append($" Power restored to {_savedPower} watts.");
                    _powerTouched = false;
                }
                if (_monitorTouched)
                {
                    rig.Monitor = FlexBase.OffOnValues.off;
                    msg.Append(" Monitor restored off.");
                    _monitorTouched = false;
                }
                if (_method == AudioCheckListenMethods.RecordPlayback)
                {
                    rig.SliceRecordOn = false;
                    msg.Append(" Playing your take in a moment.");
                }

                // The verdict — the whole point of the check. Peak SC_MIC over
                // the keyed window (reset at key-down via ToggleLock); honest for
                // PC audio AND the analog mic. -140 guards "no meter yet".
                if (rig.ScMicMaxDb > -140f)
                    msg.Append($" Your mic audio was {AudioWorkshopDialog.MicAudioVerdict(rig.ScMicMaxDb)}, peak {rig.ScMicMaxDb:F0} dBFS.");
            }

            // Unconditional key-up announcement (interrupt false so the
            // controller's own "Receiving" isn't stomped when enabled).
            ScreenReaderOutput.Speak(msg.ToString(), VerbosityLevel.Critical);

            _postUnkeyTicks = 0;
            if (_method == AudioCheckListenMethods.RecordPlayback && rig != null)
            {
                _phase = Phase.AwaitPlayback;
                _awaitPlaybackTicks = 0;
            }
            else
            {
                // Stay alive briefly for the hardware-keying honesty check.
                _phase = Phase.AwaitPlayback; // reuse the post-unkey path
                _awaitPlaybackTicks = int.MinValue; // sentinel: no playback wanted
            }
        }

        private void Watcher_Tick(object? sender, EventArgs e)
        {
            var rig = Rig;
            if (rig == null)
            {
                ForceEnd("Radio disconnected, audio check ended");
                return;
            }

            switch (_phase)
            {
                case Phase.Keyed:
                    TickKeyed(rig);
                    break;
                case Phase.AwaitPlayback:
                    TickPostUnkey(rig);
                    break;
                default:
                    _watcher.Stop();
                    break;
            }
        }

        private void TickKeyed(FlexBase rig)
        {
            // External unkey: timeout, hard kill, ALC auto-release, or a PTT
            // chord outside this dialog. The controller already spoke; we
            // restore state and finish the flow.
            if (!_ptt.IsTransmitting)
            {
                HandleUnkey(external: true);
                return;
            }

            var elapsed = (DateTime.UtcNow - _keyedAt).TotalSeconds;

            // Radio never actually keyed (interlock, wiring): say so once.
            if (!_noKeyWarned && elapsed >= 3 && !rig.Transmit)
            {
                _noKeyWarned = true;
                ScreenReaderOutput.Speak(
                    "The radio did not key. Check interlocks and the mic source.",
                    VerbosityLevel.Critical, interrupt: true);
            }

            // Spoken elapsed reminders, once per minute.
            int minutes = (int)(elapsed / 60);
            if (minutes > _lastMinuteSpoken)
            {
                _lastMinuteSpoken = minutes;
                ScreenReaderOutput.Speak(
                    minutes == 1 ? "Audio check, one minute." : $"Audio check, {minutes} minutes.",
                    VerbosityLevel.Terse);
            }

            // Record buffer is a 120-second ring — warn before the oldest
            // material starts falling off.
            if (_method == AudioCheckListenMethods.RecordPlayback &&
                !_bufferWarned && elapsed >= RecordBufferSeconds - 10)
            {
                _bufferWarned = true;
                ScreenReaderOutput.Speak(
                    "Recording buffer nearly full; oldest audio will drop off.",
                    VerbosityLevel.Terse);
            }
        }

        private void TickPostUnkey(FlexBase rig)
        {
            _postUnkeyTicks++;

            // Hardware-keying honesty: software unkey cannot override a
            // hardware PTT line (front-panel mic, ACC, rear RCA). If the rig
            // is still transmitting after our unkey, say exactly that.
            if (!_hardwareWarned && _postUnkeyTicks >= 2 && rig.Transmit && !_ptt.IsTransmitting)
            {
                _hardwareWarned = true;
                ScreenReaderOutput.Speak(
                    $"Warning: the radio is still transmitting. Hardware keying is active, source {rig.PttSourceName}. " +
                    "Software cannot unkey a hardware line; release the hand mic or rear P T T line.",
                    VerbosityLevel.Critical, interrupt: true);
                return; // keep watching until the hardware line releases
            }
            if (_hardwareWarned && rig.Transmit)
                return; // still keyed by hardware — stay alive, Escape keeps warning
            if (_hardwareWarned && !rig.Transmit)
            {
                ScreenReaderOutput.Speak("Transmitter released.", VerbosityLevel.Critical);
                _hardwareWarned = false;
            }

            if (_awaitPlaybackTicks == int.MinValue)
            {
                // No playback wanted — just the post-unkey grace, then done.
                if (_postUnkeyTicks >= 3)
                    End("Audio check ended.");
                return;
            }

            // Auto-play-on-unkey (the default flow, performed live: unkey to
            // playback within a second — never demands talking and listening
            // at once).
            _awaitPlaybackTicks++;
            if (rig.SlicePlayEnabled)
            {
                rig.SlicePlayOn = true;
                ScreenReaderOutput.Speak(
                    "Playing your take. It carries your full processing chain. Play last take repeats it.",
                    VerbosityLevel.Terse);
                End(null);
            }
            else if (_awaitPlaybackTicks >= 6)
            {
                End("No recording available.");
            }
        }

        private void RestoreChangedState(FlexBase rig, bool speak)
        {
            if (_dummyEngaged)
            {
                rig.DummyLoadMode = false;
                _dummyEngaged = false;
                if (speak)
                    ScreenReaderOutput.Speak($"Dummy load released, power back to {_dummySavedPower} watts.", VerbosityLevel.Terse);
            }
            if (_powerTouched)
            {
                rig.XmitPower = _savedPower;
                _powerTouched = false;
                if (speak)
                    ScreenReaderOutput.Speak($"Power restored to {_savedPower} watts.", VerbosityLevel.Terse);
            }
            if (_monitorTouched)
            {
                rig.Monitor = FlexBase.OffOnValues.off;
                _monitorTouched = false;
                if (speak)
                    ScreenReaderOutput.Speak("Monitor restored off.", VerbosityLevel.Terse);
            }
        }

        private static string FormatMHz(ulong hz)
        {
            return $"{hz / 1e6:0.000###} megahertz";
        }

        private static string SourceFriendlyName(string micSource)
        {
            if (string.IsNullOrEmpty(micSource)) return "an unknown source";
            return micSource.Equals("PC", StringComparison.OrdinalIgnoreCase)
                ? "this computer"
                : $"the {micSource} input";
        }
    }

    #endregion
}
