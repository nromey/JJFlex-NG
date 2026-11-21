using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JJPortaudio;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The audition's analysis off the window's thread (#262, Sprint 49 Track Q
    /// phase B, item 4, and item 9's Process again): a take analyses on a worker
    /// under a generation number; a new take or Discard cancels it; the folder is
    /// cleared so that nothing the worker writes can appear after the clear; an
    /// old generation's outcome says it is stale; and the same take can be
    /// processed again with the configuration as it is now.
    /// </summary>
    /// <remarks>
    /// A fake recorder writes a synthetic take; a fake processor blocks until the
    /// test releases it, so every race here is forced, not hoped for. No
    /// microphone, no sound, a fresh temporary folder per test.
    /// </remarks>
    public sealed class ChainAuditionBackgroundTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "jjflex-audition-bg-" + Guid.NewGuid().ToString("N"));
        private static readonly DateTime Now = new(2026, 11, 21, 9, 30, 15, 250);

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private sealed class Recorder : IAuditionRecorder
        {
            public float[] Samples = ChainAuditionAnalysisTests.Take(8);
            public string Path = "";
            public bool Running;
            public bool IsRunning => Running;
            public bool IsRecordingTo(string path) => Running && string.Equals(Path, path, StringComparison.OrdinalIgnoreCase);
            public bool Start(string path, bool holdFileStart) { Path = path; Running = true; return true; }
            public void BeginFileAt(string path, long? stopwatchTimestamp) { }
            public void Stop()
            {
                if (!Running) return;
                Running = false;
                using var w = new WavWriter(Path, ChainAuditionAnalysisTests.Rate, 1);
                w.Write(Samples, Samples.Length);
                w.Finish();
            }
        }

        /// <summary>Halves the take, after waiting to be released (or cancelled, when it honours the token).</summary>
        private sealed class Blocking : IChainAuditionProcessor
        {
            public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
            public bool HonoursCancel = true;
            public float Gain = 0.5f;
            public int Calls;
            public ChainProcessing Process(float[] raw, int rate, CancellationToken cancel, Action<double>? progress)
            {
                Interlocked.Increment(ref Calls);
                Entered.Set();
                progress?.Invoke(0.25);
                // Bounded either way, so a mutation that breaks the threading fails
                // a test instead of hanging the run.
                if (HonoursCancel) Release.Wait(TimeSpan.FromSeconds(20), cancel);
                else Release.Wait(TimeSpan.FromSeconds(20));
                progress?.Invoke(1.0);
                return ChainProcessing.Rendered(raw.Select(s => s * Gain).ToArray(), 0, false, null);
            }
        }

        private (ChainAuditionTake Take, Recorder Rec, Blocking Chain) Make()
        {
            var rec = new Recorder();
            var chain = new Blocking();
            return (new ChainAuditionTake(_folder, rec, chain, ChainAuditionTakeTests.ReadMono), rec, chain);
        }

        /// <summary>A take recorded and stopped; its file is on disk; the analysis not started.</summary>
        private static string Recorded(ChainAuditionTake take, Recorder rec, DateTime at)
        {
            Assert.True(take.Start(at));
            rec.Stop();
            return rec.Path;
        }

        private string[] Files() => Directory.Exists(_folder) ? Directory.GetFiles(_folder) : Array.Empty<string>();

        [Fact]
        public void The_analysis_runs_on_a_worker_and_the_take_says_it_is_analysing_until_it_is_ready()
        {
            var (take, rec, chain) = Make();
            string path = Recorded(take, rec, Now);
            double seen = -1;
            int seenGeneration = -1;
            var run = take.AnalyseInBackground(path, (g, p) => { seenGeneration = g; seen = Math.Max(seen, p); });
            Assert.NotNull(run);
            Assert.True(chain.Entered.Wait(10000));
            Assert.Equal(AuditionTakeState.Analysing, take.State);
            Assert.False(run!.IsCompleted, "the analysis ran on the caller's thread");
            Assert.Null(take.RenderPath(AuditionRender.Raw));
            chain.Release.Set();
            var outcome = run.Result;
            Assert.False(outcome.Cancelled);
            Assert.Equal(take.Generation, outcome.Generation);
            Assert.Equal(take.Generation, seenGeneration);
            Assert.Equal(1.0, seen);
            Assert.Equal(AuditionTakeState.Ready, take.State);
            Assert.Same(take.Result, outcome.Result);
            Assert.Equal(3, Files().Count(f => f.Contains("-cleaned") || f.Contains("-raw") || f.Contains("-removed")));
        }

        [Fact]
        public void Somebody_elses_file_starts_nothing()
        {
            var (take, rec, _) = Make();
            Recorded(take, rec, Now);
            Assert.Null(take.AnalyseInBackground(Path.Combine(_folder, "someone-else.wav")));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Discard_during_a_render_leaves_the_folder_empty_and_nothing_is_written_after_it(bool honoursCancel)
        {
            var (take, rec, chain) = Make();
            chain.HonoursCancel = honoursCancel;
            var run = take.AnalyseInBackground(Recorded(take, rec, Now))!;
            Assert.True(chain.Entered.Wait(10000));
            take.Discard();
            Assert.Empty(Files());
            // A processor that ignored the cancel goes on to finish; its renders must still never be written.
            chain.Release.Set();
            var outcome = run.Result;
            Assert.True(outcome.Cancelled);
            Assert.Null(outcome.Result);
            Assert.Empty(Files());
            Assert.Equal(AuditionTakeState.Idle, take.State);
        }

        [Fact]
        public void Discard_while_a_render_is_being_written_deletes_the_part_written_and_writes_no_more()
        {
            var (take, rec, chain) = Make();
            chain.Release.Set();
            int chunks = 0;
            take.BeforeRenderChunkForTest = () => { if (Interlocked.Increment(ref chunks) == 2) take.Discard(); };
            var outcome = take.AnalyseInBackground(Recorded(take, rec, Now))!.Result;
            Assert.True(outcome.Cancelled);
            Assert.Empty(Files());
            // No piece was written after the clear: the worker stopped at the very
            // piece during which Discard ran, of a render 8 pieces long.
            Assert.Equal(2, chunks);
        }

        [Fact]
        public void Discard_after_the_renders_are_written_but_before_the_take_is_ready_wins()
        {
            var (take, rec, chain) = Make();
            chain.Release.Set();
            take.BeforeReadyForTest = () => take.Discard();
            var outcome = take.AnalyseInBackground(Recorded(take, rec, Now))!.Result;
            Assert.True(outcome.Cancelled);
            Assert.Equal(AuditionTakeState.Idle, take.State);
            Assert.Null(take.Result);
            Assert.Empty(Files());
        }

        [Fact]
        public void A_new_take_cancels_the_last_ones_render_and_its_worker_never_touches_the_new_take()
        {
            var (take, rec, chain) = Make();
            chain.HonoursCancel = false;
            var run = take.AnalyseInBackground(Recorded(take, rec, Now))!;
            Assert.True(chain.Entered.Wait(10000));
            Assert.True(take.Start(Now.AddSeconds(30)));    // the next take: the last one is discarded
            Assert.Equal(AuditionTakeState.Recording, take.State);
            chain.Release.Set();
            Assert.True(run.Result.Cancelled);
            rec.Stop();
            var files = Files();
            Assert.Single(files);
            Assert.Equal(rec.Path, files[0], ignoreCase: true);
            Assert.Equal(AuditionTakeState.Recording, take.State);   // the new take waits for its own analysis
        }

        [Fact]
        public void An_old_generations_outcome_is_marked_stale_even_when_it_completed()
        {
            var (take, rec, chain) = Make();
            chain.Release.Set();
            var first = take.AnalyseInBackground(Recorded(take, rec, Now))!.Result;
            Assert.False(first.Cancelled);
            Assert.True(take.IsCurrent(first));
            var again = take.ProcessAgain()!.Result;
            Assert.False(take.IsCurrent(first));    // a newer generation exists
            Assert.True(take.IsCurrent(again));
        }

        [Fact]
        public void Process_again_renders_the_same_take_with_the_configuration_as_it_is_now()
        {
            var (take, rec, chain) = Make();
            chain.Release.Set();
            string path = Recorded(take, rec, Now);
            var first = take.AnalyseInBackground(path)!.Result;
            Assert.True(take.Keep(Path.Combine(_folder, "kept", "take.wav")));
            byte[] takeBytes = File.ReadAllBytes(path);

            chain.Gain = 0.25f;                       // the configuration changed
            Assert.Null(take.AnalyseInBackground(path));   // not a second analysis of the recording: Process again is the door
            var again = take.ProcessAgain();
            Assert.NotNull(again);
            var outcome = again!.Result;
            Assert.False(outcome.Cancelled);
            Assert.Equal(2, chain.Calls);
            Assert.Equal(AuditionTakeState.Ready, take.State);
            Assert.True(take.Kept);                     // still the same take
            Assert.Equal(takeBytes, File.ReadAllBytes(path));   // one take, never re-recorded
            Assert.NotEqual(first.Result!.MatchGainDb, outcome.Result!.MatchGainDb);
            Assert.NotNull(take.RenderPath(AuditionRender.Cleaned));
        }

        [Fact]
        public void Process_again_needs_a_ready_take()
        {
            var (take, rec, chain) = Make();
            Assert.Null(take.ProcessAgain());                 // nothing yet
            var run = take.AnalyseInBackground(Recorded(take, rec, Now))!;
            Assert.True(chain.Entered.Wait(10000));
            Assert.Null(take.ProcessAgain());                 // still analysing
            chain.Release.Set();
            run.Wait(10000);
            Assert.NotNull(take.ProcessAgain());              // positive control
        }

        [Fact]
        public void The_window_thread_form_still_answers_at_once()
        {
            // Saved is kept for callers that want the result in hand (the earlier
            // tracks' tests); it runs the same analysis on the calling thread.
            var (take, rec, chain) = Make();
            chain.Release.Set();
            string path = Recorded(take, rec, Now);
            var result = take.Saved(path, out string trouble);
            Assert.NotNull(result);
            Assert.Equal("", trouble);
            Assert.Equal(AuditionTakeState.Ready, take.State);
        }
    }
}
