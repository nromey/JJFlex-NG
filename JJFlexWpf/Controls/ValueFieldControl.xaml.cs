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
    // QB Track I — sign state for typed entry on negative-capable fields.
    // Minus toggles this; it applies to the whole buffer at confirm time.
    private bool _numberNegative;

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
    /// QB Track I — decimal display mode. 0 (default) keeps legacy integer
    /// behavior. N &gt; 0 means Value is carried in scaled integer units
    /// (e.g. 2 → hundredths: Value 550 displays and speaks as "5.50"). Typed
    /// entry accepts a decimal point in this mode. Used for transverter drive
    /// power in centi-dBm.
    /// </summary>
    public int DecimalPlaces { get; set; }

    /// <summary>
    /// QB Track I — unit suffix appended to display and speech (e.g. "dBm").
    /// Empty (default) keeps legacy unlabeled output. The unit rides every
    /// announcement so the operator always hears which scale they're on.
    /// </summary>
    public string Unit { get; set; } = "";

    /// <summary>Format the scaled integer value for display/speech per DecimalPlaces.</summary>
    private string FormatValue(int value)
    {
        if (DecimalPlaces <= 0) return value.ToString();
        double scale = System.Math.Pow(10, DecimalPlaces);
        return (value / scale).ToString("F" + DecimalPlaces,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private string UnitSuffix => string.IsNullOrEmpty(Unit) ? "" : " " + Unit;

    /// <summary>
    /// Set to true during poll updates to suppress ValueChanged events and speech.
    /// </summary>
    public bool SuppressEvents
    {
        get => _suppressEvents;
        set => _suppressEvents = value;
    }

    /// <summary>
    /// Configure all properties at once. Use during initialization — and for
    /// live re-configuration when a field changes personality (the TX power
    /// field flips between integer watts and decimal dBm when the TX antenna
    /// moves on/off the transverter port).
    /// </summary>
    public void Setup(string label, int min, int max, int step, int initialValue = 0,
                      int decimalPlaces = 0, string unit = "")
    {
        _label = label;
        _min = min;
        _max = max;
        _step = step;
        DecimalPlaces = decimalPlaces;
        Unit = unit;
        // Cancel any in-flight typed entry — the old buffer's scale no longer applies.
        _numberEntryMode = false;
        _numberBuffer = "";
        _numberNegative = false;
        _value = Math.Clamp(initialValue, min, max);
        UpdateDisplay();
    }

    /// <summary>
    /// Full update: visual text + AutomationProperties.Name.
    /// Use on setup and focus entry. Avoid during value changes (causes double-speak).
    /// </summary>
    private void UpdateDisplay()
    {
        string text = $"{_label}: {FormatValue(_value)}{UnitSuffix}";
        DisplayText.Text = text;
        AutomationProperties.SetName(this, text);
    }

    /// <summary>
    /// Visual-only update: changes the TextBlock but NOT AutomationProperties.Name,
    /// so NVDA doesn't auto-announce the Name change (Speak() handles it instead).
    /// </summary>
    private void UpdateVisual()
    {
        DisplayText.Text = $"{_label}: {FormatValue(_value)}{UnitSuffix}";
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
                // Up = configured Step (default 5); Shift+Up = 1 as fine-grain escape hatch.
                AdjustValue(shift ? 1 : _step);
                e.Handled = true;
                break;

            case Key.Down:
                // Down = configured Step (default 5); Shift+Down = 1 as fine-grain escape hatch.
                AdjustValue(shift ? -1 : -_step);
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
                    BeginNumberEntry(e.Key);
                    e.Handled = true;
                }
                break;

            // Minus can START entry on negative-capable fields ("-8" must be
            // typeable from the first keystroke — QB Track A, Noel live find on
            // RX RF gain; refusal speech per QB Track I). OemMinus with Shift
            // is underscore, so only the unshifted key counts.
            case Key.OemMinus:
                if (!shift)
                {
                    if (_min < 0)
                        BeginNumberEntry(e.Key);
                    else
                        RejectEntryKey($"{_label} does not accept negative values");
                    e.Handled = true;
                }
                break;

            // NumPad Subtract is minus regardless of Shift (QB Track A).
            case Key.Subtract:
                if (_min < 0)
                    BeginNumberEntry(e.Key);
                else
                    RejectEntryKey($"{_label} does not accept negative values");
                e.Handled = true;
                break;

            // QB Track I — decimal point can START entry on decimal fields
            // (".5" dBm). Integer fields speak the refusal.
            case Key.OemPeriod: case Key.Decimal:
                if (!shift)
                {
                    if (DecimalPlaces > 0)
                        BeginNumberEntry(e.Key);
                    else
                        RejectEntryKey($"{_label} takes whole numbers only");
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>
    /// Begin typed-entry mode and process the triggering key (a digit, minus
    /// on signed fields, or point on decimal fields). Rejection of
    /// inapplicable starters happens at the call sites so every refusal
    /// speaks — the one surviving copy of the Track A / Track I seam.
    /// </summary>
    private void BeginNumberEntry(Key firstKey)
    {
        _numberEntryMode = true;
        _numberBuffer = "";
        _numberNegative = false;
        ScreenReaderOutput.Speak($"Enter {_label} value", interrupt: true);
        HandleNumberEntryKey(firstKey);
    }

    /// <summary>
    /// Toggle the pending typed value's sign (Track A's seam name; one copy
    /// survives the merge).
    /// </summary>
    private void ToggleBufferSign()
    {
        _numberNegative = !_numberNegative;
        ScreenReaderOutput.Speak(_numberNegative ? "minus" : "minus removed");
        UpdateNumberEntryDisplay();
    }

    /// <summary>Audible rejection — every bound key speaks in every state.</summary>
    private static void RejectEntryKey(string message)
    {
        EarconPlayer.Warning1Beep();
        ScreenReaderOutput.Speak(message, VerbosityLevel.Terse, interrupt: true);
    }

    /// <summary>
    /// Handle key presses during number entry mode.
    /// Returns true if the key was consumed.
    /// </summary>
    private bool HandleNumberEntryKey(Key key)
    {
        // QB Track I — minus toggles the pending value's sign (so a stray
        // minus is recoverable by pressing it again). Gated on the field
        // actually reaching below zero; otherwise it speaks its refusal.
        if (key == Key.OemMinus || key == Key.Subtract)
        {
            if (_min >= 0)
            {
                RejectEntryKey($"{_label} does not accept negative values");
                return true;
            }
            ToggleBufferSign();
            return true;
        }

        // QB Track I — decimal point, on decimal-capable fields only, one per value.
        if (key == Key.OemPeriod || key == Key.Decimal)
        {
            if (DecimalPlaces <= 0)
            {
                RejectEntryKey($"{_label} takes whole numbers only");
                return true;
            }
            if (_numberBuffer.Contains('.'))
            {
                RejectEntryKey("Already has a point");
                return true;
            }
            _numberBuffer += '.';
            ScreenReaderOutput.Speak("point");
            UpdateNumberEntryDisplay();
            return true;
        }

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
            _numberNegative = false;
            ScreenReaderOutput.Speak("Cancelled", VerbosityLevel.Terse);
            UpdateDisplay();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Confirm the number entry buffer and apply the value.
    /// Integer fields parse the buffer directly; decimal fields (DecimalPlaces
    /// &gt; 0) parse a decimal number and scale it to integer units (e.g.
    /// "5.5" dBm → 550 centi-dBm). The sign toggle applies at the end.
    /// </summary>
    private void ConfirmNumberEntry()
    {
        _numberEntryMode = false;
        bool parsed;
        int val = 0;
        if (DecimalPlaces > 0)
        {
            parsed = double.TryParse(_numberBuffer,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out double d);
            if (parsed)
                val = (int)System.Math.Round(d * System.Math.Pow(10, DecimalPlaces));
        }
        else
        {
            parsed = int.TryParse(_numberBuffer, out val);
        }

        if (parsed)
        {
            if (_numberNegative) val = -val;
            val = Math.Clamp(val, _min, _max);
            _value = val;
            UpdateDisplay();

            if (!_suppressEvents)
            {
                ValueChanged?.Invoke(this, _value);
                ScreenReaderOutput.Speak($"{_label} {FormatValue(_value)}{UnitSuffix}", VerbosityLevel.Terse);
                EarconPlayer.ConfirmTone();
            }
        }
        else
        {
            ScreenReaderOutput.Speak("Invalid, cancelled", VerbosityLevel.Terse);
            UpdateDisplay();
        }
        _numberBuffer = "";
        _numberNegative = false;
    }

    /// <summary>
    /// Show the current number entry buffer in the display.
    /// </summary>
    private void UpdateNumberEntryDisplay()
    {
        string signed = (_numberNegative ? "-" : "") + _numberBuffer;
        string text = $"{_label}: {signed}_";
        DisplayText.Text = text;
        AutomationProperties.SetName(this, $"{_label}: entering {signed}");
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

            // LATEST, keyed per field. Holding an arrow key sweeps this value
            // many times a second; interrupting on every step produced a
            // stutter of half-spoken numbers and never finished saying the one
            // the operator actually stopped on. Coalescing keeps only the
            // value they settled on.
            //
            // Keyed by label so two different fields adjusted in quick
            // succession do not silence each other - only the SAME field
            // supersedes itself.
            //
            // This control has 48 field declarations across four hosts
            // (ScreenFieldsPanel, Audio Workshop, Noise Profiles, Audio
            // Levels), so this one change covers every adjustable value in the
            // application.
            ScreenReaderOutput.Speak(
                $"{_label} {FormatValue(_value)}{UnitSuffix}",
                Radios.Speech.SpeechIntent.Latest,
                VerbosityLevel.Terse,
                coalesceKey: $"value-field:{_label}");
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

            // LATEST, keyed per field. Holding an arrow key sweeps this value
            // many times a second; interrupting on every step produced a
            // stutter of half-spoken numbers and never finished saying the one
            // the operator actually stopped on. Coalescing keeps only the
            // value they settled on.
            //
            // Keyed by label so two different fields adjusted in quick
            // succession do not silence each other - only the SAME field
            // supersedes itself.
            //
            // This control has 48 field declarations across four hosts
            // (ScreenFieldsPanel, Audio Workshop, Noise Profiles, Audio
            // Levels), so this one change covers every adjustable value in the
            // application.
            ScreenReaderOutput.Speak(
                $"{_label} {FormatValue(_value)}{UnitSuffix}",
                Radios.Speech.SpeechIntent.Latest,
                VerbosityLevel.Terse,
                coalesceKey: $"value-field:{_label}");
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
