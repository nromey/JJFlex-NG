using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Controls;

/// <summary>
/// A read-only, arrow-reviewable text box for status surfaces that refresh
/// on a timer (the GPS dialog updates at 1 Hz; the Connection Tester wants
/// the same shape).
///
/// The problem this solves: a live status page rebuilt from TextBlocks is
/// invisible to the Tab key, so a screen reader user cannot park the caret
/// in it and read line by line — and a naive TextBox rewrite once a second
/// throws the caret back to the top and makes NVDA chatter. This control is
/// a tab stop, keeps the review caret where the user left it across
/// refreshes, and skips the rewrite entirely when nothing changed.
///
/// Callers push new content through <see cref="SetStatusText"/> and never
/// touch <see cref="TextBox.Text"/> directly. Line breaks and blank lines
/// are normalized per <see cref="ScreenReaderText.NormalizeLineBreaks"/> so
/// NVDA reads paragraph gaps as "blank" instead of re-reading a neighbor.
/// </summary>
public class LiveStatusTextBox : TextBox
{
    private string _lastSetText = string.Empty;

    public LiveStatusTextBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = true;
        TextWrapping = TextWrapping.Wrap;
        AcceptsReturn = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        BorderThickness = new Thickness(1);
    }

    /// <summary>
    /// Replace the content, preserving the user's reading position.
    ///
    /// When the text is unchanged the call is a no-op — this matters, because
    /// rewriting identical text still raises UIA text-changed events and NVDA
    /// narrates them. When it did change, the caret is restored by line and
    /// column rather than raw offset: the typical edit is a number changing
    /// width somewhere above the caret, and a raw-offset restore would drift
    /// the caret sideways through the middle of a word on every refresh.
    /// An active selection is preserved by raw offsets, clamped — mid-drag
    /// during a refresh is rare enough that approximate is fine.
    /// </summary>
    public void SetStatusText(string text)
    {
        string normalized = ScreenReaderText.NormalizeLineBreaks(text ?? string.Empty);
        if (normalized == _lastSetText && normalized == Text)
            return;
        _lastSetText = normalized;

        bool hadSelection = SelectionLength > 0;
        int selStart = SelectionStart;
        int selLength = SelectionLength;

        // Caret position as line + column, best-effort. GetLineIndex... can
        // return -1 before layout has run; fall back to offset 0.
        int caret = CaretIndex;
        int line = 0, column = 0;
        try
        {
            line = GetLineIndexFromCharacterIndex(caret);
            if (line >= 0)
                column = caret - GetCharacterIndexFromLineIndex(line);
            else
                line = 0;
        }
        catch { line = 0; column = 0; }

        Text = normalized;

        try
        {
            if (hadSelection)
            {
                int start = Math.Min(selStart, Text.Length);
                Select(start, Math.Min(selLength, Text.Length - start));
                return;
            }

            int lineCount = LineCount;
            if (lineCount <= 0) return;
            int newLine = Math.Min(line, lineCount - 1);
            int lineStart = GetCharacterIndexFromLineIndex(newLine);
            if (lineStart < 0) return;
            int lineLength = GetLineLength(newLine);
            // Keep the caret off the line's terminating \r\n.
            int maxColumn = Math.Max(0, lineLength - LineEndingLength(newLine, lineCount));
            CaretIndex = lineStart + Math.Min(column, maxColumn);
        }
        catch
        {
            // Layout not ready — the caret lands at 0, which is where a
            // fresh read starts anyway.
        }
    }

    private int LineEndingLength(int line, int lineCount)
        => line < lineCount - 1 ? 2 : 0; // interior lines end with \r\n
}
