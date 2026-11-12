using System;
using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Controls;

/// <summary>
/// WPF replacement for RadioBoxes.Combo — header + dropdown with rig-aware updates.
/// Used for ModeControl (CW/USB/LSB/AM/FM/DIGU/DIGL) and TXTuneControl (auto/manual).
///
/// Key behavior:
/// - UpdateDisplay() polls rig for current value via delegate, updates ComboBox if changed
/// - User selection triggers UpdateRigFunction delegate to send value to rig
/// - _userEntry flag distinguishes user actions from programmatic updates
///
/// Sprint 8 Phase 8.2: Control structure. Radio wiring in Phase 8.4+.
/// </summary>
public partial class RadioComboBox : UserControl
{
    #region Delegates (match RadioBoxes.Combo pattern)

    /// <summary>
    /// Called to get the current display value from the rig.
    /// Return type is object — Combo.UpdateDisplayDel() returns object.
    /// </summary>
    public Func<object?>? UpdateDisplayFunction { get; set; }

    /// <summary>
    /// Called when user selects a new value — sends to rig.
    /// </summary>
    public Action<object?>? UpdateRigFunction { get; set; }

    /// <summary>
    /// Called when user selects by index — sends index to rig.
    /// </summary>
    public Action<int>? UpdateRigByIndexFunction { get; set; }

    /// <summary>
    /// Maps a display value to the ComboBox index.
    /// </summary>
    public Func<object?, int>? BoxIndexFunction { get; set; }

    #endregion

    #region Properties

    /// <summary>
    /// Header text shown above the ComboBox.
    /// </summary>
    public string Header
    {
        get => HeaderLabel.Text;
        set
        {
            HeaderLabel.Text = value;
            _headerNames = !string.IsNullOrEmpty(value);
            if (_headerNames) AutomationProperties.SetName(TheComboBox, value);
            else TakeOwnNameIfUnnamed();
        }
    }

    /// <summary>True once a non-empty <see cref="Header"/> has named the inner combo.</summary>
    private bool _headerNames;

    /// <summary>
    /// The inner combo, which is what a screen reader lands on, takes the
    /// control's own accessible name when no <see cref="Header"/> names it.
    /// Home names its Operating Mode and Transmit Tune combos with
    /// <c>AutomationProperties.Name</c> on the control and never sets a
    /// Header, so the inner combo had no name and a screen reader said only
    /// "combo box, USB, collapsed". A Header, when set, still wins.
    /// </summary>
    internal void TakeOwnNameIfUnnamed()
    {
        if (_headerNames || TheComboBox == null) return;
        string own = AutomationProperties.GetName(this);
        if (!string.IsNullOrEmpty(own)) AutomationProperties.SetName(TheComboBox, own);
    }

    /// <inheritdoc/>
    /// <remarks>Follows the control's own accessible name as it is set (from XAML, after construction).</remarks>
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == AutomationProperties.NameProperty) TakeOwnNameIfUnnamed();
    }

    /// <summary>
    /// The items list. Replaces RadioBoxes.Combo.TheList (ArrayList).
    /// </summary>
    public IList? TheList
    {
        get => TheComboBox.ItemsSource as IList;
        set => TheComboBox.ItemsSource = value;
    }

    /// <summary>
    /// Whether the ComboBox is read-only (no user selection).
    /// </summary>
    public bool IsReadOnly
    {
        get => !TheComboBox.IsEnabled;
        set => TheComboBox.IsEnabled = !value;
    }

    #endregion

    private bool _userEntry;
    private object? _cachedValue;

    /// <summary>
    /// Keyboard event for keys that should be forwarded to Form1/MainWindow.
    /// </summary>
    public event Action<KeyEventArgs>? BoxKeyDown;

    public RadioComboBox()
    {
        InitializeComponent();
    }

    #region Public API (matches RadioBoxes.Combo)

    /// <summary>
    /// Poll the rig for the current value and update the display if changed.
    /// Only updates the rig if this was triggered by user action.
    /// </summary>
    public void UpdateDisplay(bool forceUpdate = false)
    {
        if (UpdateDisplayFunction == null)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateDisplay(forceUpdate));
            return;
        }

        var currentValue = UpdateDisplayFunction();
        if (!forceUpdate && Equals(currentValue, _cachedValue))
            return;

        _cachedValue = currentValue;

        // Map value to index
        if (BoxIndexFunction != null)
        {
            int index = BoxIndexFunction(currentValue);
            if (index >= 0 && index < TheComboBox.Items.Count)
            {
                _userEntry = false; // Programmatic change — don't send back to rig
                TheComboBox.SelectedIndex = index;
                // If SelectedIndex didn't change, SelectionChanged won't fire,
                // so _userEntry stays false and the next user action is ignored.
                // Always reset to true after programmatic update.
                _userEntry = true;
            }
        }
        else
        {
            // Try direct item match
            _userEntry = false;
            TheComboBox.SelectedItem = currentValue;
            _userEntry = true;
        }
    }

    /// <summary>
    /// Force a specific index selection.
    /// </summary>
    public void SetSelectedIndex(int index)
    {
        if (index >= 0 && index < TheComboBox.Items.Count)
        {
            _userEntry = false;
            TheComboBox.SelectedIndex = index;
            _userEntry = true;
        }
    }

    /// <summary>
    /// Clear the cached value so next UpdateDisplay forces a refresh.
    /// </summary>
    public void ClearCache()
    {
        _cachedValue = null;
    }

    #endregion

    #region Event Handlers

    private void TheComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_userEntry)
        {
            _userEntry = true; // Reset flag for next change
            return;
        }

        // User selected a value — send to rig
        if (UpdateRigByIndexFunction != null)
        {
            UpdateRigByIndexFunction(TheComboBox.SelectedIndex);
        }
        else if (UpdateRigFunction != null && TheComboBox.SelectedItem != null)
        {
            UpdateRigFunction(TheComboBox.SelectedItem);
        }
    }

    private void TheComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
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

    #endregion
}
