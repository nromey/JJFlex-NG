using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The helper's lifecycle over the MIT stub: restart after a stall or a
/// crash, the cap, missing, blocked and mismatched helpers, the probe, and
/// the never-orphaned guarantees. Windowless; every stub process hidden, in
/// a job, and gone at the end.
/// </summary>
public sealed class HelperLifecycleTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public HelperLifecycleTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    /// <summary>Feeds an engine from a background thread, as an audio thread would, until disposed.</summary>
    private sealed class Feeder : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        public volatile bool SawUnavailable;
        public Unavailability Seen = Unavailability.None;
        public int LatencyChanges;

        public Feeder(params WdspHelperEngine[] engines)
        {
            int latency = engines[0].LatencySamples;
            _thread = new Thread(() =>
            {
                var input = new float[480]; var output = new float[480];
                while (!_stop)
                {
                    foreach (var e in engines)
                    {
                        if (e.LatencySamples != latency) Interlocked.Increment(ref LatencyChanges);
                        if (e.IsAvailable) e.ProcessFrame(input, output);
                        else { Seen = e.Unavailability; SawUnavailable = true; }
                    }
                    Thread.Sleep(5);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Dispose() { _stop = true; _thread.Join(); }
    }

    [Theory]
    [InlineData("--stall-after", "10:1500")]
    [InlineData("--crash-after", "10:9")]
    public void A_stall_or_a_crash_restarts_the_helper_and_the_same_engines_come_back(string fault, string how)
    {
        using var rig = new HelperTestRig(n => n == 1 ? new[] { fault, how } : Array.Empty<string>());
        var left = rig.Engine("nnr");
        var right = rig.Engine("nnr");
        Assert.True(left.TrySetSetting("mask_floor_db", new EngineSettingValue(-30)));
        Assert.True(right.TrySetSetting("mask_floor_db", new EngineSettingValue(-30)));
        HelperTestRig.Until(() => Interlocked.Read(ref rig.Host.SettingAcksReceived) == 2, "both setting acks");
        var firstPid = rig.Host.CurrentProcessId;
        using (var feeder = new Feeder(left, right))
        {
            HelperTestRig.Until(() => feeder.SawUnavailable, "the helper to go down");
            Assert.Equal(Unavailability.HelperNotRunning, feeder.Seen);
            HelperTestRig.Until(() => left.IsAvailable && right.IsAvailable && rig.Host.CurrentProcessId != firstPid, "both channels back on a new helper");
            Assert.Equal(0, feeder.LatencyChanges);
        }
        Assert.Equal(2, rig.Host.LaunchedProcessIds.Count);
        Assert.Equal(1, rig.Host.Failures);
        Assert.Equal(WdspHelperState.Running, rig.Host.State);
        // Same objects, same delay, settings carried in the reopen rather than resent.
        Assert.Equal((3408, 3408), (left.LatencySamples, right.LatencySamples));
        Assert.Equal(-30, left.GetSetting("mask_floor_db").Number);
        Assert.Equal(-30, right.GetSetting("mask_floor_db").Number);
        Assert.Equal(2, Interlocked.Read(ref rig.Host.SetSettingsSent));
        Assert.Equal(new uint[] { 1, 2 }, new[] { left.Channel!.Id, right.Channel!.Id }.OrderBy(x => x));
        // And it processes again.
        var input = new float[480]; var output = new float[480];
        for (int i = 0; i < 12; i++) { HelperTestRig.Pace(left); left.ProcessFrame(input, output); }
        for (int i = 0; i < 10; i++) { HelperTestRig.Pace(left); Assert.True(left.ProcessFrame(input, output)); }
    }

    [Fact]
    public void Three_restarts_then_no_more_until_a_retry_and_one_event_each_way()
    {
        using var rig = new HelperTestRig(n => n <= 4 ? new[] { "--crash-after", "1:9" } : Array.Empty<string>());
        var events = new ConcurrentQueue<WdspHelperStateChangedEventArgs>();
        rig.Host.StateChanged += (_, e) => events.Enqueue(e);
        var engine = rig.Engine("nr2");
        using (new Feeder(engine))
        {
            HelperTestRig.Until(() => rig.Host.State == WdspHelperState.CapReached, "the cap");
            Assert.Equal(4, rig.Host.LaunchedProcessIds.Count); // the first run and three restarts
            Thread.Sleep(1000);
            Assert.Equal(4, rig.Host.LaunchedProcessIds.Count); // no fifth attempt, no loop
            Assert.Equal(WdspHelperState.CapReached, rig.Host.State);
            Assert.False(engine.IsAvailable);
            Assert.Equal(Unavailability.HelperNotRunning, engine.Unavailability);
            Assert.Equal(3408, engine.LatencySamples); // reported while down
            HelperTestRig.Until(() => events.Count == 1, "the cap event");
            var cap = Assert.Single(events);
            Assert.Equal(WdspHelperStateChange.CapReached, cap.Change);
            Assert.Equal(new[] { "nr2" }, cap.Engines);

            rig.Host.Retry();
            HelperTestRig.Until(() => engine.IsAvailable, "the helper back after the retry");
            HelperTestRig.Until(() => events.Count == 2, "the back event");
            Assert.Equal(WdspHelperStateChange.BackAfterCap, events.Last().Change);
            Assert.Equal(5, rig.Host.LaunchedProcessIds.Count);
            Assert.Equal(0, rig.Host.Failures);
        }
        Thread.Sleep(300);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void A_new_selection_resets_the_restart_count()
    {
        using var rig = new HelperTestRig(n => n <= 2 ? new[] { "--crash-after", "1:9" } : Array.Empty<string>());
        var engine = rig.Engine("nr2");
        using (new Feeder(engine))
            HelperTestRig.Until(() => rig.Host.LaunchedProcessIds.Count == 3 && engine.IsAvailable, "two failures and a good third start");
        Assert.Equal(2, rig.Host.Failures);
        using var another = rig.Engine("nnr");
        Assert.True(another.IsAvailable);
        Assert.Equal(0, rig.Host.Failures);
    }

    [Fact]
    public void A_missing_helper_is_missing_counts_nothing_never_retries_and_the_probe_starts_nothing()
    {
        string nowhere = Path.Combine(Path.GetTempPath(), "jjflex-no-such-helper-" + Guid.NewGuid().ToString("N"), "jjflex-wdsp-helper.exe");
        using var rig = new HelperTestRig(path: nowhere);
        Assert.Equal(Unavailability.HelperMissing, rig.Host.Probe("nnr"));
        Assert.Equal(0, rig.Host.StartsAttempted);
        var engine = rig.Engine("nnr");
        Assert.False(engine.IsAvailable);
        Assert.Equal(Unavailability.HelperMissing, engine.Unavailability);
        Assert.Equal(0, engine.LatencySamples);
        Assert.Equal(WdspHelperState.Missing, rig.Host.State);
        Assert.Equal(0, rig.Host.Failures);
        Thread.Sleep(600);
        Assert.Equal(1, rig.Host.StartsAttempted);
        Assert.Empty(rig.Host.LaunchedProcessIds);
        Assert.Equal(Unavailability.HelperMissing, rig.Host.Probe("nnr"));
        Assert.Equal(1, rig.Host.StartsAttempted);
        // A new selection tries again (and finds it still missing).
        using var again = rig.Engine("nr2");
        Assert.Equal(2, rig.Host.StartsAttempted);
        Assert.Equal(Unavailability.HelperMissing, again.Unavailability);
    }

    [Fact]
    public void A_helper_windows_refuses_to_start_is_missing_with_blocked_in_its_detail_and_is_never_retried()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-blocked-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "jjflex-wdsp-helper.exe");
        File.Copy(HelperTestRig.StubPath, exe);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ExecuteFile, AccessControlType.Deny);
        var info = new FileInfo(exe);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            using var rig = new HelperTestRig(path: exe);
            var engine = rig.Engine("nnr");
            Assert.Equal(Unavailability.HelperMissing, engine.Unavailability);
            Assert.Contains("blocked", engine.UnavailabilityDetail);
            Assert.Equal(WdspHelperState.Blocked, rig.Host.State);
            Assert.Equal(0, rig.Host.Failures);
            Thread.Sleep(600);
            Assert.Equal(1, rig.Host.StartsAttempted);
            Assert.Empty(rig.Host.LaunchedProcessIds);
            Assert.Equal(Unavailability.HelperMissing, rig.Host.Probe("nnr"));
            Assert.Equal(1, rig.Host.StartsAttempted);
        }
        finally
        {
            acl = info.GetAccessControl();
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("--reject")]
    [InlineData("--invalid-hello")]
    public void A_rejected_or_unreadable_handshake_is_a_mismatch_that_counts_nothing_and_never_retries(string fault)
    {
        using var rig = new HelperTestRig(_ => new[] { fault });
        var engine = rig.Engine("nnr");
        Assert.False(engine.IsAvailable);
        Assert.Equal(Unavailability.HelperVersionMismatch, engine.Unavailability);
        Assert.Equal(WdspHelperState.VersionMismatch, rig.Host.State);
        Assert.Equal(0, rig.Host.Failures);
        Thread.Sleep(600);
        Assert.Single(rig.Host.LaunchedProcessIds);
        Assert.Equal(Unavailability.HelperVersionMismatch, rig.Host.Probe("nnr"));
        Assert.Single(rig.Host.LaunchedProcessIds);
    }

    [Fact]
    public void An_engine_the_helper_does_not_offer_is_a_mismatch_naming_it_while_the_other_runs()
    {
        using var rig = new HelperTestRig(_ => new[] { "--omit", "nnr" });
        var nr2 = rig.Engine("nr2");
        Assert.True(nr2.IsAvailable);
        var nnr = rig.Engine("nnr");
        Assert.Equal(Unavailability.HelperVersionMismatch, nnr.Unavailability);
        Assert.Contains("nnr", nnr.UnavailabilityDetail);
        Assert.True(nr2.IsAvailable);
        int launched = rig.Host.LaunchedProcessIds.Count;
        Assert.Equal(Unavailability.HelperVersionMismatch, rig.Host.Probe("nnr"));
        Assert.Equal(Unavailability.None, rig.Host.Probe("nr2"));
        Assert.Equal(launched, rig.Host.LaunchedProcessIds.Count);
    }

    [Fact]
    public void A_restarted_helper_offering_a_different_delay_leaves_the_engine_a_mismatch_with_its_delay_unchanged()
    {
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--crash-after", "5:9" } : new[] { "--delay", "2500" });
        var engine = rig.Engine("nnr");
        Assert.Equal(3408, engine.LatencySamples);
        using (var feeder = new Feeder(engine))
            HelperTestRig.Until(() => engine.Unavailability == Unavailability.HelperVersionMismatch, "the mismatch after the restart");
        Assert.Equal(3408, engine.LatencySamples);
        Assert.False(engine.IsAvailable);
        Assert.Contains("2500", engine.UnavailabilityDetail);
    }

    /// <summary>
    /// A failure is published whole. The thread that breaks the connection is
    /// held at the instant its failure becomes visible, and the control thread
    /// is made to act on it right then. It must see the real condition (a
    /// broken pipe: restart, counted) and never a default one (which reads as
    /// a rejected handshake: a mismatch, never retried, the helper gone for
    /// good).
    /// </summary>
    [Fact]
    public void The_control_thread_never_acts_on_a_failure_published_without_its_reason()
    {
        using var rig = new HelperTestRig();
        var engine = rig.Engine("nnr");
        Assert.True(engine.IsAvailable);
        var c = rig.Host.CurrentConnection!;
        WdspHelperState? seen = null;
        rig.Host.FailurePublishedForTest = _ =>
        {
            // The barrier: this thread waits here until the control thread has acted.
            rig.Host.WakeControl();
            HelperTestRig.Until(() => rig.Host.State != WdspHelperState.Running, "the control thread to act on the failure");
            seen = rig.Host.State;
        };
        rig.Host.BreakForTest(c, WdspCondition.BrokenPipe, "a broken pipe, injected by the test");
        rig.Host.FailurePublishedForTest = null;
        Assert.NotNull(seen);
        Assert.NotEqual(WdspHelperState.VersionMismatch, seen);
        Assert.Equal(1, rig.Host.Failures); // a crash, counted toward the cap
        HelperTestRig.Until(() => engine.IsAvailable && rig.Host.LaunchedProcessIds.Count == 2, "the restart");
    }

    [Fact]
    public void A_fresh_host_answers_the_probe_without_starting_anything()
    {
        using var rig = new HelperTestRig();
        Assert.Equal(Unavailability.None, rig.Host.Probe("nnr"));
        Assert.Equal(Unavailability.None, rig.Host.Probe("nr2"));
        Assert.Equal(0, rig.Host.StartsAttempted);
        Assert.Empty(rig.Host.LaunchedProcessIds);
        Assert.Equal(WdspHelperState.Idle, rig.Host.State);
    }

    /// <summary>
    /// With the echo stub, processed audio and audio passed through delayed by
    /// the engine's latency are identical, so a correct whole is a perfect
    /// delayed copy of the input through a stall, a crash and both restarts.
    /// Holds through a restart only because the engine discards each channel
    /// life's start-up history: the stub's first answers carry zeros, the
    /// delayed pass-through carries the real past.
    /// </summary>
    [Fact(Skip = "Waits on Track D's delayed fallback in PerChannelDenoiserBank: today a fallback frame goes out undelayed. Un-skip when this branch is rebased onto Track D.")]
    public void No_jump_through_a_stall_a_crash_and_the_restarts()
    {
        using var rig = new HelperTestRig(n => n switch
        {
            1 => new[] { "--stall-after", "60:1500" },
            2 => new[] { "--crash-after", "60:9" },
            _ => Array.Empty<string>(),
        });
        var made = new List<WdspHelperEngine>();
        using var bank = new PerChannelDenoiserBank(() => { var e = rig.Engine(); made.Add(e); return e; }, 2);
        int lag = bank.LatencySamples;
        Assert.Equal(3408, lag);
        var rng = new Random(18_50);
        var left = new List<float>();
        var right = new List<float>();
        int f = 0;
        var output = new List<float>();
        bool Done() => rig.Host.LaunchedProcessIds.Count == 3 && made.All(e => e.IsAvailable);
        int after = 0;
        while (after < 60)
        {
            HelperTestRig.Pace(made.ToArray());
            var buffer = new float[960];
            for (int i = 0; i < 480; i++)
            {
                float l = MathF.Sin((f * 480 + i) * 0.021f) * 0.4f, r = (float)(rng.NextDouble() - 0.5);
                left.Add(l); right.Add(r);
                buffer[2 * i] = l; buffer[2 * i + 1] = r;
            }
            bank.Process(buffer, 1f);
            output.AddRange(buffer);
            Thread.Sleep(5);
            f++;
            if (Done()) after++;
            Assert.True(f < 5000, "the restarts never completed");
        }
        for (int t = 0; t < left.Count; t++)
        {
            float l = t >= lag ? left[t - lag] : 0f, r = t >= lag ? right[t - lag] : 0f;
            Assert.True(l == output[2 * t] && r == output[2 * t + 1], $"a jump at sample {t} (frame {t / 480})");
        }
        Assert.True(bank.FallbackFrames > 0, "the faults never made the engines unavailable");
    }

    // ------------------------------------------------------------------ never orphaned

    [Fact]
    public void A_closing_the_job_handle_alone_kills_the_helper()
    {
        using var rig = new HelperTestRig();
        var engine = rig.Engine();
        int pid = rig.Host.CurrentProcessId!.Value;
        Assert.True(rig.Host.CurrentHelperIsInItsJob());
        rig.Host.CloseJobForTest(); // no channel closed, no pipe closed: only the job
        Assert.True(HelperTestRig.Gone(pid, 3000), "the helper outlived its job handle");
    }

    [Fact]
    public void B_the_stub_exits_when_its_parent_does()
    {
        // The STUB's guard, not the real helper's; Track A proves the helper's.
        var parent = WdspNative.StartInJob(HelperTestRig.StubPath, "\"" + HelperTestRig.StubPath + "\" --sleep 2000");
        string pipeName = WdspNative.NewPipeName(parent.ProcessId);
        using var server = WdspNative.CreatePipeServer(pipeName);
        var child = WdspNative.StartInJob(HelperTestRig.StubPath,
            WdspNative.CommandLine(HelperTestRig.StubPath, pipeName, new[] { "--no-server-check" }));
        try
        {
            Assert.True(server.WaitForConnectionAsync().Wait(10000), "the stub never connected");
            Assert.True(HelperTestRig.Gone(parent.ProcessId, 5000), "the stand-in parent did not exit");
            Assert.True(HelperTestRig.Gone(child.ProcessId, 3000), "the stub outlived its parent");
        }
        finally
        {
            child.Job.Dispose(); parent.Job.Dispose();
            Assert.True(HelperTestRig.Gone(child.ProcessId, 3000));
            child.Process.Dispose(); parent.Process.Dispose();
        }
    }

    [Fact]
    public void C_the_stub_exits_when_its_pipe_breaks()
    {
        // The STUB's guard, not the real helper's; Track A proves the helper's.
        string pipeName = WdspNative.NewPipeName(Environment.ProcessId);
        var server = WdspNative.CreatePipeServer(pipeName);
        var child = WdspNative.StartInJob(HelperTestRig.StubPath, WdspNative.CommandLine(HelperTestRig.StubPath, pipeName, null));
        try
        {
            Assert.True(server.WaitForConnectionAsync().Wait(10000), "the stub never connected");
            Thread.Sleep(100);
            Assert.False(HelperTestRig.Gone(child.ProcessId, 0));
            server.Dispose();
            Assert.True(HelperTestRig.Gone(child.ProcessId, 3000), "the stub outlived its pipe");
        }
        finally
        {
            server.Dispose();
            child.Job.Dispose();
            Assert.True(HelperTestRig.Gone(child.ProcessId, 3000));
            child.Process.Dispose();
        }
    }

    /// <summary>
    /// The creation boundary. A hidden sacrificial parent, itself in a job of
    /// this test's, starts a child through the production start code and
    /// freezes the moment CreateProcess returns; the test then kills that
    /// parent there. The child must go with it, because it must already be in
    /// the parent's kill-on-close job. (Under the old order, create, then
    /// assign, then resume, it stays: suspended, in no job of the dead
    /// parent's, and only this test's outer job ends it.) Never the operator's
    /// application: every process here is a stub, and every pid is recorded
    /// and ended in the finally.
    /// </summary>
    [Fact]
    public void E_a_parent_killed_the_moment_it_creates_the_helper_takes_the_helper_with_it()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), "jjflex-sacrificial-" + Guid.NewGuid().ToString("N") + ".pid");
        string stub = HelperTestRig.StubPath;
        var parent = WdspNative.StartInJob(stub, "\"" + stub + "\" --sacrificial-parent \"" + pidFile + "\"");
        _out.WriteLine($"sacrificial parent pid {parent.ProcessId}");
        int childPid = 0;
        Process? child = null;
        try
        {
            HelperTestRig.Until(() => File.Exists(pidFile), "the sacrificial parent to create its child");
            childPid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            _out.WriteLine($"child pid {childPid}");
            child = Process.GetProcessById(childPid);
            _ = child.Handle; // held from here, so the pid cannot be reused under us
            Assert.False(child.HasExited, "the child should exist, suspended, at the moment of the kill");
            WdspNative.Terminate(parent.Process, 1); // killed at exactly that point
            Assert.True(HelperTestRig.Gone(parent.ProcessId, 5000), "the sacrificial parent did not die");
            Assert.True(child.WaitForExit(5000), "the helper outlived a parent killed the moment it created it");
        }
        finally
        {
            if (child != null && !child.HasExited) { HelperTestRig.Survivors.Add(childPid); child.Kill(); child.WaitForExit(2000); }
            parent.Job.Dispose(); // ends anything still in this test's job, the child included
            bool parentGone = HelperTestRig.Gone(parent.ProcessId, 3000);
            bool childGone = childPid == 0 || HelperTestRig.Gone(childPid, 3000);
            _out.WriteLine($"after cleanup: parent {parent.ProcessId} gone {parentGone}, child {childPid} gone {childGone}");
            child?.Dispose();
            parent.Process.Dispose();
            try { File.Delete(pidFile); } catch (IOException) { }
            Assert.True(parentGone && childGone, "a sacrificial process survived its cleanup");
        }
    }

    [Fact]
    public void D_the_last_channel_closing_stops_the_helper_after_the_linger_and_a_channel_inside_it_keeps_it()
    {
        using var rig = new HelperTestRig();
        var first = rig.Engine();
        int pid = rig.Host.CurrentProcessId!.Value;
        first.Dispose();
        Thread.Sleep(150); // inside the 400 ms linger
        Assert.False(HelperTestRig.Gone(pid, 0));
        var second = rig.Engine("nr2"); // arrowing back within the linger: no restart
        Assert.True(second.IsAvailable);
        Assert.Equal(pid, rig.Host.CurrentProcessId);
        Thread.Sleep(600);
        Assert.False(HelperTestRig.Gone(pid, 0));
        Assert.Single(rig.Host.LaunchedProcessIds);
        second.Dispose();
        Assert.True(HelperTestRig.Gone(pid, 3000), "the helper outlived the linger");
        HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Idle, "idle");
    }

}
