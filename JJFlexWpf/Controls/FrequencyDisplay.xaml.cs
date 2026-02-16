using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Controls;

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
    /// Mirrors RadioBoxes.MainBox keyboard handling.
    /// </summary>
    private void DisplayBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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
