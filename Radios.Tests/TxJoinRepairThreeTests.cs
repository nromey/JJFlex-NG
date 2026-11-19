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
    /// Sprint 49 Track H2, join repair 3, as carried without the unkey hold
    /// (Noel's H1 = B, 2026-11-19): Codex's two findings against 7019398b that
    /// are about the microphone worker (an abandoned worker opening a second
    /// rate after the teardown, and a returned re-open stranded when the gate
    /// starts a generated source), each with its reproduction adopted verbatim
    /// from <c>2026-11-19-codex-h2-join-results\CodexH2JoinReviewTests.cs</c>,
    /// the tests their fixes need, and the worker's schedule exploration
    /// (<see cref="TxJoinScheduleExplorationTests"/>). His three findings about
    /// the hold, and their tests, stay on sprint49/h2-join. Windowless: an
    /// unconnected FlexBase whose command queue collects writes, and fake
    /// streams that open no device. No test may reach the real microphone open:
    /// every rig sets <see cref="FlexBase.TxCaptureOpenForTest"/>.
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

            public Rig()
            {
                B.q.MainLoop = true; // collect, do not send
                Set(B, "_Transmit", true);
                // Never the real microphone: every open in these tests is a fake.
                B.TxCaptureOpenForTest = _ => null!;
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

        private static TxOpenKindProxy Kind(string name) => new(Enum.Parse(typeof(FlexBase).GetNestedType("TxOpenKind", BindingFlags.NonPublic)!, name));
        internal readonly struct TxOpenKindProxy { public TxOpenKindProxy(object v) { Value = v; } public object Value { get; } }

        // ── Codex's two worker reproductions (verbatim) ──

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

        // ── Codex's P2: no worker outlives its run ──

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
            foreach (var (method, call) in new[] { ("public static bool Initialize(", "if (!AudioAnchor.Init(inDev, outDev)) return false;"), ("public static void Terminate()", "AudioAnchor.Term();") })
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

        // ── Codex's P2: returned work is published whatever producer the gate selects ──

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

        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}
