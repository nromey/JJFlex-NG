using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using JJPortaudio;
using Radios;
using Xunit;
using static Radios.Tests.TxJoinRepairThreeTests;

namespace Radios.Tests;

/// <summary>
/// Sprint 49 Track H2 split, repair 2 (the brief
/// <c>planning\agile\sprint49-h2-split-repair-2.md</c>), beside Codex's adopted
/// reproductions in <see cref="CodexSplitRecheckTests"/>: the transmit chain's
/// one gate is put on every stream by the open path before it can produce, a
/// hand-over settles exactly once, and a key-down admitted before it completes
/// abandons the swap with the running stream still producing.
/// </summary>
/// <remarks>
/// Windowless and silent: JJPortaudio's own <c>Audio</c> stream blocks and the
/// callback's two helpers, an unconnected FlexBase whose command queue
/// collects writes, and fake streams that open no device. No PortAudio stream,
/// no sound, no radio.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class TxSplitRepairTwoTests
{
    private static Audio.StreamCB Cb(Audio a) =>
        (Audio.StreamCB)typeof(Audio).GetProperty("CBData", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(a)!;

    private static int Key(Audio a) => (int)typeof(Audio).GetField("qKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(a)!;

    private static void Drop(params Audio[] audios) { foreach (var a in audios) Audio.queues.Remove(Key(a)); }

    // ── Item 1: one serialization, on the chain, from the open ──

    [Fact]
    public void The_transmit_open_path_puts_one_chain_gate_on_every_stream_before_it_is_started()
    {
        using var x = new Rig();
        var audios = new[] { new Audio(), new Audio() };
        try
        {
            int n = 0;
            x.B.TxCaptureOpenForTest = _ =>
            {
                var s = new JJAudioStream();
                typeof(JJAudioStream).GetField("aud", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(s, audios[n++]);
                return s;
            };
            var first = (JJAudioStream)Call(x.B, "OpenTxCaptureAt", TxCallbackRate.Shipped, null)!;
            var second = (JJAudioStream)Call(x.B, "OpenTxCaptureAt", TxCallbackRate.Fast, null)!;
            var gate = (TxChainGate)Get(x.B, "txChainGate")!;
            Assert.NotNull(gate);
            Assert.Same(gate, first.TransmitPipeline.Gate);
            Assert.Same(gate, second.TransmitPipeline.Gate);
            // Before the stream starts: the open path starts nothing, so the
            // stream's first callback (if it ever has one) already finds it.
            Assert.False(Cb(audios[0]).Started);
            Assert.False(Cb(audios[1]).Started);
            // The self-clocked source runs on the stream's own pipeline, so it
            // is under the same gate.
            Assert.Same(Cb(audios[0]).TxPipeline, first.TransmitPipeline);
        }
        finally { Drop(audios); }
    }

    [Fact]
    public void The_open_path_sets_the_gate_with_the_key_source_and_only_there()
    {
        string root = FindRepoRoot();
        string text = File.ReadAllText(Path.Combine(root, "Radios", "FlexBase.TxCaptureTry.cs"));
        int open = text.IndexOf("private JJAudioStream OpenTxCaptureAt(", StringComparison.Ordinal);
        int end = text.IndexOf("private enum TxSwapResult", open, StringComparison.Ordinal);
        Assert.True(open > 0 && end > open);
        string body = text.Substring(open, end - open);
        Assert.Contains("pipeline.Gate = txChainGate;", body);
        Assert.Equal(1, Regex.Matches(text, @"\.Gate\s*=\s*txChainGate").Count);
        // And nothing in Radios hands a stream a different gate.
        foreach (var file in Directory.GetFiles(Path.Combine(root, "Radios"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            string t = File.ReadAllText(file);
            Assert.Equal(file.EndsWith("FlexBase.TxCaptureTry.cs") ? 1 : 0, Regex.Matches(t, @"\bnew\s+(JJPortaudio\.)?TxChainGate\s*\(").Count);
        }
    }

    [Fact]
    public void A_stream_cannot_stand_beside_one_that_is_not_on_a_guarded_chain_and_takes_the_running_ones_gate()
    {
        var running = new Audio(); var fresh = new Audio();
        try
        {
            Assert.False(fresh.StandBeside(running));          // the running stream never took a gate
            Assert.Null(Cb(running).Handover);                  // nothing was installed on either
            Assert.Null(Cb(fresh).Handover);
            // Positive control: on a guarded chain, it stands beside, and the
            // replacement (not started yet) takes the running stream's gate.
            var gate = new TxChainGate();
            Cb(running).TxPipeline.Gate = gate;
            Cb(fresh).TxPipeline.Gate = new TxChainGate();
            Assert.True(fresh.StandBeside(running));
            Assert.Same(gate, Cb(fresh).TxPipeline.Gate);
            Assert.Same(Cb(fresh).Handover, Cb(running).Handover);
        }
        finally { Drop(running, fresh); }
    }

    [Fact]
    public void A_stream_on_the_chain_but_in_no_swap_holds_the_gate_for_its_buffer_and_gives_it_back()
    {
        var a = new Audio();
        try
        {
            var gate = new TxChainGate();
            var cb = Cb(a);
            cb.TxPipeline.Gate = gate;
            Assert.True(Audio.EnterTxChain(null, gate, cb));
            Assert.True(gate.Held);
            Assert.False(Audio.EnterTxChain(null, gate, cb)); // a second producer is kept out
            Audio.LeaveTxChain(null, gate, cb, mayHandOver: true);
            Assert.False(gate.Held);
            Assert.True(Audio.EnterTxChain(null, null, cb));  // no gate, no swap: nothing to share
        }
        finally { Drop(a); }
    }

    [Fact]
    public void The_self_clocked_source_takes_the_chain_gate_and_sends_nothing_while_another_producer_is_inside()
    {
        var pipeline = new TxFramePipeline { Encode = _ => new byte[] { 1 } };
        int sent = 0;
        pipeline.Handler = _ => sent++;
        var gate = new TxChainGate();
        pipeline.Gate = gate;
        var pump = new TxFramePump(pipeline, 24000, 240);
        long tps = 10_000_000;
        pump.Start(0);
        Assert.True(gate.TryEnter());                       // a capture callback inside
        Assert.Equal(0, pump.PumpOnce(tps / 10, tps));       // 100 ms owed, none sent
        Assert.Equal(0, sent);
        Assert.Equal(1, gate.Refusals);                      // the pump found it taken
        gate.Leave();
        Assert.True(pump.PumpOnce(tps / 5, tps) > 0);        // positive control
        Assert.False(gate.Held);
    }

    // ── Item 2: a hand-over settles exactly once; a key-down withdraws it ──

    private sealed class Pair : IDisposable
    {
        public readonly Audio Old = new(), Fresh = new();
        public readonly TxChainGate Gate = new();
        public Audio.StreamCB OldCb => Cb(Old);
        public Audio.StreamCB FreshCb => Cb(Fresh);
        public Audio.TxCaptureHandover Handover => FreshCb.Handover;
        public Pair()
        {
            OldCb.TxPipeline.Gate = Gate; FreshCb.TxPipeline.Gate = Gate;
            Assert.True(Fresh.StandBeside(Old));
        }
        public void Dispose() => Drop(Old, Fresh);
    }

    [Fact]
    public void A_withdrawn_take_over_never_completes_and_a_completed_one_cannot_be_withdrawn()
    {
        using (var p = new Pair())
        {
            Assert.True(p.Fresh.WithdrawTakeOver());           // a key-down before the swap even asks
            p.Fresh.RequestTakeOver();                          // stays withdrawn
            Assert.True(Audio.EnterTxChain(p.Handover, p.Gate, p.OldCb));
            Audio.LeaveTxChain(p.Handover, p.Gate, p.OldCb, mayHandOver: true);
            p.Fresh.ForceTakeOver();                            // not forced either
            Assert.False(p.Fresh.TakenOver);
            Assert.True(p.Fresh.TakeOverWithdrawn);
            Assert.True(Audio.EnterTxChain(p.Handover, p.Gate, p.OldCb));   // the running stream carries on
            Audio.LeaveTxChain(p.Handover, p.Gate, p.OldCb, mayHandOver: true);
            Assert.False(Audio.EnterTxChain(p.Handover, p.Gate, p.FreshCb));
            Assert.True(p.Fresh.WithdrawTakeOver());           // idempotent
        }
        using (var p = new Pair())
        {
            p.Fresh.RequestTakeOver();
            Assert.True(Audio.EnterTxChain(p.Handover, p.Gate, p.OldCb));
            Audio.LeaveTxChain(p.Handover, p.Gate, p.OldCb, mayHandOver: true); // unkeyed: completes
            Assert.True(p.Fresh.TakenOver);
            Assert.False(p.Fresh.WithdrawTakeOver());          // too late: it had completed
            Assert.False(p.Fresh.TakeOverWithdrawn);
        }
    }

    [Fact]
    public void A_running_stream_that_finds_the_radio_keyed_at_the_end_of_its_buffer_withdraws_the_hand_over()
    {
        using var p = new Pair();
        bool keyed = true;
        p.OldCb.TxPipeline.KeySource = () => keyed;           // the interlock, as a hardware key or VOX keys it
        p.Fresh.RequestTakeOver();
        Assert.True(Audio.EnterTxChain(p.Handover, p.Gate, p.OldCb));
        Audio.LeaveTxChain(p.Handover, p.Gate, p.OldCb, mayHandOver: true);
        Assert.True(p.Fresh.TakeOverWithdrawn);
        keyed = false;                                          // an unkey does not revive it
        Assert.True(Audio.EnterTxChain(p.Handover, p.Gate, p.OldCb));
        Audio.LeaveTxChain(p.Handover, p.Gate, p.OldCb, mayHandOver: true);
        Assert.False(p.Fresh.TakenOver);
        // A key source that throws reads as keyed.
        using var q = new Pair();
        q.OldCb.TxPipeline.KeySource = () => throw new InvalidOperationException();
        q.Fresh.RequestTakeOver();
        Assert.True(Audio.EnterTxChain(q.Handover, q.Gate, q.OldCb));
        Audio.LeaveTxChain(q.Handover, q.Gate, q.OldCb, mayHandOver: true);
        Assert.True(q.Fresh.TakeOverWithdrawn);
        Assert.False(q.Gate.Held);
    }

    [Fact]
    public void A_key_down_and_the_end_of_a_buffer_racing_for_one_hand_over_settle_it_exactly_once()
    {
        int withdrawnRounds = 0, completedRounds = 0;
        for (int round = 0; round < 300; round++)
        {
            using var p = new Pair();
            int oldAfterCompletion = 0, freshFedWhileWithdrawn = 0;
            bool stop = false;
            using var go = new ManualResetEventSlim();
            p.Fresh.RequestTakeOver();
            var oldThread = new Thread(() =>
            {
                go.Wait();
                Thread.SpinWait(round % 13 * 20);
                while (!Volatile.Read(ref stop))
                {
                    if (!Audio.EnterTxChain(p.Handover, p.Gate, p.OldCb)) { Thread.SpinWait(5); continue; }
                    if (p.Fresh.TakenOver) Interlocked.Increment(ref oldAfterCompletion);
                    Thread.SpinWait(round % 7 * 3);
                    Audio.LeaveTxChain(p.Handover, p.Gate, p.OldCb, mayHandOver: true);
                }
            }) { IsBackground = true };
            bool withdrew = false;
            var keyThread = new Thread(() => { go.Wait(); Thread.SpinWait(round % 11 * 20); withdrew = p.Fresh.WithdrawTakeOver(); }) { IsBackground = true };
            oldThread.Start(); keyThread.Start(); go.Set();
            Assert.True(keyThread.Join(5000));
            Thread.Sleep(1);
            if (withdrew && Audio.EnterTxChain(p.Handover, p.Gate, p.FreshCb))
            {
                freshFedWhileWithdrawn++;
                Audio.LeaveTxChain(p.Handover, p.Gate, p.FreshCb, mayHandOver: true);
            }
            Volatile.Write(ref stop, true);
            Assert.True(oldThread.Join(5000));
            // Exactly one outcome, and it agrees with what the key-down was told.
            Assert.NotEqual(p.Fresh.TakenOver, p.Fresh.TakeOverWithdrawn);
            Assert.Equal(withdrew, p.Fresh.TakeOverWithdrawn);
            Assert.Equal(0, oldAfterCompletion);
            Assert.Equal(0, freshFedWhileWithdrawn);
            if (withdrew) withdrawnRounds++; else completedRounds++;
            Assert.False(p.Gate.Held);
        }
        // Positive control: both outcomes really were raced (a fixed winner
        // would prove nothing about the race).
        Assert.True(withdrawnRounds > 0, "the key-down never won");
        Assert.True(completedRounds > 0, "the hand-over never won");
    }

    // ── Item 2 at the swap: abandoned, the running stream untouched, asked again ──

    /// <summary>A capture stream that opens no device, with the real hand-over's one outcome.</summary>
    internal sealed class HandFake : JJAudioStream
    {
        public int Stops, Starts, Closes, Requests, Forced;
        public bool HandsOver = true;
        public Action? OnRequest;
        private volatile bool _taken, _withdrawn;
        private readonly object _gate = new();
        public override bool StopAudio() { Stops++; return true; }
        public override bool StartAudio() { Starts++; return true; }
        public override void Close() { Closes++; }
        public override bool WaitUntilProducing(int timeoutMs) => true;
        public override void RequestTakeOver() { Requests++; OnRequest?.Invoke(); lock (_gate) { if (HandsOver && !_withdrawn) _taken = true; } }
        public override bool HasTakenOver => _taken;
        public override void ForceTakeOver() { lock (_gate) { if (_withdrawn || _taken) return; Forced++; _taken = true; } }
        public override bool WithdrawTakeOver() { lock (_gate) { if (_taken) return false; _withdrawn = true; return true; } }
        public override bool TakeOverWithdrawn => _withdrawn;
    }

    private static string Swap(Rig x, HandFake fresh, HandFake old, out string? refusal)
    {
        var args = new object?[] { fresh, old, TxCallbackRate.Fast, "test", 0L, null };
        var result = Call(x.B, "SwapTxCaptureStreamAdmitted", args)!.ToString()!;
        refusal = (string?)args[5];
        return result;
    }

    [Fact]
    public void A_key_down_while_the_swap_waits_on_a_stalled_stream_abandons_it_rather_than_forcing()
    {
        using var x = new Rig();
        var old = new HandFake(); var fresh = new HandFake { HandsOver = false }; // the running stream has stopped producing
        var channel = RepairTwoChannel(x, old);
        var keyer = new Thread(() => { Thread.Sleep(60); Set(x.B, "_Transmit", true); }) { IsBackground = true }; // the radio reports keyed (a hardware key)
        keyer.Start();
        var result = Swap(x, fresh, old, out var refusal);
        Assert.True(keyer.Join(5000));
        Assert.Equal("Refused", result);
        Assert.Contains("key-down", refusal);
        Assert.Equal(0, fresh.Forced);                    // as ruled: the forced case is abandoned too
        Assert.True(fresh.TakeOverWithdrawn);
        Assert.Same(old, channel.PortAudioStream);        // the running stream carries on
        Assert.Equal(0, old.Stops);
        Assert.Equal(0, old.Closes);
        Assert.Null(Get(x.B, "_txPendingTakeOver"));
    }

    [Fact]
    public void Unkeyed_a_stalled_running_stream_is_taken_over_by_force_after_the_bound()
    {
        using var x = new Rig();
        var old = new HandFake(); var fresh = new HandFake { HandsOver = false };
        var channel = RepairTwoChannel(x, old);
        x.B.TxCaptureWorkerForTest = work => work();       // the close, inline
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = Swap(x, fresh, old, out _);
        sw.Stop();
        Assert.Equal("Swapped", result);
        Assert.Equal(1, fresh.Forced);
        Assert.True(sw.ElapsedMilliseconds >= FlexBase.TxHandoverWaitMs(TxCallbackRate.Shipped) - 5);
        Assert.Same(fresh, channel.PortAudioStream);
        Assert.Equal(0, old.Stops);                         // never stopped; closed off the loop
        Assert.Equal(1, old.Closes);
    }

    [Fact]
    public void A_key_down_during_the_trys_hand_over_abandons_it_closes_the_new_stream_and_asks_again_later()
    {
        using var x = new Rig();
        var old = new HandFake(); var fresh = new HandFake();
        var channel = RepairTwoChannel(x, old);
        x.B.TxCaptureWorkerForTest = work => work();       // the close, inline
        Set(x.B, "_txTryDone", true);
        bool keyDownReturned = false;
        fresh.OnRequest = () =>
        {
            // Our key-down from another thread while the swap asks: it is
            // admitted before the hand-over completes, and never waits on it.
            var keyer = new Thread(() => { x.B.Transmit = true; Volatile.Write(ref keyDownReturned, true); }) { IsBackground = true };
            keyer.Start();
            Assert.True(keyer.Join(5000));
        };
        var jobType = typeof(FlexBase).GetNestedType("TxOpenJob", BindingFlags.NonPublic)!;
        var job = Activator.CreateInstance(jobType, true)!;
        Set(job, "Kind", Enum.Parse(typeof(FlexBase).GetNestedType("TxOpenKind", BindingFlags.NonPublic)!, "Try"));
        Set(job, "Rate", TxCallbackRate.Fast);
        Set(job, "Channel", channel);
        Set(job, "Wake", Get(x.B, "_remoteAudioWake"));
        Set(job, "Server", JJPortaudio.Audio.ServerGeneration);
        Set(job, "KeyDownsAtStart", 0L);
        Set(job, "Stream", fresh);
        Set(job, "Beside", old);
        Call(x.B, "TxOpenReturned", job, channel);
        Assert.True(Volatile.Read(ref keyDownReturned));    // never held
        Assert.Equal("Mox", x.B.q.Dequeue().Name);          // the key-down's write, queued as it always is
        Assert.True(fresh.TakeOverWithdrawn);
        Assert.Same(old, channel.PortAudioStream);
        Assert.True(channel.Started);
        Assert.Equal(0, old.Stops);
        Assert.Equal(0, old.Closes);
        Assert.Equal(1, fresh.Closes);
        Assert.False((bool)Get(x.B, "_txTryDone")!);         // asked again at the next unkeyed moment
        Assert.False((bool)Get(x.B, "_txTryRunning")!);
    }

    [Fact]
    public void Our_key_down_withdraws_only_the_hand_over_in_progress_and_nothing_it_does_can_fail_the_key_down()
    {
        using var x = new Rig();
        Set(x.B, "_Transmit", false);
        // Nothing in progress: nothing touched.
        x.B.Transmit = true;
        Assert.Equal("Mox", x.B.q.Dequeue().Name);
        // In progress: withdrawn.
        var pending = new HandFake();
        Set(x.B, "_txPendingTakeOver", pending);
        x.B.Transmit = true;
        Assert.True(pending.TakeOverWithdrawn);
        Assert.Equal("Mox", x.B.q.Dequeue().Name);
        // A withdrawal that throws: the key-down is queued all the same.
        Set(x.B, "_txPendingTakeOver", new ThrowingFake());
        x.B.Transmit = true;
        Assert.Equal("Mox", x.B.q.Dequeue().Name);
        Set(x.B, "_txPendingTakeOver", null);
    }

    private sealed class ThrowingFake : JJAudioStream
    {
        public override bool WithdrawTakeOver() => throw new InvalidOperationException("test");
    }

    [Fact]
    public void The_swap_publishes_its_stream_before_it_reads_the_count_and_the_key_down_counts_before_it_reads_the_stream()
    {
        string text = File.ReadAllText(Path.Combine(FindRepoRoot(), "Radios", "FlexBase.TxCaptureTry.cs"));
        int note = text.IndexOf("private void NoteTxKeyDownAdmitted()", StringComparison.Ordinal);
        int inc = text.IndexOf("Interlocked.Increment(ref _txKeyDownAdmissions)", note, StringComparison.Ordinal);
        int read = text.IndexOf("Volatile.Read(ref _txPendingTakeOver)", note, StringComparison.Ordinal);
        Assert.True(note > 0 && inc > note && read > inc);
        int swap = text.IndexOf("private TxSwapResult SwapTxCaptureStreamAdmitted(", StringComparison.Ordinal);
        int publish = text.IndexOf("Interlocked.Exchange(ref _txPendingTakeOver, fresh)", swap, StringComparison.Ordinal);
        int count = text.IndexOf("TxKeyedSinceSwapAdmitted(keyDownsAtStart)", swap, StringComparison.Ordinal);
        int request = text.IndexOf("fresh.RequestTakeOver()", swap, StringComparison.Ordinal);
        Assert.True(swap > 0 && publish > swap && count > publish && request > count);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
