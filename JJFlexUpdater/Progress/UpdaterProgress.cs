namespace JJFlexUpdater.Progress;

/// <summary>
/// Phase of the overall update flow. Used by the UI to render and announce
/// progress at the right verbosity. Order is the natural sequence; the
/// progress reporter never goes backwards within a single update run.
/// </summary>
public enum UpdaterPhase
{
    Idle,
    FetchingManifest,
    HashingLocal,
    PlanningDelta,
    DownloadingFiles,
    Staging,
    HandingOff,
    FullBundleDownloading,
    FullBundleInstalling,
    Done,
    Failed,
}

/// <summary>
/// Snapshot of in-flight progress. Cheap value type — built fresh on every
/// report so the UI can bind without worrying about mutation across threads.
/// </summary>
public readonly record struct UpdaterProgressSnapshot(
    UpdaterPhase Phase,
    string Message,
    int FilesCompleted,
    int FilesTotal,
    long BytesCompleted,
    long BytesTotal);

/// <summary>
/// Sink for progress events. Implemented by the UI (e.g. the
/// "Update available" dialog) and the speech layer. The orchestrator
/// pushes snapshots as the update proceeds.
///
/// Implementations must be safe to call from background threads — the
/// dispatcher hop to the UI is the implementation's responsibility.
/// Per Track D scope: chatty verbosity announces percentages, normal /
/// terse announce milestones only.
/// </summary>
public interface IUpdaterProgressSink
{
    void Report(UpdaterProgressSnapshot snapshot);
}

/// <summary>
/// No-op sink for headless callers (auto-check on launch, command-line
/// tooling, tests). Avoids the null check on every Report call site.
/// </summary>
public sealed class NullUpdaterProgressSink : IUpdaterProgressSink
{
    public static NullUpdaterProgressSink Instance { get; } = new();
    public void Report(UpdaterProgressSnapshot snapshot) { }
}
