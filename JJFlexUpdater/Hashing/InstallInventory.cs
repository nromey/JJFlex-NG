namespace JJFlexUpdater.Hashing;

/// <summary>
/// Snapshot of "every file under the install dir" with its sha256 hash.
/// Built by <see cref="InstallInventoryWalker"/>; consumed by
/// <see cref="Delta.DeltaPlanner"/>.
///
/// Relative paths use forward slashes throughout so they match the
/// <see cref="Manifest.FileEntry.RelPath"/> convention regardless of
/// platform separator. Lookups are case-insensitive — Windows file
/// system semantics; the cache and the manifest both follow this.
/// </summary>
public sealed class InstallInventory
{
    public string InstallDir { get; }
    public IReadOnlyDictionary<string, InventoryEntry> Files { get; }

    public InstallInventory(string installDir, IReadOnlyDictionary<string, InventoryEntry> files)
    {
        InstallDir = installDir;
        Files = files;
    }
}

public sealed class InventoryEntry
{
    public string RelPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}
