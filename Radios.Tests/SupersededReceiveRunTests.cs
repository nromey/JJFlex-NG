using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using JJPortaudio;
using Xunit;
using static Radios.Tests.TxJoinRepairThreeTests;

namespace Radios.Tests
{
    /// <summary>
    /// #1229 repair 2, Codex's re-check of de893930: an admitted PC-audio run
    /// that was abandoned by the ten-second stop, and is still in its receive
    /// loop when a newer run is requested, owns its receive stream until it
    /// exits (the newer run waits in admission). So it marks its own feed ended
    /// and asks for its own closing tallies, although it is no longer the
    /// newest requested run. A run that was never admitted marks nothing.
    /// </summary>
    /// <remarks>
    /// Codex's probe, adopted: a real <see cref="FlexBase"/>, the real
    /// <c>AdmitRemoteAudioRun</c>, a stand-in thread for the abandoned run, and a
    /// receive stream built from the real <see cref="Audio"/> control block and
    /// <see cref="RxPlaybackQueue"/> with no PortAudio stream, device or radio.
    /// </remarks>
    public sealed class SupersededReceiveRunTests
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private sealed class OwnedStream : IDisposable
        {
            private readonly int _key;
            public readonly RxPlaybackQueue Queue = new("receive", 10, 10.0, 48000, RxPlaybackQueue.DefaultReserveMilliseconds);
            public readonly object Channel;

            public OwnedStream()
            {
                _key = Audio.queues.Add();
                Audio.queues.getQ(_key).Playback = Queue;
                var audio = (Audio)RuntimeHelpers.GetUninitializedObject(typeof(Audio));
                typeof(Audio).GetField("qKey", Inst)!.SetValue(audio, _key);
                var stream = (JJAudioStream)RuntimeHelpers.GetUninitializedObject(typeof(JJAudioStream));
                typeof(JJAudioStream).GetField("aud", Inst)!.SetValue(stream, audio);
                var chanType = typeof(FlexBase).GetNestedType("audioChannelData", Inst)!;
                Channel = RuntimeHelpers.GetUninitializedObject(chanType);
                chanType.GetField("PortAudioStream", Inst)!.SetValue(Channel, stream);
            }

            /// <summary>One callback that finds the queue empty after playing: a gap, or the stream's end.</summary>
            public void RunDry()
            {
                Queue.Begin(16, false); Queue.NotePlayed(10, 0);
                Queue.Begin(0, false); Queue.NotePlayed(0, 4800);
            }

            public void Dispose() => Audio.queues.Remove(_key);
        }

        private static void Own(Rig x, OwnedStream s) => Set(x.B, "opusOutputChannel", s.Channel);

        private static void Release(Rig x)
        {
            Set(x.B, "opusOutputChannel", null);
            Set(x.B, "_abandonedRemoteAudioThread", null);
            x.B.AudioFeedEnded = null;
        }

        [Fact]
        public void An_admitted_run_superseded_while_its_successor_waits_marks_its_own_feed_and_closes_its_own_tallies()
        {
            using var x = new Rig();
            using var stream = new OwnedStream();
            int admitWas = FlexBase.RemoteRunAdmissionMs;
            using var releaseOld = new ManualResetEventSlim(false);
            try
            {
                int tallies = 0;
                x.B.AudioFeedEnded = () => Interlocked.Increment(ref tallies);
                long run = (long)Get(x.B, "_remoteAudioRun")! + 1;
                Set(x.B, "_remoteAudioRun", run);
                Assert.True(x.B.AdmitRemoteAudioRun(run));      // the run that will be abandoned, admitted
                Own(x, stream);

                // It is abandoned by the ten-second stop and a newer run is requested.
                var oldRun = new Thread(() => releaseOld.Wait(10000)) { IsBackground = true };
                oldRun.Start();
                Set(x.B, "_abandonedRemoteAudioThread", oldRun);
                Set(x.B, "_remoteAudioRun", run + 1);
                FlexBase.RemoteRunAdmissionMs = 8000;
                bool admitted = false;
                var next = new Thread(() => admitted = x.B.AdmitRemoteAudioRun(run + 1)) { IsBackground = true };
                next.Start();
                Thread.Sleep(30);
                Assert.True(next.IsAlive && !admitted, "the successor is not waiting in admission, so this proves nothing");

                // The abandoned run reaches its teardown: its own stream, its own tallies.
                x.B.MarkReceiveFeedEnded(run);
                x.B.CloseReceiveTallies(run);
                Assert.True(stream.Queue.FeedEnded, "the admitted run's own stream was left unmarked");
                Assert.Equal(1, tallies);
                stream.RunDry();
                Assert.Equal(0, stream.Queue.Starvations);
                Assert.Equal(1, stream.Queue.StopStarvations);

                releaseOld.Set();
                Assert.True(oldRun.Join(2000) && next.Join(2000) && admitted, "admission did not follow the old run's exit");
            }
            finally { releaseOld.Set(); Release(x); FlexBase.RemoteRunAdmissionMs = admitWas; }
        }

        [Fact]
        public void A_successor_refused_at_admission_marks_nothing_and_closes_nothing()
        {
            using var x = new Rig();
            using var stream = new OwnedStream();
            int admitWas = FlexBase.RemoteRunAdmissionMs;
            using var releaseOld = new ManualResetEventSlim(false);
            try
            {
                int tallies = 0;
                x.B.AudioFeedEnded = () => Interlocked.Increment(ref tallies);
                long run = (long)Get(x.B, "_remoteAudioRun")! + 1;
                Set(x.B, "_remoteAudioRun", run);
                Assert.True(x.B.AdmitRemoteAudioRun(run));
                Own(x, stream);

                var oldRun = new Thread(() => releaseOld.Wait(10000)) { IsBackground = true };
                oldRun.Start();
                Set(x.B, "_abandonedRemoteAudioThread", oldRun);
                Set(x.B, "_remoteAudioRun", run + 1);
                FlexBase.RemoteRunAdmissionMs = 100;
                Assert.False(x.B.AdmitRemoteAudioRun(run + 1));  // refused: the old run is still alive

                // Even if anything in the refused run reached the marks, it owns nothing.
                x.B.MarkReceiveFeedEnded(run + 1);
                x.B.CloseReceiveTallies(run + 1);
                Assert.False(stream.Queue.FeedEnded, "a refused run marked the stream another run owns");
                Assert.Equal(0, tallies);

                // The owner still can.
                x.B.MarkReceiveFeedEnded(run);
                Assert.True(stream.Queue.FeedEnded);
            }
            finally { releaseOld.Set(); Release(x); FlexBase.RemoteRunAdmissionMs = admitWas; }
        }

        [Fact]
        public void The_current_admitted_run_marks_its_feed_and_closes_its_tallies()
        {
            // The ordinary case, the positive control for both tests above.
            using var x = new Rig();
            using var stream = new OwnedStream();
            try
            {
                int tallies = 0;
                x.B.AudioFeedEnded = () => Interlocked.Increment(ref tallies);
                long run = (long)Get(x.B, "_remoteAudioRun")! + 1;
                Set(x.B, "_remoteAudioRun", run);
                Assert.True(x.B.AdmitRemoteAudioRun(run));
                Own(x, stream);
                stream.RunDry();                                 // a real gap first: the feed is live
                Assert.Equal(1, stream.Queue.Starvations);
                x.B.MarkReceiveFeedEnded(run);
                x.B.MarkReceiveFeedEnded(run);                   // harmless twice
                x.B.CloseReceiveTallies(run);
                Assert.True(stream.Queue.FeedEnded);
                Assert.Equal(1, tallies);
            }
            finally { Release(x); }
        }
    }
}
