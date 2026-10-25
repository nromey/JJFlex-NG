using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Radios.Listenback;

/// <summary>Creation fingerprints come from status text, never optimistic vendor setters.</summary>
internal static class ListenbackCreationEvidence
{
    internal static RecoveryResource Read(string kind, uint id, IEnumerable<string> statuses) => Read(kind, id, statuses, out _, out _);
    internal static RecoveryResource Read(string kind, uint id, IEnumerable<string> statuses, out string problem, out string[] lastLines)
    {
        var lines = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in statuses)
        {
            int bar = raw.IndexOf('|');
            if (bar < 0 || !raw.StartsWith("S", StringComparison.Ordinal)) continue;
            string[] words = raw[(bar + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int at = kind == "pan" ? 2 : 1;
            if (words.Length <= at || (kind == "pan" ? words[0] != "display" || words[1] != "pan" : words[0] != "slice") ||
                !Id(words[at], out uint found) || found != id) continue;
            lines.Add(raw);
            if (Array.IndexOf(words, "removed") >= 0 || Array.IndexOf(words, "in_use=0") >= 0) { values.Clear(); continue; }
            foreach (var pair in RadioStatusTap.Values(raw)) values[pair.Key] = pair.Value;
        }
        lastLines = lines.TakeLast(8).ToArray();
        var required = kind == "slice" ? new[] { "client_handle", "RF_frequency", "rxant", "pan", "mode" } : new[] { "client_handle", "center", "rxant" };
        problem = string.Join(",", required.Where(k => !values.ContainsKey(k)).Select(k => "missing-" + k));
        if (problem.Length != 0) return null;
        if (!Id(values["client_handle"], out uint owner)) { problem = "invalid-client_handle"; return null; }
        if (!double.TryParse(values[kind == "pan" ? "center" : "RF_frequency"], NumberStyles.Float,
            CultureInfo.InvariantCulture, out double frequency) || !double.IsFinite(frequency)) { problem = "invalid-frequency"; return null; }
        uint pan = id;
        string mode = kind == "slice" ? values["mode"] : "";
        string antenna = values["rxant"];
        if (kind == "slice" && !Id(values["pan"], out pan)) { problem = "invalid-pan"; return null; }
        return new(kind, id, owner, pan, frequency, mode, antenna);
    }

    private static bool Id(string text, out uint id) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)
        : uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
}
