using System.Security.Cryptography;

namespace JJFlexUpdaterHelper;

internal sealed record ReplacedFile(string RelPath, string TargetPath, string IntermediateNewPath);

// Step 5 of the helper flow. For every entry in copy_files:
//   1. sha256 the staged source file and verify against the manifest's
//      expected_sha256. A mismatch means the staging dir is corrupt; abort
//      now (before any rename) so we can roll back cleanly.
//   2. Copy source → "<target>.new" on the same volume (so the rename in
//      step 3 is an atomic NTFS metadata op, not a cross-volume copy).
//   3. Atomic rename "<target>.new" → "<target>" via File.Move(..., overwrite: true).
//
// The .new + rename pattern is the load-bearing crash-safety property: a
// crash between step 2 and step 3 leaves "<target>.new" on disk while
// "<target>" still holds the prior good content. JJF on restart loads
// "<target>" (the old version) and ignores .new files, so a partial run
// never produces a bricked install.
internal static class ReplaceStep
{
    public static List<ReplacedFile> Execute(HandoffManifest manifest, Action<string> log)
    {
        var replaced = new List<ReplacedFile>();

        foreach (var entry in manifest.CopyFiles)
        {
            var source = PathGuard.SafeJoin(manifest.SourceDir, entry.RelPath);
            if (!File.Exists(source))
            {
                throw new FileReplaceException(
                    $"copy_files entry '{entry.RelPath}' not present in source_dir '{manifest.SourceDir}'");
            }

            VerifySha256(source, entry.ExpectedSha256, entry.RelPath);

            var target = PathGuard.SafeJoin(manifest.TargetDir, entry.RelPath);
            var newPath = target + ".new";

            var targetParent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetParent))
            {
                Directory.CreateDirectory(targetParent);
            }

            // Stage on the same volume as target so the subsequent rename is atomic.
            File.Copy(source, newPath, overwrite: true);

            // overwrite: true is fine because JJF has exited (step 3 gate).
            File.Move(newPath, target, overwrite: true);

            replaced.Add(new ReplacedFile(entry.RelPath, target, newPath));
            log($"  replace: {entry.RelPath} -> {target}");
        }

        log($"Replacement complete: {replaced.Count} files installed.");
        return replaced;
    }

    private static void VerifySha256(string path, string expectedHex, string relPath)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        var actual = Convert.ToHexString(hash);

        if (!actual.Equals(expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileReplaceException(
                $"sha256 mismatch for '{relPath}': expected {expectedHex.ToLowerInvariant()} got {actual.ToLowerInvariant()}");
        }
    }
}

public sealed class FileReplaceException : Exception
{
    public FileReplaceException() { }
    public FileReplaceException(string message) : base(message) { }
    public FileReplaceException(string message, Exception innerException) : base(message, innerException) { }
}
