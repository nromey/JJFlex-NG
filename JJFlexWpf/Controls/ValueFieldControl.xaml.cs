using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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
    private bool _numberEntryMode;
    private string _numberBuffer = "";

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
        AutomationProperties.SetName(this, $"{_label}: {_value}");
    }

    /// <summary>
    /// Full update: visual text + AutomationProperties.Name.
    /// Use on setup and focus entry. Avoid during value changes (causes double-speak).
    /// </summary>
    private void UpdateDisplay()
    {
        string text = $"{_label}: {_value}";
        DisplayText.Text = text;
        AutomationProperties.SetName(this, text);
    }

    /// <summary>
    /// Visual-only update: changes the TextBlock but NOT AutomationProperties.Name,
    /// so NVDA doesn't auto-announce the Name change (Speak() handles it instead).
    /// </summary>
    private void UpdateVisual()
    {
        DisplayText.Text = $"{_label}: {_value}";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Number entry mode: intercept digits, backspace, enter, escape
        if (_numberEntryMode)
        {
            if (HandleNumberEntryKey(e.Key))
            {
                e.Handled = true;
                return;
            }
        }

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.Up:
                // Up=1, Shift+Up=5
                AdjustValue(shift ? 5 : 1);
                e.Handled = true;
                break;

            case Key.Down:
                // Down=1, Shift+Down=5
                AdjustValue(shift ? -5 : -1);
                e.Handled = true;
                break;

            case Key.PageUp:
                AdjustValue(10);
                e.Handled = true;
                break;

            case Key.PageDown:
                AdjustValue(-10);
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

            // Digit keys auto-enter number mode (4.4)
            case Key.D0: case Key.D1: case Key.D2: case Key.D3: case Key.D4:
            case Key.D5: case Key.D6: case Key.D7: case Key.D8: case Key.D9:
            case Key.NumPad0: case Key.NumPad1: case Key.NumPad2: case Key.NumPad3: case Key.NumPad4:
            case Key.NumPad5: case Key.NumPad6: case Key.NumPad7: case Key.NumPad8: case Key.NumPad9:
                if (!shift) // Don't trigger on Shift+digit (special chars)
                {
                    _numberEntryMode = true;
                    _numberBuffer = "";
                    ScreenReaderOutput.Speak($"Enter {_label} value", interrupt: true);
                    HandleNumberEntryKey(e.Key); // Process the first digit
                    e.Handled = true;
                }
                break;

            case Key.Enter:
                _numberEntryMode = true;
                _numberBuffer = "";
                ScreenReaderOutput.Speak($"Enter {_label} value", interrupt: true);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Handle key presses during number entry mode.
    /// Returns true if the key was consumed.
    /// </summary>
    private bool HandleNumberEntryKey(Key key)
    {
        // Digit keys (top row)
        if (key >= Key.D0 && key <= Key.D9)
        {
            char digit = (char)('0' + (key - Key.D0));
            _numberBuffer += digit;
            ScreenReaderOutput.Speak(digit.ToString());
            UpdateNumberEntryDisplay();
            return true;
        }

        // Numpad digit keys
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            char digit = (char)('0' + (key - Key.NumPad0));
            _numberBuffer += digit;
            ScreenReaderOutput.Speak(digit.ToString());
            UpdateNumberEntryDisplay();
            return true;
        }

        // Backspace: delete last digit
        if (key == Key.Back && _numberBuffer.Length > 0)
        {
            _numberBuffer = _numberBuffer.Substring(0, _numberBuffer.Length - 1);
            ScreenReaderOutput.Speak("delete");
            UpdateNumberEntryDisplay();
            return true;
        }

        // Enter: confirm entry
        if (key == Key.Enter)
        {
            ConfirmNumberEntry();
            return true;
        }

        // Escape: cancel entry
        if (key == Key.Escape)
        {
            _numberEntryMode = false;
            _numberBuffer = "";
            ScreenReaderOutput.Speak("Cancelled", VerbosityLevel.Terse);
            UpdateDisplay();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Confirm the number entry buffer and apply the value.
    /// </summary>
    private void ConfirmNumberEntry()
    {
        _numberEntryMode = false;
        if (int.TryParse(_numberBuffer, out int val))
        {
            val = Math.Clamp(val, _min, _max);
            _value = val;
            UpdateDisplay();

            if (!_suppressEvents)
            {
                ValueChanged?.Invoke(this, _value);
                ScreenReaderOutput.Speak($"{_label} {_value}", VerbosityLevel.Terse);
                EarconPlayer.ConfirmTone();
            }
        }
        else
        {
            ScreenReaderOutput.Speak("Invalid, cancelled", VerbosityLevel.Terse);
            UpdateDisplay();
        }
        _numberBuffer = "";
    }

    /// <summary>
    /// Show the current number entry buffer in the display.
    /// </summary>
    private void UpdateNumberEntryDisplay()
    {
        string text = $"{_label}: {_numberBuffer}_";
        DisplayText.Text = text;
        AutomationProperties.SetName(this, $"{_label}: entering {_numberBuffer}");
    }

    private void AdjustValue(int delta)
    {
        int newValue = Math.Clamp(_value + delta, _min, _max);
        if (newValue == _value) return;

        _value = newValue;
        UpdateVisual();

        if (!_suppressEvents)
        {
            ValueChanged?.Invoke(this, _value);
            ScreenReaderOutput.Speak($"{_label} {_value}", VerbosityLevel.Terse, interrupt: true);
        }
    }

    private void SetValue(int newValue)
    {
        newValue = Math.Clamp(newValue, _min, _max);
        if (newValue == _value) return;

        _value = newValue;
        UpdateVisual();

        if (!_suppressEvents)
        {
            ValueChanged?.Invoke(this, _value);
            ScreenReaderOutput.Speak($"{_label} {_value}", VerbosityLevel.Terse, interrupt: true);
        }
    }

    /// <summary>
    /// Hide child TextBlock from UIA tree so NVDA reads only AutomationProperties.Name,
    /// not the TextBlock content as well (which causes double-speak).
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new LeafControlAutomationPeer(this);
    }

    private class LeafControlAutomationPeer : FrameworkElementAutomationPeer
    {
        public LeafControlAutomationPeer(FrameworkElement owner) : base(owner) { }
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        OuterBorder.BorderBrush = System.Windows.SystemColors.HighlightBrush;
        // Refresh the accessible name so NVDA reads the current value on focus entry.
        UpdateDisplay();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        OuterBorder.BorderBrush = System.Windows.Media.Brushes.Gray;
    }
}
