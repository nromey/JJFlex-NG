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
    public Func<bool, bool>? SetTXReqRCAEnabled { get; set; }
    public Func<bool>? GetTXReqRCAPolarity { get; set; }
    public Func<bool, bool>? SetTXReqRCAPolarity { get; set; }
    public Func<bool>? GetTXReqACCEnabled { get; set; }
    public Func<bool, bool>? SetTXReqACCEnabled { get; set; }
    public Func<bool>? GetTXReqACCPolarity { get; set; }
    public Func<bool, bool>? SetTXReqACCPolarity { get; set; }

    // TX1
    public Func<bool>? GetTX1Enabled { get; set; }
    public Func<bool, bool>? SetTX1Enabled { get; set; }
    public Func<int>? GetTX1Delay { get; set; }
    public Func<int, bool>? SetTX1Delay { get; set; }

    // TX2
    public Func<bool>? GetTX2Enabled { get; set; }
    public Func<bool, bool>? SetTX2Enabled { get; set; }
    public Func<int>? GetTX2Delay { get; set; }
    public Func<int, bool>? SetTX2Delay { get; set; }

    // TX3
    public Func<bool>? GetTX3Enabled { get; set; }
    public Func<bool, bool>? SetTX3Enabled { get; set; }
    public Func<int>? GetTX3Delay { get; set; }
    public Func<int, bool>? SetTX3Delay { get; set; }

    // TX ACC
    public Func<bool>? GetTXACCEnabled { get; set; }
    public Func<bool, bool>? SetTXACCEnabled { get; set; }
    public Func<int>? GetTXACCDelay { get; set; }
    public Func<int, bool>? SetTXACCDelay { get; set; }

    // ALC and Remote On
    public Func<bool>? GetHWAlcEnabled { get; set; }
    public Func<bool, bool>? SetHWAlcEnabled { get; set; }
    public Func<bool>? GetRemoteOnEnabled { get; set; }
    public Func<bool, bool>? SetRemoteOnEnabled { get; set; }

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

    /// <summary>
    /// Apply one of this dialog's boolean relay/interlock toggles and answer
    /// back with the toggle tone (#128 sweep audit, 2026-08-21). All eight
    /// checkboxes in this dialog are immediate radio operations that were
    /// completely silent — no tone, no speech — while every DSP toggle on the
    /// Home panel answered back, so a flip here read as the command failing.
    /// One helper so a checkbox added later cannot be silent by omission.
    /// The tone sounds the REQUEST the radio was handed: the setter answers
    /// false when it was not (no radio, or the change-nothing hold refused it
    /// and said so), and then the box goes back to the radio's value and no
    /// tone plays, because a success tone over a refusal is a contradiction
    /// (#477).
    /// </summary>
    private void ApplyToggle(CheckBox box, Func<bool>? getter, Func<bool, bool>? setter)
    {
        if (_loading) return;
        bool on = box.IsChecked == true;
        if (setter?.Invoke(on) == true)
        {
            EarconPlayer.ToggleTone(on);
            return;
        }
        PutBack(() => box.IsChecked = getter?.Invoke() ?? false);
    }

    /// <summary>Show the radio's value again after a refused write, without
    /// the change handlers writing it back.</summary>
    private void PutBack(Action restore)
    {
        bool was = _loading;
        _loading = true;
        try { restore(); }
        finally { _loading = was; }
    }

    private void ApplyPolarity(ComboBox combo, Func<bool>? getter, Func<bool, bool>? setter)
    {
        if (_loading) return;
        if (setter?.Invoke(combo.SelectedIndex == 1) == true) return;
        PutBack(() => combo.SelectedIndex = (getter?.Invoke() ?? false) ? 1 : 0);
    }

    private void TXReqRCACheck_Changed(object sender, RoutedEventArgs e)
        => ApplyToggle(TXReqRCACheck, GetTXReqRCAEnabled, SetTXReqRCAEnabled);

    private void TXReqRCAPolarityCombo_Changed(object sender, SelectionChangedEventArgs e)
        => ApplyPolarity(TXReqRCAPolarityCombo, GetTXReqRCAPolarity, SetTXReqRCAPolarity);

    private void TXReqACCCheck_Changed(object sender, RoutedEventArgs e)
        => ApplyToggle(TXReqACCCheck, GetTXReqACCEnabled, SetTXReqACCEnabled);

    private void TXReqACCPolarityCombo_Changed(object sender, SelectionChangedEventArgs e)
        => ApplyPolarity(TXReqACCPolarityCombo, GetTXReqACCPolarity, SetTXReqACCPolarity);

    #endregion

    #region TX Output Handlers

    private void TX1RCACheck_Changed(object sender, RoutedEventArgs e)
        => ApplyToggle(TX1RCACheck, GetTX1Enabled, SetTX1Enabled);

    private void TX2RCACheck_Changed(object sender, RoutedEventArgs e)
        => ApplyToggle(TX2RCACheck, GetTX2Enabled, SetTX2Enabled);

    private void TX3RCACheck_Changed(object sender, RoutedEventArgs e)
        => ApplyToggle(TX3RCACheck, GetTX3Enabled, SetTX3Enabled);

    private void TXACCCheck_Changed(object sender, RoutedEventArgs e)
        => ApplyToggle(TXACCCheck, GetTXACCEnabled, SetTXACCEnabled);

    #endregion

    #region Other Controls Handlers

    private void ALCCheck_Changed(object sender, RoutedEventArgs e)
        => ApplyToggle(ALCCheck, GetHWAlcEnabled, SetHWAlcEnabled);

    private void RemoteOnCheck_Changed(object sender, RoutedEventArgs e)
        => ApplyToggle(RemoteOnCheck, GetRemoteOnEnabled, SetRemoteOnEnabled);

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
        ApplyDelayFromBox(TX1DelayBox);

    private void TX2DelayBox_LostFocus(object sender, RoutedEventArgs e) =>
        ApplyDelayFromBox(TX2DelayBox);

    private void TX3DelayBox_LostFocus(object sender, RoutedEventArgs e) =>
        ApplyDelayFromBox(TX3DelayBox);

    private void TXACCDelayBox_LostFocus(object sender, RoutedEventArgs e) =>
        ApplyDelayFromBox(TXACCDelayBox);

    private void ApplyDelayFromBox(TextBox box)
    {
        var (get, set) = DelayAccessors(box);
        string? shown = DelayAfterFocusLoss(box.Text, get, set);
        if (shown != null) box.Text = shown;
    }

    /// <summary>
    /// Write a delay; refused (no radio, or the change-nothing hold), the box
    /// goes back to the radio's value (#477).
    /// </summary>
    private void ApplyDelay(TextBox box, int value)
    {
        var (get, set) = DelayAccessors(box);
        box.Text = DelayAfterWrite(value, get, set);
    }

    private (Func<int>? Get, Func<int, bool>? Set) DelayAccessors(TextBox box) =>
        box == TX1DelayBox ? (GetTX1Delay, SetTX1Delay)
        : box == TX2DelayBox ? (GetTX2Delay, SetTX2Delay)
        : box == TX3DelayBox ? (GetTX3Delay, SetTX3Delay)
        : box == TXACCDelayBox ? (GetTXACCDelay, SetTXACCDelay)
        : (null, null);

    /// <summary>
    /// What a delay box shows after the operator leaves it: the typed number,
    /// clamped, written if it differs from the radio's; null when the text is
    /// not a number and the box is left alone.
    /// </summary>
    internal static string? DelayAfterFocusLoss(string text, Func<int>? get, Func<int, bool>? set)
    {
        if (!int.TryParse(text, out int value)) return null;
        return DelayAfterWrite(Math.Clamp(value, DelayMin, DelayMax), get, set);
    }

    /// <summary>
    /// What a delay box shows after a write of <paramref name="value"/>.
    /// <b>A value the radio already has is not sent</b> (#477 repair one,
    /// Codex B2): Tab passing an untouched delay, or leaving one an arrow
    /// press was refused on and put back, wrote the radio's own number again,
    /// and under the change-nothing hold every such focus loss spoke a
    /// refusal of a change nobody asked for. A real edit is still written,
    /// and refused, the box shows the radio's value again.
    /// </summary>
    internal static string DelayAfterWrite(int value, Func<int>? get, Func<int, bool>? set)
    {
        if (get != null && get() == value) return value.ToString();
        if (set?.Invoke(value) == true) return value.ToString();
        return (get?.Invoke() ?? 0).ToString();
    }

    #endregion
}
