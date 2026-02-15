using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Controls;

/// <summary>
/// WPF replacement for Radios\Flex6300Filters.cs (2,822 lines).
/// Pure WPF UserControl — no WinForms, no ElementHost.
///
/// Architecture:
/// - Control structure defined in FiltersDspControl.xaml (GroupBox sections)
/// - This code-behind sets headers, ranges, and initial configuration
/// - Rig delegate wiring (UpdateDisplayFunction, UpdateRigFunction) happens
///   externally when a radio connects, NOT here — this control doesn't reference
///   FlexBase or FlexLib directly (avoids circular project references)
/// - UpdateAllControls() is called from the poll timer to refresh all boxes
/// - Cleanup() unhooks delegates when radio disconnects
///
/// The old Flex6300Filters.cs name was specific to the 6300 model but this
/// control works with all Flex 6000/8000 series and Aurora radios.
///
/// Sprint 8 Phase 8.7.
/// </summary>
public partial class FiltersDspControl : UserControl
{
    #region Control Collections (for bulk operations)

    /// <summary>
    /// All RadioComboBox controls on this panel, for bulk UpdateDisplay calls.
    /// </summary>
    public List<RadioComboBox> ComboBoxes { get; } = new();

    /// <summary>
    /// All RadioNumberBox controls on this panel, for bulk UpdateDisplay calls.
    /// </summary>
    public List<RadioNumberBox> NumberBoxes { get; } = new();

    /// <summary>
    /// All RadioInfoBox controls on this panel, for bulk UpdateDisplay calls.
    /// </summary>
    public List<RadioInfoBox> InfoBoxes { get; } = new();

    /// <summary>
    /// Special update delegates called during each poll cycle.
    /// Used for enable/disable logic, license gating, etc.
    /// </summary>
    public List<Action> SpecialUpdates { get; } = new();

    /// <summary>
    /// Delegates called on mode change (CW→SSB, etc.) to adjust control visibility/state.
    /// </summary>
    public List<Action> ModeChangeActions { get; } = new();

    #endregion

    #region Button Click Delegates

    /// <summary>Called when the Narrow button is clicked. Typically narrows the RX filter.</summary>
    public Action? NarrowFilterAction { get; set; }

    /// <summary>Called when the Widen button is clicked. Typically widens the RX filter.</summary>
    public Action? WidenFilterAction { get; set; }

    /// <summary>Called when the Shift button is clicked. Typically shifts the filter passband.</summary>
    public Action? ShiftFilterAction { get; set; }

    /// <summary>Called when the TNF button is clicked. Opens the Tracking Notch Filter dialog.</summary>
    public Action? TNFAction { get; set; }

    /// <summary>Called when the ESC button is clicked. Emergency Signal Cancel.</summary>
    public Action? ESCAction { get; set; }

    /// <summary>Called when the Info button is clicked. Shows Feature Availability.</summary>
    public Action? InfoAction { get; set; }

    /// <summary>Called when the RX EQ button is clicked. Opens RX Equalizer.</summary>
    public Action? RXEqualizerAction { get; set; }

    /// <summary>Called when the TX EQ button is clicked. Opens TX Equalizer.</summary>
    public Action? TXEqualizerAction { get; set; }

    #endregion

    #region Filter Constants (match Flex6300Filters.cs)

    /// <summary>Minimum value for FilterLow/FilterHigh controls.</summary>
    public const int FilterLowMinimum = -12000;

    /// <summary>Maximum value for FilterLow/FilterHigh controls.</summary>
    public const int FilterLowMaximum = 12000;

    /// <summary>Step size for FilterLow/FilterHigh arrow-key adjustment.</summary>
    public const int FilterIncrement = 50;

    #endregion

    public FiltersDspControl()
    {
        InitializeComponent();
        InitializeControlHeaders();
        InitializeNumberBoxRanges();
        RegisterControlCollections();
        WireButtonHandlers();
    }

    #region Initialization

    /// <summary>
    /// Set the Header property on every RadioComboBox, RadioNumberBox, and RadioInfoBox.
    /// Headers are the labels shown above each control and also set the AutomationProperties.Name.
    /// </summary>
    private void InitializeControlHeaders()
    {
        // === DSP / Filters ===
        FilterLowBox.Header = "Filter Low";
        FilterHighBox.Header = "Filter High";
        AGCSpeedControl.Header = "AGC Speed";
        AGCThresholdBox.Header = "AGC Threshold";
        RFGainBox.Header = "RF Gain";

        // === Noise Reduction ===
        NRControl.Header = "Noise Reduction";
        NRLevelBox.Header = "NR Level";
        ANFControl.Header = "Auto Notch";
        ANFLevelBox.Header = "ANF Level";
        NBControl.Header = "Noise Blanker";
        NBLevelBox.Header = "NB Level";
        WNBControl.Header = "Wideband NB";
        WNBLevelBox.Header = "WNB Level";
        APFControl.Header = "Audio Peak Filter";
        APFLevelBox.Header = "APF Level";

        // === CW / Keyer ===
        KeyerModeControl.Header = "Keyer Mode";
        KeyerSpeedBox.Header = "Keyer Speed";
        CWReverseControl.Header = "CW Reverse";
        SidetonePitchBox.Header = "Sidetone Pitch";
        SidetoneGainBox.Header = "Sidetone Gain";
        CWLControl.Header = "CW Listener";
        BreakinDelayBox.Header = "Break-in Delay";

        // === Transmit ===
        TXPowerBox.Header = "TX Power";
        TunePowerBox.Header = "Tune Power";
        TXFilterLowBox.Header = "TX Filter Low";
        TXFilterHighBox.Header = "TX Filter High";
        CompanderControl.Header = "Compander";
        CompanderLevelBox.Header = "Compander Level";
        SpeechProcControl.Header = "Speech Proc";

        // === Audio / Microphone ===
        MicGainBox.Header = "Mic Gain";
        MicBoostControl.Header = "Mic Boost";
        MicBiasControl.Header = "Mic Bias";
        MicPeakInfo.Header = "Mic Peak";
        MonitorPanBox.Header = "Monitor Pan";
        MonitorLevelBox.Header = "Monitor Level";
        MonitorModeControl.Header = "Monitor Mode";

        // === Antenna ===
        RXAntennaControl.Header = "RX Antenna";
        TXAntennaControl.Header = "TX Antenna";
        DiversityControl.Header = "Diversity";
        DiversityStatusInfo.Header = "Diversity Status";

        // === Status / Info ===
        SWRInfo.Header = "SWR";
        PATempInfo.Header = "PA Temp";
        VoltsInfo.Header = "Voltage";

        // InfoBoxes default to read-only
        MicPeakInfo.IsReadOnly = true;
        SWRInfo.IsReadOnly = true;
        PATempInfo.IsReadOnly = true;
        VoltsInfo.IsReadOnly = true;
        DiversityStatusInfo.IsReadOnly = true;
    }

    /// <summary>
    /// Set default ranges for NumberBox controls.
    /// These are overridden when rig connects (some ranges are rig-specific),
    /// but we provide sensible defaults so controls aren't unbound.
    ///
    /// Ranges match constants from FlexBase and Flex6300Filters.cs.
    /// </summary>
    private void InitializeNumberBoxRanges()
    {
        // Filter boundaries
        FilterLowBox.LowValue = FilterLowMinimum;
        FilterLowBox.HighValue = FilterLowMaximum;
        FilterLowBox.Increment = FilterIncrement;

        FilterHighBox.LowValue = FilterLowMinimum;
        FilterHighBox.HighValue = FilterLowMaximum;
        FilterHighBox.Increment = FilterIncrement;

        // AGC Threshold — range from FlexBase.AGCThresholdMin/Max
        AGCThresholdBox.LowValue = 0;
        AGCThresholdBox.HighValue = 100;
        AGCThresholdBox.Increment = 1;

        // RF Gain — range varies by rig, these are safe defaults
        RFGainBox.LowValue = 0;
        RFGainBox.HighValue = 50;
        RFGainBox.Increment = 1;

        // Noise Reduction level
        NRLevelBox.LowValue = 0;
        NRLevelBox.HighValue = 100;
        NRLevelBox.Increment = 1;

        // ANF level
        ANFLevelBox.LowValue = 0;
        ANFLevelBox.HighValue = 100;
        ANFLevelBox.Increment = 1;

        // NB level
        NBLevelBox.LowValue = 0;
        NBLevelBox.HighValue = 100;
        NBLevelBox.Increment = 1;

        // WNB level
        WNBLevelBox.LowValue = 0;
        WNBLevelBox.HighValue = 100;
        WNBLevelBox.Increment = 1;

        // APF level
        APFLevelBox.LowValue = 0;
        APFLevelBox.HighValue = 100;
        APFLevelBox.Increment = 1;

        // CW Keyer speed (WPM) — FlexBase.KeyerSpeedMin/Max
        KeyerSpeedBox.LowValue = 5;
        KeyerSpeedBox.HighValue = 100;
        KeyerSpeedBox.Increment = 1;

        // Sidetone pitch (Hz) — FlexBase.SidetonePitchMin/Max
        SidetonePitchBox.LowValue = 100;
        SidetonePitchBox.HighValue = 2600;
        SidetonePitchBox.Increment = 10;

        // Sidetone gain — FlexBase.SidetoneGainMin/Max
        SidetoneGainBox.LowValue = 0;
        SidetoneGainBox.HighValue = 100;
        SidetoneGainBox.Increment = 1;

        // Break-in delay (ms) — FlexBase.BreakinDelayMin/Max
        BreakinDelayBox.LowValue = 0;
        BreakinDelayBox.HighValue = 2000;
        BreakinDelayBox.Increment = 50;

        // TX Power — FlexBase.XmitPowerMin/Max
        TXPowerBox.LowValue = 0;
        TXPowerBox.HighValue = 100;
        TXPowerBox.Increment = 1;

        // Tune Power — FlexBase.TunePowerMin/Max
        TunePowerBox.LowValue = 0;
        TunePowerBox.HighValue = 100;
        TunePowerBox.Increment = 1;

        // TX Filter Low/High — range varies by rig
        TXFilterLowBox.LowValue = 0;
        TXFilterLowBox.HighValue = 10000;
        TXFilterLowBox.Increment = 50;

        TXFilterHighBox.LowValue = 0;
        TXFilterHighBox.HighValue = 10000;
        TXFilterHighBox.Increment = 50;

        // Compander level — FlexBase.CompanderLevelMin/Max
        CompanderLevelBox.LowValue = 0;
        CompanderLevelBox.HighValue = 100;
        CompanderLevelBox.Increment = 1;

        // Mic Gain — FlexBase.MicGainMin/Max
        MicGainBox.LowValue = 0;
        MicGainBox.HighValue = 100;
        MicGainBox.Increment = 1;

        // Monitor Pan — FlexBase.MonitorPanMin/Max
        MonitorPanBox.LowValue = 0;
        MonitorPanBox.HighValue = 100;
        MonitorPanBox.Increment = 1;

        // Monitor Level — FlexBase.SBMonitorLevelMin/Max
        MonitorLevelBox.LowValue = 0;
        MonitorLevelBox.HighValue = 100;
        MonitorLevelBox.Increment = 1;
    }

    /// <summary>
    /// Register all named controls into their respective collections
    /// for bulk UpdateDisplay/enable/disable operations.
    /// </summary>
    private void RegisterControlCollections()
    {
        // ComboBoxes
        ComboBoxes.Add(AGCSpeedControl);
        ComboBoxes.Add(NRControl);
        ComboBoxes.Add(ANFControl);
        ComboBoxes.Add(NBControl);
        ComboBoxes.Add(WNBControl);
        ComboBoxes.Add(APFControl);
        ComboBoxes.Add(KeyerModeControl);
        ComboBoxes.Add(CWReverseControl);
        ComboBoxes.Add(CWLControl);
        ComboBoxes.Add(CompanderControl);
        ComboBoxes.Add(SpeechProcControl);
        ComboBoxes.Add(MicBoostControl);
        ComboBoxes.Add(MicBiasControl);
        ComboBoxes.Add(MonitorModeControl);
        ComboBoxes.Add(RXAntennaControl);
        ComboBoxes.Add(TXAntennaControl);
        ComboBoxes.Add(DiversityControl);

        // NumberBoxes
        NumberBoxes.Add(FilterLowBox);
        NumberBoxes.Add(FilterHighBox);
        NumberBoxes.Add(AGCThresholdBox);
        NumberBoxes.Add(RFGainBox);
        NumberBoxes.Add(NRLevelBox);
        NumberBoxes.Add(ANFLevelBox);
        NumberBoxes.Add(NBLevelBox);
        NumberBoxes.Add(WNBLevelBox);
        NumberBoxes.Add(APFLevelBox);
        NumberBoxes.Add(KeyerSpeedBox);
        NumberBoxes.Add(SidetonePitchBox);
        NumberBoxes.Add(SidetoneGainBox);
        NumberBoxes.Add(BreakinDelayBox);
        NumberBoxes.Add(TXPowerBox);
        NumberBoxes.Add(TunePowerBox);
        NumberBoxes.Add(TXFilterLowBox);
        NumberBoxes.Add(TXFilterHighBox);
        NumberBoxes.Add(CompanderLevelBox);
        NumberBoxes.Add(MicGainBox);
        NumberBoxes.Add(MonitorPanBox);
        NumberBoxes.Add(MonitorLevelBox);

        // InfoBoxes
        InfoBoxes.Add(MicPeakInfo);
        InfoBoxes.Add(SWRInfo);
        InfoBoxes.Add(PATempInfo);
        InfoBoxes.Add(VoltsInfo);
        InfoBoxes.Add(DiversityStatusInfo);
    }

    /// <summary>
    /// Wire button Click handlers to their delegate properties.
    /// </summary>
    private void WireButtonHandlers()
    {
        NarrowButton.Click += (s, e) => NarrowFilterAction?.Invoke();
        WidenButton.Click += (s, e) => WidenFilterAction?.Invoke();
        ShiftButton.Click += (s, e) => ShiftFilterAction?.Invoke();
        TNFButton.Click += (s, e) => TNFAction?.Invoke();
        ESCButton.Click += (s, e) => ESCAction?.Invoke();
        InfoButton.Click += (s, e) => InfoAction?.Invoke();
        RXEqualizer.Click += (s, e) => RXEqualizerAction?.Invoke();
        TXEqualizer.Click += (s, e) => TXEqualizerAction?.Invoke();
    }

    #endregion

    #region Poll Cycle (called from PollTimer)

    /// <summary>
    /// Update all controls by polling the rig via their UpdateDisplayFunction delegates.
    /// Called from the 100ms PollTimer in MainWindow.
    /// Matches the updateBoxes() method in Flex6300Filters.cs.
    /// </summary>
    public void UpdateAllControls()
    {
        try
        {
            // Update combos
            foreach (var combo in ComboBoxes)
            {
                if (combo.IsEnabled)
                    combo.UpdateDisplay();
            }

            // Update number boxes
            foreach (var numberBox in NumberBoxes)
            {
                if (numberBox.IsEnabled)
                    numberBox.UpdateDisplay();
            }

            // Update info boxes
            foreach (var infoBox in InfoBoxes)
            {
                if (infoBox.IsEnabled)
                    infoBox.UpdateDisplay();
            }

            // Run special update delegates (enable/disable logic, license gating)
            foreach (var special in SpecialUpdates)
            {
                special();
            }
        }
        catch (Exception)
        {
            // Swallow exceptions during polling — same as Flex6300Filters.updateBoxes()
            // Prevents one bad rig value from crashing the entire update cycle.
        }
    }

    /// <summary>
    /// Enable or disable all controls on this panel.
    /// Called when radio connects/disconnects.
    /// </summary>
    public void SetAllControlsEnabled(bool enabled)
    {
        foreach (var combo in ComboBoxes)
            combo.IsEnabled = enabled;

        foreach (var numberBox in NumberBoxes)
            numberBox.IsEnabled = enabled;

        foreach (var infoBox in InfoBoxes)
            infoBox.IsEnabled = enabled;

        NarrowButton.IsEnabled = enabled;
        WidenButton.IsEnabled = enabled;
        ShiftButton.IsEnabled = enabled;
        TNFButton.IsEnabled = enabled;
        ESCButton.IsEnabled = enabled;
        InfoButton.IsEnabled = enabled;
        RXEqualizer.IsEnabled = enabled;
        TXEqualizer.IsEnabled = enabled;
    }

    /// <summary>
    /// Set all controls to read-only. Used for license gating.
    /// </summary>
    public void SetAllControlsReadOnly(bool readOnly)
    {
        foreach (var combo in ComboBoxes)
            combo.IsReadOnly = readOnly;

        foreach (var numberBox in NumberBoxes)
            numberBox.IsReadOnly = readOnly;
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// Unhook all delegate references when the radio disconnects.
    /// Prevents stale references to a disposed radio object.
    /// Matches Cleanup() in Flex6300Filters.cs.
    /// </summary>
    public void Cleanup()
    {
        // Clear combo delegates
        foreach (var combo in ComboBoxes)
        {
            combo.UpdateDisplayFunction = null;
            combo.UpdateRigFunction = null;
            combo.UpdateRigByIndexFunction = null;
            combo.BoxIndexFunction = null;
            combo.ClearCache();
        }

        // Clear number box delegates
        foreach (var numberBox in NumberBoxes)
        {
            numberBox.UpdateDisplayFunction = null;
            numberBox.UpdateRigFunction = null;
        }

        // Clear info box delegates
        foreach (var infoBox in InfoBoxes)
        {
            infoBox.UpdateDisplayFunction = null;
            infoBox.UpdateRigFunction = null;
            infoBox.Clear();
        }

        // Clear button delegates
        NarrowFilterAction = null;
        WidenFilterAction = null;
        ShiftFilterAction = null;
        TNFAction = null;
        ESCAction = null;
        InfoAction = null;
        RXEqualizerAction = null;
        TXEqualizerAction = null;

        // Clear special delegates
        SpecialUpdates.Clear();
        ModeChangeActions.Clear();

        SetAllControlsEnabled(false);
    }

    #endregion
}
