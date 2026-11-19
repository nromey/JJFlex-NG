using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJPortaudio;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track H2, the join with the receive-audio track and rulings
    /// repair 2: Codex's five findings against 661dc63f, each with its
    /// reproduction adopted from
    /// <c>2026-11-18-codex-h2-rulings-recheck-results\CodexH2RepairReviewTests.cs</c>,
    /// and the joins from his list. Adjusted only where the fix needs it to
    /// express what was already the scenario: the capture-phase reproduction
    /// now tells the pipeline WHEN each frame was captured (it had no other way
    /// to say which frames came before the release), and the rescue
    /// reproduction queues the worker instead of running it inline (an inline
    /// worker runs on the caller's thread by construction). Windowless: an
    /// unconnected FlexBase whose command queue collects writes, conditioner
    /// buffers of numbers, and fake streams that open no device. No test here
    /// may reach the real microphone open: every rig sets
    /// <see cref="FlexBase.TxCaptureOpenForTest"/>.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TxRulingsRepairTwoTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static void Set(object b, string name, object? value) => b.GetType().GetField(name, Any)!.SetValue(b, value);
        private static object? Get(object b, string name) => b.GetType().GetField(name, Any)!.GetValue(b);
        private static object? Call(FlexBase b, string method, params object?[] args) =>
            typeof(FlexBase).GetMethod(method, Any)!.Invoke(b, args);
        private static string? Report(FlexBase b) =>
            (string?)typeof(FlexBase).GetProperty("LastStreamRecoveryReport", Any)!.GetValue(b);

        /// <summary>A capture stream that opens no device: it counts its stops, starts and closes.</summary>
        private sealed class Fake : JJAudioStream
        {
            public int Stops, Starts, Closes;
            public bool Result = true;
            public override bool StopAudio() { Stops++; return true; }
            public override bool StartAudio() { Starts++; return Result; }
            public override void Close() { Closes++; }
        }

        private sealed class Rig : IDisposable
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
                try { Set(B, "_Transmit", false); Set(B, "opusInputChannel", null); Set(B, "_remoteAudioWake", null); B.Dispose(); }
                finally
                {
                    API.ProgramName = _program; API.IsGUI = _gui; Vita.VitaSocket.TraceSink = _sink;
                    FlexBase.TxAudioCallbacksPerSecond = _txRate;
                    TxCallbackRate.CurrentRate = _currentRate;
                }
            }
        }

        private static float[] Filled(float v, int n = 960) { var a = new float[n]; Array.Fill(a, v); return a; }

        /// <summary>A reducer that delays by <paramref name="delayFrames"/> stereo frames, its line primed with 1s (the "earlier tail").</summary>
        private static Action<float[], int, uint> DelayLine(int delayFrames)
        {
            var line = new Queue<float>();
            for (int i = 0; i < delayFrames * 2; i++) line.Enqueue(1f);
            return (b, n, _) => { for (int i = 0; i < n; i++) { line.Enqueue(b[i]); b[i] = line.Dequeue(); } };
        }

        // ── P1: speech captured before the release is kept ──

        [Theory]
        [InlineData(10, 50)]
        [InlineData(50, 10)]
        public void Speech_captured_before_release_but_waiting_in_the_next_capture_block_is_preserved(int rate, int releaseAtMs)
        {
            using var x = new Rig(); x.Delay(1440); Set(x.B, "_txCallbackRateThisStream", rate);
            var c = x.B.TxConditioner;
            c.NoiseReducer = DelayLine(1440);
            var sent = new List<float>();
            var p = new TxFramePipeline
            {
                Conditioner = c.Process, KeySource = x.B.TransmitKeySource,
                Encode = b => { sent.AddRange(b); return new byte[4]; }, Handler = _ => x.B.NoteTxFrameHandedOn(),
            };
            // Last callback ended at t=0. At t=releaseAtMs the device has already
            // captured that many milliseconds, but won't deliver its block until t=period.
            x.B.UnkeyAfterNoiseDelay("release between capture callbacks");
            long release = c.Drain!.ReleaseTimestamp;
            int period = 1000 / rate;
            for (int t = 0; t < period + 30; t += 10)
            {
                // The adaptation: each frame says when it was captured, relative to the release.
                long captured = release + (long)(t - releaseAtMs) * Stopwatch.Frequency / 1000;
                p.Emit(Filled(t < releaseAtMs ? 3f : 2f), 960, 48000, null, captured);
            }
            Assert.Contains(1f, sent);        // old reducer tail survived, the repair's positive control
            Assert.DoesNotContain(2f, sent);  // after-release input is suppressed
            Assert.Contains(3f, sent);        // the pre-release part of the next capture block survives too
        }

        [Fact]
        public void A_frame_straddling_the_release_keeps_exactly_the_samples_captured_before_it()
        {
            var ticket = new TxAudioConditioner.DrainTicket(1440, 1_000_000_000);
            var buf = Filled(3f);
            // 4 ms before the release at 48 kHz: 192 frames kept, 288 silenced.
            long captured = 1_000_000_000 - 4 * Stopwatch.Frequency / 1000;
            int silenced = (int)typeof(TxAudioConditioner).GetMethod("SilenceAfterRelease", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { ticket, buf, 960, 48000u, captured })!;
            Assert.Equal(288, silenced);
            Assert.All(buf.Take(384), v => Assert.Equal(3f, v));
            Assert.All(buf.Skip(384), v => Assert.Equal(0f, v));
        }

        [Fact]
        public void The_drain_counts_only_the_silence_that_went_in()
        {
            using var x = new Rig(); x.Delay(1440);
            var c = x.B.TxConditioner;
            c.NoiseReducer = DelayLine(1440);
            var p = new TxFramePipeline { Conditioner = c.Process, KeySource = x.B.TransmitKeySource, Encode = _ => new byte[4] };
            x.B.UnkeyAfterNoiseDelay("count");
            long release = c.Drain!.ReleaseTimestamp;
            // A frame wholly captured before the release counts nothing; one half before counts its half.
            p.Emit(Filled(3f), 960, 48000, null, release - 20 * Stopwatch.Frequency / 1000);
            Assert.Equal(0, c.Drain!.DrainedSamples);
            p.Emit(Filled(3f), 960, 48000, null, release - 5 * Stopwatch.Frequency / 1000);
            Assert.Equal(240, c.Drain!.DrainedSamples);
            // Unknown capture time counts as captured now: the whole frame is silence.
            p.Emit(Filled(2f), 960, 48000);
            Assert.Equal(720, c.Drain!.DrainedSamples);
        }

        [Fact]
        public void The_capture_clock_uses_the_reported_age_and_falls_back_toward_silence()
        {
            long f = Stopwatch.Frequency;
            long callback = 100 * f;
            // 100 ms block, reported 130 ms old: captured 130 ms before the callback.
            Assert.Equal(callback - (long)(0.130 * f), TxCaptureClock.BlockStartTimestamp(callback, 0.130, 4800, 48000));
            // Not reported, impossible (younger than the block) or unbelievable: the block ends at the callback.
            foreach (double age in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, 0.050, 0.100 + 0.001 * (TxCaptureClock.MaxTrustedInputLatencyMs + 1) })
                Assert.Equal(callback - 100 * f / 1000, TxCaptureClock.BlockStartTimestamp(callback, age, 4800, 48000));
            Assert.Equal(10 * f / 1000, TxCaptureClock.FrameOffsetTicks(480, 48000));
            Assert.Equal(0, TxCaptureClock.FrameOffsetTicks(480, 0));
        }

        [Fact]
        public void The_capture_callback_stamps_every_frame_it_emits()
        {
            string audio = File.ReadAllText(Path.Combine(RepoRoot(), "JJPortaudio", "JJPortaudio", "Audio.cs"));
            int at = audio.IndexOf("private static PortAudio.PaStreamCallbackResult inputCallback(", StringComparison.Ordinal);
            int end = audio.IndexOf("private static PortAudio.PaStreamCallbackResult outputCallback(", at, StringComparison.Ordinal);
            string body = audio.Substring(at, end - at);
            Assert.Contains("TxCaptureClock.BlockStartTimestamp(Stopwatch.GetTimestamp(),", body);
            Assert.Contains("timeInfo.currentTime - timeInfo.inputBufferAdcTime", body);
            Assert.Contains("data.TxPipeline.Emit(buf, (int)data.OpusFrameSZ, data.SampleRate, data.IsActive, frameCaptured)", body);
        }

        // ── P1: an older key reading never clears a newer drain (Codex's two-thread reproduction, verbatim) ──

        [Fact]
        public void An_old_unkeyed_buffer_must_not_clear_a_newly_armed_hold()
        {
            using var x = new Rig(); x.Delay(1440); var c = x.B.TxConditioner;
            Set(x.B, "_Transmit", false);
            using var read = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
            c.KeySource = () => { bool snapshot = x.B.TransmitKeyedOrInterlock; read.Set(); resume.Wait(5000); return snapshot; };
            var b = Filled(2f);
            var audio = new Thread(() => c.Process(b, 960, 48000)) { IsBackground = true }; audio.Start();
            try { Assert.True(read.Wait(5000)); Set(x.B, "_Transmit", true); x.B.UnkeyAfterNoiseDelay("review"); Assert.True(c.DrainInput); }
            finally { resume.Set(); Assert.True(audio.Join(5000)); }
            c.KeySource = x.B.TransmitKeySource; var next = Filled(2f); c.Process(next, 960, 48000);
            Assert.True(x.B.HeldUnkeyPending);
            Assert.True(c.DrainInput, "The older key=false buffer cleared the new drain; next post-release sample=" + next[0]);
        }

        [Fact]
        public void An_old_unkeyed_buffer_that_saw_an_older_drain_must_not_clear_the_newer_one()
        {
            // The same race with a drain already in force when the buffer looked:
            // the buffer may end THAT drain, never the one armed while it waited.
            using var x = new Rig(); x.Delay(1440); var c = x.B.TxConditioner;
            x.B.UnkeyAfterNoiseDelay("first over");
            var first = c.Drain;
            Assert.NotNull(first);
            Set(x.B, "_Transmit", false);
            using var read = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
            c.KeySource = () => { bool snapshot = x.B.TransmitKeyedOrInterlock; read.Set(); resume.Wait(5000); return snapshot; };
            var audio = new Thread(() => c.Process(Filled(2f), 960, 48000)) { IsBackground = true }; audio.Start();
            TxAudioConditioner.DrainTicket? second;
            try
            {
                Assert.True(read.Wait(5000));
                Set(x.B, "_Transmit", true); x.B.Transmit = true;     // a new over keys (it ends the first drain)
                x.B.UnkeyAfterNoiseDelay("second over");               // and is released
                second = c.Drain;
                Assert.NotNull(second); Assert.NotSame(first, second);
            }
            finally { resume.Set(); Assert.True(audio.Join(5000)); }
            Assert.Same(second, c.Drain);
        }

        [Fact]
        public void An_unkeyed_buffer_still_ends_the_drain_it_saw()
        {
            using var x = new Rig(); x.Delay(1440); var c = x.B.TxConditioner;
            x.B.UnkeyAfterNoiseDelay("positive control");
            Assert.True(c.DrainInput);
            Set(x.B, "_Transmit", false);
            var b = Filled(2f); c.Process(b, 960, 48000);
            Assert.False(c.DrainInput);
            Assert.All(b, v => Assert.Equal(2f, v)); // receive: the microphone runs as normal
        }

        // ── P1: a conditioner fault during the hold never sends new microphone audio (verbatim) ──

        [Fact]
        public void A_conditioner_fault_during_the_hold_must_not_send_post_release_microphone()
        {
            using var x = new Rig(); x.Delay(1440);
            var c = x.B.TxConditioner; bool fail = false;
            c.NoiseReducer = (_, _, _) => { if (fail) throw new InvalidOperationException("review reducer failure"); };
            var sent = new List<float>();
            var p = new TxFramePipeline { Conditioner = c.Process, KeySource = x.B.TransmitKeySource, Encode = b => { sent.Add(b[0]); return new byte[4]; }, Handler = _ => x.B.NoteTxFrameHandedOn() };
            x.B.UnkeyAfterNoiseDelay("review"); fail = true;
            for (int i = 0; i < 4; i++) { var b = Filled(2f); p.Emit(b, b.Length, 48000); }
            Assert.Equal(1, p.StageFailures); Assert.True(x.B.HeldUnkeyPending); Assert.True(c.DrainInput);
            Assert.DoesNotContain(2f, sent);
        }

        [Fact]
        public void An_ordinary_conditioner_fault_still_sends_the_microphone_unprocessed()
        {
            // The recovery contract for ordinary faults is unchanged: no drain, the stream lives, the audio goes on.
            using var x = new Rig(); x.Delay(1440);
            var c = x.B.TxConditioner;
            c.NoiseReducer = (_, _, _) => throw new InvalidOperationException("ordinary fault");
            var sent = new List<float>();
            var p = new TxFramePipeline { Conditioner = c.Process, KeySource = x.B.TransmitKeySource, Encode = b => { sent.Add(b[0]); return new byte[4]; } };
            for (int i = 0; i < 3; i++) Assert.True(p.Emit(Filled(2f), 960, 48000));
            Assert.Equal(1, p.StageFailures);
            Assert.Equal(new[] { 2f, 2f, 2f }, sent);
        }

        // ── P2: a queued held unkey validates the over (verbatim, plus controls) ──

        [Fact]
        public void A_queued_old_unkey_must_not_end_a_new_externally_reported_over()
        {
            using var x = new Rig(); x.Delay(1440);
            var radio = (Radio)RuntimeHelpers.GetUninitializedObject(typeof(Radio)); Set(radio, "_connected", true); Set(radio, "_mox", true); Set(x.B, "theRadio", radio);
            try
            {
                x.B.UnkeyAfterNoiseDelay("old over"); x.Ends[0].End(); var oldOff = x.B.q.Dequeue(); Assert.False(x.B.HeldUnkeyPending);
                Set(radio, "_mox", false); Call(x.B, "noteRadioMox", false);
                Set(radio, "_mox", true); Call(x.B, "noteRadioMox", true);
                Assert.True(radio.Mox); Assert.True(x.B.Transmit); // positive control: a new over really began
                ((FlexBase.FunctionDel)oldOff.Item)();
                Assert.True(radio.Mox, "The old queued unkey reached into the new externally reported over on the same connection.");
            }
            finally { Set(x.B, "theRadio", null); }
        }

        [Fact]
        public void A_queued_unkey_still_unkeys_when_the_radio_only_reported_a_key_down()
        {
            // An external key-down with no external unkey after the queueing is not a new over:
            // the unkey is never withheld on half the evidence.
            using var x = new Rig(); x.Delay(1440);
            var radio = (Radio)RuntimeHelpers.GetUninitializedObject(typeof(Radio)); Set(radio, "_connected", true); Set(radio, "_mox", true); Set(x.B, "theRadio", radio);
            try
            {
                x.B.UnkeyAfterNoiseDelay("old over"); x.Ends[0].End(); var oldOff = x.B.q.Dequeue();
                Set(x.B, "_Transmit", false); Call(x.B, "noteRadioMox", true); // a key-down edge with no unkey reported since
                ((FlexBase.FunctionDel)oldOff.Item)();
                Assert.False(radio.Mox);
            }
            finally { Set(x.B, "theRadio", null); }
        }

        [Fact]
        public void Our_own_write_raises_its_edge_as_ours_and_the_radio_s_report_as_the_radio_s()
        {
            using var x = new Rig();
            var radio = (Radio)RuntimeHelpers.GetUninitializedObject(typeof(Radio)); Set(radio, "_connected", true); Set(radio, "_mox", false); Set(x.B, "theRadio", radio);
            // FlexLib's setter raises the change synchronously, on the writing thread, as the app's handler sees it.
            PropertyChangedEventHandler h = (_, e) => { if (e.PropertyName == "Mox") Call(x.B, "noteRadioMox", radio.Mox); };
            radio.PropertyChanged += h;
            try
            {
                Set(x.B, "_Transmit", false);
                x.B.Transmit = true;
                var down = x.B.q.Dequeue(); ((FlexBase.FunctionDel)down.Item)();
                Assert.True(radio.Mox); Assert.True(x.B.Transmit);
                Assert.Equal(0L, x.B.TxOverProvenanceNow.CountedKeyDowns); // our write: not the radio's over
                // Join repair 3: the radio's report of the over ending counts once
                // the radio has CONFIRMED our key request; before that it would be
                // a stale report (TxJoinRepairThreeTests pins that half).
                Call(x.B, "noteRadioInterlock", InterlockState.Transmitting);
                Set(radio, "_interlockState", InterlockState.Receive); Set(radio, "_mox", false);
                Call(x.B, "noteRadioMox", false);                            // the radio's own report
                Assert.Equal(1L, x.B.TxOverProvenanceNow.CountedEnds);
            }
            finally { radio.PropertyChanged -= h; Set(x.B, "theRadio", null); }
        }

        // ── P2: the double-failure rescue opens on the worker (adapted: the worker is queued) ──

        private static FlexBase.audioChannelData Channel(Rig x, Fake old)
        {
            var c = (FlexBase.audioChannelData)RuntimeHelpers.GetUninitializedObject(typeof(FlexBase.audioChannelData));
            c.PortAudioStream = old; c.Started = true;
            Set(x.B, "opusInputChannel", c); Set(x.B, "opusInputAvailable", true); Set(x.B, "_txTryChannel", c);
            Set(x.B, "_txStreamAlreadyJudged", true); Set(x.B, "_Transmit", false);
            return c;
        }

        private static void Step(Rig x) => Call(x.B, "TxTryStep");
        private static bool Start(Rig x) => (bool)Call(x.B, "startOpusInputChannel")!;

        [Fact]
        public void A_double_start_failure_must_not_open_the_rescue_on_the_receive_loop()
        {
            using var x = new Rig(); var old = new Fake { Result = false }; var fresh = new Fake { Result = false };
            var channel = Channel(x, old); Set(x.B, "_txTryFallbackDue", true);
            var jobs = new List<Action>(); x.B.TxCaptureWorkerForTest = a => jobs.Add(a);
            int opens = 0; x.B.TxCaptureOpenForTest = _ => ++opens == 1 ? fresh : null!;
            Step(x);                        // the fall back's open goes to its worker
            Assert.Single(jobs); jobs[0]();  // and comes back with the new stream
            Step(x);                        // the loop swaps: both refuse to start
            Assert.Equal(1, opens);         // the rescue did NOT open on the loop
            Assert.Equal(2, jobs.Count);    // it is on its worker
            Assert.True((bool)Get(x.B, "_micReopenPending")!);
            Assert.Null(channel.PortAudioStream);
            Assert.False(Start(x));         // pending: the gate starts nothing
            Assert.Null(Report(x.B));
            jobs[1]();                      // the rescue's open, refused
            Assert.Equal(2, opens);
            Assert.False(Start(x));         // published as a failure: given up, said once
            Assert.True((bool)Get(x.B, "_micGivenUp")!);
            Assert.Equal(Lexicon.Get("audio.pc_audio.mic_stream_gave_up"), Report(x.B));
            typeof(FlexBase).GetProperty("LastStreamRecoveryReport", Any)!.SetValue(x.B, null);
            Assert.False(Start(x));
            Assert.Null(Report(x.B));
        }

        [Fact]
        public void A_rescued_stream_is_published_and_started_by_the_gate()
        {
            using var x = new Rig(); var old = new Fake { Result = false }; var fresh = new Fake { Result = false }; var rescued = new Fake();
            var channel = Channel(x, old); Set(x.B, "_txTryFallbackDue", true);
            using var wake = new RemoteAudioWake(); Set(x.B, "_remoteAudioWake", wake);
            var jobs = new List<Action>(); x.B.TxCaptureWorkerForTest = a => jobs.Add(a);
            int opens = 0; x.B.TxCaptureOpenForTest = _ => ++opens == 1 ? fresh : rescued;
            Step(x); jobs[0](); Step(x); jobs[1]();
            Assert.True(Start(x));
            Assert.Same(rescued, channel.PortAudioStream);
            Assert.Equal(1, rescued.Starts);
            Assert.Same(wake, rescued.StreamDiedHook!.Target); // this run's death hook, wired by the one open path
            Assert.False((bool)Get(x.B, "_micGivenUp")!);
            Assert.Null(Report(x.B));                           // unkeyed: traced only
        }

        // ── The joins ──

        private static Fake DeadMic(Rig x)
        {
            var dead = new Fake();
            Channel(x, dead);
            x.B.TxCaptureWorkerForTest = a => a();
            return dead;
        }

        [Fact]
        public void Every_death_ends_a_running_try_with_no_verdict_and_abandons_an_open_in_flight()
        {
            using var x = new Rig(); var dead = DeadMic(x);
            var jobs = new List<Action>(); x.B.TxCaptureWorkerForTest = a => jobs.Add(a);
            Set(x.B, "_txTryRunning", true); Set(x.B, "_txStreamAlreadyJudged", false);
            Call(x.B, "StartTxOpenJob", 50, Enum.Parse(typeof(FlexBase).GetNestedType("TxOpenKind", BindingFlags.NonPublic)!, "Fallback"), Get(x.B, "opusInputChannel"), 0, "");
            Call(x.B, "RecoverMicStream");
            Assert.False((bool)Get(x.B, "_txTryRunning")!);
            Assert.True((bool)Get(x.B, "_txStreamPartialRun")!);
            Assert.True((bool)Get(x.B, "_txTryFallbackDue")!);
            Assert.Null(Get(x.B, "_txOpenJob"));
            Assert.True((bool)Get(x.B, "_micRestartPending")!); // tier 1 as before
            var stray = new Fake(); int strayOpens = 0; x.B.TxCaptureOpenForTest = _ => { strayOpens++; return stray; };
            jobs[0]();                                           // join repair 3: the abandoned job begins no open at all
            Assert.Equal(0, strayOpens);
            Assert.Null(Get(x.B, "_txOpenJob"));
        }

        [Fact]
        public void No_try_runs_ahead_of_a_pending_restart()
        {
            using var x = new Rig(); var dead = DeadMic(x);
            Call(x.B, "RecoverMicStream");
            Set(x.B, "_txTryNextCheckTick", 0L);
            dead.Result = true;
            Assert.True(Start(x));                               // the restart, resolved against the stream that died
            Assert.Equal(0L, (long)Get(x.B, "_txTryNextCheckTick")!); // the try did not look first
            Assert.Same(dead, ((FlexBase.audioChannelData)Get(x.B, "opusInputChannel")!).PortAudioStream);
        }

        [Fact]
        public void A_failed_restart_reopens_on_the_worker_and_the_new_stream_is_started_with_this_run_s_hook()
        {
            using var x = new Rig(); var dead = DeadMic(x);
            using var wake = new RemoteAudioWake(); Set(x.B, "_remoteAudioWake", wake);
            var jobs = new List<Action>(); x.B.TxCaptureWorkerForTest = a => jobs.Add(a);
            var fresh = new Fake(); int opens = 0; x.B.TxCaptureOpenForTest = _ => { opens++; return fresh; };
            Call(x.B, "RecoverMicStream");
            dead.Result = false;
            Assert.False(Start(x));                              // tier 1 failed
            Assert.Equal(0, opens);                              // nothing opened on the loop
            Assert.Single(jobs);
            Assert.True((bool)Get(x.B, "_micReopenPending")!);
            Assert.False((bool)Get(x.B, "_micGivenUp")!);
            Assert.Null(Report(x.B));                            // pending is not a give-up
            jobs[0]();
            Assert.True(Start(x));                               // published and started by the gate
            var channel = (FlexBase.audioChannelData)Get(x.B, "opusInputChannel")!;
            Assert.Same(fresh, channel.PortAudioStream);
            Assert.Same(wake, fresh.StreamDiedHook!.Target);
            Assert.False((bool)Get(x.B, "_micRestartPending")!);
            Assert.Null(Report(x.B));
        }

        [Fact]
        public void A_reopen_that_will_not_open_gives_up_and_says_it_once()
        {
            using var x = new Rig(); var dead = DeadMic(x);
            x.B.TxCaptureOpenForTest = _ => null!;
            Call(x.B, "RecoverMicStream");
            dead.Result = false;
            Assert.False(Start(x));                              // tier 1 fails; tier 2 runs inline and is back
            Assert.False(Start(x));                              // published as a failure
            Assert.True((bool)Get(x.B, "_micGivenUp")!);
            Assert.Equal(Lexicon.Get("audio.pc_audio.mic_stream_gave_up"), Report(x.B));
            typeof(FlexBase).GetProperty("LastStreamRecoveryReport", Any)!.SetValue(x.B, null);
            Assert.False(Start(x));
            Assert.Null(Report(x.B));
        }

        [Fact]
        public void A_reopened_stream_that_will_not_start_gives_up_rather_than_reopening_again()
        {
            using var x = new Rig(); var dead = DeadMic(x);
            var fresh = new Fake { Result = false }; int opens = 0; x.B.TxCaptureOpenForTest = _ => { opens++; return fresh; };
            Call(x.B, "RecoverMicStream");
            dead.Result = false;
            Assert.False(Start(x));
            Assert.False(Start(x));
            Assert.Equal(1, opens);                              // one re-open per recovery, never a loop
            Assert.True((bool)Get(x.B, "_micGivenUp")!);
            Assert.Equal(Lexicon.Get("audio.pc_audio.mic_stream_gave_up"), Report(x.B));
        }

        [Fact]
        public void The_cap_still_gives_up_without_a_reopen()
        {
            using var x = new Rig(); var dead = DeadMic(x);
            int opens = 0; x.B.TxCaptureOpenForTest = _ => { opens++; return new Fake(); };
            long now = 1_000_000; Set(x.B, "_streamRecoveryClock", (Func<long>)(() => now));
            for (int i = 0; i < StreamRecoveryPolicy.MaxRecoveries; i++)
            {
                Call(x.B, "RecoverMicStream"); Assert.True(Start(x)); now += 1000;
            }
            Call(x.B, "RecoverMicStream");                       // the fourth death inside the window
            Assert.True((bool)Get(x.B, "_micGivenUp")!);
            Assert.Equal(0, opens);                              // the cap is not permission for a tier-2 retry
            Assert.Equal(Lexicon.Get("audio.pc_audio.mic_stream_gave_up"), Report(x.B));
        }

        [Fact]
        public void An_open_from_an_ended_run_never_publishes_into_a_later_one()
        {
            using var x = new Rig(); var dead = DeadMic(x);
            using var oldWake = new RemoteAudioWake(); using var newWake = new RemoteAudioWake();
            Set(x.B, "_remoteAudioWake", oldWake);
            var jobs = new List<Action>(); x.B.TxCaptureWorkerForTest = a => jobs.Add(a);
            var fresh = new Fake();
            // Join repair 3: a job whose run has already ended begins no open
            // (TxJoinRepairThreeTests); so here the new run takes the loop WHILE
            // the old run's open is in progress, and the open completes.
            x.B.TxCaptureOpenForTest = _ => { Set(x.B, "_remoteAudioWake", newWake); return fresh; };
            Call(x.B, "RecoverMicStream"); dead.Result = false; Start(x); // a re-open, asked by the old run
            jobs[0]();                                                   // a new run took the loop as it opened
            Start(x);
            foreach (var close in jobs.Skip(1).ToList()) close();        // its close, off the loop
            var channel = (FlexBase.audioChannelData)Get(x.B, "opusInputChannel")!;
            Assert.NotSame(fresh, channel.PortAudioStream);
            Assert.Equal(1, fresh.Closes);
        }

        [Fact]
        public void The_teardown_waits_for_an_open_still_on_its_worker()
        {
            using var x = new Rig(); DeadMic(x);
            x.B.TxCaptureWorkerForTest = null; // a real worker thread
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var fresh = new Fake();
            x.B.TxCaptureOpenForTest = _ => { entered.Set(); release.Wait(5000); return fresh; };
            Call(x.B, "StartTxOpenJob", 10, Enum.Parse(typeof(FlexBase).GetNestedType("TxOpenKind", BindingFlags.NonPublic)!, "Replace"), Get(x.B, "opusInputChannel"), 0, "test");
            Assert.True(entered.Wait(5000));
            Call(x.B, "AbandonTxOpenJob", "test teardown");
            var timer = new Timer(_ => release.Set(), null, 200, Timeout.Infinite);
            var sw = Stopwatch.StartNew();
            Call(x.B, "WaitForTxCaptureWorkers", "test teardown");
            sw.Stop(); timer.Dispose();
            Assert.Equal(0, (int)Get(x.B, "_txCaptureWorkers")!);
            Assert.Equal(1, fresh.Closes);                       // the abandoned open was closed before the wait returned
            Assert.True(sw.ElapsedMilliseconds >= 150, "the wait returned before the worker was done");
        }

        [Fact]
        public void Every_way_out_of_the_run_retires_holds_and_opens_before_the_audio_server_ends()
        {
            string remote = FlexBasePartials.JoinedText();
            int done = remote.IndexOf("            remoteDone:", StringComparison.Ordinal);
            int terminate = remote.IndexOf("Audio.Terminate();", done, StringComparison.Ordinal);
            Assert.True(done > 0 && terminate > done);
            string teardown = remote.Substring(done, terminate - done);
            Assert.Contains("if (run == Interlocked.Read(ref _remoteAudioRun))", teardown);
            Assert.Contains("CancelHeldUnkey(\"the PC audio run ended\", unkeyNow: true)", teardown);
            Assert.Contains("AbandonTxOpenJob(\"the PC audio run ended\")", teardown);
            Assert.Contains("WaitForTxCaptureWorkers(", teardown);
            // And the stop path keeps both its own and the receive track's: the hold, the open, the wake.
            int stop = remote.IndexOf("private void stopRemoteAudioThread()", StringComparison.Ordinal);
            string stopBody = remote.Substring(stop, remote.IndexOf("remoteAudioThread = null;", stop, StringComparison.Ordinal) - stop);
            Assert.Contains("CancelHeldUnkey(\"the remote audio is stopping\", unkeyNow: true);", stopBody);
            Assert.Contains("AbandonTxOpenJob(\"the remote audio is stopping\");", stopBody);
            Assert.Contains("SignalRemoteAudioWake();", stopBody);
        }

        [Fact]
        public void The_key_edge_still_wakes_the_loop_and_ends_a_hold()
        {
            string text = FlexBasePartials.JoinedText();
            int at = text.IndexOf("private void noteRadioMox(bool mox)", StringComparison.Ordinal);
            string body = text.Substring(at, text.IndexOf("raiseTransmitChange(_Transmit);", at, StringComparison.Ordinal) - at);
            // Join repair 3: the edge is read against our own key request, and a
            // stale receive report does not end the hold.
            Assert.Contains("bool staleReceive = mox != oldTransmit && NoteTxMoxEdge(mox);", body);
            Assert.Contains("if (!mox && !staleReceive) NoteRadioUnkeyedForHold(\"its Mox off\");", body);
            Assert.Contains("SignalRemoteAudioWake();", body);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
