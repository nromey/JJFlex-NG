using System.Diagnostics;

namespace JJFlexUpdater.Staging;

/// <summary>
/// Spawns the Track M updater-helper exe with the staging dir as its sole
/// argument. <c>UseShellExecute=true</c> + <c>CreateNoWindow=true</c> means
/// the helper survives JJF's Application.Exit and runs as a detached
/// process; without that, killing JJF would also kill the helper before
/// it can finish swapping files.
///
/// The helper's name and search order:
///   1. <c>%installDir%\JJFlexUpdaterHelper.exe</c>  (production layout)
///   2. fallback path supplied via <see cref="LaunchOptions.HelperOverridePath"/>
///      so a tester or a development build can point at a worktree exe
///
/// Track M ships the helper into the same install dir as JJFlexRadio.exe,
/// so the production lookup is just sibling-path resolution.
/// </summary>
public static class HelperLauncher
{
    public const string DefaultHelperFileName = "JJFlexUpdaterHelper.exe";

    public sealed class LaunchOptions
    {
        public string? HelperOverridePath { get; init; }
        public string? InstallDir { get; init; }
    }

    public static string ResolveHelperPath(LaunchOptions? options = null)
    {
        if (!string.IsNullOrEmpty(options?.HelperOverridePath)
            && File.Exists(options.HelperOverridePath))
        {
            return options.HelperOverridePath;
        }

        string installDir = options?.InstallDir
            ?? AppContext.BaseDirectory
            ?? throw new InvalidOperationException(
                "Cannot resolve install dir for helper exe lookup");
        return Path.Combine(installDir, DefaultHelperFileName);
    }

    /// <summary>
    /// Start the helper. Returns the spawned process so the caller can
    /// inspect Id for diagnostic logging; the caller is then expected to
    /// call <see cref="System.Windows.Forms.Application.Exit()"/> (or its
    /// WPF equivalent) so the helper can take over the file layout.
    ///
    /// Throws <see cref="HelperLaunchException"/> if the helper exe is not
    /// found or fails to start. Caller falls back to the full-bundle path
    /// when this happens.
    /// </summary>
    public static Process LaunchAndDetach(StagingDir staging, LaunchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(staging);

        string helperPath = ResolveHelperPath(options);
        if (!File.Exists(helperPath))
        {
            throw new HelperLaunchException(
                helperPath,
                $"updater helper exe not found at {helperPath}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = helperPath,
            Arguments = '"' + staging.Root + '"',
            UseShellExecute = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(helperPath)!,
        };

        try
        {
            var proc = Process.Start(psi)
                ?? throw new HelperLaunchException(helperPath,
                    "Process.Start returned null for the helper exe");
            return proc;
        }
        catch (Exception ex) when (ex is not HelperLaunchException)
        {
            throw new HelperLaunchException(helperPath,
                $"failed to launch helper: {ex.Message}", ex);
        }
    }
}

public sealed class HelperLaunchException : Exception
{
    public string HelperPath { get; }

    public HelperLaunchException(string helperPath, string message)
        : base(message)
    {
        HelperPath = helperPath;
    }

    public HelperLaunchException(string helperPath, string message, Exception inner)
        : base(message, inner)
    {
        HelperPath = helperPath;
    }
}
