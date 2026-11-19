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
    /// repair 2, as carried without the unkey hold (Noel's H1 = B, 2026-11-19):
    /// Codex's finding against 661dc63f about the double-failure rescue, with
    /// its reproduction adopted from
    /// <c>2026-11-18-codex-h2-rulings-recheck-results\CodexH2RepairReviewTests.cs</c>
    /// (adapted to queue the worker instead of running it inline: an inline
    /// worker runs on the caller's thread by construction), and the joins from
    /// his list. His four findings about the hold, and their tests, stay on
    /// sprint49/h2-join. Windowless: an unconnected FlexBase whose command queue
    /// collects writes, and fake streams that open no device. No test here
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
            // Repair 1, make before break: a stand-in that delivers at once and
            // is handed over to as soon as it asks, unless told otherwise.
            public bool Produces = true, HandsOver = true;
            public int Requests, Forced;
            private bool _taken;
            public override bool WaitUntilProducing(int timeoutMs) => Produces;
            public override void RequestTakeOver() { Requests++; if (HandsOver) _taken = true; }
            public override bool HasTakenOver => _taken;
            public override void ForceTakeOver() { Forced++; _taken = true; }
        }

        private sealed class Rig : IDisposable
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
                try { Set(B, "_Transmit", false); Set(B, "opusInputChannel", null); Set(B, "_remoteAudioWake", null); B.Dispose(); }
                finally
                {
                    API.ProgramName = _program; API.IsGUI = _gui; Vita.VitaSocket.TraceSink = _sink;
                    FlexBase.TxAudioCallbacksPerSecond = _txRate;
                    TxCallbackRate.CurrentRate = _currentRate;
                }
            }
        }

        // ── P2: the double-failure rescue opened on the receive loop ──
        //
        // Codex's finding against 661dc63f, fixed then by opening the rescue on a
        // worker. Since the split's repair 1 the swap is make before break: the
        // new stream is started beside the running one, which is never stopped,
        // so a new stream that will not start leaves nothing to rescue. These two
        // replace the rescue's two tests (on h2-join, and on this branch until
        // repair 1) with what must now hold instead: the running stream is
        // untouched, and nothing is opened again, on the loop or anywhere.

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
        private static object Kind(string name) => Enum.Parse(typeof(FlexBase).GetNestedType("TxOpenKind", BindingFlags.NonPublic)!, name);

        [Fact]
        public void A_new_stream_that_will_not_start_never_touches_the_running_one_and_nothing_is_rescued()
        {
            using var x = new Rig(); var old = new Fake { Result = false }; var fresh = new Fake { Result = false };
            var channel = Channel(x, old); Set(x.B, "_txTryFallbackDue", true);
            var jobs = new List<Action>(); x.B.TxCaptureWorkerForTest = a => jobs.Add(a);
            int opens = 0; x.B.TxCaptureOpenForTest = _ => { opens++; return fresh; };
            Step(x);                        // the fall back's open goes to its worker
            Assert.Single(jobs); jobs[0]();  // it opens, and will not start beside the running stream
            Assert.Equal(1, fresh.Starts);
            Assert.Equal(1, fresh.Closes);
            Step(x);                        // the loop takes the result
            Assert.Equal(1, opens);         // nothing opened again
            Assert.Single(jobs);            // on the loop or on a worker
            Assert.Equal(0, old.Stops);     // the running stream was never stopped
            Assert.Equal(0, old.Starts);    // so never needed starting again (it would have refused)
            Assert.Same(old, channel.PortAudioStream);
            Assert.True(channel.Started);
            Assert.False((bool)Get(x.B, "_micReopenPending")!);
            Assert.True(Start(x));          // the gate finds the microphone running
            Assert.False((bool)Get(x.B, "_micGivenUp")!);
            Assert.Null(Report(x.B));
            Assert.True((bool)Get(x.B, "_txTryFallbackDue")!); // asked again later
        }

        [Fact]
        public void A_tried_stream_that_starts_but_delivers_nothing_is_closed_kept_as_cannot_run_alongside_and_the_running_one_carries_on()
        {
            string root = Path.Combine(Path.GetTempPath(), "jjf-h2s-r1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var storeWas = TxCallbackRate.StoreForTest;
            try
            {
                TxCallbackRate.StoreForTest = new TxCallbackVerdictStore(root);
                using var x = new Rig(); var old = new Fake(); var fresh = new Fake { Produces = false };
                var channel = Channel(x, old);
                x.B.TxCaptureWorkerForTest = a => a();
                x.B.TxCaptureOpenForTest = _ => fresh;
                Call(x.B, "StartTxOpenJob", TxCallbackRate.Fast, Kind("Try"), channel, 0, "");
                Call(x.B, "TakeReturnedTxOpen", channel, false);
                Assert.Equal(1, fresh.Starts);
                Assert.Equal(1, fresh.Closes);   // closed on its worker, on its own server
                Assert.Equal(0, fresh.Requests); // never asked to take over
                Assert.Equal(0, old.Stops);
                Assert.Same(old, channel.PortAudioStream);
                Assert.True(channel.Started);
                var capture = new TxCaptureIdentity("", -1, (int)FlexBase.OpusTxSampleRateSetting, 2);
                var verdict = TxCallbackRate.StoreForTest.Find(capture, TxCallbackRate.Fast);
                Assert.NotNull(verdict);
                Assert.False(verdict!.Passed);
                Assert.True(verdict.CannotOpenAlongside); // about running two at once, not a verdict on the rate
            }
            finally
            {
                TxCallbackRate.StoreForTest = storeWas;
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void A_running_stream_that_stopped_delivering_is_taken_over_by_force_without_being_stopped()
        {
            using var x = new Rig(); var old = new Fake(); var fresh = new Fake { HandsOver = false };
            var channel = Channel(x, old); Set(x.B, "_txTryFallbackDue", true);
            x.B.TxCaptureWorkerForTest = a => a();
            x.B.TxCaptureOpenForTest = _ => fresh;
            var sw = Stopwatch.StartNew();
            Step(x);
            sw.Stop();
            Assert.Equal(1, fresh.Requests);
            Assert.Equal(1, fresh.Forced);   // no hand-over came, so it took over
            Assert.Equal(0, old.Stops);      // and the running stream was not stopped first
            Assert.Equal(1, old.Closes);     // it was closed off the loop afterwards
            Assert.Same(fresh, channel.PortAudioStream);
            Assert.True(channel.Started);
            Assert.True(sw.ElapsedMilliseconds < 2000, "the loop waited " + sw.ElapsedMilliseconds + " ms for the hand-over");
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
        public void Every_way_out_of_the_run_retires_its_opens_before_the_audio_server_ends()
        {
            string remote = FlexBasePartials.JoinedText();
            int done = remote.IndexOf("            remoteDone:", StringComparison.Ordinal);
            int terminate = remote.IndexOf("Audio.Terminate(serverGeneration);", done, StringComparison.Ordinal); // repair 1: its own server only
            Assert.True(done > 0 && terminate > done);
            string teardown = remote.Substring(done, terminate - done);
            Assert.Contains("if (run == Interlocked.Read(ref _remoteAudioRun))", teardown);
            Assert.DoesNotContain("CancelHeldUnkey", teardown);   // the H2 split: no hold on this branch
            Assert.Contains("AbandonTxOpenJob(\"the PC audio run ended\")", teardown);
            Assert.Contains("WaitForTxCaptureWorkers(", teardown);
            // And the stop path keeps both its own and the receive track's: the open and the wake.
            int stop = remote.IndexOf("private void stopRemoteAudioThread()", StringComparison.Ordinal);
            string stopBody = remote.Substring(stop, remote.IndexOf("remoteAudioThread = null;", stop, StringComparison.Ordinal) - stop);
            Assert.DoesNotContain("CancelHeldUnkey", stopBody);
            Assert.Contains("AbandonTxOpenJob(\"the remote audio is stopping\");", stopBody);
            Assert.Contains("SignalRemoteAudioWake();", stopBody);
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
