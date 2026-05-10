namespace JJFlexUpdater;

/// <summary>
/// Centralized endpoints for the updater. Constants live here so a test
/// or staging deploy only has to override one place. Production target is
/// the JJ Flexible Data Provider per memory/project_jjflex_data_provider.md.
/// </summary>
public static class UpdaterEndpoints
{
    /// <summary>
    /// Top-level app manifest. Lists per-channel current versions and points
    /// at per-version file manifests. Schema in
    /// <see cref="Manifest.AppManifest"/>.
    /// </summary>
    public const string AppManifestUrl =
        "https://data.jjflexible.radio/jjflex-app-manifest.json";

    /// <summary>
    /// HTTP user-agent sent on all updater requests. Lets the data provider
    /// distinguish updater traffic from browser traffic in access logs and
    /// gives Noel a one-grep way to see which JJF versions are checking in.
    /// Format: "JJFlexRadio-Updater/{appVersion} ({platform})"
    /// where platform is win-x64 / win-x86.
    /// </summary>
    public static string BuildUserAgent(string appVersion, string platform)
        => $"JJFlexRadio-Updater/{appVersion} ({platform})";
}
