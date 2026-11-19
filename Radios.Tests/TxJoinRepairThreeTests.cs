using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
    /// Sprint 49 Track H2, join repair 3: Codex's five findings against
    /// 7019398b (two P1, three P2), each with its reproduction adopted from
    /// <c>2026-11-19-codex-h2-join-results\CodexH2JoinReviewTests.cs</c>, the
    /// tests each fix needs, and the schedule explorations
    /// (<see cref="TxJoinScheduleExplorationTests"/>). Adopted verbatim except
    /// the backstop's, which now measures the stream's latency BEFORE the
    /// release, as a real stream does on every callback (the reproduction
    /// measured it only after, where no fix could see it), and asserts the
    /// fixed outcome instead of the failing one. Windowless: an unconnected
    /// FlexBase whose command queue collects writes, an uninitialized vendor
    /// radio with no command transport, conditioner buffers of numbers, and
    /// fake streams that open no device. No test may reach the real microphone
    /// open: every rig sets <see cref="FlexBase.TxCaptureOpenForTest"/>.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TxJoinRepairThreeTests
    {
        internal const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        internal static void Set(object b, string name, object? value) => b.GetType().GetField(name, Any)!.SetValue(b, value);
        internal static object? Get(object b, string name) => b.GetType().GetField(name, Any)!.GetValue(b);
        internal static object? Call(FlexBase b, string method, params object?[] args) =>
            typeof(FlexBase).GetMethod(method, Any)!.Invoke(b, args);

        /// <summary>A capture stream that opens no device: it counts its stops, starts and closes.</summary>
        internal sealed class Fake : JJAudioStream
        {
            public int Stops, Starts, Closes;
            public bool Result = true;
            public override bool StopAudio() { Stops++; return true; }
            public override bool StartAudio() { Starts++; return Result; }
            public override void Close() { Closes++; }
        }

        internal sealed class Rig : IDisposable
        {
            private readonly string _program = API.ProgramName;
            private readonly bool _gui = API.IsGUI;
            private readonly Action<string> _sink = Vita.VitaSocket.TraceSink;
            private readonly int _txRate = FlexBase.TxAudioCallbacksPerSecond;
            private readonly int _currentRate = TxCallbackRate.CurrentRate;
            public readonly FlexBase B = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
            public int Now;
            public readonly List<(int Due, Action End)> Ends = new();

            public Rig()
            {
                B.q.MainLoop = true; // collect, do not send
                Set(B, "_Transmit", true);
                B.HeldUnkeyScheduler = (ms, a) => Ends.Add((Now + ms, a));
                // Never the real microphone: every open in these tests is a fake.
                B.TxCaptureOpenForTest = _ => null!;
            }

            public void Delay(int samples)
            {
                B.TxConditioner.KeySource = B.TransmitKeySource;
                B.TxConditioner.NoiseReducer = (_, _, _) => { };
                B.TxConditioner.NoiseReducerAppliedLatencySamples = () => samples;
                B.TxConditioner.Process(new float[960], 960, 48000);
            }

            public void Dispose()
            {
                try { Set(B, "_Transmit", false); Set(B, "opusInputChannel", null); Set(B, "_remoteAudioWake", null); Set(B, "theRadio", null); B.Dispose(); }
                finally
                {
                    API.ProgramName = _program; API.IsGUI = _gui; Vita.VitaSocket.TraceSink = _sink;
                    FlexBase.TxAudioCallbacksPerSecond = _txRate;
                    TxCallbackRate.CurrentRate = _currentRate;
                }
            }
        }

        internal static float[] Filled(float v, int n = 960) { var a = new float[n]; Array.Fill(a, v); return a; }

        /// <summary>A reducer that delays by <paramref name="delayFrames"/> stereo frames, its line primed with 1s (the "earlier tail").</summary>
        internal static Action<float[], int, uint> DelayLine(int delayFrames)
        {
            var line = new Queue<float>();
            for (int i = 0; i < delayFrames * 2; i++) line.Enqueue(1f);
            return (b, n, _) => { for (int i = 0; i < n; i++) { line.Enqueue(b[i]); b[i] = line.Dequeue(); } };
        }

        internal static Radio UnconnectedRadio(bool mox, InterlockState state)
        {
            var radio = (Radio)RuntimeHelpers.GetUninitializedObject(typeof(Radio));
            Set(radio, "_connected", true); Set(radio, "_mox", mox); Set(radio, "_interlockState", state);
            return radio;
        }

        internal static readonly PropertyInfo InterlockProperty = typeof(Radio).GetProperty("InterlockState")!;

        /// <summary>Forward the radio's changes the way the app's property handler does: InterlockState to noteRadioInterlock, Mox to noteRadioMox.</summary>
        internal static PropertyChangedEventHandler Forward(FlexBase b, Radio radio) => (_, e) =>
        {
            if (e.PropertyName == "InterlockState") Call(b, "noteRadioInterlock", radio.InterlockState);
            else if (e.PropertyName == "Mox") Call(b, "noteRadioMox", radio.Mox);
        };

        private static TxOpenKindProxy Kind(string name) => new(Enum.Parse(typeof(FlexBase).GetNestedType("TxOpenKind", BindingFlags.NonPublic)!, name));
        internal readonly struct TxOpenKindProxy { public TxOpenKindProxy(object v) { Value = v; } public object Value { get; } }

        // ── Codex's five reproductions ──

        [Fact]
        public void Review_late_status_of_our_own_key_request_must_not_make_its_unkey_a_noop()
        {
            using var x=new Rig();
            var radio=(Radio)RuntimeHelpers.GetUninitializedObject(typeof(Radio));
            Set(radio,"_connected",true); Set(radio,"_mox",false); Set(x.B,"theRadio",radio); Set(x.B,"_Transmit",false);
            PropertyChangedEventHandler h=(_,e)=> { if(e.PropertyName=="Mox") Call(x.B,"noteRadioMox",radio.Mox); };
            radio.PropertyChanged+=h;
            try {
                // Our one actual MOX write: optimistic synchronous edge is marked ours.
                x.B.Transmit=true; var own=x.B.q.Dequeue(); ((FlexBase.FunctionDel)own.Item)();
                Assert.True(radio.Mox); Assert.Equal(0L, x.B.TxOverProvenanceNow.CountedKeyDowns);
                x.Delay(1440); x.B.UnkeyAfterNoiseDelay("our short over"); x.Ends[0].End(); var off=x.B.q.Dequeue();
                // Status still in flight when we requested transmit: receive first,
                // then the transmitter status confirming OUR xmit 1. No other client/write.
                var state=typeof(Radio).GetProperty("InterlockState")!;
                state.SetValue(radio,InterlockState.Receive);
                Assert.False(x.B.Transmit);
                state.SetValue(radio,InterlockState.Transmitting);
                Assert.True(x.B.Transmit);
                ((FlexBase.FunctionDel)off.Item)();
                Assert.False(radio.Mox,"The delayed statuses of our own MOX request were mistaken for an external over, withholding our only queued unkey.");
            }
            finally { radio.PropertyChanged-=h; Set(x.B,"theRadio",null); }
        }

        [Fact]
        public void Review_backstop_must_not_precede_the_tail_from_a_trusted_delayed_capture_block()
        {
            // Adapted: a real capture stream reports its block age on EVERY
            // callback, so the latency is known before the release; the
            // reproduction measured it only after, where nothing could budget it.
            using var x = new Rig(); x.Delay(1440); Set(x.B, "_txCallbackRateThisStream", 10);
            var c = x.B.TxConditioner; c.NoiseReducer = DelayLine(1440);
            var sent = new List<float>();
            var p = new TxFramePipeline { Conditioner = c.Process, KeySource = x.B.TransmitKeySource,
                Encode = b => { sent.AddRange(b); return new byte[4]; }, Handler = _ => x.B.NoteTxFrameHandedOn() };
            x.B.TxCaptureLatencyMsForTest = () => p.CaptureLatencyMs;
            // The callbacks before the release: 200 ms old for a 100 ms block, so 100 ms of device latency, believed.
            p.NoteCaptureLatency(TxCaptureClock.TrustedExcessLatencyMs(.200, 4800, 48000));
            Assert.Equal(100, p.CaptureLatencyMs);
            x.B.UnkeyAfterNoiseDelay("release 5 ms after callback, device latency 100 ms");
            var ticket = c.Drain!; long release = ticket.ReleaseTimestamp;
            // The backstop budgets the delivery of the block holding the release.
            var backstop = x.Ends[0];
            Assert.Equal(33 + 100 + 100 + 50, backstop.Due);
            // t=95 ms callback: block captured t=-105..-5, all before release.
            x.Now = 95;
            long callback = release + 95 * Stopwatch.Frequency / 1000;
            long first = TxCaptureClock.BlockStartTimestamp(callback, .200, 4800, 48000);
            Assert.Equal(callback - Stopwatch.Frequency / 5, first);
            for (int i=0; i<10; i++) p.Emit(Filled(3f), 960, 48000, null, first + i * Stopwatch.Frequency / 100);
            Assert.Equal(0, ticket.DrainedSamples);
            Assert.True(x.B.HeldUnkeyPending);
            // t=195 ms callback: block captured t=-5..+95 holds the last 5 ms of speech.
            x.Now = 195;
            long second = TxCaptureClock.BlockStartTimestamp(release + 195 * Stopwatch.Frequency / 1000, .200, 4800, 48000);
            for (int i = 0; i < 10; i++) p.Emit(Filled(3f), 960, 48000, null, second + i * Stopwatch.Frequency / 100);
            Assert.True(ticket.TailOut, "the block holding the release carried the tail out");
            Assert.Equal(2, x.Ends.Count);                   // the tail's end is scheduled
            Assert.True(x.Ends[1].Due < backstop.Due, "normal delivery ends the hold before the backstop");
            x.Now = x.Ends[1].Due; x.Ends[1].End();
            Assert.False(x.B.HeldUnkeyPending);
            // Every pre-release sample that entered went out before the unkey: 10 whole frames and 240 frames of the next.
            Assert.Equal(10 * 960 + 240 * 2, sent.Count(v => v == 3f));
            backstop.End();                                  // the backstop's later run does nothing
            Assert.Equal(1, x.B.q.Count);
        }

        [Fact]
        public void Review_abandoned_worker_must_not_open_a_second_rate_after_teardown_returns()
        {
            using var x = new Rig(); RepairTwoChannel(x);
            x.B.TxCaptureWorkerForTest = null;
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int opens=0; bool teardownReturned=false; bool secondAfterTeardown=false;
            x.B.TxCaptureOpenForTest = _ => {
                if (Interlocked.Increment(ref opens)==1) { entered.Set(); release.Wait(15000); return null!; }
                secondAfterTeardown = Volatile.Read(ref teardownReturned); return new Fake();
            };
            Call(x.B, "StartTxOpenJob", 50, Kind("Replace").Value, Get(x.B,"opusInputChannel"), 10, "test");
            try {
                Assert.True(entered.Wait(5000)); Call(x.B,"AbandonTxOpenJob","teardown");
                Call(x.B,"WaitForTxCaptureWorkers","teardown");
                Volatile.Write(ref teardownReturned,true);
                Assert.Equal(1,(int)Get(x.B,"_txCaptureWorkers")!); // positive control: timeout really left the old worker alive
            }
            finally { release.Set(); Call(x.B,"WaitForTxCaptureWorkers","test cleanup"); }
            Assert.False(secondAfterTeardown, "An abandoned old-run worker opened rate 10 after the teardown barrier returned; production can already terminate/reinitialize AudioAnchor.work here.");
            Assert.Equal(1, opens); // and it opened nothing at all after its abandonment
        }

        [Fact]
        public void Review_returned_replacement_is_published_when_generated_source_is_the_gate_s_next_start()
        {
            using var x = new Rig(); var dead=DeadMic(x); var fresh=new Fake();
            x.B.TxCaptureOpenForTest = _=>fresh;
            Call(x.B,"RecoverMicStream"); dead.Result=false; Start(x);
            Assert.True((bool)Get(x.B,"_micReopenPending")!);
            // The worker already returned. Arm the rig's real source and key:
            // these are the exact predicates selecting the generated-source branch.
            var file=(TxFilePlayer)Get(x.B,"txFilePlayer")!;
            file.Load(Enumerable.Repeat(.1f,48000).ToArray(),48000,"marker"); file.Start();
            Set(x.B,"_Transmit",true);
            Assert.True(x.B.Transmit); Assert.False(((TxInputSourceMux)typeof(FlexBase).GetProperty("TxInputSources",Any)!.GetValue(x.B)!).Idle);
            // The production gate takes its generated-source branch, not startOpusInputChannel.
            Assert.False((bool)Call(x.B,"startSelfClockedTxInput")!);
            var channel=(FlexBase.audioChannelData)Get(x.B,"opusInputChannel")!;
            Assert.Same(fresh,channel.PortAudioStream);
        }

        [Fact]
        public void Review_a_file_replacing_old_microphone_samples_after_release_must_use_its_own_time()
        {
            using var x = new Rig(); x.Delay(480);
            var file = new TxFilePlayer(); file.Load(Enumerable.Repeat(2f, 48000).ToArray(), 48000, "marker"); file.Start();
            for (int i=0;i<4;i++) file.Process(new float[960], 960, 48000);
            Assert.True(file.Engaged);
            var sent = new List<float>();
            var p = new TxFramePipeline { Source = file, Conditioner = x.B.TxConditioner.Process,
                KeySource = x.B.TransmitKeySource, Encode = b => { sent.AddRange(b); return new byte[4]; } };
            x.B.UnkeyAfterNoiseDelay("release while capture is being parked for file");
            long release = x.B.TxConditioner.Drain!.ReleaseTimestamp;
            // A capture callback still in flight uses its old microphone timestamp.
            // Its SOURCE runs now, after release, and replaces those samples with file audio.
            p.Emit(new float[960], 960, 48000, null, release - Stopwatch.Frequency / 10);
            Assert.True(file.PlayedFrames > 0);
            Assert.DoesNotContain(2f, sent);
        }

        // ── Rigs shared with the repair-two tests' shapes ──

        internal static FlexBase.audioChannelData RepairTwoChannel(Rig x, JJAudioStream? stream = null)
        {
            var c = (FlexBase.audioChannelData)RuntimeHelpers.GetUninitializedObject(typeof(FlexBase.audioChannelData));
            c.PortAudioStream = stream ?? new Fake(); c.Started = true;
            Set(x.B, "opusInputChannel", c); Set(x.B, "opusInputAvailable", true); Set(x.B, "_txTryChannel", c);
            Set(x.B, "_txStreamAlreadyJudged", true); Set(x.B, "_Transmit", false);
            return c;
        }

        internal static Fake DeadMic(Rig x)
        {
            var dead = new Fake();
            RepairTwoChannel(x, dead);
            x.B.TxCaptureWorkerForTest = a => a();
            return dead;
        }

        internal static bool Start(Rig x) => (bool)Call(x.B, "startOpusInputChannel")!;

        // ── Fix 1 (P1): a status our own key request explains never ends our over ──

        [Fact]
        public void The_provenance_reads_each_report_against_our_latest_request()
        {
            var p = new TxOverProvenance();
            // Nothing of ours outstanding: every report counts.
            Assert.Equal(TxOverProvenance.Reading.ForeignKeyDown, p.MoxReported(true, InterlockState.Transmitting));
            Assert.Equal(TxOverProvenance.Reading.OverEnded, p.MoxReported(false, InterlockState.Receive));
            Assert.Equal(1, p.CountedKeyDowns); Assert.Equal(1, p.CountedEnds);
            // Our key request: a receive is stale, the confirmation is ours.
            p.OwnKeyDownWritten();
            Assert.True(p.OwnKeyUnconfirmed);
            Assert.True(p.ReceiveReportIsStale(InterlockState.Receive));
            Assert.False(p.ReceiveReportIsStale(InterlockState.Transmitting));
            Assert.Equal(TxOverProvenance.Reading.Explained, p.MoxReported(false, InterlockState.Receive));
            Assert.Equal(TxOverProvenance.Reading.Explained, p.MoxReported(true, InterlockState.UnkeyRequested)); // an earlier over's stale end
            Assert.Equal(TxOverProvenance.Phase.KeyRequested, p.Now);
            p.InterlockReported(InterlockState.PTTRequested);                 // the confirmation, with no Mox change
            Assert.Equal(TxOverProvenance.Phase.OnAir, p.Now);
            Assert.Equal(TxOverProvenance.Reading.Explained, p.MoxReported(true, InterlockState.Transmitting));
            // The radio ends our confirmed over: counted.
            Assert.Equal(TxOverProvenance.Reading.OverEnded, p.MoxReported(false, InterlockState.Receive));
            Assert.Equal(2, p.CountedEnds);
            // Our unkey: its UnkeyRequested (FlexLib raises Mox true) and receive are ours.
            p.OwnKeyDownWritten(); p.InterlockReported(InterlockState.Transmitting); p.OwnUnkeyWritten();
            Assert.Equal(TxOverProvenance.Reading.Explained, p.MoxReported(true, InterlockState.UnkeyRequested));
            Assert.Equal(TxOverProvenance.Reading.Explained, p.MoxReported(false, InterlockState.Receive));
            Assert.Equal(TxOverProvenance.Phase.None, p.Now);
            Assert.Equal(2, p.CountedEnds); Assert.Equal(1, p.CountedKeyDowns);
        }

        [Fact]
        public void A_queued_unkey_is_withheld_only_after_a_confirmed_over_ended_and_a_foreign_one_began()
        {
            var p = new TxOverProvenance();
            p.OwnKeyDownWritten();
            var token = p.Capture();
            p.MoxReported(false, InterlockState.Receive);            // stale
            p.MoxReported(true, InterlockState.Transmitting);        // our confirmation
            Assert.False(p.OverEndedAndForeignBegunSince(token));
            p.MoxReported(false, InterlockState.Receive);            // the radio ended ours
            Assert.False(p.OverEndedAndForeignBegunSince(token));   // half the evidence
            p.MoxReported(true, InterlockState.Transmitting);        // a key-down nothing of ours explains
            Assert.True(p.OverEndedAndForeignBegunSince(token));
            Assert.False(p.OverEndedAndForeignBegunSince(p.Capture()));
        }

        [Fact]
        public void A_foreign_over_after_our_confirmed_over_is_still_not_ended_by_our_queued_unkey()
        {
            // Positive control for the P2 of rulings repair 2, now with a real own
            // write and its confirmation in front of it.
            using var x = new Rig(); x.Delay(1440);
            var radio = UnconnectedRadio(false, InterlockState.Ready); Set(x.B, "theRadio", radio); Set(x.B, "_Transmit", false);
            var h = Forward(x.B, radio); radio.PropertyChanged += h;
            try
            {
                x.B.Transmit = true; ((FlexBase.FunctionDel)x.B.q.Dequeue().Item)();
                InterlockProperty.SetValue(radio, InterlockState.PTTRequested);
                InterlockProperty.SetValue(radio, InterlockState.Transmitting);  // confirmed
                x.B.UnkeyAfterNoiseDelay("our over"); x.Ends[0].End(); var off = x.B.q.Dequeue();
                InterlockProperty.SetValue(radio, InterlockState.Receive);       // the radio ended ours
                InterlockProperty.SetValue(radio, InterlockState.PTTRequested);  // another station keys
                InterlockProperty.SetValue(radio, InterlockState.Transmitting);
                Assert.True(radio.Mox);
                ((FlexBase.FunctionDel)off.Item)();
                Assert.True(radio.Mox, "our old unkey ended an over JJ Flexible did not key");
            }
            finally { radio.PropertyChanged -= h; }
        }

        [Fact]
        public void A_stale_receive_report_does_not_end_the_hold_or_its_drain()
        {
            using var x = new Rig(); x.Delay(1440);
            var radio = UnconnectedRadio(false, InterlockState.Ready); Set(x.B, "theRadio", radio); Set(x.B, "_Transmit", false);
            var h = Forward(x.B, radio); radio.PropertyChanged += h;
            try
            {
                x.B.Transmit = true; ((FlexBase.FunctionDel)x.B.q.Dequeue().Item)();
                x.B.UnkeyAfterNoiseDelay("our short over");
                Assert.True(x.B.HeldUnkeyPending);
                InterlockProperty.SetValue(radio, InterlockState.Receive);       // stale: before our xmit 1 reached it
                Assert.True(x.B.HeldUnkeyPending, "a stale report ended our hold");
                Assert.True(x.B.TxConditioner.DrainInput);
                Assert.Equal(0, x.B.q.Count);                                    // and sent nothing
                // The chain's key stays up through the drain: no new microphone audio.
                Assert.True(x.B.TransmitKeySource());
                var b = Filled(2f); x.B.TxConditioner.Process(b, 960, 48000);
                Assert.True(x.B.TxConditioner.DrainInput);
                Assert.All(b, v => Assert.Equal(0f, v));                         // the microphone after the release: silence
                // Positive control: once the radio CONFIRMED our over, its receive does end the hold, unkeying now.
                InterlockProperty.SetValue(radio, InterlockState.Transmitting);
                InterlockProperty.SetValue(radio, InterlockState.Receive);
                Assert.False(x.B.HeldUnkeyPending);
                Assert.Equal(1, x.B.q.Count);
            }
            finally { radio.PropertyChanged -= h; }
        }

        [Fact]
        public void The_chain_s_key_holds_for_an_unconfirmed_request_only_while_a_drain_is_in_force()
        {
            using var x = new Rig();
            Set(x.B, "_Transmit", false);
            x.B.TxOverProvenanceNow.OwnKeyDownWritten();
            Assert.False(x.B.TransmitKeySource());                   // no drain: the ordinary key
            x.B.TxConditioner.BeginDrain(1440);
            Assert.True(x.B.TransmitKeySource());                    // draining, our request unconfirmed
            x.B.TxOverProvenanceNow.InterlockReported(InterlockState.Transmitting);
            Assert.False(x.B.TransmitKeySource());                   // confirmed: the radio's own reports govern
        }

        [Fact]
        public void Our_unkey_is_sent_as_its_own_command_when_a_stale_report_already_cleared_the_cache()
        {
            using var x = new Rig(); x.Delay(1440);
            var radio = UnconnectedRadio(false, InterlockState.Ready); Set(x.B, "theRadio", radio); Set(x.B, "_Transmit", false);
            var h = Forward(x.B, radio); radio.PropertyChanged += h;
            int raw = 0; x.B.OwnUnkeyCommandForTest = r => { Assert.Same(radio, r); raw++; };
            try
            {
                x.B.Transmit = true; ((FlexBase.FunctionDel)x.B.q.Dequeue().Item)();
                x.B.UnkeyAfterNoiseDelay("our short over"); x.Ends[0].End(); var off = x.B.q.Dequeue();
                InterlockProperty.SetValue(radio, InterlockState.Receive);       // stale; FlexLib's Mox cache goes false
                Assert.False(radio.Mox);
                ((FlexBase.FunctionDel)off.Item)();                              // the queued unkey runs BEFORE the confirmation
                Assert.Equal(1, raw);                                            // xmit 0 still went to the radio
                Assert.Equal(TxOverProvenance.Phase.UnkeyRequested, x.B.TxOverProvenanceNow.Now);
            }
            finally { radio.PropertyChanged -= h; }
        }

        [Fact]
        public void A_direct_unkey_is_sent_as_its_own_command_after_a_stale_report_too()
        {
            using var x = new Rig();
            var radio = UnconnectedRadio(false, InterlockState.Ready); Set(x.B, "theRadio", radio); Set(x.B, "_Transmit", false);
            var h = Forward(x.B, radio); radio.PropertyChanged += h;
            int raw = 0; x.B.OwnUnkeyCommandForTest = _ => raw++;
            try
            {
                x.B.Transmit = true; ((FlexBase.FunctionDel)x.B.q.Dequeue().Item)();
                InterlockProperty.SetValue(radio, InterlockState.Receive);       // stale
                x.B.Transmit = false; ((FlexBase.FunctionDel)x.B.q.Dequeue().Item)(); // a safety unkey, say
                Assert.Equal(1, raw);
            }
            finally { radio.PropertyChanged -= h; }
        }

        [Fact]
        public void Nothing_extra_is_sent_when_the_radio_is_off_and_nothing_of_ours_is_outstanding()
        {
            using var x = new Rig();
            var radio = UnconnectedRadio(false, InterlockState.Receive); Set(x.B, "theRadio", radio); Set(x.B, "_Transmit", false);
            int raw = 0; x.B.OwnUnkeyCommandForTest = _ => raw++;
            x.B.Transmit = false; ((FlexBase.FunctionDel)x.B.q.Dequeue().Item)();
            Assert.Equal(0, raw);
            // And a confirmed over the radio already ended: off, nothing outstanding.
            x.B.TxOverProvenanceNow.OwnKeyDownWritten(); x.B.TxOverProvenanceNow.InterlockReported(InterlockState.Transmitting);
            x.B.TxOverProvenanceNow.MoxReported(false, InterlockState.Receive);
            x.B.Transmit = false; ((FlexBase.FunctionDel)x.B.q.Dequeue().Item)();
            Assert.Equal(0, raw);
        }

        [Fact]
        public void The_property_handler_routes_the_interlock_and_mox_through_the_driven_methods()
        {
            string text = FlexBasePartials.JoinedText();
            int at = text.IndexOf("case \"InterlockState\":", StringComparison.Ordinal);
            string body = text.Substring(at, text.IndexOf("break;", at, StringComparison.Ordinal) - at);
            Assert.Contains("noteRadioInterlock(r.InterlockState);", body);
            Assert.DoesNotContain("CancelHeldUnkey(", body);   // only through the method, which reads provenance
            int mox = text.IndexOf("case \"Mox\":", StringComparison.Ordinal);
            Assert.Contains("noteRadioMox(r.Mox);", text.Substring(mox, text.IndexOf("break;", mox, StringComparison.Ordinal) - mox));
            int setter = text.IndexOf("public bool Transmit\n", StringComparison.Ordinal);
            if (setter < 0) setter = text.IndexOf("public bool Transmit\r\n", StringComparison.Ordinal);
            string set = text.Substring(setter, text.IndexOf("public bool TxTune", setter, StringComparison.Ordinal) - setter);
            Assert.Contains("if (!value) { WriteOwnUnkey(theRadio, \"the unkey\"); return; }", set);
            Assert.Contains("BeginOwnMoxWrite(true)", set);
        }

        // ── Fix 2 (P1): the backstop budgets the capture latency the clock believes ──

        [Fact]
        public void The_clock_reports_the_latency_it_believes_and_nothing_it_does_not()
        {
            Assert.Equal(100, TxCaptureClock.TrustedExcessLatencyMs(.200, 4800, 48000));
            Assert.Equal(200, TxCaptureClock.TrustedExcessLatencyMs(.300, 4800, 48000));
            Assert.Equal(1, TxCaptureClock.TrustedExcessLatencyMs(.1005, 4800, 48000)); // rounded up
            Assert.Equal(0, TxCaptureClock.TrustedExcessLatencyMs(.100, 4800, 48000));
            foreach (double age in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, 0.050, 0.301 })
                Assert.Equal(0, TxCaptureClock.TrustedExcessLatencyMs(age, 4800, 48000));
        }

        [Fact]
        public void The_pipeline_keeps_the_largest_believed_latency_of_its_stream()
        {
            var p = new TxFramePipeline();
            Assert.Equal(0, p.CaptureLatencyMs);
            p.NoteCaptureLatency(40); p.NoteCaptureLatency(10); p.NoteCaptureLatency(0);
            Assert.Equal(40, p.CaptureLatencyMs);
            p.NoteCaptureLatency(999);
            Assert.Equal(TxCaptureClock.MaxTrustedInputLatencyMs, p.CaptureLatencyMs);
        }

        [Theory]
        // delay ms, callbacks a second, latency ms, backstop ms
        [InlineData(30, 10, 0, 33 + 100 + 0 + 50)]
        [InlineData(30, 10, 100, 33 + 100 + 100 + 50)]
        [InlineData(30, 10, 200, 33 + 100 + 200 + 50)]
        [InlineData(30, 10, 500, 33 + 100 + 200 + 50)]
        [InlineData(180, 10, 200, 198 + 100 + 200 + 50)]
        [InlineData(30, 50, 0, 33 + 20 + 0 + 50)]
        [InlineData(30, 50, 100, 33 + 20 + 100 + 50)]
        [InlineData(180, 50, 200, 198 + 20 + 200 + 50)]
        [InlineData(180, 50, -5, 198 + 20 + 0 + 50)]
        public void The_backstop_is_the_hold_plus_a_period_plus_the_believed_latency_plus_the_allowance(double delay, int rate, int latency, int expected)
        {
            Assert.Equal(expected, FlexBase.HeldUnkeyBackstopMs(delay, rate, latency));
        }

        [Fact]
        public void The_armed_backstop_reads_the_running_stream_s_latency()
        {
            using var x = new Rig(); x.Delay(1440); Set(x.B, "_txCallbackRateThisStream", 50);
            x.B.TxCaptureLatencyMsForTest = () => 120;
            x.B.UnkeyAfterNoiseDelay("latency");
            Assert.Equal(33 + 20 + 120 + 50, x.Ends[0].Due);
            Assert.Equal(33 + 20 + 120 + 50, x.B.LastHeldUnkeyBackstopMs);
        }

        [Fact]
        public void The_capture_callback_notes_the_latency_it_believed()
        {
            string audio = File.ReadAllText(Path.Combine(RepoRoot(), "JJPortaudio", "JJPortaudio", "Audio.cs"));
            int at = audio.IndexOf("private static PortAudio.PaStreamCallbackResult inputCallback(", StringComparison.Ordinal);
            int end = audio.IndexOf("private static PortAudio.PaStreamCallbackResult outputCallback(", at, StringComparison.Ordinal);
            string body = audio.Substring(at, end - at);
            Assert.Contains("data.TxPipeline.NoteCaptureLatency(TxCaptureClock.TrustedExcessLatencyMs(blockAge, frameCount, data.SampleRate));", body);
            Assert.Contains("double blockAge = timeInfo.currentTime - timeInfo.inputBufferAdcTime;", body);
            Assert.Contains("blockAge, frameCount, data.SampleRate);", body);
        }

        // ── Fix 3 (P2): no worker outlives its run ──

        [Fact]
        public void An_operation_runs_only_on_its_own_live_server_and_only_while_wanted()
        {
            long live = Audio.ServerGeneration;
            int ran = 0;
            Assert.True(Audio.RunOnServer(live, () => true, () => ran++));
            Assert.False(Audio.RunOnServer(live - 1, () => true, () => ran++));
            Assert.False(Audio.RunOnServer(live, () => false, () => ran++));
            Assert.Equal(1, ran);
        }

        [Fact]
        public void The_server_s_end_waits_for_the_operation_in_progress_and_nothing_begins_after_it()
        {
            long live = Audio.ServerGeneration;
            using var inside = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
            bool operationDone = false, doneWhenEnded = false;
            var worker = new Thread(() => Audio.RunOnServer(live, null, () => { inside.Set(); finish.Wait(5000); Volatile.Write(ref operationDone, true); })) { IsBackground = true };
            worker.Start();
            try
            {
                Assert.True(inside.Wait(5000));
                var ender = new Thread(() => { Audio.MoveServerGenerationForTest(); doneWhenEnded = Volatile.Read(ref operationDone); }) { IsBackground = true };
                ender.Start();
                Assert.False(ender.Join(150), "the server ended while an operation was still in progress on it");
                finish.Set();
                Assert.True(ender.Join(5000));
                Assert.True(doneWhenEnded);
                Assert.False(Audio.RunOnServer(live, null, () => throw new InvalidOperationException("ran on an ended server")));
            }
            finally { finish.Set(); worker.Join(5000); }
        }

        [Fact]
        public void Initialize_and_Terminate_hold_the_server_s_gate_and_move_its_generation()
        {
            string audio = File.ReadAllText(Path.Combine(RepoRoot(), "JJPortaudio", "JJPortaudio", "Audio.cs"));
            foreach (var (method, call) in new[] { ("public static void Initialize(", "AudioAnchor.Init(inDev, outDev);"), ("public static void Terminate()", "AudioAnchor.Term();") })
            {
                int at = audio.IndexOf(method, StringComparison.Ordinal);
                string body = audio.Substring(at, audio.IndexOf("\n        }", at, StringComparison.Ordinal) - at);
                Assert.Contains("lock (s_serverGate)", body);
                Assert.Contains("Interlocked.Increment(ref s_serverGeneration);", body);
                Assert.Contains(call, body);
            }
        }

        [Fact]
        public void A_job_whose_server_ended_before_it_began_opens_nothing()
        {
            using var x = new Rig(); DeadMic(x);
            var jobs = new List<Action>(); x.B.TxCaptureWorkerForTest = a => jobs.Add(a);
            int opens = 0; x.B.TxCaptureOpenForTest = _ => { opens++; return new Fake(); };
            Call(x.B, "StartTxOpenJob", 50, Kind("Replace").Value, Get(x.B, "opusInputChannel"), 10, "test");
            Audio.MoveServerGenerationForTest();               // the run's server ended (Terminate)
            jobs[0]();
            Assert.Equal(0, opens);
            Assert.Null(Get(x.B, "_txOpenJob"));
        }

        [Fact]
        public void An_open_in_progress_when_its_job_is_abandoned_is_closed_on_its_own_server_before_the_end()
        {
            using var x = new Rig(); DeadMic(x);
            var jobs = new List<Action>(); x.B.TxCaptureWorkerForTest = a => jobs.Add(a);
            var fresh = new Fake(); long liveAtClose = -1;
            x.B.TxCaptureOpenForTest = _ => { Call(x.B, "AbandonTxOpenJob", "teardown during the open"); return fresh; };
            Call(x.B, "StartTxOpenJob", 50, Kind("Replace").Value, Get(x.B, "opusInputChannel"), 10, "test");
            long server = Audio.ServerGeneration;
            jobs[0]();
            liveAtClose = Audio.ServerGeneration;
            Assert.Equal(1, fresh.Closes);
            Assert.Equal(server, liveAtClose);
        }

        [Theory]
        [InlineData("stopRemoteAudio")]
        [InlineData("Disconnecting")]
        public void A_reopen_never_begins_into_a_closing_session(string flag)
        {
            using var x = new Rig(); DeadMic(x);
            var jobs = new List<Action>(); x.B.TxCaptureWorkerForTest = a => jobs.Add(a);
            int opens = 0; x.B.TxCaptureOpenForTest = _ => { opens++; return new Fake(); };
            Call(x.B, "StartTxOpenJob", 50, Kind("Replace").Value, Get(x.B, "opusInputChannel"), 10, "test");
            Set(x.B, flag, true);
            try { jobs[0](); }
            finally { Set(x.B, flag, false); }
            Assert.Equal(0, opens);
        }

        [Fact]
        public void The_second_rate_is_not_begun_once_the_run_has_ended()
        {
            using var x = new Rig(); DeadMic(x);
            using var oldWake = new RemoteAudioWake(); using var newWake = new RemoteAudioWake();
            Set(x.B, "_remoteAudioWake", oldWake);
            var jobs = new List<Action>(); x.B.TxCaptureWorkerForTest = a => jobs.Add(a);
            var rates = new List<int>();
            x.B.TxCaptureOpenForTest = r => { rates.Add(r); Set(x.B, "_remoteAudioWake", newWake); return null!; }; // the run ends as the first rate fails
            Call(x.B, "StartTxOpenJob", 50, Kind("Replace").Value, Get(x.B, "opusInputChannel"), 10, "test");
            jobs[0]();
            Assert.Equal(new[] { 50 }, rates);
        }

        [Fact]
        public void A_close_whose_server_has_ended_posts_nothing()
        {
            using var x = new Rig();
            var jobs = new List<Action>(); x.B.TxCaptureWorkerForTest = a => jobs.Add(a);
            var stream = new Fake();
            Call(x.B, "CloseTxStreamOffLoop", stream, (long?)Audio.ServerGeneration);
            Audio.MoveServerGenerationForTest();
            jobs[0]();
            Assert.Equal(0, stream.Closes);
            // Positive control: on its own live server it closes.
            Call(x.B, "CloseTxStreamOffLoop", stream, (long?)Audio.ServerGeneration);
            jobs[1]();
            Assert.Equal(1, stream.Closes);
        }

        // ── Fix 4 (P2): returned work is published whatever producer the gate selects ──

        [Fact]
        public void Every_loop_pass_publishes_a_returned_reopen_before_the_gate_chooses_a_producer()
        {
            string remote = FlexBasePartials.JoinedText();
            int loop = remote.IndexOf("while (!stopRemoteAudio)", StringComparison.Ordinal);
            int take = remote.IndexOf("TakeReturnedTxReplacement();", loop, StringComparison.Ordinal);
            int disconnecting = remote.IndexOf("if (Disconnecting)", loop, StringComparison.Ordinal);
            int gate = remote.IndexOf("if (!TxInputSources.Idle)", loop, StringComparison.Ordinal);
            Assert.True(disconnecting < take && take < gate, "the loop publishes after the disconnecting check and before the gate");
            int self = remote.IndexOf("private bool startSelfClockedTxInput()", StringComparison.Ordinal);
            Assert.Contains("TakeReturnedTxReplacement();", remote.Substring(self, remote.IndexOf("stream.SelfClockedTxRunning", self, StringComparison.Ordinal) - self));
        }

        [Fact]
        public void A_returned_try_still_waits_for_the_microphone_s_start()
        {
            // A try or fall back is a SWAP of the running capture, admitted only
            // while the microphone is the producer: the self-clock's start does
            // not take it (and so does not close it on a refused swap).
            using var x = new Rig(); var channel = RepairTwoChannel(x);
            var jobs = new List<Action>(); x.B.TxCaptureWorkerForTest = a => jobs.Add(a);
            var fresh = new Fake(); x.B.TxCaptureOpenForTest = _ => fresh;
            Call(x.B, "StartTxOpenJob", 50, Kind("Try").Value, channel, 0, "");
            jobs[0]();
            Call(x.B, "startSelfClockedTxInput");
            Assert.NotNull(Get(x.B, "_txOpenJob"));
            Assert.Equal(0, fresh.Closes);
        }

        [Fact]
        public void A_returned_reopen_published_by_the_self_clock_s_start_is_started_by_the_microphone_s_later()
        {
            using var x = new Rig(); var dead = DeadMic(x); var fresh = new Fake();
            x.B.TxCaptureOpenForTest = _ => fresh;
            Call(x.B, "RecoverMicStream"); dead.Result = false; Start(x);
            Call(x.B, "startSelfClockedTxInput");               // a file pass publishes it
            Assert.False((bool)Get(x.B, "_micReopenPending")!);
            Assert.Equal(0, fresh.Starts);                       // and starts no capture: one producer
            Assert.True(Start(x));                               // the microphone's pass starts it, as the restart
            Assert.Equal(1, fresh.Starts);
            Assert.False((bool)Get(x.B, "_micRestartPending")!);
        }

        // ── Fix 5 (P2): injected samples carry their own time ──

        private static (Rig X, TxFramePipeline P, List<float> Sent, long Release) InjectionRig(ITxInputSource? source)
        {
            var x = new Rig(); x.Delay(480);
            var sent = new List<float>();
            var p = new TxFramePipeline { Source = source, Conditioner = x.B.TxConditioner.Process,
                KeySource = x.B.TransmitKeySource, Encode = b => { sent.AddRange(b); return new byte[4]; } };
            x.B.UnkeyAfterNoiseDelay("release");
            return (x, p, sent, x.B.TxConditioner.Drain!.ReleaseTimestamp);
        }

        [Fact]
        public void A_ramping_file_s_samples_after_the_release_are_taken_as_captured_now()
        {
            var file = new TxFilePlayer(); file.Load(Enumerable.Repeat(2f, 48000).ToArray(), 48000, "marker"); file.Start();
            for (int i = 0; i < 4; i++) file.Process(new float[960], 960, 48000);
            file.Stop();                                          // released: ramping out, no longer engaged, not idle
            Assert.False(file.Engaged); Assert.False(file.Idle);
            var (x, p, sent, release) = InjectionRig(file);
            using (x)
            {
                p.Emit(Filled(3f), 960, 48000, null, release - Stopwatch.Frequency / 10);
                Assert.All(sent, v => Assert.Equal(0f, v));        // neither the file nor the old microphone under it
            }
        }

        [Fact]
        public void A_tone_engaged_after_the_release_is_taken_as_captured_now()
        {
            var tone = new TxToneGenerator(); tone.Start();
            for (int i = 0; i < 4; i++) tone.Process(new float[960], 960, 48000);
            Assert.True(tone.Engaged && tone.BypassesConditioning);
            var (x, p, sent, release) = InjectionRig(tone);
            using (x)
            {
                p.Emit(new float[960], 960, 48000, null, release - Stopwatch.Frequency / 10);
                Assert.All(sent, v => Assert.Equal(0f, v));
            }
        }

        [Fact]
        public void The_microphone_with_an_idle_source_keeps_its_capture_time()
        {
            var file = new TxFilePlayer();                         // never started: idle
            Assert.True(file.Idle);
            var (x, p, sent, release) = InjectionRig(file);
            using (x)
            {
                x.B.TxConditioner.NoiseReducer = DelayLine(480);
                p.Emit(Filled(3f), 960, 48000, null, release - 20 * Stopwatch.Frequency / 1000); // wholly before the release
                p.Emit(Filled(2f), 960, 48000, null, release + Stopwatch.Frequency / 1000);      // after it
                Assert.Equal(960, sent.Count(v => v == 3f));       // the pre-release frame went through
                Assert.DoesNotContain(2f, sent);
            }
        }

        [Fact]
        public void The_self_clock_s_untimed_frames_are_silenced_whole()
        {
            var (x, p, sent, _) = InjectionRig(null);
            using (x)
            {
                x.B.TxConditioner.NoiseReducer = DelayLine(480);
                p.Emit(Filled(2f), 960, 48000);                    // no capture time: captured now
                p.Emit(Filled(2f), 960, 48000);
                Assert.DoesNotContain(2f, sent);
            }
        }

        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
