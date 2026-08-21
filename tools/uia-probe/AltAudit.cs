using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace JJFlex.UiaProbe;

/// <summary>
/// The static half of the Alt-chord trap check.
///
/// <para><b>The bug.</b> When Alt is held, WPF does not report the letter in
/// <c>KeyEventArgs.Key</c>. It reports <c>Key.System</c> and puts the real key
/// in <c>e.SystemKey</c>. So a handler written as
/// <c>if (e.Key == Key.L &amp;&amp; alt)</c> can never be true. On 2026-08-13
/// exactly that shipped: an Alt+L binding that compiled, reviewed clean, and
/// was simply never handled — the screen reader read the focused control and
/// the key appeared to do nothing.</para>
///
/// <para>Pressing the key catches this. So does reading for it, and the two
/// catch different instances: a dynamic sweep only tests bindings it can reach,
/// while this reaches every file. Belt and braces on a bug that has already
/// shipped once.</para>
///
/// <para><b>Scope, stated honestly.</b> This is a line-window heuristic, not a
/// compiler. It answers "which files reason about Alt near a raw
/// <c>e.Key</c> comparison without ever mentioning <c>e.SystemKey</c>" — which
/// is the shape of the bug — and every hit needs a human to confirm. It does
/// not follow the key through helper methods, and a handler that normalises in
/// a different file will read here as a suspect.</para>
/// </summary>
internal static class AltAudit
{
    private sealed record Hit(string File, int Line, string Text, string Why);

    private static readonly Regex AltMention =
        new(@"ModifierKeys\.Alt|Keys\.Alt\b|Key\.LeftAlt|Key\.RightAlt|Modifiers\s*=\s*""Alt", RegexOptions.Compiled);

    private static readonly Regex RawKeyCompare =
        new(@"\be\.Key\s*==\s*Key\.|switch\s*\(\s*e\.Key\s*\)", RegexOptions.Compiled);

    private const int Window = 40;

    public static string Run(string root)
    {
        var suspects = new List<Hit>();
        var acquitted = new List<string>();
        var altBearing = new List<string>();
        int scanned = 0;

        foreach (string file in EnumerateSources(root))
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (IOException) { continue; }
            scanned++;

            bool normalises = lines.Any(l => l.Contains("SystemKey", StringComparison.Ordinal));
            var altLines = new List<int>();
            for (int i = 0; i < lines.Length; i++)
                if (AltMention.IsMatch(lines[i])) altLines.Add(i);

            if (altLines.Count == 0) continue;
            altBearing.Add(Rel(root, file) + (normalises ? " (normalises through SystemKey)" : " (never mentions SystemKey)"));

            if (normalises) { acquitted.Add(Rel(root, file)); continue; }

            foreach (int i in altLines)
            {
                int lo = Math.Max(0, i - Window), hi = Math.Min(lines.Length - 1, i + Window);
                for (int j = lo; j <= hi; j++)
                {
                    if (!RawKeyCompare.IsMatch(lines[j])) continue;
                    suspects.Add(new Hit(Rel(root, file), j + 1, lines[j].Trim(),
                        $"reasons about Alt at line {i + 1} and compares e.Key directly at line {j + 1}, "
                        + "and this file never reads e.SystemKey"));
                    break;
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("Alt-chord trap audit (static)");
        sb.AppendLine();
        sb.AppendLine($"Scanned {scanned} source files under {root}.");
        sb.AppendLine($"{altBearing.Count} of them reason about the Alt modifier at all.");
        sb.AppendLine();

        sb.AppendLine("Files that mention Alt:");
        foreach (string f in altBearing.OrderBy(x => x, StringComparer.Ordinal)) sb.AppendLine($"- {f}");
        sb.AppendLine();

        if (suspects.Count == 0)
        {
            sb.AppendLine("No suspect sites. Every file that reasons about Alt near a raw e.Key comparison "
                + "also reads e.SystemKey somewhere.");
        }
        else
        {
            sb.AppendLine("Suspect sites — each needs a human to confirm, and each needs the key pressed:");
            foreach (Hit h in suspects)
                sb.AppendLine($"- {h.File} line {h.Line}: {h.Text}  ({h.Why})");
        }
        sb.AppendLine();
        sb.AppendLine("Reminder: a clean result here is not proof. It says no file has the SHAPE of the bug. "
            + "Only pressing the key proves the chord arrives.");
        return sb.ToString();
    }

    private static IEnumerable<string> EnumerateSources(string root)
    {
        foreach (string pattern in new[] { "*.cs", "*.xaml", "*.vb" })
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (string f in files)
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                if (f.EndsWith(".g.cs", StringComparison.Ordinal)) continue;
                if (f.EndsWith(".g.i.cs", StringComparison.Ordinal)) continue;
                yield return f;
            }
        }
    }

    private static string Rel(string root, string file) =>
        file.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? file[root.Length..].TrimStart('\\', '/') : file;
}
