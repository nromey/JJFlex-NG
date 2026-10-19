using System;
using System.Globalization;

namespace Radios.Listenback;

internal static class ListenbackRestoreEvidence
{
    internal static bool Matches(SavedRadioSetting setting, string raw)
    {
        int bar = raw.IndexOf('|');
        if (bar < 0 || !raw.StartsWith("S", StringComparison.Ordinal)) return false;
        string body = raw[(bar + 1)..];
        string[] expected = setting.StatusPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] actual = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (expected.Length == 0 || actual.Length <= expected.Length) return false;
        for (int i = 0; i < expected.Length; ++i)
        {
            if (string.Equals(expected[i], actual[i], StringComparison.OrdinalIgnoreCase)) continue;
            if (!expected[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                !actual[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                !uint.TryParse(expected[i][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint e) ||
                !uint.TryParse(actual[i][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint a) || a != e) return false;
        }
        if (setting.StatusKey == "") return Array.IndexOf(actual, "removed") >= 0 || Array.IndexOf(actual, "in_use=0") >= 0;
        if (!RadioStatusTap.Values(body).TryGetValue(setting.StatusKey, out string value)) return false;
        if (value == setting.Value) return true;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) &&
            double.TryParse(setting.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double prior) && v == prior;
    }
}
