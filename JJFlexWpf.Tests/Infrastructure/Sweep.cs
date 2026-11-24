using System.Windows;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>
/// Runs every dialog through every invariant, once per test run, and hands the
/// result to whichever test asks for it.
///
/// <para>One pass, not one per test: constructing eighty dialogs is the
/// expensive part, and the invariants are pure functions of the trees that pass
/// produces. Tests read the shared result and assert on their own slice of it,
/// so a failure names one invariant in one dialog.</para>
/// </summary>
public static class Sweep
{
    private static readonly Lazy<IReadOnlyList<DialogReport>> Lazy = new(RunAll, isThreadSafe: true);

    /// <summary>
    /// The strategy the suite actually uses, chosen by measurement - see
    /// README.md and the doc comments on <see cref="RealizationStrategy"/>. The
    /// two cheap strategies produce empty trees; this one produces a real tree
    /// and takes nothing from the operator.
    ///
    /// <para>The private desktop is still attempted at UI-thread startup and is
    /// still worth attempting, but it does not succeed under "dotnet test" -
    /// see <see cref="PrivateDesktop"/>. <see cref="UiThread.Isolation"/> reports
    /// what actually happened, and the report prints it.</para>
    /// </summary>
    public const RealizationStrategy Strategy = RealizationStrategy.OffScreenNonActivated;

    private static readonly TimeSpan PerDialogTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Modal windows the watchdog had to close during the sweep.</summary>
    public static IReadOnlyList<string> WatchdogInterventions { get; private set; } = Array.Empty<string>();

    public static IReadOnlyList<DialogReport> Reports => Lazy.Value;

    public static DialogReport ReportFor(string dialog)
        => Reports.FirstOrDefault(r => string.Equals(r.Dialog, dialog, StringComparison.Ordinal))
           ?? throw new InvalidOperationException($"No sweep report for {dialog}.");

    public static IEnumerable<Finding> FindingsFor(string dialog, Invariant invariant)
        => ReportFor(dialog).Findings.Where(f => f.Invariant == invariant);

    private static IReadOnlyList<DialogReport> RunAll()
    {
        var reports = new List<DialogReport>();

        foreach (var (name, reason) in DialogCatalog.DeclaredSkips)
            reports.Add(new DialogReport { Dialog = name, SkipReason = reason });

        UiThread.EnsureStarted();
        using var watchdog = new ModalWatchdog(UiThread.NativeThreadId, TimeSpan.FromSeconds(20));

        // Task #907: one dialog's exception must not take the sweep down. See
        // UiThread.Contain for how it used to end the whole test host.
        using (var containment = UiThread.Contain())
        {
            DialogReport? previous = null;
            foreach (var entry in DialogCatalog.Discover())
            {
                watchdog.Beat();
                var report = InspectContained(entry.Name, () => Inspect(entry), containment, previous, PerDialogTimeout);
                reports.Add(report);
                previous = report;
            }

            // Anything the last dialog left behind on the dispatcher runs now,
            // inside the containment, and is that dialog's.
            UiThread.Run(() => UiThread.Drain(passes: 1));
            if (previous != null) AddEscaped(previous, containment.Take(), afterInspection: true);
        }

        watchdog.Beat();
        lock (watchdog.Interventions) WatchdogInterventions = watchdog.Interventions.ToList();

        return reports.OrderBy(r => r.Dialog, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// One dialog's inspection with a containment open (task #907). An
    /// exception that escaped to the dispatcher while it ran becomes a finding
    /// on that dialog (invariant 8) and the sweep goes on. One that escaped
    /// between the previous dialog's end and this one's start is the previous
    /// dialog's, so it goes to <paramref name="previous"/>. A body that threw
    /// or returned no report is the harness failing, reported as before.
    /// </summary>
    internal static DialogReport InspectContained(string dialog, Func<DialogReport?> inspect,
                                                  ExceptionContainment containment, DialogReport? previous,
                                                  TimeSpan timeout)
    {
        var leftOver = containment.Take();
        if (previous != null) AddEscaped(previous, leftOver, afterInspection: true);

        DialogReport? report;
        try
        {
            report = UiThread.RunWithTimeout(inspect, timeout);
            if (report == null)
                report = new DialogReport
                {
                    Dialog = dialog,
                    SkipReason = "The harness itself failed on this dialog: the inspection returned no report.",
                };
        }
        catch (TimeoutException)
        {
            report = new DialogReport
            {
                Dialog = dialog,
                SkipReason = $"Did not finish being constructed and walked within {timeout.TotalSeconds:0} seconds. " +
                             "Most likely it blocks on something at construction time.",
            };
        }
        catch (Exception ex)
        {
            // Measured 2026-11-24: an exception thrown straight out of the body
            // arrives here, containment or not; the containment only takes the
            // ones the dispatcher raises through its own filter.
            report = new DialogReport
            {
                Dialog = dialog,
                SkipReason = "The harness itself failed on this dialog: " + TreeWalk.Describe(ex),
            };
        }

        // Kept on a skipped report too: invariant 8's test fails on it even
        // when the dialog was skipped (DialogInvariantTests.Assert).
        AddEscaped(report, containment.Take(), afterInspection: false);
        return report;
    }

    private static void AddEscaped(DialogReport report, IReadOnlyList<Exception> escaped, bool afterInspection)
    {
        foreach (var ex in escaped)
        {
            report.Findings.Add(new Finding(
                Invariant.NoExceptionEscapes,
                report.Dialog,
                "the dialog's own code",
                (afterInspection ? "After its inspection ended, an" : "An") +
                " exception escaped to the dispatcher with nothing in the dialog to catch it: " +
                TreeWalk.Describe(ex).TrimEnd('.') + ". The sweep contained it and went on."));
        }
    }

    /// <summary>One dialog, start to finish. Runs on the UI thread.</summary>
    private static DialogReport Inspect(DialogEntry entry)
    {
        var window = DialogCatalog.Construct(entry.Type, out var failure);
        if (window == null)
        {
            return new DialogReport
            {
                Dialog = entry.Name,
                SkipReason = "Could not be constructed in the disconnected state: " + (failure?.Reason ?? "unknown"),
            };
        }

        using var realized = RealizedDialog.Realize(window, Strategy);
        var snapshot = TreeWalk.Walk(realized.Root);
        var tab = FocusWalk.Walk(realized);

        var report = new DialogReport
        {
            Dialog = entry.Name,
            Strategy = realized.Strategy,
            LoadedFired = realized.LoadedFired,
            PeerCount = snapshot.Peers.Count,
            FocusableCount = snapshot.VisualElements.Count(e => e is not Window && TreeWalk.IsFocusableStop(e)),
            TabStopCount = tab.Order.Count,
            FocusWalkDiagnostic = string.IsNullOrEmpty(tab.Diagnostic) ? null : tab.Diagnostic,
        };

        report.Findings.AddRange(InvariantChecks.FocusableHasName(entry.Name, snapshot));
        report.Findings.AddRange(InvariantChecks.AutomationSubtreeComplete(entry.Name, realized, snapshot));
        report.Findings.AddRange(InvariantChecks.HelpTextNotEmpty(entry.Name, snapshot));
        report.Findings.AddRange(InvariantChecks.FocusConserved(entry.Name, tab));
        report.Findings.AddRange(InvariantChecks.UniqueAutomationIds(entry.Name, snapshot));
        report.Findings.AddRange(InvariantChecks.KeyboardReachable(entry.Name, snapshot, tab));
        report.Findings.AddRange(InvariantChecks.ArrowOnlyRadioOptions(entry.Name, tab));

        FocusWalk.Release();
        return report;
    }

    /// <summary>Names of every dialog the sweep attempted, for xunit theory data.</summary>
    public static IEnumerable<object[]> DialogNames()
        => DialogCatalog.Discover().Select(e => new object[] { e.Name });
}
