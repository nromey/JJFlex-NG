using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;

namespace JJFlexWpf.Controls;

/// <summary>
/// Focusable cycle control for ScreenFieldsPanel.
/// Shows "Label: Option" text, cycles through options via Up/Down arrow keys.
/// NVDA reads AutomationProperties.Name on focus; Speak() announces selection changes.
/// </summary>
public partial class CycleFieldControl : UserControl
{
    private string _label = "";
    private string[] _options = Array.Empty<string>();
    private int _selectedIndex;
    private bool _suppressEvents;

    /// <summary>Fired when user cycles to a new option.</summary>
    public event EventHandler<int>? SelectionChanged;

    public CycleFieldControl()
    {
        InitializeComponent();
    }

    /// <summary>Human-readable label (e.g., "AGC Mode").</summary>
    public string Label
    {
        get => _label;
        set { _label = value; UpdateDisplay(); }
    }

    /// <summary>Available options to cycle through.</summary>
    public string[] Options
    {
        get => _options;
        set { _options = value ?? Array.Empty<string>(); UpdateDisplay(); }
    }

    /// <summary>Index of the currently selected option.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_options.Length == 0) return;
            int clamped = Math.Clamp(value, 0, _options.Length - 1);
            if (_selectedIndex == clamped) return;
            _selectedIndex = clamped;
            UpdateDisplay();
        }
    }

    /// <summary>The currently selected option text.</summary>
    public string SelectedOption =>
        _options.Length > 0 && _selectedIndex < _options.Length
            ? _options[_selectedIndex]
            : "";

    /// <summary>
    /// Set to true during poll updates to suppress SelectionChanged events and speech.
    /// </summary>
    public bool SuppressEvents
    {
        get => _suppressEvents;
        set => _suppressEvents = value;
    }

    /// <summary>
    /// Configure all properties at once. Use during initialization.
    /// </summary>
    public void Setup(string label, string[] options, int initialIndex = 0)
    {
        _label = label;
        _options = options ?? Array.Empty<string>();
        _selectedIndex = Math.Clamp(initialIndex, 0, Math.Max(0, _options.Length - 1));
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        string optionText = SelectedOption;
        string text = string.IsNullOrEmpty(optionText) ? _label : $"{_label}: {optionText}";
        DisplayText.Text = text;
        AutomationProperties.SetName(this, text);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_options.Length == 0) return;

        switch (e.Key)
        {
            case Key.Up:
                CycleForward();
                e.Handled = true;
                break;

            case Key.Down:
                CycleBackward();
                e.Handled = true;
                break;
        }
    }

    private void CycleForward()
    {
        if (_options.Length == 0) return;
        int newIndex = (_selectedIndex + 1) % _options.Length;
        SetIndex(newIndex);
    }

    private void CycleBackward()
    {
        if (_options.Length == 0) return;
        int newIndex = (_selectedIndex - 1 + _options.Length) % _options.Length;
        SetIndex(newIndex);
    }

    private void SetIndex(int newIndex)
    {
        if (newIndex == _selectedIndex) return;
        _selectedIndex = newIndex;
        UpdateDisplay();

        if (!_suppressEvents)
        {
            SelectionChanged?.Invoke(this, _selectedIndex);
            ScreenReaderOutput.Speak($"{_label} {SelectedOption}");
        }
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        OuterBorder.BorderBrush = System.Windows.SystemColors.HighlightBrush;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        OuterBorder.BorderBrush = System.Windows.Media.Brushes.Gray;
    }
}
