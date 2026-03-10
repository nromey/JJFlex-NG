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

    /// <summary>Callback to return focus to the FreqOut control after collapsing a category.</summary>
    public Action? ReturnFocusToFreqOut { get; set; }

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
    private CheckBox _meterToneCheck = null!;
    private CheckBox _peakWatcherCheck = null!;

    #endregion

    #region Audio and Slice Controls

    private CheckBox _muteCheck = null!;
    private ValueFieldControl _volumeControl = null!;
    private ValueFieldControl _panControl = null!;
    private ValueFieldControl _headphoneControl = null!;
    private ValueFieldControl _lineoutControl = null!;

    // Slice management controls (below audio in same expander)
    private Button _createSliceButton = null!;
    private Button _releaseSliceButton = null!;

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
    private ValueFieldControl _micGainControl = null!;
    private CheckBox _micBoostCheck = null!;
    private CheckBox _micBiasCheck = null!;
    private CheckBox _companderCheck = null!;
    private ValueFieldControl _companderLevelControl = null!;
    private CheckBox _processorCheck = null!;
    private CycleFieldControl _processorSettingControl = null!;
    private ValueFieldControl _txFilterLowControl = null!;
    private ValueFieldControl _txFilterHighControl = null!;
    private CheckBox _monitorCheck = null!;
    private ValueFieldControl _monitorLevelControl = null!;

    #endregion

    #region Antenna Controls

    private CycleFieldControl _rxAntennaControl = null!;
    private CycleFieldControl _txAntennaControl = null!;
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

        // Repopulate antenna combos from the connected radio's antenna lists
        var rxAnts = rig.RXAntennaList.ToArray();
        var txAnts = rig.TXAntennaList.ToArray();
        if (rxAnts.Length > 0) _rxAntennaControl.SetOptions(rxAnts);
        if (txAnts.Length > 0) _txAntennaControl.SetOptions(txAnts);

        // Hide NR controls if NR license is not available on this radio
        bool nrAvailable = !(rig.NoiseReductionLicenseReported && !rig.NoiseReductionLicensed);
        _neuralNrCheck.Visibility = nrAvailable ? Visibility.Visible : Visibility.Collapsed;
        _spectralNrCheck.Visibility = nrAvailable ? Visibility.Visible : Visibility.Collapsed;
        _legacyNrCheck.Visibility = nrAvailable ? Visibility.Visible : Visibility.Collapsed;
        _nrLevelControl.Visibility = Visibility.Collapsed; // shown only when Legacy NR is on

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

        // Meter Tones
        _meterToneCheck = MakeToggle("Meter Tones");
        _meterToneCheck.Checked += (s, e) => { if (!_polling) { MeterToneEngine.Enabled = true; ScreenReaderOutput.Speak("Meter tones on"); } };
        _meterToneCheck.Unchecked += (s, e) => { if (!_polling) { MeterToneEngine.Enabled = false; ScreenReaderOutput.Speak("Meter tones off"); } };
        DspContent.Children.Add(_meterToneCheck);

        _peakWatcherCheck = MakeToggle("Peak Watcher");
        _peakWatcherCheck.Checked += (s, e) => { if (!_polling) { MeterToneEngine.PeakWatcherEnabled = true; ScreenReaderOutput.Speak("Peak Watcher on"); } };
        _peakWatcherCheck.Unchecked += (s, e) => { if (!_polling) { MeterToneEngine.PeakWatcherEnabled = false; ScreenReaderOutput.Speak("Peak Watcher off"); } };
        DspContent.Children.Add(_peakWatcherCheck);
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

        // Separator between audio and slice controls
        AudioContent.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });

        // Slice management buttons
        _createSliceButton = new Button
        {
            Content = "Create Slice",
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        System.Windows.Automation.AutomationProperties.SetName(_createSliceButton, "Create a new slice");
        _createSliceButton.Click += (s, e) =>
        {
            if (_rig == null) return;
            if (_rig.MyNumSlices >= _rig.MaxSlices)
            {
                ScreenReaderOutput.Speak("Maximum slices reached");
                return;
            }
            bool ok = _rig.NewSlice();
            if (ok)
                ScreenReaderOutput.Speak($"Slice created, {_rig.MyNumSlices} slices active");
            else
                ScreenReaderOutput.Speak("Could not create slice");
        };
        AudioContent.Children.Add(_createSliceButton);

        _releaseSliceButton = new Button
        {
            Content = "Release Slice",
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        System.Windows.Automation.AutomationProperties.SetName(_releaseSliceButton, "Release the last slice");
        _releaseSliceButton.Click += (s, e) =>
        {
            if (_rig == null) return;
            int numSlices = _rig.MyNumSlices;
            if (numSlices <= 1)
            {
                ScreenReaderOutput.Speak("Cannot release the only slice");
                return;
            }
            // Release the last slice (highest index)
            bool ok = _rig.RemoveSlice(numSlices - 1);
            if (ok)
                ScreenReaderOutput.Speak($"Slice released, {_rig.MyNumSlices} slices active");
            else
                ScreenReaderOutput.Speak("Could not release slice");
        };
        AudioContent.Children.Add(_releaseSliceButton);
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
        _txPowerControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.XmitPower = v; };
        TxContent.Children.Add(_txPowerControl);

        _voxCheck = MakeToggle("VOX");
        _voxCheck.Checked += (s, e) => ToggleRig("VOX", v => { if (_rig != null) _rig.Vox = v; }, true);
        _voxCheck.Unchecked += (s, e) => ToggleRig("VOX", v => { if (_rig != null) _rig.Vox = v; }, false);
        TxContent.Children.Add(_voxCheck);

        _tunePowerControl = MakeValue("Tune Power", 0, 100, 1);
        _tunePowerControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.TunePower = v; };
        TxContent.Children.Add(_tunePowerControl);

        // Mic Gain
        _micGainControl = MakeValue("Mic Gain", 0, 100, 1);
        _micGainControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.MicGain = v; };
        TxContent.Children.Add(_micGainControl);

        // Mic Boost
        _micBoostCheck = MakeToggle("Mic Boost (+20 dB)");
        _micBoostCheck.Checked += (s, e) => ToggleRig("Mic Boost", v => { if (_rig != null) _rig.MicBoost = v; }, true);
        _micBoostCheck.Unchecked += (s, e) => ToggleRig("Mic Boost", v => { if (_rig != null) _rig.MicBoost = v; }, false);
        TxContent.Children.Add(_micBoostCheck);

        // Mic Bias
        _micBiasCheck = MakeToggle("Mic Bias (phantom power)");
        _micBiasCheck.Checked += (s, e) => ToggleRig("Mic Bias", v => { if (_rig != null) _rig.MicBias = v; }, true);
        _micBiasCheck.Unchecked += (s, e) => ToggleRig("Mic Bias", v => { if (_rig != null) _rig.MicBias = v; }, false);
        TxContent.Children.Add(_micBiasCheck);

        // Compander
        _companderCheck = MakeToggle("Compander");
        _companderCheck.Checked += (s, e) =>
        {
            ToggleRig("Compander", v => { if (_rig != null) _rig.Compander = v; }, true);
            _companderLevelControl.Visibility = Visibility.Visible;
        };
        _companderCheck.Unchecked += (s, e) =>
        {
            ToggleRig("Compander", v => { if (_rig != null) _rig.Compander = v; }, false);
            _companderLevelControl.Visibility = Visibility.Collapsed;
        };
        TxContent.Children.Add(_companderCheck);

        // Compander Level (shown when Compander is on)
        _companderLevelControl = MakeValue("Compander Level", 0, 100, 5);
        _companderLevelControl.Visibility = Visibility.Collapsed;
        _companderLevelControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.CompanderLevel = v; };
        TxContent.Children.Add(_companderLevelControl);

        // Speech Processor
        _processorCheck = MakeToggle("Speech Processor");
        _processorCheck.Checked += (s, e) =>
        {
            ToggleRig("Speech Processor", v => { if (_rig != null) _rig.ProcessorOn = v; }, true);
            _processorSettingControl.Visibility = Visibility.Visible;
        };
        _processorCheck.Unchecked += (s, e) =>
        {
            ToggleRig("Speech Processor", v => { if (_rig != null) _rig.ProcessorOn = v; }, false);
            _processorSettingControl.Visibility = Visibility.Collapsed;
        };
        TxContent.Children.Add(_processorCheck);

        // Processor Setting (shown when Processor is on)
        _processorSettingControl = MakeCycle("Processor Mode", new[] { "Normal", "DX", "DX+" });
        _processorSettingControl.Visibility = Visibility.Collapsed;
        _processorSettingControl.SelectionChanged += (s, idx) =>
        {
            if (_rig == null || _polling) return;
            _rig.ProcessorSetting = (FlexBase.ProcessorSettings)idx;
        };
        TxContent.Children.Add(_processorSettingControl);

        // TX Filter Low
        _txFilterLowControl = MakeValue("TX Filter Low", 0, 9950, 50);
        _txFilterLowControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.TXFilterLow = v; };
        TxContent.Children.Add(_txFilterLowControl);

        // TX Filter High
        _txFilterHighControl = MakeValue("TX Filter High", 50, 10000, 50);
        _txFilterHighControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.TXFilterHigh = v; };
        TxContent.Children.Add(_txFilterHighControl);

        // TX Monitor
        _monitorCheck = MakeToggle("TX Monitor");
        _monitorCheck.Checked += (s, e) =>
        {
            ToggleRig("TX Monitor", v => { if (_rig != null) _rig.Monitor = v; }, true);
            _monitorLevelControl.Visibility = Visibility.Visible;
        };
        _monitorCheck.Unchecked += (s, e) =>
        {
            ToggleRig("TX Monitor", v => { if (_rig != null) _rig.Monitor = v; }, false);
            _monitorLevelControl.Visibility = Visibility.Collapsed;
        };
        TxContent.Children.Add(_monitorCheck);

        // Monitor Level (shown when Monitor is on)
        _monitorLevelControl = MakeValue("Monitor Level", 0, 100, 5);
        _monitorLevelControl.Visibility = Visibility.Collapsed;
        _monitorLevelControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.SBMonitorLevel = v; };
        TxContent.Children.Add(_monitorLevelControl);
    }

    private void BuildAntennaControls()
    {
        // RX/TX antenna combos — populated dynamically at Initialize
        _rxAntennaControl = MakeCycle("RX Antenna", new[] { "ANT1", "ANT2" });
        _rxAntennaControl.SelectionChanged += (s, idx) =>
        {
            if (_rig == null) return;
            var list = _rig.RXAntennaList;
            if (idx >= 0 && idx < list.Count)
            {
                _rig.RXAntennaName = list[idx];
                Radios.ScreenReaderOutput.Speak($"RX antenna {list[idx]}");
            }
        };
        AntennaContent.Children.Add(_rxAntennaControl);

        _txAntennaControl = MakeCycle("TX Antenna", new[] { "ANT1", "ANT2" });
        _txAntennaControl.SelectionChanged += (s, idx) =>
        {
            if (_rig == null) return;
            var list = _rig.TXAntennaList;
            if (idx >= 0 && idx < list.Count)
            {
                _rig.TXAntennaName = list[idx];
                Radios.ScreenReaderOutput.Speak($"TX antenna {list[idx]}");
            }
        };
        AntennaContent.Children.Add(_txAntennaControl);

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

        // Only poll NR controls if license is available (controls may be collapsed)
        if (_neuralNrCheck.Visibility == Visibility.Visible)
        {
            _neuralNrCheck.IsChecked = _rig.NeuralNoiseReduction == FlexBase.OffOnValues.on;
            _spectralNrCheck.IsChecked = _rig.SpectralNoiseReduction == FlexBase.OffOnValues.on;

            bool legacyNrOn = _rig.NoiseReductionLegacy == FlexBase.OffOnValues.on;
            _legacyNrCheck.IsChecked = legacyNrOn;
            _nrLevelControl.Visibility = legacyNrOn ? Visibility.Visible : Visibility.Collapsed;
            if (legacyNrOn) _nrLevelControl.Value = _rig.NoiseReductionLegacyLevel;
        }

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

        // Meter tones (engine state, not rig state)
        _meterToneCheck.IsChecked = MeterToneEngine.Enabled;
        _peakWatcherCheck.IsChecked = MeterToneEngine.PeakWatcherEnabled;
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

        _micGainControl.Value = _rig.MicGain;
        _micBoostCheck.IsChecked = _rig.MicBoost == FlexBase.OffOnValues.on;
        _micBiasCheck.IsChecked = _rig.MicBias == FlexBase.OffOnValues.on;

        bool companderOn = _rig.Compander == FlexBase.OffOnValues.on;
        _companderCheck.IsChecked = companderOn;
        _companderLevelControl.Visibility = companderOn ? Visibility.Visible : Visibility.Collapsed;
        if (companderOn) _companderLevelControl.Value = _rig.CompanderLevel;

        bool processorOn = _rig.ProcessorOn == FlexBase.OffOnValues.on;
        _processorCheck.IsChecked = processorOn;
        _processorSettingControl.Visibility = processorOn ? Visibility.Visible : Visibility.Collapsed;
        if (processorOn) _processorSettingControl.SelectedIndex = (int)_rig.ProcessorSetting;

        _txFilterLowControl.Value = _rig.TXFilterLow;
        _txFilterHighControl.Value = _rig.TXFilterHigh;

        bool monitorOn = _rig.Monitor == FlexBase.OffOnValues.on;
        _monitorCheck.IsChecked = monitorOn;
        _monitorLevelControl.Visibility = monitorOn ? Visibility.Visible : Visibility.Collapsed;
        if (monitorOn) _monitorLevelControl.Value = _rig.SBMonitorLevel;
    }

    private void PollAntenna()
    {
        if (_rig == null) return;

        // RX/TX antenna selection
        var rxList = _rig.RXAntennaList;
        int rxIdx = rxList.IndexOf(_rig.RXAntennaName);
        if (rxIdx >= 0) _rxAntennaControl.SelectedIndex = rxIdx;

        var txList = _rig.TXAntennaList;
        int txIdx = txList.IndexOf(_rig.TXAntennaName);
        if (txIdx >= 0) _txAntennaControl.SelectedIndex = txIdx;

        // ATU controls
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

    #region Menu/Hotkey Navigation — Sprint 15 Track D

    /// <summary>Category names matching the Expander headers, for speech output.</summary>
    private static readonly string[] CategoryNames =
    {
        "Noise Reduction and DSP",
        "Audio",
        "Receiver",
        "Transmission",
        "Antenna"
    };

    /// <summary>
    /// Toggle a category: if expanded → collapse, if collapsed → expand + focus header.
    /// If the panel is hidden, shows the panel first.
    /// Called from ScreenFields menu items and Ctrl+Shift+1-5 hotkeys.
    /// </summary>
    public void ToggleCategory(int index)
    {
        if (index < 0 || index >= _expanders.Count) return;

        var expander = _expanders[index];

        // Show panel if hidden
        if (Visibility != Visibility.Visible)
            Visibility = Visibility.Visible;

        if (expander.IsExpanded)
        {
            expander.IsExpanded = false;
            ScreenReaderOutput.Speak($"{CategoryNames[index]} collapsed");
            ReturnFocusToFreqOut?.Invoke();
        }
        else
        {
            expander.IsExpanded = true;
            ScreenReaderOutput.Speak($"{CategoryNames[index]} expanded");

            // Focus the first focusable control in the expanded content
            // (deferred until layout completes so the content is rendered)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var content = GetCategoryContent(index);
                if (content != null)
                {
                    var firstFocusable = FindFirstFocusableChild(content);
                    if (firstFocusable != null)
                        Keyboard.Focus(firstFocusable);
                    else
                        expander.Focus();
                }
                else
                {
                    expander.Focus();
                }
            });
        }
    }

    /// <summary>
    /// Expand a category by index. Shows panel if hidden.
    /// </summary>
    public void ExpandCategory(int index)
    {
        if (index < 0 || index >= _expanders.Count) return;

        if (Visibility != Visibility.Visible)
            Visibility = Visibility.Visible;

        _expanders[index].IsExpanded = true;
        _expanders[index].Focus();
    }

    /// <summary>
    /// Collapse a category by index.
    /// </summary>
    public void CollapseCategory(int index)
    {
        if (index < 0 || index >= _expanders.Count) return;
        _expanders[index].IsExpanded = false;
    }

    /// <summary>Get the content StackPanel for a category index.</summary>
    private StackPanel? GetCategoryContent(int index)
    {
        return index switch
        {
            0 => DspContent,
            1 => AudioContent,
            2 => ReceiverContent,
            3 => TxContent,
            4 => AntennaContent,
            _ => null
        };
    }

    /// <summary>Find the first focusable child control in a visual tree.</summary>
    private static IInputElement? FindFirstFocusableChild(DependencyObject parent)
    {
        if (parent == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement uiElem && uiElem.Focusable && uiElem.IsEnabled
                && uiElem.Visibility == Visibility.Visible)
                return uiElem;
            var result = FindFirstFocusableChild(child);
            if (result != null) return result;
        }
        return null;
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
