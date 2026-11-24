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
    ///
    /// <para><b>The chain since Sprint 49 Track I</b> (#432, #1240, #1252):
    /// noise reduction, then the downward expander (<see cref="Gate"/>, the
    /// gate converted, with its side-chain filter and look-ahead), then the
    /// parametric equaliser (<see cref="Eq"/>), then the de-esser
    /// (<see cref="DeEsser"/>), then nothing. The order follows the
    /// recommended chain order recorded on #432 (isolation, expander,
    /// parametric EQ, compression last), with the de-esser after the EQ that
    /// boosts presence; JJ Clarity (#736) would sit after the EQ and is not
    /// built here. The order is data in the setup record; this build runs only
    /// the default order (<see cref="StageIds.DefaultOrder"/>).</para>
    /// <para><b>One owner, the whole chain.</b> <see cref="StagedGate"/>
    /// (the name is kept: renaming it would ripple through four repair rounds
    /// of tests) now carries the expander, the equaliser and the de-esser:
    /// switches and whole setups wait for an unkeyed buffer, continuous levers
    /// are live (S2, S15), and a whole setup lands on ONE buffer.</para>
    /// <para><b>The room's floor is measured here</b>, at the expander's input,
    /// after noise reduction, on unkeyed buffers only (<see cref="RoomFloor"/>,
    /// #1252), and the threshold rule is applied here
    /// (<see cref="RefreshDerivedThreshold"/>), so nothing about it needs a
    /// radio.</para>
    /// <para><b>The residual's "after" point</b> (S8, ruled 2026-11-23: "what
    /// was removed" is the cleanup stages only; 20:56, the de-esser is not
    /// one): <see cref="ResidualEndsAfter"/>, the expander's output. The EQ
    /// and the de-esser are heard in what goes out, never in the residual.</para>
    /// <para><b>The chain since Sprint 49 Track CL</b> (#1278): after the
    /// de-esser come the compressor stage (<see cref="Compressor"/>: the phase
    /// rotator, the drive, the compressor and its split-band mode) and, LAST,
    /// the limiter (<see cref="Limiter"/>). The limiter is the chain's
    /// guarantee on peaks, and it is only a guarantee while nothing follows
    /// it: when the order becomes the operator's to change, the limiter stays
    /// pinned last. Both ride in the same one-owner record
    /// (<see cref="StagedGate"/>) and neither is part of "what was removed",
    /// because both sit after <see cref="ResidualEndsAfter"/>. The chain's
    /// delay (<see cref="AppliedDelayFrames"/>) now also counts the limiter's
    /// look-ahead; the residual's own alignment does not, because the residual
    /// ends upstream of it.</para>
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

        /// <summary>The parametric equaliser, after the expander (Sprint 49 Track I, #1240).</summary>
        public ParametricEq Eq { get; } = new ParametricEq();

        /// <summary>The de-esser, after the equaliser (Sprint 49 Track I, item 4).</summary>
        public DeEsser DeEsser { get; } = new DeEsser();

        /// <summary>The compressor stage, after the de-esser: phase rotator, drive, compressor, split-band mode (Sprint 49 Track CL, #1278).</summary>
        public TxCompressor Compressor { get; } = new TxCompressor();

        /// <summary>The limiter, last in the chain (Sprint 49 Track CL, #1278).</summary>
        public TxLimiter Limiter { get; } = new TxLimiter();

        /// <summary>The room's floor at the expander's input, learned while unkeyed (#1252).</summary>
        public TxRoomFloor RoomFloor { get; } = new TxRoomFloor();

        /// <summary>The stages of this chain, by stable id, in the order they run.</summary>
        public static class StageIds
        {
            public const string NoiseReduction = "noise";
            public const string Expander = "expander";
            public const string Equaliser = "eq";
            public const string DeEsser = "deesser";
            /// <summary>The compressor stage (Sprint 49 Track CL): rotator, drive, compressor.</summary>
            public const string Compressor = "compressor";
            /// <summary>The limiter (Sprint 49 Track CL). Always last: see the class remarks.</summary>
            public const string Limiter = "limiter";

            /// <summary>The default order, and the only one this build runs.</summary>
            public static string[] DefaultOrder => new[] { NoiseReduction, Expander, Equaliser, DeEsser, Compressor, Limiter };

            /// <summary>
            /// Whether an order runs as the default here: it is the default, or
            /// the default with stages missing. A setup written before a stage
            /// existed lists the stages of its day in their default order (a
            /// Track I setup: noise, expander, eq, deesser); the stages added
            /// since run at their default places. An id this build does not
            /// know, a repeat, or a different order is not the default.
            /// </summary>
            public static bool RunsAsDefault(string[] order)
            {
                if (order == null) return true;
                var all = DefaultOrder;
                int at = 0;
                foreach (var id in order)
                {
                    while (at < all.Length && all[at] != id) at++;
                    if (at == all.Length) return false;
                    at++;
                }
                return true;
            }
        }

        /// <summary>
        /// Where "what was removed" ends (S8): the residual monitor plays the
        /// delay-aligned input minus the audio as it left THIS stage. Named once
        /// so it can be moved in one place; the de-esser was ruled out of it
        /// (2026-11-23 20:56, "it's not noise reduction").
        /// </summary>
        public const string ResidualEndsAfter = StageIds.Expander;

        /// <summary>
        /// Pluggable noise-reduction stage, invoked in place ahead of the
        /// gate: (buffer, floatCount, sampleRate). Null means no NR stage is
        /// attached. The delegate itself decides whether to touch the buffer.
        /// </summary>
        public Action<float[], int, uint> NoiseReducer { get; set; }

        /// <summary>
        /// Whether the plugged noise stage is switched on (Sprint 49 Track I,
        /// repair 1, N3): a plugged stage may be bypassing itself, so the plug
        /// alone does not say the chain is doing anything. Null: unknown, read
        /// as on when a stage is plugged. Asked off the audio thread only.
        /// </summary>
        public Func<bool> NoiseReducerEngaged { get; set; }

        /// <summary>
        /// Whether this chain is changing the transmitted audio at all: some
        /// stage is on (noise reduction, the expander, the equaliser, the
        /// de-esser) and the chain is not standing aside for CW or digital. One
        /// answer for every reader (repair 1, N3); off the audio thread.
        /// </summary>
        public bool AnyStageActive
        {
            get
            {
                if (_bypassAll) return false;
                bool nr = NoiseReducer != null && (NoiseReducerEngaged?.Invoke() ?? true);
                bool expander = Gate.Enabled || GateInForce.Enabled;
                bool eq = Eq.IsActive || GateInForce.Eq is { IsIdentity: false };
                bool deEss = DeEsser.Enabled || GateInForce.DeEsserEnabled;
                bool dynamics = Compressor.Settings.Enabled || GateInForce.Comp.Enabled
                    || Limiter.Settings.Enabled || GateInForce.Limiter.Enabled;
                return nr || expander || eq || deEss || dynamics;
            }
        }

        /// <summary>Called after NoiseReducer: actual delay for THIS buffer,
        /// in samples per channel; zero when the reducer bypassed it.</summary>
        public Func<int> NoiseReducerAppliedLatencySamples { get; set; }

        /// <summary>
        /// The expander's other mode (Sprint 49 Track I phase 2, ruled S6):
        /// asked, while the expander is on, to run in its place on this buffer,
        /// in place, with this buffer's key state. True when it ran (the
        /// expander here then only keeps its clock), false to leave the buffer
        /// to the expander. It decides its own mode switch, at an unkeyed buffer.
        /// </summary>
        public delegate bool ExpanderModeStage(float[] buffer, int count, uint sampleRate, bool keyed);

        /// <summary>The plugged <see cref="ExpanderModeStage"/>, or null: the expander always runs.</summary>
        public ExpanderModeStage ExpanderReplacement { get; set; }

        /// <summary>Called after <see cref="ExpanderReplacement"/> ran: its delay for THIS buffer, samples per channel.</summary>
        public Func<int> ExpanderReplacementAppliedLatencySamples { get; set; }

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

        /// <summary>
        /// One complete set of the in-process stages' settings: the expander
        /// (the gate converted), the equaliser and the de-esser. Immutable once
        /// published: change a <see cref="Copy"/>. Its defaults are the
        /// <see cref="TxNoiseGate"/>'s, <see cref="ParametricEq"/>'s and
        /// <see cref="JJPortaudio.DeEsser"/>'s own, so the state a new chain
        /// starts with is already what its stages hold.
        /// </summary>
        public sealed class StagedGate
        {
            public bool Enabled;
            /// <summary>Whether <see cref="ThresholdDb"/> is applied (a manual threshold); a derived one is left to the app's tick.</summary>
            public bool HasThreshold;
            public float ThresholdDb = TxNoiseGate.DefaultThresholdDb;
            public float AttackMs = 3f, HoldMs = 150f, ReleaseMs = 200f, RangeDb = 25f;
            public bool AutoThreshold = true;
            public float ThresholdMarginDb = 8f;
            // The expander (Sprint 49 Track I). The ratio and the side-chain's
            // corners are continuous levers; the side-chain filter's on and off
            // and the look-ahead's length are switches (the look-ahead moves the
            // stream in time).
            public float RatioToOne = TxNoiseGate.DefaultRatioToOne;
            public bool GateSetting = true;
            public bool SideChainEnabled;
            public float SideChainLowHz = TxNoiseGate.DefaultSideChainLowHz, SideChainHighHz = TxNoiseGate.DefaultSideChainHighHz;
            public float LookAheadMs;
            /// <summary>The equaliser's design, or null for off. Immutable, so a copy may share it.</summary>
            public ParametricEqDesign Eq;
            public bool DeEsserEnabled;
            public float DeEsserFrequencyHz = JJPortaudio.DeEsser.DefaultFrequencyHz, DeEsserAmountDb = JJPortaudio.DeEsser.DefaultAmountDb,
                DeEsserThresholdDb = JJPortaudio.DeEsser.DefaultThresholdDb;
            /// <summary>The compressor stage's settings (Sprint 49 Track CL). Immutable, so a copy may share it: on or off, the rotator's stage count and the mode are switches; every level is a continuous lever.</summary>
            public TxCompressorSettings Comp = TxCompressorSettings.Default;
            /// <summary>The limiter's settings (Sprint 49 Track CL). Immutable: on or off and the look-ahead's length are switches; the ceiling and the release are continuous levers.</summary>
            public TxLimiterSettings Limiter = TxLimiterSettings.Default;
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
        public void RequestGateEnabled(bool on) => RequestSwitch(g => g.Enabled = on);

        /// <summary>
        /// Any switch on the in-process stages (Sprint 49 Track I): the
        /// expander's side-chain filter on or off, its look-ahead's length, the
        /// equaliser on or off or a preset loaded, the de-esser on or off. Like
        /// <see cref="RequestGateEnabled"/>, it lands at the next UNKEYED buffer
        /// and is ordered with a staged setup by when it was asked (S2).
        /// </summary>
        public void RequestSwitch(Action<StagedGate> change)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            PublishGate(was => new GateState(was.Live, With(was.Pending ?? was.Live, change)));
        }

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
            Gate.RatioToOne = live.RatioToOne;
            Gate.GateSetting = live.GateSetting;
            Gate.SideChainEnabled = live.SideChainEnabled;
            Gate.SideChainLowHz = live.SideChainLowHz;
            Gate.SideChainHighHz = live.SideChainHighHz;
            Gate.LookAheadMs = live.LookAheadMs;
            Gate.Enabled = live.Enabled;
            // The room's meter hears what the detector hears (#1252).
            RoomFloor.SetSideChain(live.SideChainEnabled, Gate.SideChainLowHz, Gate.SideChainHighHz);
            Eq.UseFromAudioThread(live.Eq); // on the audio thread: the road that allocates nothing
            DeEsser.FrequencyHz = live.DeEsserFrequencyHz;
            DeEsser.AmountDb = live.DeEsserAmountDb;
            DeEsser.ThresholdDb = live.DeEsserThresholdDb;
            DeEsser.Enabled = live.DeEsserEnabled;
            Compressor.Settings = live.Comp ?? TxCompressorSettings.Default;
            Limiter.Settings = live.Limiter ?? TxLimiterSettings.Default;
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
        // Preallocated here, off the audio thread (Sprint 49 Track I, repair 1,
        // N4): 4,096 stereo frames, four times the largest buffer the transmit
        // stream hands over (480 frames), so choosing "what was removed"
        // allocates nothing on the callback. The cleanup copy grows only where
        // the input copy already did, never at a new moment.
        private const int ScratchFloats = 8192;
        private float[] _inputCopy = new float[ScratchFloats];
        private float[] _cleanupCopy = new float[ScratchFloats];
        private int _appliedDelay;
        private long _lastBufferMs;

        /// <summary>
        /// The chain's applied delay on the last buffer, frames: noise
        /// reduction's, plus the expander's look-ahead, plus the limiter's
        /// look-ahead (Sprint 49 Track CL; zero by default). The equaliser,
        /// the de-esser and the compressor add none. The audition and any
        /// later unkey hold align to this. The residual aligns to
        /// <see cref="ResidualDelayFrames"/>, which leaves the limiter out.
        /// </summary>
        public int AppliedDelayFrames => System.Threading.Volatile.Read(ref _appliedDelay);

        private volatile uint _lastSampleRate;

        /// <summary>The rate of the last buffer this chain was given, Hz, or 0 before any: the rate the transmit stream really opened at. The audition renders at 48 kHz and says so when this differs.</summary>
        public uint LastSampleRate => _lastSampleRate;

        private int _residualDelay;

        /// <summary>
        /// The delay of the audio at the residual's "after" point
        /// (<see cref="ResidualEndsAfter"/>) on the last buffer, frames: noise
        /// reduction's plus the expander's look-ahead. What "what was removed"
        /// aligns its input copy to; it never includes a stage after that point.
        /// </summary>
        public int ResidualDelayFrames => System.Threading.Volatile.Read(ref _residualDelay);

        /// <summary>Whether a buffer reached this chain in the last half second: the honest "is the computer's audio reaching it" test (a radio's own microphone sends none).</summary>
        public bool HasRecentAudio
        {
            get { long t = System.Threading.Volatile.Read(ref _lastBufferMs); return t != 0 && Environment.TickCount64 - t < 500; }
        }

        /// <summary>
        /// Apply THE threshold rule (<see cref="TxRoomFloor.ThresholdRule"/>)
        /// with the settings in force: set on the gate when it gives one, and
        /// returned; null when it left the gate alone. Called by whoever owns
        /// the policy tick; radio-neutral (#1252, ruled 2026-11-23 11:22).
        /// </summary>
        public float? RefreshDerivedThreshold(bool keyed)
        {
            var t = TxRoomFloor.ThresholdRule(AutoThresholdInForce, keyed, RoomFloor.HasFloor, RoomFloor.FloorDb, ThresholdMarginInForceDb);
            if (t is float db) Gate.ThresholdDb = db;
            return t;
        }

        /// <summary>One over's statistics from the stages after the expander, taken and zeroed: samples past full scale after the equaliser, and the de-esser's frames reduced and its deepest reduction. Counted on keyed buffers only.</summary>
        public (long EqOverFullScale, long DeEsserReducedFrames, float DeEsserMaxReductionDb) TakeOverStats()
        {
            long clips = Eq.TakeOverFullScale();
            var (frames, deepest) = DeEsser.TakeOverStats();
            return (clips, frames, deepest);
        }

        /// <summary>One over's readings from the compressor and the limiter, taken and zeroed (Sprint 49 Track CL). Counted on keyed buffers only.</summary>
        public ((float MaxReductionDb, float AverageReductionDb, long Frames) Compressor,
            (long Caught, long OutputOverFullScale, long LimitedFrames, float MaxReductionDb) Limiter) TakeDynamicsOverStats() =>
            (Compressor.TakeOverStats(), Limiter.TakeOverStats());

        private float[] _monitorBuf = new float[ScratchFloats];
        // The residual's delay line (Sprint 49 Track I, repair 2, N4; #1293 in
        // phase 2 and in Track CL): preallocated here, so a change of look-ahead
        // or engine delay re-lays the line in place, through a scratch of the
        // same size, and allocates nothing on the callback. Only a delay past
        // capacity grows it.

        /// <summary>
        /// The delay line's capacity, stereo frames (#1293). The whole chain's
        /// worst case is two noise slots, each with its bank's carry, then the
        /// expander: at 48 kHz NR2 (4,064 plus two 480-sample frames of helper
        /// pipeline, 5,024) and NNR Premium (2,454 plus three, 3,894) are 8,918,
        /// the two carries add 958, and the expander adds the larger of the
        /// longest look-ahead (10 ms at 192 kHz, 1,920) and WDSP's expander in
        /// its place (16 plus two frames of pipeline plus a carry, 1,455): 11,796
        /// frames. 16,384 holds that with room. (8,192 frames, before #1293,
        /// did not hold the two slots together.) Nothing after the residual's
        /// "after" point lengthens this line: the compressor adds no delay, and
        /// the limiter's look-ahead delays what is sent, not the residual's
        /// input history.
        /// </summary>
        public const int DelayLineCapacityFrames = 16384;

        /// <summary>
        /// The same capacity under the name of what the line holds: the delay
        /// of the audio at the residual's "after" point
        /// (<see cref="ResidualDelayFrames"/>). ONE allocation size; Track I
        /// phase 2 and Track CL each sized it for #1293 and the merge kept one.
        /// </summary>
        public const int ResidualDelayCapacityFrames = DelayLineCapacityFrames;
        private const int DelayLineFloats = DelayLineCapacityFrames * 2;
        private float[] _inputDelay = new float[DelayLineFloats];
        private float[] _inputDelayScratch = new float[DelayLineFloats];
        private int _inputDelayLen;
        private int _inputDelayAt;

        /// <summary>
        /// Process one buffer of interleaved stereo float samples in place.
        /// Matches the TxAudioProcessorCallback contract; FlexBase hands this
        /// method to the input stream.
        /// </summary>
        public void Process(float[] buffer, int count, uint sampleRate)
        {
            bool keyed = ReadKey();
            UpdateGate(keyed);
            System.Threading.Volatile.Write(ref _lastBufferMs, Environment.TickCount64);
            _lastSampleRate = sampleRate;
            if (_bypassAll) return;

            var nr = NoiseReducer;
            var appliedLatency = NoiseReducerAppliedLatencySamples;
            var sink = MonitorSink;
            var mode = (MonitorModes)_monitorMode;
            bool wantMonitor = mode != MonitorModes.Off && sink != null;
            bool anyStage = nr != null || Gate.Enabled || Eq.IsActive || DeEsser.Enabled
                || Compressor.Settings.Enabled || Limiter.Settings.Enabled;

            // Nothing attached, nothing monitored: stay entirely out of the
            // way. (The gate still wants its clock stamped so enabling it
            // mid-stream is not mistaken for a key-down; Process with the
            // gate disabled does exactly that and nothing else.) The room is
            // still learned, unkeyed (#1252): this input IS the expander's.
            if (!anyStage && !wantMonitor)
            {
                if (!keyed) RoomFloor.Process(buffer, count, sampleRate);
                Gate.Process(buffer, 0, sampleRate);
                System.Threading.Volatile.Write(ref _appliedDelay, 0);
                System.Threading.Volatile.Write(ref _residualDelay, 0);
                // Nothing on: the limiter still counts what leaves over full
                // scale on a keyed buffer (it touches nothing while off).
                Limiter.Process(buffer, count, sampleRate, keyed);
                return;
            }

            // Keep the untouched input while the stages modify the buffer —
            // the residual is a subtraction and needs both ends.
            if (_inputCopy.Length < count)
            {
                _inputCopy = new float[count];
                if (_cleanupCopy.Length < count) _cleanupCopy = new float[count];
            }
            Array.Copy(buffer, _inputCopy, count);

            // NR first, the expander second — see class remarks.
            nr?.Invoke(buffer, count, sampleRate);
            int nrDelay = nr == null ? 0 : Math.Max(0, appliedLatency?.Invoke() ?? 0);
            // The expander's input, after noise reduction: where the room's
            // floor is measured, unkeyed only (#1252; S15).
            if (!keyed) RoomFloor.Process(buffer, count, sampleRate);
            // The expander, or its other mode in its place (phase 2): then the
            // expander here only keeps its clock, and the mode's delay counts.
            var replacement = Gate.Enabled ? ExpanderReplacement : null;
            bool replaced = replacement != null && replacement(buffer, count, sampleRate, keyed);
            Gate.Process(buffer, replaced ? 0 : count, sampleRate);
            // Keep this history even while the monitor is off. Zero bypass
            // leaves it frozen, just as the bypassed engine freezes its state.
            // The look-ahead is the expander's applied delay (item 9).
            int expanderDelay = replaced ? Math.Max(0, ExpanderReplacementAppliedLatencySamples?.Invoke() ?? 0) : Gate.AppliedLookAheadFrames;
            int delay = nrDelay + expanderDelay;
            DelayInputCopy(count, delay);
            System.Threading.Volatile.Write(ref _residualDelay, delay);

            // The residual's "after" point (S8, ResidualEndsAfter): the audio
            // as it left the cleanup stages, before anything that shapes.
            bool wantResidual = wantMonitor && (mode == MonitorModes.Residual || mode == MonitorModes.Split);
            if (wantResidual)
            {
                Array.Copy(buffer, _cleanupCopy, count);
            }

            // Then the stages that shape: the equaliser, then the de-esser.
            // Neither adds delay. Their per-over counts are kept for keyed
            // buffers only; the policy tick writes them to the trace.
            Eq.Process(buffer, count, sampleRate, keyed);
            DeEsser.Process(buffer, count, sampleRate, keyed);

            // Then the dynamics (Sprint 49 Track CL): the compressor stage, and
            // the limiter LAST, so its ceiling holds whatever came before it.
            // Only the limiter's look-ahead adds delay, and only to the chain's
            // figure: the residual ended upstream.
            Compressor.Process(buffer, count, sampleRate, keyed);
            Limiter.Process(buffer, count, sampleRate, keyed);
            System.Threading.Volatile.Write(ref _appliedDelay, delay + Limiter.AppliedLookAheadFrames);

            if (!wantMonitor) return;

            if (_monitorBuf.Length < count) _monitorBuf = new float[count];
            switch (mode)
            {
                case MonitorModes.Output:
                    Array.Copy(buffer, _monitorBuf, count);
                    break;
                case MonitorModes.Residual:
                    for (int i = 0; i < count; i++)
                        _monitorBuf[i] = _inputCopy[i] - _cleanupCopy[i];
                    break;
                case MonitorModes.Split:
                    // Left = output, right = residual. The stream carries the
                    // mono mic duplicated on both channels, so channel 0 of
                    // each pair is a faithful mono rendering of either signal.
                    for (int i = 0; i + 1 < count; i += 2)
                    {
                        _monitorBuf[i] = buffer[i];
                        _monitorBuf[i + 1] = _inputCopy[i] - _cleanupCopy[i];
                    }
                    break;
            }
            sink(_monitorBuf, count, sampleRate);
        }
        private void DelayInputCopy(int count, int delaySamples)
        {
            if (delaySamples == 0) return;
            int length = checked(delaySamples * 2); // stereo interleaved
            if (_inputDelayLen != length)
            {
                int old = _inputDelayLen;
                if (_inputDelayScratch.Length < length) _inputDelayScratch = new float[length]; // past capacity only
                var next = _inputDelayScratch;
                Array.Clear(next, 0, length);
                int keep = Math.Min(length, old);
                // Preserve the latest pending samples. Increasing carry adds
                // silence before them, the same one-time lead as the bank.
                for (int i = 0; i < keep; i++)
                    next[length - keep + i] = _inputDelay[(_inputDelayAt + old - keep + i) % old];
                // The two buffers swap roles: no copy back, nothing allocated.
                _inputDelayScratch = _inputDelay;
                _inputDelay = next;
                _inputDelayLen = length;
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
