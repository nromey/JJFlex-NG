namespace JJFlex.RecordIndex.Indexing;

/// <summary>
/// Which files of the record are indexed. The policy, in one place:
///
/// - Every .zip, anywhere in the record.
/// - Loose text files (.txt .log .md .json .jsonl): build NOTES, NVDA logs, stats.
/// - NOT dev-mirror: a single rolling copy of C:\dev, overwritten each seal. It
///   is not history, it is mostly git repositories that have their own history,
///   and it is larger than everything else put together.
/// - NOT dev-orphans (a loose source tree), old (installers only), or the
///   firmware extraction under research (vendor files; see its own notes).
/// </summary>
public static class RecordScanner
{
    public static readonly string[] ExcludedTopFolders = ["dev-mirror", "dev-orphans", "old"];

    private static readonly string[] LooseText = [".txt", ".log", ".md", ".json", ".jsonl"];

    public static IEnumerable<string> Enumerate(string recordRoot)
    {
        var root = new DirectoryInfo(recordRoot);
        if (!root.Exists) throw new DirectoryNotFoundException($"The record is not reachable at {recordRoot}. Is the NAS online?");
        var files = new List<string>();
        Walk(root, root, files);
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    public static bool Included(string recordRoot, string fullPath)
    {
        var rel = Path.GetRelativePath(recordRoot, fullPath);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return false;
        var top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (ExcludedTopFolders.Contains(top, StringComparer.OrdinalIgnoreCase)) return false;
        if (rel.Contains("firmware-extraction", StringComparison.OrdinalIgnoreCase)) return false;
        var ext = Path.GetExtension(fullPath);
        return ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) || LooseText.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static void Walk(DirectoryInfo root, DirectoryInfo dir, List<string> into)
    {
        foreach (var f in dir.EnumerateFiles())
            if (Included(root.FullName, f.FullName)) into.Add(f.FullName);
        foreach (var d in dir.EnumerateDirectories())
        {
            var rel = Path.GetRelativePath(root.FullName, d.FullName);
            var top = rel.Split(Path.DirectorySeparatorChar)[0];
            if (ExcludedTopFolders.Contains(top, StringComparer.OrdinalIgnoreCase)) continue;
            if (rel.Contains("firmware-extraction", StringComparison.OrdinalIgnoreCase)) continue;
            Walk(root, d, into);
        }
    }
}
