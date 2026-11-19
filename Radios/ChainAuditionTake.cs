using System;
using System.IO;
using JJPortaudio;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The audition's working folder (#262, X8 ruled by Noel 2026-11-19 07:03:
    /// the take is discarded when the window closes unless it is kept).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The operator's voice is private.</b> Takes and their renders live here,
    /// never in the recordings folder, and nothing in this folder is traced by
    /// path, uploaded, attached to a report or copied anywhere, except by Keep.
    /// It is emptied when Check your audio closes, and again the next time it
    /// opens, in case the app ended without closing it.
    /// </para>
    /// <para>
    /// Resolved from <see cref="RadioConfig.AppDataRoot"/>, like every store
    /// under the settings root, so a run under <c>JJFLEX_CONFIG_DIR</c> keeps
    /// its takes in the throwaway tree.
    /// </para>
    /// </remarks>
    public static class ChainAuditionFolder
    {
        /// <summary>The folder's name under the settings root.</summary>
        public const string FolderName = "AuditionTakes";

        /// <summary><c>%AppData%\JJFlexRadio\AuditionTakes</c>, or its throwaway twin.</summary>
        public static string DefaultPath => Path.Combine(RadioConfig.AppDataRoot, FolderName);

        /// <summary>
        /// Delete every file directly in <paramref name="folder"/>, and nothing
        /// outside it: no subfolder is entered and no other path is touched.
        /// Returns how many files went. A file that will not go (a player still
        /// holding it) is traced by count and left for the next sweep.
        /// </summary>
        public static int Clear(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
            int deleted = 0, held = 0;
            string[] files;
            try { files = Directory.GetFiles(folder); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check your audio audition: working folder not listed: " + ex.GetType().Name,
                    System.Diagnostics.TraceLevel.Warning);
                return 0;
            }
            foreach (string file in files)
            {
                try { File.Delete(file); deleted++; }
                catch (Exception) { held++; }
            }
            if (deleted > 0 || held > 0)
                Tracing.TraceLine("Check your audio audition: working folder cleared, " + deleted + " file(s) deleted"
                    + (held > 0 ? ", " + held + " still held" : ""),
                    held > 0 ? System.Diagnostics.TraceLevel.Warning : System.Diagnostics.TraceLevel.Info);
            return deleted;
        }
    }

    /// <summary>
    /// The audition's recorder, as the take needs it. The window's is
    /// <c>RecordingNarrator</c>, the one place a microphone recording can start
    /// or stop and the one voice that says so; tests use a fake, so no test
    /// opens a microphone.
    /// </summary>
    public interface IAuditionRecorder
    {
        /// <summary>True while a recording is being written to <paramref name="path"/>.</summary>
        bool IsRecordingTo(string path);

        /// <summary>True while any recording runs, this one or another surface's.</summary>
        bool IsRunning { get; }

        /// <summary>Start recording to <paramref name="path"/>; false when it refused (it says why itself).</summary>
        bool Start(string path);

        /// <summary>Stop the recording in flight.</summary>
        void Stop();
    }

    /// <summary>Where an audition take is in its life.</summary>
    public enum AuditionTakeState
    {
        /// <summary>No take, or the last one was discarded.</summary>
        Idle,
        /// <summary>The microphone is being recorded.</summary>
        Recording,
        /// <summary>The take is analysed and its renders are on disk to play.</summary>
        Ready,
    }

    /// <summary>
    /// One audition take, start to discard (#262): record this computer's
    /// microphone with NOTHING KEYED, analyse it with <see cref="ChainAudition"/>,
    /// write the three renders to the working folder, keep it on request, and
    /// throw it all away otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing here keys, and nothing here can.</b> The type holds no radio,
    /// no push-to-talk controller and no transmit chain: its only collaborators
    /// are the recorder, the processor and the file loader it is handed.
    /// </para>
    /// <para>
    /// <b>One take at a time.</b> Starting a new take discards the last one
    /// unless it was kept, so the folder never holds more than the take being
    /// heard.
    /// </para>
    /// </remarks>
    public sealed class ChainAuditionTake
    {
        private readonly string _folder;
        private readonly IAuditionRecorder _recorder;
        private readonly IChainAuditionProcessor _processor;
        private readonly Func<string, (float[] Mono, int Rate)?> _load;

        /// <param name="folder">The working folder; <see cref="ChainAuditionFolder.DefaultPath"/> in the app.</param>
        /// <param name="recorder">Records the microphone.</param>
        /// <param name="processor">The offline chain; <see cref="ChainNotConnectedYet"/> in phase A.</param>
        /// <param name="load">Reads a recorded file to mono samples and their rate, or null when it cannot.</param>
        public ChainAuditionTake(string folder, IAuditionRecorder recorder, IChainAuditionProcessor processor,
            Func<string, (float[] Mono, int Rate)?> load)
        {
            _folder = folder ?? throw new ArgumentNullException(nameof(folder));
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _load = load ?? throw new ArgumentNullException(nameof(load));
        }

        public AuditionTakeState State { get; private set; } = AuditionTakeState.Idle;

        /// <summary>The take's raw file, as recorded; empty before the first take.</summary>
        public string TakePath { get; private set; } = "";

        /// <summary>The analysis of the take, once it is <see cref="AuditionTakeState.Ready"/>.</summary>
        public ChainAuditionResult? Result { get; private set; }

        /// <summary>True once the take has been copied out by <see cref="Keep"/>.</summary>
        public bool Kept { get; private set; }

        /// <summary>The working folder.</summary>
        public string Folder => _folder;

        /// <summary>True while this take is recording.</summary>
        public bool IsRecording => State == AuditionTakeState.Recording;

        /// <summary>
        /// Start a new take: the last one is discarded (unless it was kept, in
        /// which case its copy stays where Keep put it), the folder is made, and
        /// the recorder starts. False when the recorder refused, or another
        /// recording is already running.
        /// </summary>
        public bool Start(DateTime localNow)
        {
            if (State == AuditionTakeState.Recording || _recorder.IsRunning) return false;
            Discard();
            try { Directory.CreateDirectory(_folder); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check your audio audition: working folder not created: " + ex.GetType().Name,
                    System.Diagnostics.TraceLevel.Error);
                return false;
            }
            string path = Path.Combine(_folder,
                "audition-" + localNow.ToString("yyyy-MM-dd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + ".wav");
            TakePath = path;
            Kept = false;
            if (!_recorder.Start(path))
            {
                TakePath = "";
                return false;
            }
            State = AuditionTakeState.Recording;
            Tracing.TraceLine("Check your audio audition: take started, nothing keyed", System.Diagnostics.TraceLevel.Info);
            return true;
        }

        /// <summary>Stop this take's recording. The recorder then reports the file through <see cref="Saved"/>.</summary>
        public void StopRecording()
        {
            if (State == AuditionTakeState.Recording && _recorder.IsRecordingTo(TakePath)) _recorder.Stop();
        }

        /// <summary>
        /// The recorder finished a file. Returns the analysis when the file is
        /// this take's, and null for anybody else's (the reference recorder in
        /// the same window reports through the same event), or when the file
        /// cannot be read.
        /// </summary>
        public ChainAuditionResult? Saved(string path, out string trouble)
        {
            trouble = "";
            if (State != AuditionTakeState.Recording || !SamePath(path, TakePath)) return null;
            var loaded = _load(path);
            if (loaded is not { } take || take.Mono.Length == 0)
            {
                State = AuditionTakeState.Idle;
                trouble = "unreadable";
                Tracing.TraceLine("Check your audio audition: the take could not be read", System.Diagnostics.TraceLevel.Warning);
                return null;
            }

            var result = ChainAudition.Analyse(take.Mono, take.Rate, _processor);
            foreach (var render in ChainAudition.PlayOrder)
            {
                var samples = result.Render(render);
                if (samples == null) continue;
                string renderPath = RenderPathFor(render);
                try
                {
                    using var writer = new WavWriter(renderPath, result.SampleRate, 1);
                    writer.Write(samples, samples.Length);
                    writer.Finish();
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("Check your audio audition: " + render + " render not written: " + ex.GetType().Name,
                        System.Diagnostics.TraceLevel.Error);
                    try { File.Delete(renderPath); } catch (Exception) { /* swept at close */ }
                }
            }
            Result = result;
            State = AuditionTakeState.Ready;
            Tracing.TraceLine(ChainAudition.TraceLine(result), System.Diagnostics.TraceLevel.Info);
            return result;
        }

        /// <summary>
        /// The recorder stopped. If this take was recording and no file came of
        /// it (nothing captured, a fault), it goes back to idle. Call after any
        /// <see cref="Saved"/> the same stop produced.
        /// </summary>
        public void RecorderStopped()
        {
            if (State != AuditionTakeState.Recording || _recorder.IsRecordingTo(TakePath)) return;
            State = AuditionTakeState.Idle;
            ChainAuditionFolder.Clear(_folder);
        }

        /// <summary>The file a render was written to, or null when this take has no such render on disk.</summary>
        public string? RenderPath(AuditionRender render)
        {
            if (State != AuditionTakeState.Ready || Result?.Render(render) == null) return null;
            string path = RenderPathFor(render);
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// Copy the raw take, as recorded, to <paramref name="destination"/>: the
        /// one way a take leaves the working folder. Returns false (and traces by
        /// reason, not by path) when it could not be copied.
        /// </summary>
        public bool Keep(string destination)
        {
            if (State != AuditionTakeState.Ready || Kept || string.IsNullOrWhiteSpace(destination)) return false;
            try
            {
                string? dir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(TakePath, destination, overwrite: false);
                Kept = true;
                Tracing.TraceLine("Check your audio audition: take kept", System.Diagnostics.TraceLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check your audio audition: take not kept: " + ex.GetType().Name,
                    System.Diagnostics.TraceLevel.Warning);
                return false;
            }
        }

        /// <summary>
        /// Throw the take away: stop its recording if it is still running, and
        /// empty the working folder. A kept copy is not in the folder and stays.
        /// </summary>
        public void Discard()
        {
            bool wasRecording = State == AuditionTakeState.Recording;
            // Idle FIRST, so the file the stop below reports is not analysed.
            State = AuditionTakeState.Idle;
            Result = null;
            if (wasRecording && _recorder.IsRecordingTo(TakePath)) _recorder.Stop();
            ChainAuditionFolder.Clear(_folder);
        }

        private string RenderPathFor(AuditionRender render) =>
            Path.Combine(_folder, Path.GetFileNameWithoutExtension(TakePath) + "-" + render.ToString().ToLowerInvariant() + ".wav");

        private static bool SamePath(string a, string b) =>
            a.Length > 0 && b.Length > 0
            && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
