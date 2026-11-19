using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using JJPortaudio;
using Xunit;
using static Radios.Tests.TxJoinRepairThreeTests;

namespace Radios.Tests;

/// <summary>
/// Sprint 49 Track H2 split, repair 1 (the brief
/// <c>planning\agile\sprint49-h2-split-repair-1.md</c>), beside Codex's adopted
/// reproductions in <see cref="CodexSplitReviewTests"/>: the run- and
/// server-lifetime pieces those do not reach, and the capture hand-over that
/// makes the swap make before break on the audio path.
/// </summary>
/// <remarks>
/// Windowless and silent. The hand-over tests drive JJPortaudio's own
/// <c>Audio</c> stream blocks and the callback's two helpers directly: no
/// PortAudio stream is opened, no device, no sound. The lifetime tests use
/// managed stand-in threads.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class TxSplitRepairOneTests
{
    // ── The capture hand-over (item 2) ──

    private sealed class Pair : IDisposable
    {
        public readonly Audio Old = new(), Fresh = new();
        public Audio.StreamCB OldCb => Cb(Old);
        public Audio.StreamCB FreshCb => Cb(Fresh);
        public Audio.TxCaptureHandover Handover => FreshCb.Handover;

        private static Audio.StreamCB Cb(Audio a) =>
            (Audio.StreamCB)typeof(Audio).GetProperty("CBData", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(a)!;

        public Pair() { Fresh.StandBeside(Old); }

        public void Dispose()
        {
            foreach (var a in new[] { Old, Fresh })
            {
                int key = (int)typeof(Audio).GetField("qKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(a)!;
                Audio.queues.Remove(key);
            }
        }
    }

    [Fact]
    public void A_standing_by_stream_feeds_nothing_until_the_running_one_hands_over_at_the_end_of_its_buffer()
    {
        using var p = new Pair();
        Assert.Same(p.Handover, p.OldCb.Handover);           // both streams share it
        Assert.False(Audio.EnterTxChain(p.Handover, p.FreshCb)); // standing by
        Assert.False(p.Fresh.TakenOver);
        Assert.True(Audio.EnterTxChain(p.Handover, p.OldCb));    // the running stream carries on
        p.Fresh.RequestTakeOver();                                // asked while the old buffer is in the chain
        Assert.False(p.Fresh.TakenOver);                          // nothing changes inside a buffer
        Assert.False(Audio.EnterTxChain(p.Handover, p.FreshCb));
        Audio.LeaveTxChain(p.Handover, mayHandOver: true);        // the end of that buffer
        Assert.True(p.Fresh.TakenOver);
        Assert.False(Audio.EnterTxChain(p.Handover, p.OldCb));    // retired: feeds nothing
        Assert.True(Audio.EnterTxChain(p.Handover, p.FreshCb));   // the next buffer is the new stream's
        Audio.LeaveTxChain(p.Handover, mayHandOver: true);
        Assert.True(p.Fresh.TakenOver);
    }

    [Fact]
    public void A_buffer_that_ends_the_stream_does_not_hand_over()
    {
        using var p = new Pair();
        p.Fresh.RequestTakeOver();
        Assert.True(Audio.EnterTxChain(p.Handover, p.OldCb));
        Audio.LeaveTxChain(p.Handover, mayHandOver: false);       // the running stream's last (aborting) buffer
        Assert.False(p.Fresh.TakenOver);                          // so the swap's bound, and a forced take-over, decide
        Assert.Same(p.FreshCb, p.Handover.Next);
    }

    [Fact]
    public void A_forced_take_over_never_lets_two_callbacks_into_the_chain()
    {
        using var p = new Pair();
        Assert.True(Audio.EnterTxChain(p.Handover, p.OldCb));     // a late buffer of the old stream, inside
        p.Fresh.ForceTakeOver();
        Assert.True(p.Fresh.TakenOver);
        Assert.False(Audio.EnterTxChain(p.Handover, p.FreshCb));  // finds the chain taken: feeds nothing
        Audio.LeaveTxChain(p.Handover, mayHandOver: true);
        Assert.True(Audio.EnterTxChain(p.Handover, p.FreshCb));   // the next buffer feeds it
        Assert.False(Audio.EnterTxChain(p.Handover, p.OldCb));
        Audio.LeaveTxChain(p.Handover, mayHandOver: true);
    }

    [Fact]
    public void A_stream_that_never_stood_by_counts_as_taken_over_and_a_closed_partner_leaves_only_the_force()
    {
        var lone = new Audio();
        int key = (int)typeof(Audio).GetField("qKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lone)!;
        try
        {
            Assert.True(lone.TakenOver);
            lone.StandBeside(null!);                                 // nobody to hand over
            Assert.False(lone.TakenOver);
            lone.RequestTakeOver();
            Assert.False(lone.TakenOver);
            lone.ForceTakeOver();
            Assert.True(lone.TakenOver);
        }
        finally { Audio.queues.Remove(key); }
    }

    [Fact]
    public void Two_callbacks_racing_through_a_hand_over_and_a_force_are_never_in_the_chain_together()
    {
        for (int round = 0; round < 20; round++)
        {
            bool forced = round % 2 == 1;
            using var p = new Pair();
            int inside = 0, overlap = 0, oldAfter = 0, freshFed = 0;
            bool handedOver = false, stop = false;
            using var go = new ManualResetEventSlim();
            void Callback(Audio.StreamCB me, bool isOld)
            {
                go.Wait();
                while (!Volatile.Read(ref stop))
                {
                    if (!Audio.EnterTxChain(p.Handover, me)) { Thread.SpinWait(20); continue; }
                    if (Interlocked.Increment(ref inside) != 1) Interlocked.Increment(ref overlap);
                    if (isOld && Volatile.Read(ref handedOver)) Interlocked.Increment(ref oldAfter);
                    if (!isOld) Interlocked.Increment(ref freshFed);
                    Thread.SpinWait(50);
                    Interlocked.Decrement(ref inside);
                    Audio.LeaveTxChain(p.Handover, mayHandOver: true);
                }
            }
            var a = new Thread(() => Callback(p.OldCb, true)) { IsBackground = true };
            var b = new Thread(() => Callback(p.FreshCb, false)) { IsBackground = true };
            a.Start(); b.Start(); go.Set();
            try
            {
                Thread.Sleep(5);
                if (forced) p.Fresh.ForceTakeOver(); else p.Fresh.RequestTakeOver();
                var until = DateTime.UtcNow.AddSeconds(5);
                while (!p.Fresh.TakenOver && DateTime.UtcNow < until) Thread.Sleep(1);
                Assert.True(p.Fresh.TakenOver);
                // A hand-over is made by the old stream's own callback, at the end
                // of its buffer, so from here it can never enter again. (After a
                // force, a buffer it had already begun may still finish: only the
                // overlap is asked of that case.)
                if (!forced) Volatile.Write(ref handedOver, true);
                Thread.Sleep(20);
            }
            finally
            {
                Volatile.Write(ref stop, true);
                Assert.True(a.Join(10000)); Assert.True(b.Join(10000));
            }
            Assert.Equal(0, overlap);
            Assert.Equal(0, oldAfter);    // a retired stream never fed again
            Assert.True(freshFed > 0);    // positive control: the new one did feed
            Assert.False(Audio.EnterTxChain(p.Handover, p.OldCb));
        }
    }

    // ── The run's and the server's whole lifetime (item 1) ──

    [Fact]
    public void A_run_with_no_abandoned_predecessor_is_admitted_at_once_and_one_stopped_while_waiting_gives_up_quietly()
    {
        using var x = new Rig();
        int admitWas = Radios.FlexBase.RemoteRunAdmissionMs;
        using var release = new ManualResetEventSlim();
        var earlier = new Thread(() => release.Wait(15000)) { IsBackground = true };
        earlier.Start();
        try
        {
            long run = (long)Get(x.B, "_remoteAudioRun")!;
            var sw = Stopwatch.StartNew();
            Assert.True(x.B.AdmitRemoteAudioRun(run));
            Assert.True(sw.ElapsedMilliseconds < 1000);

            Radios.FlexBase.RemoteRunAdmissionMs = 8000;
            Set(x.B, "_abandonedRemoteAudioThread", earlier);
            var stopper = new Timer(_ => Set(x.B, "stopRemoteAudio", true), null, 150, Timeout.Infinite);
            sw.Restart();
            Assert.False(x.B.AdmitRemoteAudioRun(run));            // the operator turned PC audio off meanwhile
            sw.Stop(); stopper.Dispose();
            Assert.True(sw.ElapsedMilliseconds < 3000, "it waited " + sw.ElapsedMilliseconds + " ms after being stopped");
            Assert.Same(earlier, Get(x.B, "_abandonedRemoteAudioThread")); // still remembered for the next run
        }
        finally
        {
            release.Set(); earlier.Join(5000);
            Set(x.B, "stopRemoteAudio", false);
            Set(x.B, "_abandonedRemoteAudioThread", null);
            Radios.FlexBase.RemoteRunAdmissionMs = admitWas;
        }
    }

    [Fact]
    public void Initialize_waits_for_the_old_run_s_own_Terminate_outside_the_gate_and_then_starts()
    {
        // The old run is late, not wedged: its server ends as soon as its own
        // Terminate posts the terminate. Initialize must not hold the gate while
        // it waits, or that Terminate could not run and the start would refuse.
        var oldQueue = new BlockingCollection<AudioAnchor.workItem>();
        var oldServer = new Thread(() => { oldQueue.TryTake(out _, 15000); }) { IsBackground = true };
        oldServer.Start();
        Audio.MoveServerGenerationForTest();                     // a generation of its own, never 0 (none started)
        var was = AudioAnchor.SwapServerForTest(oldServer, oldQueue, null);
        long oldGeneration = Audio.ServerGeneration;
        Assert.True(oldGeneration > 0);
        var factoryWas = AudioAnchor.ServerThreadForTest;
        int retireWas = AudioAnchor.PreviousServerRetireMs;
        Thread? newServer = null;
        AudioAnchor.ServerThreadForTest = _ => newServer = new Thread(() => { }) { IsBackground = true };
        try
        {
            AudioAnchor.PreviousServerRetireMs = 5000;
            bool started = false; long given = 0;
            var starter = new Thread(() => started = Audio.Initialize(null!, null!, out given)) { IsBackground = true };
            starter.Start();
            Thread.Sleep(150);                                   // the start is waiting for the old server
            Assert.True(starter.IsAlive);
            var sw = Stopwatch.StartNew();
            Audio.Terminate(oldGeneration);                      // the old run's own end, late
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 1500, "the old run's Terminate waited " + sw.ElapsedMilliseconds + " ms for the gate");
            Assert.True(starter.Join(5000));
            Assert.True(started);
            Assert.Equal(oldGeneration + 2, given);              // its end, then the new start
            Assert.NotNull(newServer);
        }
        finally
        {
            oldQueue.Add(new AudioAnchor.workItem(AudioAnchor.workItems.terminate));
            oldServer.Join(5000); newServer?.Join(5000);
            AudioAnchor.PreviousServerRetireMs = retireWas;
            AudioAnchor.ServerThreadForTest = factoryWas;
            AudioAnchor.SwapServerForTest(was.Server, was.Work, was.Unretired);
        }
    }

    [Fact]
    public void The_run_s_admission_comes_before_anything_shared_and_its_refusal_tears_nothing_down()
    {
        string remote = FlexBasePartials.JoinedText();
        int proc = remote.IndexOf("private void remoteAudioProc(long run)", StringComparison.Ordinal);
        Assert.True(proc > 0);
        int admit = remote.IndexOf("if (!AdmitRemoteAudioRun(run)) return;", proc, StringComparison.Ordinal);
        int wake = remote.IndexOf("Volatile.Write(ref _remoteAudioWake, wake);", proc, StringComparison.Ordinal);
        int micState = remote.IndexOf("BeginRemoteAudioRunMicState(run);", proc, StringComparison.Ordinal);
        int channels = remote.IndexOf("opusInputChannel = null;", proc, StringComparison.Ordinal);
        Assert.True(admit > proc && admit < wake && wake < micState && micState < channels,
            "the admission must come first, before the wake, the microphone state and the channels");
        // And the abandoned thread is what the stop remembers.
        int stop = remote.IndexOf("private void stopRemoteAudioThread()", StringComparison.Ordinal);
        string stopBody = remote.Substring(stop, remote.IndexOf("remoteAudioThread = null;", stop, StringComparison.Ordinal) - stop);
        Assert.Contains("Volatile.Write(ref _abandonedRemoteAudioThread, remoteAudioThread);", stopBody);
        // And no parameterless end of "whichever server is live" is left to call.
        string audio = File.ReadAllText(Path.Combine(RepoRoot(), "JJPortaudio", "JJPortaudio", "Audio.cs"));
        Assert.DoesNotContain("public static void Terminate()", audio);
    }
}
