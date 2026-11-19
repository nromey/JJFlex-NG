using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJPortaudio;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track H2, rulings repair 1, as carried without the unkey hold
    /// (Noel's H1 = B, 2026-11-19): Codex's two findings against 55347b04 that
    /// are about the automatic try (the fall back's swap after a key-down, and
    /// the open that stalled receive), with their reproductions (adopted from
    /// <c>2026-11-18-codex-h2-rulings-results\CodexRulingsReviewTests.cs</c>;
    /// the try's open runs on a worker, so the rig runs that worker inline).
    /// His three findings about the hold, and their tests, stay on
    /// sprint49/h2-join. Windowless: an unconnected FlexBase whose command
    /// queue collects writes, and fake streams that open no device.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TxRulingsRepairOneTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static void Set(FlexBase b, string name, object value) => typeof(FlexBase).GetField(name, Any)!.SetValue(b, value);
        private static object Get(FlexBase b, string name) => typeof(FlexBase).GetField(name, Any)!.GetValue(b)!;

        /// <summary>A capture stream that opens no device: it counts its stops, starts and closes.</summary>
        private sealed class FakeStream : JJAudioStream
        {
            public int Stops, Starts, Closes;
            public Action? OnStop, OnStart;
            public bool StartResult = true;
            public override bool StopAudio() { Stops++; OnStop?.Invoke(); return true; }
            public override bool StartAudio() { Starts++; OnStart?.Invoke(); return StartResult; }
            public override void Close() { Closes++; }
            // Repair 1, make before break: delivers at once, and is handed over
            // to as soon as it asks (OnRequest runs inside the swap, first).
            public Action? OnRequest;
            public int Requests;
            private bool _taken;
            public override bool WaitUntilProducing(int timeoutMs) => true;
            public override void RequestTakeOver() { Requests++; OnRequest?.Invoke(); _taken = true; }
            public override bool HasTakenOver => _taken;
            public override void ForceTakeOver() { _taken = true; }
        }

        private sealed class Rig : IDisposable
        {
            private readonly string _program = API.ProgramName;
            private readonly bool _gui = API.IsGUI;
            private readonly Action<string> _sink = Vita.VitaSocket.TraceSink;
            private readonly int _txRate = FlexBase.TxAudioCallbacksPerSecond;
            private readonly int _currentRate = TxCallbackRate.CurrentRate;
            public readonly FlexBase Base = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };

            public Rig()
            {
                Base.q.MainLoop = true; // collect, do not send
                Set(Base, "_Transmit", true);
            }

            public void Dispose()
            {
                try { Set(Base, "_Transmit", false); Base.Dispose(); }
                finally
                {
                    API.ProgramName = _program; API.IsGUI = _gui; Vita.VitaSocket.TraceSink = _sink;
                    FlexBase.TxAudioCallbacksPerSecond = _txRate;
                    TxCallbackRate.CurrentRate = _currentRate;
                }
            }
        }

        // ── Fix 3 (P1): the fallback never swaps streams once keyed ──

        private static FlexBase.audioChannelData Channel(Rig x, FakeStream running)
        {
            var channel = (FlexBase.audioChannelData)RuntimeHelpers.GetUninitializedObject(typeof(FlexBase.audioChannelData));
            channel.PortAudioStream = running; channel.Started = true;
            Set(x.Base, "opusInputChannel", channel);
            Set(x.Base, "opusInputAvailable", true);
            Set(x.Base, "_txTryChannel", channel);
            Set(x.Base, "_txStreamAlreadyJudged", true);
            Set(x.Base, "_Transmit", false);
            x.Base.TxCaptureWorkerForTest = work => work(); // the worker, inline
            return channel;
        }

        private static void Step(Rig x) => typeof(FlexBase).GetMethod("TxTryStep", Any)!.Invoke(x.Base, null);

        [Fact]
        public void A_key_down_during_the_fallback_open_must_prevent_the_stream_swap()
        {
            using var x = new Rig();
            var old = new FakeStream(); var fresh = new FakeStream();
            Channel(x, old);
            Set(x.Base, "_txTryFallbackDue", true);
            x.Base.TxCaptureOpenForTest = rate => { Set(x.Base, "_Transmit", true); return fresh; };
            Step(x);
            Assert.True(x.Base.TransmitKeyedOrInterlock);
            Assert.Equal(0, old.Stops); // capture never stopped after the key-down
            Assert.Equal(1, fresh.Starts);   // repair 1: started beside it, standing by, feeding nothing
            Assert.Equal(0, fresh.Requests); // and never asked to take over
            Assert.Equal(1, fresh.Closes);
            Assert.True((bool)Get(x.Base, "_txTryFallbackDue")); // asked again at the next unkeyed moment
        }

        [Fact]
        public void An_unkeyed_fallback_swaps_and_our_key_down_admitted_during_the_open_prevents_it()
        {
            // Positive control: unkeyed throughout, the fall back swaps.
            using (var x = new Rig())
            {
                var old = new FakeStream(); var fresh = new FakeStream();
                var channel = Channel(x, old);
                Set(x.Base, "_txTryFallbackDue", true);
                x.Base.TxCaptureOpenForTest = rate => fresh;
                Step(x);
                Assert.Equal(0, old.Stops);      // repair 1: never stopped to make the swap
                Assert.Equal(1, old.Closes);     // closed off the loop once handed over
                Assert.Equal(1, fresh.Starts);
                Assert.Equal(1, fresh.Requests);
                Assert.Same(fresh, channel.PortAudioStream);
                Assert.False((bool)Get(x.Base, "_txTryFallbackDue"));
            }
            // Our own key-down admitted during the open, before the radio reports
            // it (so the radio's Mox still reads off): refused all the same.
            using (var x = new Rig())
            {
                var old = new FakeStream(); var fresh = new FakeStream();
                Channel(x, old);
                Set(x.Base, "_txTryFallbackDue", true);
                x.Base.TxCaptureOpenForTest = rate => { x.Base.Transmit = true; return fresh; };
                Step(x);
                Assert.False(x.Base.Transmit);           // the radio has not reported it
                Assert.Equal(0, old.Stops);
                Assert.Equal(0, fresh.Requests);         // started beside, never asked to take over
            }
        }

        /// <summary>
        /// The H2 split keeps keying exactly as it is (Noel's H1, 2026-11-19): our
        /// key-down is recorded against a swap, never held by it. On
        /// sprint49/h2-join this test asserted the opposite (the key-down waited
        /// for the swap's stop and start); that wait put a key press behind the
        /// audio server, so it is not carried. Since the split's repair 1 the swap
        /// no longer stops the microphone at all: the key-down here lands inside
        /// the swap itself (under its admission lock, as it asks the running
        /// stream to hand over), returns at once, and meets a running stream that
        /// was never stopped.
        /// </summary>
        [Fact]
        public void Our_key_down_during_the_swaps_hand_over_is_never_held_by_it_and_meets_a_live_microphone()
        {
            using var x = new Rig();
            var old = new FakeStream(); var fresh = new FakeStream();
            Channel(x, old);
            Set(x.Base, "_txTryFallbackDue", true);
            x.Base.TxCaptureOpenForTest = rate => fresh;
            bool returnedDuringSwap = false;
            int oldStopsAtKeyDown = -1;
            fresh.OnRequest = () =>
            {
                bool keyDownReturned = false;
                oldStopsAtKeyDown = old.Stops;
                var keyer = new Thread(() => { x.Base.Transmit = true; Volatile.Write(ref keyDownReturned, true); }) { IsBackground = true };
                keyer.Start();
                // The key-down returns while the swap is still inside its hand-over.
                returnedDuringSwap = keyer.Join(5000) && Volatile.Read(ref keyDownReturned);
            };
            Step(x);
            Assert.True(returnedDuringSwap, "our key-down waited on the swap");
            Assert.Equal(0, oldStopsAtKeyDown);           // the microphone was running when the over began
            Assert.Equal(1, fresh.Requests);              // positive control: the swap really ran its hand-over
            Assert.Equal(0, old.Stops);
            Assert.Equal("Mox", x.Base.q.Dequeue().Name); // the key-down's write, queued as it always is
        }

        // ── Fix 5 (P2): a failed automatic open never stalls receive ──

        [Fact]
        public void The_trys_open_runs_off_the_loop_and_a_refused_open_keeps_a_cannot_open_alongside_verdict()
        {
            string root = Path.Combine(Path.GetTempPath(), "jjf-h2r1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var storeWas = TxCallbackRate.StoreForTest;
            var costWas = TxCallbackRate.HelperFrameCostP99Nanos;
            try
            {
                TxCallbackRate.StoreForTest = new TxCallbackVerdictStore(root);
                TxCallbackRate.HelperFrameCostP99Nanos = 1_000_000; // the engines qualify
                using var x = new Rig();
                var old = new FakeStream();
                Channel(x, old);
                x.Base.TxCaptureWorkerForTest = null;               // a real worker thread, as in the app
                using var openRelease = new ManualResetEventSlim(false);
                using var openEntered = new ManualResetEventSlim(false);
                x.Base.TxCaptureOpenForTest = rate => { openEntered.Set(); openRelease.Wait(10000); return null!; }; // a refused open that takes its time
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Step(x);                                            // the loop's look
                sw.Stop();
                Assert.True(openEntered.Wait(5000));                // the open did start
                Assert.False(openRelease.IsSet);
                Assert.True(sw.ElapsedMilliseconds < 1000, "the loop waited " + sw.ElapsedMilliseconds + " ms for the open");
                Step(x);                                            // looks again while the open is out: nothing, no block
                openRelease.Set();
                var until = DateTime.UtcNow.AddSeconds(5);
                object job;
                while ((job = Get(x.Base, "_txOpenJob")) != null
                       && (int)job.GetType().GetField("State")!.GetValue(job)! != 1 && DateTime.UtcNow < until) Thread.Sleep(5);
                Step(x);                                            // the loop takes the result
                Assert.Equal(0, old.Stops);                          // the running microphone was never touched
                var capture = new TxCaptureIdentity("", -1, (int)FlexBase.OpusTxSampleRateSetting, 2);
                var verdict = TxCallbackRate.StoreForTest.Find(capture, TxCallbackRate.Fast);
                Assert.NotNull(verdict);
                Assert.True(verdict!.CannotOpenAlongside);
                // The next connection does not try again while it is held.
                var next = TxCallbackRate.ShouldTry(null, 1_000_000, verdict, DateTime.UtcNow, TxCallbackRate.Shipped, false, false, false, true);
                Assert.Equal(TxTryAnswer.NotThisConnection, next.Answer);
                Assert.Contains("open alongside", next.Reason);
            }
            finally
            {
                TxCallbackRate.StoreForTest = storeWas;
                TxCallbackRate.HelperFrameCostP99Nanos = costWas;
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void A_cannot_open_alongside_verdict_is_not_a_verdict_on_the_rate_and_expires()
        {
            var v = new TxCallbackVerdict { CallbacksPerSecond = TxCallbackRate.Fast, Passed = false, CannotOpenAlongside = true, MeasuredUtc = DateTime.UtcNow };
            var d = TxCallbackRate.Decide(null, 1_000_000, v, DateTime.UtcNow);
            Assert.Equal(TxCallbackRate.Shipped, d.CallbacksPerSecond);
            Assert.DoesNotContain("dropped", d.Reason);
            v.MeasuredUtc = DateTime.UtcNow - TxCallbackRate.MaxVerdictAge - TimeSpan.FromDays(1);
            Assert.Equal(TxTryAnswer.Try, TxCallbackRate.ShouldTry(null, 1_000_000, v, DateTime.UtcNow, TxCallbackRate.Shipped, false, false, false, true).Answer);
        }
    }
}
