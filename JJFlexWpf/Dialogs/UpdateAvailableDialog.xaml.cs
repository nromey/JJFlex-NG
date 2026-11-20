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
/// completion the update is staged but NOT launched, the dialog closes with a
/// true result carrying <see cref="Prepared"/>, and the caller asks the exit
/// questions, launches, and closes the app
/// (MainWindow.InstallDownloadedUpdate). On any
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

    /// <summary>
    /// True once the window has closed, by any route: Cancel, Escape, Alt+F4
    /// or the title bar. Set in <see cref="OnClosed"/>, on the UI thread, as
    /// the download is cancelled.
    /// </summary>
    private bool _windowClosed;

    /// <summary>
    /// The downloaded, staged and NOT launched update, set when the dialog
    /// closes with a true result. The caller launches it only after the exit
    /// questions are answered (MainWindow.InstallDownloadedUpdate).
    /// </summary>
    public PreparedUpdate? Prepared { get; private set; }

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
        HeadlineText.Text = Lexicon.Get("connect.update.headline",
            ("channel", _update.Channel.ToDisplayString().ToLowerInvariant()));
        VersionsText.Text = Lexicon.Get("connect.update.versions",
            ("currentVersion", _update.CurrentVersion), ("availableVersion", _update.AvailableVersion));

        // Until we plan the delta we only know the full-bundle size. The
        // delta-vs-full breakdown lands once planning completes; until then
        // we lead with the conservative "full installer is N MB" line.
        SizeText.Text = Lexicon.Get("connect.update.size_initial",
            ("fullSize", Format.Bytes(_update.FullInstallerSizeBytes)));

        if (string.IsNullOrEmpty(_update.Entry.ChangelogUrl))
        {
            ChangelogLinkText.Visibility = Visibility.Collapsed;
        }

        // Critical-level speech for the headline so the user always hears
        // there's an update regardless of speech verbosity.
        ScreenReaderOutput.Speak(
            Lexicon.Get("connect.update.available_speech",
                ("availableVersion", _update.AvailableVersion),
                ("channel", _update.Channel.ToDisplayString()),
                ("fullSize", Format.Bytes(_update.FullInstallerSizeBytes))),
            VerbosityLevel.Critical, interrupt: true);
    }

    private async void DownloadAndInstallButton_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        SetActionsEnabled(false);
        ProgressText.Text = Lexicon.Get("connect.update.planning_status");
        ScreenReaderOutput.Speak(Lexicon.Get("connect.update.planning_speech"), VerbosityLevel.Terse, interrupt: true);

        try
        {
            UpdatePlan? plan = await _service
                .PlanUpdateAsync(_update, _sink, _cts.Token)
                .ConfigureAwait(true);

            if (plan is null)
            {
                if (EndAfterClose()) return;
                ProgressText.Text = Lexicon.Get("connect.update.up_to_date_status");
                ScreenReaderOutput.Speak(
                    Lexicon.Get("connect.update.up_to_date_speech"),
                    VerbosityLevel.Critical, interrupt: true);
                SetActionsEnabled(true);
                return;
            }

            UpdateSizeText(plan);

            // Download and verify ONLY (#1204, repair 1). Nothing is launched
            // here: the helper waits just 30 seconds for this process to exit,
            // so it must not start until the exit questions are answered. The
            // caller takes Prepared and hands it to
            // MainWindow.InstallDownloadedUpdate, which asks the questions,
            // launches on go, says "Update is ready. Closing...", and closes.
            Prepared = await _service
                .PrepareAsync(plan, _sink, _cts.Token)
                .ConfigureAwait(true);

            // Closed while the last of the download finished: the operator
            // asked to cancel, so nothing is reported and the caller, which
            // saw a false result, installs nothing.
            if (EndAfterClose()) return;

            // Success is reported, never acted on here. This used to ask the
            // WPF Application to shut down, which does nothing in this app:
            // there is no WPF Application.
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            if (EndAfterClose()) return;
            ProgressText.Text = Lexicon.Get("connect.update.cancelled_status");
            ScreenReaderOutput.Speak(Lexicon.Get("connect.update.cancelled_speech"),
                VerbosityLevel.Terse, interrupt: true);
            SetActionsEnabled(true);
        }
        catch (Exception ex)
        {
            // A failure after a close is not reported either: the operator
            // asked to cancel, and an "Update failed" box minutes later, over
            // whatever they are doing, would say something untrue.
            if (EndAfterClose()) return;
            ProgressText.Text = Lexicon.Get("connect.update.failed_status", ("message", ex.Message));
            // Speak the reason itself — "the crash report mentions why" is a
            // dead end for a screen reader user (item 17 rule). The advisory
            // body is arrow-reviewable for the longer detail.
            ScreenReaderOutput.Speak(
                Lexicon.Get("connect.update.failed_speech", ("message", ex.Message)),
                VerbosityLevel.Critical, interrupt: true);
            AdvisoryDialog.Show(Lexicon.Get("connect.update.failed_title"),
                Lexicon.Get("connect.update.failed_body", ("message", ex.Message)));
            SetActionsEnabled(true);
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = UpdaterSettings.Load();
        settings.SkippedVersion = _update.AvailableVersion;
        settings.Save();

        ScreenReaderOutput.Speak(
            Lexicon.Get("connect.update.skipped", ("version", _update.AvailableVersion)),
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

    /// <summary>
    /// Any close cancels a running download, the way the Cancel button does
    /// (#1216). Escape never clicks the Cancel button: the base dialog's
    /// window-level Escape handler closes the window itself, so before this
    /// the download ran on behind a closed window. Done here, not in
    /// <see cref="JJFlexDialog"/>, whose Escape handling every dialog shares.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _windowClosed = true;
        _cts?.Cancel();
        base.OnClosed(e);
    }

    /// <summary>What the download's continuation does once the download ends.</summary>
    internal enum DownloadEndAnswer
    {
        /// <summary>The window is open: report the outcome as before.</summary>
        Report,

        /// <summary>The window has closed: say only "Update cancelled.", and touch neither the result nor any box.</summary>
        SayCancelledOnly,
    }

    /// <summary>
    /// The one decision behind <see cref="EndAfterClose"/>: once the window
    /// has closed, whatever the download did (finished, was cancelled, or
    /// failed), the operator asked to cancel, so the end says the cancelled
    /// sentence and nothing else. Setting the result on a closed window
    /// throws, and that throw used to land in the failure path and open an
    /// "Update failed" box.
    /// </summary>
    internal static DownloadEndAnswer AfterDownload(bool windowClosed) =>
        windowClosed ? DownloadEndAnswer.SayCancelledOnly : DownloadEndAnswer.Report;

    /// <summary>
    /// True, having said "Update cancelled.", when the window has already
    /// closed; false when the caller should report as before.
    /// </summary>
    private bool EndAfterClose()
    {
        if (AfterDownload(_windowClosed) != DownloadEndAnswer.SayCancelledOnly) return false;
        ScreenReaderOutput.Speak(Lexicon.Get("connect.update.cancelled_speech"),
            VerbosityLevel.Terse, interrupt: true);
        return true;
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
        SizeText.Text = Lexicon.Get("connect.update.size_delta",
            ("deltaSize", Format.Bytes(deltaWire)), ("fullSize", Format.Bytes(fullWire)),
            ("savings", savings));

        ScreenReaderOutput.Speak(
            Lexicon.Get("connect.update.size_speech",
                ("deltaSize", Format.Bytes(deltaWire)), ("savings", savings)),
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
