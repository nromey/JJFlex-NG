using System.Text.Json;
using System.Text.Json.Serialization;

namespace JJFlexUpdater.Manifest;

/// <summary>
/// Centralized System.Text.Json options for manifest reading and writing.
/// One options instance per process per the perf guidance — building a
/// fresh JsonSerializerOptions per call rebuilds the type cache and is
/// slow. Forward-compatibility settings live here so every manifest read
/// in the updater behaves identically:
///
/// - PropertyNameCaseInsensitive: tolerate accidental casing drift in
///   server output without breaking parsing.
/// - UnmappedMemberHandling=Skip: server can add fields without breaking
///   us.
/// - AllowTrailingCommas + ReadCommentHandling=Skip: tolerant of the
///   manifest being hand-edited during testing.
/// - WriteIndented for the writer side (handoff manifest) so a human can
///   inspect the staging dir during a failed update.
/// </summary>
public static class ManifestSerializer
{
    public static JsonSerializerOptions ReadOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static JsonSerializerOptions WriteOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppManifest? DeserializeAppManifest(string json)
        => JsonSerializer.Deserialize<AppManifest>(json, ReadOptions);

    public static FileManifest? DeserializeFileManifest(string json)
        => JsonSerializer.Deserialize<FileManifest>(json, ReadOptions);

    public static string SerializeHandoffManifest(HandoffManifest manifest)
        => JsonSerializer.Serialize(manifest, WriteOptions);
}
