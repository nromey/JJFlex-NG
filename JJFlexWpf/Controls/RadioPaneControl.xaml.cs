using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Controls;

/// <summary>
/// WPF replacement for RadioPane.vb — minimal radio status pane for Logging Mode.
/// Shows frequency, mode, band, and tune step from the connected radio.
///
/// Key behavior:
/// - Tab cycles through focusable status items (Freq, Mode, Band, TuneStep)
/// - Up/Down arrows tune the radio by current step size
/// - Shift+Up/Down tunes by 10x the step size
/// - Left/Right cycles through available tune step sizes
/// - Ctrl+F opens manual frequency entry (via delegate)
///
/// This is a "peek at the radio" during logging — exit Logging Mode for full control.
///
/// Sprint 8 Phase 8.8.
/// </summary>
public partial class RadioPaneControl : UserControl
{
    #region Tune Step Constants

    /// <summary>
    /// Available tune step sizes in Hz.
    /// Mirrors FlexLib Slice.TuneStepList: 1, 10, 50, 100, 500, 1000, 2000, 3000 Hz.
    /// </summary>
    private static readonly int[] TuneStepList = { 1, 10, 50, 100, 500, 1000, 2000, 3000 };

    /// <summary>
    /// Current index into TuneStepList. Default = 1 (10 Hz).
    /// </summary>
    private int _currentStepIndex = 1;

    #endregion

    #region Delegates (rig wiring)

    /// <summary>
    /// Get the current RX frequency from the rig (in Hz, ulong).
    /// </summary>
    public Func<ulong>? GetRXFrequency { get; set; }

    /// <summary>
    /// Set the RX frequency on the rig (in Hz, ulong).
    /// </summary>
    public Action<ulong>? SetRXFrequency { get; set; }

    /// <summary>
    /// Get the TX frequency for band lookup (in Hz, ulong).
    /// </summary>
    public Func<ulong>? GetTXFrequency { get; set; }

    /// <summary>
    /// Get the current mode string from the rig (e.g., "CW", "USB").
    /// </summary>
    public Func<string?>? GetMode { get; set; }

    /// <summary>
    /// Get the current band name for a given frequency.
    /// </summary>
    public Func<ulong, string?>? GetBandName { get; set; }

    /// <summary>
    /// Format a frequency (ulong Hz) to a display string.
    /// </summary>
    public Func<ulong, string>? FormatFrequency { get; set; }

    /// <summary>
    /// Check if the radio is powered on and connected.
    /// </summary>
    public Func<bool>? IsRadioPowered { get; set; }

    /// <summary>
    /// Set the frequency from manual entry (long, Hz).
    /// </summary>
    public Action<long>? SetFrequencyManual { get; set; }

    /// <summary>
    /// Show manual frequency entry dialog.
    /// Returns the entered frequency in Hz, or null if cancelled.
    /// </summary>
    public Func<long?>? ShowFrequencyInputDialog { get; set; }

    /// <summary>
    /// Speak text via screen reader.
    /// </summary>
    public Action<string>? SpeakAction { get; set; }

    #endregion

    public RadioPaneControl()
    {
        InitializeComponent();
    }

    #region Public API

    /// <summary>
    /// Give keyboard focus to the first control in the RadioPane.
    /// Called by F6 pane-switching logic.
    /// </summary>
    public void FocusFirst()
    {
        FreqBox.Focus();
        Keyboard.Focus(FreqBox);
    }

    /// <summary>
    /// Refresh the display from the current radio state.
    /// Call this when entering Logging Mode, after tuning, or from poll timer.
    /// </summary>
    public void UpdateFromRadio()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateFromRadio);
            return;
        }

        if (IsRadioPowered == null || !IsRadioPowered())
        {
            FreqBox.Text = "No radio";
            AutomationProperties.SetName(FreqBox, "Radio pane, Frequency: no radio connected");
            ModeBox.Text = "---";
            AutomationProperties.SetName(ModeBox, "Mode: none");
            BandBox.Text = "---";
            AutomationProperties.SetName(BandBox, "Band: none");
            TuneStepBox.Text = "---";
            AutomationProperties.SetName(TuneStepBox, "Tune step: none");
            StatusLabel.Text = "Connect a radio first";
            return;
        }

        // Frequency
        if (GetRXFrequency != null)
        {
            var rxFreq = GetRXFrequency();
            var freqText = FormatFrequency != null
                ? FormatFrequency(rxFreq)
                : (rxFreq / 1_000_000.0).ToString("F6");
            FreqBox.Text = freqText + " MHz";
            AutomationProperties.SetName(FreqBox, "Radio pane, Frequency " + freqText + " megahertz");
        }

        // Mode
        var modeText = GetMode?.Invoke()?.ToUpperInvariant() ?? "";
        ModeBox.Text = !string.IsNullOrEmpty(modeText) ? modeText : "---";
        AutomationProperties.SetName(ModeBox, "Mode " + (!string.IsNullOrEmpty(modeText) ? modeText : "none"));

        // Band
        var bandText = "---";
        if (GetTXFrequency != null && GetBandName != null)
        {
            var txFreq = GetTXFrequency();
            bandText = GetBandName(txFreq) ?? "---";
        }
        BandBox.Text = bandText;
        AutomationProperties.SetName(BandBox, "Band " + (bandText != "---" ? bandText : "none"));

        // Tune step
        var stepText = FormatStepSize(TuneStepList[_currentStepIndex]);
        TuneStepBox.Text = "Step: " + stepText;
        AutomationProperties.SetName(TuneStepBox, "Tune step " + stepText);

        StatusLabel.Text = "Up/Down tune\nLeft/Right step size\nShift+Up/Down coarse";
    }

    /// <summary>
    /// Clear delegate references on disconnect.
    /// </summary>
    public void Cleanup()
    {
        GetRXFrequency = null;
        SetRXFrequency = null;
        GetTXFrequency = null;
        GetMode = null;
        GetBandName = null;
        FormatFrequency = null;
        IsRadioPowered = null;
        SetFrequencyManual = null;
        ShowFrequencyInputDialog = null;
    }

    #endregion

    #region Keyboard Handlers

    /// <summary>
    /// Handle arrow keys for tuning and step-size changes.
    /// Matches RadioPane.vb ProcessCmdKey.
    /// </summary>
    private void RadioPane_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsRadioPowered == null || !IsRadioPowered())
            return;

        bool handled = true;

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (e.Key)
            {
                case Key.Up:
                    TuneBySteps(1);
                    break;
                case Key.Down:
                    TuneBySteps(-1);
                    break;
                case Key.Right:
                    ChangeTuneStep(1);
                    break;
                case Key.Left:
                    ChangeTuneStep(-1);
                    break;
                default:
                    handled = false;
                    break;
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            switch (e.Key)
            {
                case Key.Up:
                    TuneBySteps(10);
                    break;
                case Key.Down:
                    TuneBySteps(-10);
                    break;
                default:
                    handled = false;
                    break;
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            EnterManualFrequency();
        }
        else
        {
            handled = false;
        }

        if (handled)
        {
            UpdateFromRadio();
            e.Handled = true;
        }
    }

    #endregion

    #region Tuning Logic

    /// <summary>
    /// Tune the radio by the given number of steps.
    /// Matches RadioPane.vb TuneBySteps().
    /// </summary>
    private void TuneBySteps(int steps)
    {
        try
        {
            if (GetRXFrequency == null || SetRXFrequency == null)
                return;

            int stepHz = TuneStepList[_currentStepIndex];
            ulong currentFreq = GetRXFrequency();
            long delta = (long)steps * stepHz;
            long newFreq = (long)currentFreq + delta;

            if (newFreq > 0)
            {
                SetRXFrequency((ulong)newFreq);
            }
        }
        catch (Exception)
        {
            // Swallow — same as RadioPane.vb
        }
    }

    /// <summary>
    /// Show the FreqInput dialog to enter a frequency manually.
    /// Matches RadioPane.vb EnterManualFrequency().
    /// </summary>
    private void EnterManualFrequency()
    {
        try
        {
            if (ShowFrequencyInputDialog == null)
                return;

            var freqHz = ShowFrequencyInputDialog();
            if (freqHz.HasValue && SetFrequencyManual != null)
            {
                SetFrequencyManual(freqHz.Value);
                SpeakAction?.Invoke("Frequency set");
            }
        }
        catch (Exception)
        {
            // Swallow
        }
    }

    /// <summary>
    /// Cycle through the TuneStepList.
    /// direction = +1 (larger step) or -1 (smaller step).
    /// Announces new step size via screen reader.
    /// </summary>
    private void ChangeTuneStep(int direction)
    {
        int newIdx = _currentStepIndex + direction;
        if (newIdx < 0) newIdx = 0;
        if (newIdx >= TuneStepList.Length) newIdx = TuneStepList.Length - 1;
        _currentStepIndex = newIdx;

        var stepText = FormatStepSize(TuneStepList[_currentStepIndex]);
        TuneStepBox.Text = "Step: " + stepText;
        AutomationProperties.SetName(TuneStepBox, "Tune step " + stepText);
        SpeakAction?.Invoke("Tune step " + stepText);
    }

    /// <summary>
    /// Format a step size in Hz for display. E.g. 10 → "10 Hz", 1000 → "1 kHz".
    /// </summary>
    private static string FormatStepSize(int hz)
    {
        return hz >= 1000
            ? (hz / 1000) + " kHz"
            : hz + " Hz";
    }

    #endregion
}
