namespace JJFlexWpf;

/// <summary>
/// Text-shaping helpers for screen-reader-facing WPF text surfaces.
/// Centralized so every read-only reviewable text box in the app applies the
/// same NVDA quirks handling — AdvisoryDialog discovered them first, and any
/// dialog that builds its own arrowable text surface needs the identical
/// treatment (see the 2026-08-04 NVDA findings in the dialog-sweep ledger).
/// </summary>
public static class ScreenReaderText
{
    /// <summary>
    /// Two NVDA-specific quirks handled here so no call site has to care.
    /// Callers write \n freely; a WPF TextBox exposes bare \n or stray \r to
    /// UIA as control characters, so everything is normalized to \r\n. And a
    /// truly empty line collapses to a degenerate UIA text range that NVDA
    /// expands to the neighboring line — so arrowing onto a paragraph gap
    /// re-reads the previous line instead of saying "blank" (JAWS is
    /// unaffected). A single space on each empty line keeps the range real;
    /// NVDA reads whitespace-only lines as "blank", which is the behavior
    /// the user expects.
    /// </summary>
    public static string NormalizeLineBreaks(string s)
    {
        var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Length == 0)
                lines[i] = " ";
        return string.Join("\r\n", lines);
    }
}
