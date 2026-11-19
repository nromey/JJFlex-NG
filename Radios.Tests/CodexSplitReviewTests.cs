using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using JJPortaudio;
using Xunit;
using static Radios.Tests.TxJoinRepairThreeTests;

namespace Radios.Tests;

/// <summary>
/// Sprint 49 Track H2 split, repair 1: Codex's check of 828e1a0e
/// (<c>for-claude\2026-11-19-codex-sprint49-h2-split-check.md</c>), his three
/// tests from <c>2026-11-19-codex-h2-split-results\CodexSplitReviewTests.cs</c>,
/// adopted. Each kept his schedule; what changed is said at the test. Run
/// verbatim against 828e1a0e first: the two lifecycle tests failed and the
/// characterization passed (it pinned the silent microphone).
/// </summary>
/// <remarks>
/// Windowless and silent: managed stand-in threads for the audio server and the
/// remote-audio run, <see cref="AudioAnchor.ServerThreadForTest"/> for any
/// server Initialize would make, and fake streams. No device, no radio.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class CodexSplitReviewTests
{
    /// <summary>
    /// Adopted. His schedule exactly: the real ten-second stop abandons a run
    /// whose server is still assigned and alive, before it reached Terminate.
    /// Adapted only in its bounds (both waits shortened, so the refusal comes in
    /// a fifth of a second rather than five) and in the overload that hands the
    /// run its generation. Added: the run-level refusal one level up, which is
    /// what keeps the next run from resetting the old one's shared state, and
    /// both positive controls.
    /// </summary>
    [Fact]
    public void A_start_after_remote_thread_abandonment_must_not_replace_its_still_live_audio_server()
    {
        using var x = new Rig();
        using var releaseServer = new ManualResetEventSlim();
        using var releaseRun = new ManualResetEventSlim();
        var oldServer = new Thread(() => releaseServer.Wait(30000)) { IsBackground = true };
        var oldRun = new Thread(() => releaseRun.Wait(30000)) { IsBackground = true };
        oldServer.Start(); oldRun.Start();
        var oldQueue = new BlockingCollection<AudioAnchor.workItem>();
        var was = AudioAnchor.SwapServerForTest(oldServer, oldQueue, null);
        var factoryWas = AudioAnchor.ServerThreadForTest;
        int retireWas = AudioAnchor.PreviousServerRetireMs;
        int admitWas = Radios.FlexBase.RemoteRunAdmissionMs;
        Thread? newServer = null;
        AudioAnchor.ServerThreadForTest = _ => newServer = new Thread(() => { }) { IsBackground = true };
        try
        {
            AudioAnchor.PreviousServerRetireMs = 200;
            Radios.FlexBase.RemoteRunAdmissionMs = 200;
            Set(x.B, "_PCAudio", true);
            Set(x.B, "remoteAudioThread", oldRun);
            long oldRunNumber = (long)Get(x.B, "_remoteAudioRun")!;
            Call(x.B, "stopRemoteAudioThread"); // real ten-second abandonment, no device or radio
            Assert.True(oldRun.IsAlive);
            Assert.True(oldServer.IsAlive);
            Assert.Null(AudioAnchor.UnretiredServerForTest); // old run has not reached Term yet

            // The next run, as startRemoteAudioThread makes it: the stop flag
            // cleared, a new run number.
            Set(x.B, "stopRemoteAudio", false);
            long next = oldRunNumber + 1;
            Set(x.B, "_remoteAudioRun", next);
            Assert.True((bool)Call(x.B, "RemoteRunStopping", oldRunNumber)!); // the abandoned run sees itself stopped all the same
            Assert.False((bool)Call(x.B, "RemoteRunStopping", next)!);
            Assert.False(x.B.AdmitRemoteAudioRun(next));       // the run refuses before touching anything shared

            long generation = Audio.ServerGeneration;
            bool accepted = Audio.Initialize(null!, null!, out long given); // exactly the next run's server admission
            Assert.False(accepted, "Initialize accepted while the previous abandoned run and its original AudioServer were both still alive, before Term recorded s_unretired; queue replaced=" + !ReferenceEquals(oldQueue, AudioAnchor.work) + ", generation advanced=" + (Audio.ServerGeneration != generation));
            Assert.Equal(0, given);
            Assert.Same(oldQueue, AudioAnchor.work);
            Assert.Equal(generation, Audio.ServerGeneration);
            Assert.Null(newServer);

            // Positive controls: once the old run has ended, the next run is
            // admitted; once its server has ended, a server is made.
            releaseRun.Set();
            Assert.True(oldRun.Join(5000));
            Assert.True(x.B.AdmitRemoteAudioRun(next));
            Assert.Null(Get(x.B, "_abandonedRemoteAudioThread"));
            releaseServer.Set();
            Assert.True(oldServer.Join(5000));
            Assert.True(Audio.Initialize(null!, null!, out given));
            Assert.Equal(generation + 1, given);
            Assert.NotNull(newServer);
        }
        finally
        {
            Set(x.B, "_PCAudio", false);
            Set(x.B, "stopRemoteAudio", false);
            Set(x.B, "remoteAudioThread", null);
            Set(x.B, "_abandonedRemoteAudioThread", null);
            releaseRun.Set(); releaseServer.Set(); oldRun.Join(5000); oldServer.Join(5000); newServer?.Join(5000);
            AudioAnchor.PreviousServerRetireMs = retireWas;
            Radios.FlexBase.RemoteRunAdmissionMs = admitWas;
            AudioAnchor.ServerThreadForTest = factoryWas;
            AudioAnchor.SwapServerForTest(was.Server, was.Work, was.Unretired);
        }
    }

    /// <summary>
    /// Adopted. His positive control (Initialize accepting while the old server
    /// was alive) is now refused by the test above, so the replacement is made
    /// the only way it still can be: the old run's server ENDED ON ITS OWN while
    /// still assigned (its PortAudio initialisation failed, say), its run not yet
    /// at Terminate. Then his assertion exactly: the old run's late end posts
    /// nothing into the newer server's queue. It ends only its own generation;
    /// a run that never started a server (0) ends nothing; the newer run's own
    /// end is the positive control.
    /// </summary>
    [Fact]
    public void An_old_run_s_late_Terminate_must_not_post_to_a_replacement_server()
    {
        using var releaseOld = new ManualResetEventSlim();
        using var releaseNew = new ManualResetEventSlim();
        var oldServer = new Thread(() => releaseOld.Wait(10000)) { IsBackground = true };
        oldServer.Start();
        var oldQueue = new BlockingCollection<AudioAnchor.workItem>();
        Audio.MoveServerGenerationForTest();              // a generation of its own, never 0 (none started)
        var was = AudioAnchor.SwapServerForTest(oldServer, oldQueue, null);
        long oldGeneration = Audio.ServerGeneration;      // what the old run's Initialize gave it
        Assert.True(oldGeneration > 0);
        var factoryWas = AudioAnchor.ServerThreadForTest;
        Thread? newServer = null;
        AudioAnchor.ServerThreadForTest = _ => newServer = new Thread(() => releaseNew.Wait(10000)) { IsBackground = true };
        try
        {
            releaseOld.Set();
            Assert.True(oldServer.Join(5000));            // ended on its own, still assigned
            Assert.True(Audio.Initialize(null!, null!, out long newGeneration));
            var laterQueue = AudioAnchor.work;
            Assert.NotSame(oldQueue, laterQueue);
            Audio.Terminate(oldGeneration);               // the old run's late end
            Assert.Equal(0, laterQueue.Count);
            Audio.Terminate(0);                           // a run whose start was refused
            Assert.Equal(0, laterQueue.Count);
            Assert.Equal(newGeneration, Audio.ServerGeneration);
            Audio.Terminate(newGeneration);               // positive control: its own run's end reaches it
            Assert.Equal(1, laterQueue.Count);
        }
        finally
        {
            releaseOld.Set(); releaseNew.Set(); oldServer.Join(5000); newServer?.Join(5000);
            AudioAnchor.ServerThreadForTest = factoryWas;
            AudioAnchor.SwapServerForTest(was.Server, was.Work, was.Unretired);
        }
    }

    private sealed class Stream : JJAudioStream
    {
        public Action? Stop, Start;
        public override bool StopAudio() { Stop?.Invoke(); return true; }
        public override bool StartAudio() { Start?.Invoke(); return true; }
        public override void Close() { }
    }

    /// <summary>
    /// Adopted, and inverted. His characterization proved the old code stopped
    /// the microphone before the new stream's slow start, so an over keyed
    /// meanwhile began on dead air. Same fakes, same schedule (a key-down while
    /// the new stream's start is held for 350 ms and more); the assertions that
    /// pinned the silence now pin the opposite: the key-down is still immediate,
    /// and the running stream is never stopped, because the new one now starts
    /// beside it. (His fake reports no buffers delivered, so the swap ends as a
    /// start that produced nothing: the running stream carries on untouched.)
    /// </summary>
    [Fact]
    public void Characterize_key_down_during_a_slow_start_is_immediate_and_capture_never_goes_silent()
    {
        using var x = new Rig();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        bool producing = true;
        var old = new Stream { Stop = () => Volatile.Write(ref producing, false) };
        var fresh = new Stream { Start = () => { entered.Set(); release.Wait(5000); } };
        RepairTwoChannel(x, old);
        Set(x.B, "_txTryFallbackDue", true);
        x.B.TxCaptureWorkerForTest = a => a();
        x.B.TxCaptureOpenForTest = _ => fresh;
        Exception? error = null;
        var swap = new Thread(() => { try { Call(x.B, "TxTryStep"); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        swap.Start();
        try
        {
            Assert.True(entered.Wait(5000));
            var sw = Stopwatch.StartNew();
            x.B.Transmit = true;
            Assert.True(sw.ElapsedMilliseconds < 500);
            Assert.Equal("Mox", x.B.q.Dequeue().Name);
            Assert.True(Volatile.Read(ref producing));    // was False: the microphone had been stopped first
            Assert.False(swap.Join(350));
            Assert.True(Volatile.Read(ref producing));    // still carrying the over
        }
        finally { release.Set(); Assert.True(swap.Join(5000)); }
        Assert.Null(error);
        Assert.True(Volatile.Read(ref producing));
        var channel = (Radios.FlexBase.audioChannelData)Get(x.B, "opusInputChannel")!;
        Assert.Same(old, channel.PortAudioStream);
        Assert.True(channel.Started);
    }
}
