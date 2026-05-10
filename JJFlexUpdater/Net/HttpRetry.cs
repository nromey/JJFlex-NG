using System.Net.Http;

namespace JJFlexUpdater.Net;

/// <summary>
/// Shared retry policy for updater HTTP calls. Mirrors CrashReporter's
/// shape (commit 6ecd15f8): 3 attempts, 2s/4s exponential backoff, fail-fast
/// on 4xx (client errors don't get better with retries), retry on 5xx and
/// transient transport errors.
///
/// All callers go through this so timeout / backoff behavior is identical
/// across manifest fetches, file downloads, and full-bundle downloads.
/// </summary>
public static class HttpRetry
{
    public const int DefaultMaxAttempts = 3;

    /// <summary>
    /// Run <paramref name="send"/> up to <paramref name="maxAttempts"/> times
    /// with exponential backoff. The caller is responsible for disposing the
    /// returned <see cref="HttpResponseMessage"/>.
    ///
    /// Throws <see cref="HttpRequestException"/> on a definite failure
    /// (4xx response or attempts exhausted on transient errors). The
    /// inner exception (when present) preserves the last transport-level
    /// failure for diagnostic logs.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        if (maxAttempts < 1) maxAttempts = 1;

        Exception? lastException = null;
        string lastError = "unknown";

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var response = await send(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return response;

                int status = (int)response.StatusCode;
                lastError = $"status {status} {response.ReasonPhrase}";

                // 4xx is permanent — don't retry, dispose the response and
                // surface as failure. 5xx falls through to the retry path.
                if (status >= 400 && status < 500)
                {
                    response.Dispose();
                    throw new HttpRequestException($"Manifest fetch failed: {lastError}");
                }

                response.Dispose();
            }
            catch (HttpRequestException) when (attempt == maxAttempts)
            {
                throw; // permanent or final-attempt — let caller see it.
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient.Timeout produces TaskCanceledException; treat as
                // transient unless the caller cancelled.
                lastException = ex;
                lastError = "timeout";
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                lastError = $"{ex.GetType().Name}: {ex.Message}";
            }

            if (attempt < maxAttempts)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken)
                              .ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // Caller-initiated cancellation — stop retrying.
                    break;
                }
            }
        }

        throw new HttpRequestException(
            $"Updater request failed after {maxAttempts} attempt(s): {lastError}",
            lastException);
    }
}
