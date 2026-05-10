namespace JJFlexUpdaterHelper;

internal sealed record DeletedFile(string RelPath, string TargetPath);

// Step 6 of the helper flow. For every entry in delete_files, remove the
// corresponding file from target_dir.
//
// Best-effort semantics:
//   - File doesn't exist → success (it was already gone, nothing to do).
//   - File deleted cleanly → success, recorded for rollback (recreate from
//     backup if step 7 fires).
//   - File locked / denied → throw FileDeleteException; orchestrator rolls
//     back. JJF should be exited by this point (step 3 gate), so a locked
//     file usually means an unrelated process has it open.
internal static class DeleteStep
{
    public static List<DeletedFile> Execute(HandoffManifest manifest, Action<string> log)
    {
        var deleted = new List<DeletedFile>();

        foreach (var rel in manifest.DeleteFiles)
        {
            var target = PathGuard.SafeJoin(manifest.TargetDir, rel);
            if (!File.Exists(target))
            {
                log($"  delete: {rel} - already absent, skipping");
                continue;
            }

            try
            {
                File.Delete(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FileDeleteException($"failed to delete '{rel}': {ex.Message}", ex);
            }

            deleted.Add(new DeletedFile(rel, target));
            log($"  delete: {rel} -> removed");
        }

        log($"Deletion complete: {deleted.Count} files removed.");
        return deleted;
    }
}

public sealed class FileDeleteException : Exception
{
    public FileDeleteException() { }
    public FileDeleteException(string message) : base(message) { }
    public FileDeleteException(string message, Exception innerException) : base(message, innerException) { }
}
