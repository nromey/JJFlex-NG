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

namespace JJFlexUpdaterHelper;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailureRolledBack = 10;
    private const int ExitFailureNotRolledBack = 11;
    private const int ExitUsage = 64;
    private const int ExitManifestError = 65;
    private const int ExitJjfStillRunning = 66;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: JJFlexUpdaterHelper.exe <staging-dir>");
            return ExitUsage;
        }

        var stagingDir = args[0];

        HandoffManifest manifest;
        try
        {
            manifest = ManifestLoader.Load(stagingDir);
        }
        catch (ManifestLoadException ex)
        {
            Console.Error.WriteLine($"manifest error: {ex.Message}");
            return ExitManifestError;
        }

        EchoManifest(stagingDir, manifest);

        var waitResult = JjfProcessWaiter.WaitForExit(
            manifest.JjfPid,
            JjfProcessWaiter.DefaultTimeout,
            Console.Out.WriteLine);

        if (waitResult == JjfProcessWaiter.WaitOutcome.TimedOut)
        {
            Console.Error.WriteLine(
                $"JJF pid {manifest.JjfPid} is still running after timeout; aborting before any file change.");
            return ExitJjfStillRunning;
        }

        // ---------- Backup phase (no install state mutated yet) ----------
        BackupResult backup;
        try
        {
            backup = BackupStep.Execute(manifest, Console.Out.WriteLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"backup failed: {ex.Message}");
            Console.Error.WriteLine("Aborting before any file change. Install is untouched.");
            return ExitFailureNotRolledBack;
        }

        // ---------- Replace + Delete phases (rollback on any failure) ----------
        try
        {
            ReplaceStep.Execute(manifest, Console.Out.WriteLine);
            DeleteStep.Execute(manifest, Console.Out.WriteLine);
        }
        catch (Exception ex) when (
            ex is FileReplaceException or FileDeleteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"update failed mid-flight: {ex.Message}");

            if (!manifest.RollbackOnAnyFailure)
            {
                Console.Error.WriteLine("rollback_on_any_failure=false; leaving install in current state.");
                return ExitFailureNotRolledBack;
            }

            var rollback = RollbackStep.Execute(manifest, backup, Console.Out.WriteLine, Console.Error.WriteLine);
            if (rollback.WasFullyRolledBack)
            {
                Console.Error.WriteLine("Rolled back to pre-update state. Update did not apply.");
                return ExitFailureRolledBack;
            }

            Console.Error.WriteLine(
                $"Rollback was incomplete: {rollback.FilesFailedToRestore} files could not be restored. " +
                $"Backup remains at {manifest.BackupDir} for manual recovery.");
            return ExitFailureNotRolledBack;
        }

        Console.Out.WriteLine("Update applied successfully. (relaunch + cleanup land in subsequent commits)");
        return ExitOk;
    }

    private static void EchoManifest(string stagingDir, HandoffManifest manifest)
    {
        Console.Out.WriteLine($"JJFlexUpdaterHelper: staging-dir={stagingDir}");
        Console.Out.WriteLine($"  source_dir       = {manifest.SourceDir}");
        Console.Out.WriteLine($"  target_dir       = {manifest.TargetDir}");
        Console.Out.WriteLine($"  backup_dir       = {manifest.BackupDir}");
        Console.Out.WriteLine($"  jjf_pid          = {manifest.JjfPid}");
        Console.Out.WriteLine($"  jjf_relaunch     = {manifest.JjfRelaunchPath}");
        Console.Out.WriteLine($"  copy_files count = {manifest.CopyFiles.Count}");
        Console.Out.WriteLine($"  delete_files     = {manifest.DeleteFiles.Count}");
        Console.Out.WriteLine($"  rollback         = {manifest.RollbackOnAnyFailure}");
    }
}
