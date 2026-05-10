using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using JJFlexUpdater;
using JJFlexUpdater.Progress;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Sprint 29 Track D — Update available dialog. Reports current vs.
/// available version, channel, delta-vs-full size with savings indicator,
/// changelog link, and lets the user decide between Download &amp; install /
/// Skip this version / Cancel.
///
/// On Download &amp; install: planning + delta-download run inline; on
/// completion the helper exe is launched and the host app exits. On any
/// failure path the orchestrator transparently falls back to the full-bundle
/// installer per the Track D scope. Progress text updates a polite live
/// region so screen-reader users hear milestones without being interrupted
/// mid-typing.
/// </summary>
public partial class UpdateAvailableDialog : JJFlexDialog
{
    private readonly AvailableUpdate _update;
    private readonly UpdaterService _service;
    private readonly Dispatcher _dispatcher;
    private readonly UiProgressSink _sink;
    private CancellationTokenSource? _cts;

    public UpdateAvailableDialog(AvailableUpdate update)
        : this(update, new UpdaterService()) { }

    public UpdateAvailableDialog(AvailableUpdate update, UpdaterService service)
    {
        _update = update ?? throw new ArgumentNullException(nameof(update));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _sink = new UiProgressSink(this);

        InitializeComponent();
        PopulateInitialText();
    }

    private void PopulateInitialText()
    {
        HeadlineText.Text = $"A new {_update.Channel.ToDisplayString().ToLowerInvariant()} build is available.";
        VersionsText.Text =
            $"You're on {_update.CurrentVersion}; available is {_update.AvailableVersion}.";

        // Until we plan the delta we only know the full-bundle size. The
        // delta-vs-full breakdown lands once planning completes; until then
        // we lead with the conservative "full installer is N MB" line.
        SizeText.Text =
            $"Full installer: {Format.Bytes(_update.FullInstallerSizeBytes)}. " +
            "JJ Flex will try to download only what changed; size estimate updates after the manifest fetch.";

        if (string.IsNullOrEmpty(_update.Entry.ChangelogUrl))
        {
            ChangelogLinkText.Visibility = Visibility.Collapsed;
        }

        // Critical-level speech for the headline so the user always hears
        // there's an update regardless of speech verbosity.
        ScreenReaderOutput.Speak(
            $"Update available: {_update.AvailableVersion} on the {_update.Channel.ToDisplayString()} channel. " +
            $"Full installer is {Format.Bytes(_update.FullInstallerSizeBytes)}.",
            VerbosityLevel.Critical, interrupt: true);
    }

    private async void DownloadAndInstallButton_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        SetActionsEnabled(false);
        ProgressText.Text = "Planning update…";
        ScreenReaderOutput.Speak("Planning update", VerbosityLevel.Terse, interrupt: true);

        try
        {
            UpdatePlan? plan = await _service
                .PlanUpdateAsync(_update, _sink, _cts.Token)
                .ConfigureAwait(true);

            if (plan is null)
            {
                ProgressText.Text = "Already up to date — nothing to install.";
                ScreenReaderOutput.Speak(
                    "Already up to date.",
                    VerbosityLevel.Critical, interrupt: true);
                SetActionsEnabled(true);
                return;
            }

            UpdateSizeText(plan);

            UpdateExecutionResult result = await _service
                .ExecuteAsync(plan, _sink, _cts.Token)
                .ConfigureAwait(true);

            if (result.Mode == UpdateExecutionMode.HelperHandoff)
            {
                ScreenReaderOutput.Speak(
                    "Update is ready. Closing JJ Flex so the new version can install.",
                    VerbosityLevel.Critical, interrupt: true);
            }
            else
            {
                ScreenReaderOutput.Speak(
                    "Installer running. Closing JJ Flex.",
                    VerbosityLevel.Critical, interrupt: true);
            }

            DialogResult = true;
            Close();
            // Caller (App.OnExit / NativeMenuBar) decides on Application
            // shutdown so the host can wind down its own state. We just
            // close the dialog and let the enclosing flow shut down.
            Application.Current?.Shutdown();
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Cancelled.";
            ScreenReaderOutput.Speak("Update cancelled.",
                VerbosityLevel.Terse, interrupt: true);
            SetActionsEnabled(true);
        }
        catch (Exception ex)
        {
            ProgressText.Text = "Update failed: " + ex.Message;
            ScreenReaderOutput.Speak(
                "Update failed. The crash report mentions why.",
                VerbosityLevel.Critical, interrupt: true);
            MessageBox.Show(this,
                "Update failed.\n\n" + ex.Message,
                "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetActionsEnabled(true);
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = UpdaterSettings.Load();
        settings.SkippedVersion = _update.AvailableVersion;
        settings.Save();

        ScreenReaderOutput.Speak(
            $"Skipped version {_update.AvailableVersion}. We'll let you know about the next one.",
            VerbosityLevel.Terse, interrupt: true);
        DialogResult = false;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
        Close();
    }

    private void ChangelogHyperlink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_update.Entry.ChangelogUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _update.Entry.ChangelogUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort; if the shell hand-off fails the user can still
            // see the URL in the dialog text.
        }
    }

    private void UpdateSizeText(UpdatePlan plan)
    {
        long deltaWire = plan.DeltaBytes;            // compressed .lzma sum
        long fullWire = _update.FullInstallerSizeBytes; // NSIS .exe size
        if (fullWire <= 0) fullWire = plan.InstalledSizeBytes; // fallback

        string savings = Format.SavingsPercent(deltaWire, fullWire);
        SizeText.Text =
            $"Delta download: {Format.Bytes(deltaWire)}. " +
            $"Full installer would be {Format.Bytes(fullWire)} ({savings} smaller via delta).";

        ScreenReaderOutput.Speak(
            $"Download size {Format.Bytes(deltaWire)}, {savings} smaller than the full installer.",
            VerbosityLevel.Terse, interrupt: true);
    }

    private void SetActionsEnabled(bool enabled)
    {
        DownloadAndInstallButton.IsEnabled = enabled;
        SkipButton.IsEnabled = enabled;
        CancelButton.IsEnabled = true; // Cancel always available per dialog-escape rule
    }

    /// <summary>
    /// Marshals progress reports to the UI thread and updates the polite
    /// live region. Speech is throttled by the orchestrator's phase
    /// transitions; this just renders the text.
    /// </summary>
    private sealed class UiProgressSink : IUpdaterProgressSink
    {
        private readonly UpdateAvailableDialog _owner;

        public UiProgressSink(UpdateAvailableDialog owner) { _owner = owner; }

        public void Report(UpdaterProgressSnapshot snapshot)
        {
            _owner._dispatcher.BeginInvoke(new Action(() =>
            {
                _owner.ProgressText.Text = snapshot.Message;
            }));
        }
    }
}
