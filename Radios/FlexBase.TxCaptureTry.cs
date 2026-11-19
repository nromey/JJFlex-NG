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
        // Only an open stream is swapped in, on this loop thread, exactly as the gate's own producer
        // swaps are: stop the old (StopAudio waits for its callback to quiesce,
        // so the two never run together and the conditioner, the source mux and
        // the meter are only ever fed by one), start the new. If the new one
        // will not START, the old one, which is open and only stopped, is
        // started again; if even that fails, a stream is opened afresh at the
        // shipped rate, and only if THAT fails is the microphone given up, said
        // once. A fallback after a failed try is the same make-before-break
        // swap in the other direction, and a fallback whose open fails leaves
        // the faster stream running (dropping audio, but never silent) and is
        // asked again five seconds later.
        //
        // THE COST: the open, and the close of a stream no longer wanted, run on
        // a worker thread of their own since rulings repair 1 (Codex's P2: a
        // failed open waited five seconds on the loop that consumes receive), so
        // receive waits only for the swap's stop and start, the same order as the
        // gate's own producer swaps (25 to 110 ms, per the receive-audio findings
        // of 2026-11-18). Opening from a second thread is safe because the stream
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
        // the next unkeyed moment. Our own key-down takes the same lock before it
        // is sent, so one arriving during the stop and start waits for them (a
        // StopAudio and a StartAudio, normally one capture period) and keys a
        // radio whose microphone is already running. Hardware PTT and VOX cannot
        // wait: they are checked at the lock, and one in the stop-start window
        // itself is the residual, reported.

        /// <summary>A key-down we admitted counts as keyed for this long, until the radio's own report says so (or not).</summary>
        internal const int TxKeyAdmissionFreshMs = 2000;

        private object _txCaptureAdmissionObject;
        private object TxCaptureAdmission =>
            System.Threading.Volatile.Read(ref _txCaptureAdmissionObject)
            ?? System.Threading.Interlocked.CompareExchange(ref _txCaptureAdmissionObject, new object(), null)
            ?? _txCaptureAdmissionObject;
        private long _txKeyDownAdmissions;
        private long _txKeyAdmittedTick;

        /// <summary>Our key-down, admitted against a capture swap (the <see cref="Transmit"/> setter, before its write is queued). Waits while a swap's stop and start run.</summary>
        private void AdmitTxKeyDown()
        {
            lock (TxCaptureAdmission)
            {
                System.Threading.Interlocked.Increment(ref _txKeyDownAdmissions);
                System.Threading.Volatile.Write(ref _txKeyAdmittedTick, Math.Max(1, Environment.TickCount64));
            }
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

        /// <summary>One open, run off the loop.</summary>
        private sealed class TxOpenJob
        {
            public int Rate;
            public bool Fallback;
            public object Channel;
            public long KeyDownsAtStart;
            public long OpenMs;
            public JJAudioStream Stream;
            public int State;   // 0 opening, 1 opened and waiting for the loop, 2 taken by the loop, 3 abandoned
        }
        private TxOpenJob _txOpenJob;

        /// <summary>Tests only: runs a worker's job (inline, in the windowless tests). Null in the app: a background thread per job.</summary>
        internal Action<Action> TxCaptureWorkerForTest { get; set; }

        private void RunTxCaptureWork(string name, Action work)
        {
            var forTest = TxCaptureWorkerForTest;
            if (forTest != null) { forTest(work); return; }
            var thread = new System.Threading.Thread(() =>
            {
                try { work(); }
                catch (Exception ex) { Tracing.TraceLine("TxCallbackRate: " + name + " threw: " + ex.Message, TraceLevel.Error); }
            })
            { IsBackground = true, Name = name };
            thread.Start();
        }

        /// <summary>Close a stream nothing uses any more, off the loop (a close polls in 200 ms steps).</summary>
        private void CloseTxStreamOffLoop(JJAudioStream stream)
        {
            if (stream == null) return;
            RunTxCaptureWork("TxCaptureClose", () => { try { stream.Close(); } catch { } });
        }

        private void StartTxOpenJob(int rate, bool fallback, object channel)
        {
            var job = new TxOpenJob
            {
                Rate = rate, Fallback = fallback, Channel = channel,
                KeyDownsAtStart = System.Threading.Interlocked.Read(ref _txKeyDownAdmissions),
            };
            _txOpenJob = job;
            RunTxCaptureWork("TxCaptureOpen", () =>
            {
                long t0 = Environment.TickCount64;
                JJAudioStream opened = null;
                try { opened = OpenTxCaptureAt(rate); }
                catch (Exception ex) { Tracing.TraceLine("TxCallbackRate: the open threw: " + ex.Message, TraceLevel.Error); }
                job.OpenMs = Environment.TickCount64 - t0;
                job.Stream = opened;
                // Handed back only if nobody abandoned it meanwhile (the remote
                // audio stopped, the stream died): then it is closed here.
                if (System.Threading.Interlocked.CompareExchange(ref job.State, 1, 0) != 0)
                {
                    try { opened?.Close(); } catch { }
                    return;
                }
                System.Threading.Volatile.Write(ref _txTryNextCheckTick, 0); // the loop's next look picks it up
            });
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
            if (was == 1) CloseTxStreamOffLoop(job.Stream);
            if (was == 0 || was == 1)
                Tracing.TraceLine("TxCallbackRate: the " + (job.Fallback ? "fall back's" : "automatic try's") + " open at " + job.Rate
                    + " a second was abandoned: " + why, TraceLevel.Info);
        }

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
            var job = _txOpenJob;
            if (job != null)
            {
                if (System.Threading.Interlocked.CompareExchange(ref job.State, 2, 1) != 1) return;
                _txOpenJob = null;
                TxOpenReturned(job, channel);
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
                StartTxOpenJob(TxCallbackRate.Shipped, true, channel);
                TakeInlineTxOpen(channel);
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
            StartTxOpenJob(TxCallbackRate.Fast, false, channel);
            TakeInlineTxOpen(channel);
        }

        private string _txTryReason = "";

        /// <summary>A worker that ran inline (the windowless tests) is already back: take it on this same look.</summary>
        private void TakeInlineTxOpen(audioChannelData channel)
        {
            var job = _txOpenJob;
            if (job == null || System.Threading.Interlocked.CompareExchange(ref job.State, 2, 1) != 1) return;
            _txOpenJob = null;
            TxOpenReturned(job, channel);
        }

        /// <summary>On the loop: an open came back from its worker. Swap it in if admitted; otherwise close it off the loop and wait.</summary>
        private void TxOpenReturned(TxOpenJob job, audioChannelData channel)
        {
            var fresh = job.Stream;
            if (!ReferenceEquals(job.Channel, channel))
            {
                CloseTxStreamOffLoop(fresh); // a different connection now; its own try state was reset
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
            var result = SwapTxCaptureStreamAdmitted(fresh, job.Rate, why, job.KeyDownsAtStart, out string refusal);
            if (result == TxSwapResult.Refused)
            {
                CloseTxStreamOffLoop(fresh);
                Tracing.TraceLine("TxCallbackRate: the " + (job.Fallback ? "fall back" : "automatic try") + " at " + job.Rate
                    + " a second opened (" + job.OpenMs + " ms) but was not swapped in: " + refusal
                    + "; the microphone carries on as it was, and it is asked again at the next unkeyed moment", TraceLevel.Info);
                if (!job.Fallback) _txTryDone = false; // abandoned before it began: ask again later
                return;
            }
            if (job.Fallback)
            {
                if (result == TxSwapResult.Swapped) _txTryFallbackDue = false;
                return;
            }
            if (result != TxSwapResult.Swapped)
            {
                // The faster stream opened but would not start: that is the rate
                // failing on this device, and it is kept as a failure.
                var capture = ExpectedTxCapture(OpusTxSampleRateSetting);
                RecordTxTryVerdict(new TxCallbackVerdict
                {
                    Device = capture.Device, HostApi = capture.HostApi, SampleRate = capture.SampleRate, Channels = capture.Channels,
                    CallbacksPerSecond = TxCallbackRate.Fast, Passed = false, MeasuredUtc = DateTime.UtcNow,
                }, "failed: the faster stream opened but would not start; the microphone is back at " + _txCallbackRateThisStream);
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
        /// Open the transmit capture at <paramref name="rate"/> callbacks a
        /// second, wired exactly as the first open in remoteAudioProc wires it
        /// (the input source mux, the conditioner, the loudness meter, and the
        /// key state on the conditioner and the stream's pipeline), and NOT
        /// started. Null when it would not open, with nothing left behind.
        /// </summary>
        /// <remarks>
        /// The first open in remoteAudioProc is not routed through this yet: the
        /// receive-audio track is changing that method, and this round was told
        /// not to. <c>TxCaptureTryTests</c> holds the two wiring lists equal, so
        /// neither can gain a line the other lacks. When that track lands, the
        /// first open should call this.
        /// </remarks>
        private JJAudioStream OpenTxCaptureAt(int rate)
        {
            var forTest = TxCaptureOpenForTest;
            if (forTest != null) return forTest(rate);
            var stream = new JJAudioStream();
            bool opened;
            try
            {
                opened = stream.OpenOpus(Devices.DeviceTypes.input, OpusTxSampleRateSetting, sendOpusInput,
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
            stream.InputSource = TxInputSources;
            stream.InputProcessor = txConditioner.Process;
            stream.InputLufsMeter = txLufsMeter;
            txConditioner.KeySource = TransmitKeySource;
            var pipeline = stream.TransmitPipeline;
            if (pipeline != null) pipeline.KeySource = TransmitKeySource;
            return stream;
        }

        private enum TxSwapResult { Refused, Swapped, NotStarted }

        /// <summary>
        /// Put an open, wired stream in place of the running one, on the loop's
        /// thread, as ONE admitted step (rulings repair 1): under the capture
        /// admission lock, ask again whether it is allowed (see the header above),
        /// then stop the old and start the new; if the new will not start, start
        /// the old again. Only if that fails too is a stream opened afresh at the
        /// shipped rate, outside the lock. The old stream is closed off the loop
        /// once it is out of use.
        /// </summary>
        private TxSwapResult SwapTxCaptureStreamAdmitted(JJAudioStream fresh, int rate, string why, long keyDownsAtStart, out string refusal)
        {
            refusal = null;
            var channel = opusInputChannel;
            if (channel == null || fresh == null) { refusal = "the connection ended"; return TxSwapResult.Refused; }
            JJAudioStream old;
            bool rescue = false;
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
                    if (old != null) old.StopAudio();
                    channel.Started = false;
                    channel.PortAudioStream = fresh;
                    if (fresh.StartAudio()) channel.Started = true;
                    else
                    {
                        // StartAudio closed the new stream on its failure. The old one
                        // is open and only stopped: start it again.
                        channel.PortAudioStream = old;
                        channel.Started = old != null && old.StartAudio();
                        rescue = !channel.Started;
                        if (rescue) { channel.PortAudioStream = null; old = null; } // its own failed start closed it
                    }
                }
            }
            if (!channel.Started || !ReferenceEquals(channel.PortAudioStream, fresh))
            {
                if (rescue)
                {
                    // Both refused. Open afresh at the shipped rate, on this loop: the
                    // microphone is already lost, and this is the double-failure path
                    // only (reported). Outside the admission lock, so a key-down never
                    // waits for an open.
                    var again = OpenTxCaptureAt(TxCallbackRate.Shipped);
                    lock (channel)
                    {
                        channel.PortAudioStream = again;
                        channel.Started = again != null && again.StartAudio();
                    }
                    if (!channel.Started) GiveUpTxCapture(channel, "neither the old nor a new microphone stream would start");
                    else { _txCallbackRateThisStream = TxCallbackRate.Shipped; TxAudioCallbacksPerSecond = TxCallbackRate.Shipped; }
                }
                return TxSwapResult.NotStarted;
            }
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
        private bool _txCaptureGivenUpSaid;

        /// <summary>The microphone could not be brought back: computer transmit audio is off for this connection, said once.</summary>
        private void GiveUpTxCapture(audioChannelData channel, string why)
        {
            opusInputAvailable = false;
            channel.PortAudioStream = null;
            channel.Started = false;
            Tracing.TraceLine("remoteAudioProc: the microphone stream could not be brought back (" + why
                + "); computer transmit audio is unavailable for this connection", TraceLevel.Error);
            if (_txCaptureGivenUpSaid || SuppressSpeech) return;
            _txCaptureGivenUpSaid = true;
            try
            {
                ScreenReaderOutput.Speak(Lexicon.Get("audio.mic.stopped_not_restarted"), Speech.SpeechIntent.Queue,
                    VerbosityLevel.Critical, subject: Speech.SpeechSubject.TransmitMicrophoneLost);
            }
            catch { }
        }

        /// <summary>
        /// #1145, tier 2, the part handed to Sprint 49: the transmit capture
        /// stream DIED (the receive-audio track's detection, from PortAudio's
        /// finished callback, or a start that failed after its tier-1 restart),
        /// so open it again through the transmit open path. On the remote-audio
        /// loop's thread only. The dead stream is judged as a PARTIAL run (no
        /// verdict either way) and closed; any automatic try on it ends with no
        /// verdict; the rate is chosen again (a held pass keeps the faster rate;
        /// otherwise the shipped one), falling back to the shipped rate if the
        /// faster will not open; the new stream is wired and left for the gate's
        /// next start. If nothing opens, computer transmit audio is given up for
        /// the connection and said once. True when a stream is in place.
        /// </summary>
        /// <param name="why">What ended the stream, for the trace (for WASAPI, the HRESULT if known).</param>
        internal bool ReopenTxCaptureAfterDeath(string why)
        {
            var channel = opusInputChannel;
            if (channel == null) return false;
            AbandonTxOpenJob("the stream died (" + why + ")");
            JJAudioStream dead;
            lock (channel)
            {
                dead = channel.PortAudioStream;
                try { if (channel.Started && dead != null) dead.StopAudio(); } catch { }
                channel.Started = false;
                channel.PortAudioStream = null;
            }
            _txStreamPartialRun = true;
            _txStreamPartialReason = "the stream died part way: " + why;
            if (_txTryRunning)
            {
                _txTryRunning = false;
                Tracing.TraceLine("TxCallbackRate: automatic try at " + TxCallbackRate.Fast
                    + " a second: no verdict, the stream died part way (" + why + ")", TraceLevel.Info);
            }
            _txTryFallbackDue = false;
            if (dead != null)
            {
                JudgeTxCallbackRate(dead);
                try { dead.Close(); } catch { }
            }
            ChooseTxCallbackRate(); // resets the partial flag for the stream about to open
            int rate = TxAudioCallbacksPerSecond;
            var fresh = OpenTxCaptureAt(rate);
            if (fresh == null && rate != TxCallbackRate.Shipped)
            {
                rate = TxCallbackRate.Shipped;
                fresh = OpenTxCaptureAt(rate);
            }
            if (fresh == null)
            {
                GiveUpTxCapture(channel, "the stream died (" + why + ") and would not open again");
                return false;
            }
            lock (channel) channel.PortAudioStream = fresh;
            _txCallbackRateThisStream = rate;
            TxAudioCallbacksPerSecond = rate;
            TxCallbackRate.CurrentRate = rate;
            opusInputAvailable = true;
            Tracing.TraceLine("remoteAudioProc: the microphone stream died (" + why + ") and was opened again at "
                + rate + " callbacks a second; the gate starts it on its next pass", TraceLevel.Warning);
            return true;
        }
    }
}
