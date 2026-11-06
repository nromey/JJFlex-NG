using System;
using System.Linq;
using System.Threading;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The helper's standard error, recorded in JJ Flexible's trace and never
/// spoken (ruled 2026-11-06, S13). Same class, same windowless rules.
/// </summary>
public sealed partial class HelperLifecycleTests
{
    /// <summary>
    /// What the helper writes to standard error reaches the trace, named by its
    /// pid; and the reader ends when the helper does, which it can only do if
    /// no copy of the pipe's write end was left open in JJ Flexible.
    /// </summary>
    [Fact]
    public void The_helpers_standard_error_is_recorded_in_the_trace()
    {
        string mark = "stub stderr " + Guid.NewGuid().ToString("N");
        using var trace = new HelperTestRig.TraceCapture();
        var rig = new HelperTestRig(_ => new[] { "--stderr", mark });
        WdspHost host = rig.Host;
        try
        {
            var engine = rig.Engine("nr2");
            Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
            int pid = host.CurrentProcessId!.Value;
            HelperTestRig.Until(() => trace.Has(l => l == $"WdspHost: helper pid {pid} standard error: {mark}"), "the line in the trace", 5000);
            Assert.Equal(1, Volatile.Read(ref host.StderrReadersRunning));
        }
        finally { rig.Dispose(); }
        HelperTestRig.Until(() => Volatile.Read(ref host.StderrReadersRunning) == 0, "the standard-error reader to end with its helper", 5000);
    }

    /// <summary>A helper that writes without end costs a count, not the trace: 500 lines, then how many more.</summary>
    [Fact]
    public void Standard_error_past_500_lines_is_counted_not_traced()
    {
        using var trace = new HelperTestRig.TraceCapture();
        var rig = new HelperTestRig(_ => new[] { "--stderr-lines", "600" });
        WdspHost host = rig.Host;
        int pid;
        try
        {
            var engine = rig.Engine("nnr");
            Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
            pid = host.CurrentProcessId!.Value;
        }
        finally { rig.Dispose(); }
        HelperTestRig.Until(() => Volatile.Read(ref host.StderrReadersRunning) == 0, "the standard-error reader to end", 5000);
        var mine = trace.Lines.Where(l => l.StartsWith($"WdspHost: helper pid {pid} ")).ToArray();
        Assert.Equal(WdspHost.MaxStderrLinesTraced, mine.Count(l => l.Contains(" standard error: stub line ")));
        Assert.Contains($"WdspHost: helper pid {pid} standard error: stub line 500", mine);
        Assert.Contains($"WdspHost: helper pid {pid} wrote 100 more lines of standard error, not traced", mine);
    }

    /// <summary>
    /// The real helper's own words reach the trace: given an argument it does
    /// not know, it says so on standard error and exits 2.
    /// </summary>
    [Fact]
    public void Real_helper_what_it_says_on_standard_error_is_traced()
    {
        if (NoRealHelper()) return;
        using var trace = new HelperTestRig.TraceCapture();
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--no-such-argument" } : Array.Empty<string>(),
            HelperTestRig.Fast with { ConstructionWait = TimeSpan.FromSeconds(5) }, RealHelper());
        var engine = rig.Engine("nr2");
        HelperTestRig.Until(() => rig.Host.State == WdspHelperState.GaveUp, "the helper to exit");
        int pid = rig.Host.LaunchedProcessIds[0];
        HelperTestRig.Until(() => trace.Has(l => l.StartsWith($"WdspHost: helper pid {pid} standard error: ")), "the helper's own words in the trace", 5000);
        foreach (var l in trace.Lines.Where(l => l.StartsWith($"WdspHost: helper pid {pid} "))) _out.WriteLine(l);
        Assert.True(trace.Has(l => l.StartsWith($"WdspHost: helper pid {pid} standard error: ") && l.Contains("unknown argument")));
    }
}
