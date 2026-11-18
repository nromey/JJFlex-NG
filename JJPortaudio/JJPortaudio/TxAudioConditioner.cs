using System;

namespace JJPortaudio
{
    /// <summary>
    /// The PC-side transmit conditioning chain (Track I): noise reduction,
    /// then the noise gate, with a monitor tap that can play what the chain
    /// REMOVED, after aligning the input copy to the applied reducer delay.
    /// Sits in the PortAudio input callback as the third thing at
    /// the established insertion point — the tone source injects, this
    /// modifies, the LUFS meter observes:
    ///
    ///     mic → tone injection → THIS → LUFS meter → Opus encode
    ///
    /// That order preserves the property the meter was built for: it always
    /// measures what genuinely goes out, conditioned audio and all.
    ///
    /// Stage order inside the chain is NR first, gate second. NR lowers the
    /// floor, which makes the gate threshold easier to derive and less likely
    /// to clip quiet speech; reversed, the gate would be judging a noisy
    /// signal.
    ///
    /// The noise-reduction stage is a pluggable delegate rather than a hard
    /// reference: the engine (NoiseReductionProvider, RNNoise) lives in the
    /// UI assembly with its NAudio dependency, and this class stays
    /// System-only so the numerical harness can link it and prove the
    /// residual arithmetic. Whether NR actually runs is the provider's own
    /// business (its Enabled/Strength/mode logic) — this chain just gives it
    /// the buffer.
    ///
    /// THE RESIDUAL MONITOR is why this is buildable by a blind operator at
    /// all: removed = input − output, played to a monitor. Listening to the
    /// OUTPUT can never reveal over-processing, because the missing parts are
    /// not there to hear; hear your own speech in the RESIDUAL and the chain
    /// is eating your voice. It also proves the pathway is live: processing
    /// that is enabled-but-bypassed and processing that is on-and-gentle both
    /// sound clean on the output, but bypassed produces actual SILENCE in the
    /// residual — not something quiet, nothing.
    ///
    /// Monitor modes: Output (what goes to the radio), Residual (what was
    /// removed), and Split (output in the left ear, residual in the right —
    /// the two-ears trade-off view; note that summing them would simply
    /// reconstruct the input, which is why "both" is a stereo split and not a
    /// mix). The sink receives interleaved stereo and must copy synchronously
    /// — it is called on the PortAudio callback thread.
    ///
    /// Thread model: Process runs on the PortAudio callback thread. Setters
    /// run on UI threads; delegate and enum reads are single-word and read
    /// once per buffer. The only allocation is growing the two scratch
    /// buffers and input delay line, on a stream shape or delay change.
    /// </summary>
    public class TxAudioConditioner
    {
        /// <summary>What the monitor tap carries.</summary>
        public enum MonitorModes
        {
            /// <summary>Tap off; sink never called.</summary>
            Off = 0,
            /// <summary>The processed audio — exactly what goes to the radio.</summary>
            Output = 1,
            /// <summary>What the chain removed: input minus output.</summary>
            Residual = 2,
            /// <summary>Output in the left channel, residual in the right.</summary>
            Split = 3
        }

        /// <summary>The transmit noise gate. Its threshold is set by the app
        /// from the measured noise floor (floor + margin), not by hand.</summary>
        public TxNoiseGate Gate { get; } = new TxNoiseGate();

        /// <summary>
        /// Pluggable noise-reduction stage, invoked in place ahead of the
        /// gate: (buffer, floatCount, sampleRate). Null means no NR stage is
        /// attached. The delegate itself decides whether to touch the buffer.
        /// </summary>
        public Action<float[], int, uint> NoiseReducer { get; set; }

        /// <summary>Called after NoiseReducer: actual delay for THIS buffer,
        /// in samples per channel; zero when the reducer bypassed it.</summary>
        public Func<int> NoiseReducerAppliedLatencySamples { get; set; }

        /// <summary>
        /// Monitor sink: (buffer, floatCount, sampleRate), interleaved
        /// stereo, called on the audio callback thread. Must copy the data
        /// synchronously and return quickly.
        /// </summary>
        public Action<float[], int, uint> MonitorSink { get; set; }

        private volatile int _monitorMode = (int)MonitorModes.Off;

        /// <summary>What the monitor tap plays. Live-switchable while
        /// transmitting.</summary>
        public MonitorModes MonitorMode
        {
            get { return (MonitorModes)_monitorMode; }
            set { _monitorMode = (int)value; }
        }

        private volatile bool _bypassAll;

        /// <summary>
        /// Asked once per buffer, on the audio thread: is the radio keyed?
        /// The app answers "our Mox OR the radio's interlock says transmitting"
        /// (Sprint 49 Track H, item B), because VOX keys only the interlock, so
        /// a key state built on our own Mox would miss every VOX over. Null
        /// means no radio: never keyed. A source that throws reads as keyed,
        /// which only ever defers a change.
        /// </summary>
        public Func<bool> KeySource { get; set; }

        private volatile bool _keyed;

        /// <summary>The key state the current (or last) buffer was processed under, read from <see cref="KeySource"/> at its start.</summary>
        public bool Keyed => _keyed;

        /// <summary>Key-downs seen at a buffer boundary since this chain was made. A test and trace counter.</summary>
        public long KeyDowns { get; private set; }

        private int _gateRequest = -1; // -1 none; 0 off; 1 on

        /// <summary>
        /// A whole set of gate settings waiting for an unkeyed buffer (Sprint 49
        /// Track H, repair round 3): a whole transmit setup's gate, its on or off
        /// AND its levels and threshold policy, so none of it lands partway
        /// through an over. Published whole by the control side; the audio
        /// thread takes it with one exchange, so nothing blocks it.
        /// </summary>
        public sealed class StagedGate
        {
            public bool Enabled;
            /// <summary>Whether <see cref="ThresholdDb"/> is to be set (a manual threshold); a derived one is left to the app's tick.</summary>
            public bool HasThreshold;
            public float ThresholdDb;
            public float AttackMs, HoldMs, ReleaseMs, RangeDb;
            public bool AutoThreshold;
            public float ThresholdMarginDb;
            public StagedGate Copy() => (StagedGate)MemberwiseClone();
        }

        private StagedGate _stagedGate;

        /// <summary>
        /// Stage a whole gate for the next unkeyed buffer. It replaces any staged
        /// set and any waiting on-or-off request, because it came later.
        /// </summary>
        public void StageGate(StagedGate gate)
        {
            System.Threading.Volatile.Write(ref _gateRequest, -1);
            System.Threading.Volatile.Write(ref _stagedGate, gate);
        }

        /// <summary>
        /// A lone gate change made while a staged set waits: applied to the staged
        /// set too, so the later request still wins when it is adopted. Nothing
        /// happens when nothing is staged. Lock-free.
        /// </summary>
        public void ChangeStagedGate(Action<StagedGate> change)
        {
            while (true)
            {
                var was = System.Threading.Volatile.Read(ref _stagedGate);
                if (was == null) return;
                var next = was.Copy();
                change(next);
                if (ReferenceEquals(System.Threading.Interlocked.CompareExchange(ref _stagedGate, next, was), was)) return;
            }
        }

        /// <summary>Whether a whole gate is waiting for an unkeyed buffer.</summary>
        public bool HasStagedGate => System.Threading.Volatile.Read(ref _stagedGate) != null;

        private volatile bool _autoThresholdInForce = true;
        private volatile float _thresholdMarginInForceDb = 8f;

        /// <summary>
        /// Whether the gate's threshold is derived from the measured floor, as IN
        /// FORCE on this chain: a staged setup changes it only when adopted. The
        /// app's derived-threshold tick reads this; a lone change sets it at once.
        /// </summary>
        public bool AutoThresholdInForce
        {
            get { return _autoThresholdInForce; }
            set { _autoThresholdInForce = value; }
        }

        /// <summary>The margin over the measured floor in force, dB; see <see cref="AutoThresholdInForce"/>.</summary>
        public float ThresholdMarginInForceDb
        {
            get { return _thresholdMarginInForceDb; }
            set { _thresholdMarginInForceDb = value; }
        }

        /// <summary>
        /// Turn the gate on or off at the next UNKEYED buffer (Sprint 49 Track H,
        /// repair round 1, R3): like a noise engine change, the gate's on and off
        /// never land mid-over. Writing <c>Gate.Enabled</c> directly still works
        /// at once, for code that owns its own timing (attach, tests).
        /// </summary>
        public void RequestGateEnabled(bool on) => System.Threading.Volatile.Write(ref _gateRequest, on ? 1 : 0);

        /// <summary>Read the key state for this buffer. Runs before the bypass, so a CW over still counts as an over.</summary>
        private bool ReadKey()
        {
            var source = KeySource;
            bool keyed;
            if (source == null) keyed = false;
            else
            {
                try { keyed = source(); }
                catch { keyed = true; }
            }
            bool was = _keyed;
            _keyed = keyed;
            // With a real key state, the key-down is what resets the gate open
            // (#1075), and a gap in the stream no longer does.
            Gate.ResetsOnStreamGap = source == null;
            if (keyed && !was)
            {
                KeyDowns++;
                Gate.ResetOpen();
            }
            return keyed;
        }

        /// <summary>
        /// Policy bypass for non-voice modes (CW, digital): the whole chain
        /// steps aside, monitor included. Set by the app on mode changes —
        /// RNNoise is speech-trained and a gate has no business shaping data
        /// audio.
        /// </summary>
        public bool BypassAll
        {
            get { return _bypassAll; }
            set { _bypassAll = value; }
        }

        // Scratch: the pre-processing copy of the input (for the residual)
        // and the buffer handed to the monitor sink.
        private float[] _inputCopy = Array.Empty<float>();
        private float[] _monitorBuf = Array.Empty<float>();
        private float[] _inputDelay = Array.Empty<float>();
        private int _inputDelayAt;

        /// <summary>
        /// Process one buffer of interleaved stereo float samples in place.
        /// Matches the TxAudioProcessorCallback contract; FlexBase hands this
        /// method to the input stream.
        /// </summary>
        public void Process(float[] buffer, int count, uint sampleRate)
        {
            if (!ReadKey())
            {
                // A whole staged gate first, then a lone on-or-off asked after it.
                var staged = System.Threading.Interlocked.Exchange(ref _stagedGate, null);
                if (staged != null)
                {
                    Gate.AttackMs = staged.AttackMs;
                    Gate.HoldMs = staged.HoldMs;
                    Gate.ReleaseMs = staged.ReleaseMs;
                    Gate.RangeDb = staged.RangeDb;
                    if (staged.HasThreshold) Gate.ThresholdDb = staged.ThresholdDb;
                    _autoThresholdInForce = staged.AutoThreshold;
                    _thresholdMarginInForceDb = staged.ThresholdMarginDb;
                    Gate.Enabled = staged.Enabled;
                }
                int request = System.Threading.Interlocked.Exchange(ref _gateRequest, -1);
                if (request >= 0) Gate.Enabled = request == 1;
            }
            if (_bypassAll) return;

            var nr = NoiseReducer;
            var appliedLatency = NoiseReducerAppliedLatencySamples;
            var sink = MonitorSink;
            var mode = (MonitorModes)_monitorMode;
            bool wantMonitor = mode != MonitorModes.Off && sink != null;
            bool anyStage = nr != null || Gate.Enabled;

            // Nothing attached, nothing monitored: stay entirely out of the
            // way. (The gate still wants its clock stamped so enabling it
            // mid-stream is not mistaken for a key-down; Process with the
            // gate disabled does exactly that and nothing else.)
            if (!anyStage && !wantMonitor)
            {
                Gate.Process(buffer, 0, sampleRate);
                return;
            }

            // Keep the untouched input while the stages modify the buffer —
            // the residual is a subtraction and needs both ends.
            if (_inputCopy.Length < count) _inputCopy = new float[count];
            Array.Copy(buffer, _inputCopy, count);

            // NR first, gate second — see class remarks.
            nr?.Invoke(buffer, count, sampleRate);
            // Keep this history even while the monitor is off. Zero bypass
            // leaves it frozen, just as the bypassed engine freezes its state.
            int delay = nr == null ? 0 : Math.Max(0, appliedLatency?.Invoke() ?? 0);
            DelayInputCopy(count, delay);
            Gate.Process(buffer, count, sampleRate);

            if (!wantMonitor) return;

            if (_monitorBuf.Length < count) _monitorBuf = new float[count];
            switch (mode)
            {
                case MonitorModes.Output:
                    Array.Copy(buffer, _monitorBuf, count);
                    break;
                case MonitorModes.Residual:
                    for (int i = 0; i < count; i++)
                        _monitorBuf[i] = _inputCopy[i] - buffer[i];
                    break;
                case MonitorModes.Split:
                    // Left = output, right = residual. The stream carries the
                    // mono mic duplicated on both channels, so channel 0 of
                    // each pair is a faithful mono rendering of either signal.
                    for (int i = 0; i + 1 < count; i += 2)
                    {
                        _monitorBuf[i] = buffer[i];
                        _monitorBuf[i + 1] = _inputCopy[i] - buffer[i];
                    }
                    break;
            }
            sink(_monitorBuf, count, sampleRate);
        }
        private void DelayInputCopy(int count, int delaySamples)
        {
            if (delaySamples == 0) return;
            int length = checked(delaySamples * 2); // stereo interleaved
            if (_inputDelay.Length != length)
            {
                var next = new float[length];
                int keep = Math.Min(length, _inputDelay.Length);
                // Preserve the latest pending samples. Increasing carry adds
                // silence before them, the same one-time lead as the bank.
                for (int i = 0; i < keep; i++)
                    next[length - keep + i] = _inputDelay[(_inputDelayAt + _inputDelay.Length - keep + i) % _inputDelay.Length];
                _inputDelay = next;
                _inputDelayAt = 0;
            }
            int at = _inputDelayAt;
            for (int i = 0; i < count; i++)
            {
                float raw = _inputCopy[i];
                _inputCopy[i] = _inputDelay[at];
                _inputDelay[at] = raw;
                if (++at == length) at = 0;
            }
            _inputDelayAt = at;
        }
    }
}
