using System;
using System.Threading.Tasks;
using System.Windows;
using JJFlexUpdater;
using JJFlexUpdater.AutoCheck;
using JJTrace;

namespace JJFlexWpf;

/// <summary>
/// Sprint 29 Track D — auto-update wiring for the host. Lives in its own
/// partial so the giant MainWindow.xaml.cs doesn't grow another knob, and
/// so the Track D rip-and-replace pattern is concentrated in one place if
/// the architecture later moves into a hosted-service / DI shape.
///
/// Two trigger paths land here:
///   1. Launch-time check — fires once after MainWindow_Loaded, gated by
///      UpdaterService.ShouldRunLaunchCheck (24h since last check).
///   2. Periodic timer — every 2 hours while the app is running, gated
///      by ShouldRunPeriodicCheck (2h since last check) plus the radio
///      session gate so we don't pop a dialog mid-QSO.
/// </summary>
public partial class MainWindow
{
    private PeriodicUpdateChecker? _periodicUpdateChecker;
    private bool _updaterAutoCheckStarted;

    private void StartUpdaterAutoCheck()
    {
        if (_updaterAutoCheckStarted) return;
        _updaterAutoCheckStarted = true;

        try
        {
            // Launch-time check fires deferred so welcome speech and any
            // initial focus work finish first; otherwise the screen reader
            // can announce "checking for updates" mid-welcome.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                await RunLaunchCheckAsync().ConfigureAwait(false);
            });

            _periodicUpdateChecker = new PeriodicUpdateChecker(
                settingsProvider: () => UpdaterSettings.Load(),
                onResult: OnBackgroundUpdateResult,
                sessionGate: new RadioSessionGate(this));

            Unloaded += (_, _) => _periodicUpdateChecker?.Dispose();
        }
        catch (Exception ex)
        {
            Tracing.TraceLine(
                "StartUpdaterAutoCheck failed: " + ex.Message,
                System.Diagnostics.TraceLevel.Warning);
        }
    }

    private async Task RunLaunchCheckAsync()
    {
        try
        {
            var settings = UpdaterSettings.Load();
            if (!UpdaterService.ShouldRunLaunchCheck(settings, DateTimeOffset.UtcNow))
                return;

            var result = await BackgroundUpdateCheck.RunAsync(settings).ConfigureAwait(false);
            OnBackgroundUpdateResult(result);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine(
                "Launch-time update check failed: " + ex.Message,
                System.Diagnostics.TraceLevel.Warning);
        }
    }

    private void OnBackgroundUpdateResult(BackgroundUpdateCheck.Result result)
    {
        if (result.Error is not null)
        {
            // Background failures are silent — no nag for a network blip
            // on launch. Surface only via the trace log; manual check
            // still speaks failures since it's user-initiated.
            Tracing.TraceLine(
                $"Update check failed ({result.Reason}): {result.Error.Message}",
                System.Diagnostics.TraceLevel.Info);
            return;
        }

        if (!result.ShouldPrompt || result.AvailableUpdate is null)
            return;

        // Marshal to UI thread before opening the dialog.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Re-check session gate on the dispatcher: a connect could have
            // landed between the manifest fetch and now.
            if (RigControl != null && RigControl.IsConnected)
            {
                Tracing.TraceLine(
                    "Update prompt deferred: radio session active",
                    System.Diagnostics.TraceLevel.Info);
                return;
            }

            var dialog = new Dialogs.UpdateAvailableDialog(result.AvailableUpdate)
            {
                Owner = Window.GetWindow(this),
            };
            if (dialog.ShowDialog() == true)
                CloseForInstalledUpdate();
        }));
    }

    /// <summary>
    /// After a successful update hand-off, close the app so the waiting
    /// helper or installer can replace its files. Called by each route that
    /// shows <see cref="Dialogs.UpdateAvailableDialog"/>, only when it
    /// reported success, and only after that route's own window has closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Posted, not called.</b> The update dialog can sit over Settings,
    /// itself modal over the shell, so a close made where the result arrives
    /// would run inside two nested modal loops. Posting puts it on the
    /// dispatcher's queue, so it runs once the caller's stack has unwound.
    /// </para>
    /// <para>
    /// <b>Through the ordinary close.</b> It invokes
    /// <see cref="CloseShellCallback"/>, the same path the Exit menu item
    /// takes, so the shell's close still runs <see cref="RequestShutdown"/>,
    /// the exit questions (an unsaved contact, the still-running question)
    /// and the shutdown that follows (the farewell, the trace archive).
    /// Nothing here creates a WPF Application or ends the process directly.
    /// </para>
    /// <para>
    /// <b>A cancelled exit is said.</b> The operator may answer an exit
    /// question by staying. The helper then gives up on its own and changes
    /// nothing, so the app says plainly that the update was not installed
    /// and that it stays open, since the dialog has just said it was closing.
    /// </para>
    /// </remarks>
    public void CloseForInstalledUpdate()
    {
        Tracing.TraceLine("CloseForInstalledUpdate: update handed off; posting the shell close",
            System.Diagnostics.TraceLevel.Info);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(CloseShellForInstalledUpdate));
    }

    /// <summary>
    /// The posted half of <see cref="CloseForInstalledUpdate"/>: close the
    /// shell, and if the exit was cancelled, say the update did not install.
    /// </summary>
    internal void CloseShellForInstalledUpdate()
    {
        try
        {
            CloseShellCallback?.Invoke();
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("CloseForInstalledUpdate: the shell close threw: " + ex.Message,
                System.Diagnostics.TraceLevel.Error);
        }

        // RequestShutdown sets _isClosing only when the exit went ahead.
        if (_isClosing) return;

        Tracing.TraceLine("CloseForInstalledUpdate: the exit was cancelled; the update was not installed",
            System.Diagnostics.TraceLevel.Info);
        // DRAFT wording, connect.update.exit_cancelled.
        Radios.ScreenReaderOutput.Speak(
            Radios.Lexicon.Get("connect.update.exit_cancelled"),
            Radios.VerbosityLevel.Critical, interrupt: true);
    }

    /// <summary>
    /// Bridge between the periodic checker and JJF's connected-radio state.
    /// MayPromptNow returns false while a radio is connected so the
    /// "no update prompts during active radio sessions" rule holds for the
    /// background path too.
    /// </summary>
    private sealed class RadioSessionGate : IRadioSessionGate
    {
        private readonly MainWindow _window;
        public RadioSessionGate(MainWindow window) { _window = window; }

        public bool MayPromptNow()
        {
            try
            {
                return _window.RigControl == null || !_window.RigControl.IsConnected;
            }
            catch
            {
                // If we can't read the rig state for any reason, default to
                // "do not prompt" — failing closed is the friction-tax safe
                // choice.
                return false;
            }
        }
    }
}
