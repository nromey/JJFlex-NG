using System.ComponentModel;
using System.Diagnostics;

namespace JJFlexUpdaterHelper;

// Step 8 of the helper flow. After files are replaced and obsolete files
// deleted, start the new JJFlexRadio.exe. UseShellExecute=true so the new
// JJF gets its own process tree (matches launching from Explorer or the
// Start Menu) and is unaffected by the helper's exit.
//
// A relaunch failure does NOT trigger rollback. The update has already
// succeeded; the worst case is the user launches JJF manually from the
// Start Menu. We log the failure for forensic review and exit normally.
internal static class RelaunchStep
{
    public static void Execute(string relaunchPath, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(relaunchPath))
        {
            log("Relaunch: jjf_relaunch_path is empty; skipping relaunch.");
            return;
        }

        if (!File.Exists(relaunchPath))
        {
            log($"Relaunch: target '{relaunchPath}' does not exist after update; skipping relaunch (user can launch JJF manually).");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = relaunchPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(relaunchPath) ?? string.Empty,
        };

        try
        {
            using var proc = Process.Start(startInfo);
            var pidText = proc?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
            log($"Relaunch: started JJF (pid {pidText}).");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            log($"Relaunch FAILED: {ex.Message}. Update applied; user can launch JJF manually.");
        }
    }
}
