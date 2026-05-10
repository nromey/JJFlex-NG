namespace JJFlexUpdaterHelper;

internal sealed record BackedUpFile(string RelPath, string BackupPath);

internal sealed class BackupResult
{
    // Backups of files that copy_files will overwrite.
    public List<BackedUpFile> CopyFileBackups { get; } = [];

    // Backups of files that delete_files will remove (so rollback can recreate them).
    public List<BackedUpFile> DeleteFileBackups { get; } = [];

    public IEnumerable<BackedUpFile> All => CopyFileBackups.Concat(DeleteFileBackups);
}

// Step 4 of the helper flow. Creates backup_dir and copies the current
// version of every touched file (anything in copy_files OR delete_files
// that exists today) into backup_dir/<rel_path>. Files that don't exist
// are silently skipped — they're either net-new (copy_files entry for a
// new dependency) or already gone (delete_files entry for a file the user
// or a prior failed update removed).
//
// If ANY copy operation fails, this step throws and the orchestrator must
// abort BEFORE step 5 starts touching the install. The rule that motivates
// this design: "Backup files preserved until success is confirmed, then
// cleaned. If anything goes wrong, restore from backup." A partial backup
// that gets followed by a partial replacement is the worst of both worlds —
// fail fast here, keep the install untouched.
internal static class BackupStep
{
    public static BackupResult Execute(HandoffManifest manifest, Action<string> log)
    {
        var result = new BackupResult();

        Directory.CreateDirectory(manifest.BackupDir);
        log($"Backup dir: {manifest.BackupDir}");

        foreach (var entry in manifest.CopyFiles)
        {
            var backedUp = TryBackup(manifest.TargetDir, entry.RelPath, manifest.BackupDir, "copy", log);
            if (backedUp is not null) result.CopyFileBackups.Add(backedUp);
        }

        foreach (var rel in manifest.DeleteFiles)
        {
            var backedUp = TryBackup(manifest.TargetDir, rel, manifest.BackupDir, "delete", log);
            if (backedUp is not null) result.DeleteFileBackups.Add(backedUp);
        }

        log($"Backup complete: {result.CopyFileBackups.Count} for-copy + {result.DeleteFileBackups.Count} for-delete = {result.CopyFileBackups.Count + result.DeleteFileBackups.Count} files preserved.");
        return result;
    }

    private static BackedUpFile? TryBackup(string targetDir, string relPath, string backupDir, string reason, Action<string> log)
    {
        var src = PathGuard.SafeJoin(targetDir, relPath);
        if (!File.Exists(src))
        {
            log($"  backup ({reason}): {relPath} — not present in install, skipping");
            return null;
        }

        var dst = PathGuard.SafeJoin(backupDir, relPath);
        var dstDir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dstDir))
        {
            Directory.CreateDirectory(dstDir);
        }

        // overwrite=true so a re-run after a failed prior attempt doesn't trip
        // on a stale backup; the staging dir is single-use anyway.
        File.Copy(src, dst, overwrite: true);
        log($"  backup ({reason}): {relPath} -> {dst}");
        return new BackedUpFile(relPath, dst);
    }
}
