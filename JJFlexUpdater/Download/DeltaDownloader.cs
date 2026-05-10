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

            progress.Report(new UpdaterProgressSnapshot(
                UpdaterPhase.DownloadingFiles,
                $"Downloading {file.RelPath} ({Format.Bytes(file.SizeBytes)})",
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

            completedBytes += file.SizeBytes;
        }

        progress.Report(new UpdaterProgressSnapshot(
            UpdaterPhase.DownloadingFiles,
            $"Downloaded {totalFiles} file(s), {Format.Bytes(completedBytes)} total",
            FilesCompleted: totalFiles,
            FilesTotal: totalFiles,
            BytesCompleted: completedBytes,
            BytesTotal: totalBytes));
    }

    private async Task DownloadOneAsync(FileEntry file, string targetPath, CancellationToken ct)
    {
        using var response = await HttpRetry.SendWithRetryAsync(
            token => _http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, token),
            cancellationToken: ct).ConfigureAwait(false);

        // Stream into the staging file while computing sha256 in parallel.
        // Using a transform-style read avoids buffering the whole payload in
        // memory for large native DLLs.
        await using var sourceStreamRaw = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (sourceStreamRaw.ConfigureAwait(false))
        {
            using var sha = SHA256.Create();
            await using var fileStreamRaw = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                LocalFileHasherBuffer, useAsync: true);
            await using (fileStreamRaw.ConfigureAwait(false))
            {
                byte[] buffer = new byte[LocalFileHasherBuffer];
                int n;
                while (true)
                {
                    n = await sourceStreamRaw.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                                              .ConfigureAwait(false);
                    if (n <= 0) break;
                    sha.TransformBlock(buffer, 0, n, buffer, 0);
                    await fileStreamRaw.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                string actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DeltaDownloadException(file.RelPath, file.Url,
                        $"sha256 mismatch: got {actual}, expected {file.Sha256}");
                }
            }
        }
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
