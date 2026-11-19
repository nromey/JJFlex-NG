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
    /// Sprint 49 Track H2, rulings repair 1: Codex's five findings against
    /// 55347b04, each with its reproduction (adopted from
    /// <c>2026-11-18-codex-h2-rulings-results\CodexRulingsReviewTests.cs</c>,
    /// adjusted only where the fake needed it: the hold now schedules its
    /// backstop at the release and its end at the tail's hand-on, so the rig
    /// keeps every scheduled end; the try's open runs on a worker, so the rig
    /// runs that worker inline). Windowless: an unconnected FlexBase whose
    /// command queue collects writes, conditioner buffers of numbers, and fake
    /// streams that open no device.
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
        }

        private sealed class Rig : IDisposable
        {
            private readonly string _program = API.ProgramName;
            private readonly bool _gui = API.IsGUI;
            private readonly Action<string> _sink = Vita.VitaSocket.TraceSink;
            private readonly int _txRate = FlexBase.TxAudioCallbacksPerSecond;
            private readonly int _currentRate = TxCallbackRate.CurrentRate;
            public readonly FlexBase Base = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
            public readonly List<(int Ms, Action End)> Scheduled = new();

            public Rig()
            {
                Base.q.MainLoop = true; // collect, do not send
                Base.HeldUnkeyScheduler = (ms, end) => Scheduled.Add((ms, end));
                Set(Base, "_Transmit", true);
            }

            /// <summary>Run every end that is due by <paramref name="ms"/> after it was scheduled, once each.</summary>
            public void RunDue(int ms)
            {
                var due = Scheduled.Where(s => s.Ms <= ms).ToList();
                foreach (var s in due) { Scheduled.Remove(s); s.End(); }
            }

            public void RunAll()
            {
                var all = Scheduled.ToList();
                Scheduled.Clear();
                foreach (var s in all) s.End();
            }

            public void Delay(int samples)
            {
                Base.TxConditioner.KeySource = Base.TransmitKeySource;
                Base.TxConditioner.NoiseReducer = (_, _, _) => { };
                Base.TxConditioner.NoiseReducerAppliedLatencySamples = () => samples;
                Base.TxConditioner.Process(new float[960], 960, 48000);
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

        private static float[] Ones(int n) { var a = new float[n]; Array.Fill(a, 1f); return a; }

        // ── Fix 1 (P1): the unkey follows the audio, not a clock ──

        [Fact]
        public void The_unkey_must_not_precede_the_buffer_that_drains_the_tail()
        {
            using var x = new Rig();
            var line = new Queue<float>();
            for (int i = 0; i < 1440 * 2; i++) line.Enqueue(0);
            float lastOut = -1;
            x.Base.TxConditioner.NoiseReducer = (b, n, _) =>
            {
                for (int i = 0; i < n; i++) { line.Enqueue(b[i]); b[i] = line.Dequeue(); }
            };
            x.Base.TxConditioner.NoiseReducerAppliedLatencySamples = () => 1440; // 30 ms
            x.Base.TxConditioner.KeySource = x.Base.TransmitKeySource;
            // Shipped capture is a 100 ms block, processed as ten 10 ms frames.
            for (int f = 0; f < 10; f++) x.Base.TxConditioner.Process(Ones(960), 960, 48000);
            x.Base.UnkeyAfterNoiseDelay("review");
            Assert.Equal(33, x.Base.LastHeldUnkeyMs);
            Assert.Contains(1f, line); // positive control: speech remains delayed inside the reducer

            // Release just after this callback: at +33 ms the next callback (+100
            // ms) has not arrived. Whatever is due by then may run: no unkey.
            x.RunDue(33);
            Assert.Equal(0, x.Base.q.Count);

            // The next callback: frames go through and are handed on. The first
            // two frames (20 ms of silence) do not yet carry the 30 ms tail out.
            for (int f = 0; f < 2; f++)
            {
                var b = Ones(960);
                x.Base.TxConditioner.Process(b, 960, 48000);
                lastOut = b[959];
                x.Base.NoteTxFrameHandedOn();
                Assert.Single(x.Scheduled);        // only the backstop: the hand-on scheduled nothing
                x.RunDue(33);
                Assert.Equal(0, x.Base.q.Count);
            }
            Assert.Equal(1f, lastOut);             // speech was still coming out
            // The third frame carries the last of the speech out.
            var third = Ones(960);
            x.Base.TxConditioner.Process(third, 960, 48000);
            Assert.True(x.Base.TxConditioner.Drain!.TailOut);
            Assert.DoesNotContain(1f, line);       // nothing of the speech is left inside
            Assert.Equal(0, x.Base.q.Count);       // processed, but not yet handed on
            x.Base.NoteTxFrameHandedOn();
            var end = Assert.Single(x.Scheduled, s => s.Ms <= 33);
            Assert.True(end.Ms >= 3);              // at least the 10% margin
            x.RunDue(33);
            Assert.Equal(1, x.Base.q.Count);       // the unkey, after the tail was handed on
            Assert.Equal("Mox", x.Base.q.Dequeue().Name);
        }

        [Fact]
        public void A_stalled_capture_is_unkeyed_by_the_backstop_and_never_held_keyed()
        {
            using var x = new Rig();
            x.Delay(1440);
            x.Base.UnkeyAfterNoiseDelay("review");
            int rate = FlexBase.TxAudioCallbacksPerSecond;
            int expected = 33 + (int)Math.Ceiling(1000.0 / rate) + FlexBase.HeldUnkeyBackstopAllowanceMs;
            var backstop = Assert.Single(x.Scheduled);
            Assert.Equal(expected, backstop.Ms);
            Assert.Equal(expected, x.Base.LastHeldUnkeyBackstopMs);
            x.RunAll();                            // no buffer ever came
            Assert.False(x.Base.HeldUnkeyPending);
            Assert.Equal(1, x.Base.q.Count);
        }

        [Fact]
        public void The_hold_arithmetic_at_both_capture_rates()
        {
            Assert.Equal(3, FlexBase.HeldUnkeyMarginMs(30));
            Assert.Equal(18, FlexBase.HeldUnkeyMarginMs(180));
            Assert.Equal(33 + 100 + 50, FlexBase.HeldUnkeyBackstopMs(30, 10));
            Assert.Equal(198 + 100 + 50, FlexBase.HeldUnkeyBackstopMs(180, 10));
            Assert.Equal(33 + 20 + 50, FlexBase.HeldUnkeyBackstopMs(30, 50));
            Assert.Equal(198 + 20 + 50, FlexBase.HeldUnkeyBackstopMs(180, 50));
        }

        [Fact]
        public void A_drain_counts_only_drained_buffers_and_any_path_through_the_chain()
        {
            var c = new TxAudioConditioner { NoiseReducer = (_, _, _) => { }, NoiseReducerAppliedLatencySamples = () => 960 };
            bool keyed = true;
            c.KeySource = () => keyed;
            c.Process(Ones(960), 960, 48000);       // undrained: counts nothing
            var t = c.BeginDrain(960);
            Assert.Equal(0, t.DrainedSamples);
            c.Process(Ones(960), 960, 48000);
            Assert.Equal(480, t.DrainedSamples);
            Assert.False(t.TailOut);
            c.BypassAll = true;                      // CW: the chain stood aside; silence still passes
            c.Process(Ones(960), 960, 48000);
            Assert.True(t.TailOut);
            keyed = false;                           // the first unkeyed buffer ends the drain
            c.Process(Ones(960), 960, 48000);
            Assert.Null(c.Drain);
        }

        // ── Fix 2 (P1): a key-down after the hold always reopens the microphone ──

        [Fact]
        public void A_key_down_after_the_hold_expires_must_reopen_the_microphone()
        {
            using var x = new Rig();
            x.Delay(1440);
            x.Base.UnkeyAfterNoiseDelay("review");
            Assert.True(x.Base.TxConditioner.DrainInput);
            x.RunAll(); // the hold ends (here by its backstop), before any callback
            Assert.False(x.Base.HeldUnkeyPending);
            Assert.True(x.Base.TxConditioner.DrainInput); // positive control: the drain outlives the hold's end
            // A new over before any callback observed the very short unkeyed gap.
            x.Base.Transmit = true;
            Assert.False(x.Base.TxConditioner.DrainInput);
        }

        [Fact]
        public void A_key_down_after_an_immediate_unkey_reopens_the_microphone()
        {
            using var x = new Rig();
            x.Delay(1440);
            x.Base.UnkeyAfterNoiseDelay("review");
            x.Base.Transmit = false;                // a safety unkey during the hold
            Assert.True(x.Base.TxConditioner.DrainInput);
            x.Base.Transmit = true;                 // then a quick new key
            Assert.False(x.Base.TxConditioner.DrainInput);
        }

        // ── Fix 4 (P2): a held unkey dies with its over and its connection ──

        [Fact]
        public void Dispose_must_cancel_a_pending_unkey()
        {
            using var x = new Rig();
            x.Delay(1440);
            x.Base.UnkeyAfterNoiseDelay("review");
            Assert.True(x.Base.HeldUnkeyPending);
            x.Base.Dispose();
            Assert.False(x.Base.HeldUnkeyPending);
            x.RunAll();
            Assert.Equal(0, x.Base.q.Count);
        }

        [Fact]
        public void A_radio_unkey_report_must_cancel_a_pending_unkey()
        {
            using var x = new Rig();
            x.Delay(1440);
            x.Base.UnkeyAfterNoiseDelay("review");
            Assert.True(x.Base.HeldUnkeyPending);
            typeof(FlexBase).GetMethod("noteRadioMox", Any)!.Invoke(x.Base, new object[] { false });
            Assert.False(x.Base.HeldUnkeyPending);
            Assert.False(x.Base.TxConditioner.DrainInput);
            x.RunAll();
            Assert.Equal(0, x.Base.q.Count);
        }

        [Fact]
        public void The_remote_audio_stopping_or_a_released_radio_ends_a_pending_unkey()
        {
            using (var x = new Rig())
            {
                x.Delay(1440);
                x.Base.UnkeyAfterNoiseDelay("review");
                x.Base.CancelHeldUnkey("the remote audio is stopping", unkeyNow: true);
                Assert.False(x.Base.HeldUnkeyPending);
                Assert.False(x.Base.TxConditioner.DrainInput);
                x.RunAll();
                Assert.Equal(0, x.Base.q.Count); // no connection on this rig: nothing is written for it
            }
            using (var x = new Rig())
            {
                x.Delay(1440);
                x.Base.UnkeyAfterNoiseDelay("review");
                // The radio was released and another connection took its place
                // (a stand-in object nothing dereferences) before the end ran.
                Set(x.Base, "theRadio", RuntimeHelpers.GetUninitializedObject(typeof(Radio)));
                try
                {
                    x.RunAll();
                    Assert.False(x.Base.HeldUnkeyPending);
                    Assert.Equal(0, x.Base.q.Count);  // checked when the end runs, not only when it was scheduled
                    Assert.False(x.Base.TxConditioner.DrainInput);
                }
                finally { Set(x.Base, "theRadio", null!); }
            }
        }

        [Fact]
        public void The_queued_held_unkey_never_dereferences_a_radio_that_has_gone()
        {
            using var x = new Rig();
            x.Delay(1440);
            x.Base.UnkeyAfterNoiseDelay("review");
            x.RunAll();
            var item = x.Base.q.Dequeue();
            Assert.Equal("Mox", item.Name);
            // The rig has no radio (theRadio is null, as after a release): the
            // write checks at its run and does nothing, rather than throwing.
            var ex = Record.Exception(() => ((FlexBase.FunctionDel)item.Item)());
            Assert.Null(ex);
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
            Assert.Equal(0, fresh.Starts);
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
                Assert.Equal(1, old.Stops);
                Assert.Equal(1, fresh.Starts);
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
                Assert.Equal(0, fresh.Starts);
            }
        }

        [Fact]
        public void Our_key_down_during_the_swaps_stop_and_start_waits_for_them()
        {
            using var x = new Rig();
            var old = new FakeStream(); var fresh = new FakeStream();
            Channel(x, old);
            Set(x.Base, "_txTryFallbackDue", true);
            x.Base.TxCaptureOpenForTest = rate => fresh;
            Thread? keyer = null;
            bool keyDownReturned = false;
            bool returnedBeforeStart = false;
            old.OnStop = () =>
            {
                keyer = new Thread(() => { x.Base.Transmit = true; Volatile.Write(ref keyDownReturned, true); }) { IsBackground = true };
                keyer.Start();
                // Wait until the key-down is blocked on the admission (or has returned, which fails below).
                var until = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < until && !Volatile.Read(ref keyDownReturned)
                       && (keyer.ThreadState & ThreadState.WaitSleepJoin) == 0) Thread.Sleep(1);
                Thread.Sleep(20);
            };
            fresh.OnStart = () => returnedBeforeStart = Volatile.Read(ref keyDownReturned);
            Step(x);
            Assert.True(keyer!.Join(5000));
            Assert.Equal(1, fresh.Starts);
            Assert.False(returnedBeforeStart);        // the key-down waited for the new stream to start
            Assert.True(keyDownReturned);
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
