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
        /// spoken into the take. The microphone opens now, but the FILE begins
        /// <paramref name="fileStartsAfter"/> later: what is captured before
        /// then is never written (Track Q3). False when it refused (it says why
        /// itself, before anything was captured).
        /// </summary>
        bool Start(string path, TimeSpan fileStartsAfter);

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
    /// microphone check sounds) plays as soon as it is open.
    /// </para>
    /// <para>
    /// <b>The order, every take (Track Q3, Noel 2026-11-19 20:26 and 20:29):</b>
    /// the countdown if it is on, then "Recording" said in full, then the start
    /// tone unless the recorder beeps are off, then the take. The microphone
    /// opens as the tone begins, so a slow device loses nothing, but the FILE
    /// begins only once the tone has finished (<c>markerSettle</c> after it
    /// opens); with no tone, it begins as soon as the microphone opens. So the
    /// file is the take: nothing is trimmed afterwards, and what is measured,
    /// what is played and what Keep saves are the same samples.
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
        private readonly Func<bool> _markerSounds;

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
        /// How long after the microphone opens the file begins when the marker
        /// sounds: the window passes the computer microphone check's own
        /// settle, its measure of the tone having finished. Ignored when no
        /// marker sounds.
        /// </param>
        /// <param name="markerSounds">
        /// Asked as each take starts: whether the marker will be heard. In the
        /// window, whether the recorder beeps are on (the start tone plays only
        /// then). Null means it always sounds when there is one.
        /// </param>
        public ChainAuditionTake(string folder, IAuditionRecorder recorder, IChainAuditionProcessor processor,
            Func<string, (float[] Mono, int Rate)?> load, Action? startMarker = null, TimeSpan markerSettle = default,
            Func<bool>? markerSounds = null)
        {
            _folder = folder ?? throw new ArgumentNullException(nameof(folder));
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _load = load ?? throw new ArgumentNullException(nameof(load));
            _startMarker = startMarker;
            _markerSettle = markerSettle < TimeSpan.Zero ? TimeSpan.Zero : markerSettle;
            _markerSounds = markerSounds ?? (() => true);
        }

        public AuditionTakeState State { get; private set; } = AuditionTakeState.Idle;

        /// <summary>The take's file, as recorded: it begins after the start tone, so it is exactly what is measured and played. Empty before the first take.</summary>
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
            // Decided before the microphone opens, so the recorder knows where
            // the file begins: after the tone when there is one, at once when not.
            bool marking = _startMarker != null && MarkerWillSound();
            TimeSpan fileStartsAfter = marking ? _markerSettle : TimeSpan.Zero;
            if (!_recorder.Start(path, fileStartsAfter))
            {
                TakePath = "";
                return false;
            }
            State = AuditionTakeState.Recording;
            // The capture is open: mark it, at once, with the check's start tone.
            if (marking) SoundStartMarker();
            Tracing.TraceLine("Check your audio audition: take started, nothing keyed"
                + (marking ? ", start tone sounded, the file starting " + (long)fileStartsAfter.TotalMilliseconds + " ms in"
                           : ", no start tone, the file starting at once"),
                System.Diagnostics.TraceLevel.Info);
            return true;
        }

        /// <summary>
        /// Start a take once the countdown before it has been said in full, the
        /// way every Check your audio method starts (Noel 2026-11-19 12:22).
        /// Nothing is opened until <paramref name="countSaid"/> completes true:
        /// in the window that is the countdown's own gate, which waits for its
        /// last spoken word to be finished, and is true at once with the
        /// countdown off. Then <paramref name="startSaid"/>, "Recording", is said
        /// in full (Track Q3, Noel 2026-11-19 20:26), and only then does the
        /// microphone open. False, with nothing opened, when the count was
        /// called off, its words could not be delivered, "Recording" could not
        /// be said where it was the only warning, or the start is no longer
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
        /// <param name="startSaid">
        /// Says "Recording" and completes once it has been said in full: true to
        /// open, false to open nothing. Run after the count and before
        /// <paramref name="beforeOpening"/>. Null says nothing (tests that are
        /// not about the start).
        /// </param>
        public async Task<bool> StartAfterAsync(Func<Task<bool>> countSaid, Func<bool> stillWanted,
            Func<DateTime> localNow, Func<bool>? beforeOpening = null, Func<Task<bool>>? startSaid = null)
        {
            if (countSaid == null) throw new ArgumentNullException(nameof(countSaid));
            if (stillWanted == null) throw new ArgumentNullException(nameof(stillWanted));
            if (localNow == null) throw new ArgumentNullException(nameof(localNow));
            if (!await countSaid() || !stillWanted()) return false;
            if (startSaid != null && (!await startSaid() || !stillWanted())) return false;
            if (beforeOpening != null && !beforeOpening()) return false;
            return Start(localNow());
        }

        /// <summary>Whether the marker will be heard, guarded: a question that throws reads as "no tone", so the file starts at once and nothing of the take is lost.</summary>
        private bool MarkerWillSound()
        {
            try { return _markerSounds(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check your audio audition: asking whether the start tone sounds threw: " + ex.GetType().Name,
                    System.Diagnostics.TraceLevel.Warning);
                return false;
            }
        }

        /// <summary>Sound the start marker, guarded: a tone that throws must never cost the take.</summary>
        private void SoundStartMarker()
        {
            if (_startMarker == null) return;
            try { _startMarker(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check your audio audition: the start tone threw and was ignored: " + ex.GetType().Name,
                    System.Diagnostics.TraceLevel.Warning);
            }
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

            // The file began after the start tone (Track Q3), so it is the take
            // as it is: nothing is trimmed, and Keep saves these same samples.
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
        /// Copy the take's file to <paramref name="destination"/>: the one way a
        /// take leaves the working folder. It is exactly what was measured and
        /// played, because the file began after the start tone (Track Q3, Noel
        /// 2026-11-19 20:29: <i>"Keep saves exactly what you heard"</i>). Returns
        /// false (and traces by reason, not by path) when it could not be copied.
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
