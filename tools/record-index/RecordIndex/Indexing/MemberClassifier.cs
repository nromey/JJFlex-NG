namespace JJFlex.RecordIndex.Indexing;

public enum Treatment { Text, Jsonl, Html, Sniff, Skip }

/// <summary>Decides what a member is and how (or whether) to read its text.</summary>
public static class MemberClassifier
{
    private static readonly HashSet<string> Binary = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".so", ".dylib", ".lib", ".obj", ".winmd", ".nupkg", ".msi", ".cab",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff",
        ".wav", ".mp3", ".ogg", ".flac", ".opus", ".m4a", ".aac", ".wma", ".mid",
        ".mp4", ".mkv", ".avi", ".mov", ".webm",
        ".pdf", ".chm", ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt",
        ".dmp", ".db", ".sqlite", ".sqlite3", ".db-wal", ".db-shm", ".bin", ".dat", ".onnx", ".pt", ".gguf",
        ".ttf", ".otf", ".woff", ".woff2", ".pyc", ".class", ".jar", ".etl", ".blf", ".regtrans-ms",
    };

    private static readonly HashSet<string> Archives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".gz", ".7z", ".rar", ".tar", ".tgz", ".bz2", ".xz", ".zst",
    };

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".xml", ".config", ".ini", ".toml", ".yaml", ".yml", ".csv", ".tsv",
        ".cs", ".vb", ".py", ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".js", ".ts", ".css", ".xaml", ".resx",
        ".csproj", ".vbproj", ".props", ".targets", ".sln", ".nsi", ".trx", ".adi", ".adif", ".sql", ".rst", ".diff", ".patch",
        ".editorconfig", ".gitignore", ".gitattributes",
    };

    public static (string Kind, Treatment How, string? Note) Classify(string archiveKind, string memberPath)
    {
        var name = memberPath.Replace('\\', '/');
        var file = name[(name.LastIndexOf('/') + 1)..];
        var ext = Path.GetExtension(file);

        if (Archives.Contains(ext)) return ("nested-archive", Treatment.Skip, "an archive inside the archive; not opened");
        if (Binary.Contains(ext)) return ("binary", Treatment.Skip, "binary");

        if (ext.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            return (name.Contains("/projects/", StringComparison.OrdinalIgnoreCase) ? "transcript" : "jsonl", Treatment.Jsonl, null);
        if (ext.Equals(".htm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".html", StringComparison.OrdinalIgnoreCase))
            return ("html", Treatment.Html, null);

        string kind =
            file.StartsWith("JJFlexRadio", StringComparison.OrdinalIgnoreCase) && file.Contains("Trace", StringComparison.OrdinalIgnoreCase) ? "trace" :
            file.Contains("nvda", StringComparison.OrdinalIgnoreCase) && ext.Equals(".log", StringComparison.OrdinalIgnoreCase) ? "nvda-log" :
            archiveKind == "memory" && ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ? "memory-entry" :
            file.StartsWith("NOTES", StringComparison.OrdinalIgnoreCase) || file.StartsWith("BUILD-INFO", StringComparison.OrdinalIgnoreCase) ? "build-notes" :
            ext.ToLowerInvariant() switch
            {
                ".md" => "markdown",
                ".txt" => "text",
                ".log" => "log",
                ".trx" => "test-results",
                ".json" or ".xml" or ".config" or ".ini" or ".toml" or ".yaml" or ".yml" => "config",
                ".cs" or ".vb" or ".py" or ".ps1" or ".psm1" or ".bat" or ".cmd" or ".sh" or ".js" or ".ts" or ".css" or ".xaml"
                    or ".csproj" or ".vbproj" or ".props" or ".targets" or ".sln" or ".nsi" or ".sql" => "source",
                _ => "text",
            };
        return (kind, TextExt.Contains(ext) ? Treatment.Text : Treatment.Sniff, null);
    }

    /// <summary>
    /// True when the first bytes look like text: no NUL bytes (unless UTF-16 with
    /// a byte-order mark), and not dominated by control characters.
    /// </summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF))) return true;
        int control = 0;
        foreach (var b in head)
        {
            if (b == 0) return false;
            if (b < 0x09 || (b > 0x0D && b < 0x20)) control++;
        }
        return control * 20 < Math.Max(1, head.Length);
    }

    /// <summary>The archive's kind, from where it sits in the record.</summary>
    public static string ArchiveKind(string relativePath)
    {
        var parts = relativePath.Replace('/', '\\').Split('\\');
        var top = parts[0].ToLowerInvariant();
        var file = parts[^1];
        if (System.Text.RegularExpressions.Regex.IsMatch(top, @"^\d+\.\d+\.\d+\.\d+$"))
            return file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "build" : "build-notes";
        return top switch
        {
            "memory" => "memory",
            "claude-state" => "claude-state",
            "private" => "private",
            "appdata" => "appdata",
            "nvda" or "nvda-logs" => "nvda-log",
            "stats" => "stats",
            "research" => "research",
            _ => "other",
        };
    }
}
