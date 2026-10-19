using System;
using System.Collections.Generic;
using System.Globalization;

namespace Radios.Listenback;

/// <summary>Creation fingerprints come from status text, never optimistic vendor setters.</summary>
internal static class ListenbackCreationEvidence
{
    internal static RecoveryResource Read(string kind, uint id, IEnumerable<string> statuses)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in statuses)
        {
            int bar = raw.IndexOf('|');
            if (bar < 0 || !raw.StartsWith("S", StringComparison.Ordinal)) continue;
            string[] words = raw[(bar + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int at = kind == "pan" ? 2 : 1;
            if (words.Length <= at || (kind == "pan" ? words[0] != "display" || words[1] != "pan" : words[0] != "slice") ||
                !Id(words[at], out uint found) || found != id) continue;
            if (Array.IndexOf(words, "removed") >= 0 || Array.IndexOf(words, "in_use=0") >= 0) { values.Clear(); continue; }
            foreach (var pair in RadioStatusTap.Values(raw)) values[pair.Key] = pair.Value;
        }
        if (!values.TryGetValue("client_handle", out string ownerText) || !Id(ownerText, out uint owner) ||
            !values.TryGetValue(kind == "pan" ? "center" : "RF_frequency", out string frequencyText) ||
            !double.TryParse(frequencyText, NumberStyles.Float, CultureInfo.InvariantCulture, out double frequency) ||
            !double.IsFinite(frequency) || !values.TryGetValue("rxant", out string antenna)) return null;
        uint pan = id;
        string mode = "";
        if (kind == "slice" && (!values.TryGetValue("pan", out string panText) || !Id(panText, out pan) ||
            !values.TryGetValue("mode", out mode))) return null;
        return new(kind, id, owner, pan, frequency, mode, antenna);
    }

    private static bool Id(string text, out uint id) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)
        : uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
}
