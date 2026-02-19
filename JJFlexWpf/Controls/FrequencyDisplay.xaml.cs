using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Controls;

/// <summary>
/// TextBox subclass that suppresses NVDA's automatic text change reading.
/// FreqOut is a custom multi-field display where all screen reader speech is
/// managed by our Speak() calls — NVDA's default TextBox change announcements
/// are meaningless noise (reads the entire concatenated field string).
/// Uses FrameworkElementAutomationPeer instead of TextBoxAutomationPeer to
/// avoid ValuePattern/TextPattern change events.
/// </summary>
public class SilentTextBox : TextBox
{
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new SilentTextBoxPeer(this);
    }

    private class SilentTextBoxPeer : FrameworkElementAutomationPeer
    {
        public SilentTextBoxPeer(FrameworkElement owner) : base(owner) { }

        protected override string GetClassNameCore() => "FrequencyDisplay";

        protected override AutomationControlType GetAutomationControlTypeCore()
            => AutomationControlType.Custom;
    }
}

/// <summary>
/// WPF replacement for RadioBoxes.MainBox — multi-field frequency/status display.
/// Used for FreqOut (frequency, RIT, XIT, split, VOX, S-meter, slice indicators)
/// and StatusBox (power, memories, scan, knob, log file).
///
/// Maintains the same Field-based architecture as the WinForms version:
/// - Fields are defined with key, length, delimiters, and handler delegates
/// - Write(key, text) updates a field value
/// - Display() renders all fields into the TextBox
/// - PositionToField() maps cursor position to a Field for keyboard routing
///
/// Sprint 8 Phase 8.2: Core control structure. Radio wiring in Phase 8.4+.
/// </summary>
public partial class FrequencyDisplay : UserControl
{
    #region Field System

    /// <summary>
    /// Delegate for field-specific keyboard/action handlers.
    /// Matches RadioBoxes.MainBox.HandlerDel signature.
    /// </summary>
    public delegate void FieldHandlerDelegate(object? parameter);

    /// <summary>
    /// A single field within the multi-field display.
    /// Mirrors RadioBoxes.MainBox.Field.
    /// </summary>
    public class DisplayField
    {
        public string Key { get; }
        public int Length { get; }
        public string LeftDelim { get; }
        public string RightDelim { get; }
        public FieldHandlerDelegate? Handler { get; set; }

        /// <summary>
        /// Human-readable label for screen reader announcements (e.g. "Frequency", "Slice 0").
        /// Falls back to Key if not set.
        /// </summary>
        public string? Label { get; set; }

        /// <summary>
        /// Default cursor offset within the field when navigating to it.
        /// Used to set a reasonable tune step (e.g. position 8 in Freq = 1 kHz step).
        /// </summary>
        public int DefaultCursorOffset { get; set; }

        internal string Text { get; set; } = "";
        internal int Position { get; set; }

        public DisplayField(string key, int length, string leftDelim, string rightDelim,
                           FieldHandlerDelegate? handler = null)
        {
            Key = key;
            Length = System.Math.Min(length, 20); // Match WinForms cap
            LeftDelim = leftDelim;
            RightDelim = rightDelim;
            Handler = handler;
        }
    }

    private DisplayField[] _fields = System.Array.Empty<DisplayField>();
    private readonly Dictionary<string, DisplayField> _fieldDict = new();

    /// <summary>
    /// True if any field value has changed since last Display() call.
    /// </summary>
    public bool Changed { get; private set; }

    #endregion

    #region Events

    /// <summary>
    /// Fired when a key is pressed while focus is in a field.
    /// Matches RadioBoxes.MainBox BoxKeydown event pattern.
    /// </summary>
    public event System.Action<DisplayField, KeyEventArgs>? FieldKeyDown;

    #endregion

    public FrequencyDisplay()
    {
        InitializeComponent();
    }

    #region Public API (matches RadioBoxes.MainBox)

    /// <summary>
    /// Initialize the display with an array of field definitions.
    /// Calculates field positions and renders initial display.
    /// </summary>
    public void Populate(DisplayField[] fields)
    {
        _fields = fields;
        _fieldDict.Clear();

        int pos = 0;
        foreach (var field in _fields)
        {
            field.Position = pos;
            field.Text = new string(' ', field.Length);
            _fieldDict[field.Key] = field;
            pos += field.LeftDelim.Length + field.Length + field.RightDelim.Length;
        }

        Display();
    }

    /// <summary>
    /// Update a field's value. Right-justifies within field width.
    /// Sets Changed = true if the value actually changed.
    /// </summary>
    public void Write(string key, string text)
    {
        if (!_fieldDict.TryGetValue(key, out var field))
            return;

        // Right-justify and truncate to field width (matches WinForms behavior)
        string padded = text.Length > field.Length
            ? text[..field.Length]
            : text.PadLeft(field.Length);

        if (padded != field.Text)
        {
            field.Text = padded;
            Changed = true;
        }
    }

    /// <summary>
    /// Read a field's current text value.
    /// </summary>
    public string Read(string key)
    {
        return _fieldDict.TryGetValue(key, out var field) ? field.Text : "";
    }

    /// <summary>
    /// Get a field's starting position in the display string.
    /// </summary>
    public int GetFieldPosition(string key)
    {
        if (!_fieldDict.TryGetValue(key, out var field))
            return 0;
        return field.Position + field.LeftDelim.Length;
    }

    /// <summary>
    /// Get a field's width.
    /// </summary>
    public int GetFieldLength(string key)
    {
        return _fieldDict.TryGetValue(key, out var field) ? field.Length : 0;
    }

    /// <summary>
    /// Execute a field's handler delegate.
    /// </summary>
    public void InvokeHandler(string key, object? parameter = null)
    {
        if (_fieldDict.TryGetValue(key, out var field))
            field.Handler?.Invoke(parameter);
    }

    /// <summary>
    /// Check if a field is empty (all spaces).
    /// </summary>
    public bool IsEmpty(string key)
    {
        return !_fieldDict.TryGetValue(key, out var field) || string.IsNullOrWhiteSpace(field.Text);
    }

    /// <summary>
    /// Clear all field values to spaces.
    /// </summary>
    public void Clear()
    {
        foreach (var field in _fields)
            field.Text = new string(' ', field.Length);
        Changed = true;
        Display();
    }

    /// <summary>
    /// Render all fields into the TextBox. Preserves cursor position.
    /// Thread-safe via Dispatcher.
    /// </summary>
    public void Display()
    {
        Changed = false;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(Display);
            return;
        }

        // Build display string from all fields
        var sb = new System.Text.StringBuilder();
        foreach (var field in _fields)
        {
            sb.Append(field.LeftDelim);
            sb.Append(field.Text);
            sb.Append(field.RightDelim);
        }

        // Preserve cursor position
        int savedPos = DisplayBox.SelectionStart;
        int savedLen = DisplayBox.SelectionLength;

        DisplayBox.Text = sb.ToString();

        // Restore cursor
        if (savedPos <= DisplayBox.Text.Length)
        {
            DisplayBox.SelectionStart = savedPos;
            DisplayBox.SelectionLength = savedLen;
        }
    }

    /// <summary>
    /// Find which field the cursor is currently in.
    /// Returns null if cursor is in a delimiter area.
    /// </summary>
    public DisplayField? PositionToField(int position)
    {
        foreach (var field in _fields)
        {
            int fieldStart = field.Position + field.LeftDelim.Length;
            int fieldEnd = fieldStart + field.Length;
            if (position >= fieldStart && position < fieldEnd)
                return field;
        }
        return null;
    }

    #endregion

    #region Keyboard Routing

    /// <summary>
    /// Route keyboard events to the field under the cursor.
    /// Left/Right move cursor one position at a time (character-by-character).
    /// Home/End jump to first/last field. PgDn jumps to Frequency field.
    /// Other keys route to field-specific handlers.
    /// </summary>
    private void DisplayBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Left/Right: character-by-character cursor movement
        if (e.Key == Key.Left || e.Key == Key.Right)
        {
            NavigateCharacter(e.Key == Key.Right ? 1 : -1);
            e.Handled = true;
            return;
        }

        // Home: jump to first field (Slice)
        if (e.Key == Key.Home)
        {
            if (_fields.Length > 0)
                NavigateToField(_fields[0]);
            e.Handled = true;
            return;
        }

        // End: jump to last field (XIT)
        if (e.Key == Key.End)
        {
            if (_fields.Length > 0)
                NavigateToField(_fields[_fields.Length - 1]);
            e.Handled = true;
            return;
        }

        // PgDn: jump to Frequency field
        if (e.Key == Key.PageDown)
        {
            if (_fieldDict.TryGetValue("Freq", out var freqField))
                NavigateToField(freqField);
            e.Handled = true;
            return;
        }

        // All other keys: route to field handler
        var field = PositionToField(DisplayBox.SelectionStart);
        if (field != null)
        {
            FieldKeyDown?.Invoke(field, e);
        }
    }

    /// <summary>
    /// Suppress all direct text input — content is managed by field handlers.
    /// The TextBox is non-readonly so it can receive focus and cursor positioning,
    /// but typed characters go to the handler (e.g. digit entry in frequency field)
    /// instead of modifying the text directly.
    /// </summary>
    private void DisplayBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        e.Handled = true;
    }

    #endregion

    #region Field Navigation

    /// <summary>
    /// Move cursor one position in the given direction (+1 = right, -1 = left).
    /// Skips delimiter positions, dot separators in Freq, and sign positions in RIT/XIT.
    /// On field boundary crossing, announces the new field label + value.
    /// Within Freq/RIT/XIT, announces the step size at the new position.
    /// </summary>
    private void NavigateCharacter(int direction)
    {
        if (_fields.Length == 0) return;

        int currentPos = DisplayBox.SelectionStart;
        var currentField = PositionToField(currentPos);
        int totalLen = DisplayBox.Text?.Length ?? 0;
        if (totalLen == 0) return;

        int newPos = currentPos + direction;

        // Skip delimiter positions, non-tunable positions, and non-position-sensitive
        // fields we're already in (Volume=3 chars, SMeter=4 chars act as single-step targets)
        while (newPos >= 0 && newPos < totalLen)
        {
            var fieldAtPos = PositionToField(newPos);
            if (fieldAtPos == null)
            {
                // On a delimiter — skip
                newPos += direction;
                continue;
            }

            // Skip dots and leading spaces in Freq field
            if (fieldAtPos.Key == "Freq")
            {
                int fs = fieldAtPos.Position + fieldAtPos.LeftDelim.Length;
                int pif = newPos - fs;
                if (pif >= 0 && pif < fieldAtPos.Text.Length)
                {
                    char c = fieldAtPos.Text[pif];
                    if (c == '.' || c == ' ')
                    {
                        newPos += direction;
                        continue;
                    }
                }
            }

            // Skip sign position (pos 0) in RIT/XIT
            if (fieldAtPos.Key == "RIT" || fieldAtPos.Key == "XIT")
            {
                int fs = fieldAtPos.Position + fieldAtPos.LeftDelim.Length;
                int pif = newPos - fs;
                if (pif == 0)
                {
                    newPos += direction;
                    continue;
                }
            }

            // For non-position-sensitive fields we're already in, skip through
            // to the next field (no per-character step to announce)
            if (currentField != null && fieldAtPos == currentField &&
                fieldAtPos.Key != "Freq" && fieldAtPos.Key != "RIT" && fieldAtPos.Key != "XIT")
            {
                newPos += direction;
                continue;
            }

            break; // Valid position found
        }

        // Clamp check
        if (newPos < 0 || newPos >= totalLen) return;

        var newField = PositionToField(newPos);
        if (newField == null) return;

        DisplayBox.SelectionStart = newPos;

        if (newField != currentField)
        {
            // Crossed into a new field — announce label + value (+ step for position-sensitive fields)
            string label = newField.Label ?? newField.Key;
            string value = GetSpeechText(newField);
            string speech = string.IsNullOrEmpty(value) ? label : $"{label} {value}";

            if (value != "off")
            {
                int fieldStart = newField.Position + newField.LeftDelim.Length;
                int posInField = newPos - fieldStart;
                string? stepName = GetStepName(newField.Key, posInField, newField.Length, newField.Text);
                if (stepName != null)
                    speech += $", {stepName}";
            }

            Radios.ScreenReaderOutput.Speak(speech, true);
        }
        else
        {
            // Same field — announce digit + step for position-sensitive fields
            int fieldStart = newField.Position + newField.LeftDelim.Length;
            int posInField = newPos - fieldStart;
            string? stepName = GetStepName(newField.Key, posInField, newField.Length, newField.Text);
            if (stepName != null)
            {
                char digit = posInField < newField.Text.Length ? newField.Text[posInField] : ' ';
                Radios.ScreenReaderOutput.Speak($"{digit}, {stepName}", true);
            }
        }
    }

    /// <summary>
    /// Move cursor to the given field and announce it for the screen reader.
    /// For position-sensitive fields (Freq, RIT, XIT), also announces the current step size.
    /// </summary>
    private void NavigateToField(DisplayField field)
    {
        int fieldStart = field.Position + field.LeftDelim.Length;
        int offset = System.Math.Min(field.DefaultCursorOffset, field.Length - 1);
        DisplayBox.SelectionStart = fieldStart + offset;

        string label = field.Label ?? field.Key;
        string value = GetSpeechText(field);

        string speech = string.IsNullOrEmpty(value) ? label : $"{label} {value}";

        // Announce step size for position-sensitive fields, but not when RIT/XIT is "off"
        if (value != "off")
        {
            string? stepName = GetStepName(field.Key, offset, field.Length, field.Text);
            if (stepName != null)
                speech += $", {stepName}";
        }

        Radios.ScreenReaderOutput.Speak(speech, true);
    }

    /// <summary>
    /// Get the human-readable step name for a cursor position within a field.
    /// Returns null for fields that don't have position-sensitive stepping.
    /// Also accessible via GetStepNameForKey for use by FreqOutHandlers.
    /// </summary>
    internal static string? GetStepNameForKey(string fieldKey, int posInField, int fieldLen, string? fieldText = null)
        => GetStepName(fieldKey, posInField, fieldLen, fieldText);

    private static string? GetStepName(string fieldKey, int posInField, int fieldLen, string? fieldText = null)
    {
        if (fieldKey == "Freq")
        {
            // Freq field text contains dots and leading spaces (e.g. "   3.828.750").
            // Count actual digits to the right of the cursor position to determine step size.
            if (fieldText != null)
            {
                int digitsToRight = 0;
                for (int i = posInField + 1; i < fieldText.Length; i++)
                {
                    char c = fieldText[i];
                    if (c != '.' && c != ' ')
                        digitsToRight++;
                }
                return digitsToRight switch
                {
                    >= 7 => "10 megahertz",
                    6 => "1 megahertz",
                    5 => "100 kilohertz",
                    4 => "10 kilohertz",
                    3 => "1 kilohertz",
                    2 => "100 hertz",
                    1 => "10 hertz",
                    0 => "1 hertz",
                    _ => null
                };
            }

            // Fallback when no field text available (shouldn't happen in practice)
            int exponent = fieldLen - 1 - posInField;
            return exponent switch
            {
                >= 7 => "10 megahertz",
                6 => "1 megahertz",
                5 => "100 kilohertz",
                4 => "10 kilohertz",
                3 => "1 kilohertz",
                2 => "100 hertz",
                1 => "10 hertz",
                0 => "1 hertz",
                _ => null
            };
        }

        if (fieldKey == "RIT" || fieldKey == "XIT")
        {
            // RIT/XIT field is 5 chars: sign + 4 digits (+0150)
            // Skip sign at pos 0
            int exponent = fieldLen - 1 - posInField;
            return exponent switch
            {
                >= 4 => null, // sign position
                3 => "1000 hertz",
                2 => "100 hertz",
                1 => "10 hertz",
                0 => "1 hertz",
                _ => null
            };
        }

        return null;
    }

    /// <summary>
    /// Announce a field's name and value via the screen reader.
    /// Uses interrupt to override NVDA's raw character reading — FreqOut is a custom
    /// multi-field display where NVDA's native reading is meaningless without the field label.
    /// </summary>
    private static void AnnounceField(DisplayField field)
    {
        string label = field.Label ?? field.Key;
        string value = GetSpeechText(field);
        if (string.IsNullOrEmpty(value))
            Radios.ScreenReaderOutput.Speak(label, true);
        else
            Radios.ScreenReaderOutput.Speak($"{label} {value}", true);
    }

    /// <summary>
    /// Translate braille display text into speech-friendly text.
    /// Braille display shows "rrrr" for inactive RIT and "xxxx" for inactive XIT —
    /// these are meaningful on a braille display but nonsensical as speech.
    /// Toggle fields (Split, VOX, Mute, Offset) translate single-character indicators to words.
    /// </summary>
    private static string GetSpeechText(DisplayField field)
    {
        string raw = field.Text.Trim();
        string key = field.Key;

        // RIT inactive shows as "rrrr" on braille — speak "off"
        if (key == "RIT" && raw.Replace("r", "").Length == 0 && raw.Length > 0)
            return "off";

        // XIT inactive shows as "xxxx" on braille — speak "off"
        if (key == "XIT" && raw.Replace("x", "").Length == 0 && raw.Length > 0)
            return "off";

        // Toggle fields: translate display characters to speech
        if (key == "Split")
            return raw == "S" ? "on" : "off";

        if (key == "VOX")
            return raw == "V" ? "on" : "off";

        if (key == "Mute")
            return raw == "M" ? "on" : "off";

        if (key == "Offset")
        {
            if (raw == "+") return "plus";
            if (raw == "-") return "minus";
            return "off";
        }

        return raw;
    }

    #endregion

    #region Selection Helpers

    /// <summary>
    /// Get/set cursor position in the display. Used by Form1 polling logic.
    /// </summary>
    public int SelectionStart
    {
        get => DisplayBox.SelectionStart;
        set => DisplayBox.SelectionStart = value;
    }

    public int SelectionLength
    {
        get => DisplayBox.SelectionLength;
        set => DisplayBox.SelectionLength = value;
    }

    #endregion
}
