using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Radios;

/// <summary>
/// Parses the human-readable chord strings the leader-key inventory carries
/// ("Ctrl+J, Shift+N", "Ctrl+J, H or ?", "Ctrl+J, Shift+A through Shift+H")
/// into the <see cref="Keys"/> values the dispatcher actually switches on.
/// </summary>
/// <remarks>
/// <para>
/// Sprint 35 Track E (#183, #206). Two consumers, on purpose:
/// </para>
/// <para>
/// 1. <c>KeyInventory.TryFindLeaderNearMiss</c> uses it to answer "is this
/// letter bound at another modifier level?" when a leader chord falls through
/// to the unknown-command arm — so "Ctrl+G is not a command" can continue
/// "G: arm or disarm the TX test tone" instead of dead-ending (#206).
/// </para>
/// <para>
/// 2. The leader-layer consistency test parses the advertised chord strings
/// out of the inventory SOURCE and runs them through this same parser, then
/// compares the result against the chords <c>DoLeaderCommand</c>'s switch
/// handles — both directions. That is the check whose absence let "H or ?"
/// advertise a dead "?" for months (#183): the help said it, the switch
/// carried a bare <c>Keys.Oem2</c> that a shifted "?" never matches, and
/// nothing compared the two.
/// </para>
/// <para>
/// Lives in Radios rather than JJFlexWpf so Radios.Tests can exercise it
/// directly — the same placement reasoning as <see cref="KeyMapIntegrity"/>.
/// </para>
/// </remarks>
public static class LeaderChordParser
{
    private const string LeaderPrefix = "Ctrl+J, ";

    /// <summary>
    /// Every chord one inventory KeyDisplay advertises, exclusions removed.
    /// </summary>
    /// <param name="keyDisplay">
    /// The display string, with or without the "Ctrl+J, " prefix. A string
    /// that does not parse contributes nothing rather than throwing — the
    /// consistency test is what catches an unparseable entry, loudly.
    /// </param>
    /// <param name="excluded">
    /// Chords written inside this entry's RANGE that do not belong to it, as
    /// exact KeyDisplay strings ("Ctrl+J, Shift+F"). Matches the inventory's
    /// ExcludedKeys field.
    /// </param>
    public static IReadOnlyList<Keys> ParseDisplay(string keyDisplay, IEnumerable<string>? excluded = null)
    {
        var result = new List<Keys>();
        if (string.IsNullOrWhiteSpace(keyDisplay)) return result;

        string body = StripPrefix(keyDisplay);

        // Range form: "Shift+A through Shift+H".
        int through = body.IndexOf(" through ", StringComparison.Ordinal);
        if (through >= 0)
        {
            if (TryParseChord(body.Substring(0, through), out Keys from) &&
                TryParseChord(body.Substring(through + " through ".Length), out Keys to))
            {
                Keys mods = from & Keys.Modifiers;
                Keys fromCode = from & Keys.KeyCode;
                Keys toCode = to & Keys.KeyCode;
                if ((to & Keys.Modifiers) == mods && fromCode <= toCode)
                {
                    for (int c = (int)fromCode; c <= (int)toCode; c++)
                        result.Add((Keys)c | mods);
                }
            }
        }
        else
        {
            // Alternate form: "H or ?". Each alternate is one chord — except
            // "?", which contributes BOTH Oem2|Shift and bare Oem2: the glyph
            // is the shifted form on a US layout, and the dispatcher carries
            // both cases so the key lands with or without Shift. Advertising
            // both keeps the consistency test honest in both directions.
            foreach (string alt in body.Split(new[] { " or " }, StringSplitOptions.RemoveEmptyEntries))
            {
                string a = alt.Trim();
                if (a == "?")
                {
                    result.Add(Keys.Oem2 | Keys.Shift);
                    result.Add(Keys.Oem2);
                }
                else if (TryParseChord(a, out Keys chord))
                {
                    result.Add(chord);
                }
            }
        }

        if (excluded != null)
        {
            foreach (string ex in excluded)
            {
                if (TryParseChord(StripPrefix(ex), out Keys exChord))
                    result.Remove(exChord);
            }
        }

        return result;
    }

    /// <summary>
    /// One chord: "N", "Shift+N", "Ctrl+A", "Escape". "?" parses as
    /// Oem2|Shift (its US-layout arrival form) — the bare-Oem2 twin is
    /// <see cref="ParseDisplay"/>'s business, not this method's.
    /// </summary>
    public static bool TryParseChord(string text, out Keys chord)
    {
        chord = Keys.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        Keys mods = Keys.None;
        string[] parts = StripPrefix(text.Trim()).Split('+');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim())
            {
                case "Ctrl":
                case "Control": mods |= Keys.Control; break;
                case "Shift": mods |= Keys.Shift; break;
                case "Alt": mods |= Keys.Alt; break;
                default: return false;
            }
        }

        string keyName = parts[parts.Length - 1].Trim();
        if (keyName.Length == 0) return false;

        if (keyName == "?")
        {
            chord = Keys.Oem2 | Keys.Shift | mods;
            return true;
        }
        if (keyName.Length == 1 && keyName[0] >= 'A' && keyName[0] <= 'Z')
        {
            chord = (Keys)keyName[0] | mods;
            return true;
        }
        if (Enum.TryParse(keyName, out Keys named) && named != Keys.None &&
            (named & Keys.Modifiers) == 0)
        {
            chord = named | mods;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The same letter at other modifier levels, most-likely-intent first:
    /// bare, then Shift, then Ctrl, the pressed chord itself excluded.
    /// </summary>
    /// <remarks>
    /// #206's ordering rule, verbatim from the task: "Name at most one
    /// alternative — the bare form first, since that is the most likely
    /// intent." The caller takes the first candidate that is actually bound.
    /// </remarks>
    public static IReadOnlyList<Keys> NearMissCandidates(Keys pressed)
    {
        var result = new List<Keys>();
        Keys code = pressed & Keys.KeyCode;
        if (code == Keys.None) return result;

        foreach (Keys candidate in new[] { code, code | Keys.Shift, code | Keys.Control })
        {
            if (candidate != pressed)
                result.Add(candidate);
        }
        return result;
    }

    private static string StripPrefix(string s) =>
        s.StartsWith(LeaderPrefix, StringComparison.Ordinal)
            ? s.Substring(LeaderPrefix.Length)
            : s;
}
