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
            if (dialog.ShowDialog() == true && dialog.Prepared != null)
                InstallDownloadedUpdate(dialog.Prepared);
        }));
    }

    /// <summary>
    /// After an update has been downloaded and staged (not launched), install
    /// it and close the app: ask the exit questions, and only on go launch
    /// the helper or installer and close (#1204, repair 1). Called by each
    /// route that shows <see cref="Dialogs.UpdateAvailableDialog"/>, only when
    /// it reported success, and only after that route's own window closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Posted, not called.</b> The update dialog can sit over Settings,
    /// itself modal over the shell, so this would otherwise run inside the
    /// caller's nested modal loops. Posting puts it on the dispatcher's queue,
    /// so it runs once the caller's own stack has unwound.
    /// </para>
    /// <para>
    /// <b>The questions come before the launch.</b> The helper waits only 30
    /// seconds for this process to exit, and gives up if it is still running.
    /// Launching first gave the operator 30 seconds, unannounced, to answer an
    /// exit question; and staying open did not stop a helper already running.
    /// So nothing is launched until the questions are answered, which they
    /// are once, with no deadline. See <see cref="InstallAndClose"/>.
    /// </para>
    /// </remarks>
    public void InstallDownloadedUpdate(PreparedUpdate prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        Tracing.TraceLine($"InstallDownloadedUpdate: update downloaded ({prepared.Mode}); posting the exit questions",
            System.Diagnostics.TraceLevel.Info);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => InstallAndClose(() => prepared.Launch())));
    }

    /// <summary>
    /// The exit questions alone, asked without committing the exit. Returns
    /// true to go, false to stay. Set by ApplicationEvents.vb to
    /// <c>AskExitQuestions</c>, the same two questions
    /// <c>ExitApplication</c> asks (an unsaved contact, then the still-running
    /// question), in the same order and with the same words.
    /// </summary>
    public Func<bool>? ExitQuestionsCallback { get; set; }

    /// <summary>
    /// One-shot: true only while the shell close that follows an update's
    /// answered exit questions is running. Read and cleared by
    /// <see cref="TakeExitQuestionsAnswered"/>.
    /// </summary>
    private bool _exitQuestionsAnswered;

    /// <summary>
    /// True when this exit's questions were already answered (an update asked
    /// them before launching), and clears the mark, so it serves exactly one
    /// exit. Called by <c>ExitApplication</c> before it asks the questions.
    /// </summary>
    public bool TakeExitQuestionsAnswered()
    {
        bool answered = _exitQuestionsAnswered;
        _exitQuestionsAnswered = false;
        return answered;
    }

    /// <summary>
    /// The posted half of <see cref="InstallDownloadedUpdate"/>, in order:
    /// ask the exit questions; on stay, launch nothing and say so; on go,
    /// launch, say the update is ready, and close the shell through its own
    /// close with the questions marked as answered for that one exit.
    /// </summary>
    /// <param name="launch">Starts the staged helper or installer
    /// (<see cref="PreparedUpdate.Launch"/>); a parameter so the order can be
    /// tested without starting a process.</param>
    internal void InstallAndClose(Func<UpdateExecutionResult> launch)
    {
        bool go;
        try
        {
            // No callback means no questions can be asked here; the close
            // below then asks them itself, as an ordinary exit would.
            go = ExitQuestionsCallback?.Invoke() ?? true;
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("InstallDownloadedUpdate: the exit questions threw; staying: " + ex.Message,
                System.Diagnostics.TraceLevel.Error);
            go = false;
        }

        if (!go)
        {
            // Nothing was launched, so this is true whatever happens next.
            Tracing.TraceLine("InstallDownloadedUpdate: the operator stayed; the update was not launched",
                System.Diagnostics.TraceLevel.Info);
            // DRAFT wording, connect.update.exit_cancelled.
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("connect.update.exit_cancelled"),
                Radios.VerbosityLevel.Critical, interrupt: true);
            return;
        }

        UpdateExecutionResult result;
        try
        {
            result = launch();
        }
        catch (Exception ex)
        {
            // A helper that exists but will not start. The app stays open on
            // the version it has; nothing is waiting for it to exit.
            Tracing.TraceLine("InstallDownloadedUpdate: the launch failed: " + ex.Message,
                System.Diagnostics.TraceLevel.Error);
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("connect.update.failed_speech", ("message", ex.Message)),
                Radios.VerbosityLevel.Critical, interrupt: true);
            return;
        }

        Tracing.TraceLine($"InstallDownloadedUpdate: launched ({result.Mode}); closing the shell",
            System.Diagnostics.TraceLevel.Info);
        Radios.ScreenReaderOutput.Speak(
            Radios.Lexicon.Get(result.Mode == UpdateExecutionMode.HelperHandoff
                ? "connect.update.helper_handoff"
                : "connect.update.installer_running"),
            Radios.VerbosityLevel.Critical, interrupt: true);

        // The questions were asked above only if a callback asked them.
        _exitQuestionsAnswered = ExitQuestionsCallback != null;
        try
        {
            // The Exit item's path: the shell's FormClosing runs
            // RequestShutdown and ExitApplication, which skips the questions
            // this once and runs everything else (the CW farewell, the radio
            // close, the trace archive), and MyApplication_Shutdown after it.
            CloseShellCallback?.Invoke();
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("InstallDownloadedUpdate: the shell close threw: " + ex.Message,
                System.Diagnostics.TraceLevel.Error);
        }
        finally
        {
            // Consumed by that one exit, or cleared here if it never reached
            // ExitApplication, so it cannot answer a later exit's questions.
            _exitQuestionsAnswered = false;
        }

        if (!_isClosing)
            Tracing.TraceLine("InstallDownloadedUpdate: the shell did not close after the launch",
                System.Diagnostics.TraceLevel.Error);
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
