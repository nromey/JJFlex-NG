using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// WPF replacement for EscDialog (Radios/EscDialog.cs).
/// Enhanced Signal Clarity controls: phase shift slider + gain slider.
/// Requires diversity-capable radio with diversity active.
///
/// All rig and slice operations use delegates — no direct FlexBase or Slice references.
///
/// Sprint 9 Track B.
/// </summary>
public partial class EscDialog : JJFlexDialog
{
    private const double GainMin = 0.0;
    private const double GainMax = 4.0;
    private const double GainStep = 0.05;

    private bool _suppressControlEvents;

    #region Delegates

    /// <summary>Gets current ESC enabled state.</summary>
    public Func<bool>? GetEscEnabled { get; set; }

    /// <summary>Sets ESC enabled state.</summary>
    public Action<bool>? SetEscEnabled { get; set; }

    /// <summary>Gets current phase shift in degrees.</summary>
    public Func<double>? GetPhaseShift { get; set; }

    /// <summary>Sets phase shift in degrees.</summary>
    public Action<double>? SetPhaseShift { get; set; }

    /// <summary>Gets current ESC gain.</summary>
    public Func<double>? GetEscGain { get; set; }

    /// <summary>Sets ESC gain value.</summary>
    public Action<double>? SetEscGain { get; set; }

    /// <summary>Returns true if the radio has a tracked slice available.</summary>
    public Func<bool>? HasActiveSlice { get; set; }

    /// <summary>Returns true if diversity hardware is present.</summary>
    public Func<bool>? IsDiversityReady { get; set; }

    /// <summary>Returns true if diversity is currently enabled.</summary>
    public Func<bool>? IsDiversityOn { get; set; }

    /// <summary>Returns a message explaining why diversity isn't available, or null.</summary>
    public Func<string?>? GetDiversityGateMessage { get; set; }

    /// <summary>Called when the dialog should refresh its state (e.g., slice changed).</summary>
    public Action<Action>? RegisterRefreshCallback { get; set; }

    /// <summary>Called when dialog closes to unregister notifications.</summary>
    public Action? UnregisterRefreshCallback { get; set; }

    #endregion

    public EscDialog()
    {
        InitializeComponent();
        Loaded += EscDialog_Loaded;
        Closed += EscDialog_Closed;
    }

    private void EscDialog_Loaded(object sender, RoutedEventArgs e)
    {
        RegisterRefreshCallback?.Invoke(UpdateControlsFromSlice);
        UpdateControlsFromSlice();
    }

    private void EscDialog_Closed(object? sender, EventArgs e)
    {
        UnregisterRefreshCallback?.Invoke();
    }

    private void UpdateControlsFromSlice()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateControlsFromSlice);
            return;
        }

        bool hasSlice = HasActiveSlice?.Invoke() ?? false;
        bool diversityReady = IsDiversityReady?.Invoke() ?? false;
        bool diversityActive = IsDiversityOn?.Invoke() ?? false;
        bool controlsEnabled = hasSlice && diversityReady && diversityActive;
        bool escEnabled = hasSlice && (GetEscEnabled?.Invoke() ?? false);

        _suppressControlEvents = true;
        try
        {
            EscEnabledCheckBox.IsChecked = escEnabled;
            EscEnabledCheckBox.IsEnabled = hasSlice && diversityActive;
            PhaseSlider.IsEnabled = controlsEnabled;
            PhaseValueBox.IsEnabled = controlsEnabled;
            GainSlider.IsEnabled = controlsEnabled;
            GainValueBox.IsEnabled = controlsEnabled;

            if (hasSlice)
            {
                UpdatePhaseDisplay(GetPhaseShift?.Invoke() ?? 0);
                UpdateGainDisplay(GetEscGain?.Invoke() ?? GainMin);
            }
            else
            {
                UpdatePhaseDisplay(0);
                UpdateGainDisplay(GainMin);
            }

            if (!hasSlice)
                StatusLabel.Text = "Select an active slice to adjust ESC.";
            else if (!diversityReady)
            {
                var gate = GetDiversityGateMessage?.Invoke();
                StatusLabel.Text = string.IsNullOrEmpty(gate) ? "Diversity not ready." : gate;
            }
            else if (!diversityActive)
                StatusLabel.Text = "Enable diversity to apply ESC.";
            else
                StatusLabel.Text = escEnabled ? "ESC is enabled." : "ESC is disabled.";
        }
        finally
        {
            _suppressControlEvents = false;
        }
    }

    private void UpdatePhaseDisplay(double value)
    {
        double normalized = NormalizePhase(value);
        int sliderValue = (int)Math.Round(normalized);
        sliderValue = Math.Max((int)PhaseSlider.Minimum, Math.Min((int)PhaseSlider.Maximum, sliderValue));

        bool prev = _suppressControlEvents;
        _suppressControlEvents = true;
        try
        {
            PhaseSlider.Value = sliderValue;
            PhaseValueBox.Text = normalized.ToString("0.0", CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressControlEvents = prev;
        }
    }

    private void UpdateGainDisplay(double value)
    {
        double normalized = NormalizeGain(value);
        int sliderValue = GainToSlider(normalized);
        sliderValue = Math.Max((int)GainSlider.Minimum, Math.Min((int)GainSlider.Maximum, sliderValue));

        bool prev = _suppressControlEvents;
        _suppressControlEvents = true;
        try
        {
            GainSlider.Value = sliderValue;
            GainValueBox.Text = normalized.ToString("0.00", CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressControlEvents = prev;
        }
    }

    private static double NormalizePhase(double degrees)
    {
        double normalized = degrees % 360.0;
        if (normalized < 0) normalized += 360.0;
        return normalized;
    }

    private static double NormalizeGain(double gain)
    {
        if (double.IsNaN(gain) || double.IsInfinity(gain)) return GainMin;
        return Math.Min(GainMax, Math.Max(GainMin, gain));
    }

    private static int GainToSlider(double gain) => (int)Math.Round(gain / GainStep);
    private static double SliderToGain(int value) => value * GainStep;

    private void ApplyPhase(double value)
    {
        double normalized = NormalizePhase(value);
        SetPhaseShift?.Invoke(normalized);
        UpdatePhaseDisplay(normalized);
    }

    private void ApplyGain(double value)
    {
        double normalized = NormalizeGain(value);
        SetEscGain?.Invoke(normalized);
        UpdateGainDisplay(normalized);
    }

    private double ParsePhaseText()
    {
        if (double.TryParse(PhaseValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;
        return 0.0;
    }

    private double ParseGainText()
    {
        if (double.TryParse(GainValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;
        return GainMin;
    }

    #region Event Handlers

    private void EscEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressControlEvents) return;
        SetEscEnabled?.Invoke(EscEnabledCheckBox.IsChecked == true);
        UpdateControlsFromSlice();
    }

    private void PhaseSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressControlEvents) return;
        ApplyPhase(PhaseSlider.Value);
    }

    private void PhaseValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (_suppressControlEvents) return;
        double current = GetPhaseShift?.Invoke() ?? ParsePhaseText();

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (double.TryParse(PhaseValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var pv))
                    ApplyPhase(pv);
                else
                    UpdatePhaseDisplay(GetPhaseShift?.Invoke() ?? 0);
                break;
            case Key.Up:
                e.Handled = true;
                ApplyPhase(current + 1);
                break;
            case Key.Down:
                e.Handled = true;
                ApplyPhase(current - 1);
                break;
            case Key.PageUp:
                e.Handled = true;
                ApplyPhase(current + 10);
                break;
            case Key.PageDown:
                e.Handled = true;
                ApplyPhase(current - 10);
                break;
        }
    }

    private void PhaseValueBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressControlEvents) return;
        if (double.TryParse(PhaseValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            ApplyPhase(v);
        else
            UpdatePhaseDisplay(GetPhaseShift?.Invoke() ?? 0);
    }

    private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressControlEvents) return;
        ApplyGain(SliderToGain((int)GainSlider.Value));
    }

    private void GainValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (_suppressControlEvents) return;
        double current = GetEscGain?.Invoke() ?? ParseGainText();

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (double.TryParse(GainValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gv))
                    ApplyGain(gv);
                else
                    UpdateGainDisplay(GetEscGain?.Invoke() ?? GainMin);
                break;
            case Key.Up:
                e.Handled = true;
                ApplyGain(current + GainStep);
                break;
            case Key.Down:
                e.Handled = true;
                ApplyGain(current - GainStep);
                break;
            case Key.PageUp:
                e.Handled = true;
                ApplyGain(current + GainStep * 5);
                break;
            case Key.PageDown:
                e.Handled = true;
                ApplyGain(current - GainStep * 5);
                break;
        }
    }

    private void GainValueBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressControlEvents) return;
        if (double.TryParse(GainValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            ApplyGain(v);
        else
            UpdateGainDisplay(GetEscGain?.Invoke() ?? GainMin);
    }

    private void Preset90Button_Click(object sender, RoutedEventArgs e) => ApplyPhase(90);
    private void Preset180Button_Click(object sender, RoutedEventArgs e) => ApplyPhase(180);

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    #endregion
}
