using System.Diagnostics;

namespace JJFlexUpdater.Staging;

/// <summary>
/// Spawns the NSIS full-bundle installer in silent mode (<c>/S</c>) per
/// the Sprint 29 design memo. NSIS handles install + restart end to end;
/// JJF exits immediately after launch so the installer can replace files.
///
/// Silent install is gated on the same user-consent flow that runs the
/// delta path; this is not "force-install Chrome-style" — Noel's 2026-05-03
/// review explicitly rejected that. Silent here means "no per-step prompts
/// once the user has said yes to the update."
/// </summary>
public static class InstallerLauncher
{
    /// <summary>
    /// Start the installer detached. UseShellExecute=true keeps the
    /// installer alive after JJF exits. Returns the spawned process.
    /// </summary>
    public static Process LaunchSilentAndDetach(string installerPath)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Installer not found.", installerPath);

        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            // /S = silent install (NSIS convention).
            // /D=...   would force the install dir; we deliberately do NOT
            //          override it so the installer respects the original
            //          install location (Program Files vs Program Files (x86)).
            Arguments = "/S",
            UseShellExecute = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
        };

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException(
                $"Process.Start returned null for installer: {installerPath}");
        return proc;
    }
}
