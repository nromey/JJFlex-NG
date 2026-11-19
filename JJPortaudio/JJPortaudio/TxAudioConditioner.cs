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

        private volatile bool _drainInput;

        /// <summary>
        /// S16 (ruled 2026-11-18 21:34): set while an unkey is held for the
        /// noise reduction's delay. Every buffer that reaches this chain is
        /// silenced BEFORE the noise reduction, so the radio carries the chain's
        /// drained tail and never new microphone audio from after the release.
        /// Cleared by the first unkeyed buffer, or by the app when a key-down
        /// ends the hold and the over carries on.
        /// </summary>
        public bool DrainInput
        {
            get { return _drainInput; }
            set { _drainInput = value; }
        }

        private int _lastDelaySamples;
        private int _lastDelayRate;
        private long _lastDelayTick;

        /// <summary>A delay older than this (milliseconds) is not trusted: no buffer has reached the chain since, so nothing is in it to drain.</summary>
        public const int DelayFreshnessMs = 250;

        private void NoteDelay(int samples, uint sampleRate)
        {
            System.Threading.Volatile.Write(ref _lastDelaySamples, samples);
            System.Threading.Volatile.Write(ref _lastDelayRate, (int)sampleRate);
            System.Threading.Volatile.Write(ref _lastDelayTick, Environment.TickCount64);
        }

        /// <summary>
        /// The delay the noise reduction added to the last buffer through this
        /// chain, in milliseconds: the figure the reducer itself reports for that
        /// buffer (<see cref="NoiseReducerAppliedLatencySamples"/>, the engines
        /// actually running), not a second calculation. Zero when the chain is
        /// stood aside, nothing reduced it, or no buffer has come through for
        /// <see cref="DelayFreshnessMs"/> (a generated source that bypasses the
        /// chain, or a stopped capture).
        /// </summary>
        public double RecentNoiseDelayMs(long nowTick)
        {
            int samples = System.Threading.Volatile.Read(ref _lastDelaySamples);
            int rate = System.Threading.Volatile.Read(ref _lastDelayRate);
            long at = System.Threading.Volatile.Read(ref _lastDelayTick);
            if (samples <= 0 || rate <= 0 || nowTick - at > DelayFreshnessMs) return 0;
            return samples * 1000.0 / rate;
        }

        /// <summary>Key-downs seen at a buffer boundary since this chain was made. A test and trace counter.</summary>
        public long KeyDowns { get; private set; }

        // ── The gate's settings: one owner (Sprint 49 Track H, repair rounds 3 and 4) ──
        //
        // ONE published state holds the gate settings in force (Live) and a whole
        // set waiting for an unkeyed buffer (Pending). Every writer goes through
        // it: a whole setup stages Pending; a lone on-or-off changes Pending (so
        // it, too, lands only unkeyed); a lone level or threshold-policy change
        // changes Live AND Pending. The control side publishes each change as a
        // new state with a compare-and-swap; the audio thread adopts Pending by
        // swapping in a state the control side already built, and copies Live
        // onto the gate whenever Live is a new object. So a later lone change
        // always wins, whatever the audio thread is in the middle of: if the
        // audio side adopted first, the change lands on the adopted Live; if the
        // change landed first, the adoption carries it. Round 3 had a separate
        // staged record the audio side TOOK and then applied, and a lone change
        // made after the take found nothing to update and was overwritten
        // (Codex's re-check 3). No lock and no allocation on the audio thread.

        /// <summary>One complete set of gate settings. Immutable once published: change a <see cref="Copy"/>.</summary>
        public sealed class StagedGate
        {
            public bool Enabled;
            /// <summary>Whether <see cref="ThresholdDb"/> is applied (a manual threshold); a derived one is left to the app's tick.</summary>
            public bool HasThreshold;
            public float ThresholdDb = TxNoiseGate.DefaultThresholdDb;
            public float AttackMs = 3f, HoldMs = 150f, ReleaseMs = 200f, RangeDb = 25f;
            public bool AutoThreshold = true;
            public float ThresholdMarginDb = 8f;
            public StagedGate Copy() => (StagedGate)MemberwiseClone();
        }

        private sealed class GateState
        {
            public readonly StagedGate Live;
            public readonly StagedGate Pending;
            /// <summary>Pending as Live, built by the control side so adopting it allocates nothing on the audio thread.</summary>
            public readonly GateState Adopted;
            public GateState(StagedGate live, StagedGate pending)
            {
                Live = live; Pending = pending;
                Adopted = pending == null ? null : new GateState(pending, null);
            }
        }

        private GateState _gateState;
        private StagedGate _appliedLive;

        /// <summary>Tests only: called on the audio thread after a buffer has read (and maybe adopted) the gate state and before it writes the gate. Null in the app.</summary>
        internal Action GateTakenForTest;

        private void PublishGate(Func<GateState, GateState> change)
        {
            while (true)
            {
                var was = System.Threading.Volatile.Read(ref _gateState);
                if (ReferenceEquals(System.Threading.Interlocked.CompareExchange(ref _gateState, change(was), was), was)) return;
            }
        }

        private static StagedGate With(StagedGate from, Action<StagedGate> change)
        {
            var next = from.Copy();
            change(next);
            return next;
        }

        /// <summary>Stage a whole gate for the next unkeyed buffer. It replaces anything waiting, because it came later.</summary>
        public void StageGate(StagedGate gate)
        {
            if (gate == null) throw new ArgumentNullException(nameof(gate));
            var copy = gate.Copy();
            PublishGate(was => new GateState(was.Live, copy));
        }

        /// <summary>
        /// A lone level or threshold-policy change (depth, attack, hold, release,
        /// threshold, margin, auto threshold): in force from the next buffer, and
        /// carried into anything waiting, so it still wins when that is adopted.
        /// </summary>
        public void ChangeGate(Action<StagedGate> change)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            PublishGate(was => new GateState(With(was.Live, change), was.Pending == null ? null : With(was.Pending, change)));
        }

        /// <summary>Put a whole gate in force at once, with nothing waiting (attach: there is no over to protect yet).</summary>
        public void SetGateInForce(StagedGate gate)
        {
            if (gate == null) throw new ArgumentNullException(nameof(gate));
            var copy = gate.Copy();
            PublishGate(_ => new GateState(copy, null));
        }

        /// <summary>
        /// Turn the gate on or off at the next UNKEYED buffer (repair round 1,
        /// R3): like a noise engine change, the gate's on and off never land
        /// mid-over. It changes the waiting set (or starts one from the set in
        /// force), so it is ordered with a staged setup by when it was asked.
        /// </summary>
        public void RequestGateEnabled(bool on) =>
            PublishGate(was => new GateState(was.Live, With(was.Pending ?? was.Live, g => g.Enabled = on)));

        /// <summary>Whether a whole gate (or an on-or-off) is waiting for an unkeyed buffer.</summary>
        public bool HasStagedGate => System.Threading.Volatile.Read(ref _gateState).Pending != null;

        /// <summary>The gate settings in force now (not yet necessarily copied onto <see cref="Gate"/> if no buffer has run since).</summary>
        public StagedGate GateInForce => System.Threading.Volatile.Read(ref _gateState).Live;

        /// <summary>Whether the gate's threshold is derived from the measured floor, as IN FORCE: a waiting setup changes it only when adopted. The app's derived-threshold tick reads this.</summary>
        public bool AutoThresholdInForce => GateInForce.AutoThreshold;

        /// <summary>The margin over the measured floor in force, dB; see <see cref="AutoThresholdInForce"/>.</summary>
        public float ThresholdMarginInForceDb => GateInForce.ThresholdMarginDb;

        /// <summary>On the audio thread: adopt anything waiting (unkeyed only), then copy the set in force onto the gate if it is new.</summary>
        private void UpdateGate(bool keyed)
        {
            var state = System.Threading.Volatile.Read(ref _gateState);
            if (!keyed && state.Adopted != null)
            {
                // A lost race means a control change published meanwhile; it is
                // adopted on the next unkeyed buffer, carrying that change.
                if (ReferenceEquals(System.Threading.Interlocked.CompareExchange(ref _gateState, state.Adopted, state), state))
                    state = state.Adopted;
            }
            GateTakenForTest?.Invoke();
            ApplyLive(state.Live);
            // A lone change published while the copy above ran wins at once
            // rather than one buffer later.
            ApplyLive(System.Threading.Volatile.Read(ref _gateState).Live);
        }

        private void ApplyLive(StagedGate live)
        {
            if (ReferenceEquals(live, _appliedLive)) return;
            Gate.AttackMs = live.AttackMs;
            Gate.HoldMs = live.HoldMs;
            Gate.ReleaseMs = live.ReleaseMs;
            Gate.RangeDb = live.RangeDb;
            if (live.HasThreshold) Gate.ThresholdDb = live.ThresholdDb;
            Gate.Enabled = live.Enabled;
            _appliedLive = live;
        }

        /// <summary>Starts the published state at the gate's own defaults, already applied, so code that writes <see cref="Gate"/> directly before any change is not overwritten.</summary>
        public TxAudioConditioner()
        {
            var initial = new StagedGate();
            _gateState = new GateState(initial, null);
            _appliedLive = initial;
        }

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
            // The held unkey's drain ends with the over (S16): the first buffer
            // the key reads up is receive, and the microphone runs as normal.
            if (!keyed) _drainInput = false;
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
            UpdateGate(ReadKey());
            // S16: while an unkey is held for the noise reduction's delay, the
            // radio stays keyed and the capture keeps running, but nothing the
            // microphone hears from now on may go on air. Silence goes in, so
            // what comes out is the chain's drained tail of the audio captured
            // before the release, then silence. Before the bypass, so it holds
            // whatever the mode.
            if (_drainInput && count > 0) Array.Clear(buffer, 0, Math.Min(count, buffer.Length));
            if (_bypassAll) { NoteDelay(0, sampleRate); return; }

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
                NoteDelay(0, sampleRate);
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
            NoteDelay(delay, sampleRate);
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
