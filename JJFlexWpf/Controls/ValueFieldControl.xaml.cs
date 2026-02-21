using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;

namespace JJFlexWpf.Controls;

/// <summary>
/// Focusable value control for ScreenFieldsPanel.
/// Shows "Label: Value" text, adjustable via Up/Down arrow keys.
/// NVDA reads AutomationProperties.Name on focus; Speak() announces value changes.
/// </summary>
public partial class ValueFieldControl : UserControl
{
    private string _label = "";
    private int _value;
    private int _min;
    private int _max = 100;
    private int _step = 5;
    private bool _suppressEvents;

    /// <summary>Fired when user adjusts the value via keyboard.</summary>
    public event EventHandler<int>? ValueChanged;

    public ValueFieldControl()
    {
        InitializeComponent();
    }

    /// <summary>Human-readable label (e.g., "Volume").</summary>
    public string Label
    {
        get => _label;
        set { _label = value; UpdateDisplay(); }
    }

    /// <summary>Current value.</summary>
    public int Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = Math.Clamp(value, _min, _max);
            UpdateDisplay();
        }
    }

    /// <summary>Minimum allowed value.</summary>
    public int Min
    {
        get => _min;
        set { _min = value; }
    }

    /// <summary>Maximum allowed value.</summary>
    public int Max
    {
        get => _max;
        set { _max = value; }
    }

    /// <summary>Step size for Up/Down adjustment.</summary>
    public int Step
    {
        get => _step;
        set { _step = value; }
    }

    /// <summary>
    /// Set to true during poll updates to suppress ValueChanged events and speech.
    /// </summary>
    public bool SuppressEvents
    {
        get => _suppressEvents;
        set => _suppressEvents = value;
    }

    /// <summary>
    /// Configure all properties at once. Use during initialization.
    /// </summary>
    public void Setup(string label, int min, int max, int step, int initialValue = 0)
    {
        _label = label;
        _min = min;
        _max = max;
        _step = step;
        _value = Math.Clamp(initialValue, min, max);
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        string text = $"{_label}: {_value}";
        DisplayText.Text = text;
        AutomationProperties.SetName(this, text);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                AdjustValue(_step);
                e.Handled = true;
                break;

            case Key.Down:
                AdjustValue(-_step);
                e.Handled = true;
                break;

            case Key.Home:
                SetValue(_min);
                e.Handled = true;
                break;

            case Key.End:
                SetValue(_max);
                e.Handled = true;
                break;
        }
    }

    private void AdjustValue(int delta)
    {
        int newValue = Math.Clamp(_value + delta, _min, _max);
        if (newValue == _value) return;

        _value = newValue;
        UpdateDisplay();

        if (!_suppressEvents)
        {
            ValueChanged?.Invoke(this, _value);
            ScreenReaderOutput.Speak($"{_label} {_value}");
        }
    }

    private void SetValue(int newValue)
    {
        newValue = Math.Clamp(newValue, _min, _max);
        if (newValue == _value) return;

        _value = newValue;
        UpdateDisplay();

        if (!_suppressEvents)
        {
            ValueChanged?.Invoke(this, _value);
            ScreenReaderOutput.Speak($"{_label} {_value}");
        }
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        // Highlight border on focus for sighted users
        OuterBorder.BorderBrush = System.Windows.SystemColors.HighlightBrush;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        OuterBorder.BorderBrush = System.Windows.Media.Brushes.Gray;
    }
}
