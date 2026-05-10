using System.Text.Json;
using System.Text.Json.Serialization;

namespace JJFlexUpdaterHelper;

// Schema mirrored from Track D's TRACK-INSTRUCTIONS.md, the canonical source.
// schema_version=1 is the only shape this helper accepts; v2+ are forward
// changes Track D would coordinate before shipping.

internal sealed record HandoffManifest(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("source_dir")] string SourceDir,
    [property: JsonPropertyName("target_dir")] string TargetDir,
    [property: JsonPropertyName("backup_dir")] string BackupDir,
    [property: JsonPropertyName("jjf_pid")] int JjfPid,
    [property: JsonPropertyName("jjf_relaunch_path")] string JjfRelaunchPath,
    [property: JsonPropertyName("copy_files")] IReadOnlyList<CopyFile> CopyFiles,
    [property: JsonPropertyName("delete_files")] IReadOnlyList<string> DeleteFiles,
    [property: JsonPropertyName("rollback_on_any_failure")] bool RollbackOnAnyFailure);

internal sealed record CopyFile(
    [property: JsonPropertyName("rel_path")] string RelPath,
    [property: JsonPropertyName("expected_sha256")] string ExpectedSha256);

public sealed class ManifestLoadException : Exception
{
    public ManifestLoadException() { }
    public ManifestLoadException(string message) : base(message) { }
    public ManifestLoadException(string message, Exception innerException) : base(message, innerException) { }
}

internal static class ManifestLoader
{
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static HandoffManifest Load(string stagingDir)
    {
        if (!Directory.Exists(stagingDir))
        {
            throw new ManifestLoadException($"staging dir does not exist: {stagingDir}");
        }

        var manifestPath = Path.Combine(stagingDir, "handoff-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new ManifestLoadException($"handoff-manifest.json not found at: {manifestPath}");
        }

        HandoffManifest? manifest;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize<HandoffManifest>(stream, s_options);
        }
        catch (JsonException ex)
        {
            throw new ManifestLoadException($"handoff-manifest.json is not valid JSON: {ex.Message}");
        }

        if (manifest is null)
        {
            throw new ManifestLoadException("handoff-manifest.json deserialized to null");
        }

        Validate(manifest);
        return manifest;
    }

    private static void Validate(HandoffManifest m)
    {
        if (m.SchemaVersion != SupportedSchemaVersion)
        {
            throw new ManifestLoadException(
                $"unsupported schema_version {m.SchemaVersion} (this helper accepts only {SupportedSchemaVersion})");
        }

        if (string.IsNullOrWhiteSpace(m.SourceDir)) throw new ManifestLoadException("source_dir is required");
        if (string.IsNullOrWhiteSpace(m.TargetDir)) throw new ManifestLoadException("target_dir is required");
        if (string.IsNullOrWhiteSpace(m.BackupDir)) throw new ManifestLoadException("backup_dir is required");
        if (string.IsNullOrWhiteSpace(m.JjfRelaunchPath)) throw new ManifestLoadException("jjf_relaunch_path is required");
        if (m.JjfPid <= 0) throw new ManifestLoadException($"jjf_pid must be positive (got {m.JjfPid})");
        if (m.CopyFiles is null) throw new ManifestLoadException("copy_files is required");
        if (m.DeleteFiles is null) throw new ManifestLoadException("delete_files is required");

        for (var i = 0; i < m.CopyFiles.Count; i++)
        {
            var entry = m.CopyFiles[i];
            if (entry is null) throw new ManifestLoadException($"copy_files[{i}] is null");
            if (string.IsNullOrWhiteSpace(entry.RelPath)) throw new ManifestLoadException($"copy_files[{i}].rel_path is required");
            if (string.IsNullOrWhiteSpace(entry.ExpectedSha256)) throw new ManifestLoadException($"copy_files[{i}].expected_sha256 is required");
            try { PathGuard.RejectIfUnsafe(entry.RelPath); }
            catch (ArgumentException ex) { throw new ManifestLoadException($"copy_files[{i}]: {ex.Message}"); }
        }

        for (var i = 0; i < m.DeleteFiles.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(m.DeleteFiles[i]))
            {
                throw new ManifestLoadException($"delete_files[{i}] is empty");
            }
            try { PathGuard.RejectIfUnsafe(m.DeleteFiles[i]); }
            catch (ArgumentException ex) { throw new ManifestLoadException($"delete_files[{i}]: {ex.Message}"); }
        }
    }
}
