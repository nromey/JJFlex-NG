using System;
using System.Diagnostics;
using System.Threading;
using JJPortaudio;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// PC-side audio processing pipeline for RX audio.
    /// Chains processing stages between Opus decode and PortAudio playback:
    /// Capture listens first, then the spectral and neural slots run, followed
    /// by the warning duck and recording playback level.
    ///
    /// Works on ALL radios (6000/8000/Aurora) since processing runs on the PC,
    /// unlike radio-side NR which requires 8000/Aurora hardware DSP.
    ///
    /// Designed as the single insertion point for all PC-side audio processing.
    /// Future stages (waterfall FFT tap, recording tap, DSP filters) plug in here.
    ///
    /// Thread safety: Process() is called from FlexBase's remoteAudioProc thread.
    /// Control state is published by reference. The audio thread owns both
    /// slots while the interlocked process mark is held; disconnect waits
    /// before freeing their native state.
    /// Sprint 25 Phase 20.
    /// </summary>
    public class RxAudioPipeline : IDisposable, IRxContentClassifier
    {
        private SpectralSubtractionProvider _spectralSub;
        private SpectralSubtractionProvider _processingCapture;
        private readonly Action<PerChannelDenoiserBank> _prepareSpectral;
        private readonly DenoiseEngineCatalog _catalog;
        /// <summary>The engine catalog the slots were built from (Sprint 49 Track P: the load-time shared-delay plan reads it).</summary>
        internal DenoiseEngineCatalog Catalog => _catalog;
        public PcDenoiseSlot SpectralSlot { get; }
        public PcDenoiseSlot NeuralSlot { get; }
        private int _processMark; // 0 idle, 1 inside Process, 2 permanently closed
        private int _disposed;
        private string _mode = "";
        public event EventHandler<DenoiseSelectionResult>? SelectionResult;
        public string CurrentMode => Volatile.Read(ref _mode);
        public bool SelectPcEngine(string id, out string? refusal)
        {
            var row = _catalog.Find(id);
            if (row == null) { refusal = "unknown-engine"; return false; }
            if (row.Side != DenoiseSide.Pc) { refusal = "radio-engine"; return false; }
            return (row.Family == DenoiseFamily.Spectral ? SpectralSlot : NeuralSlot).Select(id, out refusal);
        }
        public bool SelectPcEngine(string id) => SelectPcEngine(id, out _);
        // THE MODE GATE IS RETIRED (Sprint 49 Track E; ruled by Noel 2026-11-07,
        // question 4: "retire the switch"). "PC Neural NR Voice Modes Only" held
        // the neural slot off in CW and both slots off in digital modes. Per-mode
        // memory replaced it: no engine is locked out of any mode, and each mode
        // group remembers what the operator last used in it (NoiseController).
        // So the slots run whatever is on, in every mode.
        private void ForwardSelection(object? sender, DenoiseSelectionResult result) => SelectionResult?.Invoke(this, result);
        public string CaptureFinishKey() => CaptureFinishKey(SpectralEnabled, SpectralSlot.ChosenEngineId);
        /// <summary>
        /// What the capture's finish sentence says next. No mode holds the
        /// spectral slot back any more, so the "held by mode" fragment is gone:
        /// it would now be untrue.
        /// </summary>
        public static string CaptureFinishKey(bool on, string selection) =>
            on && selection == "spectral_capture" ? "audio.noise.capture.spectral_is_using_it" : "audio.noise.capture.turn_spectral_on";
        private readonly int _channels;

        /// <summary>Kept for the duck's ramp, which has to convert a time
        /// constant into a per-frame increment.</summary>
        private readonly int _sampleRate;

        /// <summary>
        /// Create the pipeline for the given audio format.
        /// Both providers are created in standalone mode (no ISampleProvider source).
        /// </summary>
        public RxAudioPipeline(int sampleRate = 48000, int channels = 2, DenoiseEngineCatalog? catalog = null)
            : this(sampleRate, channels, catalog, 0) { }

        /// <summary>
        /// As above, with the running tally's period in stopwatch ticks (#1229):
        /// one minute when 0. Tests shorten it so a period passes in a few buffers.
        /// </summary>
        internal RxAudioPipeline(int sampleRate, int channels, DenoiseEngineCatalog? catalog, long tallyPeriodTicks)
        {
            _channels = channels;
            _sampleRate = sampleRate;
            _catalog = catalog ?? new DenoiseEngineCatalog();
            // One engine-switch budget for both slots (#873 phase 2): they run in series
            // on one thread, so two drains at once must not double the rate.
            _switchBudget = new RxLagBudget(sampleRate);
            _classifier = new RxContentClassifier();
            SpectralSlot = new(DenoiseFamily.Spectral, "spectral_capture", sampleRate, channels, _catalog, DenoiseDirection.Receive, _switchBudget);
            NeuralSlot = new(DenoiseFamily.Neural, "rnnoise", sampleRate, channels, _catalog, DenoiseDirection.Receive, _switchBudget);
            SpectralSlot.SelectionResult += ForwardSelection;
            NeuralSlot.SelectionResult += ForwardSelection;
            _spectralSub = new SpectralSubtractionProvider(sampleRate, channels);
            _processingCapture = _spectralSub;
            _prepareSpectral = PrepareSpectral;
            _spectralKeepUp = new SlotKeepUp(this, DenoiseFamily.Spectral, tallyPeriodTicks);
            _neuralKeepUp = new SlotKeepUp(this, DenoiseFamily.Neural, tallyPeriodTicks);
            _keepUpRaiser = new KeepUpRaiser(this);

            Trace.WriteLine($"RxAudioPipeline: created ({sampleRate}Hz, {channels}ch)");
        }

        // --- RNNoise properties ---

        /// <summary>Enable/disable PC-side neural noise reduction (RNNoise).</summary>
        public bool RnnEnabled
        {
            get => NeuralSlot.Enabled;
            set => NeuralSlot.Enabled = value;
        }

        /// <summary>RNNoise wet/dry mix: 0.0 = bypass, 1.0 = fully processed. Default from <see cref="NoiseFloor.DefaultStrength"/> (floor 10, stored 0.9).</summary>
        public float RnnStrength
        {
            get => NeuralSlot.GetStrength("rnnoise");
            set => NeuralSlot.SetStrength("rnnoise", value);
        }

        // --- Spectral subtraction properties ---

        /// <summary>Enable/disable PC-side spectral subtraction.</summary>
        public bool SpectralEnabled
        {
            get => SpectralSlot.Enabled;
            set => SpectralSlot.Enabled = value;
        }

        /// <summary>Subtraction aggressiveness: 0.0 = none, 1.0 = max. Default 0.7.</summary>
        public float SpectralStrength
        {
            get => (float)SpectralSlot.GetSetting("spectral_capture", "subtraction_amount").Number;
            set => SpectralSlot.TrySetSetting("spectral_capture", "subtraction_amount", new EngineSettingValue(Math.Round(Math.Clamp(value, 0f, 1f), 2)));
        }

        /// <summary>Spectral floor to prevent musical noise artifacts. Default 0.02.</summary>
        public float SpectralFloor
        {
            get => (float)SpectralSlot.GetSetting("spectral_capture", "spectral_floor").Number;
            set => SpectralSlot.TrySetSetting("spectral_capture", "spectral_floor", new EngineSettingValue(Math.Round(Math.Clamp(value, 0f, 0.2f), 3)));
        }

        /// <summary>True while actively sampling noise profile.</summary>
        public bool IsNoiseSampling => _spectralSub.IsSampling;

        /// <summary>Noise sampling progress 0.0-1.0.</summary>
        public float NoiseSamplingProgress => _spectralSub.SamplingProgress;

        /// <summary>True if a noise profile has been captured.</summary>
        public bool HasNoiseProfile => _spectralSub.HasProfile;

        /// <summary>Name of the loaded noise profile.</summary>
        public string NoiseProfileName => _spectralSub.ProfileName;

        // --- Mode tracking ---

        /// <summary>
        /// Set the current slice mode (USB, CW, DIGU and so on). Called by the UI
        /// when the mode changes. It no longer turns any engine off (the mode gate
        /// was retired 2026-11-07, above); it tells the lag shifter what it is
        /// hearing (#873 phase 2): nothing is cut or repeated in DIGU, DIGL and
        /// RTTY, and CW's gap splices are kept short.
        /// </summary>
        public void SetCurrentMode(string mode)
        {
            Volatile.Write(ref _mode, mode ?? "");
            _classifier.SetMode(mode);
        }

        // --- The lag shifter's budget and classes (#873 phase 2, Sprint 49 Track P2) ---

        private readonly RxLagBudget _switchBudget;
        private readonly RxContentClassifier _classifier;

        /// <summary>Tests only: the engine-switch budget both slots spend from.</summary>
        internal RxLagBudget SwitchBudget => _switchBudget;

        /// <summary>Tests only: the content classifier.</summary>
        internal RxContentClassifier Classifier => _classifier;

        private RxContentClass _lastOutClass;

        /// <summary>
        /// The class of the buffer <see cref="Process"/> just returned, as it comes
        /// OUT (#873 phase 2, item 2): the input's class from as many blocks ago as
        /// the two slots delay the audio now, so the playback queue's catch-up
        /// splices audio by what that audio is, not by what the newest input is.
        /// Unclassified when the buffer was not processed.
        /// </summary>
        public RxContentClass LastBufferClass => _lastOutClass;

        // --- Noise profile management ---

        /// <summary>Start sampling noise. Call when the band is quiet.
        /// Default 3 seconds (range 1-5) per the ratified capture-window decision.</summary>
        public void StartNoiseSampling(int durationSeconds = 3)
        {
            var old = Volatile.Read(ref _spectralSub);
            var fresh = new SpectralSubtractionProvider(_sampleRate, _channels) { ProfileName = old.ProfileName };
            for (int ch = 0; ch < _channels; ch++) fresh.SetChannelProfile(ch, old.GetChannelProfile(ch));
            fresh.StartSampling(durationSeconds);
            Interlocked.Exchange(ref _spectralSub, fresh);
        }

        /// <summary>Cancel an in-progress noise sampling session.</summary>
        public void CancelNoiseSampling()
        {
            _spectralSub.CancelSampling();
        }

        /// <summary>Load a noise profile from file.</summary>
        public bool LoadNoiseProfile(string filePath)
        {
            return _spectralSub.LoadProfile(filePath);
        }

        /// <summary>Save the current noise profile to file.</summary>
        public bool SaveNoiseProfile(string filePath, string name, string band = "", string antenna = "")
        {
            return _spectralSub.SaveProfile(filePath, name, band, antenna);
        }

        /// <summary>Clear the current noise profile.</summary>
        public void ClearNoiseProfile()
        {
            _spectralSub.ClearProfile();
        }

        // --- Core processing ---

        /// <summary>
        /// Process a decoded audio buffer in-place.
        /// Capture listens to raw input, then the spectral slot and neural slot
        /// run before the warning duck and recording playback level.
        ///
        /// Called from FlexBase.remoteAudioProc thread via the PostDecodeProcessor delegate.
        /// </summary>
        public void Process(float[] buffer)
        {
            _lastOutClass = RxContentClass.Unclassified;
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _processMark, 1, 0) != 0) return;
            try
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                // A gap is time the audio was absent: from the end of the last
                // buffer's processing to the start of this one. Never from start
                // to start, which counts the pipeline's own processing as a pause
                // and let a very slow engine reset its own grace (repair 1, B3).
                long entered = Stopwatch.GetTimestamp();
                bool gap = _lastProcessEnd != 0 && entered - _lastProcessEnd > KeepUpGapTicks;
                int count = buffer.Length;
                // What this buffer is, for the lag shifter, read on the dry input
                // before anything changes it; each slot reads it at its own delay.
                var cls = _classifier.Classify(buffer, _channels);
                var capture = Volatile.Read(ref _spectralSub);
                bool capturing = capture.IsSampling;
                capture.ProcessInPlace(buffer, 0, count, _channels); // capture only, never enabled
                _processingCapture = capture;
                // Each slot is timed on its own and its running bank's late count
                // read either side, so the keep-up rule can tell which engine fell
                // behind (Sprint 49 Track L). Reads and arithmetic only.
                long start = Stopwatch.GetTimestamp();
                var spectralBank = SpectralSlot.RunningBank;
                long spectralLate = spectralBank?.LateFrames ?? 0;
                SpectralSlot.Process(buffer, true, capture.HasProfile, capturing, _prepareSpectral, cls, gap);
                long afterSpectral = Stopwatch.GetTimestamp();
                var neuralBank = NeuralSlot.RunningBank;
                long neuralLate = neuralBank?.LateFrames ?? 0;
                NeuralSlot.Process(buffer, true, true, false, null, cls, gap);
                _switchBudget.Advance(count / Math.Max(1, _channels));
                long afterNeural = Stopwatch.GetTimestamp();
                long bufferTicks = _channels > 0 && _sampleRate > 0
                    ? (long)((double)(count / _channels) * Stopwatch.Frequency / _sampleRate) : 0;
                _spectralKeepUp.After(SpectralSlot, spectralBank, spectralLate, afterSpectral - start, bufferTicks, gap, capturing, afterNeural);
                _neuralKeepUp.After(NeuralSlot, neuralBank, neuralLate, afterNeural - afterSpectral, bufferTicks, gap, false, afterNeural);
                ApplyDuck(buffer, count);
                ApplyPlaybackLevel(buffer, count);
                int lagBlocks = (SpectralSlot.HeardDelaySamples + NeuralSlot.HeardDelaySamples + RxContentClassifier.BlockFrames / 2) / RxContentClassifier.BlockFrames;
                _lastOutClass = _classifier.ClassOfBlock(_classifier.Blocks - 1 - lagBlocks);
                _lastProcessEnd = Stopwatch.GetTimestamp();
            }
            finally { Interlocked.Exchange(ref _processMark, 0); }
        }

        // --- Keeping up (Sprint 49 Track L) ---
        //
        // Whether each slot's engine keeps up with the audio. The rule runs on
        // this thread, per buffer, with no allocation; a change is published to
        // a small mailbox and the event is raised from the thread pool, never
        // from here. Receive only (ruled 2026-11-19, X3).

        /// <summary>
        /// A pause this long with no audio, from the end of one buffer's
        /// processing to the start of the next, is a gap in the stream, and the
        /// buffers after it are start-up again: a burst after an outage can make
        /// a helper engine's answers late without the engine being slow. Ten
        /// buffers' worth; the receive loop conceals shorter gaps to length.
        /// Time spent inside <see cref="Process"/> is never a gap, however long:
        /// that is the engine's cost, the very thing the rule judges (repair 1,
        /// B3: an engine at 55 ms a channel reset its own grace every buffer
        /// when the gap was measured from start to start).
        /// </summary>
        internal static readonly long KeepUpGapTicks = Stopwatch.Frequency / 10;

        private long _lastProcessEnd;
        private readonly SlotKeepUp _spectralKeepUp, _neuralKeepUp;
        private int _keepUpQueued;
        private readonly KeepUpRaiser _keepUpRaiser;

        /// <summary>
        /// An engine fell behind, recovered, or proved it keeps up after
        /// starting. Raised on a thread-pool thread, never the audio thread; a
        /// listener marshals to the UI itself.
        /// </summary>
        public event EventHandler<EngineKeepUpResult>? KeepUpResult;

        /// <summary>
        /// A helper engine's answers stopped, or started again, arriving in time
        /// (<see cref="EngineKeepUpResult.Delivery"/> set). A diagnostic only:
        /// traced, raised here for tests, and never the warning's trigger,
        /// because a fast engine can be starved of delivery and a slow one can
        /// be on time (repair 1, B1). Raised off the audio thread.
        /// </summary>
        internal event EventHandler<EngineKeepUpResult>? DeliveryDiagnostic;

        /// <summary>
        /// Which engine ids run in the helper, judged by the helper's own
        /// processing time rather than by timing the slot (NNR and NR2). A
        /// static lambda, so asking it allocates nothing; tests give their own
        /// fake helper engines here.
        /// </summary>
        internal Func<string, bool> IsHelperEngine { get; set; } = static id => Array.IndexOf(WdspHost.KnownEngines, id) >= 0;

        /// <summary>
        /// The helper's new work since the slot's last look: its processing time
        /// for one frame, added over every channel its one worker serves, and how
        /// many frames of audio that covers, each answer counted once (repair 2;
        /// <see cref="HelperServiceMeter"/>). Given the slot's running bank, the
        /// slot's own meter and the time. Called on the audio thread, so it must
        /// not allocate or wait; tests give their own figures here.
        /// </summary>
        internal HelperServiceSource HelperService { get; set; } = static (bank, meter, now) => meter.Read(bank, now);

        /// <summary>The rule for one slot, for tests.</summary>
        internal EngineKeepUpRule KeepUpRuleFor(DenoiseFamily family) =>
            (family == DenoiseFamily.Spectral ? _spectralKeepUp : _neuralKeepUp).Rule;

        /// <summary>The delivery diagnostic's rule for one slot, for tests.</summary>
        internal EngineKeepUpRule DeliveryRuleFor(DenoiseFamily family) =>
            (family == DenoiseFamily.Spectral ? _spectralKeepUp : _neuralKeepUp).Missed;

        private void QueueKeepUpRaise()
        {
            if (Interlocked.Exchange(ref _keepUpQueued, 1) == 0)
                ThreadPool.UnsafeQueueUserWorkItem(_keepUpRaiser, preferLocal: false);
        }

        /// <summary>
        /// PC audio went off: the receive loop that calls <see cref="Process"/>
        /// has ended (#1229). Called once, on that loop's own thread, before the
        /// streams are stopped (<c>FlexBase.AudioFeedEnded</c>). Each slot's open
        /// tally period is closed and its session total queued; the lines are
        /// written off this thread, like every keep-up line.
        /// </summary>
        public void FeedEnded()
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _processMark, 1, 0) != 0) return;
            try
            {
                long now = Stopwatch.GetTimestamp();
                _spectralKeepUp.FeedEnded(now);
                _neuralKeepUp.FeedEnded(now);
            }
            finally { Interlocked.Exchange(ref _processMark, 0); }
            QueueKeepUpRaise();
        }

        /// <summary>
        /// A tally line (#1229), raised off the audio thread after it is traced.
        /// For tests: the trace is the record.
        /// </summary>
        internal event EventHandler<SlotTallyReport>? TallyWritten;

        /// <summary>The running tally for one slot, for tests.</summary>
        internal SlotTally TallyFor(DenoiseFamily family) =>
            (family == DenoiseFamily.Spectral ? _spectralKeepUp : _neuralKeepUp).Tally;

        private void WriteTallies(SlotKeepUp probe)
        {
            while (probe.Tally.TryTake(out var report))
            {
                Trace.WriteLine("RxAudioPipeline: tally " + report.Describe());
                try { TallyWritten?.Invoke(this, report); }
                catch (Exception ex) { Trace.WriteLine("RxAudioPipeline: a tally listener threw: " + ex.Message); }
            }
        }

        private void RaiseKeepUp()
        {
            Volatile.Write(ref _keepUpQueued, 0);
            // Tally lines first and even after disposal: the session total is
            // queued as PC audio goes off, which a disconnect can follow at once.
            WriteTallies(_spectralKeepUp);
            WriteTallies(_neuralKeepUp);
            if (Volatile.Read(ref _disposed) != 0) return;
            Raise(_spectralKeepUp);
            Raise(_neuralKeepUp);
        }

        private void Raise(SlotKeepUp probe)
        {
            if (probe.Delivery.TakePublished(probe.Family, out var delivery))
            {
                Trace.WriteLine("RxAudioPipeline: helper " + delivery.Describe());
                try { DeliveryDiagnostic?.Invoke(this, delivery); }
                catch (Exception ex) { Trace.WriteLine("RxAudioPipeline: a delivery listener threw: " + ex.Message); }
            }
            if (!probe.Main.TakePublished(probe.Family, out var result)) return;
            Trace.WriteLine("RxAudioPipeline: keep-up " + result.Describe());
            try { KeepUpResult?.Invoke(this, result); }
            catch (Exception ex) { Trace.WriteLine("RxAudioPipeline: a keep-up listener threw: " + ex.Message); }
        }

        private sealed class KeepUpRaiser : IThreadPoolWorkItem
        {
            private readonly RxAudioPipeline _owner;
            public KeepUpRaiser(RxAudioPipeline owner) => _owner = owner;
            public void Execute() => _owner.RaiseKeepUp();
        }

        /// <summary>
        /// One published change, waiting for the pool thread. Written only by
        /// the audio thread under a sequence lock, so the pool thread reads a
        /// whole change or tries again, and the audio thread never waits.
        /// </summary>
        private sealed class Mailbox
        {
            private readonly bool _delivery;
            private int _seq, _taken; // _seq is odd while the audio thread writes
            private string _engine = "";
            private EngineKeepUpChange _change;
            private bool _helper;
            private int _late, _filled, _missed;
            private double _averageTicks, _bufferTicks;

            public Mailbox(bool delivery) => _delivery = delivery;

            public void Publish(EngineKeepUpRule rule, EngineKeepUpChange change, bool helper, double averageTicks, double bufferTicks, int missed)
            {
                Interlocked.Increment(ref _seq);
                _engine = rule.EngineId; _change = change; _helper = helper;
                _late = rule.LateInWindow; _filled = rule.Filled; _missed = missed;
                _averageTicks = averageTicks; _bufferTicks = bufferTicks;
                Interlocked.Increment(ref _seq);
            }

            /// <summary>The newest published change not yet taken, read whole. Off the audio thread only.</summary>
            public bool TakePublished(DenoiseFamily family, out EngineKeepUpResult result)
            {
                var spin = new SpinWait();
                while (true)
                {
                    int seq = Volatile.Read(ref _seq);
                    if ((seq & 1) != 0) { spin.SpinOnce(); continue; }
                    if (seq == Volatile.Read(ref _taken)) { result = null!; return false; }
                    var r = new EngineKeepUpResult(family, _engine, _change, _helper, _late, _filled,
                        _averageTicks * 1000.0 / Stopwatch.Frequency, _bufferTicks * 1000.0 / Stopwatch.Frequency, _missed, _delivery);
                    Interlocked.MemoryBarrier(); // the field reads above may not move past the second look
                    if (Volatile.Read(ref _seq) != seq) continue;
                    Volatile.Write(ref _taken, seq);
                    result = r;
                    return true;
                }
            }
        }

        /// <summary>
        /// One slot's keep-up state: the warning's rule, the delivery
        /// diagnostic's rule, the bank it last saw, and a mailbox for each.
        /// Written only by the audio thread.
        /// </summary>
        private sealed class SlotKeepUp
        {
            /// <summary>One helper frame's audio, in stopwatch ticks.</summary>
            private static readonly double HelperFrameTicks = (double)HelperServiceTime.FrameNanos * Stopwatch.Frequency / 1e9;

            private readonly RxAudioPipeline _owner;
            public readonly DenoiseFamily Family;
            /// <summary>The warning's rule: the slot's time for an engine here, the helper's processing time for a helper engine.</summary>
            public readonly EngineKeepUpRule Rule = new();
            /// <summary>
            /// A helper engine's delivery: buffers with a frame that did not come
            /// back by its deadline (the running bank's late count), and warm-ups
            /// that never end. Traced, never warned about.
            /// </summary>
            public readonly EngineKeepUpRule Missed = new();
            public readonly Mailbox Main = new(false), Delivery = new(true);
            /// <summary>The helper's new work since this slot last looked (repair 2): each answer counted once.</summary>
            public readonly HelperServiceMeter Meter = new();
            /// <summary>The running tally (#1229): a line a minute, at a stop or change, and a session total at PC audio off.</summary>
            public readonly SlotTally Tally;
            private PerChannelDenoiserBank? _bank;
            private bool _helper;
            private double _averageTicks, _bufferTicks;

            public SlotKeepUp(RxAudioPipeline owner, DenoiseFamily family, long tallyPeriodTicks)
            {
                _owner = owner; Family = family;
                Tally = new SlotTally(family, Stopwatch.Frequency, tallyPeriodTicks);
            }

            /// <summary>PC audio went off: the open period and the session total are queued.</summary>
            public void FeedEnded(long now) => Tally.FeedEnded(now);

            /// <summary>After the slot processed a buffer: what happened, told to the rules.</summary>
            public void After(PcDenoiseSlot slot, PerChannelDenoiserBank? bankBefore, long lateBefore, long elapsedTicks,
                long bufferTicks, bool gap, bool paused, long now)
            {
                if (gap) { Rule.Gap(); Missed.Gap(); Meter.Forget(); }
                string chosen = slot.ChosenEngineId;
                bool delivered = slot.BuildDelivered(chosen);
                var change = EngineKeepUpChange.None;
                var missed = EngineKeepUpChange.None;
                bool tallied = false; // a tally line was queued by this buffer (#1229)
                if (delivered || !string.Equals(chosen, slot.RunningEngineId, StringComparison.Ordinal))
                {
                    // A switch or a warm-up: two banks may run, and what is heard
                    // is changing, so this process's timing is not counted. A
                    // helper engine's warm-up is the exception: the helper's own
                    // processing time is its measure, so a helper too slow ever
                    // to finish warming is still found (EngineKeepUpRule.Warming).
                    if (delivered && slot.Enabled && !paused && _owner.IsHelperEngine(chosen))
                    {
                        _helper = true;
                        var service = _owner.HelperService(slot.RunningBank, Meter, now);
                        // Each frame of new work is one warm-up buffer judged; no new answers, none (repair 2).
                        // Every frame the meter forwarded is judged, however many (repair 4): the meter has
                        // already moved past them, so a frame not judged now is never judged.
                        bool late = HelperServiceTime.Late(service.PerFrameNanos);
                        AverageHelper(service.PerFrameNanos, service.Frames);
                        change = Rule.Warming(chosen, late, service.Frames);
                        missed = Missed.Warming(chosen);
                    }
                    else { Rule.Gap(); Missed.Gap(); Meter.Forget(); _averageTicks = 0; }
                    _bank = null;
                    // The tally counts only an engine running alone; through a
                    // switch or warm-up its period stays open and only the minute is kept.
                    tallied = Tally.Tick(now);
                }
                else
                {
                    string processing = slot.ProcessingEngineId;
                    var bank = slot.RunningBank;
                    if (processing.Length == 0 || bank == null) { Rule.Gap(); Missed.Gap(); Meter.Forget(); _bank = null; tallied = Tally.Idle(now); }
                    else if (!ReferenceEquals(bank, _bank) || !ReferenceEquals(bank, bankBefore)
                        || !string.Equals(processing, Rule.EngineId, StringComparison.Ordinal))
                    {
                        // A new bank, or the first buffer after a gap: start counting afresh.
                        _bank = bank;
                        _helper = bank.EngineFor(0) is WdspHelperEngine || _owner.IsHelperEngine(processing);
                        _averageTicks = 0;
                        Rule.Restart(processing);
                        Missed.Restart(processing);
                        Meter.Forget();
                        // Processed, but judged by nothing yet: counted as a buffer only.
                        tallied = Tally.Buffer(processing, _helper, now, 0, 0, false, 0, 0);
                    }
                    else if (_helper)
                    {
                        // The helper's work, counted once per answer against the
                        // audio it processed (repair 2): a buffer with no new
                        // answer on every served channel judges nothing, so a
                        // stall in delivery cannot repeat one slow answer, and a
                        // burst after it counts each answer once.
                        // Every frame the meter forwarded reaches the rule, however
                        // many (repair 4, after Codex's third re-check): repair 3's
                        // meter can forward hundreds at once when a channel leaves
                        // the round, and judging only a window of them lost the
                        // rest. The rule's batch form is exact and bounded in time.
                        var service = _owner.HelperService(bank, Meter, now);
                        bool late = HelperServiceTime.Late(service.PerFrameNanos);
                        AverageHelper(service.PerFrameNanos, service.Frames);
                        bool missedNow = bank.LateFrames != lateBefore;
                        int frames = service.Frames > 0 ? service.Frames : 0;
                        // The tally counts what the rule judges, not its grace (repair 1):
                        // a batch that crosses the end of the grace is judged in part.
                        int judged = Math.Max(0, frames - Rule.GraceLeft);
                        change = Rule.Observe(late, service.Frames);
                        missed = Missed.Observe(missedNow);
                        // The dip goes in with this buffer, before the buffer can close the minute (repair 1).
                        tallied = Tally.Buffer(processing, true, now, judged, late ? judged : 0, missedNow,
                            frames > 0 && service.PerFrameNanos > 0 ? (double)service.PerFrameNanos * frames * Stopwatch.Frequency / 1e9 : 0,
                            frames * HelperFrameTicks, missed == EngineKeepUpChange.FellBehind);
                    }
                    else
                    {
                        bool late = bufferTicks > 0 && elapsedTicks > bufferTicks;
                        Average(elapsedTicks, bufferTicks);
                        int judged = Rule.GraceLeft > 0 ? 0 : 1;
                        change = Rule.Observe(late);
                        tallied = Tally.Buffer(processing, false, now, judged, late ? judged : 0, false, elapsedTicks, bufferTicks);
                    }
                }
                if (tallied) _owner.QueueKeepUpRaise();
                // The delivery diagnostic is traced when it falls behind or comes
                // back; its KeepingUp would only be noise in the log.
                if (missed is EngineKeepUpChange.FellBehind or EngineKeepUpChange.Recovered)
                {
                    Delivery.Publish(Missed, missed, true, _averageTicks, _bufferTicks, Missed.LateInWindow);
                    _owner.QueueKeepUpRaise();
                }
                if (change != EngineKeepUpChange.None)
                {
                    Main.Publish(Rule, change, _helper, _averageTicks, _bufferTicks, _helper ? Missed.LateInWindow : 0);
                    _owner.QueueKeepUpRaise();
                }
            }

            /// <summary>
            /// <paramref name="frames"/> helper frames at one cost into the trace's
            /// running average: exactly <see cref="Average"/> that many times, in
            /// closed form (repair 4), so a large batch costs the audio thread nothing more.
            /// </summary>
            private void AverageHelper(long serviceNanos, int frames)
            {
                if (frames <= 0) return;
                double sample = serviceNanos <= 0 ? 0 : serviceNanos * HelperFrameTicks / HelperServiceTime.FrameNanos;
                _averageTicks = _averageTicks == 0
                    ? sample
                    : sample + (_averageTicks - sample) * Math.Pow(1 - 1.0 / Rule.Window, frames);
                _bufferTicks = HelperFrameTicks;
            }

            private void Average(double sampleTicks, double bufferTicks)
            {
                _averageTicks = _averageTicks == 0 ? sampleTicks : _averageTicks + (sampleTicks - _averageTicks) / Rule.Window;
                _bufferTicks = bufferTicks;
            }
        }

        private void PrepareSpectral(PerChannelDenoiserBank bank)
        {
            for (int ch = 0; ch < bank.Channels; ch++)
                if (bank.EngineFor(ch) is SpectralCaptureEngine engine)
                    engine.SetProfile(_processingCapture.GetChannelProfile(ch));
        }

        // --- Recording playback level (#787, bench 2026-10-25) ---
        //
        // After the warning duck, and separate from it: the duck is
        // warnings-only by ruling, and this one follows a recording playing on
        // this computer. The two multiply, which is right — a warning during
        // playback still dips the band further. See RxPlaybackLevel.

        private float _playbackGain = 1f;

        private void ApplyPlaybackLevel(float[] buffer, int count) =>
            _playbackGain = GlideGain(buffer, count, Math.Max(_channels, 1), _sampleRate,
                _playbackGain, RxPlaybackLevel.TargetGain, Math.Max(6f, RxPlaybackLevel.DepthDb));

        private const float FloorDb = -60f;

        private static float ToDb(float gain) => gain <= 0.001f ? FloorDb : 20f * MathF.Log10(gain);

        /// <summary>
        /// Ramp <paramref name="buffer"/> from <paramref name="gain"/> toward
        /// <paramref name="target"/>, per frame, and return where it got to.
        /// The ramp moves in DECIBELS, at a rate that crosses
        /// <paramref name="depthDb"/> in <see cref="RxPlaybackLevel.AttackMs"/>
        /// going down and <see cref="RxPlaybackLevel.ReleaseMs"/> coming back.
        /// A straight ramp in amplitude sounds like a jump at its start, because
        /// the ear hears loudness in decibels (retest 2026-10-25: "a bit of a
        /// blip as it brings the band up"). Ramped, never stepped, for the
        /// reason on <see cref="ApplyDuck"/>.
        /// </summary>
        internal static float GlideGain(float[] buffer, int count, int channels, float sampleRate,
            float gain, float target, float depthDb)
        {
            if (target >= 0.9999f && gain >= 0.9999f) return 1f;

            // Coming back to full is the band's half of the crossfade, so it is
            // the mirror of the recording's equal-power cosine: a sine in
            // amplitude, quick at first, from the lowered level to 1 over
            // ReleaseMs. The decibel curve rose slowly at first and left "a
            // slight dip in volume" (bench 2026-10-25 21:19). Where the glide
            // is on that sine is read back from the gain itself, so nothing
            // else has to be remembered between buffers.
            if (target >= 0.9999f && gain < target)
            {
                float low = MathF.Pow(10f, -Math.Max(0f, depthDb) / 20f);
                if (depthDb >= 59f) low = 0f; // muted
                float span = 1f - low;
                float t = span <= 0f ? 1f
                    : MathF.Asin(Math.Clamp((gain - low) / span, 0f, 1f)) * 2f / MathF.PI;
                float advance = 1f / Math.Max(1f, sampleRate * (RxPlaybackLevel.ReleaseMs / 1000f));
                for (int i = 0; i < count; i += channels)
                {
                    t = Math.Min(1f, t + advance);
                    // The shared ramp's equal-power member (#199, #873): the same sine as before.
                    gain = t >= 1f ? 1f : low + span * CrossFadeCurve.In(t);
                    for (int c = 0; c < channels && i + c < count; c++)
                        buffer[i + c] *= gain;
                }
                return gain;
            }

            float db = ToDb(gain), targetDb = ToDb(target);
            float ms = targetDb < db ? RxPlaybackLevel.AttackMs : RxPlaybackLevel.ReleaseMs;
            float step = depthDb / Math.Max(1f, sampleRate * (ms / 1000f));
            for (int i = 0; i < count; i += channels)
            {
                if (db > targetDb) db = Math.Max(targetDb, db - step);
                else if (db < targetDb) db = Math.Min(targetDb, db + step);
                // Arrived: exactly the target, so muted is a true zero.
                gain = db == targetDb ? target : db <= FloorDb ? 0f : MathF.Pow(10f, db / 20f);
                for (int c = 0; c < channels && i + c < count; c++)
                    buffer[i + c] *= gain;
            }
            return gain;
        }

        // --- Warning duck (#116) ---
        //
        // Last in the chain on purpose. The noise reducers are level-sensitive
        // — spectral subtraction works against a measured noise floor, and
        // RNNoise was trained on speech at natural levels — so pulling the
        // signal down in front of them would move the floor they are working
        // against every time a warning sounded. The duck is a listening
        // adjustment, not part of the cleanup, so it goes after both.

        /// <summary>Where the duck gain is right now, glided per frame toward
        /// <see cref="RxDuck.TargetGain"/>.</summary>
        private float _duckGain = 1f;

        /// <summary>
        /// Ramp the buffer toward the duck's target gain.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Ramped, never stepped.</b> A hard gain change mid-buffer is a
        /// discontinuity, and a discontinuity is a click — precisely the
        /// artifact class this whole area of the app exists to remove. So the
        /// gain moves by a fixed increment per FRAME (not per sample, or a
        /// stereo stream would ramp twice as fast as a mono one).
        /// </para>
        /// <para>
        /// The increment is sized so a full excursion takes
        /// <see cref="RxDuck.AttackMs"/> going down and
        /// <see cref="RxDuck.ReleaseMs"/> coming back. Both are read here on
        /// every buffer rather than cached, because since #535 they follow the
        /// operator's timing preset live — and the release also grows with
        /// the depth, so a deeper dip comes back at the same RATE rather than
        /// in the same time. Changing the depth changes how deep it goes and
        /// how long it takes to come home, never how steep the return is.
        /// </para>
        /// <para>
        /// <b>It converges on 1.0 whenever nothing is asking for a duck</b>,
        /// including after a request expires, after ducking is switched off
        /// mid-duck, and after anything at all goes wrong upstream. There is
        /// no state that has to be unwound; the only way to stay attenuated is
        /// for something to keep asking.
        /// </para>
        /// </remarks>
        private void ApplyDuck(float[] buffer, int count)
        {
            float target = RxDuck.TargetGain;

            // Nothing to do: not ducking, and not still on the way home.
            if (target >= 0.9999f && _duckGain >= 0.9999f) return;

            int channels = Math.Max(_channels, 1);
            float span = 1f - RxDuck.DuckedGain;
            if (span < 0.0001f) span = 1f; // depth of zero: glide home promptly

            float ms = target < _duckGain ? RxDuck.AttackMs : RxDuck.ReleaseMs;
            float step = span / Math.Max(1f, _sampleRate * (ms / 1000f));

            for (int i = 0; i < count; i += channels)
            {
                if (_duckGain > target) _duckGain = Math.Max(target, _duckGain - step);
                else if (_duckGain < target) _duckGain = Math.Min(target, _duckGain + step);

                for (int c = 0; c < channels && i + c < count; c++)
                    buffer[i + c] *= _duckGain;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            SpectralSlot.CloseAdmission(); NeuralSlot.CloseAdmission();
            var wait = Stopwatch.StartNew();
            var spin = new SpinWait();
            while (Interlocked.CompareExchange(ref _processMark, 2, 0) != 0)
            {
                if (wait.ElapsedMilliseconds >= 250)
                {
                    Trace.WriteLine("RxAudioPipeline: disconnect timed out; retaining engines rather than freeing during Process");
                    return;
                }
                spin.SpinOnce();
            }
            SpectralSlot.DisposeOwned(); NeuralSlot.DisposeOwned();
            _classifier.Dispose();
            Trace.WriteLine("RxAudioPipeline: disposed");
        }
    }
}
