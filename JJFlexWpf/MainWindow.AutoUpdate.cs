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
            dialog.ShowDialog();
        }));
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
