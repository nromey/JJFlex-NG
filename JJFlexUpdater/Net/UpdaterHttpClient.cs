using System.Net;
using System.Net.Http;
using System.Reflection;

namespace JJFlexUpdater.Net;

/// <summary>
/// Single shared <see cref="HttpClient"/> for every updater HTTP call.
/// Mirrors the Sprint 29 Track C CrashReporter pattern (commit 6ecd15f8) —
/// repeated <c>new HttpClient()</c> risks socket exhaustion. One per process
/// is fine; updater requests are infrequent and never highly concurrent.
///
/// The User-Agent header is built from the running JJF assembly version so
/// access logs at the data provider distinguish updater traffic from browser
/// traffic and per-version checkin counts are visible. Platform string comes
/// from <see cref="UpdaterPlatform.Current"/>.
/// </summary>
public static class UpdaterHttpClient
{
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);

    public static HttpClient Instance { get; } = BuildInstance();

    private static HttpClient BuildInstance()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler)
        {
            Timeout = DefaultTimeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BuildUserAgent());
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");
        return client;
    }

    private static string BuildUserAgent()
    {
        string version;
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetCallingAssembly();
            version = asm.GetName().Version?.ToString() ?? "0.0.0.0";
        }
        catch
        {
            version = "0.0.0.0";
        }
        return UpdaterEndpoints.BuildUserAgent(version, UpdaterPlatform.Current);
    }
}

/// <summary>
/// Resolves the running platform string ("win-x64" / "win-x86") for use
/// in manifest entry filtering and the User-Agent header.
/// </summary>
public static class UpdaterPlatform
{
    public static string Current { get; } = Environment.Is64BitProcess ? "win-x64" : "win-x86";
}
