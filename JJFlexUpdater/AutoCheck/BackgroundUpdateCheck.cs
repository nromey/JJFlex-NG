using JJFlexUpdater.Net;

namespace JJFlexUpdater.AutoCheck;

/// <summary>
/// Headless update checker for the launch-time and periodic-timer paths.
/// Returns a compact result the host can use to decide whether to surface
/// the Update available dialog, ignore the result, or speak a critical
/// failure. No UI, no speech — caller is responsible for that side.
///
/// This is the contract for the auto-check side; the manual "Check for
/// Updates" command (Tools menu and Settings → Updates → Check now) calls
/// the same UpdaterService directly because it has UI context to spend.
/// </summary>
public static class BackgroundUpdateCheck
{
    public sealed class Result
    {
        public AvailableUpdate? AvailableUpdate { get; init; }
        public bool ShouldPrompt { get; init; }
        public Exception? Error { get; init; }
        public string Reason { get; init; } = string.Empty;
    }

    /// <summary>
    /// Run the check and return a structured result. NEVER throws — every
    /// failure mode bubbles up via <see cref="Result.Error"/>. Caller may
    /// log, ignore, or surface as it sees fit.
    /// </summary>
    public static async Task<Result> RunAsync(
        UpdaterSettings settings,
        UpdaterService? service = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        service ??= new UpdaterService();

        try
        {
            var available = await service
                .CheckForUpdateAsync(settings.Channel, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            settings.LastCheckUtc = DateTimeOffset.UtcNow;
            settings.Save();

            if (available is null)
            {
                return new Result { ShouldPrompt = false, Reason = "up_to_date" };
            }

            // Honor SkippedVersion — user has already declined this exact version.
            if (string.Equals(settings.SkippedVersion, available.AvailableVersion,
                              StringComparison.OrdinalIgnoreCase))
            {
                return new Result
                {
                    AvailableUpdate = available,
                    ShouldPrompt = false,
                    Reason = "skipped_version",
                };
            }

            return new Result
            {
                AvailableUpdate = available,
                ShouldPrompt = true,
                Reason = "available",
            };
        }
        catch (UpdaterFetchException ex)
        {
            return new Result { Error = ex, Reason = "fetch_failed" };
        }
        catch (Exception ex)
        {
            return new Result { Error = ex, Reason = "unexpected" };
        }
    }
}
