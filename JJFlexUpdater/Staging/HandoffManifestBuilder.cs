using System.Diagnostics;
using JJFlexUpdater.Delta;
using JJFlexUpdater.Manifest;

namespace JJFlexUpdater.Staging;

/// <summary>
/// Builds the <see cref="HandoffManifest"/> for the helper exe and writes it
/// to <see cref="StagingDir.HandoffManifestPath"/>. The contract details
/// (PID, relaunch path, copy/delete lists) live in
/// <see cref="HandoffManifest"/>; this class just assembles them from the
/// upstream pieces and serializes.
/// </summary>
public static class HandoffManifestBuilder
{
    public static HandoffManifest Build(
        StagingDir staging,
        string installDir,
        string relaunchPath,
        DeltaPlan plan,
        int? jjfPid = null)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(installDir);
        ArgumentNullException.ThrowIfNull(relaunchPath);
        ArgumentNullException.ThrowIfNull(plan);

        return new HandoffManifest
        {
            SourceDir = staging.FilesDir,
            TargetDir = installDir,
            BackupDir = staging.BackupDir,
            JjfPid = jjfPid ?? Process.GetCurrentProcess().Id,
            JjfRelaunchPath = relaunchPath,
            CopyFiles = plan.ToDownload
                .Select(f => new HandoffCopyEntry
                {
                    RelPath = f.RelPath,
                    ExpectedSha256 = f.Sha256,
                })
                .ToList(),
            DeleteFiles = plan.ToDelete.ToList(),
            RollbackOnAnyFailure = true,
        };
    }

    public static async Task WriteAsync(
        StagingDir staging,
        HandoffManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(manifest);

        string json = ManifestSerializer.SerializeHandoffManifest(manifest);
        await File.WriteAllTextAsync(staging.HandoffManifestPath, json, cancellationToken)
                  .ConfigureAwait(false);
    }
}
