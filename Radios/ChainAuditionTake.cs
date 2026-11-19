using System;
using System.IO;
using System.Threading.Tasks;
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

        /// <summary>
        /// Start recording to <paramref name="path"/>, SAYING NOTHING: the take
        /// sounds its own start marker once this returns, and nothing may be
        /// spoken into the take. False when it refused (it says why itself,
        /// before anything was captured).
        /// </summary>
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
    /// <para>
    /// <b>It starts the way the computer microphone check starts</b> (Sprint 49
    /// Track Q2, Noel 2026-11-19 12:22: <i>"start recording after speech is
    /// done, start it with a beep or count it down like we've done in all the
    /// other stuff"</i>). <see cref="StartAfterAsync"/> opens nothing until the
    /// countdown has been said in full; the recorder then opens saying nothing,
    /// and the start marker (the recorder's start tone, the one the computer
    /// microphone check sounds) plays as soon as it is open. The microphone may
    /// hear that tone through the speakers, so the take's first
    /// <c>markerSettle</c> is left out of everything it is measured and played
    /// as, the way that check starts its levels again once the tone has
    /// finished.
    /// </para>
    /// </remarks>
    public sealed class ChainAuditionTake
    {
        private readonly string _folder;
        private readonly IAuditionRecorder _recorder;
        private readonly IChainAuditionProcessor _processor;
        private readonly Func<string, (float[] Mono, int Rate)?> _load;
        private readonly Action? _startMarker;
        private readonly TimeSpan _markerSettle;

        /// <summary>True when this take's start marker sounded, so its settle is left out of the take.</summary>
        private bool _marked;

        /// <param name="folder">The working folder; <see cref="ChainAuditionFolder.DefaultPath"/> in the app.</param>
        /// <param name="recorder">Records the microphone, saying nothing.</param>
        /// <param name="processor">The offline chain; <see cref="ChainNotConnectedYet"/> in phase A.</param>
        /// <param name="load">Reads a recorded file to mono samples and their rate, or null when it cannot.</param>
        /// <param name="startMarker">
        /// Sounded once the recorder is open: in the window, the recorder's
        /// start tone, the computer microphone check's own. Null only in tests
        /// that are not about the start.
        /// </param>
        /// <param name="markerSettle">
        /// How long after the marker the take is listened to from: the window
        /// passes the computer microphone check's own settle. Ignored without a
        /// marker.
        /// </param>
        public ChainAuditionTake(string folder, IAuditionRecorder recorder, IChainAuditionProcessor processor,
            Func<string, (float[] Mono, int Rate)?> load, Action? startMarker = null, TimeSpan markerSettle = default)
        {
            _folder = folder ?? throw new ArgumentNullException(nameof(folder));
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _load = load ?? throw new ArgumentNullException(nameof(load));
            _startMarker = startMarker;
            _markerSettle = markerSettle < TimeSpan.Zero ? TimeSpan.Zero : markerSettle;
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
            _marked = false;
            if (!_recorder.Start(path))
            {
                TakePath = "";
                return false;
            }
            State = AuditionTakeState.Recording;
            // The capture is open: mark it, at once, with the check's start tone.
            _marked = SoundStartMarker();
            Tracing.TraceLine("Check your audio audition: take started, nothing keyed"
                + (_marked ? ", start tone sounded" : ""), System.Diagnostics.TraceLevel.Info);
            return true;
        }

        /// <summary>
        /// Start a take once the countdown before it has been said in full, the
        /// way every Check your audio method starts (Noel 2026-11-19 12:22).
        /// Nothing is opened until <paramref name="countSaid"/> completes true:
        /// in the window that is the countdown's own gate, which waits for its
        /// last spoken word to be finished, and is true at once with the
        /// countdown off. False, with nothing opened, when the count was called
        /// off, its words could not be delivered, or the start is no longer
        /// wanted.
        /// </summary>
        /// <param name="countSaid">The countdown's gate: true once it has been said and has ended.</param>
        /// <param name="stillWanted">Asked after the count: false when the start was cancelled or the window is closing.</param>
        /// <param name="localNow">The clock the take's file is named by, read as capture opens.</param>
        /// <param name="beforeOpening">
        /// Run just before the recorder opens, in the same synchronous step as
        /// the open, so nothing can start sounding between the two. The window
        /// stops, saying nothing, everything of its own still sounding (Track Q2
        /// repair 2), and answers true only when nothing is. False opens nothing.
        /// </param>
        public async Task<bool> StartAfterAsync(Func<Task<bool>> countSaid, Func<bool> stillWanted,
            Func<DateTime> localNow, Func<bool>? beforeOpening = null)
        {
            if (countSaid == null) throw new ArgumentNullException(nameof(countSaid));
            if (stillWanted == null) throw new ArgumentNullException(nameof(stillWanted));
            if (localNow == null) throw new ArgumentNullException(nameof(localNow));
            if (!await countSaid() || !stillWanted()) return false;
            if (beforeOpening != null && !beforeOpening()) return false;
            return Start(localNow());
        }

        /// <summary>Sound the start marker, guarded: a tone that throws must never cost the take. True when it was asked to sound.</summary>
        private bool SoundStartMarker()
        {
            if (_startMarker == null) return false;
            try { _startMarker(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check your audio audition: the start tone threw and was ignored: " + ex.GetType().Name,
                    System.Diagnostics.TraceLevel.Warning);
            }
            return true;
        }

        /// <summary>
        /// The take as it is measured and played: what was recorded, less the
        /// marker's settle at its start when the marker sounded. Never empty
        /// when the recording was not (a take no longer than the settle keeps
        /// its last sample), so a press-and-release take reads as too short,
        /// not as unreadable.
        /// </summary>
        internal static float[] AfterMarker(float[] recorded, int rate, TimeSpan settle, bool marked)
        {
            if (!marked || settle <= TimeSpan.Zero || recorded.Length == 0) return recorded;
            long skip = (long)Math.Round(settle.TotalSeconds * Math.Max(1, rate));
            if (skip <= 0) return recorded;
            int from = (int)Math.Min(skip, recorded.Length - 1L);
            var kept = new float[recorded.Length - from];
            Array.Copy(recorded, from, kept, 0, kept.Length);
            return kept;
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

            // The start tone and the moment after it are not the operator: they
            // are left out, as the computer microphone check starts its levels
            // again once its tone has finished.
            var heard = AfterMarker(take.Mono, take.Rate, _markerSettle, _marked);
            var result = ChainAudition.Analyse(heard, take.Rate, _processor);
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
