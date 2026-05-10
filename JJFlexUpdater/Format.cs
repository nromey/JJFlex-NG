using System.Globalization;

namespace JJFlexUpdater;

/// <summary>
/// Tiny shared formatting helpers. Kept centralized so progress strings,
/// dialog labels, and trace lines all describe sizes the same way.
/// Screen-reader output favors short forms ("12 MB" not "12,288 KB"), so
/// we round to the most natural unit at the byte boundary.
/// </summary>
public static class Format
{
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public static string SavingsPercent(long deltaBytes, long fullBytes)
    {
        if (fullBytes <= 0) return "0%";
        double pct = 100.0 * (1.0 - ((double)deltaBytes / fullBytes));
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;
        return pct.ToString("F0", CultureInfo.InvariantCulture) + "%";
    }
}
