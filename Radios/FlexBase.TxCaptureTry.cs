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
        // A failed open therefore changes nothing at all: the old stream never
        // stopped, and the try ends with no verdict. Only an open stream is
        // swapped in, on this loop thread, exactly as the gate's own producer
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
        // THE COST, said plainly: the open runs on the remote-audio loop's
        // thread, which also feeds receive playback, so receive audio waits for
        // the length of the open, once per try. That is the same order as every
        // producer swap the gate already makes (25 to 110 ms of polling, per the
        // receive-audio findings of 2026-11-18). The open was NOT moved to a pool
        // thread because the stream table's key generator (Audio's queues.Add,
        // an unsynchronized Random) is not safe to call from two threads at once,
        // and that file belongs to the receive-audio track's coordination. The
        // try's trace line carries the open's duration so the bench can see it.
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
                if (keyed || busy || sourceEngaged || !channel.Started) return; // at once, as soon as it is allowed
                var back = OpenTxCaptureAt(TxCallbackRate.Shipped);
                if (back == null)
                {
                    if (!_txTryFallbackFailedTraced)
                    {
                        _txTryFallbackFailedTraced = true;
                        Tracing.TraceLine("TxCallbackRate: could not open the microphone at " + TxCallbackRate.Shipped
                            + " a second to fall back; it carries on at " + TxCallbackRate.Fast
                            + " (never silent), asked again every " + TxTryFallbackRetryMs / 1000 + " seconds", TraceLevel.Error);
                    }
                    _txTryNextCheckTick = Environment.TickCount64 + TxTryFallbackRetryMs;
                    return;
                }
                if (SwapTxCaptureStream(back, TxCallbackRate.Shipped, "fall back after the automatic try: " + _txTryFallbackWhy))
                    _txTryFallbackDue = false;
                return;
            }

            var capture = ExpectedTxCapture(OpusTxSampleRateSetting);
            TxCallbackVerdict verdict = null;
            try { verdict = TxCallbackRate.Store?.Find(capture, TxCallbackRate.Fast); } catch { verdict = null; }
            var decision = TxCallbackRate.ShouldTry(AudioBuffering.ConfiguredTxCallbacksPerSecond(), TxCallbackRate.HelperFrameCostP99Nanos,
                verdict, DateTime.UtcNow, _txCallbackRateThisStream, _txTryDone, keyed, busy, channel.Started && !sourceEngaged);
            if (decision.Answer != TxTryAnswer.Try) return;

            _txTryDone = true;
            long t0 = Environment.TickCount64;
            var fresh = OpenTxCaptureAt(TxCallbackRate.Fast);
            _txTryOpenMs = Environment.TickCount64 - t0;
            if (fresh == null)
            {
                Tracing.TraceLine("TxCallbackRate: automatic try at " + TxCallbackRate.Fast + " a second (" + decision.Reason
                    + "): the faster stream would not open (" + _txTryOpenMs + " ms); the microphone carried on at "
                    + _txCallbackRateThisStream + " untouched; no verdict", TraceLevel.Info);
                return;
            }
            // Still allowed? The open took time on this thread.
            bool stillKeyed;
            try { stillKeyed = TransmitKeyedOrInterlock; } catch { stillKeyed = true; }
            if (stillKeyed || TxCheckOrRecordingRunning || !TxInputSources.Idle || !channel.Started)
            {
                fresh.Close();
                _txTryDone = false; // abandoned before it began: ask again later
                return;
            }
            if (!SwapTxCaptureStream(fresh, TxCallbackRate.Fast, "the automatic try"))
            {
                // The faster stream opened but would not start: that is the rate
                // failing on this device, and it is kept as a failure.
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

        /// <summary>
        /// Put an open, wired stream in place of the running one, on the loop's
        /// thread: stop the old, start the new; if the new will not start, start
        /// the old again; if that fails too, open afresh at the shipped rate.
        /// The old stream is closed once it is out of use. True when the new
        /// stream is the one running.
        /// </summary>
        private bool SwapTxCaptureStream(JJAudioStream fresh, int rate, string why)
        {
            var channel = opusInputChannel;
            if (channel == null || fresh == null) { fresh?.Close(); return false; }
            JJAudioStream old;
            bool swapped;
            lock (channel)
            {
                old = channel.PortAudioStream;
                bool wasStarted = channel.Started;
                if (wasStarted && old != null) old.StopAudio();
                channel.Started = false;
                channel.PortAudioStream = fresh;
                if (!wasStarted) swapped = true;
                else if (fresh.StartAudio()) { channel.Started = true; swapped = true; }
                else
                {
                    // StartAudio closed the new stream on its failure. The old one
                    // is open and only stopped: start it again.
                    swapped = false;
                    channel.PortAudioStream = old;
                    channel.Started = old != null && old.StartAudio();
                    if (!channel.Started)
                    {
                        // Both refused. The old stream is closed now too (its own
                        // failed start closed it), so open afresh at the shipped
                        // rate rather than starting a closed stream again.
                        old = null;
                        var rescue = OpenTxCaptureAt(TxCallbackRate.Shipped);
                        channel.PortAudioStream = rescue;
                        channel.Started = rescue != null && rescue.StartAudio();
                        if (!channel.Started) GiveUpTxCapture(channel, "neither the old nor a new microphone stream would start");
                        else { _txCallbackRateThisStream = TxCallbackRate.Shipped; TxAudioCallbacksPerSecond = TxCallbackRate.Shipped; }
                    }
                    return false;
                }
            }
            if (old != null && !ReferenceEquals(old, fresh))
            {
                // Judged as it closes, like any stream, unless the try already
                // judged it; a shipped-rate one earns nothing.
                JudgeTxCallbackRate(old);
                try { old.Close(); } catch { }
            }
            // The stream now running is a new one: nothing about it is judged or partial yet.
            _txStreamAlreadyJudged = false;
            _txStreamPartialRun = false;
            _txStreamPartialReason = "";
            _txCallbackRateThisStream = rate;
            TxAudioCallbacksPerSecond = rate;
            TxCallbackRate.CurrentRate = rate;
            Tracing.TraceLine("remoteAudioProc: transmit capture now at " + rate + " callbacks a second (" + why + ")", TraceLevel.Info);
            return swapped;
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
