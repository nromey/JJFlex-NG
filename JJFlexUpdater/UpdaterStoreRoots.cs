namespace JJFlexUpdater;

/// <summary>
/// Where the updater keeps what it writes between runs: its preferences
/// (<see cref="UpdaterSettings"/>, normally <c>%APPDATA%\JJFlexRadio</c>) and
/// its install-hash cache (<see cref="Hashing.HashInventoryCache"/>, normally
/// <c>%LOCALAPPDATA%\JJFlexRadio</c>). On an ordinary run both stay exactly
/// where they always were. On a run that <c>JJFLEX_CONFIG_DIR</c> moved, and
/// whose move the settings resolver accepted, both follow the throwaway tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The updater does not reference Radios, so it built
/// both paths from the system folders itself, and CLAUDE.md listed it as
/// deliberately independent of the settings root because it "never runs in a
/// test". The testing manifest lever (<see cref="UpdaterEndpoints.TestAppManifestUrlVariable"/>)
/// made that run possible: an isolated harness run whose manifest fetch
/// succeeds reaches <see cref="UpdaterSettings.Save"/>, which would have
/// written the operator's live settings folder while the trace said the run
/// was isolated.
/// </para>
/// <para>
/// <b>Set once, at start-up, by the app</b>, from
/// <c>Radios.RadioConfig.AppDataRoot</c> and <c>Radios.LocalDataRoot.Folder</c>,
/// behind the same gate as the manifest lever
/// (<c>Radios.LocalDataRoot.RunRelocatedByConfigDir</c>), and before anything
/// can read or save the updater's settings. Nothing else moves it.
/// </para>
/// </remarks>
public static class UpdaterStoreRoots
{
    private static string? _settingsFolder;
    private static string? _localFolder;

    /// <summary>The folder <see cref="UpdaterSettings"/> reads and writes in
    /// instead of <c>%APPDATA%\JJFlexRadio</c>; null on an ordinary run.</summary>
    public static string? SettingsFolder => Volatile.Read(ref _settingsFolder);

    /// <summary>The folder the install-hash cache lives in instead of
    /// <c>%LOCALAPPDATA%\JJFlexRadio</c>; null on an ordinary run.</summary>
    public static string? LocalFolder => Volatile.Read(ref _localFolder);

    /// <summary>
    /// Decide where the updater's stores go, without touching anything. A run
    /// not moved by an accepted <c>JJFLEX_CONFIG_DIR</c> is no decision at all:
    /// false, both folders null, no line. A moved run takes both folders when
    /// each is a full path; otherwise it takes neither and the line says the
    /// ordinary folders are still in use.
    /// </summary>
    public static bool DecideRelocatedRoots(string? settingsRoot, string? localRoot,
        bool runRelocatedByConfigDir,
        out string? settingsFolder, out string? localFolder, out string? traceLine)
    {
        settingsFolder = null;
        localFolder = null;
        traceLine = null;
        if (!runRelocatedByConfigDir) return false;

        if (!IsFullPath(settingsRoot) || !IsFullPath(localRoot))
        {
            traceLine = "Updater: the run is isolated by JJFLEX_CONFIG_DIR, but its settings root ("
                + (settingsRoot ?? "none") + ") or local data root (" + (localRoot ?? "none")
                + ") is not a full path, so update preferences and the update hash cache stay in "
                + "the ordinary folders.";
            return false;
        }

        settingsFolder = settingsRoot!.Trim();
        localFolder = localRoot!.Trim();
        traceLine = "Updater: update preferences are kept at "
            + Path.Combine(settingsFolder, UpdaterSettings.DefaultFileName)
            + " and the update hash cache at "
            + Path.Combine(localFolder, Hashing.HashInventoryCache.DefaultFileName)
            + " (a run isolated by JJFLEX_CONFIG_DIR). The ordinary update-settings.json is not read or written.";
        return true;
    }

    /// <summary>
    /// Apply <see cref="DecideRelocatedRoots"/>: move both folders when it
    /// says so, and hand back its trace line, or null when there is nothing to
    /// say. Called once, at start-up.
    /// </summary>
    public static string? ApplyRelocatedRoots(string? settingsRoot, string? localRoot, bool runRelocatedByConfigDir)
    {
        if (DecideRelocatedRoots(settingsRoot, localRoot, runRelocatedByConfigDir,
                out string? settingsFolder, out string? localFolder, out string? traceLine))
        {
            Volatile.Write(ref _settingsFolder, settingsFolder);
            Volatile.Write(ref _localFolder, localFolder);
        }
        return traceLine;
    }

    private static bool IsFullPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path.Trim());
}
