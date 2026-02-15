using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// WPF replacement for FlexEq (Radios/FlexEq.cs).
/// 8-band equalizer: 63, 125, 250, 500, 1000, 2000, 4000, 8000 Hz.
/// Each band ranges from -10 to +10 dB, increment 1.
/// Supports restore (to values at open time) and clear (all to 0).
///
/// All EQ operations use delegates — no direct FlexBase or Equalizer references.
///
/// Sprint 9 Track B.
/// </summary>
public partial class EqualizerDialog : JJFlexDialog
{
    private const int BandCount = 8;
    private const int LowValue = -10;
    private const int HighValue = 10;

    #region Delegates

    /// <summary>
    /// Gets the title suffix (RX or TX).
    /// </summary>
    public Func<string>? GetEqTitle { get; set; }

    /// <summary>
    /// Gets the current level for a band index (0-7).
    /// Band order: 63, 125, 250, 500, 1000, 2000, 4000, 8000 Hz.
    /// </summary>
    public Func<int, int>? GetBandLevel { get; set; }

    /// <summary>
    /// Sets the level for a band index (0-7).
    /// </summary>
    public Action<int, int>? SetBandLevel { get; set; }

    #endregion

    private TextBox[] _bandBoxes = null!;
    private int[] _originals = null!;

    public EqualizerDialog()
    {
        InitializeComponent();
        Loaded += EqualizerDialog_Loaded;
    }

    private void EqualizerDialog_Loaded(object sender, RoutedEventArgs e)
    {
        _bandBoxes = new[]
        {
            Level63Box, Level125Box, Level250Box, Level500Box,
            Level1000Box, Level2000Box, Level4000Box, Level8000Box
        };

        string title = GetEqTitle?.Invoke() ?? "Equalizer";
        Title = title;

        _originals = new int[BandCount];
        for (int i = 0; i < BandCount; i++)
        {
            _originals[i] = GetBandLevel?.Invoke(i) ?? 0;
        }

        UpdateAllBoxes();
        Level63Box.Focus();
    }

    private void UpdateAllBoxes()
    {
        for (int i = 0; i < BandCount; i++)
        {
            int level = GetBandLevel?.Invoke(i) ?? 0;
            _bandBoxes[i].Text = level.ToString();
        }
    }

    private int GetBandIndex(TextBox box)
    {
        if (box.Tag is string s && int.TryParse(s, out int idx))
            return idx;
        return Array.IndexOf(_bandBoxes, box);
    }

    private void ApplyBandValue(TextBox box)
    {
        int idx = GetBandIndex(box);
        if (idx < 0 || idx >= BandCount) return;

        if (int.TryParse(box.Text, out int val))
        {
            val = Math.Max(LowValue, Math.Min(HighValue, val));
            SetBandLevel?.Invoke(idx, val);
        }
        int current = GetBandLevel?.Invoke(idx) ?? 0;
        box.Text = current.ToString();
    }

    private void AdjustBand(TextBox box, int delta)
    {
        int idx = GetBandIndex(box);
        if (idx < 0 || idx >= BandCount) return;

        int current = GetBandLevel?.Invoke(idx) ?? 0;
        int newVal = Math.Max(LowValue, Math.Min(HighValue, current + delta));
        SetBandLevel?.Invoke(idx, newVal);
        box.Text = newVal.ToString();
    }

    #region Event Handlers

    private void LevelBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ApplyBandValue(box);
                break;
            case Key.Up:
                e.Handled = true;
                AdjustBand(box, 1);
                break;
            case Key.Down:
                e.Handled = true;
                AdjustBand(box, -1);
                break;
        }
    }

    private void LevelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            ApplyBandValue(box);
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < BandCount; i++)
        {
            SetBandLevel?.Invoke(i, _originals[i]);
        }
        UpdateAllBoxes();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < BandCount; i++)
        {
            SetBandLevel?.Invoke(i, 0);
        }
        UpdateAllBoxes();
    }

    private void FinishedButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // Restore original values on cancel
        for (int i = 0; i < BandCount; i++)
        {
            SetBandLevel?.Invoke(i, _originals[i]);
        }
        DialogResult = false;
        Close();
    }

    #endregion
}
