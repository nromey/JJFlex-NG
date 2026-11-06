using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The helper's reply limit against a reader that falls behind but never
/// stops. Same class, same windowless rules.
/// </summary>
/// <remarks>
/// <para>The helper counts everything waiting for its writer, each message's
/// bytes plus 64. FrameOut and Heartbeat are dropped once 16 MB is waiting; a
/// reply (an acknowledgement or an Error) that would take the total past 32 MB
/// ends the connection with exit 6. So the limit is reached only by more than
/// 16 MB of replies going unread, whatever the reader's speed.</para>
/// <para>The stub's --reply-queue plays that accounting at a small scale (frames
/// dropped from half the limit, the connection ended past it) and reports the
/// most reply bytes it ever held. A reader slowed to 10 ms a message falls far
/// behind two engines' audio, so frames pile up to half the limit and are
/// dropped, and this is a reader the silence watchdog never fires on, because
/// it keeps reading. What keeps the replies small is the settings bounds: one
/// unanswered request per setting, under the connection's cap, and an answer
/// not yet read is still unanswered. These tests establish that for SettingAcks,
/// the only reply a running connection sends without end, under a settings
/// flood. They are the stub's model of the helper's queue, not the helper's
/// queue itself.</para>
/// </remarks>
public sealed partial class HelperLifecycleTests
{
    private const int ReplyLimitBytes = 256 * 1024;

    private static (long Peak, long Dropped)? LastReplyReport(HelperTestRig.TraceCapture trace, int pid)
    {
        string prefix = $"WdspHost: helper pid {pid} standard error: reply bytes peak ";
        var last = trace.Lines.LastOrDefault(l => l.StartsWith(prefix));
        if (last == null) return null;
        var parts = last[prefix.Length..].Split(' ');
        return (long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[3], CultureInfo.InvariantCulture));
    }

    /// <summary>Runs a settings flood on three settings over two engines while the reader is slow and both engines are fed.</summary>
    private static void SlowReaderFlood(HelperTestRig rig, WdspHelperEngine nnr, WdspHelperEngine nr2, int ms, Func<bool> stop)
    {
        rig.Host.DispatchHookForTest = _ => Thread.Sleep(10);
        try
        {
            using var feeder = new Feeder(nnr, nr2);
            var t = System.Diagnostics.Stopwatch.StartNew();
            double[] floors = { -40, -35, -30, -25 };
            string[] gains = { "linear", "log" };
            int i = 0;
            while (t.ElapsedMilliseconds < ms && !stop())
            {
                nnr.TrySetSetting("mask_floor_db", new EngineSettingValue(floors[i % floors.Length]));
                nr2.TrySetSetting("gain_method", new EngineSettingValue(0, gains[i % 2]));
                nr2.TrySetSetting("trained_t2", new EngineSettingValue(0.02 + (i % 28) * 0.01));
                i++;
            }
        }
        finally { rig.Host.DispatchHookForTest = null; }
    }

    /// <summary>
    /// A reader far behind the helper but still reading: frames are dropped at
    /// half the limit, replies stay at a handful, the connection lives, and the
    /// silence watchdog rightly never fires.
    /// </summary>
    [Fact]
    public void A_slow_reader_under_a_settings_flood_never_brings_the_reply_limit_near()
    {
        using var trace = new HelperTestRig.TraceCapture();
        using var rig = new HelperTestRig(_ => new[] { "--reply-queue", ReplyLimitBytes.ToString(CultureInfo.InvariantCulture) });
        var nnr = rig.Engine("nnr");
        var nr2 = rig.Engine("nr2");
        Assert.True(nnr.IsAvailable && nr2.IsAvailable);
        int pid = rig.Host.CurrentProcessId!.Value;
        SlowReaderFlood(rig, nnr, nr2, 4000, () => false);
        Thread.Sleep(300); // the stub's next report
        var report = LastReplyReport(trace, pid);
        _out.WriteLine($"stub report: {report}; sent {Interlocked.Read(ref rig.Host.SetSettingsSent)}, " +
            $"peak in flight {Interlocked.Read(ref rig.Host.PeakSettingsInFlight)}, state {rig.Host.State}");
        Assert.NotNull(report);
        // Positive control: the reader really fell behind, far enough to fill half the limit with frames.
        Assert.True(report!.Value.Dropped > 0, "the reader never fell far enough behind to matter: no frame was dropped");
        // Three settings, one unanswered request each, plus the two open acknowledgements: a few hundred bytes.
        Assert.InRange(report.Value.Peak, 1, 8 * 1024);
        Assert.Equal(WdspHelperState.Running, rig.Host.State);
        Assert.Equal(0, rig.Host.Failures);
        Assert.Single(rig.Host.LaunchedProcessIds);
    }

    /// <summary>
    /// The positive control for the test above: lift both settings bounds and
    /// the same slow reader lets the replies grow until the stub, like the
    /// helper, ends the connection with exit 6. So the arrangement can reach
    /// the limit, and it is the bounds that keep it away.
    /// </summary>
    [Fact]
    public void Without_the_settings_bounds_the_same_slow_reader_reaches_the_reply_limit()
    {
        using var trace = new HelperTestRig.TraceCapture();
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--reply-queue", ReplyLimitBytes.ToString(CultureInfo.InvariantCulture) } : Array.Empty<string>(),
            SlowRestart() with { MaxSettingsInFlight = 1_000_000, MaxInFlightPerSetting = int.MaxValue });
        var nnr = rig.Engine("nnr");
        var nr2 = rig.Engine("nr2");
        Assert.True(nnr.IsAvailable && nr2.IsAvailable);
        int pid = rig.Host.CurrentProcessId!.Value;
        SlowReaderFlood(rig, nnr, nr2, 8000, () => rig.Host.State != WdspHelperState.Running);
        HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Restarting, "the stub to end the connection at its reply limit", 8000);
        _out.WriteLine(nr2.UnavailabilityDetail);
        Assert.Equal(WdspCondition.FatalProtocolError, rig.Host.LastFailureCondition);
        HelperTestRig.Until(() => trace.Has(l => l.StartsWith($"WdspHost: helper pid {pid} standard error: reply limit: ")), "the stub to say which limit", 3000);
    }
}
