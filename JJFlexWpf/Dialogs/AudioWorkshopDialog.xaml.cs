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
    private Button? _startCheckButton;
    private CycleFieldControl? _listenMethodControl;
    private CheckBox? _lowPowerCheck;
    private Button? _playTakeButton;
    private Button? _loopbackButton;
    private TextBlock? _loopbackInfo;
    private CycleFieldControl? _micSourceControl;
    private TextBlock? _monitorHeader;

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

    // Preset callback (wired from outside)
    public Func<AudioChainPresets>? GetPresetsCallback { get; set; }
    public Action<AudioChainPresets>? SavePresetsCallback { get; set; }

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
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _session != null && _session.EscapeStopsTransmit)
        {
            _session.StopCheck();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
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
            // Clear a stale loopback arrangement flag on the departing rig so
            // a reconnect on the same FlexBase can arrange again. Writes are
            // internally guarded when the underlying radio is gone.
            if (oldRig != null && oldRig.LoopbackArranged)
                oldRig.EndLoopbackArrangement();
            _meterTimer.Stop();
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
        if (_lowPowerCheck != null)
        {
            _polling = true;
            try { _lowPowerCheck.IsChecked = _radioCfg.AudioCheckLowPower; }
            finally { _polling = false; }
        }
    }

    private void SavePerRadioPrefs()
    {
        if (_radioCfg == null || string.IsNullOrEmpty(_radioCfgSerial)) return;
        if (_listenMethodControl != null)
            _radioCfg.AudioCheckListenMethod = (AudioCheckListenMethods)_listenMethodControl.SelectedIndex;
        if (_lowPowerCheck != null)
            _radioCfg.AudioCheckLowPower = _lowPowerCheck.IsChecked == true;
        _radioCfg.SaveForRadio(_radioCfgSerial);
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
        // Audio Check session — the "hear yourself" loop (QB Track G).
        BuildAudioCheckSection();

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
    }

    #region Audio Check Section

    private void BuildAudioCheckSection()
    {
        AddSectionHeader(TxAudioContent, "Audio Check");

        _startCheckButton = new Button
        {
            Content = "Start Audio Check",
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2)
        };
        AutomationProperties.SetName(_startCheckButton, "Start Audio Check");
        _startCheckButton.Click += (s, e) => ToggleAudioCheck();
        TxAudioContent.Children.Add(_startCheckButton);

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

        _lowPowerCheck = MakeToggle("Low power during checks (10 watts)");
        _lowPowerCheck.IsChecked = true; // conservative default; per-radio pref overrides on SetRig
        _lowPowerCheck.Checked += (s, e) => LowPowerChanged(true);
        _lowPowerCheck.Unchecked += (s, e) => LowPowerChanged(false);
        TxAudioContent.Children.Add(_lowPowerCheck);

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

        var session = new AudioCheckSession(this, _rig, ptt,
            AudioCheckListenMethods.Monitor, lowPower: false, loopback: true);
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

    private void LowPowerChanged(bool on)
    {
        if (_polling) return;
        SavePerRadioPrefs();
        ScreenReaderOutput.Speak($"Low power during checks {(on ? "on" : "off")}",
            VerbosityLevel.Terse, interrupt: true);
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
        bool lowPower = _lowPowerCheck?.IsChecked == true;

        var session = new AudioCheckSession(this, _rig, ptt, method, lowPower);
        if (session.Start())
        {
            _session = session;
            SetStartButtonLabel("Stop Audio Check");
            // The existing tab order is the adjust ring — land on the first
            // knob so arrows work immediately.
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

        var okBtn = new Button { Content = "OK", MinWidth = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80, Height = 28, IsCancel = true };
        AutomationProperties.SetName(okBtn, "OK");
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
        cancelBtn.Click += (s2, e2) => picker.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okBtn);
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
            SavePresetsCallback?.Invoke(presets);
            ScreenReaderOutput.Speak($"Preset {name} saved", VerbosityLevel.Terse);
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
        private readonly bool _lowPower;
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
            PttSafetyController ptt, AudioCheckListenMethods method, bool lowPower,
            bool loopback = false)
        {
            _owner = owner;
            _ptt = ptt;
            _method = method;
            _lowPower = lowPower;
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
                // Effective power for the safety line, dropped BEFORE keying.
                int currentPower = rig.XmitPower;
                if (_lowPower && currentPower > 10)
                {
                    _savedPower = currentPower;
                    _powerTouched = true;
                    rig.XmitPower = 10;
                    effectivePower = 10;
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
                line.Append($"Transmitting on {FormatMHz(rig.TXFrequency)}, {effectivePower} watts, audio from {SourceFriendlyName(rig.MicSource)}.");
                if (_powerTouched)
                    line.Append($" Power reduced from {_savedPower} watts for the check.");
                if (monitorTurnedOn)
                    line.Append(" Monitor on.");
                if (recorderAlreadyRunning)
                    line.Append(" Recorder was already running; using it.");
                else if (_method == AudioCheckListenMethods.RecordPlayback)
                    line.Append(" Recording; your take plays back when you stop.");
                if (rig.RemoteRig && _method == AudioCheckListenMethods.Monitor)
                    line.Append(" Over remote, monitor audio arrives delayed; record and play back is recommended.");
            }
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
