using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Controls;

// ORPHANED (QB Track L audit, 2026-08-07): the only consumer of this
// control is FiltersDspControl, which is itself orphaned (Sprint 8
// replacement never wired in — see its header). If FiltersDspControl gets
// wired in, this rides along; note it lacks minus-sign entry (negative
// values unreachable by typing — the ValueFieldControl fix is the model).
// Wire-in-or-remove is Noel's call — do not delete without that decision.

/// <summary>
/// WPF replacement for RadioBoxes.NumberBox — header + numeric TextBox with keyboard shortcuts.
/// Used for integer rig parameters (e.g., power level, filter width).
///
/// Key behavior:
/// - Up/Down arrows adjust value by Increment within LowValue..HighValue range
/// - Home/End jump to LowValue/HighValue
/// - Enter confirms direct text entry
/// - UpdateDisplay() polls rig for current int value via delegate
/// - User changes trigger UpdateRigFunction delegate
/// - directEntry flag prevents rig polling while user is typing
///
/// Sprint 8 Phase 8.2: Control structure. Radio wiring in Phase 8.4+.
/// </summary>
public partial class RadioNumberBox : UserControl
{
    #region Delegates (match RadioBoxes.NumberBox pattern)

    /// <summary>
    /// Called to get the current integer value from the rig.
    /// </summary>
    public Func<int>? UpdateDisplayFunction { get; set; }

    /// <summary>
    /// Called when user changes the value — sends to rig.
    /// </summary>
    public Action<int>? UpdateRigFunction { get; set; }

    #endregion

    #region Properties

    /// <summary>
    /// Header text shown above the TextBox.
    /// </summary>
    public string Header
    {
        get => HeaderLabel.Text;
        set
        {
            HeaderLabel.Text = value;
            AutomationProperties.SetName(TheTextBox, value);
        }
    }

    /// <summary>
    /// Low limit for the value range.
    /// </summary>
    public int LowValue { get; set; }

    /// <summary>
    /// High limit. Set to less than LowValue for unlimited.
    /// </summary>
    public int HighValue { get; set; }

    /// <summary>
    /// Step size for Up/Down arrow key adjustment.
    /// </summary>
    public int Increment { get; set; } = 1;

    /// <summary>
    /// Whether the TextBox is read-only (no user entry).
    /// </summary>
    public bool IsReadOnly { get; set; }

    #endregion

    private int _cachedValue;
    private bool _directEntry;

    /// <summary>
    /// Keyboard event for keys that should be forwarded to MainWindow.
    /// </summary>
    public event Action<KeyEventArgs>? BoxKeyDown;

    public RadioNumberBox()
    {
        InitializeComponent();
        // Set cached value outside the range so first value is always displayed
        _cachedValue = int.MinValue;
    }

    #region Public API (matches RadioBoxes.NumberBox)

    /// <summary>
    /// Poll the rig for the current value and update the display if changed.
    /// Skips update if user is in direct entry mode.
    /// Thread-safe via Dispatcher.
    /// </summary>
    public void UpdateDisplay(bool forceUpdate = false)
    {
        if (UpdateDisplayFunction == null)
            return;

        // Don't overwrite while user is typing
        if (_directEntry)
            return;

        int newVal = UpdateDisplayFunction();
        if (!forceUpdate && newVal == _cachedValue)
            return;

        _cachedValue = newVal;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => TheTextBox.Text = newVal.ToString());
        }
        else
        {
            TheTextBox.Text = newVal.ToString();
        }
    }

    /// <summary>
    /// Overload without parameter matches WinForms pattern.
    /// </summary>
    public void UpdateDisplay()
    {
        UpdateDisplay(false);
    }

    #endregion

    #region Event Handlers

    private void TheTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Forward function keys and modified keys to parent window
        if (e.Key >= Key.F1 && e.Key <= Key.F24)
        {
            BoxKeyDown?.Invoke(e);
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            BoxKeyDown?.Invoke(e);
            return;
        }

        if (IsReadOnly)
        {
            e.Handled = true;
            return;
        }

        if (!int.TryParse(TheTextBox.Text, out int value))
            return;

        bool hasLimit = HighValue > LowValue;

        switch (e.Key)
        {
            case Key.Up:
                value += Increment;
                if (hasLimit && value > HighValue) value = HighValue;
                UpdateBoxAndRig(value);
                e.Handled = true;
                break;

            case Key.Down:
                value -= Increment;
                if (value < LowValue) value = LowValue;
                UpdateBoxAndRig(value);
                e.Handled = true;
                break;

            case Key.Home:
                UpdateBoxAndRig(LowValue);
                e.Handled = true;
                break;

            case Key.End:
                if (hasLimit)
                {
                    UpdateBoxAndRig(HighValue);
                    e.Handled = true;
                }
                break;

            case Key.Return:
                UpdateBoxAndRig(value);
                _directEntry = false;
                e.Handled = true;
                break;

            default:
                _directEntry = true;
                break;
        }
    }

    private void UpdateBoxAndRig(int value)
    {
        // Clamp typed entries too — arrows already respect the range, but a
        // value typed into the TextBox and confirmed with Enter used to go to
        // the rig unclamped, overshooting the field's real ceiling or floor
        // (QB Track A, 2026-08-07). Minus works here natively: this is a real
        // TextBox, so signed entry like "-8" was never blocked.
        if (value < LowValue) value = LowValue;
        bool hasLimit = HighValue > LowValue;
        if (hasLimit && value > HighValue) value = HighValue;

        TheTextBox.Text = value.ToString();
        TheTextBox.SelectAll();
        UpdateRigFunction?.Invoke(value);
    }

    private void TheTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        TheTextBox.SelectAll();
    }

    private void TheTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _directEntry = false;
    }

    #endregion
}
