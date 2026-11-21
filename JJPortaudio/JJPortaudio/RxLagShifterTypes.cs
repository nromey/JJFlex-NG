using System;

namespace JJPortaudio
{
    /// <summary>
    /// What one 10 ms block of receive audio is, as far as the lag shifter is
    /// concerned (#873 phase 2, #473). Flags, one byte, so it can travel with
    /// its audio without allocation. <see cref="Unclassified"/> is zero on
    /// purpose: audio nobody classified is treated as voice, the conservative
    /// reading, and never gets the fast noise policy (Sprint 49 Track P2 brief,
    /// item 2).
    /// </summary>
    [Flags]
    public enum RxContentClass : byte
    {
        /// <summary>No classifier ran: treat as voice, and use the shifter's own tonal measure.</summary>
        Unclassified = 0,
        /// <summary>A classifier ran on this block; the other flags mean what they say.</summary>
        Classified = 1,
        /// <summary>
        /// The speech detector called it speech (probability 0.3 or more, or inside
        /// the hangover). It keeps the fast noise policy away; it never decides what
        /// is tonal, because the stored detector calls a steady carrier speech.
        /// </summary>
        Speech = 2,
        /// <summary>The loudness check called it active: above its noise floor by the gate.</summary>
        Active = 4,
        /// <summary>The detector was unsure (probability between 0.1 and 0.3): a gap splice here is capped at 20 ms.</summary>
        Uncertain = 8,
        /// <summary>At least 100 ms of consecutive blocks that were neither speech nor active: the fast noise policy may run.</summary>
        NotVoiceRun = 16,
        /// <summary>The slice is in CW: gap splices are capped at 10 ms and spaced at least 40 ms apart.</summary>
        Cw = 32,
        /// <summary>A digital mode (DIGU, DIGL, RTTY): nothing is cut or repeated; standing latency is held.</summary>
        NoSplice = 64,
    }

    /// <summary>Why a stretch of audio is being drained or grown. Diagnostics only; never operator words.</summary>
    public enum LagCause : byte
    {
        None,
        /// <summary>A switch to or from a noise reduction engine outside the shared set (owner one).</summary>
        EngineSwitch,
        /// <summary>Computer noise reduction turned on: the dry signal grows to the engine's delay first (owner one).</summary>
        TurnOn,
        /// <summary>Computer noise reduction turned off: the dry signal drains from the delay in force to zero (owner one).</summary>
        TurnOff,
        /// <summary>A new shared-delay plan under a running engine (owner one).</summary>
        Retime,
        /// <summary>Late audio, a stall then a burst: the surplus is caught up (owner two).</summary>
        LateAudio,
        /// <summary>The queue running low: playback slows to stretch over it (owner two).</summary>
        Bridge,
        /// <summary>A join across a receive gap longer than concealment covers (owner two).</summary>
        Seam,
        /// <summary>A slow clock difference between the radio and the sound card (owner two).</summary>
        Drift,
        /// <summary>A backlog above the catch-up limit: one faded cut at a quiet moment (owner two).</summary>
        Cut,
        /// <summary>A step handed in from outside (a third owner, such as SilkTune later).</summary>
        External,
    }

    /// <summary>Which budget a request runs on.</summary>
    public enum LagPolicyKind : byte
    {
        /// <summary>Gentle: periods 3 percent or less, gaps 25 percent or less.</summary>
        EngineSwitch,
        /// <summary>Drop-out catch-up and bridging: gaps first up to 50 percent, periods 10 percent (5 percent when tonal).</summary>
        CatchUp,
        /// <summary>Clock drift: period splices only, 1 percent or less.</summary>
        Drift,
    }

    /// <summary>
    /// The rates one kind of request may run at. Every number here is the
    /// research's proposal from simulation, not a measured threshold of
    /// audibility (the Track P2 brief, item 1): each is named and sourced, and
    /// Noel's ear on the renders sets them.
    /// </summary>
    public readonly struct LagPolicy
    {
        public readonly LagPolicyKind Kind;
        /// <summary>Whole-period splices, as a fraction of the audio played, for voice and unclassified audio.</summary>
        public readonly float PeriodRate;
        /// <summary>Whole-period splices for tonal material (a carrier, CW), at the 0.97 gate.</summary>
        public readonly float TonalPeriodRate;
        /// <summary>Gap splices (pauses, key-up, band noise), as a fraction of the audio played; 0 when gaps are not used.</summary>
        public readonly float GapRate;

        public LagPolicy(LagPolicyKind kind, float periodRate, float tonalPeriodRate, float gapRate)
        {
            Kind = kind; PeriodRate = periodRate; TonalPeriodRate = tonalPeriodRate; GapRate = gapRate;
        }

        public bool AllowsGaps => GapRate > 0f;

        /// <summary>
        /// Engine changes, turning noise reduction on or off, and a retime:
        /// periods 3 percent, gaps 25 percent. Source: the first research report
        /// (2026-11-08), section 6 step 5; the follow-up, section 4: 3 percent
        /// sits below Quené's (2007) 5 percent tempo JND for speech.
        /// </summary>
        public static readonly LagPolicy EngineSwitch = new(LagPolicyKind.EngineSwitch, 0.03f, 0.03f, 0.25f);

        /// <summary>
        /// Late audio and bridging: gaps 50 percent, periods 10 percent (NetEQ's
        /// cap, `kMinTimescaleInterval = 5`, about 100 ms a second), 5 percent at
        /// the 0.97 gate for tonal material. Source: the first report, section 5;
        /// Noel heard the periods-10, gaps-50 renders on 2026-11-08 12:36 ("They
        /// sound normal to me ... even with the 2000 ms gap").
        /// </summary>
        public static readonly LagPolicy CatchUp = new(LagPolicyKind.CatchUp, 0.10f, 0.05f, 0.50f);

        /// <summary>
        /// Clock drift: period splices only, 1 percent or less. Source: the first
        /// report, section 5 ("inaudible by any measure").
        /// </summary>
        public static readonly LagPolicy Drift = new(LagPolicyKind.Drift, 0.01f, 0.01f, 0f);

        public static LagPolicy For(LagPolicyKind kind) =>
            kind == LagPolicyKind.EngineSwitch ? EngineSwitch : kind == LagPolicyKind.Drift ? Drift : CatchUp;
    }

    /// <summary>
    /// What one drain or growth did, from its request to its end: for the trace
    /// line (item 7) and for tests. A plain struct, copied, never allocated.
    /// </summary>
    public struct RxLagEvent
    {
        public LagCause Cause;
        public LagPolicyKind Policy;
        /// <summary>Frames asked for at the start: positive to remove, negative to add.</summary>
        public long Requested;
        public long Removed;
        public long Added;
        public int PeriodSplices;
        public int GapSplices;
        public int Cuts;
        public int SeamJoins;
        /// <summary>The lowest correlation a period splice was accepted at; 1 when none was made.</summary>
        public float WorstCorrelation;
        /// <summary>Output frames from the request to the end.</summary>
        public long FramesTaken;
        /// <summary>Ended by its owner before it finished (a starvation, a newer request of another kind).</summary>
        public bool Abandoned;
        /// <summary>Its owner fell back to the old behaviour (an engine switch that did not pre-grow in time).</summary>
        public bool FellBack;
        /// <summary>What was left undone, below the smallest splice, kept as depth.</summary>
        public long Remainder;

        public string Describe(int sampleRate)
        {
            double ms(long frames) => sampleRate > 0 ? frames * 1000.0 / sampleRate : 0;
            return Cause + " (" + Policy + " policy): asked " + ms(Requested).ToString("F1") + " ms, removed "
                + ms(Removed).ToString("F1") + " ms, added " + ms(Added).ToString("F1") + " ms in "
                + ms(FramesTaken).ToString("F0") + " ms; " + PeriodSplices + " period splice(s), " + GapSplices
                + " gap splice(s), " + Cuts + " cut(s), " + SeamJoins + " seam join(s); worst period correlation "
                + WorstCorrelation.ToString("0.000") + "; " + ms(Remainder).ToString("F1") + " ms left as depth"
                + (Abandoned ? "; abandoned" : "") + (FellBack ? "; fell back to the old switch" : "");
        }
    }

    /// <summary>
    /// The token buckets a request spends from: one for whole-period splices and
    /// one for gap splices, filled at the policy's rate per frame of audio
    /// played, eased in over about half a second from the moment a request
    /// becomes active (the first report's "slow onset"). One budget can be
    /// shared by several shifters that run in series on one thread, so two
    /// drains at once never double the rate (the two receive noise reduction
    /// slots share one; item 3). Allocation-free.
    /// </summary>
    public sealed class RxLagBudget
    {
        /// <summary>The ease-in: the rate climbs from nothing to full over this long. The first report's "ramp the budget in over about half a second".</summary>
        public const double EaseInMilliseconds = 500;

        private readonly int _sampleRate;
        private readonly long _easeFrames;
        private double _period, _gap;
        private long _activeFrames;
        private bool _touched;
        private LagPolicy _policy = LagPolicy.EngineSwitch;
        private bool _tonal;

        public RxLagBudget(int sampleRate)
        {
            _sampleRate = sampleRate > 0 ? sampleRate : 48000;
            _easeFrames = (long)(_sampleRate * EaseInMilliseconds / 1000.0);
        }

        /// <summary>Tokens available for whole-period splices, in frames.</summary>
        public double PeriodTokens => _period;

        /// <summary>Tokens available for gap splices, in frames.</summary>
        public double GapTokens => _gap;

        /// <summary>A request is active on this budget during this stretch of audio (called by a shifter as it renders).</summary>
        public void Touch(in LagPolicy policy, bool tonal)
        {
            _touched = true;
            _policy = policy;
            _tonal = tonal;
        }

        /// <summary>
        /// <paramref name="frames"/> of audio have been played. Fills the buckets
        /// at the policy's rate if a request was active, or empties them and
        /// restarts the ease-in if none was. Each bucket holds at most one
        /// largest splice of its kind, so the budget never bursts.
        /// </summary>
        public void Advance(int frames)
        {
            if (frames <= 0) return;
            if (!_touched)
            {
                _period = 0; _gap = 0; _activeFrames = 0;
                return;
            }
            _touched = false;
            double ease = _easeFrames <= 0 ? 1 : Math.Min(1.0, (_activeFrames + frames) / (double)_easeFrames);
            _activeFrames += frames;
            float periodRate = _tonal ? _policy.TonalPeriodRate : _policy.PeriodRate;
            _period = Math.Min(RxLagShifter.MaxLagFramesAt(_sampleRate), _period + periodRate * frames * ease);
            _gap = Math.Min(RxLagShifter.MaxGapFramesAt(_sampleRate), _gap + _policy.GapRate * frames * ease);
        }

        internal bool TakePeriod(int frames)
        {
            if (_period < frames) return false;
            _period -= frames;
            return true;
        }

        internal bool TakeGap(int frames)
        {
            if (_gap < frames) return false;
            _gap -= frames;
            return true;
        }

        /// <summary>Tests only: empty it as if idle.</summary>
        internal void Reset() { _period = 0; _gap = 0; _activeFrames = 0; _touched = false; }
    }
}
