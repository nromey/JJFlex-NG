using System.IO;
using System.Windows;
using System.Windows.Controls;
using JJFlexWpf.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Task #907: one dialog whose own code throws must not take the sweep, or the
/// test host, down with it. On 2026-11-06 and 2026-11-23 a Loaded handler that
/// threw (StationLookupWindow, on a missing <c>cty_wt.dat</c>) ended the whole
/// run with 0xC000041D and no dialog reported anything.
///
/// <para><b>BUILDS REAL WINDOWS.</b> Run only through <c>tools/deskrun</c>, one
/// class per run; never in the windowless filter.</para>
/// </summary>
public sealed class SweepContainmentTests
{
    private readonly ITestOutputHelper _output;

    public SweepContainmentTests(ITestOutputHelper output) => _output = output;

    private const string PlantedMessage = "planted by SweepContainmentTests";

    /// <summary>A window whose own Loaded handler throws, as StationLookupWindow's did.</summary>
    private sealed class ThrowsInLoadedWindow : Window
    {
        public ThrowsInLoadedWindow()
        {
            Content = new Button { Content = "Nothing here" };
            Loaded += (_, _) => throw new FileNotFoundException(PlantedMessage, "cty_wt.dat");
        }
    }

    private sealed class PlainWindow : Window
    {
        public PlainWindow() => Content = new Button { Content = "Plain" };
    }

    private static DialogReport RealizeAndReport(Window window, string name)
    {
        using var realized = RealizedDialog.Realize(window, Sweep.Strategy);
        return new DialogReport { Dialog = name, LoadedFired = realized.LoadedFired, Strategy = realized.Strategy };
    }

    [Fact]
    public void A_dialog_whose_Loaded_throws_becomes_a_finding_and_the_next_dialog_is_still_inspected()
    {
        UiThread.EnsureStarted();
        _output.WriteLine(UiThread.VerdictLine);

        DialogReport thrower, next;
        using (var containment = UiThread.Contain())
        {
            thrower = Sweep.InspectContained("ThrowsInLoadedWindow",
                () => RealizeAndReport(new ThrowsInLoadedWindow(), "ThrowsInLoadedWindow"),
                containment, previous: null, TimeSpan.FromSeconds(45));
            next = Sweep.InspectContained("PlainWindow",
                () => RealizeAndReport(new PlainWindow(), "PlainWindow"),
                containment, previous: thrower, TimeSpan.FromSeconds(45));
        }

        foreach (var finding in thrower.Findings.Concat(next.Findings)) _output.WriteLine(finding.ToString());
        _output.WriteLine($"Thrower: skipped {thrower.Skipped} ({thrower.SkipReason}), Loaded fired {thrower.LoadedFired}.");
        _output.WriteLine($"Next: skipped {next.Skipped} ({next.SkipReason}), Loaded fired {next.LoadedFired}.");

        Assert.False(thrower.Skipped, "The throwing dialog was skipped rather than reported: " + thrower.SkipReason);
        var escaped = Assert.Single(thrower.Findings, f => f.Invariant == Invariant.NoExceptionEscapes);
        Assert.Contains("FileNotFoundException", escaped.Detail, StringComparison.Ordinal);
        Assert.Contains(PlantedMessage, escaped.Detail, StringComparison.Ordinal);

        // The positive control: the sweep went on, and the dialog after the
        // thrower was inspected normally and carries nothing of the thrower's.
        Assert.False(next.Skipped, "The dialog after the thrower was not inspected: " + next.SkipReason);
        Assert.True(next.LoadedFired, "The dialog after the thrower never loaded.");
        Assert.Empty(next.Findings);
    }

    /// <summary>
    /// What a containment does to an exception thrown straight out of the
    /// inspection body rather than from inside a window procedure: either way
    /// the dialog must come back as the harness failing, never as a pass.
    /// </summary>
    [Fact]
    public void An_inspection_that_itself_throws_is_still_reported_as_the_harness_failing()
    {
        UiThread.EnsureStarted();

        DialogReport report;
        using (var containment = UiThread.Contain())
        {
            // Which way it goes is measured and printed, not assumed.
            Exception? reachedCaller = null;
            try { UiThread.RunWithTimeout<object?>(() => throw new InvalidOperationException("probe"), TimeSpan.FromSeconds(45)); }
            catch (Exception ex) { reachedCaller = ex; }
            var contained = containment.Take();
            _output.WriteLine($"A body that throws inside a containment: reached the caller {reachedCaller != null}, " +
                              $"contained {contained.Count}.");

            report = Sweep.InspectContained("HarnessThrows",
                () => throw new InvalidOperationException("planted harness failure"),
                containment, previous: null, TimeSpan.FromSeconds(45));
        }

        _output.WriteLine($"Skipped {report.Skipped}: {report.SkipReason}");
        Assert.True(report.Skipped);
        Assert.Contains("The harness itself failed on this dialog", report.SkipReason, StringComparison.Ordinal);
        Assert.Contains("planted harness failure", report.SkipReason, StringComparison.Ordinal);
    }

    /// <summary>Outside a containment nothing changed: an exception thrown in a body still reaches the caller.</summary>
    [Fact]
    public void Without_a_containment_an_exception_in_a_body_still_reaches_the_caller()
    {
        UiThread.EnsureStarted();
        var thrown = Assert.ThrowsAny<Exception>(() => UiThread.Run(() => throw new InvalidOperationException("planted")));
        _output.WriteLine(thrown.ToString());
        Assert.Contains("planted", thrown.Message + thrown.InnerException?.Message, StringComparison.Ordinal);
    }
}
