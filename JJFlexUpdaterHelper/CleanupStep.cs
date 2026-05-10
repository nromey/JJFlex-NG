namespace JJFlexUpdaterHelper;

// Step 9 of the helper flow. Removes the staging dir (manifest + files +
// backup) on success.
//
// On failure paths (rolled-back or not-rolled-back), the staging dir is
// preserved deliberately — the operator and helper.log inside it are the
// forensic record we want to keep around. Cleanup is best-effort: a
// permission error or in-use file under staging produces a warning, not
// a hard failure (the update already succeeded).
internal static class CleanupStep
{
    public static void Execute(string stagingDir, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(stagingDir))
        {
            log("Cleanup: staging dir path is empty; nothing to remove.");
            return;
        }

        if (!Directory.Exists(stagingDir))
        {
            log($"Cleanup: staging dir already gone: {stagingDir}");
            return;
        }

        try
        {
            Directory.Delete(stagingDir, recursive: true);
            log($"Cleanup: removed staging dir {stagingDir}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Cleanup FAILED (best-effort): {ex.Message}. Staging dir: {stagingDir}");
        }
    }
}
