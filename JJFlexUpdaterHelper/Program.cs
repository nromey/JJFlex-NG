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
        Console.Out.WriteLine($"JJFlexUpdaterHelper: staging-dir={stagingDir}");
        Console.Out.WriteLine("(scaffold — implementation lands in subsequent commits)");
        return ExitOk;
    }
}
