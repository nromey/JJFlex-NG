namespace JJFlexUpdater;

/// <summary>
/// Update channel naming locked 2026-05-03 in
/// memory/project_sprint29_updater_vision.md "Channel naming — rename FROM
/// 'daily' TO 'nightly'." The string values match the JSON keys in
/// <see cref="Manifest.AppManifest.Channels"/> so callers can use ToWireString
/// for round-trip without a lookup table.
/// </summary>
public enum UpdateChannel
{
    /// <summary>Stable releases — least frequent, public.</summary>
    Stable,

    /// <summary>Milestone betas.</summary>
    Beta,

    /// <summary>Overnight CI/CD builds — most frequent, "you may need to pick up the pieces."</summary>
    Nightly,
}

public static class UpdateChannelExtensions
{
    public static string ToWireString(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => "stable",
        UpdateChannel.Beta => "beta",
        UpdateChannel.Nightly => "nightly",
        _ => "stable",
    };

    public static string ToDisplayString(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => "Stable",
        UpdateChannel.Beta => "Beta",
        UpdateChannel.Nightly => "Nightly",
        _ => "Stable",
    };

    public static bool TryParse(string? value, out UpdateChannel channel)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "stable": channel = UpdateChannel.Stable; return true;
            case "beta": channel = UpdateChannel.Beta; return true;
            case "nightly":
            case "daily": // legacy spelling — accept on read, write as "nightly"
                channel = UpdateChannel.Nightly; return true;
            default: channel = UpdateChannel.Stable; return false;
        }
    }
}
