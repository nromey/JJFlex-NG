// JJFlexUpdaterHelper — Sprint 29 Track M
//
// A standalone exe that performs the file-replacement phase of a JJFlex update.
// Track D's in-app updater stages new files into a temp dir, writes a
// handoff-manifest.json, and launches this helper with the staging-dir path as
// its single argument. This helper waits for JJF to exit, backs up the current
// install, atomically replaces files, deletes obsolete files, and relaunches
// JJF — rolling back on any failure.
//
// See TRACK-INSTRUCTIONS.md (sibling worktree at C:\dev\jjflex-updater-helper)
// for the full design contract.

using System.Reflection;

namespace JJFlexUpdaterHelper;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailureRolledBack = 10;
    private const int ExitFailureNotRolledBack = 11;
    private const int ExitUsage = 64;
    private const int ExitManifestError = 65;
    private const int ExitJjfStillRunning = 66;
    private const int ExitAlreadyRunning = 67;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: JJFlexUpdaterHelper.exe <staging-dir>");
            return ExitUsage;
        }

        var stagingDir = args[0];

        // Single-instance guard goes first — a second helper trying to update
        // the same install in parallel must back off cleanly without ever
        // touching the staging dir or the helper.log of the running instance.
        using var guard = SingleInstanceGuard.TryAcquire();
        if (!guard.IsOwner)
        {
            Console.Error.WriteLine(
                "another JJFlexUpdaterHelper instance is already running; aborting.");
            return ExitAlreadyRunning;
        }

        // Open the helper.log alongside the staging dir if it exists. If the
        // staging dir doesn't exist (or isn't writable), the logger silently
        // falls back to console-only and the manifest-load below produces
        // the right ExitManifestError.
        var logPath = Directory.Exists(stagingDir)
            ? Path.Combine(stagingDir, "helper.log")
            : null;

        using var logger = new Logger(logPath);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
        logger.Info($"JJFlexUpdaterHelper v{version} start, staging-dir={stagingDir}");

        var exitCode = RunUpdate(stagingDir, logger);
        logger.Info($"JJFlexUpdaterHelper exit code={exitCode}");
        return exitCode;
    }

    private static int RunUpdate(string stagingDir, Logger logger)
    {
        HandoffManifest manifest;
        try
        {
            manifest = ManifestLoader.Load(stagingDir);
        }
        catch (ManifestLoadException ex)
        {
            logger.Warn($"manifest error: {ex.Message}");
            return ExitManifestError;
        }

        EchoManifest(stagingDir, manifest, logger);

        var waitResult = JjfProcessWaiter.WaitForExit(
            manifest.JjfPid,
            JjfProcessWaiter.DefaultTimeout,
            logger.Info);

        if (waitResult == JjfProcessWaiter.WaitOutcome.TimedOut)
        {
            logger.Warn(
                $"JJF pid {manifest.JjfPid} is still running after timeout; aborting before any file change. Staging dir preserved at: {stagingDir}");
            return ExitJjfStillRunning;
        }

        // ---------- Backup phase (no install state mutated yet) ----------
        BackupResult backup;
        try
        {
            backup = BackupStep.Execute(manifest, logger.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.Warn($"backup failed: {ex.Message}");
            logger.Warn($"Aborting before any file change. Install is untouched. Staging dir preserved at: {stagingDir}");
            return ExitFailureNotRolledBack;
        }

        // ---------- Replace + Delete phases (rollback on any failure) ----------
        try
        {
            ReplaceStep.Execute(manifest, logger.Info);
            DeleteStep.Execute(manifest, logger.Info);
        }
        catch (Exception ex) when (
            ex is FileReplaceException or FileDeleteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.Warn($"update failed mid-flight: {ex.Message}");

            if (!manifest.RollbackOnAnyFailure)
            {
                logger.Warn($"rollback_on_any_failure=false; leaving install in current state. Staging dir preserved at: {stagingDir}");
                return ExitFailureNotRolledBack;
            }

            var rollback = RollbackStep.Execute(manifest, backup, logger.Info, logger.Warn);
            if (rollback.WasFullyRolledBack)
            {
                logger.Warn($"Rolled back to pre-update state. Update did not apply. Staging dir preserved at: {stagingDir}");
                return ExitFailureRolledBack;
            }

            logger.Warn(
                $"Rollback was incomplete: {rollback.FilesFailedToRestore} files could not be restored. " +
                $"Backup remains at {manifest.BackupDir} for manual recovery. Staging dir preserved at: {stagingDir}");
            return ExitFailureNotRolledBack;
        }

        logger.Info("Update applied successfully.");
        RelaunchStep.Execute(manifest.JjfRelaunchPath, logger.Info);

        // helper.log lives inside stagingDir; close the file handle before
        // CleanupStep tries to delete the dir or Windows will refuse the
        // recursive Directory.Delete on the open file.
        logger.CloseFileLog();
        CleanupStep.Execute(stagingDir, logger.Info);
        return ExitOk;
    }

    private static void EchoManifest(string stagingDir, HandoffManifest manifest, Logger logger)
    {
        logger.Info($"JJFlexUpdaterHelper: staging-dir={stagingDir}");
        logger.Info($"  source_dir       = {manifest.SourceDir}");
        logger.Info($"  target_dir       = {manifest.TargetDir}");
        logger.Info($"  backup_dir       = {manifest.BackupDir}");
        logger.Info($"  jjf_pid          = {manifest.JjfPid}");
        logger.Info($"  jjf_relaunch     = {manifest.JjfRelaunchPath}");
        logger.Info($"  copy_files count = {manifest.CopyFiles.Count}");
        logger.Info($"  delete_files     = {manifest.DeleteFiles.Count}");
        logger.Info($"  rollback         = {manifest.RollbackOnAnyFailure}");
    }
}
