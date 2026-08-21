using System.Globalization;
using System.Text;

namespace JJFlex.UiaProbe;

[Flags]
internal enum Mods { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

/// <summary>One physical keystroke: modifiers held, then one key struck.</summary>
internal sealed record Step(Mods Mods, ushort Vk, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// A chord is a SEQUENCE of steps, because this application's key map is not
/// flat: Ctrl+J is a leader that opens a layer, so "Ctrl+J, V, H" is three
/// keystrokes in a row and testing only the first proves nothing.
/// </summary>
internal sealed class Chord
{
    public required IReadOnlyList<Step> Steps { get; init; }
    public required string Display { get; init; }

    public bool UsesAlt => Steps.Any(s => (s.Mods & Mods.Alt) != 0);

    public override string ToString() => Display;

    // ────────────────────────── parsing ──────────────────────────

    /// <summary>
    /// Parse a chord written the way this repository writes them:
    /// "Ctrl+J, Ctrl+A", "Shift+F6", "Space", "Ctrl+J, V, H".
    /// Steps are comma-separated; modifiers are plus-separated.
    /// </summary>
    public static bool TryParse(string text, out Chord chord, out string error)
    {
        chord = null!;
        error = "";
        var steps = new List<Step>();
        foreach (string rawStep in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseStep(rawStep, out Step step, out error)) return false;
            steps.Add(step);
        }
        if (steps.Count == 0) { error = "empty chord"; return false; }
        chord = new Chord { Steps = steps, Display = string.Join(", ", steps.Select(s => s.Display)) };
        return true;
    }

    private static bool TryParseStep(string text, out Step step, out string error)
    {
        step = null!;
        error = "";
        var mods = Mods.None;
        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // "Ctrl++" and a bare "+" both mean the plus key, so only treat a part
        // as a modifier when something follows it.
        string keyPart = parts.Length == 0 ? text.Trim() : parts[^1];
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Mods.Ctrl; break;
                case "alt": mods |= Mods.Alt; break;
                case "shift": mods |= Mods.Shift; break;
                case "win" or "windows": mods |= Mods.Win; break;
                default: error = $"unknown modifier '{parts[i]}' in '{text}'"; return false;
            }
        }
        if (text.EndsWith('+') && parts.Length > 0) keyPart = "+";

        if (!TryKey(keyPart, out ushort vk, out bool needsShift))
        {
            error = $"unknown key '{keyPart}' in '{text}'";
            return false;
        }
        if (needsShift) mods |= Mods.Shift;

        step = new Step(mods, vk, Render(mods, keyPart));
        return true;
    }

    private static string Render(Mods m, string key)
    {
        var sb = new StringBuilder();
        if ((m & Mods.Ctrl) != 0) sb.Append("Ctrl+");
        if ((m & Mods.Alt) != 0) sb.Append("Alt+");
        if ((m & Mods.Shift) != 0) sb.Append("Shift+");
        if ((m & Mods.Win) != 0) sb.Append("Win+");
        sb.Append(Canonical(key));
        return sb.ToString();
    }

    private static string Canonical(string key) => key.Length == 1
        ? key.ToUpperInvariant()
        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.ToLowerInvariant());

    /// <summary>
    /// Key name to virtual-key code. <paramref name="needsShift"/> comes back
    /// true for the shifted face of a physical key — "?" is Shift and the "/"
    /// key, and forgetting that is how a probe reports a working key as dead.
    /// Layout note: the OEM codes below are the US layout. On another layout
    /// the punctuation chords land on different physical keys, which the report
    /// must not silently call a failure.
    /// </summary>
    private static bool TryKey(string name, out ushort vk, out bool needsShift)
    {
        needsShift = false;
        vk = 0;
        string n = name.Trim();
        if (n.Length == 0) return false;

        if (n.Length == 1)
        {
            char c = char.ToUpperInvariant(n[0]);
            if (c is >= 'A' and <= 'Z') { vk = (ushort)c; return true; }
            if (c is >= '0' and <= '9') { vk = (ushort)c; return true; }
            switch (c)
            {
                case '[': vk = 0xDB; return true;
                case ']': vk = 0xDD; return true;
                case ',': vk = 0xBC; return true;
                case '.': vk = 0xBE; return true;
                case '=': vk = 0xBB; return true;
                case '-': vk = 0xBD; return true;
                case '/': vk = 0xBF; return true;
                case ';': vk = 0xBA; return true;
                case '\'': vk = 0xDE; return true;
                case '\\': vk = 0xDC; return true;
                case '`': vk = 0xC0; return true;
                case '+': vk = 0xBB; needsShift = true; return true;
                case '?': vk = 0xBF; needsShift = true; return true;
                case '<': vk = 0xBC; needsShift = true; return true;
                case '>': vk = 0xBE; needsShift = true; return true;
                case '_': vk = 0xBD; needsShift = true; return true;
                case ' ': vk = 0x20; return true;
            }
            return false;
        }

        if (n.Length is 2 or 3 && (n[0] is 'F' or 'f')
            && int.TryParse(n.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int fn)
            && fn is >= 1 and <= 24)
        {
            vk = (ushort)(0x70 + fn - 1);
            return true;
        }

        switch (n.ToLowerInvariant())
        {
            case "space" or "spacebar": vk = 0x20; return true;
            case "enter" or "return": vk = 0x0D; return true;
            case "escape" or "esc": vk = 0x1B; return true;
            case "tab": vk = 0x09; return true;
            case "backspace" or "back": vk = 0x08; return true;
            case "delete" or "del": vk = 0x2E; return true;
            case "insert" or "ins": vk = 0x2D; return true;
            case "up": vk = 0x26; return true;
            case "down": vk = 0x28; return true;
            case "left": vk = 0x25; return true;
            case "right": vk = 0x27; return true;
            case "home": vk = 0x24; return true;
            case "end": vk = 0x23; return true;
            case "pageup" or "pgup" or "page up": vk = 0x21; return true;
            case "pagedown" or "pgdn" or "page down": vk = 0x22; return true;
            case "comma": vk = 0xBC; return true;
            case "period" or "dot": vk = 0xBE; return true;
            case "plus": vk = 0xBB; needsShift = true; return true;
            case "equals" or "equal": vk = 0xBB; return true;
            case "minus" or "dash" or "hyphen": vk = 0xBD; return true;
            case "slash": vk = 0xBF; return true;
            case "question": vk = 0xBF; needsShift = true; return true;
            case "semicolon": vk = 0xBA; return true;
            case "quote" or "apostrophe": vk = 0xDE; return true;
            case "backslash": vk = 0xDC; return true;
            case "backtick" or "grave": vk = 0xC0; return true;
            case "lbracket" or "openbracket": vk = 0xDB; return true;
            case "rbracket" or "closebracket": vk = 0xDD; return true;
            case "apps" or "menu" or "applications": vk = 0x5D; return true;
            default: return false;
        }
    }
}
