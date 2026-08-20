using JJFlexWpf.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using System.IO;

namespace JJFlexWpf.Tests;

/// <summary>
/// Tests about the harness rather than about the app: the focus-avoidance
/// measurement, the coverage accounting, and the inventory the sweep produces.
/// </summary>
public sealed class HarnessTests
{
    private readonly ITestOutputHelper _output;

    public HarnessTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The single decisive measurement, kept separate from the full probe so it
    /// can be run on its own in seconds: one plain dialog, realized with the
    /// strategy the suite uses, and then asked the two questions that matter.
    ///
    /// <para><b>Is the tree real?</b> Loaded ran, the automation tree has more
    /// than the window in it, and WPF focus can be placed and moved. If any of
    /// those is false the suite's findings are its own artefacts.</para>
    ///
    /// <para><b>Did anything leave the operator's hands?</b> The foreground
    /// window is recorded before and after. Nothing here injects synthetic
    /// input; focus is moved by calling MoveFocus on elements inside this
    /// process, and Win32 focus is per message queue, so a non-foreground
    /// process moving its own focus is invisible to everything else.</para>
    /// </summary>
    [Fact]
    public void A_dialog_becomes_real_without_taking_the_keyboard()
    {
        var entry = DialogCatalog.Discover().First(e => e.Name == "AboutProgramDialog");
        var foregroundBefore = Foreground.Current();

        var result = UiThread.RunWithTimeout(() =>
        {
            var window = DialogCatalog.Construct(entry.Type, out var failure);
            Assert.True(window != null, "Could not construct AboutProgramDialog: " + failure?.Reason);

            using var realized = RealizedDialog.Realize(window!, Sweep.Strategy);
            var snapshot = TreeWalk.Walk(realized.Root);
            var tab = FocusWalk.Walk(realized);
            FocusWalk.Release();

            return (realized.LoadedFired, Peers: snapshot.Peers.Count, tab.Executed, Stops: tab.Order.Count,
                    tab.MovesRequested, tab.FocusEventsObserved, tab.Diagnostic);
        }, TimeSpan.FromSeconds(60));

        var foregroundAfter = Foreground.Current();

        _output.WriteLine($"Strategy: {Sweep.Strategy}, desktop isolation: {UiThread.Isolation}.");
        _output.WriteLine(
            $"Loaded fired: {result.LoadedFired}; automation peers: {result.Peers}; focus movable: {result.Executed}; " +
            $"tab stops: {result.Stops}; {result.MovesRequested} moves produced {result.FocusEventsObserved} focus events. {result.Diagnostic}");
        _output.WriteLine($"Foreground before: {foregroundBefore}");
        _output.WriteLine($"Foreground after:  {foregroundAfter}");

        Assert.True(result.LoadedFired, "Loaded never fired, so the tree is not the one a screen reader would see.");
        Assert.True(result.Peers > 1, "The automation tree contained nothing below the window.");
        Assert.True(result.Executed, "WPF focus could not be placed in the window: " + result.Diagnostic);
        Assert.Equal(foregroundBefore.ProcessId, foregroundAfter.ProcessId);
    }

    /// <summary>
    /// The headline result. Runs all four candidate strategies and prints what
    /// each one actually produced, so the answer to "can this suite run while
    /// the operator works" is measured rather than asserted.
    /// </summary>
    [Fact]
    public void Focus_avoidance_strategies_are_measured()
    {
        var probes = StrategyProbe.RunAll();
        ProbeCache.Value = probes;

        foreach (var probe in probes)
        {
            _output.WriteLine(
                $"{probe.Strategy} / {probe.Dialog}: constructed={probe.Constructed}, loaded={probe.LoadedFired}, " +
                $"peers={probe.PeerCount}, focusable={probe.FocusableCount}, focusMovable={probe.FocusMovable}, " +
                $"tabStops={probe.TabStops}. {probe.Note}");
        }

        var chosen = probes.Where(p => p.Strategy == Sweep.Strategy).ToList();
        Assert.NotEmpty(chosen);
        Assert.True(
            chosen.Any(p => p.Constructed && p.PeerCount > 1),
            "The strategy this suite relies on produced no automation tree at all. " +
            "If this fails, the sweep's findings are harness artefacts and must not be believed.");
    }

    /// <summary>
    /// Coverage accounting. Not an assertion about the app - an assertion that
    /// the suite is honest about what it did and did not look at.
    /// </summary>
    [Fact]
    public void Every_dialog_is_either_inspected_or_skipped_with_a_reason()
    {
        var reports = Sweep.Reports;
        var discovered = DialogCatalog.Discover().Count + DialogCatalog.DeclaredSkips.Count;

        _output.WriteLine($"{reports.Count} dialogs accounted for out of {discovered} discovered.");
        foreach (var report in reports.Where(r => r.Skipped))
            _output.WriteLine($"SKIPPED {report.Dialog}: {report.SkipReason}");

        Assert.Equal(discovered, reports.Count);
        Assert.All(reports, r => Assert.True(
            r.Skipped ? !string.IsNullOrWhiteSpace(r.SkipReason) : true,
            $"{r.Dialog} was skipped without a reason."));
    }

    /// <summary>
    /// Writes the inventory. Always passes: the document is the deliverable, and
    /// the defects it lists are reported by the per-dialog tests.
    /// </summary>
    [Fact]
    public void Findings_inventory_is_written()
    {
        var reports = Sweep.Reports;
        var probes = ProbeCache.Value ?? StrategyProbe.RunAll();
        var surfaces = DelegateSurfaceScan.Surfaces;

        var text = ReportWriter.Build(reports, probes, surfaces);

        var root = RepoPaths.Root;
        if (root != null)
        {
            var path = Path.Combine(root, "docs", "planning", "active", "sprint33-tier1-findings.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            _output.WriteLine("Inventory written to " + path);
        }

        _output.WriteLine(text);
        Assert.NotEmpty(text);
    }

    private static class ProbeCache
    {
        public static IReadOnlyList<ProbeResult>? Value { get; set; }
    }
}
