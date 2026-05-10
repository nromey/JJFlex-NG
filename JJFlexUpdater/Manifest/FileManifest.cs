using System.Text.Json.Serialization;

namespace JJFlexUpdater.Manifest;

/// <summary>
/// Per-version file manifest. Hosted at the URL given by
/// <see cref="AppManifestEntry.FileManifestUrl"/>. Lists every file the
/// installed JJF should contain at that version, content-addressable URL
/// for each, plus an obsolete-list of files the helper exe should delete
/// from the prior install.
/// </summary>
public sealed class FileManifest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<FileEntry> Files { get; set; } = new();

    /// <summary>
    /// Files present in the prior version but absent from this one. The
    /// helper exe deletes these from the install dir during the swap.
    /// Relative paths only (no absolute paths, no parent-dir traversal).
    /// </summary>
    [JsonPropertyName("obsolete")]
    public List<string> Obsolete { get; set; } = new();
}

public sealed class FileEntry
{
    /// <summary>
    /// Path relative to the install dir (e.g. "JJFlexRadio.exe" or
    /// "runtimes/win-x64/native/portaudio.dll"). Forward slashes only;
    /// the local file walker normalizes platform separators.
    /// </summary>
    [JsonPropertyName("rel_path")]
    public string RelPath { get; set; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    /// <summary>Lowercase hex sha256.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Content-addressable URL.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
