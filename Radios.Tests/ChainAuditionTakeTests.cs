using System;
using System.IO;
using System.Linq;
using JJPortaudio;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// One audition take, start to discard (#262): recorded with nothing keyed
    /// into its own working folder, analysed, rendered, kept only on request,
    /// and thrown away otherwise (X8, ruled by Noel 2026-11-19 07:03).
    /// </summary>
    /// <remarks>
    /// The recorder is a fake that writes the synthetic take to the path it was
    /// given when stopped, the way <c>RecordingNarrator</c> reports a file; no
    /// microphone is opened. Every folder is a fresh temporary one.
    /// <b>What these cannot see:</b> the narrator's own words and earcons, and
    /// the window's buttons; those are the harness walk's.
    /// </remarks>
    // In the collection because one test reads RadioConfig.AppDataRoot.
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ChainAuditionTakeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-audition-" + Guid.NewGuid().ToString("N"));
        private string Folder => Path.Combine(_root, ChainAuditionFolder.FolderName);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>Records by writing <see cref="Samples"/> on Stop, then reports the file, as the narrator does.</summary>
        private sealed class FakeRecorder : IAuditionRecorder
        {
            public float[] Samples = ChainAuditionAnalysisTests.Take(8);
            public string Path = "";
            public bool Running;
            public bool OtherRecording;
            public bool Refuse;
            public bool CaptureNothing;
            public int Starts;
            public Action<string>? Saved;
            public Action? Stopped;

            public bool IsRunning => Running || OtherRecording;
            public bool IsRecordingTo(string path) => Running && string.Equals(Path, path, StringComparison.OrdinalIgnoreCase);

            public bool Start(string path, bool holdFileStart)
            {
                Starts++;
                if (Refuse) return false;
                Path = path;
                Running = true;
                return true;
            }

            public void BeginFileAt(string path, long? stopwatchTimestamp) { }

            public void Stop()
            {
                if (!Running) return;
                Running = false;
                if (!CaptureNothing)
                {
                    using (var w = new WavWriter(Path, ChainAuditionAnalysisTests.Rate, 1))
                    {
                        w.Write(Samples, Samples.Length);
                        w.Finish();
                    }
                    Saved?.Invoke(Path);
                }
                Stopped?.Invoke();
            }
        }

        private (ChainAuditionTake Take, FakeRecorder Recorder) Make(IChainAuditionProcessor? processor = null)
        {
            var recorder = new FakeRecorder();
            var take = new ChainAuditionTake(Folder, recorder, processor ?? ChainNotConnectedYet.Instance, ReadMono);
            recorder.Saved = path => take.Saved(path, out _);
            recorder.Stopped = take.RecorderStopped;
            return (take, recorder);
        }

        private static readonly DateTime Now = new(2026, 11, 19, 9, 30, 15, 250);

        [Fact]
        public void The_working_folder_is_under_the_settings_root_and_not_the_recordings_folder()
        {
            Assert.Equal(Path.Combine(RadioConfig.AppDataRoot, "AuditionTakes"), ChainAuditionFolder.DefaultPath);
            Assert.NotEqual("Recordings", ChainAuditionFolder.FolderName);
        }

        [Fact]
        public void A_take_records_into_the_working_folder_and_is_analysed_when_it_stops()
        {
            var (take, rec) = Make();
            Assert.True(take.Start(Now));
            Assert.Equal(AuditionTakeState.Recording, take.State);
            Assert.Equal(Folder, Path.GetDirectoryName(take.TakePath));
            Assert.Equal(take.TakePath, rec.Path);

            take.StopRecording();
            Assert.Equal(AuditionTakeState.Ready, take.State);
            Assert.Equal(AuditionSpeech.Heard, take.Result!.Speech);
            // Phase A: the raw render only, beside the take itself.
            Assert.NotNull(take.RenderPath(AuditionRender.Raw));
            Assert.Null(take.RenderPath(AuditionRender.Cleaned));
            Assert.Null(take.RenderPath(AuditionRender.Removed));
            Assert.Equal(2, Directory.GetFiles(Folder).Length);
        }

        [Fact]
        public void With_a_chain_all_three_renders_are_on_disk_to_play()
        {
            var (take, _) = Make(new ChainAuditionAnalysisTests.FakeChain(ChainAuditionAnalysisTests.GoodGate));
            Assert.True(take.Start(Now));
            take.StopRecording();
            foreach (var render in ChainAudition.PlayOrder)
            {
                string? path = take.RenderPath(render);
                Assert.NotNull(path);
                Assert.Equal(Folder, Path.GetDirectoryName(path));
                Assert.Equal(take.Result!.Render(render)!.Length, ReadMono(path!)!.Value.Mono.Length);
            }
        }

        [Fact]
        public void Another_recording_running_refuses_the_start_without_starting_anything()
        {
            var (take, rec) = Make();
            rec.OtherRecording = true;
            Assert.False(take.Start(Now));
            Assert.Equal(0, rec.Starts);
            Assert.Equal(AuditionTakeState.Idle, take.State);
        }

        [Fact]
        public void A_refused_start_leaves_no_take()
        {
            var (take, rec) = Make();
            rec.Refuse = true;
            Assert.False(take.Start(Now));
            Assert.Equal(AuditionTakeState.Idle, take.State);
            Assert.Equal("", take.TakePath);
        }

        [Fact]
        public void Someone_elses_recording_is_not_the_audition_take()
        {
            var (take, _) = Make();
            Assert.True(take.Start(Now));
            Assert.Null(take.Saved(Path.Combine(_root, "Recordings", "take-reference.wav"), out string trouble));
            Assert.Equal("", trouble);
            Assert.Equal(AuditionTakeState.Recording, take.State);
        }

        [Fact]
        public void A_stop_that_captured_nothing_goes_back_to_idle()
        {
            var (take, rec) = Make();
            rec.CaptureNothing = true;
            Assert.True(take.Start(Now));
            take.StopRecording();
            Assert.Equal(AuditionTakeState.Idle, take.State);
            Assert.Null(take.Result);
        }

        [Fact]
        public void Keep_copies_the_raw_take_as_recorded_and_discard_then_empties_the_folder()
        {
            var (take, _) = Make();
            Assert.True(take.Start(Now));
            take.StopRecording();
            string kept = Path.Combine(_root, "Recordings", "take-2026-11-19-093015.wav");
            Assert.True(take.Keep(kept));
            Assert.True(take.Kept);
            Assert.False(take.Keep(kept + ".again"));   // once
            Assert.Equal(File.ReadAllBytes(take.TakePath), File.ReadAllBytes(kept));

            take.Discard();
            Assert.Empty(Directory.GetFiles(Folder));
            Assert.True(File.Exists(kept));   // the kept copy is not in the working folder
            Assert.Equal(AuditionTakeState.Idle, take.State);
        }

        [Fact]
        public void Discard_without_keep_leaves_nothing_anywhere()
        {
            var (take, _) = Make();
            Assert.True(take.Start(Now));
            take.StopRecording();
            take.Discard();
            Assert.Empty(Directory.GetFiles(Folder));
            Assert.False(Directory.Exists(Path.Combine(_root, "Recordings")));
        }

        [Fact]
        public void Discarding_while_recording_stops_the_recorder_and_does_not_analyse_what_it_wrote()
        {
            var (take, rec) = Make();
            Assert.True(take.Start(Now));
            take.Discard();
            Assert.False(rec.Running);
            Assert.Null(take.Result);
            Assert.Equal(AuditionTakeState.Idle, take.State);
            Assert.Empty(Directory.GetFiles(Folder));
        }

        [Fact]
        public void A_new_take_throws_the_last_unkept_one_away()
        {
            var (take, _) = Make();
            Assert.True(take.Start(Now));
            take.StopRecording();
            string first = take.TakePath;
            Assert.True(take.Start(Now.AddSeconds(5)));
            Assert.False(File.Exists(first));
            Assert.Null(take.Result);
        }

        [Fact]
        public void Clearing_touches_only_files_directly_in_the_working_folder()
        {
            Directory.CreateDirectory(Path.Combine(Folder, "inner"));
            File.WriteAllText(Path.Combine(Folder, "audition-1.wav"), "x");
            File.WriteAllText(Path.Combine(Folder, "inner", "keep.txt"), "x");
            File.WriteAllText(Path.Combine(_root, "beside.wav"), "x");
            Assert.Equal(1, ChainAuditionFolder.Clear(Folder));
            Assert.True(File.Exists(Path.Combine(Folder, "inner", "keep.txt")));
            Assert.True(File.Exists(Path.Combine(_root, "beside.wav")));
            Assert.Equal(0, ChainAuditionFolder.Clear(Path.Combine(_root, "missing")));
        }

        [Fact]
        public void The_take_holds_nothing_that_can_key_a_radio()
        {
            // Phase A's proof that the audition keys nothing, at the type that
            // owns the take: no radio, no push-to-talk controller, no session,
            // no transmit chain. The window's side is pinned in
            // CheckYourAudioAuditionWiringTests.
            string src = File.ReadAllText(Path.Combine(LeaderSourceScan.RepoRoot(), "Radios", "ChainAuditionTake.cs"));
            foreach (string forbidden in new[] { "PttSafetyController", "FlexBase", ".Transmit", "TxAudioConditioning", "TxNoiseChain", "Mox" })
                Assert.DoesNotContain(forbidden, src);
        }

        /// <summary>A 16-bit PCM WAV, as <see cref="WavWriter"/> writes it, back to mono samples.</summary>
        internal static (float[] Mono, int Rate)? ReadMono(string path)
        {
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path));
            r.ReadBytes(12);
            int rate = 0, channels = 1;
            while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
            {
                string id = new string(r.ReadChars(4));
                int size = r.ReadInt32();
                if (id == "fmt ")
                {
                    r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32();
                    r.ReadBytes(size - 8);
                }
                else if (id == "data")
                {
                    int frames = size / (2 * channels);
                    var mono = new float[frames];
                    for (int i = 0; i < frames; i++)
                    {
                        mono[i] = r.ReadInt16() / 32768f;
                        for (int c = 1; c < channels; c++) r.ReadInt16();
                    }
                    return (mono, rate);
                }
                else r.ReadBytes(size);
            }
            return null;
        }
    }
}
