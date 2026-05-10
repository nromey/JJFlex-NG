using System.Reflection;
using JJFlexUpdater.Delta;
using JJFlexUpdater.Download;
using JJFlexUpdater.Hashing;
using JJFlexUpdater.Manifest;
using JJFlexUpdater.Net;
using JJFlexUpdater.Progress;
using JJFlexUpdater.Staging;

namespace JJFlexUpdater;

/// <summary>
/// Single front door for the updater. Wires the per-step pieces (manifest
/// fetch, hash inventory, delta plan, download, staging, helper handoff,
/// full-bundle fallback) into a sequence the UI and the background timer
/// both call. Stateless across runs — settings live in
/// <see cref="UpdaterSettings"/>, which the service loads + saves on demand.
///
/// Threading: every public method is async and safe to call from the UI
/// thread or from a background timer; progress callbacks fire on whatever
/// thread the work happens on, so the UI sink is responsible for marshaling
/// to the dispatcher.
/// </summary>
public sealed class UpdaterService
{
    private readonly ManifestFetcher _fetcher;
    private readonly InstallInventoryWalker _walker;
    private readonly DeltaDownloader _deltaDownloader;
    private readonly FullBundleDownloader _fullDownloader;
    private readonly Func<string> _installDirProvider;
    private readonly Func<string> _relaunchPathProvider;

    public UpdaterService() : this(
        new ManifestFetcher(),
        new InstallInventoryWalker(),
        new DeltaDownloader(),
        new FullBundleDownloader(),
        () => AppContext.BaseDirectory,
        DefaultRelaunchPath)
    {
    }

    public UpdaterService(
        ManifestFetcher fetcher,
        InstallInventoryWalker walker,
        DeltaDownloader deltaDownloader,
        FullBundleDownloader fullDownloader,
        Func<string> installDirProvider,
        Func<string> relaunchPathProvider)
    {
        _fetcher = fetcher;
        _walker = walker;
        _deltaDownloader = deltaDownloader;
        _fullDownloader = fullDownloader;
        _installDirProvider = installDirProvider;
        _relaunchPathProvider = relaunchPathProvider;
    }

    /// <summary>
    /// Phase 1 of the update flow: ask the data provider what's available.
    /// Returns null when no update is applicable (running version &gt;= channel
    /// latest, or nothing for our platform). Surfaces
    /// <see cref="UpdaterFetchException"/> for hard failures so the caller
    /// can decide whether to swallow (auto-check) or speak (manual-trigger).
    /// </summary>
    public async Task<AvailableUpdate?> CheckForUpdateAsync(
        UpdateChannel channel,
        string? currentVersion = null,
        CancellationToken cancellationToken = default)
    {
        currentVersion ??= ResolveCurrentVersion();

        var manifest = await _fetcher.FetchAppManifestAsync(cancellationToken).ConfigureAwait(false);
        if (!manifest.Channels.TryGetValue(channel.ToWireString(), out var channelInfo))
            return null;

        var entry = channelInfo.Entries
            .Where(e => string.Equals(e.Platform, UpdaterPlatform.Current, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => ParseVersion(e.Version))
            .FirstOrDefault();
        if (entry is null) return null;

        var latest = ParseVersion(entry.Version);
        var current = ParseVersion(currentVersion);
        if (latest <= current) return null;

        return new AvailableUpdate(channel, currentVersion, entry);
    }

    /// <summary>
    /// Phase 2 of the update flow: fetch the per-version file manifest,
    /// hash the install dir, and compute the delta plan. Returns null if
    /// nothing actually needs to change (manifest's hashes already match
    /// the local install — covers the case where a delta was applied
    /// out-of-band, e.g. partial reinstall).
    /// </summary>
    public async Task<UpdatePlan?> PlanUpdateAsync(
        AvailableUpdate update,
        IUpdaterProgressSink? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        progress ??= NullUpdaterProgressSink.Instance;

        progress.Report(new UpdaterProgressSnapshot(
            UpdaterPhase.FetchingManifest,
            $"Fetching file manifest for {update.Entry.Version}",
            0, 0, 0, 0));
        var fileManifest = await _fetcher
            .FetchFileManifestAsync(update.Entry.FileManifestUrl, cancellationToken)
            .ConfigureAwait(false);

        progress.Report(new UpdaterProgressSnapshot(
            UpdaterPhase.HashingLocal,
            "Hashing local files",
            0, 0, 0, 0));
        var inventory = await _walker
            .ScanAsync(_installDirProvider(), cancellationToken)
            .ConfigureAwait(false);

        progress.Report(new UpdaterProgressSnapshot(
            UpdaterPhase.PlanningDelta,
            "Comparing local install against manifest",
            0, 0, 0, 0));
        var deltaPlan = DeltaPlanner.Plan(inventory, fileManifest);
        if (!deltaPlan.HasWork) return null;

        return new UpdatePlan(update, fileManifest, deltaPlan);
    }

    /// <summary>
    /// Phase 3 of the update flow: download the delta into a fresh staging
    /// dir, write the handoff manifest, launch the helper exe. On
    /// success the helper is detached and running; the caller MUST
    /// invoke <see cref="System.Windows.Application.Current"/>.Shutdown
    /// (or its WinForms equivalent) immediately so the helper can swap
    /// files. On failure (delta-download or helper-launch), this method
    /// transparently falls back to <see cref="ExecuteFullBundleAsync"/>.
    /// </summary>
    public async Task<UpdateExecutionResult> ExecuteAsync(
        UpdatePlan plan,
        IUpdaterProgressSink? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        progress ??= NullUpdaterProgressSink.Instance;

        StagingDir staging;
        try
        {
            staging = StagingDir.Create();
            await _deltaDownloader
                .DownloadAllAsync(plan.Delta, staging.FilesDir, progress, cancellationToken)
                .ConfigureAwait(false);

            progress.Report(new UpdaterProgressSnapshot(
                UpdaterPhase.Staging, "Building handoff manifest", 0, 0, 0, 0));
            var handoff = HandoffManifestBuilder.Build(
                staging,
                installDir: _installDirProvider(),
                relaunchPath: _relaunchPathProvider(),
                plan: plan.Delta);
            await HandoffManifestBuilder.WriteAsync(staging, handoff, cancellationToken)
                                        .ConfigureAwait(false);

            progress.Report(new UpdaterProgressSnapshot(
                UpdaterPhase.HandingOff, "Launching updater helper", 0, 0, 0, 0));
            HelperLauncher.LaunchAndDetach(staging,
                new HelperLauncher.LaunchOptions
                {
                    InstallDir = _installDirProvider(),
                });

            return UpdateExecutionResult.HelperHandoff(staging.Root);
        }
        catch (Exception deltaFailure)
            when (deltaFailure is DeltaDownloadException
                                or HelperLaunchException
                                or UpdaterFetchException
                                or System.Net.Http.HttpRequestException)
        {
            progress.Report(new UpdaterProgressSnapshot(
                UpdaterPhase.FullBundleDownloading,
                "Delta path failed; falling back to full installer",
                0, 0, 0, 0));
            return await ExecuteFullBundleAsync(plan.AvailableUpdate, progress, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Force the full-bundle path (used when the delta path threw or the
    /// user explicitly chose "Reinstall"). Downloads the installer to a
    /// staging dir and launches it silently. Caller exits immediately
    /// after a successful return.
    /// </summary>
    public async Task<UpdateExecutionResult> ExecuteFullBundleAsync(
        AvailableUpdate update,
        IUpdaterProgressSink? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        progress ??= NullUpdaterProgressSink.Instance;

        var staging = StagingDir.Create();
        string installerPath = await _fullDownloader
            .DownloadAsync(update.Entry, staging.Root, progress, cancellationToken)
            .ConfigureAwait(false);

        progress.Report(new UpdaterProgressSnapshot(
            UpdaterPhase.FullBundleInstalling,
            "Launching installer",
            0, 0, 0, 0));
        InstallerLauncher.LaunchSilentAndDetach(installerPath);
        return UpdateExecutionResult.FullBundle(installerPath);
    }

    /// <summary>
    /// Decide whether the periodic / launch-time auto-check should fire.
    /// Centralized so the UI tab description and the actual gate line up.
    /// </summary>
    public static bool ShouldRunPeriodicCheck(UpdaterSettings settings, DateTimeOffset nowUtc)
    {
        if (!settings.PeriodicCheckWhileRunning) return false;
        if (!settings.LastCheckUtc.HasValue) return true;
        return nowUtc - settings.LastCheckUtc.Value >= TimeSpan.FromHours(2);
    }

    public static bool ShouldRunLaunchCheck(UpdaterSettings settings, DateTimeOffset nowUtc)
    {
        if (!settings.AutoCheckOnLaunch) return false;
        if (!settings.LastCheckUtc.HasValue) return true;
        return nowUtc - settings.LastCheckUtc.Value >= TimeSpan.FromHours(24);
    }

    private static string DefaultRelaunchPath()
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry is not null)
        {
            string? loc = entry.Location;
            if (!string.IsNullOrEmpty(loc) && File.Exists(loc)) return loc;
        }
        return Path.Combine(AppContext.BaseDirectory, "JJFlexRadio.exe");
    }

    private static string ResolveCurrentVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return asm.GetName().Version?.ToString() ?? "0.0.0.0";
        }
        catch
        {
            return "0.0.0.0";
        }
    }

    private static Version ParseVersion(string raw)
    {
        if (Version.TryParse(raw, out var v)) return v;
        return new Version(0, 0, 0, 0);
    }
}

/// <summary>
/// Result of <see cref="UpdaterService.CheckForUpdateAsync"/>: a newer
/// version on the user's selected channel, with the manifest entry in tow
/// for downstream display.
/// </summary>
public sealed class AvailableUpdate
{
    public UpdateChannel Channel { get; }
    public string CurrentVersion { get; }
    public AppManifestEntry Entry { get; }
    public string AvailableVersion => Entry.Version;
    public long FullInstallerSizeBytes => Entry.FullInstallerSizeBytes;

    public AvailableUpdate(UpdateChannel channel, string currentVersion, AppManifestEntry entry)
    {
        Channel = channel;
        CurrentVersion = currentVersion;
        Entry = entry;
    }
}

/// <summary>
/// Result of <see cref="UpdaterService.PlanUpdateAsync"/>: the file
/// manifest + delta plan for the selected available update. Carries the
/// upstream <see cref="AvailableUpdate"/> so the execute step can fall
/// back to the full bundle without re-fetching.
/// </summary>
public sealed class UpdatePlan
{
    public AvailableUpdate AvailableUpdate { get; }
    public FileManifest FileManifest { get; }
    public DeltaPlan Delta { get; }
    /// <summary>Wire bytes — sum of compressed .lzma blob sizes.</summary>
    public long DeltaBytes => Delta.DeltaBytes;
    /// <summary>On-disk install size after the swap.</summary>
    public long InstalledSizeBytes => Delta.InstalledSizeBytes;

    public UpdatePlan(AvailableUpdate available, FileManifest fileManifest, DeltaPlan delta)
    {
        AvailableUpdate = available;
        FileManifest = fileManifest;
        Delta = delta;
    }
}

/// <summary>Outcome of the execute step: either helper-handoff or full-bundle install.</summary>
public sealed class UpdateExecutionResult
{
    public UpdateExecutionMode Mode { get; }
    public string PathOfNote { get; }

    private UpdateExecutionResult(UpdateExecutionMode mode, string path)
    {
        Mode = mode;
        PathOfNote = path;
    }

    public static UpdateExecutionResult HelperHandoff(string stagingRoot)
        => new(UpdateExecutionMode.HelperHandoff, stagingRoot);

    public static UpdateExecutionResult FullBundle(string installerPath)
        => new(UpdateExecutionMode.FullBundleInstaller, installerPath);
}

public enum UpdateExecutionMode
{
    HelperHandoff,
    FullBundleInstaller,
}
