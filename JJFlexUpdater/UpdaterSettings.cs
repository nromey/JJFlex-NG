using JJTrace;
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

    /// <summary>Guards the warning below to one line per process. This is a
    /// property getter and can be read repeatedly; an unguarded trace here
    /// would flood the file with the same sentence.</summary>
    private static bool _warnedUnreadableChannel;

    public UpdateChannel Channel
    {
        get
        {
            // Falling back to Stable is deliberate and safe - a corrupt file
            // must never silently opt someone INTO nightly. But it is still a
            // silent demotion for anyone who chose Beta or Nightly, and this
            // setting decides which builds they receive, so it gets said once.
            if (!UpdateChannelExtensions.TryParse(ChannelWire, out var c))
            {
                if (!_warnedUnreadableChannel)
                {
                    _warnedUnreadableChannel = true;
                    Tracing.TraceLine(
                        $"UpdaterSettings: update channel '{ChannelWire}' is not one I "
                        + "recognise — using Stable. Any Beta or Nightly choice has been lost.",
                        System.Diagnostics.TraceLevel.Warning);
                }
            }
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
        catch (Exception ex)
        {
            // Defaults are the right recovery - the updater must keep working
            // with a damaged settings file. But this silently discards every
            // choice the operator made, including their channel, so it does not
            // get to happen quietly. The file is left in place deliberately:
            // overwriting it on the next Save destroys the only evidence of
            // what went wrong.
            Tracing.TraceLine(
                $"UpdaterSettings.Load: {path} is unreadable ({ex.Message}) — using defaults. "
                + "Any saved update preferences have been lost.",
                System.Diagnostics.TraceLevel.Error);
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
