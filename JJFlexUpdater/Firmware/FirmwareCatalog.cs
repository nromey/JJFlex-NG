using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using JJFlexUpdater.Manifest;
using JJFlexUpdater.Net;

namespace JJFlexUpdater.Firmware;

/// <summary>
/// Fetches the firmware catalogue and downloads images from the JJ Flexible
/// data provider.
///
/// Kept entirely separate from the radio: this class knows about HTTP, JSON
/// and hashing, and nothing about FlexLib. Handing the caller a verified file
/// on disk is where its job ends — the radio side takes a path, which is also
/// what makes a hand-picked local file and a downloaded one indistinguishable
/// to everything downstream.
///
/// "The catalogue does not exist yet" is a normal state, not an error worth
/// alarming about. It is expected to be the state on the day this ships.
/// </summary>
public sealed class FirmwareCatalog
{
    private readonly HttpClient _http;

    public FirmwareCatalog() : this(UpdaterHttpClient.Instance) { }

    public FirmwareCatalog(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Work out which platform family a model belongs to.
    ///
    /// FlexRadio ships one image per family: "FLEX-9600" for the larger
    /// 8000-series radios (internally "BigBend") and "FLEX-6x00" for everything
    /// else. Callers that can read <c>Radio.IsBigBend</c> should pass it —
    /// this string fallback exists for the disconnected case.
    /// </summary>
    public static string FamilyForModel(string model, bool? isBigBend = null)
    {
        if (isBigBend.HasValue) return isBigBend.Value ? "FLEX-9600" : "FLEX-6x00";
        if (string.IsNullOrWhiteSpace(model)) return string.Empty;

        // Only used when the radio isn't connected to ask. 8600 and the Aurora
        // AU-520 are the BigBend models we know of; anything else falls to the
        // common image, and an explicit "models" list in the manifest overrides
        // this guess anyway.
        string m = model.ToUpperInvariant();
        if (m.Contains("8600") || m.Contains("AU-520")) return "FLEX-9600";
        return "FLEX-6x00";
    }

    public async Task<FirmwareManifest> FetchAsync(CancellationToken cancellationToken = default)
        => await FetchAsync(UpdaterEndpoints.FirmwareManifestUrl, cancellationToken).ConfigureAwait(false);

    public async Task<FirmwareManifest> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        string body;
        try
        {
            using var response = await HttpRetry.SendWithRetryAsync(
                token => _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, token),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException hex)
        {
            throw new UpdaterFetchException(url, hex.Message, hex);
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<FirmwareManifest>(body, ManifestSerializer.ReadOptions);
            if (manifest is null) throw new UpdaterFetchException(url, "firmware manifest deserialized to null");
            return manifest;
        }
        catch (JsonException jex)
        {
            throw new UpdaterFetchException(url, $"firmware manifest JSON parse error: {jex.Message}", jex);
        }
    }

    /// <summary>
    /// Best image for a model, or null when the catalogue has nothing for it.
    /// Where several match, the highest version wins.
    /// </summary>
    public static FirmwareImage? BestImageFor(FirmwareManifest manifest, string model, bool? isBigBend = null)
    {
        if (manifest?.Images == null || manifest.Images.Count == 0) return null;

        string family = FamilyForModel(model, isBigBend);
        FirmwareImage? best = null;

        foreach (var image in manifest.Images)
        {
            if (!image.AppliesTo(model, family)) continue;
            if (best == null || CompareVersions(image.Version, best.Version) > 0)
                best = image;
        }
        return best;
    }

    /// <summary>
    /// Compare two dotted version strings numerically. Returns &gt;0 when a is
    /// newer. Missing components count as zero, so "4.2.20" sorts below
    /// "4.2.20.41234" rather than comparing as text — which is the bug you get
    /// from string comparison, where "4.2.9" beats "4.2.20".
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = (a ?? string.Empty).Split('.');
        var pb = (b ?? string.Empty).Split('.');
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            long va = i < pa.Length && long.TryParse(pa[i], out long x) ? x : 0;
            long vb = i < pb.Length && long.TryParse(pb[i], out long y) ? y : 0;
            if (va != vb) return va < vb ? -1 : 1;
        }
        return 0;
    }

    /// <summary>
    /// Download an image to a local file and verify its SHA256.
    ///
    /// Downloads to a ".partial" file and only moves it into place once the
    /// hash matches, so an interrupted download can never be mistaken for a
    /// complete one and handed to the radio.
    /// </summary>
    /// <param name="image">Catalogue entry to fetch.</param>
    /// <param name="destinationDirectory">Where to put the finished file.</param>
    /// <param name="onProgress">Fraction 0..1, or -1 when the server sends no length.</param>
    /// <returns>Full path to the verified file.</returns>
    public async Task<string> DownloadAsync(
        FirmwareImage image,
        string destinationDirectory,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (string.IsNullOrWhiteSpace(image.Url))
            throw new UpdaterFetchException(image.FileName, "the catalogue entry has no download address");

        Directory.CreateDirectory(destinationDirectory);
        string fileName = string.IsNullOrWhiteSpace(image.FileName)
            ? Path.GetFileName(new Uri(image.Url).LocalPath)
            : image.FileName;
        string finalPath = Path.Combine(destinationDirectory, fileName);
        string partialPath = finalPath + ".partial";

        try
        {
            using (var response = await HttpRetry.SendWithRetryAsync(
                       token => _http.GetAsync(image.Url, HttpCompletionOption.ResponseHeadersRead, token),
                       cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                long? total = response.Content.Headers.ContentLength;
                using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    read += n;
                    onProgress?.Invoke(total.HasValue && total.Value > 0 ? (double)read / total.Value : -1);
                }
            }

            string actual = ComputeSha256(partialPath);
            if (!string.IsNullOrWhiteSpace(image.Sha256)
                && !string.Equals(actual, image.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partialPath);
                throw new UpdaterFetchException(image.Url,
                    $"checksum mismatch: got {actual}, expected {image.Sha256}");
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partialPath, finalPath);
            return finalPath;
        }
        catch (HttpRequestException hex)
        {
            TryDelete(partialPath);
            throw new UpdaterFetchException(image.Url, hex.Message, hex);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
