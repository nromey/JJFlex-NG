using System.Text.RegularExpressions;
using System.IO;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>One delegate-shaped hook and what the codebase does with it.</summary>
public sealed record DelegateSurface(
    string Name,
    string DeclaredIn,
    int DeclaredAtLine,
    bool Invoked,
    bool Assigned,
    IReadOnlyList<string> InvokedFrom)
{
    /// <summary>
    /// Declared, called at a real callsite, and never given a value anywhere in
    /// the tree. The menu item exists, the key works, the call happens, and
    /// nothing occurs. This is the shape of the TX Controls defect.
    /// </summary>
    public bool IsDeadSurface => Invoked && !Assigned;
}

/// <summary>
/// Invariant 7, and the one check in this suite that reads source rather than
/// walking a tree.
///
/// <para><b>Why it is here at all.</b> The tree walk answers "is this dialog's
/// content real"; it cannot answer "does anything ever open this dialog". A hook
/// that is declared, invoked from a menu handler, and never assigned produces a
/// menu item that does nothing - and there is no window to walk, because no
/// window is ever created. Confirming that class needs the callsites, so this
/// check reads them.</para>
///
/// <para>It is written to be confirmation-grade rather than a grep: a surface is
/// only reported when a callsite exists AND no assignment exists anywhere in the
/// worktree, which is the difference between a defect and a rumour.</para>
/// </summary>
public static class DelegateSurfaceScan
{
    private static readonly string[] SearchRoots = { "JJFlexWpf", "Radios", "JJFlexControl", "JJFlexUpdater" };
    private static readonly string[] CallsiteRoots = { ".", };

    private static readonly Regex DeclarationPattern = new(
        @"^\s*public\s+(?:static\s+)?(?:required\s+)?(?:Action|Func)\b[^=;]*?\b(?<name>[A-Za-z_]\w*)\s*\{\s*get;\s*(?:private\s+|init\s+|protected\s+)?set;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FieldPattern = new(
        @"^\s*public\s+(?:static\s+)?(?:Action|Func)\b[^=;]*?\b(?<name>[A-Za-z_]\w*)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Lazy<IReadOnlyList<DelegateSurface>> LazyScan = new(Scan, isThreadSafe: true);

    public static IReadOnlyList<DelegateSurface> Surfaces => LazyScan.Value;

    private static IReadOnlyList<DelegateSurface> Scan()
    {
        var root = RepoPaths.Root;
        if (root == null) return Array.Empty<DelegateSurface>();

        var declarations = new List<(string Name, string File, int Line)>();
        foreach (var relative in SearchRoots)
        {
            var directory = Path.Combine(root, relative);
            if (!Directory.Exists(directory)) continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (IsGenerated(file)) continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = StripComment(lines[i]);
                    var match = DeclarationPattern.Match(line);
                    if (!match.Success) match = FieldPattern.Match(line);
                    if (!match.Success) continue;
                    declarations.Add((match.Groups["name"].Value, Relative(root, file), i + 1));
                }
            }
        }

        // One pass over every source file, so a hundred names cost one read each.
        var sources = new List<(string File, string[] Lines)>();
        foreach (var relative in CallsiteRoots)
        {
            var directory = Path.GetFullPath(Path.Combine(root, relative));
            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (extension is not (".cs" or ".vb" or ".xaml")) continue;
                if (IsGenerated(file) || IsVendor(root, file)) continue;
                sources.Add((Relative(root, file), File.ReadAllLines(file)));
            }
        }

        var results = new List<DelegateSurface>();
        foreach (var group in declarations.GroupBy(d => d.Name, StringComparer.Ordinal))
        {
            var name = group.Key;
            var assignPattern = new Regex($@"\b{Regex.Escape(name)}\s*(?:\+)?=(?!=)", RegexOptions.CultureInvariant);
            var invokePattern = new Regex($@"\b{Regex.Escape(name)}\s*(?:\?)?\s*(?:\.\s*Invoke\s*\(|\()", RegexOptions.CultureInvariant);

            var assigned = false;
            var invokedFrom = new List<string>();

            foreach (var (file, lines) in sources)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = StripComment(lines[i]);
                    if (line.Length == 0) continue;

                    if (!assigned && assignPattern.IsMatch(line) && !IsDeclarationLine(group, file, i + 1))
                        assigned = true;

                    if (invokePattern.IsMatch(line) && !IsDeclarationLine(group, file, i + 1))
                        invokedFrom.Add($"{file}:{i + 1}");
                }
            }

            var first = group.First();
            results.Add(new DelegateSurface(
                name,
                string.Join(", ", group.Select(d => $"{d.File}:{d.Line}").Distinct(StringComparer.Ordinal)),
                first.Line,
                invokedFrom.Count > 0,
                assigned,
                invokedFrom.Take(6).ToList()));
        }

        return results.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    private static bool IsDeclarationLine(IEnumerable<(string Name, string File, int Line)> declarations, string file, int line)
        => declarations.Any(d => string.Equals(d.File, file, StringComparison.OrdinalIgnoreCase) && d.Line == line);

    private static string StripComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        if (index >= 0) line = line[..index];
        index = line.IndexOf("'''", StringComparison.Ordinal);
        if (index >= 0) line = line[..index];
        return line.Trim();
    }

    private static bool IsGenerated(string file)
    {
        var name = Path.GetFileName(file);
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".Designer.vb", StringComparison.OrdinalIgnoreCase)
               || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
               || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVendor(string root, string file)
        => file.StartsWith(Path.Combine(root, "FlexLib_API"), StringComparison.OrdinalIgnoreCase)
           || file.StartsWith(Path.Combine(root, "P-Opus-master"), StringComparison.OrdinalIgnoreCase)
           || file.StartsWith(Path.Combine(root, "PortAudioSharp-src-0.19.3"), StringComparison.OrdinalIgnoreCase);

    private static string Relative(string root, string file)
        => Path.GetRelativePath(root, file).Replace('\\', '/');
}
