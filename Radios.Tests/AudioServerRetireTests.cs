using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track H2 split: Codex's P2 against a4018dfb
    /// (<c>for-claude\2026-11-19-codex-sprint49-h2-join-recheck.md</c>, "The new
    /// server gate is not the complete lifetime barrier claimed"). Audio.Terminate
    /// waits one second for the audio server and returns; until this, the server
    /// it stopped waiting for was forgotten, the next Initialize replaced the
    /// process-global queue under it, and the old server, reading the static
    /// queue on every pass, went on to take the NEW server's items beside it.
    /// Now each server serves only its own queue, a server Terminate stopped
    /// waiting for is remembered, and Initialize refuses (nothing replaced)
    /// until it has gone.
    /// </summary>
    /// <remarks>
    /// Windowless and silent: the "servers" here are managed stand-in threads,
    /// and <see cref="AudioAnchor.ServerThreadForTest"/> makes every thread
    /// Initialize would start, so no test reaches PortAudio, opens a device or
    /// plays anything. Every static touched is put back. In the collection that
    /// owns the audio server's statics (the generation tests and the closed
    /// stream tests are there too).
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class AudioServerRetireTests
    {
        /// <summary>A stand-in "server" that runs until it is let go.</summary>
        private static Thread StandIn(ManualResetEventSlim release)
        {
            var t = new Thread(() => release.Wait(15000)) { IsBackground = true, Name = "StandInAudioServer" };
            t.Start();
            return t;
        }

        [Fact]
        public void A_server_Terminate_stopped_waiting_for_is_remembered_and_the_next_Initialize_refuses_until_it_has_gone()
        {
            using var release = new ManualResetEventSlim();
            var stand = StandIn(release);
            var queue = new BlockingCollection<AudioAnchor.workItem>();
            var was = AudioAnchor.SwapServerForTest(stand, queue, null);
            int retireWas = AudioAnchor.PreviousServerRetireMs;
            var threadWas = AudioAnchor.ServerThreadForTest;
            Thread? started = null;
            AudioAnchor.ServerThreadForTest = q => started = new Thread(() => { }) { IsBackground = true };
            try
            {
                AudioAnchor.PreviousServerRetireMs = 50;
                var sw = Stopwatch.StartNew();
                Audio.Terminate();                                  // the stand-in never takes its terminate
                sw.Stop();
                Assert.True(sw.ElapsedMilliseconds < 5000, "Terminate must stay bounded, so the app can always close");
                Assert.Equal(1, queue.Count);                       // the terminate went on ITS queue
                Assert.True(stand.IsAlive);
                Assert.Same(stand, AudioAnchor.UnretiredServerForTest); // remembered, not forgotten

                long generation = Audio.ServerGeneration;
                Assert.False(Audio.Initialize(null!, null!));       // refused while the old server is alive
                Assert.Null(started);                                // no second server was made
                Assert.Same(queue, AudioAnchor.work);                // nothing was replaced
                Assert.Equal(generation, Audio.ServerGeneration);    // and the generation did not move
                Audio.Terminate();                                   // the refused run's own end posts nothing
                Assert.Equal(1, queue.Count);
                generation = Audio.ServerGeneration;                 // (a Terminate always moves it: nothing old may run)

                // Positive control: once the old server has gone, a start goes ahead.
                release.Set();
                Assert.True(stand.Join(5000));
                Assert.True(Audio.Initialize(null!, null!));
                Assert.NotNull(started);
                Assert.Null(AudioAnchor.UnretiredServerForTest);
                Assert.NotSame(queue, AudioAnchor.work);             // its own new queue
                Assert.Equal(generation + 1, Audio.ServerGeneration);
            }
            finally
            {
                release.Set();
                stand.Join(5000);
                started?.Join(5000);
                AudioAnchor.ServerThreadForTest = threadWas;
                AudioAnchor.PreviousServerRetireMs = retireWas;
                AudioAnchor.SwapServerForTest(was.Server, was.Work, was.Unretired);
            }
        }

        [Fact]
        public void A_server_takes_items_only_from_its_own_queue_never_from_a_later_one()
        {
            var own = new BlockingCollection<AudioAnchor.workItem>();
            var later = new BlockingCollection<AudioAnchor.workItem>();
            var was = AudioAnchor.SwapServerForTest(null!, later, null); // a later Init's queue is the static one
            var server = new Thread(() => AudioAnchor.ServeQueue(own)) { IsBackground = true, Name = "OwnQueueServer" };
            try
            {
                server.Start();
                // An item for a closed stream is skipped (traced) by the loop: it
                // proves the loop is taking from this queue without any PortAudio call.
                own.Add(new AudioAnchor.workItem(AudioAnchor.workItems.close));
                var until = DateTime.UtcNow.AddSeconds(5);
                while (own.Count > 0 && DateTime.UtcNow < until) Thread.Sleep(5);
                Assert.Equal(0, own.Count);                          // positive control: it serves its own queue
                later.Add(new AudioAnchor.workItem(AudioAnchor.workItems.close));
                Thread.Sleep(200);
                Assert.Equal(1, later.Count);                        // and never touches the later server's
                own.CompleteAdding();
                Assert.True(server.Join(5000));                      // its queue completed, it ends
            }
            finally
            {
                if (!own.IsAddingCompleted) own.CompleteAdding();
                server.Join(5000);
                AudioAnchor.SwapServerForTest(was.Server, was.Work, was.Unretired);
            }
        }
    }
}
