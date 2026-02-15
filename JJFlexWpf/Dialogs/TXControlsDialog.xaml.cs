using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// WPF replacement for Radios\TXControls.cs.
/// Transmit control settings: TX request (RCA/ACC), TX output enables + delays,
/// hardware ALC, and remote-on.
///
/// All rig communication uses delegates — no direct FlexLib references.
/// Changes are sent to the rig immediately as the user adjusts controls.
///
/// Sprint 9 Track B.
/// </summary>
public partial class TXControlsDialog : JJFlexDialog
{
    private const int DelayMin = 0;
    private const int DelayMax = 500;
    private const int DelayIncrement = 1;

    private bool _loading;

    #region Rig Delegates

    // TX Request
    public Func<bool>? GetTXReqRCAEnabled { get; set; }
    public Action<bool>? SetTXReqRCAEnabled { get; set; }
    public Func<bool>? GetTXReqRCAPolarity { get; set; }
    public Action<bool>? SetTXReqRCAPolarity { get; set; }
    public Func<bool>? GetTXReqACCEnabled { get; set; }
    public Action<bool>? SetTXReqACCEnabled { get; set; }
    public Func<bool>? GetTXReqACCPolarity { get; set; }
    public Action<bool>? SetTXReqACCPolarity { get; set; }

    // TX1
    public Func<bool>? GetTX1Enabled { get; set; }
    public Action<bool>? SetTX1Enabled { get; set; }
    public Func<int>? GetTX1Delay { get; set; }
    public Action<int>? SetTX1Delay { get; set; }

    // TX2
    public Func<bool>? GetTX2Enabled { get; set; }
    public Action<bool>? SetTX2Enabled { get; set; }
    public Func<int>? GetTX2Delay { get; set; }
    public Action<int>? SetTX2Delay { get; set; }

    // TX3
    public Func<bool>? GetTX3Enabled { get; set; }
    public Action<bool>? SetTX3Enabled { get; set; }
    public Func<int>? GetTX3Delay { get; set; }
    public Action<int>? SetTX3Delay { get; set; }

    // TX ACC
    public Func<bool>? GetTXACCEnabled { get; set; }
    public Action<bool>? SetTXACCEnabled { get; set; }
    public Func<int>? GetTXACCDelay { get; set; }
    public Action<int>? SetTXACCDelay { get; set; }

    // ALC and Remote On
    public Func<bool>? GetHWAlcEnabled { get; set; }
    public Action<bool>? SetHWAlcEnabled { get; set; }
    public Func<bool>? GetRemoteOnEnabled { get; set; }
    public Action<bool>? SetRemoteOnEnabled { get; set; }

    #endregion

    public TXControlsDialog()
    {
        InitializeComponent();

        // Polarity combo items
        TXReqRCAPolarityCombo.Items.Add("Low");
        TXReqRCAPolarityCombo.Items.Add("High");
        TXReqACCPolarityCombo.Items.Add("Low");
        TXReqACCPolarityCombo.Items.Add("High");

        Loaded += TXControlsDialog_Loaded;
    }

    private void TXControlsDialog_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;

        // Load current values from rig
        TXReqRCACheck.IsChecked = GetTXReqRCAEnabled?.Invoke() ?? false;
        TXReqRCAPolarityCombo.SelectedIndex = (GetTXReqRCAPolarity?.Invoke() ?? false) ? 1 : 0;
        TXReqACCCheck.IsChecked = GetTXReqACCEnabled?.Invoke() ?? false;
        TXReqACCPolarityCombo.SelectedIndex = (GetTXReqACCPolarity?.Invoke() ?? false) ? 1 : 0;

        TX1RCACheck.IsChecked = GetTX1Enabled?.Invoke() ?? false;
        TX1DelayBox.Text = (GetTX1Delay?.Invoke() ?? 0).ToString();
        TX2RCACheck.IsChecked = GetTX2Enabled?.Invoke() ?? false;
        TX2DelayBox.Text = (GetTX2Delay?.Invoke() ?? 0).ToString();
        TX3RCACheck.IsChecked = GetTX3Enabled?.Invoke() ?? false;
        TX3DelayBox.Text = (GetTX3Delay?.Invoke() ?? 0).ToString();
        TXACCCheck.IsChecked = GetTXACCEnabled?.Invoke() ?? false;
        TXACCDelayBox.Text = (GetTXACCDelay?.Invoke() ?? 0).ToString();

        ALCCheck.IsChecked = GetHWAlcEnabled?.Invoke() ?? false;
        RemoteOnCheck.IsChecked = GetRemoteOnEnabled?.Invoke() ?? false;

        _loading = false;
    }

    #region TX Request Handlers

    private void TXReqRCACheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SetTXReqRCAEnabled?.Invoke(TXReqRCACheck.IsChecked == true);
    }

    private void TXReqRCAPolarityCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SetTXReqRCAPolarity?.Invoke(TXReqRCAPolarityCombo.SelectedIndex == 1);
    }

    private void TXReqACCCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SetTXReqACCEnabled?.Invoke(TXReqACCCheck.IsChecked == true);
    }

    private void TXReqACCPolarityCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SetTXReqACCPolarity?.Invoke(TXReqACCPolarityCombo.SelectedIndex == 1);
    }

    #endregion

    #region TX Output Handlers

    private void TX1RCACheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SetTX1Enabled?.Invoke(TX1RCACheck.IsChecked == true);
    }

    private void TX2RCACheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SetTX2Enabled?.Invoke(TX2RCACheck.IsChecked == true);
    }

    private void TX3RCACheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SetTX3Enabled?.Invoke(TX3RCACheck.IsChecked == true);
    }

    private void TXACCCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SetTXACCEnabled?.Invoke(TXACCCheck.IsChecked == true);
    }

    #endregion

    #region Other Controls Handlers

    private void ALCCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SetHWAlcEnabled?.Invoke(ALCCheck.IsChecked == true);
    }

    private void RemoteOnCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SetRemoteOnEnabled?.Invoke(RemoteOnCheck.IsChecked == true);
    }

    #endregion

    #region Delay Box Handlers

    private void DelayBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (!int.TryParse(box.Text, out int value)) return;

        switch (e.Key)
        {
            case Key.Up:
                value = Math.Min(value + DelayIncrement, DelayMax);
                box.Text = value.ToString();
                ApplyDelay(box, value);
                e.Handled = true;
                break;
            case Key.Down:
                value = Math.Max(value - DelayIncrement, DelayMin);
                box.Text = value.ToString();
                ApplyDelay(box, value);
                e.Handled = true;
                break;
        }
    }

    private void TX1DelayBox_LostFocus(object sender, RoutedEventArgs e) =>
        ApplyDelayFromBox(TX1DelayBox, SetTX1Delay);

    private void TX2DelayBox_LostFocus(object sender, RoutedEventArgs e) =>
        ApplyDelayFromBox(TX2DelayBox, SetTX2Delay);

    private void TX3DelayBox_LostFocus(object sender, RoutedEventArgs e) =>
        ApplyDelayFromBox(TX3DelayBox, SetTX3Delay);

    private void TXACCDelayBox_LostFocus(object sender, RoutedEventArgs e) =>
        ApplyDelayFromBox(TXACCDelayBox, SetTXACCDelay);

    private void ApplyDelayFromBox(TextBox box, Action<int>? setter)
    {
        if (int.TryParse(box.Text, out int value))
        {
            value = Math.Clamp(value, DelayMin, DelayMax);
            box.Text = value.ToString();
            setter?.Invoke(value);
        }
    }

    private void ApplyDelay(TextBox box, int value)
    {
        if (box == TX1DelayBox) SetTX1Delay?.Invoke(value);
        else if (box == TX2DelayBox) SetTX2Delay?.Invoke(value);
        else if (box == TX3DelayBox) SetTX3Delay?.Invoke(value);
        else if (box == TXACCDelayBox) SetTXACCDelay?.Invoke(value);
    }

    #endregion
}
