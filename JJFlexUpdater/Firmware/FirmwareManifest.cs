using System.Text.Json.Serialization;

namespace JJFlexUpdater.Firmware;

/// <summary>
/// Radio firmware catalogue hosted at
/// <see cref="UpdaterEndpoints.FirmwareManifestUrl"/>.
///
/// Deliberately separate from the app manifest. Radio firmware and JJ Flex
/// releases move on completely independent schedules — firmware comes from
/// FlexRadio, JJ Flex from us — and coupling them would mean republishing one
/// document every time either changed.
///
/// Same forward-compatibility posture as <see cref="Manifest.AppManifest"/>:
/// unknown properties are ignored, optional fields are nullable, and
/// <see cref="SchemaVersion"/> exists so a future breaking change can hard-fail
/// rather than silently misparse.
/// </summary>
public sealed class FirmwareManifest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("generated_at")]
    public DateTimeOffset? GeneratedAt { get; set; }

    [JsonPropertyName("images")]
    public List<FirmwareImage> Images { get; set; } = new();
}

/// <summary>
/// One firmware image. FlexRadio ships a single image per platform family
/// covering many models, so <see cref="Models"/> is a list and
/// <see cref="Family"/> is the fallback when a model is not named explicitly.
/// </summary>
public sealed class FirmwareImage
{
    /// <summary>
    /// Platform family: "FLEX-6x00" or "FLEX-9600". Matches the image's own
    /// filename prefix, which is how FlexRadio names them.
    /// </summary>
    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    /// <summary>
    /// Models this image covers, e.g. "FLEX-6300", "FLEX-8600". Preferred over
    /// <see cref="Family"/> when present because it survives us guessing the
    /// family wrong for a model we have not seen before.
    /// </summary>
    [JsonPropertyName("models")]
    public List<string> Models { get; set; } = new();

    /// <summary>Full four-part version, e.g. "4.2.20.41234".</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    /// <summary>
    /// SHA256 of the .ssdr as published. The radio is the real integrity
    /// authority — its bootloader rejects a bad image — but verifying before
    /// sending turns a corrupt download into an error message instead of a
    /// several-minute update attempt that fails at the far end.
    /// </summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Minimum firmware version that can jump straight to this one, when
    /// FlexRadio requires an intermediate step. Null means no restriction
    /// known. Advisory: we surface it, we do not enforce it, because getting
    /// this wrong in either direction is worse than telling the user.
    /// </summary>
    [JsonPropertyName("min_version_for_direct_update")]
    public string? MinVersionForDirectUpdate { get; set; }

    /// <summary>
    /// True when this release is a breaking change the user genuinely needs —
    /// a security fix, a protocol change JJ Flex depends on, or a release
    /// FlexRadio has flagged as mandatory. The update prompt uses stronger
    /// language and re-offers more insistently. It still never auto-installs:
    /// firmware forces a radio reboot and is LAN-only, so applying it is
    /// always a deliberate user act (policy decided 2026-08-03).
    /// </summary>
    [JsonPropertyName("breaking")]
    public bool Breaking { get; set; }

    /// <summary>
    /// Plain-language reason shown when <see cref="Breaking"/> is true —
    /// "why you actually want this one", not a changelog.
    /// </summary>
    [JsonPropertyName("breaking_reason")]
    public string? BreakingReason { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>True when this image is applicable to the given radio model.</summary>
    public bool AppliesTo(string model, string family)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            foreach (var m in Models)
            {
                if (string.Equals(m, model, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return !string.IsNullOrWhiteSpace(family)
            && string.Equals(Family, family, StringComparison.OrdinalIgnoreCase);
    }
}
