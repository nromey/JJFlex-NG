using System.Net.Http;
using System.Security.Cryptography;
using JJFlexUpdater.Delta;
using JJFlexUpdater.Manifest;
using JJFlexUpdater.Net;
using JJFlexUpdater.Progress;

namespace JJFlexUpdater.Download;

/// <summary>
/// Downloads each file in the <see cref="DeltaPlan.ToDownload"/> list from
/// its content-addressable URL and writes it under the staging dir at the
/// same relative path the install dir will see post-swap. Hash-verifies on
/// the fly: a mismatched stream surfaces as
/// <see cref="DeltaDownloadException"/> so the caller can fall back to the
/// full-bundle path.
///
/// Per-file retry leans on <see cref="HttpRetry"/>; whole-batch failure is
/// terminal (no point re-running the loop — fall back to full-bundle and
/// let NSIS re-deliver the entire payload). Progress is reported per-file
/// so the UI can announce file count + byte totals at chatty verbosity.
/// </summary>
public sealed class DeltaDownloader
{
    private readonly HttpClient _http;

    public DeltaDownloader() : this(UpdaterHttpClient.Instance) { }

    public DeltaDownloader(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task DownloadAllAsync(
        DeltaPlan plan,
        string stagingFilesDir,
        IUpdaterProgressSink progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(stagingFilesDir);
        progress ??= NullUpdaterProgressSink.Instance;

        Directory.CreateDirectory(stagingFilesDir);

        long totalBytes = plan.DeltaBytes;
        long completedBytes = 0;
        int totalFiles = plan.ToDownload.Count;

        for (int i = 0; i < totalFiles; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = plan.ToDownload[i];
            string targetPath = ResolveStagingPath(stagingFilesDir, file.RelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            // Wire-bytes for progress (compressed sum); fall back to
            // uncompressed if the manifest didn't supply a compressed
            // size for this entry.
            long fileWire = file.CompressedSizeBytes > 0
                ? file.CompressedSizeBytes
                : file.SizeBytes;

            progress.Report(new UpdaterProgressSnapshot(
                UpdaterPhase.DownloadingFiles,
                $"Downloading {file.RelPath} ({Format.Bytes(fileWire)})",
                FilesCompleted: i,
                FilesTotal: totalFiles,
                BytesCompleted: completedBytes,
                BytesTotal: totalBytes));

            try
            {
                await DownloadOneAsync(file, targetPath, cancellationToken).ConfigureAwait(false);
            }
            catch (DeltaDownloadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DeltaDownloadException(file.RelPath, file.Url,
                    $"download failed: {ex.Message}", ex);
            }

            completedBytes += fileWire;
        }

        progress.Report(new UpdaterProgressSnapshot(
            UpdaterPhase.DownloadingFiles,
            $"Downloaded {totalFiles} file(s), {Format.Bytes(completedBytes)} total",
            FilesCompleted: totalFiles,
            FilesTotal: totalFiles,
            BytesCompleted: completedBytes,
            BytesTotal: totalBytes));
    }

    /// <summary>
    /// Fetch the .lzma blob, optionally verify the wire bytes against
    /// <see cref="FileEntry.CompressedSha256"/>, decompress via SharpCompress
    /// LZMA Alone, and verify the decompressed bytes against
    /// <see cref="FileEntry.Sha256"/>. The post-decompression hash is the
    /// load-bearing identity check (matches the local-inventory walker's
    /// hash convention). The compressed-side check is a cheap belt-and
    /// -suspenders gate.
    /// </summary>
    private async Task DownloadOneAsync(FileEntry file, string targetPath, CancellationToken ct)
    {
        // 1. Fetch the .lzma blob into memory. Per-file blobs are bounded
        //    (largest single JJF asset is the main exe at ~8 MB uncompressed
        //    → ~4 MB compressed) so buffering avoids the LZMA stream having
        //    to seek backwards during decode.
        byte[] compressedBytes;
        using (var response = await HttpRetry.SendWithRetryAsync(
            token => _http.GetAsync(file.Url, HttpCompletionOption.ResponseContentRead, token),
            cancellationToken: ct).ConfigureAwait(false))
        {
            compressedBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        // 2. Optional in-flight integrity check. Cheap — bytes already
        //    in memory. Catches mid-stream corruption before we waste
        //    decompression cycles on garbage.
        if (!string.IsNullOrEmpty(file.CompressedSha256))
        {
            string compressedActual = HashBytes(compressedBytes);
            if (!string.Equals(compressedActual, file.CompressedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new DeltaDownloadException(file.RelPath, file.Url,
                    $"compressed sha256 mismatch: got {compressedActual}, expected {file.CompressedSha256}");
            }
        }

        // 3. Decompress + write + hash, all in one streaming pass. Track N
        //    pinned the upload format as XZ (FORMAT_XZ via Python's lzma
        //    module) — framed format with magic bytes (FD 37 7A 58 5A 00)
        //    + per-block integrity checks. SharpCompress's XZStream
        //    auto-detects from the magic header so we don't manually parse
        //    a 13-byte LZMA Alone header. The post-decompression sha256 on
        //    file.Sha256 stays the canonical integrity gate; XZ's internal
        //    CRC32 / CRC64 is belt-and-suspenders.
        using (var memSource = new MemoryStream(compressedBytes, 0, compressedBytes.Length, writable: false, publiclyVisible: false))
        using (var xz = new SharpCompress.Compressors.Xz.XZStream(memSource))
        using (var sha = SHA256.Create())
        {
            var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write,
                FileShare.None, LocalFileHasherBuffer, useAsync: true);
            await using (fs.ConfigureAwait(false))
            {
                byte[] buffer = new byte[LocalFileHasherBuffer];
                while (true)
                {
                    // XZStream's Read is sync; call inline. Decompress speed
                    // is ~50-150 MB/sec on modern hardware, so this isn't
                    // worth offloading to a thread pool task.
                    int n = xz.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    sha.TransformBlock(buffer, 0, n, buffer, 0);
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                string actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DeltaDownloadException(file.RelPath, file.Url,
                        $"sha256 mismatch (post-decompression): got {actual}, expected {file.Sha256}");
                }
            }
        }
    }

    private static string HashBytes(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private const int LocalFileHasherBuffer = 81920;

    /// <summary>
    /// Resolve <paramref name="relPath"/> under <paramref name="stagingFilesDir"/>
    /// while rejecting parent-traversal sequences. Defense-in-depth: a malicious
    /// or corrupt manifest must not let us write outside the staging dir.
    /// </summary>
    private static string ResolveStagingPath(string stagingFilesDir, string relPath)
    {
        string normalized = relPath.Replace('/', Path.DirectorySeparatorChar);
        string combined = Path.GetFullPath(Path.Combine(stagingFilesDir, normalized));
        string baseFull = Path.GetFullPath(stagingFilesDir);

        if (!combined.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeltaDownloadException(relPath, string.Empty,
                $"manifest entry escapes staging dir: {relPath}");
        }
        return combined;
    }
}

/// <summary>
/// Thrown by <see cref="DeltaDownloader"/> on any per-file failure.
/// Carries enough context (rel path, URL, message) for the trace log;
/// the UI surfaces a friendly "couldn't download an update piece"
/// line and the orchestrator falls back to the full-bundle path.
/// </summary>
public sealed class DeltaDownloadException : Exception
{
    public string RelPath { get; }
    public string Url { get; }

    public DeltaDownloadException(string relPath, string url, string message)
        : base($"{message} [{relPath}]")
    {
        RelPath = relPath;
        Url = url;
    }

    public DeltaDownloadException(string relPath, string url, string message, Exception inner)
        : base($"{message} [{relPath}]", inner)
    {
        RelPath = relPath;
        Url = url;
    }
}
