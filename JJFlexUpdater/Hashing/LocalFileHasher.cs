using System.Security.Cryptography;

namespace JJFlexUpdater.Hashing;

/// <summary>
/// Computes lowercase-hex SHA256 over a file path. One-shot helper used
/// by both the local-inventory walker and the per-file download verifier.
/// </summary>
public static class LocalFileHasher
{
    public const int StreamBufferSize = 81920;

    public static async Task<string> ComputeAsync(string path, CancellationToken cancellationToken = default)
    {
        var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, useAsync: true);
        await using (fs.ConfigureAwait(false))
        {
            using var sha = SHA256.Create();
            byte[] hash = await sha.ComputeHashAsync(fs, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
