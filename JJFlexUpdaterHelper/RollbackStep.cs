namespace JJFlexUpdaterHelper;

internal sealed class RollbackResult
{
    public int FilesRestored { get; set; }
    public int FilesFailedToRestore { get; set; }
    public List<string> FailedRestores { get; } = [];
    public bool WasFullyRolledBack => FilesFailedToRestore == 0;
}

// Step 7 of the helper flow. Triggered by any failure during backup,
// replacement, or deletion. Restores the install to its pre-update state:
//
//   1. Copy backup_dir/<rel_path> back over target_dir/<rel_path> for every
//      file in BackupResult.CopyFileBackups (these were going to be
//      overwritten by step 5; restore the originals).
//   2. Copy backup_dir/<rel_path> back into target_dir/<rel_path> for every
//      file in BackupResult.DeleteFileBackups (these were going to be
//      removed by step 6; recreate them).
//   3. Sweep ".new" siblings of every copy_files target (a crash mid-replace
//      between File.Copy and File.Move leaves an orphan .new on disk; clean
//      it up so a future retry doesn't get confused).
//
// Every individual file restore is best-effort: a single failure is logged
// to a warning stream and counted, but the loop continues so the install
// gets restored as far as possible. The returned RollbackResult tells the
// orchestrator whether the rollback was fully clean (exit 10,
// ExitFailureRolledBack) or partially incomplete (exit 11,
// ExitFailureNotRolledBack — which signals "manual cleanup may be needed,
// look at backup_dir").
//
// IMPORTANT: this method MUST NOT throw. Any unexpected exception aborts
// further restoration and leaves the install in a worse state than if we
// had kept trying.
internal static class RollbackStep
{
    public static RollbackResult Execute(
        HandoffManifest manifest,
        BackupResult backup,
        Action<string> log,
        Action<string> warn)
    {
        var result = new RollbackResult();

        log("Rollback: starting restore from backup...");

        foreach (var b in backup.CopyFileBackups)
        {
            RestoreFromBackup(manifest.TargetDir, b, "copy", result, log, warn);
        }

        foreach (var b in backup.DeleteFileBackups)
        {
            RestoreFromBackup(manifest.TargetDir, b, "delete", result, log, warn);
        }

        foreach (var entry in manifest.CopyFiles)
        {
            string target;
            try { target = PathGuard.SafeJoin(manifest.TargetDir, entry.RelPath); }
            catch (Exception ex)
            {
                warn($"  cleanup: skipping orphan-.new for '{entry.RelPath}' ({ex.Message})");
                continue;
            }

            var newPath = target + ".new";
            if (!File.Exists(newPath)) continue;

            try
            {
                File.Delete(newPath);
                log($"  cleanup: removed orphan {newPath}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warn($"  cleanup FAILED: {newPath}: {ex.Message}");
                // Orphan-.new is not counted as a restore failure — it's
                // cosmetic; JJF won't load it. Surface the warning so the
                // user can clean up manually.
            }
        }

        log($"Rollback: complete ({result.FilesRestored} restored, {result.FilesFailedToRestore} failed).");
        return result;
    }

    private static void RestoreFromBackup(
        string targetDir,
        BackedUpFile b,
        string reason,
        RollbackResult result,
        Action<string> log,
        Action<string> warn)
    {
        try
        {
            var target = PathGuard.SafeJoin(targetDir, b.RelPath);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(b.BackupPath, target, overwrite: true);
            result.FilesRestored++;
            log($"  restore ({reason}): {b.RelPath} <- {b.BackupPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            result.FilesFailedToRestore++;
            result.FailedRestores.Add(b.RelPath);
            warn($"  restore ({reason}) FAILED: {b.RelPath}: {ex.Message}");
        }
    }
}
