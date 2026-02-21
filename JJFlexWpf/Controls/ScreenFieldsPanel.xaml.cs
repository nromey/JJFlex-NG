using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Radios;

namespace JJFlexWpf.Controls;

/// <summary>
/// Sprint 14: Expandable screen fields panel for direct keyboard access to radio parameters.
/// Contains Expander categories (DSP, Audio, Receiver, TX, Antenna) with focusable controls.
///
/// Navigation:
///   Tab/Shift+Tab — move between fields and category headers
///   Ctrl+Tab / Ctrl+Shift+Tab — jump between category headers
///   Escape — return focus to FreqOut
///   Space — toggle CheckBox / expand/collapse Expander
///   Up/Down — adjust ValueFieldControl / cycle CycleFieldControl
/// </summary>
public partial class ScreenFieldsPanel : UserControl
{
    private FlexBase? _rig;
    private bool _polling;

    /// <summary>Fired when user presses Escape — MainWindow wires this to FreqOut.FocusDisplay().</summary>
    public event EventHandler? EscapePressed;

    // All Expanders for Ctrl+Tab navigation
    private readonly List<Expander> _expanders = new();

    #region DSP Controls

    private CheckBox _neuralNrCheck = null!;
    private CheckBox _spectralNrCheck = null!;
    private CheckBox _legacyNrCheck = null!;
    private ValueFieldControl _nrLevelControl = null!;
    private CheckBox _nbCheck = null!;
    private ValueFieldControl _nbLevelControl = null!;
    private CheckBox _wnbCheck = null!;
    private ValueFieldControl _wnbLevelControl = null!;
    private CheckBox _fftNotchCheck = null!;
    private CheckBox _legacyNotchCheck = null!;
    private CheckBox _apfCheck = null!;

    #endregion

    #region Audio Controls

    private CheckBox _muteCheck = null!;
    private ValueFieldControl _volumeControl = null!;
    private ValueFieldControl _panControl = null!;
    private ValueFieldControl _headphoneControl = null!;
    private ValueFieldControl _lineoutControl = null!;

    #endregion

    #region Receiver Controls

    private CycleFieldControl _agcModeControl = null!;
    private ValueFieldControl _agcThresholdControl = null!;
    private CheckBox _squelchCheck = null!;
    private ValueFieldControl _squelchLevelControl = null!;
    private ValueFieldControl _rfGainControl = null!;

    #endregion

    #region TX Controls

    private ValueFieldControl _txPowerControl = null!;
    private CheckBox _voxCheck = null!;
    private ValueFieldControl _tunePowerControl = null!;

    #endregion

    #region Antenna Controls

    private CheckBox _atuCheck = null!;
    private CycleFieldControl _atuModeControl = null!;

    #endregion

    public ScreenFieldsPanel()
    {
        InitializeComponent();
        BuildControls();

        _expanders.Add(DspExpander);
        _expanders.Add(AudioExpander);
        _expanders.Add(ReceiverExpander);
        _expanders.Add(TxExpander);
        _expanders.Add(AntennaExpander);
    }

    /// <summary>
    /// Wire the panel to a connected radio. Call after radio connection.
    /// </summary>
    public void Initialize(FlexBase rig)
    {
        _rig = rig;

        // Antenna category always visible — ATU is on all supported Flex radios

        // RF Gain bounds vary by radio model — update from connected radio
        _rfGainControl.Min = rig.RFGainMin;
        _rfGainControl.Max = rig.RFGainMax;
        _rfGainControl.Step = rig.RFGainIncrement;

        // Force initial poll to populate values
        PollUpdate();
    }

    /// <summary>
    /// Disconnect from the radio (on disconnect or power off).
    /// </summary>
    public void Detach()
    {
        _rig = null;
    }

    #region Build Controls

    private void BuildControls()
    {
        BuildDSPControls();
        BuildAudioControls();
        BuildReceiverControls();
        BuildTXControls();
        BuildAntennaControls();
    }

    private void BuildDSPControls()
    {
        _neuralNrCheck = MakeToggle("Neural NR (RNN)");
        _neuralNrCheck.Checked += (s, e) => ToggleRig("Neural NR", v => { if (_rig != null) _rig.NeuralNoiseReduction = v; }, true);
        _neuralNrCheck.Unchecked += (s, e) => ToggleRig("Neural NR", v => { if (_rig != null) _rig.NeuralNoiseReduction = v; }, false);
        DspContent.Children.Add(_neuralNrCheck);

        _spectralNrCheck = MakeToggle("Spectral NR (NRS)");
        _spectralNrCheck.Checked += (s, e) => ToggleRig("Spectral NR", v => { if (_rig != null) _rig.SpectralNoiseReduction = v; }, true);
        _spectralNrCheck.Unchecked += (s, e) => ToggleRig("Spectral NR", v => { if (_rig != null) _rig.SpectralNoiseReduction = v; }, false);
        DspContent.Children.Add(_spectralNrCheck);

        _legacyNrCheck = MakeToggle("Legacy NR");
        _legacyNrCheck.Checked += (s, e) =>
        {
            ToggleRig("Legacy NR", v => { if (_rig != null) _rig.NoiseReductionLegacy = v; }, true);
            _nrLevelControl.Visibility = Visibility.Visible;
        };
        _legacyNrCheck.Unchecked += (s, e) =>
        {
            ToggleRig("Legacy NR", v => { if (_rig != null) _rig.NoiseReductionLegacy = v; }, false);
            _nrLevelControl.Visibility = Visibility.Collapsed;
        };
        DspContent.Children.Add(_legacyNrCheck);

        _nrLevelControl = MakeValue("NR Level", 0, 15, 1);
        _nrLevelControl.Visibility = Visibility.Collapsed;
        _nrLevelControl.ValueChanged += (s, v) => { if (_rig != null) _rig.NoiseReductionLegacyLevel = v; };
        DspContent.Children.Add(_nrLevelControl);

        _nbCheck = MakeToggle("Noise Blanker");
        _nbCheck.Checked += (s, e) =>
        {
            ToggleRig("Noise Blanker", v => { if (_rig != null) _rig.NoiseBlanker = v; }, true);
            _nbLevelControl.Visibility = Visibility.Visible;
        };
        _nbCheck.Unchecked += (s, e) =>
        {
            ToggleRig("Noise Blanker", v => { if (_rig != null) _rig.NoiseBlanker = v; }, false);
            _nbLevelControl.Visibility = Visibility.Collapsed;
        };
        DspContent.Children.Add(_nbCheck);

        _nbLevelControl = MakeValue("NB Level", 0, 100, 5);
        _nbLevelControl.Visibility = Visibility.Collapsed;
        _nbLevelControl.ValueChanged += (s, v) => { if (_rig != null) _rig.NoiseBlankerLevel = v; };
        DspContent.Children.Add(_nbLevelControl);

        _wnbCheck = MakeToggle("Wideband NB");
        _wnbCheck.Checked += (s, e) =>
        {
            ToggleRig("Wideband NB", v => { if (_rig != null) _rig.WidebandNoiseBlanker = v; }, true);
            _wnbLevelControl.Visibility = Visibility.Visible;
        };
        _wnbCheck.Unchecked += (s, e) =>
        {
            ToggleRig("Wideband NB", v => { if (_rig != null) _rig.WidebandNoiseBlanker = v; }, false);
            _wnbLevelControl.Visibility = Visibility.Collapsed;
        };
        DspContent.Children.Add(_wnbCheck);

        _wnbLevelControl = MakeValue("WNB Level", 0, 100, 5);
        _wnbLevelControl.Visibility = Visibility.Collapsed;
        _wnbLevelControl.ValueChanged += (s, v) => { if (_rig != null) _rig.WidebandNoiseBlankerLevel = v; };
        DspContent.Children.Add(_wnbLevelControl);

        _fftNotchCheck = MakeToggle("FFT Auto-Notch");
        _fftNotchCheck.Checked += (s, e) => ToggleRig("FFT Auto-Notch", v => { if (_rig != null) _rig.AutoNotchFFT = v; }, true);
        _fftNotchCheck.Unchecked += (s, e) => ToggleRig("FFT Auto-Notch", v => { if (_rig != null) _rig.AutoNotchFFT = v; }, false);
        DspContent.Children.Add(_fftNotchCheck);

        _legacyNotchCheck = MakeToggle("Legacy Auto-Notch");
        _legacyNotchCheck.Checked += (s, e) => ToggleRig("Legacy Auto-Notch", v => { if (_rig != null) _rig.AutoNotchLegacy = v; }, true);
        _legacyNotchCheck.Unchecked += (s, e) => ToggleRig("Legacy Auto-Notch", v => { if (_rig != null) _rig.AutoNotchLegacy = v; }, false);
        DspContent.Children.Add(_legacyNotchCheck);

        _apfCheck = MakeToggle("Audio Peak Filter");
        _apfCheck.Checked += (s, e) => ToggleRig("APF", v => { if (_rig != null) _rig.APF = v; }, true);
        _apfCheck.Unchecked += (s, e) => ToggleRig("APF", v => { if (_rig != null) _rig.APF = v; }, false);
        DspContent.Children.Add(_apfCheck);
    }

    private void BuildAudioControls()
    {
        _muteCheck = MakeToggle("Mute");
        _muteCheck.Checked += (s, e) => ToggleBoolRig("Mute", v => { if (_rig != null) _rig.SliceMute = v; }, true);
        _muteCheck.Unchecked += (s, e) => ToggleBoolRig("Mute", v => { if (_rig != null) _rig.SliceMute = v; }, false);
        AudioContent.Children.Add(_muteCheck);

        _volumeControl = MakeValue("Volume", 0, 100, 5);
        _volumeControl.ValueChanged += (s, v) => { if (_rig != null) _rig.AudioGain = v; };
        AudioContent.Children.Add(_volumeControl);

        _panControl = MakeValue("Pan", 0, 100, 5);
        _panControl.ValueChanged += (s, v) => { if (_rig != null) _rig.AudioPan = v; };
        AudioContent.Children.Add(_panControl);

        _headphoneControl = MakeValue("Headphone Level", 0, 100, 5);
        _headphoneControl.ValueChanged += (s, v) => { if (_rig != null) _rig.HeadphoneGain = v; };
        AudioContent.Children.Add(_headphoneControl);

        _lineoutControl = MakeValue("Line Out Level", 0, 100, 5);
        _lineoutControl.ValueChanged += (s, v) => { if (_rig != null) _rig.LineoutGain = v; };
        AudioContent.Children.Add(_lineoutControl);
    }

    private void BuildReceiverControls()
    {
        _agcModeControl = MakeCycle("AGC Mode", new[] { "Off", "Slow", "Medium", "Fast" });
        _agcModeControl.SelectionChanged += (s, idx) =>
        {
            if (_rig == null) return;
            var mode = idx switch
            {
                0 => Flex.Smoothlake.FlexLib.AGCMode.Off,
                1 => Flex.Smoothlake.FlexLib.AGCMode.Slow,
                2 => Flex.Smoothlake.FlexLib.AGCMode.Medium,
                3 => Flex.Smoothlake.FlexLib.AGCMode.Fast,
                _ => Flex.Smoothlake.FlexLib.AGCMode.Medium
            };
            _rig.AGCSpeed = mode;
        };
        ReceiverContent.Children.Add(_agcModeControl);

        _agcThresholdControl = MakeValue("AGC Threshold",
            FlexBase.AGCThresholdMin, FlexBase.AGCThresholdMax, FlexBase.AGCThresholdIncrement);
        _agcThresholdControl.ValueChanged += (s, v) => { if (_rig != null) _rig.AGCThreshold = v; };
        ReceiverContent.Children.Add(_agcThresholdControl);

        _squelchCheck = MakeToggle("Squelch");
        _squelchCheck.Checked += (s, e) =>
        {
            ToggleRig("Squelch", v => { if (_rig != null) _rig.Squelch = v; }, true);
            _squelchLevelControl.Visibility = Visibility.Visible;
        };
        _squelchCheck.Unchecked += (s, e) =>
        {
            ToggleRig("Squelch", v => { if (_rig != null) _rig.Squelch = v; }, false);
            _squelchLevelControl.Visibility = Visibility.Collapsed;
        };
        ReceiverContent.Children.Add(_squelchCheck);

        _squelchLevelControl = MakeValue("Squelch Level",
            FlexBase.SquelchLevelMin, FlexBase.SquelchLevelMax, FlexBase.SquelchLevelIncrement);
        _squelchLevelControl.Visibility = Visibility.Collapsed;
        _squelchLevelControl.ValueChanged += (s, v) => { if (_rig != null) _rig.SquelchLevel = v; };
        ReceiverContent.Children.Add(_squelchLevelControl);

        // RF Gain bounds are instance fields (vary by radio), set defaults here, updated in Initialize()
        _rfGainControl = MakeValue("RF Gain", -10, 30, 10);
        _rfGainControl.ValueChanged += (s, v) => { if (_rig != null) _rig.RFGain = v; };
        ReceiverContent.Children.Add(_rfGainControl);
    }

    private void BuildTXControls()
    {
        _txPowerControl = MakeValue("TX Power", 0, 100, 1);
        _txPowerControl.ValueChanged += (s, v) => { if (_rig != null) _rig.XmitPower = v; };
        TxContent.Children.Add(_txPowerControl);

        _voxCheck = MakeToggle("VOX");
        _voxCheck.Checked += (s, e) => ToggleRig("VOX", v => { if (_rig != null) _rig.Vox = v; }, true);
        _voxCheck.Unchecked += (s, e) => ToggleRig("VOX", v => { if (_rig != null) _rig.Vox = v; }, false);
        TxContent.Children.Add(_voxCheck);

        _tunePowerControl = MakeValue("Tune Power", 0, 100, 1);
        _tunePowerControl.ValueChanged += (s, v) => { if (_rig != null) _rig.TunePower = v; };
        TxContent.Children.Add(_tunePowerControl);
    }

    private void BuildAntennaControls()
    {
        _atuCheck = MakeToggle("ATU");
        _atuCheck.Checked += (s, e) => ToggleBoolRig("ATU", v => { if (_rig != null) _rig.FlexTunerOn = v; }, true);
        _atuCheck.Unchecked += (s, e) => ToggleBoolRig("ATU", v => { if (_rig != null) _rig.FlexTunerOn = v; }, false);
        AntennaContent.Children.Add(_atuCheck);

        _atuModeControl = MakeCycle("ATU Mode", new[] { "None", "Manual", "Auto" });
        _atuModeControl.SelectionChanged += (s, idx) =>
        {
            if (_rig == null) return;
            var mode = idx switch
            {
                0 => FlexBase.FlexTunerTypes.none,
                1 => FlexBase.FlexTunerTypes.manual,
                2 => FlexBase.FlexTunerTypes.auto,
                _ => FlexBase.FlexTunerTypes.auto
            };
            _rig.FlexTunerType = mode;
        };
        AntennaContent.Children.Add(_atuModeControl);
    }

    #endregion

    #region Control Factories

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

    #endregion

    #region Rig Toggle Helpers

    private void ToggleRig(string label, Action<FlexBase.OffOnValues> setter, bool isOn)
    {
        if (_polling || _rig == null) return;
        setter(isOn ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off);
        ScreenReaderOutput.Speak($"{label} {(isOn ? "on" : "off")}");
    }

    private void ToggleBoolRig(string label, Action<bool> setter, bool isOn)
    {
        if (_polling || _rig == null) return;
        setter(isOn);
        ScreenReaderOutput.Speak($"{label} {(isOn ? "on" : "off")}");
    }

    #endregion

    #region Polling

    /// <summary>
    /// Update all visible field values from the radio. Called by MainWindow's 100ms poll timer.
    /// Only updates fields in expanded categories for performance.
    /// </summary>
    public void PollUpdate()
    {
        if (_rig == null) return;

        _polling = true;
        try
        {
            if (DspExpander.IsExpanded)
                PollDSP();
            if (AudioExpander.IsExpanded)
                PollAudio();
            if (ReceiverExpander.IsExpanded)
                PollReceiver();
            if (TxExpander.IsExpanded)
                PollTX();
            if (AntennaExpander.IsExpanded && AntennaExpander.Visibility == Visibility.Visible)
                PollAntenna();
        }
        finally
        {
            _polling = false;
        }
    }

    private void PollDSP()
    {
        if (_rig == null) return;

        _neuralNrCheck.IsChecked = _rig.NeuralNoiseReduction == FlexBase.OffOnValues.on;
        _spectralNrCheck.IsChecked = _rig.SpectralNoiseReduction == FlexBase.OffOnValues.on;

        bool legacyNrOn = _rig.NoiseReductionLegacy == FlexBase.OffOnValues.on;
        _legacyNrCheck.IsChecked = legacyNrOn;
        _nrLevelControl.Visibility = legacyNrOn ? Visibility.Visible : Visibility.Collapsed;
        if (legacyNrOn) _nrLevelControl.Value = _rig.NoiseReductionLegacyLevel;

        bool nbOn = _rig.NoiseBlanker == FlexBase.OffOnValues.on;
        _nbCheck.IsChecked = nbOn;
        _nbLevelControl.Visibility = nbOn ? Visibility.Visible : Visibility.Collapsed;
        if (nbOn) _nbLevelControl.Value = _rig.NoiseBlankerLevel;

        bool wnbOn = _rig.WidebandNoiseBlanker == FlexBase.OffOnValues.on;
        _wnbCheck.IsChecked = wnbOn;
        _wnbLevelControl.Visibility = wnbOn ? Visibility.Visible : Visibility.Collapsed;
        if (wnbOn) _wnbLevelControl.Value = _rig.WidebandNoiseBlankerLevel;

        _fftNotchCheck.IsChecked = _rig.AutoNotchFFT == FlexBase.OffOnValues.on;
        _legacyNotchCheck.IsChecked = _rig.AutoNotchLegacy == FlexBase.OffOnValues.on;
        _apfCheck.IsChecked = _rig.APF == FlexBase.OffOnValues.on;

        // APF only visible in CW modes
        string mode = _rig.Mode?.ToUpperInvariant() ?? "";
        bool isCW = mode == "CW" || mode == "CWL" || mode == "CWU";
        _apfCheck.Visibility = isCW ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PollAudio()
    {
        if (_rig == null) return;

        _muteCheck.IsChecked = _rig.SliceMute;
        _volumeControl.Value = _rig.AudioGain;
        _panControl.Value = _rig.AudioPan;
        _headphoneControl.Value = _rig.HeadphoneGain;
        _lineoutControl.Value = _rig.LineoutGain;
    }

    private void PollReceiver()
    {
        if (_rig == null) return;

        var agcMode = _rig.AGCSpeed;
        int agcIndex = agcMode switch
        {
            Flex.Smoothlake.FlexLib.AGCMode.Off => 0,
            Flex.Smoothlake.FlexLib.AGCMode.Slow => 1,
            Flex.Smoothlake.FlexLib.AGCMode.Medium => 2,
            Flex.Smoothlake.FlexLib.AGCMode.Fast => 3,
            _ => 2
        };
        _agcModeControl.SelectedIndex = agcIndex;

        _agcThresholdControl.Value = _rig.AGCThreshold;

        bool squelchOn = _rig.Squelch == FlexBase.OffOnValues.on;
        _squelchCheck.IsChecked = squelchOn;
        _squelchLevelControl.Visibility = squelchOn ? Visibility.Visible : Visibility.Collapsed;
        if (squelchOn) _squelchLevelControl.Value = _rig.SquelchLevel;

        _rfGainControl.Value = _rig.RFGain;
    }

    private void PollTX()
    {
        if (_rig == null) return;

        _txPowerControl.Value = _rig.XmitPower;
        _voxCheck.IsChecked = _rig.Vox == FlexBase.OffOnValues.on;
        _tunePowerControl.Value = _rig.TunePower;
    }

    private void PollAntenna()
    {
        if (_rig == null) return;

        _atuCheck.IsChecked = _rig.FlexTunerOn;

        var atuMode = _rig.FlexTunerType;
        int atuIndex = atuMode switch
        {
            FlexBase.FlexTunerTypes.none => 0,
            FlexBase.FlexTunerTypes.manual => 1,
            FlexBase.FlexTunerTypes.auto => 2,
            _ => 2
        };
        _atuModeControl.SelectedIndex = atuIndex;
    }

    #endregion

    #region Keyboard Navigation

    private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Escape → return to FreqOut
        if (e.Key == Key.Escape)
        {
            EscapePressed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        // Ctrl+Tab → jump to next Expander header
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusNextExpander(forward: true);
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+Tab → jump to previous Expander header
        if (e.Key == Key.Tab && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            FocusNextExpander(forward: false);
            e.Handled = true;
            return;
        }
    }

    private void FocusNextExpander(bool forward)
    {
        // Get visible expanders only
        var visible = new List<Expander>();
        foreach (var exp in _expanders)
        {
            if (exp.Visibility == Visibility.Visible)
                visible.Add(exp);
        }

        if (visible.Count == 0) return;

        // Find which expander the focused element is inside
        var focused = Keyboard.FocusedElement as DependencyObject;
        int currentIndex = -1;

        if (focused != null)
        {
            for (int i = 0; i < visible.Count; i++)
            {
                if (IsDescendantOf(focused, visible[i]))
                {
                    currentIndex = i;
                    break;
                }
            }
        }

        // Calculate next index
        int nextIndex;
        if (currentIndex < 0)
        {
            nextIndex = forward ? 0 : visible.Count - 1;
        }
        else
        {
            nextIndex = forward
                ? (currentIndex + 1) % visible.Count
                : (currentIndex - 1 + visible.Count) % visible.Count;
        }

        // Focus the expander's toggle button (header)
        visible[nextIndex].Focus();
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        var current = element;
        while (current != null)
        {
            if (current == ancestor) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    #endregion
}
