using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The helper as it behaves after its 2026-11-05 repair rounds, read by the
/// host: its exit codes named, its closes taken as they are now answered, its
/// queue limits never approached. Same class, same windowless rules.
/// </summary>
public sealed partial class HelperLifecycleTests
{
    /// <summary>Restarts held off, so a test can read the reason while the helper is down.</summary>
    private static WdspHostPolicy SlowRestart(TimeSpan? constructionWait = null) => HelperTestRig.Fast with
    {
        RestartDelays = new[] { TimeSpan.FromSeconds(30) },
        ConstructionWait = constructionWait ?? HelperTestRig.Fast.ConstructionWait,
    };

    // ------------------------------------------------------------------ exit codes

    /// <summary>
    /// Every way the helper exits is named, in the reason an engine gives and
    /// the host's detail (which the trace prints), never a bare number or a
    /// pipe that merely closed. The stub exits as the helper does: 6 after an
    /// Error 257, which is how the helper ends a client that overflowed one of
    /// its limits; 7, its own code; 9, a crash. Each of these is the crash
    /// path, counted, and the engine keeps its delay while down, which is what
    /// the bank's delayed pass-through lines up on. (2, 5 and 8 give up at
    /// once, ruled 2026-11-06: see the next test.)
    /// </summary>
    [Theory]
    [InlineData("--fatal-after", "5", 6u, "a protocol error", true, true)]
    [InlineData("--exit-early", "4", 4u, "it could not connect to its pipe", false, false)]
    [InlineData("--crash-after", "5:7", 7u, "it had rejected the protocol version", true, false)]
    [InlineData("--crash-after", "5:9", 9u, "a crash", true, false)]
    public void Every_helper_exit_is_named_in_the_reason_the_engine_gives(string fault, string how, uint code, string meaning,
        bool opensFirst, bool protocolError)
    {
        using var rig = new HelperTestRig(n => n == 1 ? new[] { fault, how } : Array.Empty<string>(),
            SlowRestart(opensFirst ? null : TimeSpan.FromSeconds(2)));
        var engine = rig.Engine("nnr");
        if (opensFirst)
        {
            Assert.True(engine.IsAvailable);
            Assert.Equal(3408, engine.LatencySamples);
            using var feeder = new Feeder(engine);
            HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Restarting, "the helper to go down");
        }
        else HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Restarting, "the helper to go down");

        string detail = engine.UnavailabilityDetail;
        _out.WriteLine("engine: " + detail);
        _out.WriteLine("host: " + rig.Host.Detail);
        Assert.False(engine.IsAvailable);
        Assert.Equal(Unavailability.HelperNotRunning, engine.Unavailability);
        Assert.Contains($"the helper exited with code {code}", detail);
        Assert.Contains(meaning, detail);
        Assert.Contains($"exited with code {code}", rig.Host.Detail);
        Assert.Equal(1, rig.Host.Failures); // the crash path, counted toward the cap
        Assert.Equal(protocolError ? WdspCondition.FatalProtocolError : WdspCondition.ProcessExited, rig.Host.LastFailureCondition);
        if (protocolError)
            Assert.Contains("before closing it said Error 257 UnexpectedMessage on channel 0: the client queued more than", detail);
        // No jump: the delay the bank lined up on stands while the helper is down.
        Assert.Equal(opensFirst ? 3408 : 0, engine.LatencySamples);
    }

    /// <summary>
    /// Exits 2 (a bad command line), 5 (its pipe's server was not JJ Flexible)
    /// and 8 (it could not make its own folder its working directory) give up
    /// at once, with no retries, because a restart cannot mend any of them
    /// (ruled 2026-11-06, S12). Exit 8 is the helper missing as far as the
    /// operator is concerned, with its own state, so a later surface can offer
    /// a reinstall from it; 2 and 5 are a helper that cannot run. Neither is
    /// counted toward the cap, each is traced, the engine keeps its delay, and
    /// a retry starts again. The fault is on the first start only, so a restart
    /// (50 ms away under this policy) would have been seen.
    /// </summary>
    [Theory]
    [InlineData("--exit-early", "8", 8u, "it could not make its own folder its working directory", false)]
    [InlineData("--exit-early", "2", 2u, "a bad command line", false)]
    [InlineData("--exit-early", "5", 5u, "its pipe's server was not JJ Flexible's process", false)]
    [InlineData("--crash-after", "5:5", 5u, "its pipe's server was not JJ Flexible's process", true)]
    public void Exits_2_5_and_8_give_up_at_once_and_8_reads_as_missing(string fault, string how, uint code, string meaning, bool opensFirst)
    {
        using var trace = new HelperTestRig.TraceCapture();
        using var rig = new HelperTestRig(n => n == 1 ? new[] { fault, how } : Array.Empty<string>(),
            HelperTestRig.Fast with { ConstructionWait = TimeSpan.FromSeconds(5) });
        bool missing = code == 8;
        var state = missing ? WdspHelperState.FolderUnusable : WdspHelperState.GaveUp;
        var engine = rig.Engine("nnr");
        if (opensFirst)
        {
            Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
            using var feeder = new Feeder(engine);
            HelperTestRig.Until(() => rig.Host.State == state, "the helper to give up");
        }
        else HelperTestRig.Until(() => rig.Host.State == state, "the helper to give up");
        Thread.Sleep(600); // twelve restart delays: none may come
        _out.WriteLine("engine: " + engine.UnavailabilityDetail);
        _out.WriteLine("host: " + rig.Host.Detail);
        Assert.Equal(state, rig.Host.State);
        Assert.Single(rig.Host.LaunchedProcessIds);
        Assert.Equal(0, rig.Host.Failures);
        Assert.Equal(missing ? WdspCondition.NoWorkingDirectory : WdspCondition.UnrecoverableExit, rig.Host.LastFailureCondition);
        var reason = missing ? Unavailability.HelperMissing : Unavailability.HelperNotRunning;
        Assert.Equal(reason, engine.Unavailability);
        Assert.Equal(reason, rig.Host.ProbeState("nnr", fileExists: true));
        Assert.Contains($"exited with code {code} ({meaning})", engine.UnavailabilityDetail);
        if (missing) Assert.StartsWith("the helper is missing: ", engine.UnavailabilityDetail);
        Assert.Equal(opensFirst ? 3408 : 0, engine.LatencySamples);
        int pid = rig.Host.LaunchedProcessIds[0];
        Assert.True(trace.Has(l => l.Contains($"-> {state}") && l.Contains($"code {code}")),
            "the give-up was not traced: " + string.Join(" | ", trace.Lines.Where(l => l.StartsWith("WdspHost"))));

        rig.Host.Retry(); // the operator's retry starts again; the stub behaves this time
        var again = rig.Engine("nr2");
        Assert.True(again.IsAvailable, again.UnavailabilityDetail);
        Assert.Equal(2, rig.Host.LaunchedProcessIds.Count);
        Assert.NotEqual(pid, rig.Host.CurrentProcessId);
    }

    /// <summary>
    /// The writer can be the first to find the pipe gone: it writes a frame
    /// after the helper has closed. It names the exit as the reader does.
    /// Deterministic: the reader is held, so only the writer can see the end.
    /// Found on x86, where the writer won the race about one run in six and the
    /// failure read "writing: IOException: Pipe is broken".
    /// </summary>
    [Fact]
    public void An_exit_the_writer_finds_first_is_named_too()
    {
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--fatal-after", "5" } : Array.Empty<string>(), SlowRestart());
        var engine = rig.Engine("nr2");
        Assert.True(engine.IsAvailable);
        using var gate = new ManualResetEventSlim(false);
        rig.Host.DispatchHookForTest = _ => gate.Wait(15000);
        try
        {
            using var feeder = new Feeder(engine);
            HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Restarting, "the helper to go down", 8000);
        }
        finally
        {
            rig.Host.DispatchHookForTest = null;
            gate.Set();
        }
        _out.WriteLine(engine.UnavailabilityDetail);
        Assert.Contains("the helper exited with code 6 (a protocol error", engine.UnavailabilityDetail);
        Assert.Equal(WdspCondition.FatalProtocolError, rig.Host.LastFailureCondition);
    }

    /// <summary>
    /// And when the writer finds the end first while the reader is merely
    /// behind, what the helper said before closing still reaches the reason:
    /// the Error is in the pipe, unread, and the writer waits (bounded) for the
    /// reader to finish reading before it publishes. Deterministic both ways:
    /// the reader is held until the writer either waits for it (the repaired
    /// host) or publishes without it (the host before the repair, where the
    /// Error text was lost: Codex's check saw three runs in four lose it with
    /// a 75 ms reader pause).
    /// </summary>
    [Fact]
    public void An_exit_the_writer_finds_first_keeps_what_the_helper_said()
    {
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--fatal-after", "5" } : Array.Empty<string>(), SlowRestart());
        var engine = rig.Engine("nr2");
        Assert.True(engine.IsAvailable);
        using var gate = new ManualResetEventSlim(false);
        int writerWaited = 0;
        rig.Host.WriterAwaitsReaderForTest = _ => { Interlocked.Increment(ref writerWaited); gate.Set(); };
        rig.Host.FailurePublishedForTest = _ => gate.Set();
        rig.Host.DispatchHookForTest = _ => gate.Wait(15000);
        try
        {
            using var feeder = new Feeder(engine);
            HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Restarting, "the helper to go down", 8000);
        }
        finally
        {
            rig.Host.DispatchHookForTest = null;
            gate.Set();
        }
        _out.WriteLine(engine.UnavailabilityDetail);
        Assert.Contains("the helper exited with code 6 (a protocol error", engine.UnavailabilityDetail);
        Assert.Contains("before closing it said Error 257 UnexpectedMessage on channel 0: the client queued more than", engine.UnavailabilityDetail);
        Assert.Equal(WdspCondition.FatalProtocolError, rig.Host.LastFailureCondition);
        Assert.Equal(1, Volatile.Read(ref writerWaited)); // the writer, not the reader, found the end
    }

    /// <summary>
    /// A fatal Error before the HelloAck (Error 257, then the close and exit 6)
    /// is a protocol error like any other: Contract 2's Fatal rule sends it down
    /// the crash path, and only a HelloReject is a version mismatch. So it is
    /// counted toward the cap, restarted, and named with its exit code and what
    /// the helper said. (Until the 2026-11-06 repair it read as a mismatch,
    /// never retried and never counted; Codex's check of the contract 2 round
    /// found this test pinning that.)
    /// </summary>
    [Fact]
    public void An_Error_before_the_handshake_is_a_protocol_error_on_the_crash_path()
    {
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--error-first" } : Array.Empty<string>(),
            SlowRestart(TimeSpan.FromSeconds(2)));
        var engine = rig.Engine("nnr");
        HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Restarting, "the helper to go down");
        _out.WriteLine("engine: " + engine.UnavailabilityDetail);
        _out.WriteLine("host: " + rig.Host.Detail);
        Assert.False(engine.IsAvailable);
        Assert.Equal(Unavailability.HelperNotRunning, engine.Unavailability);
        Assert.Equal(WdspCondition.FatalProtocolError, rig.Host.LastFailureCondition);
        Assert.Equal(1, rig.Host.Failures);
        Assert.Contains("the helper exited with code 6 (a protocol error", engine.UnavailabilityDetail);
        Assert.Contains("before closing it said Error 257 UnexpectedMessage on channel 0: refused by the test's command line",
            engine.UnavailabilityDetail);
    }

    /// <summary>
    /// And the restart after it is a real one: with a short restart delay, the
    /// next start (which the stub answers properly) brings a new engine up on
    /// a second helper.
    /// </summary>
    [Fact]
    public void An_Error_before_the_handshake_is_followed_by_a_restart_that_works()
    {
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--error-first" } : Array.Empty<string>(),
            HelperTestRig.Fast with { ConstructionWait = TimeSpan.FromSeconds(10) });
        var engine = rig.Engine("nr2");
        Assert.True(engine.IsAvailable, engine.UnavailabilityDetail);
        Assert.Equal(2, rig.Host.LaunchedProcessIds.Count);
        Assert.Equal(WdspHelperState.Running, rig.Host.State);
    }

    /// <summary>
    /// The same rule for a protocol error JJ Flexible finds itself before the
    /// handshake (here a Heartbeat ahead of the HelloAck, out of handshake
    /// order): the crash path, counted, and restarted.
    /// </summary>
    [Fact]
    public void A_protocol_error_found_before_the_handshake_is_the_crash_path()
    {
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--heartbeat-first" } : Array.Empty<string>(),
            SlowRestart(TimeSpan.FromSeconds(2)));
        var engine = rig.Engine("nnr");
        HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Restarting, "the helper to go down");
        _out.WriteLine("host: " + rig.Host.Detail);
        Assert.Equal(Unavailability.HelperNotRunning, engine.Unavailability);
        Assert.Equal(WdspCondition.FatalProtocolError, rig.Host.LastFailureCondition);
        Assert.Equal(1, rig.Host.Failures);
        Assert.Contains("before the handshake: Heartbeat before the handshake", rig.Host.Detail);
    }

    /// <summary>
    /// What stays a mismatch: a HelloAck that cannot even be read is an invalid
    /// HelloAck, which Contract 2 maps to HelperVersionMismatch, never retried.
    /// </summary>
    [Fact]
    public void A_HelloAck_that_cannot_be_read_is_still_a_mismatch()
    {
        using var rig = new HelperTestRig(_ => new[] { "--garbled-hello" });
        var engine = rig.Engine("nnr");
        _out.WriteLine(engine.UnavailabilityDetail);
        Assert.Equal(Unavailability.HelperVersionMismatch, engine.Unavailability);
        Assert.Equal(WdspHelperState.VersionMismatch, rig.Host.State);
        Assert.Contains("an invalid HelloAck", engine.UnavailabilityDetail);
        Thread.Sleep(600);
        Assert.Equal(0, rig.Host.Failures);
        Assert.Single(rig.Host.LaunchedProcessIds);
    }

    // ------------------------------------------------------------------ closes

    /// <summary>
    /// A channel closed while its build was failing: the helper answers the
    /// open (ChannelBuildFailed), then the close, UnknownChannel, because the
    /// channel never reached its table. That answer settles a channel this
    /// end closed; it is not the two ends disagreeing. One close per channel.
    /// </summary>
    [Fact]
    public void A_close_answered_UnknownChannel_after_a_failed_build_settles_the_channel()
    {
        // A linger longer than the build, so the helper is still there to answer.
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--fail-build", "2:600" } : Array.Empty<string>(),
            HelperTestRig.Fast with { Linger = TimeSpan.FromSeconds(3) });
        var engine = rig.Engine("nnr");
        Assert.True(engine.IsAvailable);
        engine.Reset(); // closes channel 1 (Ok) and opens channel 2, whose build will fail
        HelperTestRig.Until(() => engine.Channel?.Id == 2, "the reopen to be sent");
        Thread.Sleep(100); // inside the 600 ms build
        engine.Dispose(); // the close for channel 2 goes in while it is being built
        HelperTestRig.Until(() => Interlocked.Read(ref rig.Host.CloseAcksSettled) == 2, "both closes settled", 5000);
        Thread.Sleep(200);
        Assert.Equal(2, Interlocked.Read(ref rig.Host.CloseChannelsSent));
        Assert.Equal(2, Interlocked.Read(ref rig.Host.CloseAcksSettled));
        Assert.Equal(0, Interlocked.Read(ref rig.Host.CloseAcksUnexpected));
        Assert.Equal(1, rig.Host.LaunchedProcessIds.Count);
    }

    /// <summary>
    /// A close the helper reads after the channel left its table is answered
    /// UnknownChannel (its README: a repeat read just after the ack was queued
    /// "may be answered either way"). For a channel this end closed, that too
    /// just settles it.
    /// </summary>
    [Fact]
    public void A_close_answered_UnknownChannel_for_a_closed_channel_settles_it()
    {
        using var rig = new HelperTestRig(_ => new[] { "--close-unknown" });
        var engine = rig.Engine("nr2");
        Assert.True(engine.IsAvailable);
        engine.Dispose();
        HelperTestRig.Until(() => Interlocked.Read(ref rig.Host.CloseAcksSettled) == 1, "the close settled", 5000);
        Assert.Equal(1, Interlocked.Read(ref rig.Host.CloseChannelsSent));
        Assert.Equal(0, Interlocked.Read(ref rig.Host.CloseAcksUnexpected));
    }

    /// <summary>
    /// The helper gives a close repeated before its ack no answer at all, so
    /// nothing may ever wait for a close's answer. Against a stub that answers
    /// no close: a channel closes, another opens and runs beside the silence,
    /// a second Dispose and a Reset after it send nothing more, and the helper
    /// still stops on time when the last engine goes.
    /// </summary>
    [Fact]
    public void Nothing_waits_for_the_answer_to_a_close()
    {
        using var rig = new HelperTestRig(_ => new[] { "--swallow-closes" });
        var first = rig.Engine("nnr");
        Assert.True(first.IsAvailable);
        int pid = rig.Host.CurrentProcessId!.Value;
        first.Dispose();
        first.Dispose();
        first.Reset();
        var second = rig.Engine("nr2"); // inside the linger: the same helper
        Assert.True(second.IsAvailable);
        Assert.Equal(pid, rig.Host.CurrentProcessId);
        var input = new float[480]; var output = new float[480];
        int answered = 0;
        for (int i = 0; i < 30; i++) { HelperTestRig.Pace(second); if (second.ProcessFrame(input, output)) answered++; }
        Assert.True(answered >= 20, $"only {answered} of 30 frames answered beside an unanswered close");
        Assert.Equal(1, Interlocked.Read(ref rig.Host.CloseChannelsSent));
        second.Dispose();
        Assert.True(HelperTestRig.Gone(pid, 3000), "the helper outlived the linger while closes went unanswered");
        HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Idle, "idle");
        Assert.Equal(2, Interlocked.Read(ref rig.Host.CloseChannelsSent));
        Assert.Equal(0, Interlocked.Read(ref rig.Host.CloseAcksSettled));
    }

    // ------------------------------------------------------------------ the helper's limits

    private static int InFlight(HelperTestRig rig) => Volatile.Read(ref rig.Host.CurrentConnection!.SettingsInFlight);

    /// <summary>
    /// The helper ends a connection that leaves more than 4,096 settings and
    /// closes queued. However fast a value changes, the host keeps no more than
    /// its cap in flight, sends each setting's latest value when room comes,
    /// and loses nothing. The stub plays the helper with a limit of 12 and a
    /// slow processing thread (20 ms a setting); the cap here is 8. The limit
    /// of one unanswered request per setting is lifted here, so the cap alone
    /// is what keeps the helper alive; the next test holds that limit.
    /// </summary>
    [Fact]
    public void Settings_in_flight_never_pass_the_cap_however_fast_a_value_changes()
    {
        using var rig = new HelperTestRig(_ => new[] { "--slow-settings", "20", "--request-limit", "12" },
            HelperTestRig.Fast with { MaxSettingsInFlight = 8, MaxInFlightPerSetting = int.MaxValue });
        var engine = rig.Engine("nr2");
        Assert.True(engine.IsAvailable);
        var t = System.Diagnostics.Stopwatch.StartNew();
        int changes = 0;
        while (t.ElapsedMilliseconds < 1500)
        {
            Assert.True(engine.TrySetSetting("trained_t2", new EngineSettingValue(0.02 + (changes % 28) * 0.01)));
            changes++;
        }
        Assert.True(engine.TrySetSetting("trained_t2", new EngineSettingValue(0.3))); // never one of the values above
        HelperTestRig.Until(() => engine.GetSetting("trained_t2").Number == 0.3 && InFlight(rig) == 0, "the last value applied", 15000);
        long sent = Interlocked.Read(ref rig.Host.SetSettingsSent);
        _out.WriteLine($"{changes} changes, {sent} SetSettings sent, peak in flight {Interlocked.Read(ref rig.Host.PeakSettingsInFlight)}");
        Assert.True(engine.IsAvailable);
        Assert.Equal(0, rig.Host.Failures);
        Assert.Single(rig.Host.LaunchedProcessIds);
        Assert.InRange(Interlocked.Read(ref rig.Host.PeakSettingsInFlight), 1, 8);
        Assert.True(sent < changes, "the values were not coalesced");
    }

    /// <summary>
    /// A setting that changes without end must not starve another setting on
    /// the same channel. With the shipped cap (256) and a slow helper (20 ms a
    /// setting), NR2's gain method flips as fast as a thread can flip it while,
    /// 50 ms in, the trained threshold is set once. That value must be in
    /// effect within a second, while the flood is still going. (Codex's check
    /// of the contract 2 round: before the repair the later setting waited
    /// behind every freed slot and 255 queued requests, 12 seconds in its run.)
    /// Afterwards both settings' latest values are in effect.
    /// </summary>
    [Fact]
    public void A_setting_that_never_stops_changing_does_not_starve_another_on_its_channel()
    {
        using var rig = new HelperTestRig(_ => new[] { "--slow-settings", "20" });
        var engine = rig.Engine("nr2");
        Assert.True(engine.IsAvailable);
        Assert.Equal(WdspHostPolicy.MaxSettingsInFlightDefault, rig.Host.Policy.MaxSettingsInFlight);
        string[] gains = { "linear", "log" };
        var t = System.Diagnostics.Stopwatch.StartNew();
        long setAt = -1, appliedAt = -1;
        int i = 0;
        while (t.ElapsedMilliseconds < 3000)
        {
            engine.TrySetSetting("gain_method", new EngineSettingValue(0, gains[i++ % 2]));
            if (setAt < 0 && t.ElapsedMilliseconds >= 50)
            {
                Assert.True(engine.TrySetSetting("trained_t2", new EngineSettingValue(0.3)));
                setAt = t.ElapsedMilliseconds;
            }
            if (setAt >= 0 && appliedAt < 0 && engine.GetSetting("trained_t2").Number == 0.3) appliedAt = t.ElapsedMilliseconds;
        }
        string last = gains[(i - 1) % 2];
        _out.WriteLine($"{i} gain changes; threshold set at {setAt} ms, in effect at {appliedAt} ms; " +
            $"{Interlocked.Read(ref rig.Host.SetSettingsSent)} sent, peak in flight {Interlocked.Read(ref rig.Host.PeakSettingsInFlight)}");
        Assert.True(appliedAt >= 0, "the threshold was still not in effect when the flood ended, 3 s in");
        Assert.InRange(appliedAt - setAt, 0, 1000);
        // One unanswered request per setting, and this channel has two settings.
        Assert.InRange(Interlocked.Read(ref rig.Host.PeakSettingsInFlight), 1, 2);
        HelperTestRig.Until(() => engine.GetSetting("gain_method").Choice == last && InFlight(rig) == 0, "the last gain method in effect", 15000);
        Assert.Equal(0.3, engine.GetSetting("trained_t2").Number);
        Assert.Equal(0, rig.Host.Failures);
    }

    /// <summary>
    /// A client that does overflow the helper's limit (here, a host whose cap is
    /// above the stub's limit) ends as the helper ends it: Error 257, the close,
    /// exit 6. That reads as a protocol failure, named, and follows the crash
    /// path; the engine keeps its delay, so the bank's delayed pass-through
    /// carries on with no jump.
    /// </summary>
    [Fact]
    public void Overflowing_the_helpers_limit_reads_as_a_named_protocol_failure_and_keeps_the_delay()
    {
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--slow-settings", "20", "--request-limit", "12" } : Array.Empty<string>(),
            SlowRestart() with { MaxSettingsInFlight = 100_000, MaxInFlightPerSetting = int.MaxValue });
        var engine = rig.Engine("nr2");
        int latency = engine.LatencySamples;
        Assert.Equal(3408, latency);
        int i = 0;
        while (rig.Host.State == WdspHelperState.Running && i < 2_000_000) { engine.TrySetSetting("trained_t2", new EngineSettingValue(0.02 + (i % 28) * 0.01)); i++; }
        HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Restarting, "the helper to end the connection");
        _out.WriteLine(engine.UnavailabilityDetail);
        Assert.Equal(WdspCondition.FatalProtocolError, rig.Host.LastFailureCondition);
        Assert.Equal(Unavailability.HelperNotRunning, engine.Unavailability);
        Assert.Contains("the helper exited with code 6 (a protocol error", engine.UnavailabilityDetail);
        Assert.Contains("before closing it said Error 257 UnexpectedMessage on channel 0: more than 12 settings and closes queued", engine.UnavailabilityDetail);
        Assert.Equal(1, rig.Host.Failures);
        Assert.False(engine.IsAvailable);
        Assert.Equal(latency, engine.LatencySamples);
    }

    /// <summary>
    /// Track A's open question: a setting the helper reads after its channel's
    /// close is answered UnknownChannel after the CloseChannelAck today, and
    /// might be answered not at all if that is ruled the other way. Either way
    /// its count goes when the close is answered, the late answer is discarded
    /// as expected, and the room it held comes back. With a cap of 2 and two
    /// settings held, another engine's setting waits, and then gets through.
    /// </summary>
    [Theory]
    [InlineData("answer")]
    [InlineData("drop")]
    public void A_setting_answered_after_its_close_or_never_leaks_nothing(string how)
    {
        using var rig = new HelperTestRig(_ => new[] { "--hold-setting-acks", "nnr:" + how },
            HelperTestRig.Fast with { MaxSettingsInFlight = 2, MaxInFlightPerSetting = 2 });
        var held = rig.Engine("nnr");
        var other = rig.Engine("nr2");
        Assert.True(held.TrySetSetting("mask_floor_db", new EngineSettingValue(-30)));
        HelperTestRig.Until(() => Interlocked.Read(ref rig.Host.SetSettingsSent) == 1, "the first setting sent");
        Assert.True(held.TrySetSetting("mask_floor_db", new EngineSettingValue(-35)));
        HelperTestRig.Until(() => Interlocked.Read(ref rig.Host.SetSettingsSent) == 2, "the second setting sent");
        Assert.Equal(2, InFlight(rig));
        Assert.True(other.TrySetSetting("trained_t2", new EngineSettingValue(0.3)));
        Thread.Sleep(300);
        Assert.Equal(2, Interlocked.Read(ref rig.Host.SetSettingsSent)); // held back: no room
        held.Dispose();
        HelperTestRig.Until(() => other.GetSetting("trained_t2").Number == 0.3, "the other engine's setting, once the close made room");
        HelperTestRig.Until(() => InFlight(rig) == 0, "nothing left in flight");
        Thread.Sleep(200);
        Assert.Equal(0, InFlight(rig));
        Assert.Equal(how == "answer" ? 2 : 0, Interlocked.Read(ref rig.Host.SettingAcksAfterClose));
        Assert.Equal(0, Interlocked.Read(ref rig.Host.SettingAcksUnplaced));
        Assert.Equal(1, Interlocked.Read(ref rig.Host.CloseAcksSettled));
        Assert.True(other.IsAvailable);
    }

    /// <summary>
    /// A reader that stops reading altogether is a stall: nothing read for the
    /// stall time, and the helper is ended (here the test policy's 1 s; the
    /// app's is 500 ms). This bounds complete silence only. A reader that keeps
    /// reading, however slowly, refreshes the liveness time and is never a
    /// stall; what keeps such a reader from the helper's reply limit is the
    /// settings bounds, which the reply-limit tests establish
    /// (HelperLifecycleTests.ReplyLimit.cs). Until the 2026-11-06 repair this
    /// comment claimed the stall kept every backlog under 2 MB; that was
    /// arithmetic, not a bound, and Codex's check showed a slow reader the
    /// stall never catches.
    /// </summary>
    [Fact]
    public void A_reader_that_stops_reading_is_a_stall_long_before_the_helpers_reply_limit()
    {
        using var rig = new HelperTestRig(policy: SlowRestart());
        var engine = rig.Engine("nr2");
        Assert.True(engine.IsAvailable);
        using var gate = new ManualResetEventSlim(false);
        var t = System.Diagnostics.Stopwatch.StartNew();
        rig.Host.DispatchHookForTest = _ => gate.Wait(15000);
        try
        {
            using var feeder = new Feeder(engine);
            HelperTestRig.Until(() => rig.Host.State == WdspHelperState.Restarting, "the stall", 6000);
        }
        finally
        {
            rig.Host.DispatchHookForTest = null;
            gate.Set();
        }
        _out.WriteLine($"stall after {t.ElapsedMilliseconds} ms (policy {rig.Host.Policy.Stall.TotalMilliseconds} ms)");
        Assert.Equal(WdspCondition.Stall, rig.Host.LastFailureCondition);
        Assert.InRange(t.ElapsedMilliseconds, (long)rig.Host.Policy.Stall.TotalMilliseconds, (long)rig.Host.Policy.Stall.TotalMilliseconds + 1500);
    }

    /// <summary>
    /// The words for each code, held to the helper's README so a renamed code
    /// cannot drift silently; and which condition each exit is: 6 a protocol
    /// error, 2 and 5 a helper that cannot run, 8 a helper that cannot use its
    /// folder (missing, to the operator), every other code the process exiting.
    /// </summary>
    [Fact]
    public void Each_exit_code_has_its_own_words_and_its_own_condition()
    {
        var codes = new uint[] { 0, 2, 3, 4, 5, 6, 7, 8 };
        var words = codes.Select(WdspProtocol.DescribeExit).ToArray();
        Assert.Equal(words.Length, words.Distinct().Count());
        Assert.All(words, w => Assert.DoesNotContain("a crash", w));
        Assert.Contains("a crash", WdspProtocol.DescribeExit(1));
        Assert.Contains("0xC0000005", WdspProtocol.DescribeExit(0xC0000005));
        foreach (var c in codes.Append(1u).Append(9u).Append(0xC0000005))
            Assert.Equal(c switch
            {
                6 => WdspCondition.FatalProtocolError,
                2 or 5 => WdspCondition.UnrecoverableExit,
                8 => WdspCondition.NoWorkingDirectory,
                _ => WdspCondition.ProcessExited,
            }, WdspProtocol.ConditionForExit(c));
        // The crash path for these two; 2, 5 and 8 give up at once (ruled 2026-11-06).
        Assert.True(WdspProtocol.CountsTowardCap(WdspCondition.FatalProtocolError));
        Assert.True(WdspProtocol.CountsTowardCap(WdspCondition.ProcessExited));
        Assert.False(WdspProtocol.CountsTowardCap(WdspCondition.UnrecoverableExit));
        Assert.False(WdspProtocol.CountsTowardCap(WdspCondition.NoWorkingDirectory));
    }
}
