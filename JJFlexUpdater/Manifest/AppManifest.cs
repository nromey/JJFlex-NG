using System.Text.Json;
using System.Text.Json.Serialization;

namespace JJFlexUpdater.Manifest;

/// <summary>
/// Top-level app manifest hosted at
/// <see cref="UpdaterEndpoints.AppManifestUrl"/>. Lists per-channel current
/// versions and points at per-version <see cref="FileManifest"/> URLs.
///
/// Forward-compatibility: unknown JSON properties are ignored by the System
/// .Text.Json defaults we configure in <see cref="ManifestSerializer"/>;
/// optional fields are nullable so a server schema bump that adds a field
/// doesn't break older clients. The <see cref="SchemaVersion"/> field exists
/// so we can hard-fail on a major schema break in a future migration.
/// </summary>
public sealed class AppManifest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("channels")]
    public Dictionary<string, ChannelInfo> Channels { get; set; } = new();
}

public sealed class ChannelInfo
{
    [JsonPropertyName("latest_version")]
    public string LatestVersion { get; set; } = string.Empty;

    [JsonPropertyName("entries")]
    public List<AppManifestEntry> Entries { get; set; } = new();
}

public sealed class AppManifestEntry
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>"win-x64" / "win-x86".</summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    /// <summary>URL of the per-version <see cref="FileManifest"/>.</summary>
    [JsonPropertyName("file_manifest_url")]
    public string FileManifestUrl { get; set; } = string.Empty;

    /// <summary>Fallback installer URL — used by the full-bundle fallback path.</summary>
    [JsonPropertyName("full_installer_url")]
    public string FullInstallerUrl { get; set; } = string.Empty;

    [JsonPropertyName("full_installer_sha256")]
    public string FullInstallerSha256 { get; set; } = string.Empty;

    [JsonPropertyName("full_installer_size_bytes")]
    public long FullInstallerSizeBytes { get; set; }

    [JsonPropertyName("changelog_url")]
    public string? ChangelogUrl { get; set; }

    /// <summary>
    /// Reserved slot for Track E's chained-update pattern (firmware after
    /// app update). Track D deserializes the field but does not act on it;
    /// Track E will populate the consumer side.
    /// See memory/project_chained_updater_pattern.md.
    /// </summary>
    [JsonPropertyName("chained_updates")]
    public List<ChainedUpdate>? ChainedUpdates { get; set; }
}

/// <summary>
/// Forward-compat placeholder for the chained-update pattern. Track E will
/// extend this; Track D only needs the field to round-trip.
/// </summary>
public sealed class ChainedUpdate
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("min_client_version")]
    public string? MinClientVersion { get; set; }

    [JsonPropertyName("descriptor_url")]
    public string? DescriptorUrl { get; set; }
}
