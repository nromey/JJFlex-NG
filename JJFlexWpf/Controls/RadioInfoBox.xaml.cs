using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Controls;

/// <summary>
/// WPF replacement for RadioBoxes.InfoBox — header + text display with rig-aware updates.
/// Used for various read-only or editable rig information fields.
///
/// Key behavior:
/// - UpdateDisplay() polls rig for current string value via delegate, updates TextBox if changed
/// - UpdateRigFunction called when user edits text (non-readonly mode)
/// - Thread-safe via Dispatcher
///
/// Sprint 8 Phase 8.2: Control structure. Radio wiring in Phase 8.4+.
/// </summary>
public partial class RadioInfoBox : UserControl
{
    #region Delegates (match RadioBoxes.InfoBox pattern)

    /// <summary>
    /// Called to get the current display value from the rig.
    /// Returns string (matches InfoBox.UpdateDisplayDel).
    /// </summary>
    public Func<string?>? UpdateDisplayFunction { get; set; }

    /// <summary>
    /// Called when user changes the text — sends to rig.
    /// </summary>
    public Action<string?>? UpdateRigFunction { get; set; }

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
    /// Whether the TextBox is read-only.
    /// </summary>
    public bool IsReadOnly
    {
        get => TheTextBox.IsReadOnly;
        set => TheTextBox.IsReadOnly = value;
    }

    /// <summary>
    /// The text content of the TextBox.
    /// </summary>
    public override string ToString() => TheTextBox.Text;

    /// <summary>
    /// Get or set the display text.
    /// </summary>
    public string DisplayText
    {
        get => TheTextBox.Text;
        set => TheTextBox.Text = value;
    }

    #endregion

    private string? _cachedValue;

    /// <summary>
    /// Keyboard event for keys that should be forwarded to MainWindow.
    /// </summary>
    public event Action<KeyEventArgs>? BoxKeyDown;

    public RadioInfoBox()
    {
        InitializeComponent();
    }

    #region Public API (matches RadioBoxes.InfoBox)

    /// <summary>
    /// Poll the rig for the current value and update the display if changed.
    /// Thread-safe via Dispatcher.
    /// </summary>
    public void UpdateDisplay(bool forceUpdate = false)
    {
        if (UpdateDisplayFunction == null)
            return;

        string? newVal = UpdateDisplayFunction();
        if (newVal == null)
            return;

        if (!forceUpdate && newVal == _cachedValue)
            return;

        _cachedValue = newVal;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => TheTextBox.Text = newVal);
        }
        else
        {
            TheTextBox.Text = newVal;
        }
    }

    /// <summary>
    /// Overload without parameter matches WinForms pattern.
    /// </summary>
    public void UpdateDisplay()
    {
        UpdateDisplay(false);
    }

    /// <summary>
    /// Clear the cached value so next UpdateDisplay forces a refresh.
    /// </summary>
    public void Clear()
    {
        _cachedValue = null;
    }

    #endregion

    #region Event Handlers

    private void TheTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Forward function keys and modified keys to the parent window
        if (e.Key >= Key.F1 && e.Key <= Key.F24)
        {
            BoxKeyDown?.Invoke(e);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
                 Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            BoxKeyDown?.Invoke(e);
        }
    }

    private void TheTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Select all text on focus (matches WinForms Box_Enter behavior)
        TheTextBox.SelectAll();
    }

    #endregion
}
