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
    /// pipe that merely closed. The stub exits as the helper does: 8 and 2
    /// before it connects; 6 after an Error 257, which is how the helper ends
    /// a client that overflowed one of its limits; 5 and 7, its own codes; 9,
    /// a crash. Every one is still the crash path, counted, and the engine keeps
    /// its delay while down, which is what the bank's delayed pass-through
    /// lines up on.
    /// </summary>
    [Theory]
    [InlineData("--exit-early", "8", 8u, "could not make its own folder its working directory", false, false)]
    [InlineData("--exit-early", "2", 2u, "a bad command line", false, false)]
    [InlineData("--fatal-after", "5", 6u, "a protocol error", true, true)]
    [InlineData("--crash-after", "5:5", 5u, "its pipe's server was not JJ Flexible's process", true, false)]
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
    /// The helper refusing JJ Flexible's first message (Error 257, then the
    /// close and exit 6) is its answer to the handshake: a mismatch that names
    /// what it said, never retried and never counted, even though the pipe
    /// breaks a moment later.
    /// </summary>
    [Fact]
    public void An_Error_before_the_handshake_is_the_helpers_refusal_named_and_never_retried()
    {
        using var rig = new HelperTestRig(_ => new[] { "--error-first" });
        var engine = rig.Engine("nnr");
        Assert.False(engine.IsAvailable);
        Assert.Equal(Unavailability.HelperVersionMismatch, engine.Unavailability);
        _out.WriteLine(engine.UnavailabilityDetail);
        Assert.Contains("the helper refused JJ Flexible's handshake: Error 257 UnexpectedMessage on channel 0: refused by the test's command line",
            engine.UnavailabilityDetail);
        Assert.Equal(WdspHelperState.VersionMismatch, rig.Host.State);
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

    /// <summary>The words for each code, held to the helper's README so a renamed code cannot drift silently.</summary>
    [Fact]
    public void Each_exit_code_has_its_own_words_and_only_6_is_a_protocol_error()
    {
        var codes = new uint[] { 0, 2, 3, 4, 5, 6, 7, 8 };
        var words = codes.Select(WdspProtocol.DescribeExit).ToArray();
        Assert.Equal(words.Length, words.Distinct().Count());
        Assert.All(words, w => Assert.DoesNotContain("a crash", w));
        Assert.Contains("a crash", WdspProtocol.DescribeExit(1));
        Assert.Contains("0xC0000005", WdspProtocol.DescribeExit(0xC0000005));
        foreach (var c in codes)
            Assert.Equal(c == 6 ? WdspCondition.FatalProtocolError : WdspCondition.ProcessExited, WdspProtocol.ConditionForExit(c));
        // Both conditions are the crash path, as the appendix rules every exit to be.
        Assert.True(WdspProtocol.CountsTowardCap(WdspCondition.FatalProtocolError));
        Assert.True(WdspProtocol.CountsTowardCap(WdspCondition.ProcessExited));
    }
}
