using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
    private TextBlock? _alcLabel;
    private TextBlock? _micLevelLabel;
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
            _meterTimer.Stop();
            _instance = null;
        };
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
        _rig = rig;
        if (rig != null)
        {
            PollTxAudio();
            _meterTimer.Start();
        }
        else
        {
            _meterTimer.Stop();
        }
    }

    public void FocusTab(int tabIndex)
    {
        if (tabIndex >= 0 && tabIndex < MainTabs.Items.Count)
            MainTabs.SelectedIndex = tabIndex;
    }

    #region Tab 1: TX Audio

    private void BuildTxAudioTab()
    {
        // Microphone section
        AddSectionHeader(TxAudioContent, "Microphone");

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

        // Monitor section
        AddSectionHeader(TxAudioContent, "TX Monitor");

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

        _alcLabel = MakeMeterLabel("ALC: --");
        LiveMetersContent.Children.Add(_alcLabel);

        _micLevelLabel = MakeMeterLabel("Mic Level: --");
        LiveMetersContent.Children.Add(_micLevelLabel);

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

        if (_alcLabel != null)
            _alcLabel.Text = $"ALC: {_rig.ALC:F2}";

        if (_micLevelLabel != null)
            _micLevelLabel.Text = $"Mic Level: {_rig.MicData:F1}";

        if (_paTempLabel != null)
            _paTempLabel.Text = $"PA Temperature: {_rig.PATemp:F1} °C";

        if (_voltsLabel != null)
            _voltsLabel.Text = $"Supply Voltage: {_rig.Volts:F1} V";
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

    private static void AddSectionHeader(StackPanel parent, string text)
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
}
