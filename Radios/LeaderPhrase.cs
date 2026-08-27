using System;

namespace Radios;

/// <summary>
/// Trims a leader command's inventory description down to the part worth
/// saying in the near-miss moment (#206).
/// </summary>
/// <remarks>
/// <para>
/// The inventory description is written for the Keys dialog and the Ctrl+J, H
/// help, where the reader is browsing and the parenthetical earns its place.
/// The near-miss is the opposite situation: the operator has already made a
/// mistake, is standing inside a modal layer, and wants to know which key they
/// nearly pressed so they can press the right one. #206 says it outright —
/// "keep it short — this fires when someone has already made a mistake and does
/// not want a paragraph."
/// </para>
/// <para>
/// Before this, the longest near-miss ran to twenty words: "Shift+O is not a
/// command. O: Say what is still running and what it is costing — recording,
/// captures, meter tones." The tail is real information and it is the wrong
/// information here.
/// </para>
/// <para>
/// <b>The rule is deliberately dumb and deterministic:</b> cut at the first em
/// dash, opening parenthesis or colon, because that is where these descriptions
/// consistently stop naming the command and start qualifying it. A cleverer
/// rule would be a second thing to keep true. It never cuts at a comma —
/// "Say what is still running and what it is costing" needs its whole clause,
/// and a comma rule would amputate it.
/// </para>
/// <para>
/// It is a RENDERING of the inventory description, not a second copy of it.
/// Nothing here is hand-maintained, so a description that changes upstream
/// changes here too — which is the whole point of the layer having one table
/// (#265).
/// </para>
/// </remarks>
public static class LeaderPhrase
{
    private static readonly string[] Qualifiers = { " — ", " (", ": " };

    /// <summary>
    /// The naming half of a leader description: everything up to the first
    /// qualifier, trailing punctuation removed. Returns the input unchanged
    /// when there is no qualifier, and never returns empty — a description
    /// that is nothing but a qualifier comes back whole rather than blank,
    /// because saying the long form beats saying nothing.
    /// </summary>
    public static string Brief(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return description;

        int cut = -1;
        foreach (string q in Qualifiers)
        {
            int at = description.IndexOf(q, StringComparison.Ordinal);
            if (at >= 0 && (cut < 0 || at < cut)) cut = at;
        }
        if (cut < 0) return description.Trim();

        string head = description.Substring(0, cut).TrimEnd(' ', ',', ';', ':', '.', '—', '-');
        return head.Length == 0 ? description.Trim() : head;
    }
}
