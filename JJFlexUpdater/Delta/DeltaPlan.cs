using JJFlexUpdater.Manifest;

namespace JJFlexUpdater.Delta;

/// <summary>
/// Result of comparing a local <see cref="Hashing.InstallInventory"/> against
/// a remote <see cref="FileManifest"/>. Three lists, byte-budget summary, and
/// an explicit "is anything to do?" flag so the caller can short-circuit a
/// no-op update.
/// </summary>
public sealed class DeltaPlan
{
    /// <summary>Files that must be fetched from the data provider.</summary>
    public IReadOnlyList<FileEntry> ToDownload { get; init; } = Array.Empty<FileEntry>();

    /// <summary>Files already present locally with matching sha256 — no action.</summary>
    public IReadOnlyList<FileEntry> ToKeep { get; init; } = Array.Empty<FileEntry>();

    /// <summary>
    /// Relative paths the helper exe must remove from the install dir. Sources:
    /// the manifest's <see cref="FileManifest.Obsolete"/> list, plus any local
    /// file that's not in the new manifest.
    /// </summary>
    public IReadOnlyList<string> ToDelete { get; init; } = Array.Empty<string>();

    /// <summary>Total bytes the delta path will need to download.</summary>
    public long DeltaBytes => ToDownload.Sum(f => f.SizeBytes);

    /// <summary>Total install size after the update completes.</summary>
    public long FullSizeBytes => ToDownload.Concat(ToKeep).Sum(f => f.SizeBytes);

    /// <summary>True when at least one file needs to change on disk.</summary>
    public bool HasWork => ToDownload.Count > 0 || ToDelete.Count > 0;
}
