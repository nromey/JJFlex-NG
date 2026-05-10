namespace JJFlexUpdaterHelper;

// Defense-in-depth against a malformed or hostile rel_path that tries to
// escape the target dir (e.g. "..\\..\\Windows\\System32\\evil.dll").
// Track D and Track M ship together so the helper trusts Track D in practice,
// but a path-traversal check is cheap and protects against any bug or
// future tampering of the staged manifest on disk.
internal static class PathGuard
{
    private static readonly char[] s_pathSeparators = ['/', '\\'];

    public static void RejectIfUnsafe(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath))
        {
            throw new ArgumentException("rel_path is empty", nameof(relPath));
        }

        if (Path.IsPathRooted(relPath))
        {
            throw new ArgumentException($"rel_path must not be rooted: '{relPath}'", nameof(relPath));
        }

        // Block any segment that's exactly ".." or contains the segment.
        // Path.GetFullPath would normalize these silently and let them through.
        var segments = relPath.Split(s_pathSeparators);
        foreach (var seg in segments)
        {
            if (seg == "..")
            {
                throw new ArgumentException($"rel_path must not contain '..' segments: '{relPath}'", nameof(relPath));
            }
        }
    }

    // Joins baseDir + relPath and asserts the result stays under baseDir.
    // Use for any path the helper is about to read/write so a malformed
    // rel_path can't reach outside the install dir or staging dir.
    public static string SafeJoin(string baseDir, string relPath)
    {
        RejectIfUnsafe(relPath);

        var fullBase = Path.GetFullPath(baseDir);
        var combined = Path.GetFullPath(Path.Combine(fullBase, relPath));

        var basePrefix = fullBase.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase) &&
            !combined.Equals(fullBase.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"rel_path '{relPath}' resolves outside base dir '{baseDir}' (would land at '{combined}')",
                nameof(relPath));
        }

        return combined;
    }
}
