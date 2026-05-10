using System.Net.Http;
using JJFlexUpdater.Manifest;

namespace JJFlexUpdater.Net;

/// <summary>
/// Fetches and parses the top-level <see cref="AppManifest"/> and per-version
/// <see cref="FileManifest"/> documents. Wraps <see cref="HttpRetry"/> +
/// <see cref="UpdaterHttpClient"/> + <see cref="ManifestSerializer"/>.
///
/// All methods are best-effort with bounded retry: on a definite failure
/// (manifest does not exist, parse error, server returns 4xx) the caller
/// gets an <see cref="UpdaterFetchException"/> with diagnostic detail.
/// "Manifest doesn't exist yet" is a normal early-rollout state and the
/// caller is expected to handle it gracefully — see Track D Definition of
/// Done item "No actual manifest required to ship."
/// </summary>
public sealed class ManifestFetcher
{
    private readonly HttpClient _http;

    public ManifestFetcher() : this(UpdaterHttpClient.Instance) { }

    public ManifestFetcher(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public Task<AppManifest> FetchAppManifestAsync(CancellationToken cancellationToken = default)
        => FetchAppManifestAsync(UpdaterEndpoints.AppManifestUrl, cancellationToken);

    public async Task<AppManifest> FetchAppManifestAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        string body = await FetchBodyAsync(url, cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = ManifestSerializer.DeserializeAppManifest(body);
            if (manifest is null)
                throw new UpdaterFetchException(url, "manifest deserialized to null");
            return manifest;
        }
        catch (System.Text.Json.JsonException jex)
        {
            throw new UpdaterFetchException(url, $"app manifest JSON parse error: {jex.Message}", jex);
        }
    }

    public async Task<FileManifest> FetchFileManifestAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        string body = await FetchBodyAsync(url, cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = ManifestSerializer.DeserializeFileManifest(body);
            if (manifest is null)
                throw new UpdaterFetchException(url, "file manifest deserialized to null");
            return manifest;
        }
        catch (System.Text.Json.JsonException jex)
        {
            throw new UpdaterFetchException(url, $"file manifest JSON parse error: {jex.Message}", jex);
        }
    }

    private async Task<string> FetchBodyAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await HttpRetry.SendWithRetryAsync(
                token => _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, token),
                cancellationToken: ct).ConfigureAwait(false);
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException hex)
        {
            throw new UpdaterFetchException(url, hex.Message, hex);
        }
    }
}

/// <summary>
/// Thrown when the updater can't get a usable manifest. The <see cref="Url"/>
/// property is the resource we tried; <see cref="Exception.Message"/>
/// describes why. Caller speech / dialogs should NOT echo the raw message —
/// use a friendly "couldn't reach the update server" line and stash this in
/// the trace log for Noel to inspect.
/// </summary>
public sealed class UpdaterFetchException : Exception
{
    public string Url { get; }

    public UpdaterFetchException(string url, string message)
        : base($"{message} ({url})")
    {
        Url = url;
    }

    public UpdaterFetchException(string url, string message, Exception inner)
        : base($"{message} ({url})", inner)
    {
        Url = url;
    }
}
