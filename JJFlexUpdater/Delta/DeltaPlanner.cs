using JJFlexUpdater.Hashing;
using JJFlexUpdater.Manifest;

namespace JJFlexUpdater.Delta;

/// <summary>
/// Pure function: given a local install inventory and a remote file manifest,
/// classify every file into download / keep / delete. No I/O — easy to unit
/// test against synthetic inputs per the Track D Definition of Done item
/// "Delta computation correctly identifies download/keep/delete lists for
/// synthetic test scenarios."
/// </summary>
public static class DeltaPlanner
{
    public static DeltaPlan Plan(InstallInventory local, FileManifest remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        var toDownload = new List<FileEntry>();
        var toKeep = new List<FileEntry>();

        var remotePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var remoteFile in remote.Files)
        {
            remotePaths.Add(remoteFile.RelPath);

            if (local.Files.TryGetValue(remoteFile.RelPath, out var localEntry)
                && string.Equals(localEntry.Sha256, remoteFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                toKeep.Add(remoteFile);
            }
            else
            {
                toDownload.Add(remoteFile);
            }
        }

        // delete = obsolete list ∪ local files not in remote (and not under
        // the excluded user-data subdirs, which the walker already filtered).
        var toDelete = new List<string>();
        foreach (string obsolete in remote.Obsolete)
        {
            if (!string.IsNullOrWhiteSpace(obsolete))
                toDelete.Add(obsolete);
        }
        foreach (string localPath in local.Files.Keys)
        {
            if (!remotePaths.Contains(localPath))
                toDelete.Add(localPath);
        }

        // Dedupe any overlap between the obsolete list and local-not-in-remote.
        toDelete = toDelete
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DeltaPlan
        {
            ToDownload = toDownload,
            ToKeep = toKeep,
            ToDelete = toDelete,
        };
    }
}
