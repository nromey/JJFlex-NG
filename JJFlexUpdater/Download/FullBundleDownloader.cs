using System.Net.Http;
using System.Security.Cryptography;
using JJFlexUpdater.Manifest;
using JJFlexUpdater.Net;
using JJFlexUpdater.Progress;

namespace JJFlexUpdater.Download;

/// <summary>
/// Downloads the full NSIS installer for an <see cref="AppManifestEntry"/>
/// and verifies its SHA256. Used as the fallback path when delta computation
/// or per-file delta download fails — guaranteed to deliver every file
/// because NSIS handles install + restart end to end. Track M is bypassed
/// in this mode.
///
/// The downloaded installer lives in the staging dir under <c>installer\</c>
/// so a failure leaves a complete artifact for manual retry. Per Track D
/// "MUST NOT regress" rule #2.
/// </summary>
public sealed class FullBundleDownloader
{
    public const string InstallerSubdir = "installer";

    private readonly HttpClient _http;

    public FullBundleDownloader() : this(UpdaterHttpClient.Instance) { }

    public FullBundleDownloader(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Download the installer named by <paramref name="entry"/> into
    /// <c>{stagingRoot}\installer\</c>. Returns the absolute path.
    /// </summary>
    public async Task<string> DownloadAsync(
        AppManifestEntry entry,
        string stagingRoot,
        IUpdaterProgressSink progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(stagingRoot);
        if (string.IsNullOrEmpty(entry.FullInstallerUrl))
            throw new FullBundleException("manifest entry has no full_installer_url");
        if (string.IsNullOrEmpty(entry.FullInstallerSha256))
            throw new FullBundleException("manifest entry has no full_installer_sha256");

        progress ??= NullUpdaterProgressSink.Instance;

        string installerDir = Path.Combine(stagingRoot, InstallerSubdir);
        Directory.CreateDirectory(installerDir);

        string fileName = SafeFileNameFromUrl(entry.FullInstallerUrl, entry.Version);
        string targetPath = Path.Combine(installerDir, fileName);

        progress.Report(new UpdaterProgressSnapshot(
            UpdaterPhase.FullBundleDownloading,
            $"Downloading installer ({Format.Bytes(entry.FullInstallerSizeBytes)})",
            FilesCompleted: 0,
            FilesTotal: 1,
            BytesCompleted: 0,
            BytesTotal: entry.FullInstallerSizeBytes));

        try
        {
            using var response = await HttpRetry.SendWithRetryAsync(
                token => _http.GetAsync(entry.FullInstallerUrl,
                    HttpCompletionOption.ResponseHeadersRead, token),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await using var sourceStreamRaw = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (sourceStreamRaw.ConfigureAwait(false))
            {
                using var sha = SHA256.Create();
                await using var fileStreamRaw = new FileStream(
                    targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    81920, useAsync: true);
                await using (fileStreamRaw.ConfigureAwait(false))
                {
                    byte[] buffer = new byte[81920];
                    long total = 0;
                    while (true)
                    {
                        int n = await sourceStreamRaw
                            .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                            .ConfigureAwait(false);
                        if (n <= 0) break;
                        sha.TransformBlock(buffer, 0, n, buffer, 0);
                        await fileStreamRaw
                            .WriteAsync(buffer.AsMemory(0, n), cancellationToken)
                            .ConfigureAwait(false);
                        total += n;
                        progress.Report(new UpdaterProgressSnapshot(
                            UpdaterPhase.FullBundleDownloading,
                            $"Downloading installer ({Format.Bytes(total)} / {Format.Bytes(entry.FullInstallerSizeBytes)})",
                            FilesCompleted: 0,
                            FilesTotal: 1,
                            BytesCompleted: total,
                            BytesTotal: entry.FullInstallerSizeBytes));
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                    string actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                    if (!string.Equals(actual, entry.FullInstallerSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new FullBundleException(
                            $"installer sha256 mismatch: got {actual}, expected {entry.FullInstallerSha256}");
                    }
                }
            }
        }
        catch (FullBundleException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FullBundleException($"installer download failed: {ex.Message}", ex);
        }

        return targetPath;
    }

    private static string SafeFileNameFromUrl(string url, string fallbackVersion)
    {
        try
        {
            string fromUrl = Path.GetFileName(new Uri(url).LocalPath);
            if (!string.IsNullOrWhiteSpace(fromUrl)
                && fromUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return fromUrl;
            }
        }
        catch
        {
            // fall through
        }
        string ver = string.IsNullOrEmpty(fallbackVersion) ? "unknown" : fallbackVersion;
        return $"Setup_JJFlexRadio_{ver}_{UpdaterPlatform.Current}.exe";
    }
}

public sealed class FullBundleException : Exception
{
    public FullBundleException(string message) : base(message) { }
    public FullBundleException(string message, Exception inner) : base(message, inner) { }
}
