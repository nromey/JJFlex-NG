using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace JJFlexWpf.Controls;

/// <summary>
/// WPF replacement for RadioBoxes.ChekBox — enum flags display with collapsible checkboxes.
/// Used for displaying bit-flag enum values from the rig (e.g., DAX/DiversityEngine flags).
///
/// Key behavior:
/// - Setup() initializes with an enum type and current flags value
/// - Shows summary text (e.g., "Flag1+Flag3") when collapsed
/// - Shows individual checkboxes when focused/expanded
/// - Expands on GotFocus, collapses on LostFocus (matches WinForms behavior)
///
/// Sprint 8 Phase 8.2: Control structure. Radio wiring in Phase 8.4+.
/// </summary>
public partial class RadioCheckBox : UserControl
{
    #region Properties

    /// <summary>
    /// GroupBox heading text.
    /// </summary>
    public string Heading
    {
        get => TheGroupBox.Header as string ?? "";
        set
        {
            TheGroupBox.Header = value;
            AutomationProperties.SetName(this, value);
        }
    }

    #endregion

    private CheckBox[]? _checkBoxes;
    private string[]? _bitNames;
    private int[]? _bitValues;
    private int _flagMask;

    public RadioCheckBox()
    {
        InitializeComponent();
        GotFocus += RadioCheckBox_GotFocus;
        LostFocus += RadioCheckBox_LostFocus;
    }

    #region Public API (matches RadioBoxes.ChekBox)

    /// <summary>
    /// Initialize with an enum type and current flags value.
    /// Creates a checkbox for each enum value.
    /// </summary>
    /// <param name="enumType">The enum type (must be [Flags] enum)</param>
    /// <param name="currentFlags">Current flags value as object (cast from int)</param>
    public void Setup(Type enumType, object currentFlags)
    {
        _bitNames = enumType.GetEnumNames();
        var rawValues = (int[])enumType.GetEnumValues();
        _bitValues = rawValues;
        int nBits = _bitValues.Length;

        _flagMask = 0;
        _checkBoxes = new CheckBox[nBits];
        CheckBoxPanel.Children.Clear();

        int flags = (int)currentFlags;

        for (int i = 0; i < nBits; i++)
        {
            _flagMask |= _bitValues[i];

            var cb = new CheckBox
            {
                Content = _bitNames[i],
                IsEnabled = false, // Read-only display (matches WinForms)
                Margin = new Thickness(2, 1, 2, 1),
                IsChecked = (flags & _bitValues[i]) != 0
            };
            AutomationProperties.SetName(cb, _bitNames[i]);
            _checkBoxes[i] = cb;
            CheckBoxPanel.Children.Add(cb);
        }

        UpdateSummary(flags);
    }

    /// <summary>
    /// Update the flags value and refresh display.
    /// </summary>
    public void UpdateFlags(int flags)
    {
        if (_checkBoxes == null || _bitValues == null)
            return;

        for (int i = 0; i < _checkBoxes.Length; i++)
        {
            _checkBoxes[i].IsChecked = (flags & _bitValues[i]) != 0;
        }

        UpdateSummary(flags);
    }

    #endregion

    #region Private Helpers

    private void UpdateSummary(int flags)
    {
        if (_checkBoxes == null || _bitNames == null || _bitValues == null)
            return;

        var parts = new System.Collections.Generic.List<string>();
        for (int i = 0; i < _bitValues.Length; i++)
        {
            if ((flags & _bitValues[i]) != 0)
                parts.Add(_bitNames[i]);
        }

        SummaryText.Text = parts.Count > 0
            ? string.Join("+", parts)
            : "(none)";
    }

    #endregion

    #region Focus Expand/Collapse

    private void RadioCheckBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Expand: show checkboxes, hide summary
        SummaryText.Visibility = Visibility.Collapsed;
        CheckBoxPanel.Visibility = Visibility.Visible;
    }

    private void RadioCheckBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Collapse: show summary, hide checkboxes
        SummaryText.Visibility = Visibility.Visible;
        CheckBoxPanel.Visibility = Visibility.Collapsed;
    }

    #endregion
}
