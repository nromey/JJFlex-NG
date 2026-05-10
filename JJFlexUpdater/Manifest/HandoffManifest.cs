using System.Text.Json.Serialization;

namespace JJFlexUpdater.Manifest;

/// <summary>
/// Contract between Track D (this project — writes the manifest) and
/// Track M (the updater-helper exe — reads it). Lives in the staging dir
/// at <c>%TEMP%\JJFlexUpdate-&lt;guid&gt;\handoff-manifest.json</c>. If
/// the schema needs to evolve, bump <see cref="SchemaVersion"/>; v1
/// readers must reject anything they can't handle and surface a fallback.
///
/// All paths are absolute. <see cref="CopyFiles"/> entries reference
/// paths under <see cref="SourceDir"/>; <see cref="DeleteFiles"/> entries
/// are relative to <see cref="TargetDir"/> with no parent-traversal.
/// </summary>
public sealed class HandoffManifest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("source_dir")]
    public string SourceDir { get; set; } = string.Empty;

    [JsonPropertyName("target_dir")]
    public string TargetDir { get; set; } = string.Empty;

    [JsonPropertyName("backup_dir")]
    public string BackupDir { get; set; } = string.Empty;

    /// <summary>JJF process id; helper waits for this PID to exit before swap.</summary>
    [JsonPropertyName("jjf_pid")]
    public int JjfPid { get; set; }

    /// <summary>Absolute path to JJFlexRadio.exe — the helper relaunches this on success.</summary>
    [JsonPropertyName("jjf_relaunch_path")]
    public string JjfRelaunchPath { get; set; } = string.Empty;

    [JsonPropertyName("copy_files")]
    public List<HandoffCopyEntry> CopyFiles { get; set; } = new();

    /// <summary>Relative paths inside <see cref="TargetDir"/> to delete during swap.</summary>
    [JsonPropertyName("delete_files")]
    public List<string> DeleteFiles { get; set; } = new();

    /// <summary>
    /// When true, the helper restores from <see cref="BackupDir"/> if any
    /// copy or delete fails mid-swap. Defaults to true; the only reason
    /// to flip it off is a manual recovery run where we want partial
    /// progress preserved.
    /// </summary>
    [JsonPropertyName("rollback_on_any_failure")]
    public bool RollbackOnAnyFailure { get; set; } = true;
}

public sealed class HandoffCopyEntry
{
    /// <summary>Path relative to both <see cref="HandoffManifest.SourceDir"/> and target dir.</summary>
    [JsonPropertyName("rel_path")]
    public string RelPath { get; set; } = string.Empty;

    /// <summary>Lowercase hex sha256 — helper re-verifies before swap.</summary>
    [JsonPropertyName("expected_sha256")]
    public string ExpectedSha256 { get; set; } = string.Empty;
}
