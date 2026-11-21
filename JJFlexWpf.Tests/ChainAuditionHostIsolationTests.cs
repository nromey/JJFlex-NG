using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The audition's render has a helper host of its own (#262, Sprint 49 Track Q
/// phase B repair 1, R1; Codex's check of 3c61da16). On the live host, the
/// audition's helper engines would share the live chain's process, connection,
/// writer, restart count and failure handling: a crash in the audition would
/// take down the live channel, and choosing an audition engine would revive a
/// capped live host. Each test runs the probe on a SEPARATE host (the repair) and
/// on a SHARED host (the positive control, which shows the probe would have
/// caught the defect).
/// </summary>
/// <remarks>
/// Windowless: asserts <see cref="NoiseReductionPerChannelTests.AssertWindowless"/>;
/// engines on the MIT stub helper only (<see cref="HelperTestRig"/>), every stub
/// process checked gone. A rig's own engine stands in for the live chain's.
/// Nothing is played; no radio.
/// </remarks>
public sealed class ChainAuditionHostIsolationTests
{
    public ChainAuditionHostIsolationTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static TxNoiseSetup Nnr() => ChainAuditionTwinTests.Setup(neural: "nnr");

    /// <summary>A twin that owns a stub host per render, as the app's twin owns one; the hosts it made are listed for the checks.</summary>
    private static ChainAuditionTwin Owning(Func<int, IReadOnlyList<string>>? args, List<WdspHost> made) =>
        new(Nnr, () => null, () =>
        {
            var host = new WdspHost(HelperTestRig.StubPath, HelperTestRig.Fast, args) { PublishesLiveTelemetry = false };
            lock (made) made.Add(host);
            return new ChainAuditionTwin.RenderEngines(DenoiseEngineCatalog.ForOfflineRender(host), host);
        }, TimeSpan.FromSeconds(20));

    /// <summary>A twin on someone else's host: the shape before the repair, when the audition used the live host.</summary>
    private static ChainAuditionTwin Sharing(WdspHost host) =>
        new(Nnr, () => null, () => DenoiseEngineCatalog.ForOfflineRender(host), TimeSpan.FromSeconds(20));

    private static void AllGone(IEnumerable<WdspHost> hosts)
    {
        // Disposed by the twin when its render ended, not merely left to linger and idle out.
        foreach (var host in hosts)
            Assert.True((bool)typeof(WdspHost).GetField("_disposed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!,
                "the twin did not dispose the helper host it owned");
        foreach (var host in hosts)
            foreach (int pid in host.LaunchedProcessIds)
                if (!HelperTestRig.Gone(pid, 5000)) { HelperTestRig.Kill(pid); Assert.Fail("a render host's helper outlived its render: pid " + pid); }
    }

    private static (WdspHelperEngine Live, uint Channel, int? Pid) LiveEngine(HelperTestRig rig)
    {
        var live = rig.Engine("nnr");
        Assert.True(live.IsAvailable, live.UnavailabilityDetail);
        return (live, live.Channel!.Id, rig.Host.CurrentProcessId);
    }

    [Fact]
    public void A_helper_crash_in_the_audition_leaves_the_live_engine_alone_and_on_a_shared_host_it_does_not()
    {
        // Separate (the repair): the audition's helper crashes after 100 frames.
        using (var rig = new HelperTestRig())
        {
            var (live, channel, pid) = LiveEngine(rig);
            var made = new List<WdspHost>();
            var r = Owning(_ => new[] { "--crash-after", "100:9" }, made).Process(ChainAuditionTwinTests.Take(6), 48000, CancellationToken.None, null);
            Assert.False(r.Available);   // the audition's own render fails, as it should
            Assert.Single(made);
            Thread.Sleep(500);
            Assert.True(live.IsAvailable, live.UnavailabilityDetail);
            Assert.Equal(channel, live.Channel!.Id);
            Assert.Equal(pid, rig.Host.CurrentProcessId);
            Assert.Equal(0, rig.Host.Failures);
            AllGone(made);
        }
        // Shared (the positive control): the same crash on the live host.
        using (var rig = new HelperTestRig(n => n == 1 ? new[] { "--crash-after", "100:9" } : Array.Empty<string>()))
        {
            var (live, channel, pid) = LiveEngine(rig);
            Sharing(rig.Host).Process(ChainAuditionTwinTests.Take(6), 48000, CancellationToken.None, null);
            HelperTestRig.Until(() => rig.Host.CurrentProcessId != pid || rig.Host.LaunchedProcessIds.Count > 1,
                "the shared host restarting the live engine's helper after the audition's crash");
        }
    }

    /// <summary>
    /// The live host's restart count, which decides when it gives up (the cap),
    /// is not reset by an audition: on a shared host, attaching the audition's
    /// engine reset it to zero (<c>WdspHost.Attach</c>), so a failing live helper
    /// could be kept from its cap, or a capped one revived. Two crashes are
    /// counted against the live helper first, then the audition renders.
    /// </summary>
    [Fact]
    public void An_audition_leaves_the_live_hosts_restart_count_alone_and_on_a_shared_host_it_resets_it()
    {
        foreach (bool shared in new[] { false, true })
        {
            using var rig = new HelperTestRig(n => n <= 2 ? new[] { "--crash-after", "1:9" } : Array.Empty<string>());
            var live = rig.Engine("nnr");
            var input = new float[480]; var output = new float[480];
            HelperTestRig.Until(() => rig.Host.Failures == 2 && live.IsAvailable && rig.Host.StartsAttempted == 3,
                "two crashes counted against the live helper, then a healthy third start", ms: 20000,
                step: () => { if (live.IsAvailable) live.ProcessFrame(input, output); });
            var made = new List<WdspHost>();
            var r = (shared ? Sharing(rig.Host) : Owning(null, made)).Process(ChainAuditionTwinTests.Take(4), 48000, CancellationToken.None, null);
            Assert.True(r.Available, r.UnavailableKey);
            if (shared) Assert.True(rig.Host.Failures < 2, "the shared host's count was not reset, so this probe could not see the defect");
            else
            {
                Assert.Equal(2, rig.Host.Failures);
                Assert.Equal(3, rig.Host.StartsAttempted);
                AllGone(made);
            }
        }
    }

    /// <summary>
    /// And a capped live host is never touched: the twin asks the row's probe
    /// first, which answers "not running" for a capped host, so even on a shared
    /// host nothing is attached. On its own host the audition renders regardless.
    /// </summary>
    [Fact]
    public void A_capped_live_host_stays_capped_through_an_audition()
    {
        using var rig = new HelperTestRig(_ => new[] { "--exit-early", "9" });
        rig.Engine("nnr");
        HelperTestRig.Until(() => rig.Host.State == WdspHelperState.CapReached, "the live host capped", ms: 20000);
        int starts = rig.Host.StartsAttempted;
        var made = new List<WdspHost>();
        Assert.True(Owning(null, made).Process(ChainAuditionTwinTests.Take(4), 48000, CancellationToken.None, null).Available);
        Assert.Equal(ChainAuditionTwin.EngineUnavailableKey,
            Sharing(rig.Host).Process(ChainAuditionTwinTests.Take(4), 48000, CancellationToken.None, null).UnavailableKey);
        Thread.Sleep(300);
        Assert.Equal(WdspHelperState.CapReached, rig.Host.State);
        Assert.Equal(starts, rig.Host.StartsAttempted);
        AllGone(made);
    }

    [Fact]
    public void A_live_helper_failure_mid_render_leaves_the_audition_complete_and_on_a_shared_host_it_does_not()
    {
        using (var rig = new HelperTestRig())
        {
            LiveEngine(rig);
            int livePid = rig.Host.CurrentProcessId!.Value;
            var made = new List<WdspHost>();
            bool killed = false;
            var r = Owning(null, made).Process(ChainAuditionTwinTests.Take(6), 48000, CancellationToken.None,
                p => { if (!killed && p >= 0.3) { killed = true; HelperTestRig.Kill(livePid); } });
            Assert.True(killed);
            Assert.True(r.Available, r.UnavailableKey + " " + string.Join(", ", r.UnavailableArgs));
            AllGone(made);
        }
        using (var rig = new HelperTestRig())
        {
            LiveEngine(rig);
            int livePid = rig.Host.CurrentProcessId!.Value;
            bool killed = false;
            var r = Sharing(rig.Host).Process(ChainAuditionTwinTests.Take(6), 48000, CancellationToken.None,
                p => { if (!killed && p >= 0.3) { killed = true; HelperTestRig.Kill(livePid); } });
            Assert.True(killed);
            Assert.False(r.Available, "the shared render survived the live helper's death, so this probe could not see the defect");
        }
    }

    [Fact]
    public void The_apps_render_host_is_its_own_and_publishes_nothing_to_the_live_capture_policy()
    {
        using var host = ChainAuditionTwin.NewRenderHost();
        Assert.NotSame(WdspHost.Default, host);
        Assert.False(host.PublishesLiveTelemetry);
        Assert.Null(host.CostStore);
        Assert.Empty(host.LaunchedProcessIds);   // creating it starts nothing
        // The app's host still feeds the policy, and the handshake asks first.
        using (var apps = new WdspHost(null)) Assert.True(apps.PublishesLiveTelemetry);
        string source = File.ReadAllText(Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "WdspHost.cs"));
        Assert.Matches(new Regex(@"if \(PublishesLiveTelemetry\) Radios\.TxCallbackRate\.HelperFrameCostP99Nanos = "), source);
        Assert.Single(Regex.Matches(source, @"TxCallbackRate\.HelperFrameCostP99Nanos\s*="));
        // And ForApp builds its engines on such a host, never the default.
        string twin = File.ReadAllText(Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "ChainAuditionTwin.cs"));
        string forApp = twin.Substring(twin.IndexOf("internal static ChainAuditionTwin ForApp(", StringComparison.Ordinal));
        forApp = forApp.Substring(0, forApp.IndexOf("DefaultBuildWait);", StringComparison.Ordinal));
        Assert.Contains("NewRenderHost()", forApp);
        Assert.DoesNotContain("WdspHost.Default", forApp);
    }

    [Fact]
    public void The_twin_disposes_the_host_it_owns_when_the_render_ends_even_when_cancelled()
    {
        var made = new List<WdspHost>();
        using var cts = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => Owning(null, made)
            .Process(ChainAuditionTwinTests.Take(6), 48000, cts.Token, p => { if (p >= 0.3) cts.Cancel(); }));
        var host = Assert.Single(made);
        Assert.NotEmpty(host.LaunchedProcessIds);   // positive control: it really started a helper
        AllGone(made);
    }
}
