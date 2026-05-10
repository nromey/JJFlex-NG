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
    private const int ExitUsage = 64;
    private const int ExitManifestError = 65;
    private const int ExitFailureRolledBack = 10;
    private const int ExitFailureNotRolledBack = 11;

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

        Console.Out.WriteLine($"JJFlexUpdaterHelper: staging-dir={stagingDir}");
        Console.Out.WriteLine($"  source_dir       = {manifest.SourceDir}");
        Console.Out.WriteLine($"  target_dir       = {manifest.TargetDir}");
        Console.Out.WriteLine($"  backup_dir       = {manifest.BackupDir}");
        Console.Out.WriteLine($"  jjf_pid          = {manifest.JjfPid}");
        Console.Out.WriteLine($"  jjf_relaunch     = {manifest.JjfRelaunchPath}");
        Console.Out.WriteLine($"  copy_files count = {manifest.CopyFiles.Count}");
        Console.Out.WriteLine($"  delete_files     = {manifest.DeleteFiles.Count}");
        Console.Out.WriteLine($"  rollback         = {manifest.RollbackOnAnyFailure}");
        Console.Out.WriteLine("(further phases land in subsequent commits)");
        return ExitOk;
    }
}
