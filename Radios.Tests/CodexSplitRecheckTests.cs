using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using JJPortaudio;
using PortAudioSharp;
using Audio = JJPortaudio.Audio;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Codex's re-check of the H2 split at <c>ab5ba9a8</c>, adopted (Sprint 49 Track
/// H2 split, repair 2; the brief <c>planning\agile\sprint49-h2-split-repair-2.md</c>).
/// Source: <c>for-claude\2026-11-19-codex-h2-split-recheck-results\CodexSplitRecheckTests.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Windowless and silent: the REAL managed input callback (reached by
/// reflection, as Codex reached it) and the real <see cref="TxFramePipeline"/>,
/// with fake encode and send. No PortAudio function, device, sound, window,
/// radio or RF.
/// </para>
/// <para>
/// <b>Adapted, in three places, and only these.</b> (1) Each capture's pipeline
/// carries one shared <see cref="TxChainGate"/>, put there before any callback,
/// exactly as the transmit open path (<c>FlexBase.OpenTxCaptureAt</c>) does in
/// the app; that the app really does so is pinned in
/// <see cref="TxSplitRepairTwoTests"/>. Codex's file, run verbatim against
/// <c>ab5ba9a8</c> on this machine, failed his two concurrent-entry cases (2 of 7),
/// as his own log does; the gate made per swap, the design he found, is the
/// mutation that turns them red again. (2) The healthy hand-over is unkeyed,
/// because a swap is only ever admitted unkeyed and, since repair 2, a keyed one
/// never completes. (3) His keyed-repeat characterization is inverted: a keyed
/// hand-over is withdrawn and replays nothing, and its unkeyed twin keeps his
/// exact schedule as the characterization of the seam the ruling allows.
/// </para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class CodexSplitRecheckTests
{
    internal sealed class Capture : IDisposable
    {
        public readonly Audio Audio = new();
        public readonly int Key;
        public readonly Audio.StreamCB Cb;
        public bool Keyed;
        private static readonly PortAudio.PaStreamCallbackDelegate Callback =
            typeof(Audio).GetMethod("inputCallback", BindingFlags.NonPublic | BindingFlags.Static)!
                .CreateDelegate<PortAudio.PaStreamCallbackDelegate>();

        public Capture(TxChainGate gate, int samples = 1920, bool keyed = true)
        {
            Key = (int)typeof(Audio).GetField("qKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Audio)!;
            Cb = Audio.queues.getQ(Key);
            Cb.Active = true;
            Cb.UseOpus = true;
            Cb.Channels = 2;
            Cb.BufferSize = (uint)samples;
            Cb.OpusFrameSZ = (uint)Math.Min(samples, 1920); // 20 ms Opus frames inside 100 ms fallback buffers
            Cb.SampleRate = 48000;
            Cb.opusPool = new Audio.bufPool(Cb.OpusFrameSZ, 10);
            Cb.TxPipeline.Encode = _ => new byte[] { 1 };
            Cb.TxPipeline.Handler = _ => { };
            Keyed = keyed;
            Cb.TxPipeline.KeySource = () => Keyed;
            // Adaptation (1): the chain's gate, on the pipeline before any callback, as the open path puts it.
            Cb.TxPipeline.Gate = gate;
        }

        public void Deliver(float marker, double adcTime = 1, double currentTime = 2)
        {
            float[] samples = Enumerable.Repeat(marker, (int)Cb.BufferSize).ToArray();
            DeliverSamples(samples, adcTime, currentTime);
        }

        public void DeliverFrames(float[] markers, double adcTime, double currentTime)
        {
            Assert.Equal((int)Cb.BufferSize, markers.Length * (int)Cb.OpusFrameSZ);
            float[] samples = markers.SelectMany(m => Enumerable.Repeat(m, (int)Cb.OpusFrameSZ)).ToArray();
            DeliverSamples(samples, adcTime, currentTime);
        }

        private void DeliverSamples(float[] samples, double adcTime, double currentTime)
        {
            IntPtr ptr = Marshal.AllocHGlobal(samples.Length * sizeof(float));
            try
            {
                Marshal.Copy(samples, 0, ptr, samples.Length);
                var time = new PortAudio.PaStreamCallbackTimeInfo { inputBufferAdcTime = adcTime, currentTime = currentTime };
                Assert.Equal(PortAudio.PaStreamCallbackResult.paContinue,
                    Callback(ptr, IntPtr.Zero, Cb.BufferSize / 2, ref time, 0, new IntPtr(Key)));
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        public void Dispose()
        {
            Cb.opusPool.Done();
            Audio.queues.Remove(Key);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Forced_takeover_must_serialize_a_callback_already_inside_when_StandBeside_installs_its_guard(bool previousHandover, bool installedBefore)
    {
        var gate = new TxChainGate();
        using var old = new Capture(gate, 9600); // shipped 100 ms callback, five 20 ms frames
        using var fresh = new Capture(gate);
        using var previous = new Capture(gate);
        if (previousHandover)
        {
            Assert.True(old.Audio.StandBeside(previous.Audio));
            old.Audio.ForceTakeOver();
        }
        if (installedBefore) Assert.True(fresh.Audio.StandBeside(old.Audio)); // positive control: existing guard covers callback
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int inside = 0, overlap = 0, freshFed = 0;
        JJPortaudio.TxAudioProcessorCallback sharedConditioner = (buffer, _, _) =>
        {
            if (Interlocked.Increment(ref inside) != 1) Interlocked.Increment(ref overlap);
            try
            {
                if (buffer[0] == 1) { entered.Set(); Assert.True(release.Wait(10000)); }
                else Interlocked.Increment(ref freshFed);
            }
            finally { Interlocked.Decrement(ref inside); }
        };
        old.Cb.TxPipeline.Conditioner = fresh.Cb.TxPipeline.Conditioner = sharedConditioner;
        Exception? callbackError = null;
        var t = new Thread(() => { try { old.Deliver(1); } catch (Exception e) { callbackError = e; } }) { IsBackground = true };
        t.Start();
        long refusedBefore;
        try
        {
            Assert.True(entered.Wait(5000)); // actual callback already inside shared conditioner
            if (!installedBefore) Assert.True(fresh.Audio.StandBeside(old.Audio)); // actual worker publication can occur AFTER callback entered
            fresh.Deliver(2); // first standby buffer; WaitUntilProducing now has its positive control
            Assert.Equal(0, freshFed);
            fresh.Audio.RequestTakeOver();
            Assert.False(fresh.Audio.TakenOver);
            Thread.Sleep(350 + 20);
            fresh.Audio.ForceTakeOver(); // same deadline/force as the real swap
            refusedBefore = gate.Refusals;
            fresh.Deliver(3);
            Assert.Equal(0, overlap); // expected invariant: no two users of the persistent conditioner/mux/meter
            Assert.Equal(refusedBefore + 1, gate.Refusals); // and it was the chain's gate that kept it out
        }
        finally { release.Set(); Assert.True(t.Join(5000)); }
        Assert.Null(callbackError);
        fresh.Deliver(4);
        Assert.True(freshFed > 0); // positive control: new producer works after old leaves
        Assert.False(gate.Held);
    }

    [Fact]
    public void Healthy_handover_serializes_both_real_callbacks_and_the_retired_stream_feeds_nothing()
    {
        var gate = new TxChainGate();
        using var old = new Capture(gate, keyed: false); using var fresh = new Capture(gate, keyed: false); // adaptation (2): unkeyed
        var sent = new List<float>();
        old.Cb.TxPipeline.Encode = b => BitConverter.GetBytes(b[0]);
        fresh.Cb.TxPipeline.Encode = b => BitConverter.GetBytes(b[0]);
        old.Cb.TxPipeline.Handler = b => sent.Add(BitConverter.ToSingle(b));
        fresh.Cb.TxPipeline.Handler = old.Cb.TxPipeline.Handler;
        Assert.True(fresh.Audio.StandBeside(old.Audio));
        fresh.Deliver(10); // standby does not encode
        fresh.Audio.RequestTakeOver();
        old.Deliver(20); // completes buffer then transfers
        fresh.Deliver(30);
        old.Deliver(40); // retired does not encode
        Assert.Equal(new float[] { 20, 30 }, sent);
        Assert.True(fresh.Audio.TakenOver);
    }

    /// <summary>
    /// Codex's characterization of the real bound, kept as he wrote it: with the
    /// gate in place, a stalled old callback keeps the replacement out for EVERY
    /// buffer it stalls across, not one. The 350 and 110 ms figures bound only
    /// the wait before a forced take-over.
    /// </summary>
    [Fact]
    public void A_forced_takeover_with_the_guard_installed_can_drop_more_than_one_new_buffer_until_the_old_callback_returns()
    {
        var gate = new TxChainGate();
        using var old = new Capture(gate); using var fresh = new Capture(gate);
        Assert.True(fresh.Audio.StandBeside(old.Audio)); // even when the guard was installed early enough
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        old.Cb.TxPipeline.Conditioner = (_, _, _) => { entered.Set(); Assert.True(release.Wait(10000)); };
        int freshSent = 0;
        fresh.Cb.TxPipeline.Handler = _ => freshSent++;
        Exception? error = null;
        var t = new Thread(() => { try { old.Deliver(1); } catch (Exception e) { error = e; } }) { IsBackground = true };
        t.Start();
        try
        {
            Assert.True(entered.Wait(5000));
            fresh.Audio.ForceTakeOver();
            for (int i = 0; i < 4; i++) fresh.Deliver(2, 2 + i * .1, 2.1 + i * .1);
            Assert.Equal(0, freshSent); // all four drop; no one-buffer bound on the gate
        }
        finally { release.Set(); Assert.True(t.Join(5000)); }
        Assert.Null(error);
        fresh.Deliver(3);
        Assert.Equal(1, freshSent); // positive control once the old callback leaves
    }

    /// <summary>
    /// Codex's keyed-repeat schedule, inverted (adaptation 3, the brief's item
    /// 2). On ab5ba9a8 this sent 7, 7, 8, 9, 10, 11: the old stream's last 20 ms,
    /// then the same 20 ms again at the start of the new 100 ms buffer, into an
    /// over. Now the running stream finds the radio keyed at the end of its
    /// buffer and withdraws the hand-over, so nothing is replayed, the new
    /// stream feeds nothing, and the old one simply carries on.
    /// </summary>
    [Fact]
    public void A_keyed_handover_is_withdrawn_so_no_captured_interval_is_sent_twice_and_the_running_stream_carries_on()
    {
        var gate = new TxChainGate();
        using var old = new Capture(gate, 1920); using var fresh = new Capture(gate, 9600);
        var sent = new List<float>();
        old.Cb.TxPipeline.Encode = b => BitConverter.GetBytes(b[0]);
        fresh.Cb.TxPipeline.Encode = b => BitConverter.GetBytes(b[0]);
        old.Cb.TxPipeline.Handler = b => sent.Add(BitConverter.ToSingle(b));
        fresh.Cb.TxPipeline.Handler = old.Cb.TxPipeline.Handler;
        Assert.True(fresh.Audio.StandBeside(old.Audio));
        fresh.Deliver(7, .9, 1.0); // standby already delivers
        fresh.Audio.RequestTakeOver();
        old.Deliver(7, 1.000, 1.020);
        fresh.DeliverFrames(new float[] { 7, 8, 9, 10, 11 }, 1.000, 1.100);
        Assert.Equal(new float[] { 7 }, sent);            // nothing replayed into the over
        Assert.True(fresh.Audio.TakeOverWithdrawn);
        Assert.False(fresh.Audio.TakenOver);
        old.Deliver(8, 1.020, 1.040);                      // the running stream still produces
        fresh.DeliverFrames(new float[] { 12, 13, 14, 15, 16 }, 1.100, 1.200);
        Assert.Equal(new float[] { 7, 8 }, sent);
        fresh.Audio.RequestTakeOver();                     // a withdrawn hand-over stays withdrawn
        old.Deliver(9, 1.040, 1.060);
        Assert.False(fresh.Audio.TakenOver);
        Assert.Equal(new float[] { 7, 8, 9 }, sent);
    }

    /// <summary>
    /// His exact schedule, unkeyed: the CHARACTERIZATION of the seam the ruling
    /// allows ("an unkeyed handover may keep the make-before-break seam"). Two
    /// independently buffered streams are not sample aligned, so the first new
    /// buffer can repeat what the old stream sent last, here 20 ms. This pins
    /// that the repeat exists, so no text calls the seam clean; it is not a
    /// claim that the size is bounded (a different input latency moves it).
    /// </summary>
    [Fact]
    public void An_unkeyed_handover_can_send_the_same_captured_interval_twice_the_seam_the_ruling_allows()
    {
        var gate = new TxChainGate();
        using var old = new Capture(gate, 1920, keyed: false); using var fresh = new Capture(gate, 9600, keyed: false);
        var sent = new List<float>();
        old.Cb.TxPipeline.Encode = b => BitConverter.GetBytes(b[0]);
        fresh.Cb.TxPipeline.Encode = b => BitConverter.GetBytes(b[0]);
        old.Cb.TxPipeline.Handler = b => sent.Add(BitConverter.ToSingle(b));
        fresh.Cb.TxPipeline.Handler = old.Cb.TxPipeline.Handler;
        Assert.True(fresh.Audio.StandBeside(old.Audio));
        fresh.Deliver(7, .9, 1.0);
        fresh.Audio.RequestTakeOver();
        old.Deliver(7, 1.000, 1.020);
        fresh.DeliverFrames(new float[] { 7, 8, 9, 10, 11 }, 1.000, 1.100);
        Assert.Equal(new float[] { 7, 7, 8, 9, 10, 11 }, sent);
        Assert.Equal(6, old.Cb.TxPipeline.FramesSent + fresh.Cb.TxPipeline.FramesSent);
        Assert.True(fresh.Audio.TakenOver);
    }
}
