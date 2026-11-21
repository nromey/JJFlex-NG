using System;
using System.IO;
using System.Threading;
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
        /// spoken into the take. The microphone opens now. With
        /// <paramref name="holdFileStart"/>, the FILE waits for
        /// <see cref="BeginFileAt"/>: nothing captured before the moment it is
        /// given is ever written (Track Q3 repair 1). False when it refused (it
        /// says why itself, before anything was captured).
        /// </summary>
        bool Start(string path, bool holdFileStart);

        /// <summary>
        /// For the recording to <paramref name="path"/> started with a held
        /// file start: the file begins at the frame the microphone captured at
        /// <paramref name="stopwatchTimestamp"/> (on the
        /// <see cref="System.Diagnostics.Stopwatch"/> clock), or, when null, at
        /// the first frame captured. Any thread; ignored for any other recording.
        /// </summary>
        void BeginFileAt(string path, long? stopwatchTimestamp);

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
        /// <summary>
        /// The take is recorded and being analysed on a worker (Sprint 49 Track Q
        /// phase B, item 4): its renders are not on disk yet, so nothing plays and
        /// nothing can be kept until it is <see cref="Ready"/>.
        /// </summary>
        Analysing,
    }

    /// <summary>
    /// How one analysis of a take ended (Track Q phase B, item 4). The window
    /// shows or says it only when <see cref="ChainAuditionTake.IsCurrent"/>.
    /// </summary>
    public sealed class AuditionAnalysisOutcome
    {
        /// <summary>The take's generation the analysis ran under.</summary>
        public int Generation { get; init; }

        /// <summary>The analysis, or null when it was cancelled or failed.</summary>
        public ChainAuditionResult? Result { get; init; }

        /// <summary>"" when it ran; "unreadable" when the take could not be read back; "failed" when the analysis itself threw.</summary>
        public string Trouble { get; init; } = "";

        /// <summary>A new take, Discard, or the window closing cancelled it: nothing of it is shown, said or kept on disk.</summary>
        public bool Cancelled { get; init; }
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
    /// begins only once the tone has finished; with no tone, it begins as soon
    /// as the microphone opens. So the file is the take: nothing is trimmed
    /// afterwards, and what is measured, what is played and what Keep saves are
    /// the same samples.
    /// </para>
    /// <para>
    /// <b>"Finished" is the tone's real end, not a fixed time (repair 1;
    /// Codex's check of 83932446, C1).</b> Track Q3 cut a fixed 450 ms of
    /// capture. The tone is about 160 ms and reaches the speaker after whatever
    /// the alert output holds ahead of it (90 ms by default, up to 800 ms by the
    /// operator's setting), so with a fast output about 200 ms of the operator's
    /// first words were lost and with a slow one the tone was in the file. The
    /// marker now reports the moment the tone is audibly over, read from the
    /// output's own play position; the file begins <see cref="ToneMarginMs"/>
    /// after that moment, at the frame the microphone captured then.
    /// </para>
    /// </remarks>
    public sealed class ChainAuditionTake
    {
        // Track Q phase B, item 4: the analysis runs on a worker under a
        // generation number. Every change of state, the folder's clear and every
        // chunk a worker writes are made under this one lock, so once Discard
        // has cleared the folder no worker can write into it again.
        private readonly object _sync = new();
        private int _generation;
        private CancellationTokenSource? _cancel;
        private readonly string _folder;
        private readonly IAuditionRecorder _recorder;
        private readonly IChainAuditionProcessor _processor;
        private readonly Func<string, (float[] Mono, int Rate)?> _load;
        private readonly Func<Task<long?>>? _startMarker;
        private readonly Func<bool> _markerSounds;

        /// <summary>
        /// How long after the tone is audibly over the file begins, in
        /// milliseconds. It covers what the tone's reported end cannot see: the
        /// Windows audio engine and driver after the alert output's own buffers,
        /// the room's echo of the last pip reaching the microphone, and the
        /// slack in placing that moment on the capture. A person answers a tone
        /// in no less than about 200 ms, so this costs none of what they say
        /// after it (Track Q3 repair 1).
        /// </summary>
        public const int ToneMarginMs = 60;

        /// <summary>
        /// When the marker could not say when its tone ended (its report
        /// faulted), the file begins this long after it was sounded: the
        /// longest the tone could take, about 160 ms behind the longest alert
        /// output, 800 ms, with room to spare. Losing a little of the take is
        /// the price of keeping the tone out of it.
        /// </summary>
        public const int UnknownToneEndMs = 1200;

        /// <summary>The moment the file begins for a tone audibly over at <paramref name="audibleEnd"/>, both on the Stopwatch clock.</summary>
        public static long FileStartAfterTone(long audibleEnd) =>
            audibleEnd + ToneMarginMs * System.Diagnostics.Stopwatch.Frequency / 1000;

        /// <param name="folder">The working folder; <see cref="ChainAuditionFolder.DefaultPath"/> in the app.</param>
        /// <param name="recorder">Records the microphone, saying nothing.</param>
        /// <param name="processor">The offline chain; <see cref="ChainNotConnectedYet"/> in phase A.</param>
        /// <param name="load">Reads a recorded file to mono samples and their rate, or null when it cannot.</param>
        /// <param name="startMarker">
        /// Sounded once the recorder is open: in the window, the recorder's
        /// start tone, the computer microphone check's own. It completes with
        /// the moment its tone is audibly over, on the
        /// <see cref="System.Diagnostics.Stopwatch"/> clock, or with null when
        /// no tone sounded, including one the output never took and that was
        /// withdrawn so it can never sound (Track Q3 repair 2): the file then
        /// begins at its first frame. Null only in tests that are not about the start.
        /// </param>
        /// <param name="markerSounds">
        /// Asked as each take starts: whether the marker will be heard. In the
        /// window, whether the recorder beeps are on (the start tone plays only
        /// then). Null means it always sounds when there is one.
        /// </param>
        public ChainAuditionTake(string folder, IAuditionRecorder recorder, IChainAuditionProcessor processor,
            Func<string, (float[] Mono, int Rate)?> load, Func<Task<long?>>? startMarker = null,
            Func<bool>? markerSounds = null)
        {
            _folder = folder ?? throw new ArgumentNullException(nameof(folder));
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _load = load ?? throw new ArgumentNullException(nameof(load));
            _startMarker = startMarker;
            _markerSounds = markerSounds ?? (() => true);
        }

        private AuditionTakeState _state = AuditionTakeState.Idle;

        public AuditionTakeState State
        {
            get { lock (_sync) return _state; }
            private set { lock (_sync) _state = value; }
        }

        /// <summary>
        /// Counts the analyses started and the takes thrown away (Track Q phase B,
        /// item 4). An outcome whose generation is not this one belongs to work a
        /// newer take, Discard or Process again has superseded.
        /// </summary>
        public int Generation { get { lock (_sync) return _generation; } }

        /// <summary>True when <paramref name="outcome"/> is the newest analysis and was not cancelled: the only kind the window may show or say.</summary>
        public bool IsCurrent(AuditionAnalysisOutcome outcome)
        {
            lock (_sync) return outcome != null && !outcome.Cancelled && outcome.Generation == _generation;
        }

        /// <summary>True while the take is being analysed.</summary>
        public bool IsAnalysing => State == AuditionTakeState.Analysing;

        /// <summary>
        /// How many samples a render is written in at a time, each piece under the
        /// lock with a check that it is still wanted: one second at 48 kHz, so a
        /// Discard waits for at most one piece, and nothing is written after it.
        /// </summary>
        internal const int RenderChunkSamples = 48000;

        /// <summary>An outcome's <see cref="AuditionAnalysisOutcome.Trouble"/> when the take could not be read back.</summary>
        public const string TroubleUnreadable = "unreadable";

        /// <summary>An outcome's <see cref="AuditionAnalysisOutcome.Trouble"/> when the analysis itself threw.</summary>
        public const string TroubleFailed = "failed";

        /// <summary>Tests only: run on the worker before each piece of a render is written, outside the lock.</summary>
        internal Action? BeforeRenderChunkForTest { get; set; }

        /// <summary>Tests only: run on the worker after the renders are written and before the take is marked ready, outside the lock.</summary>
        internal Action? BeforeReadyForTest { get; set; }

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
            // A take still analysing is thrown away like any other: its worker is
            // cancelled (Track Q phase B, item 4).
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
            // Decided before the microphone opens, so the recorder knows whether
            // to hold its file for the tone's end, or begin it at once.
            bool marking = _startMarker != null && MarkerWillSound();
            if (!_recorder.Start(path, holdFileStart: marking))
            {
                TakePath = "";
                return false;
            }
            State = AuditionTakeState.Recording;
            Tracing.TraceLine("Check your audio audition: take started, nothing keyed"
                + (marking ? ", start tone sounding, the file waiting for its end"
                           : ", no start tone, the file starting at once"),
                System.Diagnostics.TraceLevel.Info);
            // The capture is open: mark it, at once, with the check's start
            // tone, and begin the file once the tone is audibly over.
            if (marking) SoundStartMarker(path);
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

        /// <summary>
        /// Sound the start marker, guarded, and tell the recorder where the
        /// file begins once the marker says when its tone is audibly over. A
        /// marker that throws must never cost the take: nothing sounded, so the
        /// file begins at the first frame. One whose report faults leaves the
        /// file beginning <see cref="UnknownToneEndMs"/> after it was sounded.
        /// </summary>
        private void SoundStartMarker(string path)
        {
            if (_startMarker == null) return;
            long asked = System.Diagnostics.Stopwatch.GetTimestamp();
            Task<long?>? end;
            try { end = _startMarker(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check your audio audition: the start tone threw and was ignored, so the file starts at once: "
                    + ex.GetType().Name, System.Diagnostics.TraceLevel.Warning);
                BeginFileAt(path, null);
                return;
            }
            if (end == null) { BeginFileAt(path, null); return; }
            if (end.IsCompleted) FileStartFrom(path, end, asked);
            else end.ContinueWith(t => FileStartFrom(path, t, asked), TaskScheduler.Default);
        }

        private void FileStartFrom(string path, Task<long?> end, long asked)
        {
            if (end.Status == TaskStatus.RanToCompletion)
            {
                if (end.Result is long audibleEnd) BeginFileAt(path, FileStartAfterTone(audibleEnd));
                else
                {
                    Tracing.TraceLine("Check your audio audition: no start tone sounded after all (or it was withdrawn unheard), so the file starts at once",
                        System.Diagnostics.TraceLevel.Info);
                    BeginFileAt(path, null);
                }
                return;
            }
            Tracing.TraceLine("Check your audio audition: the start tone could not say when it ended, so the file starts "
                + UnknownToneEndMs + " ms after it sounded", System.Diagnostics.TraceLevel.Warning);
            BeginFileAt(path, asked + UnknownToneEndMs * System.Diagnostics.Stopwatch.Frequency / 1000);
        }

        private void BeginFileAt(string path, long? stopwatchTimestamp)
        {
            try { _recorder.BeginFileAt(path, stopwatchTimestamp); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check your audio audition: telling the recorder where the file begins threw: " + ex.GetType().Name,
                    System.Diagnostics.TraceLevel.Error);
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
            int generation;
            string take;
            lock (_sync)
            {
                if (_state != AuditionTakeState.Recording || !SamePath(path, TakePath)) return null;
                generation = BeginLocked(out _);
                take = TakePath;
            }
            // The calling thread's form: the same analysis, in hand at once.
            var outcome = Run(take, generation, CancellationToken.None, null);
            trouble = outcome.Trouble;
            return outcome.Result;
        }

        /// <summary>
        /// The recorder finished a file: when it is this take's, analyse it ON A
        /// WORKER (Track Q phase B, item 4) and return at once. The take is
        /// <see cref="AuditionTakeState.Analysing"/> until the task completes;
        /// <paramref name="progress"/> is told the generation and the fraction
        /// done, on the worker. Null for anybody else's file (the reference
        /// recorder reports through the same event). The task never faults: its
        /// outcome says how it ended.
        /// </summary>
        public Task<AuditionAnalysisOutcome>? AnalyseInBackground(string path, Action<int, double>? progress = null)
        {
            lock (_sync)
            {
                if (_state != AuditionTakeState.Recording || !SamePath(path, TakePath)) return null;
                return StartWorkerLocked(progress);
            }
        }

        /// <summary>
        /// Process the take already in the working folder again, through the
        /// configuration as it is NOW, with no new recording (Track Q phase B,
        /// item 9; Noel's S5, 2026-11-20). One take, processed again: #262's
        /// one-take rule. Its old renders are removed first (the window stops
        /// playing them before it asks). Null unless the take is ready.
        /// </summary>
        public Task<AuditionAnalysisOutcome>? ProcessAgain(Action<int, double>? progress = null)
        {
            lock (_sync)
            {
                if (_state != AuditionTakeState.Ready || TakePath.Length == 0 || !File.Exists(TakePath)) return null;
                foreach (var render in ChainAudition.PlayOrder)
                {
                    try { File.Delete(RenderPathFor(TakePath, render)); }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine("Check your audio audition: an old " + render + " render could not be removed: " + ex.GetType().Name,
                            System.Diagnostics.TraceLevel.Warning);
                    }
                }
                Tracing.TraceLine("Check your audio audition: processing the take again", System.Diagnostics.TraceLevel.Info);
                return StartWorkerLocked(progress);
            }
        }

        /// <summary>Under the lock: a new generation, the last analysis cancelled, the take analysing.</summary>
        private int BeginLocked(out CancellationToken token)
        {
            _cancel?.Cancel();
            var cts = new CancellationTokenSource();
            _cancel = cts;
            token = cts.Token;
            _state = AuditionTakeState.Analysing;
            Result = null;
            return ++_generation;
        }

        private Task<AuditionAnalysisOutcome> StartWorkerLocked(Action<int, double>? progress)
        {
            int generation = BeginLocked(out var token);
            string take = TakePath;
            return Task.Run(() => Run(take, generation, token, progress));
        }

        /// <summary>True when work of <paramref name="generation"/> must stop: superseded or cancelled. Under the lock.</summary>
        private bool StaleLocked(int generation, CancellationToken token) => generation != _generation || token.IsCancellationRequested;

        private AuditionAnalysisOutcome Cancelled(int generation)
        {
            Tracing.TraceLine("Check your audio audition: an analysis was cancelled (a new take, Discard, or the window closing)",
                System.Diagnostics.TraceLevel.Info);
            return new AuditionAnalysisOutcome { Generation = generation, Cancelled = true };
        }

        /// <summary>One analysis, start to finish, on whichever thread runs it. Never throws.</summary>
        private AuditionAnalysisOutcome Run(string takePath, int generation, CancellationToken token, Action<int, double>? progress)
        {
            try
            {
                var loaded = _load(takePath);
                if (loaded is not { } take || take.Mono.Length == 0)
                {
                    lock (_sync)
                    {
                        if (StaleLocked(generation, token)) return Cancelled(generation);
                        _state = AuditionTakeState.Idle;
                    }
                    Tracing.TraceLine("Check your audio audition: the take could not be read", System.Diagnostics.TraceLevel.Warning);
                    return new AuditionAnalysisOutcome { Generation = generation, Trouble = TroubleUnreadable };
                }

                // The file began after the start tone (Track Q3), so it is the take
                // as it is: nothing is trimmed, and Keep saves these same samples.
                var result = ChainAudition.Analyse(take.Mono, take.Rate, _processor, token,
                    progress == null ? null : p => progress(generation, p));
                foreach (var render in ChainAudition.PlayOrder)
                {
                    var audio = result.Render(render);
                    if (audio == null) continue;
                    if (!WriteRender(RenderPathFor(takePath, render), audio, result.SampleRate, render, generation, token))
                        return Cancelled(generation);
                }
                BeforeReadyForTest?.Invoke();
                lock (_sync)
                {
                    if (StaleLocked(generation, token)) return Cancelled(generation);
                    Result = result;
                    _state = AuditionTakeState.Ready;
                }
                Tracing.TraceLine(ChainAudition.TraceLine(result), System.Diagnostics.TraceLevel.Info);
                return new AuditionAnalysisOutcome { Generation = generation, Result = result };
            }
            catch (OperationCanceledException)
            {
                return Cancelled(generation);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check your audio audition: the analysis failed: " + ex.GetType().Name + ": " + ex.Message,
                    System.Diagnostics.TraceLevel.Error);
                lock (_sync)
                {
                    if (StaleLocked(generation, token)) return Cancelled(generation);
                    _state = AuditionTakeState.Idle;
                }
                return new AuditionAnalysisOutcome { Generation = generation, Trouble = TroubleFailed };
            }
        }

        /// <summary>
        /// Write one render a piece at a time, each piece under the lock and only
        /// while the work is still wanted. Once Discard has cleared the folder
        /// under the same lock, the next piece finds the work stale, and the part
        /// already written (which the clear could not delete while it was open)
        /// is closed and deleted here. False only when the work was cancelled; a
        /// render that fails to write is traced and skipped, as before.
        /// </summary>
        private bool WriteRender(string path, float[] samples, int rate, AuditionRender render, int generation, CancellationToken token)
        {
            WavWriter? writer = null;
            try
            {
                lock (_sync)
                {
                    if (StaleLocked(generation, token)) return false;
                    writer = new WavWriter(path, rate, 1);
                }
                for (int at = 0; at < samples.Length; at += RenderChunkSamples)
                {
                    BeforeRenderChunkForTest?.Invoke();
                    lock (_sync)
                    {
                        if (StaleLocked(generation, token)) { Abandon(ref writer, path); return false; }
                        writer.Write(samples, at, Math.Min(RenderChunkSamples, samples.Length - at));
                    }
                }
                lock (_sync)
                {
                    if (StaleLocked(generation, token)) { Abandon(ref writer, path); return false; }
                    writer.Finish();
                    writer.Dispose();
                    writer = null;
                }
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Check your audio audition: " + render + " render not written: " + ex.GetType().Name,
                    System.Diagnostics.TraceLevel.Error);
                lock (_sync)
                {
                    Abandon(ref writer, path);
                    return !StaleLocked(generation, token);
                }
            }
        }

        private static void Abandon(ref WavWriter? writer, string path)
        {
            try { writer?.Dispose(); } catch (Exception) { /* closing a part we are about to delete */ }
            writer = null;
            try { File.Delete(path); } catch (Exception) { /* swept at close */ }
        }

        /// <summary>
        /// The recorder stopped. If this take was recording and no file came of
        /// it (nothing captured, a fault), it goes back to idle. Call after any
        /// <see cref="Saved"/> the same stop produced.
        /// </summary>
        public void RecorderStopped()
        {
            lock (_sync)
            {
                if (_state != AuditionTakeState.Recording || _recorder.IsRecordingTo(TakePath)) return;
                _state = AuditionTakeState.Idle;
                ChainAuditionFolder.Clear(_folder);
            }
        }

        /// <summary>The file a render was written to, or null when this take has no such render on disk.</summary>
        public string? RenderPath(AuditionRender render)
        {
            if (State != AuditionTakeState.Ready || Result?.Render(render) == null) return null;
            string path = RenderPathFor(TakePath, render);
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
            lock (_sync)
            {
                bool wasRecording = _state == AuditionTakeState.Recording;
                // Idle FIRST, so the file the stop below reports is not analysed.
                _state = AuditionTakeState.Idle;
                Result = null;
                // Any analysis in flight is superseded and cancelled (Track Q phase
                // B, item 4); under this lock, so none of its pieces can be written
                // after the clear below.
                _generation++;
                _cancel?.Cancel();
                _cancel = null;
                if (wasRecording && _recorder.IsRecordingTo(TakePath)) _recorder.Stop();
                ChainAuditionFolder.Clear(_folder);
            }
        }

        /// <summary>A render's file, beside the take it belongs to (the path is passed, so a worker names its own take's files even after a newer take began).</summary>
        private string RenderPathFor(string takePath, AuditionRender render) =>
            Path.Combine(_folder, Path.GetFileNameWithoutExtension(takePath) + "-" + render.ToString().ToLowerInvariant() + ".wav");

        private static bool SamePath(string a, string b) =>
            a.Length > 0 && b.Length > 0
            && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
