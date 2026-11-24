using JJTrace;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JJFlexUpdater;

/// <summary>
/// User-facing updater preferences. Persisted to
/// <c>%APPDATA%\JJFlexRadio\update-settings.json</c> so they survive between
/// runs and an in-place delta swap (the file lives in user-writable space,
/// not the install dir). A run isolated by <c>JJFLEX_CONFIG_DIR</c> keeps the
/// file in its throwaway tree instead (<see cref="DefaultPath"/>). The
/// Updates settings tab reads + writes through this type; the orchestrator
/// consults it on every check.
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

    /// <summary>
    /// The channel as an enum: a view over <see cref="ChannelWire"/>, which is
    /// the one that is stored.
    /// <para><b>It must stay out of the JSON.</b> Unannotated, it serialises
    /// under its own name "Channel", and the options below read names without
    /// regard to case, so it collides with <see cref="ChannelWire"/>'s
    /// "channel": System.Text.Json refuses the whole type and every
    /// <see cref="Save"/> threw. That is why no update choice was ever
    /// remembered between sessions until this attribute went on.
    /// <c>UpdaterSettingsPersistenceTests</c> holds every serialised type in
    /// the updater to the same rule.</para>
    /// </summary>
    [JsonIgnore]
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

    /// <summary>
    /// Where the preferences live: <c>%APPDATA%\JJFlexRadio</c> on an ordinary
    /// run, and the throwaway settings tree on a run that an accepted
    /// <c>JJFLEX_CONFIG_DIR</c> moved (<see cref="UpdaterStoreRoots"/>), so an
    /// isolated test run never reads or writes the operator's own file.
    /// </summary>
    public static string DefaultPath()
    {
        string? relocated = UpdaterStoreRoots.SettingsFolder;
        if (relocated != null) return Path.Combine(relocated, DefaultFileName);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JJFlexRadio",
            DefaultFileName);
    }

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

    /// <summary>
    /// Write the preferences. Never throws: a save failure must not crash the
    /// app or break an in-flight update. But it is traced, at Error, naming
    /// the file and the reason, because a failed save silently forgets every
    /// choice the operator just made. This catch was bare from the file's
    /// first commit, and that is how every save failing went unseen.
    /// </summary>
    public void Save(string? path = null)
    {
        if (!TrySave(path, out string? failure))
            Tracing.TraceLine(failure!, System.Diagnostics.TraceLevel.Error);
    }

    /// <summary>
    /// <see cref="Save"/>'s work, reporting instead of tracing: true when the
    /// file was written, otherwise false with the sentence <see cref="Save"/>
    /// traces. Never throws.
    /// </summary>
    public bool TrySave(string? path, out string? failure)
    {
        path ??= DefaultPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(this, Options);
            File.WriteAllText(path, json);
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            failure = $"UpdaterSettings.Save: could not write {path} ({ex.GetType().Name}: {ex.Message}). "
                + "The update preferences just chosen will be lost at the next start.";
            return false;
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
