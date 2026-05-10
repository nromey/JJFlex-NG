namespace JJFlexUpdater.AutoCheck;

/// <summary>
/// Background timer that fires <see cref="BackgroundUpdateCheck"/> every
/// <see cref="CheckIntervalMinutes"/> while the app is running. The
/// 2-hour cadence comes from Noel's 2026-05-03 review: R2's zero-egress
/// makes periodic checks free for hosting, and a 2-hour window catches
/// nightly-channel testers who left the app running overnight.
///
/// Threading: timer ticks on a thread-pool thread. The host's
/// <see cref="OnUpdateFound"/> callback is invoked off-thread; callers are
/// responsible for marshaling to the UI dispatcher before showing dialogs.
///
/// Quiet on a connected radio: the host can pass an
/// <see cref="IRadioSessionGate"/> the checker consults each tick. When a
/// session is active, the manifest fetch is deferred — we don't want to
/// pop a "you have an update" dialog on top of a QSO. The deferral does
/// NOT update <see cref="UpdaterSettings.LastCheckUtc"/> so the next tick
/// retries promptly once the user disconnects.
/// </summary>
public sealed class PeriodicUpdateChecker : IDisposable
{
    public const int DefaultCheckIntervalMinutes = 120; // 2-hour cadence per 2026-05-03

    private readonly Timer _timer;
    private readonly IRadioSessionGate _sessionGate;
    private readonly Func<UpdaterSettings> _settingsProvider;
    private readonly Func<UpdaterService> _serviceFactory;
    private readonly Action<BackgroundUpdateCheck.Result> _onResult;
    private readonly TimeSpan _interval;
    private bool _disposed;

    public PeriodicUpdateChecker(
        Func<UpdaterSettings> settingsProvider,
        Action<BackgroundUpdateCheck.Result> onResult,
        IRadioSessionGate? sessionGate = null,
        Func<UpdaterService>? serviceFactory = null,
        TimeSpan? interval = null)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _onResult = onResult ?? throw new ArgumentNullException(nameof(onResult));
        _sessionGate = sessionGate ?? AlwaysAllowGate.Instance;
        _serviceFactory = serviceFactory ?? (() => new UpdaterService());
        _interval = interval ?? TimeSpan.FromMinutes(DefaultCheckIntervalMinutes);

        _timer = new Timer(OnTick, state: null, dueTime: _interval, period: _interval);
    }

    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }

    private void OnTick(object? state)
    {
        try
        {
            var settings = _settingsProvider();
            if (!UpdaterService.ShouldRunPeriodicCheck(settings, DateTimeOffset.UtcNow))
                return;
            if (!_sessionGate.MayPromptNow())
                return;

            var service = _serviceFactory();
            var result = BackgroundUpdateCheck
                .RunAsync(settings, service)
                .GetAwaiter().GetResult();
            _onResult(result);
        }
        catch
        {
            // Background timer must never crash the host. BackgroundUpdateCheck
            // already swallows fetch exceptions; this catch is for the
            // settings-provider / service-factory / callback edges.
        }
    }
}

/// <summary>
/// Lets the host tell the periodic checker "an update prompt would
/// interrupt something — defer this tick." Implemented by the host as a
/// thin wrapper over Rig.IsConnected (or whatever signal indicates an
/// active radio session).
/// </summary>
public interface IRadioSessionGate
{
    bool MayPromptNow();
}

internal sealed class AlwaysAllowGate : IRadioSessionGate
{
    public static AlwaysAllowGate Instance { get; } = new();
    public bool MayPromptNow() => true;
}
