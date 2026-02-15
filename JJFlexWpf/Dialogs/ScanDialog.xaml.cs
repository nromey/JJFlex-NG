using System;
using System.Windows;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// WPF replacement for scan.vb.
/// Memory scan configuration dialog: start/end frequency, increment, speed.
/// Supports saved scan presets (load, save, replace, remove).
///
/// All rig and scan operations use delegates — no direct FlexBase or Form1 references.
///
/// Sprint 9 Track B.
/// </summary>
public partial class ScanDialog : JJFlexDialog
{
    private const string SpeedError = "The speed must be 1 through 600 tenths of a second.";

    #region Delegates

    /// <summary>
    /// Starts a linear scan with the given parameters.
    /// Parameters: startFreq, endFreq, incrementHz, speedTenths.
    /// </summary>
    public Action<string, string, string, string>? StartLinearScan { get; set; }

    /// <summary>
    /// Formats a raw frequency string for display.
    /// </summary>
    public Func<string, string>? FormatFrequency { get; set; }

    /// <summary>
    /// Formats a display frequency for the radio (raw format).
    /// </summary>
    public Func<string, string?>? FormatFrequencyForRadio { get; set; }

    /// <summary>
    /// Shows a saved scan picker. Returns the selected scan data, or null.
    /// </summary>
    public Func<ScanPreset?>? PickSavedScan { get; set; }

    /// <summary>Saves a new scan preset. Returns true on success.</summary>
    public Func<ScanPreset, bool>? SaveScan { get; set; }

    /// <summary>Replaces an existing scan preset. Returns true on success.</summary>
    public Func<ScanPreset, bool>? ReplaceScan { get; set; }

    /// <summary>Removes a scan preset by name. Returns true on success.</summary>
    public Func<string, bool>? RemoveScan { get; set; }

    /// <summary>Asks user for a name (input box). Returns the name or null.</summary>
    public Func<string?>? GetScanName { get; set; }

    #endregion

    #region Preset Data

    /// <summary>
    /// Scan preset data for saved scans.
    /// </summary>
    public class ScanPreset
    {
        public string Name { get; set; } = "";
        public string StartFrequency { get; set; } = "";
        public string EndFrequency { get; set; } = "";
        public string Increment { get; set; } = "";
        public string Speed { get; set; } = "";
    }

    /// <summary>Set before showing the dialog to load a preset.</summary>
    public ScanPreset? Preset { get; set; }

    #endregion

    private ScanPreset? _activePreset;

    public ScanDialog()
    {
        InitializeComponent();
        Loaded += ScanDialog_Loaded;
    }

    private void ScanDialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (Preset != null)
        {
            LoadPreset(Preset);
            Preset = null;
        }
        StartFreqBox.Focus();
    }

    private void LoadPreset(ScanPreset preset)
    {
        _activePreset = preset;
        StartFreqBox.Text = FormatFrequency?.Invoke(preset.StartFrequency) ?? preset.StartFrequency;
        EndFreqBox.Text = FormatFrequency?.Invoke(preset.EndFrequency) ?? preset.EndFrequency;

        // Convert increment from Hz to kHz for display
        if (preset.Increment.Length >= 4)
        {
            string kHz = preset.Increment[..^3] + "." + preset.Increment[^3..^2];
            IncrementBox.Text = kHz;
        }
        else
        {
            IncrementBox.Text = preset.Increment;
        }

        SpeedBox.Text = preset.Speed;
        ReplaceButton.IsEnabled = true;
        RemoveButton.IsEnabled = true;
    }

    private bool ValidateScan(out string? low, out string? high, out string? increment)
    {
        low = FormatFrequencyForRadio?.Invoke(StartFreqBox.Text);
        high = FormatFrequencyForRadio?.Invoke(EndFreqBox.Text);
        increment = GetIncrementHz(IncrementBox.Text);

        if (low == null)
        {
            MessageBox.Show("Invalid starting frequency.", "Scan", MessageBoxButton.OK, MessageBoxImage.Warning);
            StartFreqBox.Focus();
            return false;
        }
        if (high == null)
        {
            MessageBox.Show("Invalid ending frequency.", "Scan", MessageBoxButton.OK, MessageBoxImage.Warning);
            EndFreqBox.Focus();
            return false;
        }
        if (string.Compare(high, low, StringComparison.Ordinal) <= 0)
        {
            MessageBox.Show("The ending frequency must exceed the starting frequency.", "Scan",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            StartFreqBox.Focus();
            return false;
        }
        if (string.IsNullOrEmpty(increment))
        {
            MessageBox.Show("The increment must be in kHz, 0.01 through 999.99", "Scan",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            IncrementBox.Focus();
            return false;
        }

        if (!int.TryParse(SpeedBox.Text, out int speed) || speed < 1 || speed > 600)
        {
            MessageBox.Show(SpeedError, "Scan", MessageBoxButton.OK, MessageBoxImage.Warning);
            SpeedBox.Focus();
            return false;
        }

        return true;
    }

    private static string? GetIncrementHz(string kHzText)
    {
        if (!float.TryParse(kHzText, out float kHz)) return null;
        int hz = (int)(kHz * 1000);
        if (hz < 10 || hz > 999990 || hz % 10 != 0) return null;
        return hz.ToString();
    }

    #region Button Handlers

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateScan(out var low, out var high, out var increment)) return;

        StartLinearScan?.Invoke(low!, high!, increment!, SpeedBox.Text);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        StartFreqBox.Text = "";
        EndFreqBox.Text = "";
        IncrementBox.Text = "";
        SpeedBox.Text = "";
        _activePreset = null;
        ReplaceButton.IsEnabled = false;
        RemoveButton.IsEnabled = false;
        StartFreqBox.Focus();
    }

    private void UseSavedButton_Click(object sender, RoutedEventArgs e)
    {
        var preset = PickSavedScan?.Invoke();
        if (preset != null)
            LoadPreset(preset);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateScan(out var low, out var high, out var increment)) return;

        string? name = GetScanName?.Invoke();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("You must provide a name.", "Save Scan",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var preset = new ScanPreset
        {
            Name = name,
            StartFrequency = low!,
            EndFrequency = high!,
            Increment = increment!,
            Speed = SpeedBox.Text
        };

        if (SaveScan?.Invoke(preset) != true)
        {
            MessageBox.Show($"{name} already exists.", "Save Scan",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activePreset == null) return;
        if (!ValidateScan(out var low, out var high, out var increment)) return;

        var updated = new ScanPreset
        {
            Name = _activePreset.Name,
            StartFrequency = low!,
            EndFrequency = high!,
            Increment = increment!,
            Speed = SpeedBox.Text
        };

        ReplaceScan?.Invoke(updated);
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activePreset == null) return;

        if (RemoveScan?.Invoke(_activePreset.Name) == true)
        {
            DialogResult = true;
            Close();
        }
    }

    #endregion
}
