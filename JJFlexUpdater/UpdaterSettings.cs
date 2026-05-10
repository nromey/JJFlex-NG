using System.Text.Json;
using System.Text.Json.Serialization;

namespace JJFlexUpdater;

/// <summary>
/// User-facing updater preferences. Persisted to
/// <c>%APPDATA%\JJFlexRadio\update-settings.json</c> so they survive between
/// runs and an in-place delta swap (the file lives in user-writable space,
/// not the install dir). The Updates settings tab reads + writes through
/// this type; the orchestrator consults it on every check.
/// </summary>
public sealed class UpdaterSettings
{
    public const string DefaultFileName = "update-settings.json";

    /// <summary>Channel the user has selected (default Stable per friction-tax-friendly default).</summary>
    [JsonPropertyName("channel")]
    public string ChannelWire { get; set; } = UpdateChannel.Stable.ToWireString();

    /// <summary>Auto-check on launch (default true per Sprint 29 plan).</summary>
    [JsonPropertyName("auto_check_on_launch")]
    public bool AutoCheckOnLaunch { get; set; } = true;

    /// <summary>
    /// 2-hour periodic check while running (default true per Noel's
    /// 2026-05-03 review). R2's zero-egress cost makes this free to do.
    /// </summary>
    [JsonPropertyName("periodic_check_while_running")]
    public bool PeriodicCheckWhileRunning { get; set; } = true;

    /// <summary>
    /// Last time the updater finished a manifest fetch. Drives the "no
    /// duplicate check within a 2-hour window" guard for the periodic
    /// timer and the launch-time skip-if-recent rule.
    /// </summary>
    [JsonPropertyName("last_check_utc")]
    public DateTimeOffset? LastCheckUtc { get; set; }

    /// <summary>
    /// User has dismissed updates for this version — don't re-prompt
    /// until a newer version appears.
    /// </summary>
    [JsonPropertyName("skipped_version")]
    public string? SkippedVersion { get; set; }

    /// <summary>
    /// True once the user has confirmed they understand nightly is
    /// volatile. Keeps the consent dialog from re-appearing on
    /// subsequent channel toggles.
    /// </summary>
    [JsonPropertyName("nightly_consent_acknowledged")]
    public bool NightlyConsentAcknowledged { get; set; }

    public UpdateChannel Channel
    {
        get
        {
            UpdateChannelExtensions.TryParse(ChannelWire, out var c);
            return c;
        }
        set => ChannelWire = value.ToWireString();
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JJFlexRadio",
        DefaultFileName);

    public static UpdaterSettings Load(string? path = null)
    {
        path ??= DefaultPath();
        try
        {
            if (!File.Exists(path)) return new UpdaterSettings();
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UpdaterSettings>(json, Options) ?? new UpdaterSettings();
        }
        catch
        {
            return new UpdaterSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(this, Options);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Settings are best-effort; we never want a save failure to
            // crash the app or break an in-flight update.
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
