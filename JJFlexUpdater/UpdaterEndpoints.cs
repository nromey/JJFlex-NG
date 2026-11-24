namespace JJFlexUpdater;

/// <summary>
/// Centralized endpoints for the updater. Constants live here so a test
/// or staging deploy only has to override one place. Production target is
/// the JJ Flexible Data Provider per memory/project_jjflex_data_provider.md.
/// </summary>
public static class UpdaterEndpoints
{
    /// <summary>
    /// The production top-level app manifest. Lists per-channel current
    /// versions and points at per-version file manifests. Schema in
    /// <see cref="Manifest.AppManifest"/>.
    /// </summary>
    public const string ProductionAppManifestUrl =
        "https://data.jjflexible.radio/jjflex-app-manifest.json";

    private static string _appManifestUrl = ProductionAppManifestUrl;

    /// <summary>
    /// The app manifest this run fetches: <see cref="ProductionAppManifestUrl"/>,
    /// unless a test run moved it with <see cref="ApplyTestAppManifestUrl"/>.
    /// </summary>
    public static string AppManifestUrl => Volatile.Read(ref _appManifestUrl);

    /// <summary>
    /// The environment variable a test run uses to fetch the app manifest
    /// from a local HTTP server, so the harness can stage an update.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A testing lever, like <c>JJFLEX_CONFIG_DIR</c>: never a setting,
    /// never shown anywhere, never documented as a feature.</b> The app reads
    /// it only on a run whose settings tree <c>JJFLEX_CONFIG_DIR</c> moved and
    /// that move was accepted; an ordinary run never reads it at all.
    /// </para>
    /// <para>
    /// <b>It bounds where the manifest comes from, not where files are
    /// fetched.</b> Only plain <c>http</c> to <c>localhost</c> or
    /// <c>127.0.0.1</c> is taken, but the manifest's own
    /// <c>FileManifestUrl</c> and <c>FullInstallerUrl</c> are absolute, so a
    /// local manifest can still name any download. A staged manifest should
    /// point those at the same local server.
    /// </para>
    /// </remarks>
    public const string TestAppManifestUrlVariable = "JJFLEX_TEST_APP_MANIFEST_URL";

    /// <summary>
    /// Decide what the testing lever does, without touching anything: the URL
    /// to fetch the app manifest from, or null to keep the production one,
    /// and the trace line saying so. A run not moved by <c>JJFLEX_CONFIG_DIR</c>,
    /// or no URL offered, is no decision at all: null, with no line.
    /// </summary>
    public static string? DecideTestAppManifestUrl(string? offered, bool runRelocatedByConfigDir,
        out string? traceLine)
    {
        traceLine = null;
        if (!runRelocatedByConfigDir || string.IsNullOrWhiteSpace(offered)) return null;

        string candidate = offered.Trim();
        if (!IsLocalHttpUrl(candidate, out string why))
        {
            traceLine = "Updater: " + TestAppManifestUrlVariable + " refused (" + why
                + "); the app manifest comes from " + ProductionAppManifestUrl;
            return null;
        }
        traceLine = "Updater: TESTING app manifest in use at " + candidate + " (" + TestAppManifestUrlVariable
            + " is set on a run isolated by JJFLEX_CONFIG_DIR). This run does not read the production manifest.";
        return candidate;
    }

    /// <summary>
    /// Apply <see cref="DecideTestAppManifestUrl"/>: move
    /// <see cref="AppManifestUrl"/> when it says so, and hand back its trace
    /// line, or null when there is nothing to say. Called once, at start-up.
    /// </summary>
    public static string? ApplyTestAppManifestUrl(string? offered, bool runRelocatedByConfigDir)
    {
        string? url = DecideTestAppManifestUrl(offered, runRelocatedByConfigDir, out string? traceLine);
        if (url != null) Volatile.Write(ref _appManifestUrl, url);
        return traceLine;
    }

    /// <summary>
    /// True only for an absolute URL whose PARSED scheme is <c>http</c> and
    /// whose parsed host is exactly <c>localhost</c> or <c>127.0.0.1</c>. A
    /// prefix test would take <c>http://localhost.example.com/</c> and
    /// <c>http://127.0.0.1@elsewhere/</c>; the parse does not.
    /// </summary>
    public static bool IsLocalHttpUrl(string? candidate, out string refusal)
    {
        refusal = "";
        if (string.IsNullOrWhiteSpace(candidate) || !Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            refusal = "not an absolute URL";
            return false;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
        {
            refusal = "scheme " + uri.Scheme + " is not http";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            refusal = "it carries user information before the host";
            return false;
        }
        string host = uri.Host;
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "127.0.0.1", StringComparison.Ordinal))
        {
            refusal = "host " + host + " is not localhost or 127.0.0.1";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Radio firmware catalogue. Separate document from the app manifest
    /// because radio firmware comes from FlexRadio and JJ Flex releases come
    /// from us — coupling them would mean republishing one every time the
    /// other changed. Schema in <see cref="Firmware.FirmwareManifest"/>.
    /// </summary>
    public const string FirmwareManifestUrl =
        "https://data.jjflexible.radio/firmware/manifest.json";

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
