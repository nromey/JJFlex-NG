using System;
using System.Diagnostics;
using JJPortaudio;
using JJTrace;

namespace Radios
{
    public partial class FlexBase
    {
        // ── S13, ruled by Noel 2026-11-18 21:21: the faster capture rate tries
        // itself on each machine ──
        //
        // "I'd pass it on mine, if Don's has problems then we can file that."
        // Until this, a machine earned 50 callbacks a second only after a run
        // started by hand on the bench. Now, once the helper's benchmark says the
        // slow engines fit and no verdict is held (or the one held is older than
        // TxCallbackRate.MaxVerdictAge), the app tries 50 on its own: only while
        // receiving, never keyed, never during a microphone check or a
        // recording, once per connection. The environment override still wins.
        //
        // HOW THE MICROPHONE IS NEVER LEFT SILENT. Make before break: the faster
        // stream is OPENED while the running one carries on (two streams on one
        // capture device; PortAudio's WASAPI shared mode and MME both allow it).
        // A failed open therefore changes nothing on the air: the old stream
        // never stopped. It keeps a "cannot open alongside" verdict (rulings
        // repair 1), so a device that refuses a second open is not asked again at
        // every connection; that verdict says nothing about the rate itself.
        // THE SWAP IS MAKE BEFORE BREAK ON THE AUDIO PATH TOO (the H2 split,
        // repair 1, Codex's finding that a key-down during the old stop-then-
        // start could open an over on a silent microphone for seconds). The new
        // stream is opened AND STARTED on its worker, beside the running one,
        // standing by: its callbacks run but feed nothing (JJPortaudio's
        // TxCaptureHandover). It is handed back only once the device is
        // delivering to it. On this loop thread the swap then asks the running
        // stream to hand over, and the running stream's own callback does it at
        // the END of the buffer it is processing, so the new stream's next
        // buffer is the first it feeds: the conditioner, the source mux and the
        // meter are only ever fed by one, and the microphone is never stopped to
        // make the swap. A new stream that will not start, or delivers nothing,
        // changes nothing: the running one was never touched, so there is no
        // restart and no rescue. If the running stream has itself stopped
        // producing, the new one takes over by force (a guard keeps two
        // callbacks out of the chain even then). The old stream is closed off
        // the loop. A fallback after a failed try is the same swap in the other
        // direction, and a fallback that will not open or start leaves the
        // faster stream running (dropping audio, but never silent) and is
        // asked again five seconds later.
        //
        // THE COST: the open, and the close of a stream no longer wanted, run on
        // a worker thread of their own since rulings repair 1 (Codex's P2: a
        // failed open waited five seconds on the loop that consumes receive), and
        // since the split's repair 1 the new stream's start and its first buffer
        // are waited for there too, so receive waits only for the hand-over: at
        // most one period of the running stream normally (100 ms at 10 a second,
        // 20 ms at 50), and TxHandoverWaitMs (350 ms and 110 ms) when the running
        // stream has stopped producing. Opening from a second thread is safe because the stream
        // table's key generator (Audio's queues.Add) now takes a lock. The try's
        // trace line carries the open's duration so the bench can see it.
        //
        // WHAT KEYING DURING A TRY DOES. The try only BEGINS while receiving. If
        // the operator keys while it runs, the faster stream carries the over
        // (swapping mid-over would put a gap on the air); its dropout count is
        // still watched, a dropout fails the try, and the fall back to 10 waits
        // for the unkey. This is a reading of "never while keyed", reported for
        // a ruling.

        /// <summary>How often the try looks, in milliseconds. A fallback being retried looks every <see cref="TxTryFallbackRetryMs"/>.</summary>
        internal const int TxTryCheckMs = 1000;

        /// <summary>A fallback whose open failed is asked again after this many milliseconds; the faster stream keeps running meanwhile.</summary>
        internal const int TxTryFallbackRetryMs = 5000;

        private long _txTryNextCheckTick;
        private object _txTryChannel;           // the connection (input channel) the try state belongs to
        private bool _txTryDone;                 // one try per connection
        private bool _txTryRunning;
        private bool _txTryDisturbed;
        private bool _txTryFallbackDue;
        private string _txTryFallbackWhy = "";
        private bool _txTryFallbackFailedTraced;
        private long _txTryOpenMs;

        /// <summary>A stream that died part way, or shared its device during a try: it earns no verdict at close.</summary>
        private bool _txStreamPartialRun;
        private string _txStreamPartialReason = "";

        /// <summary>The automatic try already judged the running stream (a failure, or a disturbed run); closing it judges nothing more.</summary>
        private bool _txStreamAlreadyJudged;

        /// <summary>Tests only: replaces the stream open, so the try's decisions can be driven without a device.</summary>
        internal Func<int, JJAudioStream> TxCaptureOpenForTest { get; set; }

        /// <summary>Whether a microphone check or a recording is using the microphone: a probe holds the device, or a Check your audio take is tapping the transmit audio.</summary>
        internal bool TxCheckOrRecordingRunning
        {
            get
            {
                if (MicProbe.ActiveCaptures > 0) return true;
                try { return _iqLease != null && _iqLease.CurrentCount == 0; }
                catch { return true; }
            }
        }

        /// <summary>
        /// The try's one look, on the remote-audio loop's thread (from
        /// <c>startOpusInputChannel</c>). At most once a second; everything it
        /// does to the stream happens here, on the thread that owns it.
        /// </summary>
        private void MaybeTryFasterTxCapture()
        {
            long now = Environment.TickCount64;
            if (now < _txTryNextCheckTick) return;
            _txTryNextCheckTick = now + TxTryCheckMs;
            try { TxTryStep(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("TxCallbackRate: the automatic try could not look: " + ex.Message, TraceLevel.Error);
            }
        }

        // ── Rulings repair 1: the open runs on its own worker (Codex's P2), and
        // the swap is one admitted step (Codex's P1) ──
        //
        // A failed open waits up to five seconds in Audio.Open for a stream the
        // audio server already refused, and the loop that calls this also
        // consumes receive packets, so the open (and the close of a stream we
        // no longer want, which polls in 200 ms steps) now runs on a worker
        // thread of its own, one per open. The loop starts it and carries on;
        // a later look picks up what it opened. Only the stop and start of the
        // swap stay on the loop, as every producer swap the gate makes does.
        //
        // THE SWAP IS ADMITTED, NOT ASSUMED. After every open (the try's and the
        // fall back's), on the loop, under the capture admission lock: keyed (our
        // Mox, the interlock, or a key-down admitted in the last
        // TxKeyAdmissionFreshMs that the radio has not yet reported), any key-down
        // admitted since the open began, a microphone check, a recording, a
        // generated source, a stopped capture, or a changed connection each
        // refuse it; the new stream is closed (off the loop) and the try waits for
        // the next unkeyed moment.
        //
        // OUR KEY-DOWN IS COUNTED, NEVER HELD (the H2 split, 2026-11-19). On
        // sprint49/h2-join our own key-down also took the admission lock, so one
        // arriving during the swap's stop and start waited for them. That made
        // a key press wait on the audio server (normally one capture period, up
        // to seconds if the server is wedged), which changes how keying works,
        // and Noel's H1 ruling keeps keying exactly as it is until the key work
        // is rebuilt on main. So the Transmit setter only records the key-down
        // (a counter and a time, no lock, no wait): a key-down admitted while the
        // open ran, or in the last TxKeyAdmissionFreshMs, still refuses the swap.
        // One that lands inside the swap itself (the hand-over wait) no longer
        // meets a stopped microphone (repair 1): until the hand-over the running
        // stream feeds the over, and from it the new one does.

        /// <summary>A key-down we admitted counts as keyed for this long, until the radio's own report says so (or not).</summary>
        internal const int TxKeyAdmissionFreshMs = 2000;

        /// <summary>How long a swap's new stream, started beside the running one, may take to deliver its first buffer (on its worker) before it counts as not starting (repair 1).</summary>
        internal const int TxStandbyProducingMs = 1000;

        /// <summary>
        /// How long the swap waits, on the loop, for the running stream to hand
        /// over at the end of a buffer: three of its periods and a little more.
        /// Longer means it has stopped producing, and the new stream takes over
        /// by force (repair 1).
        /// </summary>
        internal static int TxHandoverWaitMs(int runningCallbacksPerSecond) =>
            3000 / Math.Max(1, runningCallbacksPerSecond > 0 ? runningCallbacksPerSecond : TxCallbackRate.Shipped) + 50;

        private object _txCaptureAdmissionObject;
        private object TxCaptureAdmission =>
            System.Threading.Volatile.Read(ref _txCaptureAdmissionObject)
            ?? System.Threading.Interlocked.CompareExchange(ref _txCaptureAdmissionObject, new object(), null)
            ?? _txCaptureAdmissionObject;
        private long _txKeyDownAdmissions;
        private long _txKeyAdmittedTick;

        /// <summary>
        /// Our key-down, recorded against a capture swap (the <see cref="Transmit"/>
        /// setter, before its write is queued). Never waits and never refuses:
        /// keying is unchanged (see the header above).
        /// </summary>
        private void NoteTxKeyDownAdmitted()
        {
            System.Threading.Interlocked.Increment(ref _txKeyDownAdmissions);
            System.Threading.Volatile.Write(ref _txKeyAdmittedTick, Math.Max(1, Environment.TickCount64));
        }

        /// <summary>Our unkey: a key-down admitted earlier no longer counts. Never waits.</summary>
        private void NoteTxKeyUpRequested() => System.Threading.Volatile.Write(ref _txKeyAdmittedTick, 0);

        private bool TxKeyRecentlyAdmitted
        {
            get
            {
                long at = System.Threading.Volatile.Read(ref _txKeyAdmittedTick);
                return at != 0 && Environment.TickCount64 - at < TxKeyAdmissionFreshMs;
            }
        }

        /// <summary>
        /// What an open on the worker is for. A <see cref="Replace"/> open puts a
        /// stream where there is none (rulings repair 2): the double-failure
        /// rescue of a swap, and the re-open after a microphone stream died
        /// (#1145, tier 2). Until it publishes, the microphone is pending and the
        /// gate starts nothing.
        /// </summary>
        private enum TxOpenKind { Try, Fallback, Replace }

        /// <summary>One open, run off the loop, bound to the connection (its input channel) and the run (its wake) that asked.</summary>
        private sealed class TxOpenJob
        {
            public TxOpenKind Kind;
            public int Rate;
            public int SecondRate;               // Replace only: opened if Rate will not; 0 for none
            public int OpenedRate;
            public string Why = "";
            public object Channel;
            public RemoteAudioWake Wake;         // the run that asked; the only one it may signal or publish into
            public long Server;                  // the audio server it was started under (Audio.ServerGeneration); the only one it may open or close on
            public long KeyDownsAtStart;
            public long OpenMs;
            public JJAudioStream Stream;
            public JJAudioStream Beside;         // Try and Fallback: the running stream it is to replace, make before break (repair 1)
            public bool StartFailed;             // Try and Fallback: it opened but would not start, or started and produced nothing
            public string StartFailedWhy = "";
            public int State;   // 0 opening, 1 opened and waiting for the loop, 2 taken by the loop, 3 abandoned
            public bool Fallback => Kind == TxOpenKind.Fallback;
        }
        private TxOpenJob _txOpenJob;

        /// <summary>Tests only: runs a worker's job (inline, in the windowless tests). Null in the app: a background thread per job.</summary>
        internal Action<Action> TxCaptureWorkerForTest { get; set; }

        /// <summary>Opens and closes running on their workers now. The teardown waits for them before the audio server is ended.</summary>
        private int _txCaptureWorkers;

        /// <summary>How long the teardown waits for an open or close still on its worker: an open is bounded at five seconds by the audio server, a close at a few 200 ms polls.</summary>
        internal const int TxCaptureWorkerDrainMs = 6000;

        private void RunTxCaptureWork(string name, Action work)
        {
            System.Threading.Interlocked.Increment(ref _txCaptureWorkers);
            void Run()
            {
                try { work(); }
                catch (Exception ex) { Tracing.TraceLine("TxCallbackRate: " + name + " threw: " + ex.Message, TraceLevel.Error); }
                finally { System.Threading.Interlocked.Decrement(ref _txCaptureWorkers); }
            }
            var forTest = TxCaptureWorkerForTest;
            if (forTest != null) { forTest(Run); return; }
            var thread = new System.Threading.Thread(Run) { IsBackground = true, Name = name };
            thread.Start();
        }

        /// <summary>
        /// The teardown's courtesy wait for opens and closes still on their
        /// workers, so they can finish and close what they opened in the
        /// ordinary way. It is NOT the barrier (join repair 3, Codex's P2: a
        /// worker left behind at a timed bound could start another open after
        /// it). The barrier is the audio server's gate
        /// (<see cref="JJPortaudio.Audio.RunOnServer"/>): every worker operation
        /// runs under it, only against the server it was started under, and
        /// only while still wanted, and Audio.Terminate and Audio.Initialize
        /// take the same gate. So whatever this wait leaves behind can finish
        /// its one operation in progress on its own server before that server
        /// ends, and can begin nothing afterwards.
        /// </summary>
        private void WaitForTxCaptureWorkers(string why)
        {
            long until = Environment.TickCount64 + TxCaptureWorkerDrainMs;
            while (System.Threading.Volatile.Read(ref _txCaptureWorkers) > 0 && Environment.TickCount64 < until)
                System.Threading.Thread.Sleep(10);
            int left = System.Threading.Volatile.Read(ref _txCaptureWorkers);
            if (left > 0)
                Tracing.TraceLine("TxCallbackRate: " + left + " microphone open or close still on its worker after "
                    + TxCaptureWorkerDrainMs + " ms (" + why + "); the audio server's end waits only for the operation in"
                    + " progress, and nothing further can begin on it", TraceLevel.Warning);
        }

        /// <summary>
        /// Close a stream nothing uses any more, off the loop (a close polls in
        /// 200 ms steps), on the audio server it was opened under
        /// (<paramref name="server"/>; by default the live one, read here on the
        /// caller's thread). If that server has ended meanwhile, nothing is
        /// posted: its end closed every stream it had (join repair 3).
        /// </summary>
        private void CloseTxStreamOffLoop(JJAudioStream stream, long? server = null)
        {
            if (stream == null) return;
            long on = server ?? JJPortaudio.Audio.ServerGeneration;
            RunTxCaptureWork("TxCaptureClose", () =>
            {
                if (!JJPortaudio.Audio.RunOnServer(on, null, () => { try { stream.Close(); } catch { } }))
                    Tracing.TraceLine("TxCallbackRate: a microphone stream's close found its audio server already ended;"
                        + " nothing posted", TraceLevel.Info);
            });
        }

        /// <summary>
        /// Whether a worker's job may still begin an operation (join repair 3,
        /// Codex's P2 and the #1181 point): not abandoned, its run still the
        /// current one, the remote audio not told to stop, and the connection
        /// not disconnecting. Asked under the audio server's gate immediately
        /// before every open, so a re-open never opens into a closing session.
        /// </summary>
        private bool TxOpenJobWanted(TxOpenJob job) =>
            System.Threading.Volatile.Read(ref job.State) == 0
            && ReferenceEquals(job.Wake, System.Threading.Volatile.Read(ref _remoteAudioWake))
            && !stopRemoteAudio
            && !Disconnecting;

        private void StartTxOpenJob(int rate, TxOpenKind kind, object channel, int secondRate = 0, string why = "")
        {
            var job = new TxOpenJob
            {
                Rate = rate, Kind = kind, Channel = channel, SecondRate = secondRate, Why = why ?? "",
                // Read on the loop, so it is this run's wake (rulings repair 2):
                // the worker signals only the run that asked, and a newer run's
                // wake is never captured.
                Wake = System.Threading.Volatile.Read(ref _remoteAudioWake),
                // And this run's audio server (join repair 3): the worker's opens,
                // and the close of anything it opened, run only on it.
                Server = JJPortaudio.Audio.ServerGeneration,
                KeyDownsAtStart = System.Threading.Interlocked.Read(ref _txKeyDownAdmissions),
                // Repair 1: a swap's new stream starts beside the running one,
                // read here on the loop, which owns it.
                Beside = kind == TxOpenKind.Replace ? null : (channel as audioChannelData)?.PortAudioStream,
            };
            _txOpenJob = job;
            RunTxCaptureWork("TxCaptureOpen", () => RunTxOpenJob(job));
        }

        /// <summary>
        /// The worker's body (join repair 3, Codex's P2). Each open is ONE
        /// operation under the audio server's gate, begun only if the job is
        /// still wanted and its server still live; an open that completes after
        /// its job was abandoned is closed in that same operation, on that same
        /// server. A job that may no longer begin anything ends quietly: nothing
        /// opened, nothing handed back, nothing said.
        /// </summary>
        private void RunTxOpenJob(TxOpenJob job)
        {
            long t0 = Environment.TickCount64;
            JJAudioStream opened = null;
            bool handedBack = false;
            int second = job.SecondRate > 0 && job.SecondRate != job.Rate ? job.SecondRate : 0;
            for (int attempt = 0; attempt < (second > 0 ? 2 : 1) && opened == null; attempt++)
            {
                int r = attempt == 0 ? job.Rate : second;
                bool ran = JJPortaudio.Audio.RunOnServer(job.Server, () => TxOpenJobWanted(job), () =>
                {
                    job.OpenedRate = r;
                    try { opened = OpenTxCaptureAt(r, job.Wake); }
                    catch (Exception ex) { Tracing.TraceLine("TxCallbackRate: the open threw: " + ex.Message, TraceLevel.Error); opened = null; }
                    if (opened == null) return;
                    job.OpenMs = Environment.TickCount64 - t0;
                    if (job.Kind != TxOpenKind.Replace)
                    {
                        // Repair 1, make before break: a swap's new stream is
                        // STARTED here, standing by beside the running one (it
                        // feeds nothing), so the swap never stops the microphone
                        // to start it. A start that fails leaves the running
                        // stream exactly as it was.
                        bool started;
                        try { started = opened.StartAudioBeside(job.Beside); }
                        catch (Exception ex) { Tracing.TraceLine("TxCallbackRate: the start beside the running stream threw: " + ex.Message, TraceLevel.Error); started = false; }
                        if (!started)
                        {
                            job.StartFailed = true;
                            job.StartFailedWhy = "it opened but would not start";
                            try { opened.Close(); } catch { }
                            opened = null;
                        }
                        return; // handed back below, once it is seen producing, outside the gate
                    }
                    job.Stream = opened;
                    // Handed back only if nobody abandoned it while it opened (the
                    // remote audio stopped, the stream died, the run ended); if
                    // somebody did, it is closed here, on its own server.
                    if (System.Threading.Interlocked.CompareExchange(ref job.State, 1, 0) == 0) handedBack = true;
                    else { try { opened.Close(); } catch { } }
                });
                if (!ran)
                {
                    System.Threading.Interlocked.CompareExchange(ref job.State, 3, 0);
                    System.Threading.Interlocked.CompareExchange(ref _txOpenJob, null, job);
                    Tracing.TraceLine("TxCallbackRate: the " + TxOpenName(job) + " open at " + r + " a second did not begin:"
                        + " its job was abandoned, its run or audio server ended, or the session is closing", TraceLevel.Info);
                    return;
                }
            }
            if (job.Kind != TxOpenKind.Replace && opened != null)
            {
                // Started and standing by: handed back only once the device is
                // delivering to it, so the swap's take-over is never asked of a
                // stream that has produced nothing. Waited here, off the loop and
                // outside the server gate.
                bool producing;
                try { producing = opened.WaitUntilProducing(TxStandbyProducingMs); } catch { producing = false; }
                job.OpenMs = Environment.TickCount64 - t0;
                var standing = opened;
                if (!producing)
                {
                    job.StartFailed = true;
                    job.StartFailedWhy = "it started but delivered no audio within " + TxStandbyProducingMs + " ms";
                    opened = null;
                    if (!JJPortaudio.Audio.RunOnServer(job.Server, null, () => { try { standing.Close(); } catch { } }))
                        Tracing.TraceLine("TxCallbackRate: a standing-by stream's close found its audio server already ended; nothing posted", TraceLevel.Info);
                }
                else
                {
                    job.Stream = standing;
                    if (System.Threading.Interlocked.CompareExchange(ref job.State, 1, 0) == 0) handedBack = true;
                    else
                    {
                        // Abandoned while it started: closed on its own server.
                        if (!JJPortaudio.Audio.RunOnServer(job.Server, null, () => { try { standing.Close(); } catch { } }))
                            Tracing.TraceLine("TxCallbackRate: an abandoned standing-by stream's close found its audio server already ended; nothing posted", TraceLevel.Info);
                        return;
                    }
                }
            }
            if (!handedBack)
            {
                if (opened != null) return; // opened, then abandoned: closed above
                job.OpenMs = Environment.TickCount64 - t0;
                job.Stream = null;
                if (System.Threading.Interlocked.CompareExchange(ref job.State, 1, 0) != 0) return;
            }
            System.Threading.Volatile.Write(ref _txTryNextCheckTick, 0); // the loop's next look picks it up
            job.Wake?.Signal();                                           // and that look comes now, not at the idle timeout
        }

        /// <summary>
        /// On the loop: take an open that is back from its worker and act on it.
        /// Every look of the gate's start does this first (rulings repair 2), so
        /// a re-open publishes as soon as it is back, whatever the try is doing.
        /// </summary>
        private void TakeReturnedTxOpen(audioChannelData channel, bool replacementOnly = false)
        {
            var job = _txOpenJob;
            if (job == null) return;
            if (replacementOnly && job.Kind != TxOpenKind.Replace) return;
            if (System.Threading.Interlocked.CompareExchange(ref job.State, 2, 1) != 1) return;
            _txOpenJob = null;
            TxOpenReturned(job, channel);
        }

        /// <summary>
        /// On the loop, once every pass and at the top of BOTH producers' starts
        /// (join repair 3, Codex's P2): a re-open that is back from its worker is
        /// published whatever producer the gate is about to select. It used to
        /// be taken only by the microphone's start, so when the gate started a
        /// file or the tone on the self-clock instead, the opened stream sat in
        /// its job and the self-clock found no stream to run on. Publishing a
        /// re-open puts a stream where there was none and starts nothing, so the
        /// single-producer order (stop the capture, then start the self-clock)
        /// is untouched. A try's or fall back's open is a SWAP of the running
        /// capture, admitted only while the microphone is the producer, so it
        /// still waits for the microphone's start.
        /// </summary>
        private void TakeReturnedTxReplacement()
        {
            if (!opusInputAvailable) return;
            var channel = opusInputChannel;
            if (channel == null) return;
            TakeReturnedTxOpen(channel, replacementOnly: true);
        }

        /// <summary>
        /// Abandon an open in flight or waiting: its stream is closed, by the
        /// worker if it is still opening, here (off the loop) if it is waiting.
        /// For the remote audio stopping and the stream dying.
        /// </summary>
        private void AbandonTxOpenJob(string why)
        {
            var job = System.Threading.Interlocked.Exchange(ref _txOpenJob, null);
            if (job == null) return;
            int was = System.Threading.Interlocked.Exchange(ref job.State, 3);
            if (was == 1) CloseTxStreamOffLoop(job.Stream, job.Server);
            if (was == 0 || was == 1)
                Tracing.TraceLine("TxCallbackRate: the " + TxOpenName(job) + " open at " + job.Rate
                    + " a second was abandoned: " + why, TraceLevel.Info);
        }

        private static string TxOpenName(TxOpenJob job) => job.Kind switch
        {
            TxOpenKind.Fallback => "fall back's",
            TxOpenKind.Replace => "microphone re-open's",
            _ => "automatic try's",
        };

        private void TxTryStep()
        {
            var channel = opusInputChannel;
            if (channel == null || !opusInputAvailable) return;
            if (!ReferenceEquals(channel, _txTryChannel))
            {
                // A new connection: a new try is allowed.
                _txTryChannel = channel;
                _txTryDone = _txTryRunning = _txTryDisturbed = _txTryFallbackDue = _txTryFallbackFailedTraced = false;
                _txTryFallbackWhy = "";
            }

            // An open on its worker: nothing to do until it is back; receive carries on.
            if (_txOpenJob != null)
            {
                TakeReturnedTxOpen(channel);
                return;
            }

            bool keyed;
            try { keyed = TransmitKeyedOrInterlock; } catch { keyed = true; }
            bool busy = TxCheckOrRecordingRunning;
            bool sourceEngaged;
            try { sourceEngaged = !TxInputSources.Idle; } catch { sourceEngaged = true; }

            if (_txTryRunning)
            {
                if (busy) { _txTryDisturbed = true; _txStreamPartialRun = true; _txStreamPartialReason = "a microphone check or recording shared the device during the automatic try"; }
                var stream = channel.PortAudioStream;
                if (stream == null) { _txTryRunning = false; return; }
                var (callbacks, overflows, underflows) = stream.InputDropouts;
                var progress = _txTryDisturbed ? TxTryProgress.Failed : TxCallbackRate.TryProgress(callbacks, overflows, underflows, TxCallbackRate.Fast);
                if (progress == TxTryProgress.Running) return;
                _txTryRunning = false;
                ConcludeTxTry(progress, callbacks, overflows, underflows);
                if (!_txTryFallbackDue) return;
            }

            if (_txTryFallbackDue)
            {
                if (keyed || TxKeyRecentlyAdmitted || busy || sourceEngaged || !channel.Started) return; // at once, as soon as it is allowed
                StartTxOpenJob(TxCallbackRate.Shipped, TxOpenKind.Fallback, channel);
                TakeReturnedTxOpen(channel);
                return;
            }

            var capture = ExpectedTxCapture(OpusTxSampleRateSetting);
            TxCallbackVerdict verdict = null;
            try { verdict = TxCallbackRate.Store?.Find(capture, TxCallbackRate.Fast); } catch { verdict = null; }
            var decision = TxCallbackRate.ShouldTry(AudioBuffering.ConfiguredTxCallbacksPerSecond(), TxCallbackRate.HelperFrameCostP99Nanos,
                verdict, DateTime.UtcNow, _txCallbackRateThisStream, _txTryDone, keyed || TxKeyRecentlyAdmitted, busy, channel.Started && !sourceEngaged);
            if (decision.Answer != TxTryAnswer.Try) return;

            _txTryDone = true;
            _txTryReason = decision.Reason;
            StartTxOpenJob(TxCallbackRate.Fast, TxOpenKind.Try, channel);
            TakeReturnedTxOpen(channel); // a worker that ran inline (the windowless tests) is already back
        }

        private string _txTryReason = "";

        /// <summary>On the loop: an open came back from its worker. Swap it in if admitted; otherwise close it off the loop and wait.</summary>
        private void TxOpenReturned(TxOpenJob job, audioChannelData channel)
        {
            var fresh = job.Stream;
            // Bound to the connection AND the run that asked (rulings repair 2):
            // an open from an ended run never publishes into a later one.
            if (!ReferenceEquals(job.Channel, channel) || !ReferenceEquals(job.Wake, System.Threading.Volatile.Read(ref _remoteAudioWake)))
            {
                CloseTxStreamOffLoop(fresh, job.Server); // a different connection now; its own try state was reset
                return;
            }
            if (job.Kind == TxOpenKind.Replace)
            {
                PublishTxCaptureReplacement(job, channel);
                return;
            }
            if (fresh == null && job.StartFailed)
            {
                // Repair 1: it opened but would not start (or delivered nothing)
                // beside the running stream, which was never touched. A fall back
                // is asked again later, as a fall back whose open failed is. A
                // try keeps it as "cannot open alongside", NOT as the rate
                // failing: the start now runs while the other stream is running,
                // so a refusal says the device will not run two at once, which
                // is the same thing a refused open alongside says (a judgement,
                // reported; before repair 1 the start came after the stop, so a
                // refusal there was about the rate).
                if (job.Fallback)
                {
                    Tracing.TraceLine("TxCallbackRate: the fall back at " + TxCallbackRate.Shipped + " a second " + job.StartFailedWhy
                        + " (" + job.OpenMs + " ms); the microphone carries on at " + TxCallbackRate.Fast
                        + " untouched, asked again in " + TxTryFallbackRetryMs / 1000 + " seconds", TraceLevel.Error);
                    _txTryNextCheckTick = Environment.TickCount64 + TxTryFallbackRetryMs;
                    return;
                }
                var failedCapture = ExpectedTxCapture(OpusTxSampleRateSetting);
                RecordTxTryVerdict(new TxCallbackVerdict
                {
                    Device = failedCapture.Device, HostApi = failedCapture.HostApi, SampleRate = failedCapture.SampleRate, Channels = failedCapture.Channels,
                    CallbacksPerSecond = TxCallbackRate.Fast, Passed = false, CannotOpenAlongside = true, MeasuredUtc = DateTime.UtcNow,
                }, "(" + _txTryReason + "): the faster stream " + job.StartFailedWhy + " beside the running one (" + job.OpenMs
                    + " ms, off the receive loop); the microphone carried on at " + _txCallbackRateThisStream + " untouched; not tried again on this device for "
                    + TxCallbackRate.MaxVerdictAge.TotalDays.ToString("0") + " days");
                return;
            }
            if (fresh == null)
            {
                if (job.Fallback)
                {
                    if (!_txTryFallbackFailedTraced)
                    {
                        _txTryFallbackFailedTraced = true;
                        Tracing.TraceLine("TxCallbackRate: could not open the microphone at " + TxCallbackRate.Shipped
                            + " a second to fall back (" + job.OpenMs + " ms); it carries on at " + TxCallbackRate.Fast
                            + " (never silent), asked again every " + TxTryFallbackRetryMs / 1000 + " seconds", TraceLevel.Error);
                    }
                    _txTryNextCheckTick = Environment.TickCount64 + TxTryFallbackRetryMs;
                    return;
                }
                // The faster stream would not open while the running one carried
                // on. Kept as its own verdict, "cannot open alongside", so it is
                // not tried at every connection; it is not a verdict on the rate.
                var capture = ExpectedTxCapture(OpusTxSampleRateSetting);
                RecordTxTryVerdict(new TxCallbackVerdict
                {
                    Device = capture.Device, HostApi = capture.HostApi, SampleRate = capture.SampleRate, Channels = capture.Channels,
                    CallbacksPerSecond = TxCallbackRate.Fast, Passed = false, CannotOpenAlongside = true, MeasuredUtc = DateTime.UtcNow,
                }, "(" + _txTryReason + "): the faster stream would not open alongside the running one (" + job.OpenMs
                    + " ms, off the receive loop); the microphone carried on at " + _txCallbackRateThisStream + " untouched; not tried again on this device for "
                    + TxCallbackRate.MaxVerdictAge.TotalDays.ToString("0") + " days");
                return;
            }
            _txTryOpenMs = job.OpenMs;
            string why = job.Fallback ? "fall back after the automatic try: " + _txTryFallbackWhy : "the automatic try";
            var result = SwapTxCaptureStreamAdmitted(fresh, job.Beside, job.Rate, why, job.KeyDownsAtStart, out string refusal);
            if (result == TxSwapResult.Refused)
            {
                CloseTxStreamOffLoop(fresh, job.Server);
                Tracing.TraceLine("TxCallbackRate: the " + (job.Fallback ? "fall back" : "automatic try") + " at " + job.Rate
                    + " a second opened (" + job.OpenMs + " ms) but was not swapped in: " + refusal
                    + "; the microphone carries on as it was, and it is asked again at the next unkeyed moment", TraceLevel.Info);
                if (!job.Fallback) _txTryDone = false; // abandoned before it began: ask again later
                return;
            }
            if (job.Fallback)
            {
                _txTryFallbackDue = false;
                return;
            }
            _txTryRunning = true;
            _txTryDisturbed = false;
        }

        /// <summary>A try's end: a pass is kept and the faster stream stays; a failure is kept and the fall back is due; a disturbed try keeps nothing and falls back too.</summary>
        private void ConcludeTxTry(TxTryProgress progress, long callbacks, long overflows, long underflows)
        {
            var stream = opusInputChannel?.PortAudioStream;
            var capture = new TxCaptureIdentity(remoteInputDevice?.Name ?? "", remoteInputDevice?.hostApiTypeId ?? -1,
                (int)(stream?.SampleRate ?? 0), stream?.Channels == 1 ? 1 : 2);
            string counts = callbacks + " callbacks, " + overflows + " input overflows, " + underflows + " underflows; the open took " + _txTryOpenMs + " ms";
            if (_txTryDisturbed)
            {
                _txStreamAlreadyJudged = true;
                _txTryFallbackDue = true;
                _txTryFallbackWhy = "a microphone check or recording shared the device";
                Tracing.TraceLine("TxCallbackRate: automatic try at " + TxCallbackRate.Fast + " a second: no verdict, a microphone check or a recording shared the device ("
                    + counts + "); back to " + TxCallbackRate.Shipped + " as soon as it is allowed", TraceLevel.Info);
                return;
            }
            var verdict = new TxCallbackVerdict
            {
                Device = capture.Device, HostApi = capture.HostApi, SampleRate = capture.SampleRate, Channels = capture.Channels,
                CallbacksPerSecond = TxCallbackRate.Fast, Passed = progress == TxTryProgress.Passed,
                Callbacks = callbacks, Overflows = overflows, Underflows = underflows, MeasuredUtc = DateTime.UtcNow,
            };
            if (verdict.Passed)
            {
                RecordTxTryVerdict(verdict, "passed (" + counts + "); the microphone stays at " + TxCallbackRate.Fast);
                return;
            }
            _txStreamAlreadyJudged = true;
            _txTryFallbackDue = true;
            _txTryFallbackWhy = "it dropped audio";
            RecordTxTryVerdict(verdict, "failed: it dropped audio (" + counts + "); back to " + TxCallbackRate.Shipped
                + (TransmitKeyedOrInterlock ? " at the unkey" : " now"));
        }

        /// <summary>Keep a try's verdict for this machine and device, and trace the try's one line.</summary>
        private void RecordTxTryVerdict(TxCallbackVerdict verdict, string outcome)
        {
            bool kept = false;
            try { kept = TxCallbackRate.Store?.Record(verdict) == true; } catch { kept = false; }
            Tracing.TraceLine("TxCallbackRate: automatic try at " + TxCallbackRate.Fast + " a second " + outcome
                + (kept ? "; kept for this machine" : "; NOT kept: the verdict could not be saved"),
                verdict.Passed && kept ? TraceLevel.Info : TraceLevel.Warning);
        }

        /// <summary>
        /// THE transmit capture open (rulings repair 2: the first open in
        /// remoteAudioProc, the automatic try, its fall back, the swap's rescue
        /// and the re-open after a death all come here). Opens at
        /// <paramref name="rate"/> callbacks a second with the transmit Opus
        /// profile, traces the rate the device actually gave, and wires it: the
        /// input source mux, the conditioner, the loudness meter, the key state
        /// on the conditioner and the stream's pipeline, and the death hook to
        /// <paramref name="wake"/>, the run that asked. NOT started. Null when it
        /// would not open, with nothing left behind.
        /// </summary>
        /// <param name="rate">Callbacks a second.</param>
        /// <param name="wake">The asking run's wake: a stream that dies wakes that run's loop (#1145). Null wires no hook; the loop still notices the died flag within its idle wait.</param>
        private JJAudioStream OpenTxCaptureAt(int rate, RemoteAudioWake wake)
        {
            JJAudioStream stream;
            var forTest = TxCaptureOpenForTest;
            if (forTest != null) stream = forTest(rate);
            else
            {
                stream = new JJAudioStream();
                uint asked = OpusTxSampleRateSetting;
                bool opened;
                try
                {
                    opened = stream.OpenOpus(Devices.DeviceTypes.input, asked, sendOpusInput,
                        null, rate, OpusTxEncodeProfile);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("TxCallbackRate: opening the microphone at " + rate + " a second threw: " + ex.Message, TraceLevel.Error);
                    opened = false;
                }
                if (!opened)
                {
                    try { stream.Close(); } catch { }
                    return null;
                }
                // Always log what the transmit stream actually opened as,
                // whether or not it matches what was asked for. The device has
                // the last word on both the rate and the channel count, and a
                // line that only appears on divergence is a line nobody can
                // use as a baseline.
                Tracing.TraceLine("remoteAudioProc:opus input channel open at "
                    + stream.SampleRate + " Hz, " + stream.Channels + " channel(s), " + rate + " callbacks a second"
                    + (stream.SampleRate != asked ? " — the device refused the requested " + asked + " Hz" : ""),
                    TraceLevel.Info);
            }
            if (stream == null) return null;
            // Audio Track C: hand the persistent TX injection sources to the
            // input stream so an engaged one replaces the mic at the encoder.
            // Sprint 33 Track I: this used to be the tone generator alone; it
            // is now the mux carrying the tone and the reference-voice player.
            stream.InputSource = TxInputSources;
            // Track I: hand the persistent TX conditioning chain (NR + gate +
            // residual tap) to the same stream. It runs AFTER the tone
            // injection point (and is skipped entirely while a tone is
            // engaged — the tone is a calibrated reference) and BEFORE the
            // LUFS meter, so the meter keeps measuring what genuinely ships.
            stream.InputProcessor = txConditioner.Process;
            // Engine Track: hand the persistent LUFS meter to the same stream.
            // It taps the callback AFTER the tone injection, so it measures
            // whatever is actually being encoded and sent — tone or mic.
            stream.InputLufsMeter = txLufsMeter;
            // Sprint 49 Track H: the key state the chain reads once per buffer,
            // our Mox OR the radio's interlock. The conditioner resets the gate
            // open at a real key-down with it (#1075) and the noise reduction
            // holds its changes for the unkey; the pipeline ends a faulted
            // stage's skip when it changes (#1074).
            txConditioner.KeySource = TransmitKeySource;
            var pipeline = stream.TransmitPipeline;
            if (pipeline != null) pipeline.KeySource = TransmitKeySource;
            // #1145: a microphone stream that ends on its own wakes the loop of
            // the run that opened it; the loop also checks the flag on every
            // pass without it.
            if (wake != null) stream.StreamDiedHook = wake.Signal;
            return stream;
        }

        private enum TxSwapResult { Refused, Swapped }

        /// <summary>
        /// Put an open, wired stream in place of the running one, on the loop's
        /// thread, as ONE admitted step (rulings repair 1): under the capture
        /// admission lock, ask again whether it is allowed (see the header above).
        /// MAKE BEFORE BREAK (the H2 split, repair 1): the new stream is already
        /// started and producing beside the running one, feeding nothing; the
        /// swap asks the running stream to hand over at the end of a buffer and
        /// waits for it (<see cref="TxHandoverWaitMs"/>), so the microphone is
        /// never stopped to make the swap. If the running stream has stopped
        /// producing, the new one takes over by force. The old stream is closed
        /// off the loop once it is out of use; nothing is ever stopped first, so
        /// there is nothing to start again and no rescue.
        /// </summary>
        private TxSwapResult SwapTxCaptureStreamAdmitted(JJAudioStream fresh, JJAudioStream beside, int rate, string why, long keyDownsAtStart, out string refusal)
        {
            refusal = null;
            var channel = opusInputChannel;
            if (channel == null || fresh == null) { refusal = "the connection ended"; return TxSwapResult.Refused; }
            JJAudioStream old;
            bool forced;
            long handoverMs;
            lock (TxCaptureAdmission)
            {
                bool keyed;
                try { keyed = TransmitKeyedOrInterlock; } catch { keyed = true; }
                bool sourceEngaged;
                try { sourceEngaged = !TxInputSources.Idle; } catch { sourceEngaged = true; }
                if (System.Threading.Interlocked.Read(ref _txKeyDownAdmissions) != keyDownsAtStart) refusal = "a key-down arrived while it opened";
                else if (keyed || TxKeyRecentlyAdmitted) refusal = "the radio is keyed";
                else if (TxCheckOrRecordingRunning) refusal = "a microphone check or recording holds the device";
                else if (sourceEngaged) refusal = "a generated source is engaged";
                else if (!opusInputAvailable || !ReferenceEquals(opusInputChannel, channel)) refusal = "the connection changed";
                if (refusal != null) return TxSwapResult.Refused;
                lock (channel)
                {
                    if (!channel.Started) { refusal = "the capture is not running"; return TxSwapResult.Refused; }
                    old = channel.PortAudioStream;
                    if (!ReferenceEquals(old, beside)) { refusal = "the running stream changed while it opened"; return TxSwapResult.Refused; }
                    var clock = Stopwatch.StartNew();
                    fresh.RequestTakeOver();
                    int bound = TxHandoverWaitMs(_txCallbackRateThisStream);
                    while (!fresh.HasTakenOver && clock.ElapsedMilliseconds < bound) System.Threading.Thread.Sleep(1);
                    forced = !fresh.HasTakenOver;
                    if (forced) fresh.ForceTakeOver(); // the running stream had stopped producing
                    handoverMs = clock.ElapsedMilliseconds;
                    channel.PortAudioStream = fresh;
                    channel.Started = true;
                }
            }
            why += forced
                ? "; the running stream delivered nothing for " + handoverMs + " ms, so the new one took over without its hand-over"
                : "; handed over at the end of a buffer after " + handoverMs + " ms, the microphone never stopped";
            if (old != null && !ReferenceEquals(old, fresh))
            {
                // Judged as it closes, like any stream, unless the try already
                // judged it; a shipped-rate one earns nothing. Closed off the loop.
                JudgeTxCallbackRate(old);
                CloseTxStreamOffLoop(old);
            }
            // The stream now running is a new one: nothing about it is judged or partial yet.
            _txStreamAlreadyJudged = false;
            _txStreamPartialRun = false;
            _txStreamPartialReason = "";
            _txCallbackRateThisStream = rate;
            TxAudioCallbacksPerSecond = rate;
            TxCallbackRate.CurrentRate = rate;
            Tracing.TraceLine("remoteAudioProc: transmit capture now at " + rate + " callbacks a second (" + why + ")", TraceLevel.Info);
            return TxSwapResult.Swapped;
        }
        /// <summary>
        /// A microphone re-open (a swap's rescue, or tier 2 after a death) is on
        /// its worker: the channel holds no stream, and the gate starts nothing
        /// until it publishes (rulings repair 2).
        /// </summary>
        private bool _micReopenPending;

        /// <summary>
        /// The microphone could not be brought back: computer transmit audio is
        /// off for the rest of the run, through the receive-audio track's one
        /// give-up (#1145), so the give-up is said once whichever road reached it.
        /// </summary>
        private void GiveUpTxCapture(string why)
        {
            Tracing.TraceLine("remoteAudioProc: the microphone stream could not be brought back (" + why
                + "); computer transmit audio is unavailable for this connection", TraceLevel.Error);
            GiveUpMicStream();
        }

        /// <summary>
        /// Put a stream where there is none, opened on a worker (rulings repair
        /// 2): marks the microphone pending until <see cref="PublishTxCaptureReplacement"/>.
        /// </summary>
        private void BeginTxCaptureReplacement(audioChannelData channel, int rate, int secondRate, string why)
        {
            _micReopenPending = true;
            StartTxOpenJob(rate, TxOpenKind.Replace, channel, secondRate, why);
        }

        /// <summary>
        /// On the loop: a re-open is back. With a stream, it goes into the
        /// channel, NOT started: the gate's start starts it and resolves it as a
        /// restart (said or not by the receive-audio track's rules). Without one,
        /// the microphone is given up, said once.
        /// </summary>
        private void PublishTxCaptureReplacement(TxOpenJob job, audioChannelData channel)
        {
            _micReopenPending = false;
            var fresh = job.Stream;
            if (fresh == null)
            {
                GiveUpTxCapture(job.Why + ", and a new stream would not open (" + job.OpenMs + " ms, off the receive loop)");
                return;
            }
            lock (channel)
            {
                channel.PortAudioStream = fresh;
                channel.Started = false;
            }
            _txStreamAlreadyJudged = false;
            _txStreamPartialRun = false;
            _txStreamPartialReason = "";
            _txCallbackRateThisStream = job.OpenedRate;
            TxAudioCallbacksPerSecond = job.OpenedRate;
            TxCallbackRate.CurrentRate = job.OpenedRate;
            Tracing.TraceLine("remoteAudioProc: the microphone (" + job.Why + ") was opened again at "
                + job.OpenedRate + " callbacks a second (" + job.OpenMs + " ms, off the receive loop); the gate starts it on its next pass",
                TraceLevel.Warning);
            NoteMicReplacementPublished();
        }

        /// <summary>
        /// Every death of the transmit capture stream (rulings repair 2, Codex's
        /// join list), whatever recovers it: an open in flight against it is
        /// abandoned, the stream is marked PARTIAL so it earns no verdict at its
        /// close, and an automatic try running on it ends with no verdict (its
        /// callback counts span the death) and falls back to the shipped rate as
        /// soon as that is allowed, as a disturbed try does.
        /// </summary>
        private void NoteTxCaptureDied(string why)
        {
            AbandonTxOpenJob("the stream died (" + why + ")");
            _txStreamPartialRun = true;
            _txStreamPartialReason = "the stream died part way: " + why;
            if (!_txTryRunning) return;
            _txTryRunning = false;
            _txStreamAlreadyJudged = true;
            _txTryFallbackDue = true;
            _txTryFallbackWhy = "the stream died during the automatic try";
            Tracing.TraceLine("TxCallbackRate: automatic try at " + TxCallbackRate.Fast
                + " a second: no verdict, the stream died part way (" + why + "); back to " + TxCallbackRate.Shipped
                + " as soon as it is allowed", TraceLevel.Info);
        }

        /// <summary>
        /// #1145, tier 2: the transmit capture stream DIED and its tier-1
        /// restart failed (the receive-audio track's give-up point, rulings
        /// repair 2), so open a new one through the transmit open path, ON A
        /// WORKER. On the remote-audio loop's thread only. The dead stream is
        /// judged as a PARTIAL run (no verdict either way) and, if still open,
        /// closed off the loop; any automatic try on it ends with no verdict; the
        /// rate is chosen again (a held pass keeps the faster rate, otherwise the
        /// shipped one), and the shipped rate is opened if the faster will not.
        /// Until the new stream publishes the microphone is pending and the gate
        /// starts nothing; the gate's start after it resolves it as a restart.
        /// If nothing opens, or it will not start, the microphone is given up and
        /// said once. Bounded by the receive-audio track's recovery cap: this is
        /// the escalation of one recovery the cap already counted, never a new
        /// one. False when there is no connection to re-open on.
        /// </summary>
        /// <param name="why">What ended the stream, for the trace (for WASAPI, the HRESULT if known).</param>
        internal bool ReopenTxCaptureAfterDeath(string why)
        {
            var channel = opusInputChannel;
            if (channel == null) return false;
            NoteTxCaptureDied(why);
            JJAudioStream dead;
            lock (channel)
            {
                dead = channel.PortAudioStream;
                try { if (channel.Started && dead != null) dead.StopAudio(); } catch { }
                channel.Started = false;
                channel.PortAudioStream = null;
            }
            // The rate is chosen again below, so no fall back is owed.
            _txTryFallbackDue = false;
            if (dead != null)
            {
                JudgeTxCallbackRate(dead);
                // A failed start already closed an open stream (JJAudioStream.StartAudio);
                // only one still open is closed here, and off the loop.
                if (dead.IsOpen) CloseTxStreamOffLoop(dead);
            }
            ChooseTxCallbackRate(); // resets the partial flag for the stream about to open
            int rate = TxAudioCallbacksPerSecond;
            BeginTxCaptureReplacement(channel, rate, rate != TxCallbackRate.Shipped ? TxCallbackRate.Shipped : 0,
                "the stream died (" + why + ")");
            return true;
        }
    }
}
