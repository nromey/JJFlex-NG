using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private RxAudioPipeline? _audioPipeline;

    /// <summary>
    /// The PC-side audio processing pipeline, available for hotkey and menu wiring.
    /// Created when a rig connects, disposed on detach. Works on ALL radios.
    /// </summary>
    public RxAudioPipeline? AudioPipeline => _audioPipeline;

    /// <summary>Fired when user presses Escape — MainWindow wires this to FreqOut.FocusDisplay().</summary>
    public event EventHandler? EscapePressed;

    /// <summary>Callback to return focus to the FreqOut control after collapsing a category.</summary>
    public Action? ReturnFocusToFreqOut { get; set; }

    // All Expanders for Ctrl+Tab navigation
    private readonly List<Expander> _expanders = new();

    #region DSP Controls

    private CheckBox _neuralNrCheck = null!;
    private CheckBox _spectralNrCheck = null!;
    private CheckBox _nrfCheck = null!;
    private CheckBox _legacyNrCheck = null!;
    private ValueFieldControl _nrLevelControl = null!;
    private CheckBox _nbCheck = null!;
    private ValueFieldControl _nbLevelControl = null!;
    private CheckBox _wnbCheck = null!;
    private ValueFieldControl _wnbLevelControl = null!;
    private CheckBox _fftNotchCheck = null!;
    private CheckBox _legacyNotchCheck = null!;
    private CheckBox _apfCheck = null!;
    // PC-side NR controls (work on ALL radios, processing runs on PC)
    private CheckBox _pcRnnCheck = null!;
    private CheckBox _pcSpectralCheck = null!;
    // DSP controls track (2026-08-11) — the engine knobs finally get buttons:
    // strengths, floor, voice-only, noise capture, and the profile readout.
    private ValueFieldControl _pcRnnStrengthControl = null!;
    private CheckBox _pcRnnVoiceOnlyCheck = null!;
    private ValueFieldControl _pcSpectralStrengthControl = null!;
    private ValueFieldControl _pcSpectralFloorControl = null!;
    private Button _captureNoiseButton = null!;
    private Button _noiseProfilesButton = null!;
    private System.Windows.Controls.TextBlock _noiseProfileDisplay = null!;
    private CheckBox _meterToneCheck = null!;
    private CheckBox _peakWatcherCheck = null!;

    #endregion

    #region Audio and Slice Controls

    private CheckBox _muteCheck = null!;
    private ValueFieldControl _volumeControl = null!;
    private ValueFieldControl _panControl = null!;
    private ValueFieldControl _headphoneControl = null!;
    private ValueFieldControl _lineoutControl = null!;

    // PC audio group — Audio Arc Track A, 2026-08-11. The PC output volume is
    // the playback gain a remote operator actually hears; mic level is the
    // transmit level (PC audio included); the verdict readout is the arrow-to-
    // it answer to "how do I sound".
    private ValueFieldControl _pcVolumeControl = null!;
    private ValueFieldControl _micLevelControl = null!;
    private System.Windows.Controls.TextBlock _micVerdictDisplay = null!;

    // On-radio output mutes — same state the Audio menu's On-Radio group flips.
    private CheckBox _headphoneMuteCheck = null!;
    private CheckBox _lineoutMuteCheck = null!;
    private CheckBox _frontSpeakerMuteCheck = null!;

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
    private System.Windows.Controls.TextBlock _rxFilterWidthDisplay = null!;

    #endregion

    #region TX Controls

    private ValueFieldControl _txPowerControl = null!;
    // QB Track I — true while the TX power field is in transverter-drive
    // personality (dBm, hundredths) because the TX antenna is the XVTR port.
    private bool _txPowerXvtrMode;
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

    // Sprint 28 Phase 3 — double-Escape detection state.
    private DateTime _lastEscapeTime = DateTime.MinValue;
    // Suppress per-group collapse earcons and announcements during bulk collapse-all
    // so the user hears just the gavel + "all panels collapsed" announcement. Also
    // used during Escape-collapse to defer the collapse earcon — see Bug 3 fix
    // (Phase 3.3, 2026-04-21).
    private bool _suppressCollapseEarcons;
    // Sprint 28 Phase 3.3 — pending deferred collapse earcon. Set when Escape-
    // collapses a group; fired on timer tick (tolerance + 50 ms) if no second
    // Escape arrived. Cancelled by a second Escape arriving within tolerance
    // (which plays the gavel instead). This prevents the overlap where the
    // collapse earcon and the gavel would both play for a double-Escape gesture.
    private DispatcherTimer? _pendingCollapseEarconTimer;

    public ScreenFieldsPanel()
    {
        InitializeComponent();
        BuildControls();

        _expanders.Add(DspExpander);
        _expanders.Add(AudioExpander);
        _expanders.Add(ReceiverExpander);
        _expanders.Add(TxExpander);
        _expanders.Add(AntennaExpander);

        // Sprint 28 Phase 3 — hook Expanded/Collapsed events on every group so that
        // any expansion path (user hotkey, menu, Space-on-header, Escape collapse,
        // programmatic) plays the consistent expand/collapse earcon and speaks the
        // category name. Consolidating the announcement here (rather than at each
        // caller) is single-source-of-truth: one place decides how group state
        // changes are announced.
        foreach (var exp in _expanders)
        {
            exp.Expanded += OnGroupExpanded;
            exp.Collapsed += OnGroupCollapsed;
        }
    }

    /// <summary>Sprint 28 Phase 3 — fires when any group expands. Plays expand earcon.
    /// Explicit Speak removed 2026-04-21 after user feedback: NVDA's natural focus-
    /// change announcement covers the identity of the expanded group; adding an
    /// explicit Speak causes a double-announce. Earcon conveys the state change
    /// semantically; screen reader handles identity.</summary>
    private void OnGroupExpanded(object? sender, RoutedEventArgs e)
    {
        EarconPlayer.PlayExpand();
    }

    /// <summary>Sprint 28 Phase 3 — fires when any group collapses. Plays collapse
    /// earcon. Suppressed during bulk collapse-all (gavel earcon covers that case).
    /// Explicit Speak removed 2026-04-21 after user feedback (see OnGroupExpanded).</summary>
    private void OnGroupCollapsed(object? sender, RoutedEventArgs e)
    {
        if (_suppressCollapseEarcons) return;
        EarconPlayer.PlayCollapse();
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

        // NRF, NRS, RNN all require 8000-series/Aurora DSP hardware
        bool advancedNrAvailable = rig.NeuralNRHardwareSupported;
        _neuralNrCheck.Visibility = advancedNrAvailable ? Visibility.Visible : Visibility.Collapsed;
        _spectralNrCheck.Visibility = advancedNrAvailable ? Visibility.Visible : Visibility.Collapsed;
        _nrfCheck.Visibility = advancedNrAvailable ? Visibility.Visible : Visibility.Collapsed;
        // Legacy NR is always available — no license required
        _nrLevelControl.Visibility = Visibility.Collapsed; // shown only when Legacy NR is on

        // Hide ATU controls if radio has no ATU hardware
        bool hasATU = rig.HasATU;
        _atuCheck.Visibility = hasATU ? Visibility.Visible : Visibility.Collapsed;
        _atuModeControl.Visibility = hasATU ? Visibility.Visible : Visibility.Collapsed;

        // RF Gain bounds vary by radio model — update from connected radio
        _rfGainControl.Min = rig.RFGainMin;
        _rfGainControl.Max = rig.RFGainMax;
        _rfGainControl.Step = rig.RFGainIncrement;

        // QB Track I — TX power personality follows the TX antenna from the
        // first paint (PollTX keeps it honest afterwards).
        ReconfigureTxPowerForMode(rig.XvtrPowerAvailable);

        // Subscribe to mode changes for immediate DSP refresh
        rig.ModeChanged += OnModeChanged;

        // Create PC-side audio processing pipeline (works on ALL radios)
        _audioPipeline?.Dispose();
        _audioPipeline = new RxAudioPipeline();
        rig.AudioPostProcessor = _audioPipeline.Process;
        // Feed current mode so RNNoise auto-disables for CW/digital
        _audioPipeline.SetCurrentMode(rig.Mode ?? "");

        // Track I: wire the TX conditioning chain (noise gate + TX noise
        // reduction + residual monitor) — same lifecycle as the RX pipeline.
        TxAudioConditioning.Attach(rig);

        // Force initial poll to populate values
        PollUpdate();
    }

    private void OnModeChanged(string newMode)
    {
        // Update pipeline mode (thread-safe, can be called from any thread)
        _audioPipeline?.SetCurrentMode(newMode ?? "");

        Dispatcher.BeginInvoke(() =>
        {
            if (_rig != null && DspExpander.IsExpanded)
                PollDSP();
        });
    }

    /// <summary>
    /// Disconnect from the radio (on disconnect or power off).
    /// </summary>
    public void Detach()
    {
        if (_rig != null)
        {
            _rig.ModeChanged -= OnModeChanged;
            _rig.AudioPostProcessor = null;
        }
        // Track I: unhook the TX conditioning chain alongside the RX pipeline.
        TxAudioConditioning.Detach();
        _audioPipeline?.Dispose();
        _audioPipeline = null;
        _rig = null;
        // QB Track I — back to the watts personality for the next radio.
        ReconfigureTxPowerForMode(false);
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
        // "On-Radio" prefix (DSP controls track, 2026-08-11): these two run in
        // the radio's own DSP hardware and have PC-side namesakes below —
        // without the prefix, "Spectral NR" and "PC Spectral NR" sat in one
        // list daring an operator to guess which was which. Same vocabulary
        // as On-Radio Headphone Level vs PC Output Volume.
        _neuralNrCheck = MakeToggle(Lexicon.Get("audio.fields.neural_nr"));
        _neuralNrCheck.Checked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.neural_nr_spoken"), v => { if (_rig != null) _rig.NeuralNoiseReduction = v; }, true);
        _neuralNrCheck.Unchecked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.neural_nr_spoken"), v => { if (_rig != null) _rig.NeuralNoiseReduction = v; }, false);
        DspContent.Children.Add(_neuralNrCheck);

        _spectralNrCheck = MakeToggle(Lexicon.Get("audio.fields.spectral_nr"));
        _spectralNrCheck.Checked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.spectral_nr_spoken"), v => { if (_rig != null) _rig.SpectralNoiseReduction = v; }, true);
        _spectralNrCheck.Unchecked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.spectral_nr_spoken"), v => { if (_rig != null) _rig.SpectralNoiseReduction = v; }, false);
        DspContent.Children.Add(_spectralNrCheck);

        _nrfCheck = MakeToggle(Lexicon.Get("audio.fields.nr_filter"));
        _nrfCheck.Checked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.nr_filter_spoken"), v => { if (_rig != null) _rig.NoiseReductionFilter = v; }, true);
        _nrfCheck.Unchecked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.nr_filter_spoken"), v => { if (_rig != null) _rig.NoiseReductionFilter = v; }, false);
        DspContent.Children.Add(_nrfCheck);

        _legacyNrCheck = MakeToggle(Lexicon.Get("audio.fields.legacy_nr"));
        _legacyNrCheck.Checked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.legacy_nr"), v => { if (_rig != null) _rig.NoiseReductionLegacy = v; }, true);
            _nrLevelControl.Visibility = Visibility.Visible;
        };
        _legacyNrCheck.Unchecked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.legacy_nr"), v => { if (_rig != null) _rig.NoiseReductionLegacy = v; }, false);
            _nrLevelControl.Visibility = Visibility.Collapsed;
        };
        DspContent.Children.Add(_legacyNrCheck);

        _nrLevelControl = MakeValue(Lexicon.Get("audio.fields.nr_level"), 1, 15, 1);
        _nrLevelControl.Visibility = Visibility.Collapsed;
        _nrLevelControl.ValueChanged += (s, v) => { if (_rig != null) _rig.NoiseReductionLegacyLevel = v; };
        DspContent.Children.Add(_nrLevelControl);

        _nbCheck = MakeToggle(Lexicon.Get("audio.fields.noise_blanker"));
        _nbCheck.Checked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.noise_blanker"), v => { if (_rig != null) _rig.NoiseBlanker = v; }, true);
            _nbLevelControl.Visibility = Visibility.Visible;
        };
        _nbCheck.Unchecked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.noise_blanker"), v => { if (_rig != null) _rig.NoiseBlanker = v; }, false);
            _nbLevelControl.Visibility = Visibility.Collapsed;
        };
        DspContent.Children.Add(_nbCheck);

        _nbLevelControl = MakeValue(Lexicon.Get("audio.fields.nb_level"), 1, 100, 5);
        _nbLevelControl.Visibility = Visibility.Collapsed;
        _nbLevelControl.ValueChanged += (s, v) => { if (_rig != null) _rig.NoiseBlankerLevel = v; };
        DspContent.Children.Add(_nbLevelControl);

        _wnbCheck = MakeToggle(Lexicon.Get("audio.fields.wideband_nb"));
        _wnbCheck.Checked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.wideband_nb"), v => { if (_rig != null) _rig.WidebandNoiseBlanker = v; }, true);
            _wnbLevelControl.Visibility = Visibility.Visible;
        };
        _wnbCheck.Unchecked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.wideband_nb"), v => { if (_rig != null) _rig.WidebandNoiseBlanker = v; }, false);
            _wnbLevelControl.Visibility = Visibility.Collapsed;
        };
        DspContent.Children.Add(_wnbCheck);

        _wnbLevelControl = MakeValue(Lexicon.Get("audio.fields.wnb_level"), 1, 100, 5);
        _wnbLevelControl.Visibility = Visibility.Collapsed;
        _wnbLevelControl.ValueChanged += (s, v) => { if (_rig != null) _rig.WidebandNoiseBlankerLevel = v; };
        DspContent.Children.Add(_wnbLevelControl);

        _fftNotchCheck = MakeToggle(Lexicon.Get("audio.fields.fft_auto_notch"));
        _fftNotchCheck.Checked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.fft_auto_notch"), v => { if (_rig != null) _rig.AutoNotchFFT = v; }, true);
        _fftNotchCheck.Unchecked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.fft_auto_notch"), v => { if (_rig != null) _rig.AutoNotchFFT = v; }, false);
        DspContent.Children.Add(_fftNotchCheck);

        _legacyNotchCheck = MakeToggle(Lexicon.Get("audio.fields.legacy_auto_notch"));
        _legacyNotchCheck.Checked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.legacy_auto_notch"), v => { if (_rig != null) _rig.AutoNotchLegacy = v; }, true);
        _legacyNotchCheck.Unchecked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.legacy_auto_notch"), v => { if (_rig != null) _rig.AutoNotchLegacy = v; }, false);
        DspContent.Children.Add(_legacyNotchCheck);

        _apfCheck = MakeToggle(Lexicon.Get("audio.fields.apf"));
        _apfCheck.Checked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.apf_spoken"), v => { if (_rig != null) _rig.APF = v; }, true);
        _apfCheck.Unchecked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.apf_spoken"), v => { if (_rig != null) _rig.APF = v; }, false);
        DspContent.Children.Add(_apfCheck);

        // PC-side noise reduction (runs on computer, works on ALL radios).
        // DSP controls track (2026-08-11): the engine was finished in Sprint
        // 25 Phase 20; these are the buttons it never had. Strength/floor
        // follow the house pattern (level fields appear when the toggle is
        // on); capture and the profile readout stay visible always — they
        // are the doorway into making Spectral NR work at all.
        _pcRnnCheck = MakeToggle(Lexicon.Get("audio.fields.pc_neural_nr"));
        _pcRnnCheck.Checked += (s, e) =>
        {
            if (_polling || _audioPipeline == null) return;
            _audioPipeline.RnnEnabled = true;
            _pcRnnStrengthControl.Visibility = Visibility.Visible;
            _pcRnnVoiceOnlyCheck.Visibility = Visibility.Visible;
            EarconPlayer.FeatureOnTone();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.fields.pc_neural_nr_on"), VerbosityLevel.Terse, interrupt: true);
            FindMainWindow()?.PersistDspSettings();
        };
        _pcRnnCheck.Unchecked += (s, e) =>
        {
            if (_polling || _audioPipeline == null) return;
            _audioPipeline.RnnEnabled = false;
            _pcRnnStrengthControl.Visibility = Visibility.Collapsed;
            _pcRnnVoiceOnlyCheck.Visibility = Visibility.Collapsed;
            EarconPlayer.FeatureOffTone();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.fields.pc_neural_nr_off"), VerbosityLevel.Terse, interrupt: true);
            FindMainWindow()?.PersistDspSettings();
        };
        DspContent.Children.Add(_pcRnnCheck);

        // Strength is a wet/dry mix (0.0-1.0 in the engine) surfaced as a
        // percentage — "80 percent" speaks better than "zero point eight".
        _pcRnnStrengthControl = new ValueFieldControl();
        _pcRnnStrengthControl.Setup(Lexicon.Get("audio.fields.pc_neural_nr_strength"), 0, 100, 5, 80, 0, Lexicon.Get("audio.fields.unit_percent"));
        _pcRnnStrengthControl.Visibility = Visibility.Collapsed;
        _pcRnnStrengthControl.ValueChanged += (s, v) =>
        {
            if (_audioPipeline == null || _polling) return;
            _audioPipeline.RnnStrength = v / 100f;
            FindMainWindow()?.PersistDspSettings();
        };
        DspContent.Children.Add(_pcRnnStrengthControl);

        // Checked = the engine steps aside for CW and digital modes (it is
        // speech-trained and chews on data tones).
        _pcRnnVoiceOnlyCheck = MakeToggle(Lexicon.Get("audio.fields.pc_neural_nr_voice_only"));
        _pcRnnVoiceOnlyCheck.Visibility = Visibility.Collapsed;
        _pcRnnVoiceOnlyCheck.Checked += (s, e) =>
        {
            if (_polling || _audioPipeline == null) return;
            _audioPipeline.RnnAutoDisableNonVoice = true;
            EarconPlayer.FeatureOnTone();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.fields.pc_neural_nr_voice_only_on"), VerbosityLevel.Terse, interrupt: true);
            FindMainWindow()?.PersistDspSettings();
        };
        _pcRnnVoiceOnlyCheck.Unchecked += (s, e) =>
        {
            if (_polling || _audioPipeline == null) return;
            _audioPipeline.RnnAutoDisableNonVoice = false;
            EarconPlayer.FeatureOffTone();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.fields.pc_neural_nr_voice_only_off"), VerbosityLevel.Terse, interrupt: true);
            FindMainWindow()?.PersistDspSettings();
        };
        DspContent.Children.Add(_pcRnnVoiceOnlyCheck);

        _pcSpectralCheck = MakeToggle(Lexicon.Get("audio.fields.pc_spectral_nr"));
        _pcSpectralCheck.Checked += (s, e) =>
        {
            if (_polling || _audioPipeline == null) return;
            _audioPipeline.SpectralEnabled = true;
            _pcSpectralStrengthControl.Visibility = Visibility.Visible;
            _pcSpectralFloorControl.Visibility = Visibility.Visible;
            EarconPlayer.FeatureOnTone();
            // The no-profile message now names the exit — before this track
            // it announced a dead end no surface in the app could resolve.
            ScreenReaderOutput.Speak(_audioPipeline.HasNoiseProfile
                ? Lexicon.Get("audio.fields.pc_spectral_nr_on")
                : Lexicon.Get("audio.fields.pc_spectral_nr_on_no_profile"),
                VerbosityLevel.Terse, interrupt: true);
            FindMainWindow()?.PersistDspSettings();
        };
        _pcSpectralCheck.Unchecked += (s, e) =>
        {
            if (_polling || _audioPipeline == null) return;
            _audioPipeline.SpectralEnabled = false;
            _pcSpectralStrengthControl.Visibility = Visibility.Collapsed;
            _pcSpectralFloorControl.Visibility = Visibility.Collapsed;
            EarconPlayer.FeatureOffTone();
            ScreenReaderOutput.Speak(Lexicon.Get("audio.fields.pc_spectral_nr_off"), VerbosityLevel.Terse, interrupt: true);
            FindMainWindow()?.PersistDspSettings();
        };
        DspContent.Children.Add(_pcSpectralCheck);

        _pcSpectralStrengthControl = new ValueFieldControl();
        _pcSpectralStrengthControl.Setup(Lexicon.Get("audio.fields.pc_spectral_nr_strength"), 0, 100, 5, 70, 0, Lexicon.Get("audio.fields.unit_percent"));
        _pcSpectralStrengthControl.Visibility = Visibility.Collapsed;
        _pcSpectralStrengthControl.ValueChanged += (s, v) =>
        {
            if (_audioPipeline == null || _polling) return;
            _audioPipeline.SpectralStrength = v / 100f;
            FindMainWindow()?.PersistDspSettings();
        };
        DspContent.Children.Add(_pcSpectralStrengthControl);

        // Floor: how much of the original audio always survives subtraction —
        // the guard against watery "musical noise". Engine range is 0-1 but
        // useful values are single-digit percent, so the field runs 0-20%.
        _pcSpectralFloorControl = new ValueFieldControl();
        _pcSpectralFloorControl.Setup(Lexicon.Get("audio.fields.pc_spectral_nr_floor"), 0, 20, 1, 2, 0, Lexicon.Get("audio.fields.unit_percent"));
        _pcSpectralFloorControl.Visibility = Visibility.Collapsed;
        _pcSpectralFloorControl.ValueChanged += (s, v) =>
        {
            if (_audioPipeline == null || _polling) return;
            _audioPipeline.SpectralFloor = v / 100f;
            FindMainWindow()?.PersistDspSettings();
        };
        DspContent.Children.Add(_pcSpectralFloorControl);

        // Capture — always visible, and honest about its second job: while a
        // capture runs, this same button cancels it (label follows along).
        _captureNoiseButton = new Button
        {
            Content = Lexicon.Get("audio.fields.capture_noise_button"),
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        System.Windows.Automation.AutomationProperties.SetName(
            _captureNoiseButton, Lexicon.Get("audio.fields.capture_noise_name"));
        _captureNoiseButton.Click += (s, e) =>
        {
            var mw = FindMainWindow();
            NoiseCaptureNarrator.Toggle(_rig, _audioPipeline,
                mw?.CurrentAudioConfig?.SpectralSubSampleDuration ?? 3,
                onFinished: UpdateNoiseProfileDisplay);
        };
        DspContent.Children.Add(_captureNoiseButton);
        NoiseCaptureNarrator.StateChanged += UpdateCaptureButtonLabel;

        _noiseProfilesButton = new Button
        {
            Content = Lexicon.Get("audio.fields.noise_profiles_button"),
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        System.Windows.Automation.AutomationProperties.SetName(
            _noiseProfilesButton, Lexicon.Get("audio.fields.noise_profiles_name"));
        _noiseProfilesButton.Click += (s, e) =>
        {
            var mw = FindMainWindow();
            new Dialogs.NoiseProfilesDialog(_rig, _audioPipeline,
                () => mw?.CurrentAudioConfig, () => mw?.PersistDspSettings()).ShowDialog();
            UpdateNoiseProfileDisplay();
        };
        DspContent.Children.Add(_noiseProfilesButton);

        // Read-only profile readout — arrow to it, hear which profile is
        // loaded (name, band, antenna ride the name). Mic-verdict pattern:
        // the accessible name holds still while focused so a mid-capture
        // change doesn't flood the screen reader.
        _noiseProfileDisplay = new System.Windows.Controls.TextBlock
        {
            Margin = new Thickness(4, 6, 4, 2),
            Focusable = true,
            IsHitTestVisible = true,
            Text = Lexicon.Get("audio.fields.noise_profile_none")
        };
        System.Windows.Automation.AutomationProperties.SetName(
            _noiseProfileDisplay, Lexicon.Get("audio.fields.noise_profile_none"));
        _noiseProfileDisplay.GotFocus += (s, e) =>
        {
            // Refresh so the accessible name the screen reader is about to
            // read is current (the poll leaves the name alone while focused).
            // No Speak — the screen reader reads the name on focus itself.
            UpdateNoiseProfileDisplay();
            System.Windows.Automation.AutomationProperties.SetName(
                _noiseProfileDisplay, _noiseProfileDisplay.Text);
        };
        DspContent.Children.Add(_noiseProfileDisplay);

        // Meter Tones
        _meterToneCheck = MakeToggle(Lexicon.Get("audio.fields.meter_tones"));
        _meterToneCheck.Checked += (s, e) => { if (!_polling) { MeterToneEngine.Enabled = true; EarconPlayer.FeatureOnTone(); ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.tones_on"), VerbosityLevel.Terse); } };
        _meterToneCheck.Unchecked += (s, e) => { if (!_polling) { MeterToneEngine.Enabled = false; EarconPlayer.FeatureOffTone(); ScreenReaderOutput.Speak(Lexicon.Get("audio.meters.tones_off"), VerbosityLevel.Terse); } };
        DspContent.Children.Add(_meterToneCheck);

        _peakWatcherCheck = MakeToggle(Lexicon.Get("audio.fields.peak_watcher"));
        // #128: no tone at this control — PeakWatcherEnabled's setter tones,
        // so all three of its roads (this checkbox, the Meters panel checkbox,
        // the menu item) answer back identically. A tone here as well would
        // sound twice per press, the same defect the sweep audit removed from
        // the PC audio chord on 2026-08-21.
        _peakWatcherCheck.Checked += (s, e) => { if (!_polling) { MeterToneEngine.PeakWatcherEnabled = true; ScreenReaderOutput.Speak(Lexicon.Get("audio.fields.peak_watcher_on"), VerbosityLevel.Terse); } };
        _peakWatcherCheck.Unchecked += (s, e) => { if (!_polling) { MeterToneEngine.PeakWatcherEnabled = false; ScreenReaderOutput.Speak(Lexicon.Get("audio.fields.peak_watcher_off"), VerbosityLevel.Terse); } };
        DspContent.Children.Add(_peakWatcherCheck);
    }

    private void BuildAudioControls()
    {
        _muteCheck = MakeToggle(Lexicon.Get("audio.fields.mute"));
        _muteCheck.Checked += (s, e) => ToggleBoolRig(Lexicon.Get("audio.fields.mute"), v => { if (_rig != null) _rig.SliceMute = v; }, true);
        _muteCheck.Unchecked += (s, e) => ToggleBoolRig(Lexicon.Get("audio.fields.mute"), v => { if (_rig != null) _rig.SliceMute = v; }, false);
        AudioContent.Children.Add(_muteCheck);

        _volumeControl = MakeValue(Lexicon.Get("audio.fields.volume"), 0, 100, 5);
        _volumeControl.ValueChanged += (s, v) => { if (_rig != null) _rig.AudioGain = v; };
        AudioContent.Children.Add(_volumeControl);

        _panControl = MakeValue(Lexicon.Get("audio.fields.pan"), 0, 100, 5);
        _panControl.ValueChanged += (s, v) => { if (_rig != null) _rig.AudioPan = v; };
        AudioContent.Children.Add(_panControl);

        // === PC audio group (Audio Arc Track A, 2026-08-11) ===
        // These are the controls a remote PC-audio operator actually needs;
        // they were missing entirely while the on-radio jack levels sat here
        // unlabeled, pretending to be "the volume".
        AudioContent.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });

        _pcVolumeControl = new ValueFieldControl();
        _pcVolumeControl.Setup(Lexicon.Get("audio.fields.pc_output_volume"), FlexBase.PcOutputVolumeDbMin,
            FlexBase.PcOutputVolumeDbMax, 1, FlexBase.PcOutputVolumeDbSetting, unit: Lexicon.Get("audio.fields.unit_db"));
        _pcVolumeControl.ValueChanged += (s, v) =>
        {
            if (_rig == null || _polling) return;
            _rig.PcOutputVolumeDb = v;
            // App-level setting — persist as it changes (24 steps max, tiny file).
            FindMainWindow()?.PersistPcOutputVolume();
        };
        AudioContent.Children.Add(_pcVolumeControl);

        _micLevelControl = MakeValue(Lexicon.Get("audio.fields.mic_level"), 0, 100, 5);
        _micLevelControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.MicGain = v; };
        AudioContent.Children.Add(_micLevelControl);

        // Read-only mic-audio verdict — arrow to it, hear how you sound.
        // Same judgment the Audio Workshop and the unkey summary speak,
        // because all three compose it through MicAudioReport.
        _micVerdictDisplay = new System.Windows.Controls.TextBlock
        {
            Margin = new Thickness(4, 6, 4, 2),
            Focusable = true,
            IsHitTestVisible = true,
            Text = Lexicon.Get("audio.fields.mic_verdict_none")
        };
        System.Windows.Automation.AutomationProperties.SetName(
            _micVerdictDisplay, Lexicon.Get("audio.fields.mic_verdict_none"));
        _micVerdictDisplay.GotFocus += (s, e) =>
        {
            // Refresh the name on entry — the poll deliberately leaves the
            // accessible name alone while this control is focused so a
            // transmit in progress doesn't spam the screen reader with name
            // changes. No Speak — the screen reader reads the refreshed name
            // on focus itself.
            System.Windows.Automation.AutomationProperties.SetName(
                _micVerdictDisplay, _micVerdictDisplay.Text);
        };
        AudioContent.Children.Add(_micVerdictDisplay);

        // === On-radio outputs group ===
        // "On-radio" is the load-bearing word: these move the radio's own
        // jacks, which a remote PC-audio operator cannot hear.
        AudioContent.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });

        _headphoneControl = MakeValue(Lexicon.Get("audio.fields.headphone_level"), 0, 100, 5);
        _headphoneControl.ValueChanged += (s, v) => { if (_rig != null) _rig.HeadphoneGain = v; };
        AudioContent.Children.Add(_headphoneControl);

        _lineoutControl = MakeValue(Lexicon.Get("audio.fields.lineout_level"), 0, 100, 5);
        _lineoutControl.ValueChanged += (s, v) => { if (_rig != null) _rig.LineoutGain = v; };
        AudioContent.Children.Add(_lineoutControl);

        _headphoneMuteCheck = MakeToggle(Lexicon.Get("audio.fields.headphone_mute"));
        _headphoneMuteCheck.Checked += (s, e) => ToggleBoolRig(Lexicon.Get("audio.fields.headphone_mute_spoken"), v => { if (_rig != null) _rig.HeadphoneMute = v; }, true);
        _headphoneMuteCheck.Unchecked += (s, e) => ToggleBoolRig(Lexicon.Get("audio.fields.headphone_mute_spoken"), v => { if (_rig != null) _rig.HeadphoneMute = v; }, false);
        AudioContent.Children.Add(_headphoneMuteCheck);

        _lineoutMuteCheck = MakeToggle(Lexicon.Get("audio.fields.lineout_mute"));
        _lineoutMuteCheck.Checked += (s, e) => ToggleBoolRig(Lexicon.Get("audio.fields.lineout_mute_spoken"), v => { if (_rig != null) _rig.LineoutMute = v; }, true);
        _lineoutMuteCheck.Unchecked += (s, e) => ToggleBoolRig(Lexicon.Get("audio.fields.lineout_mute_spoken"), v => { if (_rig != null) _rig.LineoutMute = v; }, false);
        AudioContent.Children.Add(_lineoutMuteCheck);

        _frontSpeakerMuteCheck = MakeToggle(Lexicon.Get("audio.fields.front_speaker_mute"));
        _frontSpeakerMuteCheck.Checked += (s, e) => ToggleBoolRig(Lexicon.Get("audio.fields.front_speaker_mute_spoken"), v => { if (_rig != null) _rig.FrontSpeakerMute = v; }, true);
        _frontSpeakerMuteCheck.Unchecked += (s, e) => ToggleBoolRig(Lexicon.Get("audio.fields.front_speaker_mute_spoken"), v => { if (_rig != null) _rig.FrontSpeakerMute = v; }, false);
        AudioContent.Children.Add(_frontSpeakerMuteCheck);

        // Separator between audio and slice controls
        AudioContent.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });

        // Slice management buttons
        _createSliceButton = new Button
        {
            Content = Lexicon.Get("audio.fields.create_slice_button"),
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        System.Windows.Automation.AutomationProperties.SetName(_createSliceButton, Lexicon.Get("audio.fields.create_slice_name"));
        _createSliceButton.Click += (s, e) =>
        {
            if (_rig == null) return;
            bool ok = _rig.NewSlice();
            if (ok)
            {
                int n = _rig.MyNumSlices;
                ScreenReaderOutput.Speak(Lexicon.Get(
                    n == 1 ? "audio.fields.slice_created_one" : "audio.fields.slice_created_many",
                    ("n", n)), VerbosityLevel.Terse);
            }
            else
                ScreenReaderOutput.Speak(Lexicon.Get("audio.fields.max_slices"), VerbosityLevel.Terse);
        };
        AudioContent.Children.Add(_createSliceButton);

        _releaseSliceButton = new Button
        {
            Content = Lexicon.Get("audio.fields.release_slice_button"),
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        System.Windows.Automation.AutomationProperties.SetName(_releaseSliceButton, Lexicon.Get("audio.fields.release_slice_name"));
        _releaseSliceButton.Click += (s, e) =>
        {
            if (_rig == null) return;
            int numSlices = _rig.MyNumSlices;
            if (numSlices <= 1)
            {
                ScreenReaderOutput.Speak(Lexicon.Get("audio.fields.cannot_release_only_slice"), VerbosityLevel.Terse);
                return;
            }
            // Release the last slice (highest index)
            bool ok = _rig.RemoveSlice(numSlices - 1);
            if (ok)
            {
                int n = _rig.MyNumSlices;
                ScreenReaderOutput.Speak(Lexicon.Get(
                    n == 1 ? "audio.fields.slice_released_one" : "audio.fields.slice_released_many",
                    ("n", n)), VerbosityLevel.Terse);
            }
            else
                ScreenReaderOutput.Speak(Lexicon.Get("audio.fields.could_not_release_slice"), VerbosityLevel.Terse);
        };
        AudioContent.Children.Add(_releaseSliceButton);

        // Multi-slice buttons — mirror the Home-field Shift+M and Shift+Comma hotkeys.
        var muteAllButton = new Button
        {
            Content = Lexicon.Get("audio.fields.mute_all_button"),
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        System.Windows.Automation.AutomationProperties.SetName(
            muteAllButton, Lexicon.Get("audio.fields.mute_all_name"));
        muteAllButton.Click += (s, e) =>
        {
            if (_rig == null) return;
            bool target = !_rig.AllMySlicesMuted;
            _rig.SetAllMySlicesMute(target);
            if (target) EarconPlayer.MuteAllOnTone();
            else EarconPlayer.MuteAllOffTone();
            ScreenReaderOutput.Speak(
                target ? Lexicon.Get("audio.fields.all_slices_muted") : Lexicon.Get("audio.fields.all_slices_unmuted"), VerbosityLevel.Terse);
        };
        AudioContent.Children.Add(muteAllButton);

        var releaseAllButton = new Button
        {
            Content = Lexicon.Get("audio.fields.release_all_button"),
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        // The NAME is the button's own label, read from the same key, so the
        // two cannot drift apart again (#363). The reasoning — that the slice
        // you are on is the one that survives — is an explanation, and an
        // explanation is Ctrl+F1's job: on demand when it is wanted, silent
        // when it is not.
        System.Windows.Automation.AutomationProperties.SetName(
            releaseAllButton, Lexicon.Get("audio.fields.release_all_button"));
        JJFlexHelp.SetText(releaseAllButton, Lexicon.Get("audio.fields.release_all_help"));
        releaseAllButton.Click += (s, e) =>
        {
            if (_rig == null) return;
            int before = _rig.MyNumSlices;
            if (before <= 1)
            {
                ScreenReaderOutput.Speak(Lexicon.Get("audio.fields.only_one_slice"), VerbosityLevel.Terse);
                return;
            }
            if (_rig.ReleaseAllExtraSlices())
            {
                EarconPlayer.MuteAllOnTone();
                int removed = before - 1;
                string keptLetter = _rig.VFOToLetter(_rig.RXVFO);
                ScreenReaderOutput.Speak(
                    Lexicon.Get(
                        removed == 1 ? "audio.fields.released_extra_one" : "audio.fields.released_extra_many",
                        ("removed", removed), ("keptLetter", keptLetter)),
                    VerbosityLevel.Terse);
            }
        };
        AudioContent.Children.Add(releaseAllButton);
    }

    private void BuildReceiverControls()
    {
        _agcModeControl = MakeCycle(Lexicon.Get("audio.fields.agc_mode"), new[] { Lexicon.Get("audio.fields.agc_off"), Lexicon.Get("audio.fields.agc_slow"), Lexicon.Get("audio.fields.agc_medium"), Lexicon.Get("audio.fields.agc_fast") });
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

        _agcThresholdControl = MakeValue(Lexicon.Get("audio.fields.agc_threshold"),
            FlexBase.AGCThresholdMin, FlexBase.AGCThresholdMax, FlexBase.AGCThresholdIncrement);
        // !_polling matches the sibling handlers: a poll refresh rewriting the
        // control's value must not echo back to the rig — since #225 that echo
        // would also be misread as an operator settings change.
        _agcThresholdControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.AGCThreshold = v; };
        ReceiverContent.Children.Add(_agcThresholdControl);

        _squelchCheck = MakeToggle(Lexicon.Get("audio.fields.squelch"));
        _squelchCheck.Checked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.squelch"), v => { if (_rig != null) _rig.Squelch = v; }, true);
            _squelchLevelControl.Visibility = Visibility.Visible;
        };
        _squelchCheck.Unchecked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.squelch"), v => { if (_rig != null) _rig.Squelch = v; }, false);
            _squelchLevelControl.Visibility = Visibility.Collapsed;
        };
        ReceiverContent.Children.Add(_squelchCheck);

        _squelchLevelControl = MakeValue(Lexicon.Get("audio.fields.squelch_level"),
            FlexBase.SquelchLevelMin, FlexBase.SquelchLevelMax, FlexBase.SquelchLevelIncrement);
        _squelchLevelControl.Visibility = Visibility.Collapsed;
        _squelchLevelControl.ValueChanged += (s, v) => { if (_rig != null) _rig.SquelchLevel = v; };
        ReceiverContent.Children.Add(_squelchLevelControl);

        // RF Gain bounds are instance fields (vary by radio), set defaults here, updated in Initialize()
        _rfGainControl = MakeValue(Lexicon.Get("audio.fields.rf_gain"), -10, 30, 10);
        // !_polling for the same reason as the AGC threshold handler above.
        _rfGainControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.RFGain = v; };
        ReceiverContent.Children.Add(_rfGainControl);

        // Read-only RX filter width display. The accessible name tracks the
        // text (PollReceiver refreshes both together), so the screen reader
        // reads the current width on focus by itself — no GotFocus Speak.
        _rxFilterWidthDisplay = new System.Windows.Controls.TextBlock
        {
            Margin = new Thickness(4, 6, 4, 2),
            Focusable = true,
            IsHitTestVisible = true
        };
        System.Windows.Automation.AutomationProperties.SetName(_rxFilterWidthDisplay, Lexicon.Get("audio.fields.rx_filter_width_name"));
        ReceiverContent.Children.Add(_rxFilterWidthDisplay);
    }

    private void BuildTXControls()
    {
        _txPowerControl = MakeValue(Lexicon.Get("audio.fields.tx_power"), 0, 100, 1);
        // QB Track I — one control, two personalities: integer watts on a
        // normal TX antenna, centi-dBm transverter drive on the XVTR port.
        _txPowerControl.ValueChanged += (s, v) =>
        {
            if (_rig == null || _polling) return;
            if (_txPowerXvtrMode) _rig.XvtrDrivePowerCentiDbm = v;
            else _rig.XmitPower = v;
        };
        TxContent.Children.Add(_txPowerControl);

        _voxCheck = MakeToggle(Lexicon.Get("audio.fields.vox"));
        _voxCheck.Checked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.vox"), v => { if (_rig != null) _rig.Vox = v; }, true);
        _voxCheck.Unchecked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.vox"), v => { if (_rig != null) _rig.Vox = v; }, false);
        TxContent.Children.Add(_voxCheck);

        _tunePowerControl = MakeValue(Lexicon.Get("audio.fields.tune_power"), 0, 100, 1);
        _tunePowerControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.TunePower = v; };
        TxContent.Children.Add(_tunePowerControl);

        // Mic Gain
        _micGainControl = MakeValue(Lexicon.Get("audio.fields.mic_gain"), 0, 100, 1);
        _micGainControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.MicGain = v; };
        TxContent.Children.Add(_micGainControl);

        // Mic Boost
        _micBoostCheck = MakeToggle(Lexicon.Get("audio.fields.mic_boost"));
        _micBoostCheck.Checked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.mic_boost_spoken"), v => { if (_rig != null) _rig.MicBoost = v; }, true);
        _micBoostCheck.Unchecked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.mic_boost_spoken"), v => { if (_rig != null) _rig.MicBoost = v; }, false);
        TxContent.Children.Add(_micBoostCheck);

        // Mic Bias
        _micBiasCheck = MakeToggle(Lexicon.Get("audio.fields.mic_bias"));
        _micBiasCheck.Checked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.mic_bias_spoken"), v => { if (_rig != null) _rig.MicBias = v; }, true);
        _micBiasCheck.Unchecked += (s, e) => ToggleRig(Lexicon.Get("audio.fields.mic_bias_spoken"), v => { if (_rig != null) _rig.MicBias = v; }, false);
        TxContent.Children.Add(_micBiasCheck);

        // Compander
        _companderCheck = MakeToggle(Lexicon.Get("audio.fields.compander"));
        _companderCheck.Checked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.compander"), v => { if (_rig != null) _rig.Compander = v; }, true);
            _companderLevelControl.Visibility = Visibility.Visible;
        };
        _companderCheck.Unchecked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.compander"), v => { if (_rig != null) _rig.Compander = v; }, false);
            _companderLevelControl.Visibility = Visibility.Collapsed;
        };
        TxContent.Children.Add(_companderCheck);

        // Compander Level (shown when Compander is on)
        _companderLevelControl = MakeValue(Lexicon.Get("audio.fields.compander_level"), 0, 100, 5);
        _companderLevelControl.Visibility = Visibility.Collapsed;
        _companderLevelControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.CompanderLevel = v; };
        TxContent.Children.Add(_companderLevelControl);

        // Speech Processor
        _processorCheck = MakeToggle(Lexicon.Get("audio.fields.speech_processor"));
        _processorCheck.Checked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.speech_processor"), v => { if (_rig != null) _rig.ProcessorOn = v; }, true);
            _processorSettingControl.Visibility = Visibility.Visible;
        };
        _processorCheck.Unchecked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.speech_processor"), v => { if (_rig != null) _rig.ProcessorOn = v; }, false);
            _processorSettingControl.Visibility = Visibility.Collapsed;
        };
        TxContent.Children.Add(_processorCheck);

        // Processor Setting (shown when Processor is on)
        _processorSettingControl = MakeCycle(Lexicon.Get("audio.fields.processor_mode"), new[] { Lexicon.Get("audio.fields.processor_normal"), Lexicon.Get("audio.fields.processor_dx"), Lexicon.Get("audio.fields.processor_dx_plus") });
        _processorSettingControl.Visibility = Visibility.Collapsed;
        _processorSettingControl.SelectionChanged += (s, idx) =>
        {
            if (_rig == null || _polling) return;
            _rig.ProcessorSetting = (FlexBase.ProcessorSettings)idx;
        };
        TxContent.Children.Add(_processorSettingControl);

        // TX Filter Low
        _txFilterLowControl = MakeValue(Lexicon.Get("audio.fields.tx_filter_low"), 0, 9950, 50);
        _txFilterLowControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.TXFilterLow = v; };
        TxContent.Children.Add(_txFilterLowControl);

        // TX Filter High
        _txFilterHighControl = MakeValue(Lexicon.Get("audio.fields.tx_filter_high"), 50, 10000, 50);
        _txFilterHighControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.TXFilterHigh = v; };
        TxContent.Children.Add(_txFilterHighControl);

        // TX Monitor
        _monitorCheck = MakeToggle(Lexicon.Get("audio.fields.tx_monitor"));
        _monitorCheck.Checked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.tx_monitor"), v => { if (_rig != null) _rig.Monitor = v; }, true);
            _monitorLevelControl.Visibility = Visibility.Visible;
        };
        _monitorCheck.Unchecked += (s, e) =>
        {
            ToggleRig(Lexicon.Get("audio.fields.tx_monitor"), v => { if (_rig != null) _rig.Monitor = v; }, false);
            _monitorLevelControl.Visibility = Visibility.Collapsed;
        };
        TxContent.Children.Add(_monitorCheck);

        // Monitor Level (shown when Monitor is on)
        _monitorLevelControl = MakeValue(Lexicon.Get("audio.fields.monitor_level"), 0, 100, 5);
        _monitorLevelControl.Visibility = Visibility.Collapsed;
        _monitorLevelControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.SBMonitorLevel = v; };
        TxContent.Children.Add(_monitorLevelControl);
    }

    private void BuildAntennaControls()
    {
        // RX/TX antenna combos — populated dynamically at Initialize
        _rxAntennaControl = MakeCycle(Lexicon.Get("audio.fields.rx_antenna"), new[] { "ANT1", "ANT2" });
        _rxAntennaControl.SelectionChanged += (s, idx) =>
        {
            // No Speak — the control announces its new value natively.
            if (_rig == null) return;
            var list = _rig.RXAntennaList;
            if (idx >= 0 && idx < list.Count)
                _rig.RXAntennaName = list[idx];
        };
        AntennaContent.Children.Add(_rxAntennaControl);

        _txAntennaControl = MakeCycle(Lexicon.Get("audio.fields.tx_antenna"), new[] { "ANT1", "ANT2" });
        _txAntennaControl.SelectionChanged += (s, idx) =>
        {
            // No Speak — the control announces its new value natively.
            if (_rig == null) return;
            var list = _rig.TXAntennaList;
            if (idx >= 0 && idx < list.Count)
                _rig.TXAntennaName = list[idx];
        };
        AntennaContent.Children.Add(_txAntennaControl);

        _atuCheck = MakeToggle(Lexicon.Get("audio.fields.atu"));
        _atuCheck.Checked += (s, e) => ToggleBoolRig(Lexicon.Get("audio.fields.atu"), v =>
        {
            if (_rig != null) _rig.FlexTunerType = FlexBase.FlexTunerTypes.auto;
        }, true);
        _atuCheck.Unchecked += (s, e) => ToggleBoolRig(Lexicon.Get("audio.fields.atu"), v =>
        {
            if (_rig != null) _rig.FlexTunerType = FlexBase.FlexTunerTypes.none;
        }, false);
        AntennaContent.Children.Add(_atuCheck);

        _atuModeControl = MakeCycle(Lexicon.Get("audio.fields.atu_mode"), new[] { Lexicon.Get("audio.fields.atu_mode_none"), Lexicon.Get("audio.fields.atu_mode_manual"), Lexicon.Get("audio.fields.atu_mode_auto") });
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

    /// <summary>
    /// Walk up the visual tree to the hosting MainWindow. MainWindow is a
    /// UserControl inside the WinForms shell, not a WPF Window, so
    /// Window.GetWindow cannot find it.
    /// </summary>
    private MainWindow? FindMainWindow()
    {
        DependencyObject? cur = this;
        while (cur != null)
        {
            if (cur is MainWindow mw) return mw;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return null;
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

    #endregion

    #region Rig Toggle Helpers

    private void ToggleRig(string label, Action<FlexBase.OffOnValues> setter, bool isOn)
    {
        if (_polling || _rig == null) return;
        setter(isOn ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off);
        if (isOn) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
        // interrupt: true cuts off NVDA's native "checked"/"not checked" announcement
        ScreenReaderOutput.Speak(
            Lexicon.Get(isOn ? "audio.fields.toggle_on" : "audio.fields.toggle_off", ("label", label)),
            VerbosityLevel.Terse, interrupt: true);
    }

    private void ToggleBoolRig(string label, Action<bool> setter, bool isOn)
    {
        if (_polling || _rig == null) return;
        setter(isOn);
        if (isOn) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
        ScreenReaderOutput.Speak(
            Lexicon.Get(isOn ? "audio.fields.toggle_on" : "audio.fields.toggle_off", ("label", label)),
            VerbosityLevel.Terse, interrupt: true);
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
            _nrfCheck.IsChecked = _rig.NoiseReductionFilter == FlexBase.OffOnValues.on;

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

        // PC-side NR (pipeline state, not rig state)
        if (_audioPipeline != null)
        {
            bool rnnOn = _audioPipeline.RnnEnabled;
            _pcRnnCheck.IsChecked = rnnOn;
            _pcRnnStrengthControl.Visibility = rnnOn ? Visibility.Visible : Visibility.Collapsed;
            _pcRnnVoiceOnlyCheck.Visibility = rnnOn ? Visibility.Visible : Visibility.Collapsed;
            if (rnnOn)
            {
                _pcRnnStrengthControl.Value = (int)Math.Round(_audioPipeline.RnnStrength * 100);
                _pcRnnVoiceOnlyCheck.IsChecked = _audioPipeline.RnnAutoDisableNonVoice;
            }

            bool subOn = _audioPipeline.SpectralEnabled;
            _pcSpectralCheck.IsChecked = subOn;
            _pcSpectralStrengthControl.Visibility = subOn ? Visibility.Visible : Visibility.Collapsed;
            _pcSpectralFloorControl.Visibility = subOn ? Visibility.Visible : Visibility.Collapsed;
            if (subOn)
            {
                _pcSpectralStrengthControl.Value = (int)Math.Round(_audioPipeline.SpectralStrength * 100);
                _pcSpectralFloorControl.Value = (int)Math.Round(_audioPipeline.SpectralFloor * 100);
            }

            UpdateNoiseProfileDisplay();
        }

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

        // PC audio group
        _pcVolumeControl.Value = _rig.PcOutputVolumeDb;
        _micLevelControl.Value = _rig.MicGain;
        UpdateMicVerdict();

        // On-radio outputs group
        _headphoneControl.Value = _rig.HeadphoneGain;
        _lineoutControl.Value = _rig.LineoutGain;
        _headphoneMuteCheck.IsChecked = _rig.HeadphoneMute;
        _lineoutMuteCheck.IsChecked = _rig.LineoutMute;
        _frontSpeakerMuteCheck.IsChecked = _rig.FrontSpeakerMute;
    }

    /// <summary>
    /// Refresh the mic-audio verdict readout. Live SC_MIC recent peak while
    /// transmitting (it follows a level back down), the whole-transmit peak
    /// after unkey, and honest "no data" wording before any transmit. The
    /// accessible name is left untouched while the control is focused so a
    /// changing verdict doesn't flood the screen reader; GotFocus refreshes
    /// the name on entry and the screen reader reads it natively.
    /// </summary>
    private void UpdateMicVerdict()
    {
        if (_rig == null) return;

        string text;
        float recent = _rig.ScMicRecentDb;
        float max = _rig.ScMicMaxDb;
        if (_rig.Transmit && recent > -140f)
            text = MicAudioReport.Compose(_rig, Lexicon.Get("audio.fields.mic_verdict_now"), recent, live: true);
        else if (max > -140f)
            text = MicAudioReport.Compose(_rig, Lexicon.Get("audio.fields.mic_verdict_last"), max, live: false);
        else
            text = Lexicon.Get("audio.fields.mic_verdict_none");

        if (text != _micVerdictDisplay.Text)
        {
            _micVerdictDisplay.Text = text;
            if (!_micVerdictDisplay.IsKeyboardFocused)
                System.Windows.Automation.AutomationProperties.SetName(_micVerdictDisplay, text);
        }
    }

    /// <summary>
    /// DSP controls track — refresh the noise-profile readout. Same
    /// accessible-name discipline as the mic verdict: text updates live, the
    /// name only changes while the control is unfocused, GotFocus refreshes
    /// the name on entry and the screen reader reads it natively.
    /// </summary>
    private void UpdateNoiseProfileDisplay()
    {
        string text;
        if (NoiseCaptureNarrator.IsRunning)
            text = Lexicon.Get("audio.fields.noise_profile_capturing");
        else if (_audioPipeline == null)
            text = Lexicon.Get("audio.fields.noise_profile_no_radio");
        else if (_audioPipeline.HasNoiseProfile)
        {
            string name = _audioPipeline.NoiseProfileName;
            text = string.IsNullOrEmpty(name)
                ? Lexicon.Get("audio.fields.noise_profile_this_session")
                : $"Noise profile: {name}";
        }
        else
            text = Lexicon.Get("audio.fields.noise_profile_none_hint");

        if (text != _noiseProfileDisplay.Text)
        {
            _noiseProfileDisplay.Text = text;
            if (!_noiseProfileDisplay.IsKeyboardFocused)
                System.Windows.Automation.AutomationProperties.SetName(_noiseProfileDisplay, text);
        }
    }

    /// <summary>
    /// Keep the capture button honest while a capture runs: pressing it then
    /// cancels, so it must say so. Narrator StateChanged drives this.
    /// </summary>
    private void UpdateCaptureButtonLabel()
    {
        bool running = NoiseCaptureNarrator.IsRunning;
        _captureNoiseButton.Content = running ? Lexicon.Get("audio.fields.cancel_noise_button") : Lexicon.Get("audio.fields.capture_noise_button");
        System.Windows.Automation.AutomationProperties.SetName(_captureNoiseButton,
            running ? Lexicon.Get("audio.fields.cancel_noise_name")
                    : Lexicon.Get("audio.fields.capture_noise_name"));
        UpdateNoiseProfileDisplay();
    }

    /// <summary>
    /// DSP controls track — push the persisted PC-side NR settings into the
    /// freshly created pipeline, and reload the last noise profile so
    /// Spectral NR comes back exactly as the operator left it. Called by
    /// MainWindow.PowerOn after the audio config loads (the pipeline itself
    /// is created earlier, in Initialize). Deliberately silent: this is
    /// connect-time restore, not an operator action.
    /// </summary>
    public void ApplyDspConfig(AudioOutputConfig cfg)
    {
        if (_audioPipeline == null) return;

        _audioPipeline.RnnEnabled = cfg.RNNoiseEnabled;
        _audioPipeline.RnnStrength = Math.Clamp(cfg.RNNoiseStrength, 0f, 1f);
        _audioPipeline.RnnAutoDisableNonVoice = cfg.RNNoiseAutoDisableNonVoice;
        _audioPipeline.SpectralEnabled = cfg.SpectralSubEnabled;
        _audioPipeline.SpectralStrength = Math.Clamp(cfg.SpectralSubStrength, 0f, 1f);
        _audioPipeline.SpectralFloor = Math.Clamp(cfg.SpectralSubFloor, 0f, 1f);

        string lastProfile = cfg.NoiseProfileLastPath;
        if (!string.IsNullOrEmpty(lastProfile) && System.IO.File.Exists(lastProfile))
            _audioPipeline.LoadNoiseProfile(lastProfile);

        PollUpdate();
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

        // RX filter width (read-only)
        int filterLow = _rig.FilterLow;
        int filterHigh = _rig.FilterHigh;
        int filterWidth = filterHigh - filterLow;
        string widthText = filterWidth >= 1000
            ? Lexicon.Get("audio.fields.rx_filter_width_khz", ("filterLow", filterLow),
                ("filterHigh", filterHigh), ("filterWidth", $"{filterWidth / 1000.0:F1}"))
            : Lexicon.Get("audio.fields.rx_filter_width_hz", ("filterLow", filterLow),
                ("filterHigh", filterHigh), ("filterWidth", filterWidth));
        _rxFilterWidthDisplay.Text = widthText;
        System.Windows.Automation.AutomationProperties.SetName(_rxFilterWidthDisplay, widthText);
    }

    /// <summary>
    /// QB Track I — put the TX power field in the right personality for the
    /// current TX antenna. Watts (integer, 0-100) normally; transverter drive
    /// (dBm, hundredths, FlexLib bounds) when the TX antenna is the XVTR
    /// port. The unit rides the label's value suffix, so entering the field
    /// always announces which scale it is on.
    /// </summary>
    private void ReconfigureTxPowerForMode(bool xvtr)
    {
        _txPowerXvtrMode = xvtr;
        if (xvtr && _rig != null)
        {
            _txPowerControl.Setup(Lexicon.Get("audio.fields.tx_power"), FlexBase.XvtrDriveMinCentiDbm,
                _rig.XvtrDriveMaxCentiDbm, FlexBase.XvtrDriveIncrementCentiDbm,
                _rig.XvtrDrivePowerCentiDbm, decimalPlaces: 2, unit: Lexicon.Get("audio.fields.unit_dbm"));
        }
        else
        {
            _txPowerControl.Setup(Lexicon.Get("audio.fields.tx_power"), 0, 100, 1, _rig?.XmitPower ?? 0);
        }
    }

    private void PollTX()
    {
        if (_rig == null) return;

        // Personality follows the TX antenna. Speak the flip only when the
        // operator is sitting on the field — silently changing the meaning of
        // a focused number would be lying; announcing background flips from a
        // poll would be noise.
        bool xvtrNow = _rig.XvtrPowerAvailable;
        if (xvtrNow != _txPowerXvtrMode)
        {
            ReconfigureTxPowerForMode(xvtrNow);
            if (_txPowerControl.IsKeyboardFocusWithin)
            {
                ScreenReaderOutput.Speak(xvtrNow
                    ? Lexicon.Get("audio.fields.tx_power_now_dbm")
                    : Lexicon.Get("audio.fields.tx_power_now_watts"), VerbosityLevel.Terse, interrupt: true);
            }
        }

        _txPowerControl.Value = xvtrNow ? _rig.XvtrDrivePowerCentiDbm : _rig.XmitPower;
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

        // ATU controls — checkbox reflects tuner type (none=off, manual/auto=on)
        _atuCheck.IsChecked = _rig.FlexTunerType != FlexBase.FlexTunerTypes.none;

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
            expander.IsExpanded = false; // Sprint 28 Phase 3 — Collapsed event now handles announcement + earcon
            ReturnFocusToFreqOut?.Invoke();
        }
        else
        {
            expander.IsExpanded = true; // Sprint 28 Phase 3 — Expanded event now handles announcement + earcon

            // Focus the first focusable control in the expanded content.
            // Delay slightly so the "expanded" speech finishes before the
            // focused control announces itself (otherwise NVDA steps on it).
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, async () =>
            {
                await System.Threading.Tasks.Task.Delay(150);
                var content = GetCategoryContent(index);
                if (content != null)
                {
                    var firstFocusable = FindFirstFocusableChild(content);
                    if (firstFocusable != null)
                        Keyboard.Focus(firstFocusable);
                    else
                        ExpanderFocus.FocusHeader(expander);
                }
                else
                {
                    ExpanderFocus.FocusHeader(expander);
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
        // The HEADER, not the container — see ExpanderFocus. This line focused
        // the container raw until task #105, which is the silent-landing plus
        // dead-Space pair, arriving here for anyone who expanded a category
        // through the API rather than through Ctrl+Tab.
        ExpanderFocus.FocusHeader(_expanders[index]);
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
        // Sprint 28 Phase 3 — Escape semantics:
        //   Single Escape with focus inside an expanded group → collapse that
        //   group, focus lands on its header (OnGroupCollapsed plays the collapse
        //   earcon and announces).
        //   Double Escape within DoubleTapTolerance → collapse ALL expanded groups
        //   and return focus to Home (FreqOut), with a single gavel earcon and a
        //   single "all panels collapsed, home" announcement.
        //   Single Escape with no expanded group in focus → legacy behavior,
        //   return to FreqOut (this preserves "Escape = back out" for users who
        //   aren't actively working inside a group).
        if (e.Key == Key.Escape)
        {
            var now = DateTime.UtcNow;
            bool isDoubleEscape = (now - _lastEscapeTime).TotalMilliseconds
                                  < Radios.AccessibilityConfig.Current.DoubleTapToleranceMs;
            _lastEscapeTime = now;

            if (isDoubleEscape)
            {
                // Reset so a third Escape doesn't re-trigger collapse-all.
                _lastEscapeTime = DateTime.MinValue;
                // Cancel any pending collapse earcon from the first Escape — the
                // gavel is about to play and we don't want the collapse earcon
                // arriving on top of it. Bug 3 fix (Phase 3.3).
                _pendingCollapseEarconTimer?.Stop();
                _pendingCollapseEarconTimer = null;
                CollapseAllGroupsAndGoHome();
                e.Handled = true;
                return;
            }

            // Single Escape — if focus is inside an expanded group, collapse it.
            // Order matters: Focus() BEFORE IsExpanded=false. See Phase 3.2 fix.
            var targetExpander = FindFocusedExpandedGroup();
            if (targetExpander != null)
            {
                // Focus the inner ToggleButton rather than the Expander container.
                // Tab-navigation lands on the ToggleButton naturally (which is why
                // manually-tabbed focus + Space works for expand/collapse); but
                // programmatic Expander.Focus() lands on the Expander element
                // itself, and Space doesn't route to the toggle from there. This
                // is why Phase 3/3.2's attempt to "focus the header" didn't let
                // Space re-expand after Escape-collapse. Finding and focusing the
                // ToggleButton specifically fixes it. Bug 2 fix (Phase 3.4,
                // 2026-04-21 — user green-lit find-toggle-button approach).
                // The walk itself now lives in ExpanderFocus (task #105) — the
                // RigSelector had independently derived the identical fix.
                ExpanderFocus.FocusHeader(targetExpander);

                // Collapse the group with the per-group earcon suppressed — we'll
                // play the collapse earcon deferred via timer so a potential second
                // Escape can cancel it before it fires. The collapse action itself
                // happens immediately (user gets instant NVDA focus-change feedback
                // from the header state transition). Only the earcon is deferred.
                // Bug 3 fix (Phase 3.3, 2026-04-21 — "collapse tone and gavel layer").
                _suppressCollapseEarcons = true;
                try
                {
                    targetExpander.IsExpanded = false;
                }
                finally
                {
                    _suppressCollapseEarcons = false;
                }

                // Schedule the collapse earcon for tolerance + 50 ms. If a second
                // Escape arrives within tolerance, the double-Escape branch above
                // will stop and null this timer before it fires.
                _pendingCollapseEarconTimer?.Stop();
                _pendingCollapseEarconTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(
                        Radios.AccessibilityConfig.Current.DoubleTapToleranceMs + 50)
                };
                _pendingCollapseEarconTimer.Tick += (s, args) =>
                {
                    _pendingCollapseEarconTimer?.Stop();
                    _pendingCollapseEarconTimer = null;
                    EarconPlayer.PlayCollapse();
                };
                _pendingCollapseEarconTimer.Start();

                e.Handled = true;
                return;
            }

            // Fallback — no expanded group in focus: legacy "return to FreqOut".
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

    /// <summary>
    /// Sprint 28 Phase 3 — find the expanded group containing the currently focused
    /// element. Returns null if focus is outside any group, or if the enclosing
    /// group is already collapsed.
    /// </summary>
    private Expander? FindFocusedExpandedGroup()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused == null) return null;
        foreach (var exp in _expanders)
        {
            if (exp.IsExpanded && IsDescendantOf(focused, exp))
                return exp;
        }
        return null;
    }

    /// <summary>
    /// Sprint 28 Phase 3 — collapse every expanded group without individual
    /// earcons or announcements, then play the gavel earcon, announce once
    /// (&quot;All panels collapsed, home&quot;), and return focus to FreqOut.
    /// </summary>
    private void CollapseAllGroupsAndGoHome()
    {
        // Play the gavel first so it leads audibly — bulk collapse is essentially
        // instantaneous; playing the gavel afterward would desync audio from the
        // visual/focus state change.
        EarconPlayer.PlayCollapseAll();

        _suppressCollapseEarcons = true;
        try
        {
            foreach (var exp in _expanders)
            {
                if (exp.IsExpanded)
                    exp.IsExpanded = false;
            }
        }
        finally
        {
            _suppressCollapseEarcons = false;
        }

        ScreenReaderOutput.Speak(
            Lexicon.Get("audio.fields.all_collapsed"), VerbosityLevel.Terse, interrupt: true);

        // Return focus to Home (FreqOut) — MainWindow wires this event to
        // FreqOut.FocusDisplay() per the pattern established pre-Sprint-28.
        EscapePressed?.Invoke(this, EventArgs.Empty);
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

        // Focus the expander's toggle button (header). Using the shared
        // ExpanderFocus helper so the toggle receives keyboard focus
        // consistently — enables Space-to-expand/collapse after Ctrl+Tab
        // navigation. Sprint 28 Phase 3.4 alignment with the Escape-collapse fix.
        ExpanderFocus.FocusHeader(visible[nextIndex]);
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
