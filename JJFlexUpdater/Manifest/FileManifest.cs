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

    /// <summary>Uncompressed size — what the file occupies on disk after the swap.</summary>
    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    /// <summary>
    /// Lowercase hex sha256 over the UNCOMPRESSED file. This is the
    /// load-bearing identity hash — the local inventory's sha256 is
    /// computed over uncompressed bytes too, so equal hashes mean
    /// "identical file on disk." Verified post-decompression in the
    /// download path; mismatch fails the file.
    /// </summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Wire size — bytes the client actually fetches across the network.
    /// Sum of these across <see cref="Delta.DeltaPlan.ToDownload"/> drives
    /// the user-facing "delta size" indicator and the network-transfer
    /// progress total.
    /// </summary>
    [JsonPropertyName("compressed_size_bytes")]
    public long CompressedSizeBytes { get; set; }

    /// <summary>
    /// Lowercase hex sha256 over the COMPRESSED .lzma blob — the bytes
    /// actually fetched from <see cref="Url"/>. Used as a cheap in-flight
    /// integrity check before decompression. Optional in the schema; the
    /// post-decompression sha256 (<see cref="Sha256"/>) is the load-bearing
    /// gate. Lets us bail before decompressing a corrupt blob.
    /// </summary>
    [JsonPropertyName("compressed_sha256")]
    public string CompressedSha256 { get; set; } = string.Empty;

    /// <summary>
    /// Content-addressable URL of the .lzma blob. Track N convention:
    /// <c>https://data.jjflexible.radio/files/&lt;sha-prefix&gt;/&lt;sha&gt;.lzma</c>.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
