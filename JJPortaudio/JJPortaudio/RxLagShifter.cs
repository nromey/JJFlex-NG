using System;

namespace JJPortaudio
{
    /// <summary>
    /// The receive lag shifter's core (#873 phase 2, #473, #1154): a ring of
    /// interleaved audio whose read point can move non-uniformly. It cuts or
    /// repeats whole waveform periods, or shortens or lengthens gaps, in audio
    /// already in hand, with a correlation-matched fade at every splice, the same
    /// splice point for both ears, a tonal veto on the gap path, a 150 ms pause
    /// floor, and a budget per kind of content. Written once and used by two
    /// owners (ruled by Noel 2026-11-20 11:25, S7): each receive noise reduction
    /// slot, as its pad line (owner one), and the playback queue's consumer in the
    /// output callback (owner two). A third owner (SilkTune, #199) can hand it a
    /// seam or a step through the same calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pitch is preserved by construction.</b> Between splices the audio is
    /// untouched; at a splice the only error is a bounded phase step, never a
    /// frequency shift. For a pure tone a correlation of at least c bounds the
    /// step to arccos(c): 0.45 radians at the voice gate, 0.25 at the tonal one
    /// (the first research report, 2026-11-08, section 2).
    /// </para>
    /// <para>
    /// <b>It never reads audio that is not there.</b> A removal looks ahead only
    /// as far as the frames in hand allow after this render's own output is
    /// served; a growth reads only audio already played, never a region repeated
    /// already. Every look-ahead is bounded by the write point.
    /// </para>
    /// <para>
    /// <b>Real-time safe.</b> Everything is allocated in the constructor. Write and
    /// Render allocate nothing, lock nothing and log nothing. One thread uses an
    /// instance (the remote-audio thread for a slot, PortAudio's output thread for
    /// the queue).
    /// </para>
    /// <para>
    /// No library is copied in. NetEQ (BSD) and Sonic (Apache 2.0) were read for
    /// the method only, through the research reports.
    /// </para>
    /// </remarks>
    public sealed class RxLagShifter
    {
        // ── The constants. Every one is the research's proposal, not a measured threshold. ──

        /// <summary>The shortest splice: 2.5 ms. NetEQ's <c>kMinLag = 10</c> at 4 kHz (first report, section 2, read in NetEQ's source).</summary>
        public const double MinLagMilliseconds = 2.5;
        /// <summary>The longest whole-period splice: 15 ms. NetEQ's <c>kMaxLag = 60</c> at 4 kHz (as above).</summary>
        public const double MaxLagMilliseconds = 15;
        /// <summary>The voice gate: 0.9. NetEQ's <c>kCorrelationThreshold = 14746; // 0.9 in Q14</c> (as above).</summary>
        public const float VoiceGate = 0.9f;
        /// <summary>The tonal gate: 0.97. At NetEQ's 0.9 one or two CW elements in 59 took phase steps of 0.6 to 1.4 radians; at 0.97 none did (first report, section 2).</summary>
        public const float TonalGate = 0.97f;
        /// <summary>Tonal splices are at most 5 ms (first report, section 2: 15 ms jumps changed CW elements by 20 to 35 ms).</summary>
        public const double TonalMaxJumpMilliseconds = 5;
        /// <summary>Tonal splices at most one per 120 ms (first report, section 2, the CW rhythm runs).</summary>
        public const double TonalMinIntervalMilliseconds = 120;
        /// <summary>
        /// A keyed run younger than this is an element, and takes at most one tonal
        /// splice; an older one is a carrier, spliced at the tonal spacing. A dah at
        /// 10 WPM is 360 ms (my judgement, so a CW element changes by at most one
        /// 5 ms splice: the brief's "element changes within 5 ms").
        /// </summary>
        public const double TonalElementMilliseconds = 400;
        /// <summary>Periodicity at or above this, with a stable period, is tonal and vetoes the gap path (follow-up, sections 1 and 6).</summary>
        public const float TonalPeriodicity = 0.9f;
        /// <summary>No splice whose two segments differ by more than this (first report's model, the transient guard).</summary>
        public const double TransientDb = 3;
        /// <summary>A rise this large between 2.5 ms sub-blocks is an onset; no removed or repeated span may hold one (as above).</summary>
        public const double OnsetDb = 6;
        /// <summary>A 10 ms block within this much of the noise floor is a gap (first report's model, "within 6 dB of the floor").</summary>
        public const double GapAboveFloorDb = 6;
        /// <summary>The noise floor is this percentile of the last <see cref="FloorWindowMilliseconds"/> of block energies (follow-up, section 1, the model's estimator).</summary>
        public const int FloorPercentile = 10;
        /// <summary>The window the floor is taken over (my judgement: long enough to span a syllable and its pause).</summary>
        public const double FloorWindowMilliseconds = 1500;
        /// <summary>Never shorten a pause below 150 ms (Mach1, Covell, Withgott and Slaney 1998, via the follow-up, section 4).</summary>
        public const double PauseFloorMilliseconds = 150;
        /// <summary>A gap splice in speech removes at most 40 ms (first report, section 5).</summary>
        public const double GapMaxSpeechMilliseconds = 40;
        /// <summary>A gap splice in CW removes at most 10 ms (first report, section 5).</summary>
        public const double GapMaxCwMilliseconds = 10;
        /// <summary>CW gap splices at most one per 40 ms (first report, section 2).</summary>
        public const double CwGapMinIntervalMilliseconds = 40;
        /// <summary>A gap splice in band noise removes at most 40 ms (first report, section 5).</summary>
        public const double GapMaxNoiseMilliseconds = 40;
        /// <summary>While the voice probability sits between 0.1 and 0.3, a gap splice is capped at 20 ms (follow-up, section 6).</summary>
        public const double GapMaxUncertainMilliseconds = 20;
        /// <summary>The fade at a gap splice (my judgement: long enough that a matched fade on noise holds level, short against a 150 ms pause).</summary>
        public const double GapFadeMilliseconds = 5;
        /// <summary>The fade at a cut: 10 ms at r = 0 (first report, section 5).</summary>
        public const double CutFadeMilliseconds = 10;
        /// <summary>
        /// A seam join never starts inside the first 2.5 ms after the seam. Measured
        /// (RxSeamJoinTests): Opus's CELT frames overlap by 2.5 ms, so the decoder's
        /// first samples after a lost run are its own blend of stale state and the new
        /// frame, and for audio half a period out of phase that blend nearly cancels
        /// (a 43 dB dip on a 625 Hz tone). A join that begins inside it keeps the hole.
        /// </summary>
        public const double SeamSkipMilliseconds = 2.5;
        /// <summary>The fade at a seam join (my judgement: as a gap splice's).</summary>
        public const double SeamFadeMilliseconds = 5;
        /// <summary>The fade when the queue runs dry, and when audio comes back: "a few milliseconds", so neither edge clicks (item 4).</summary>
        public const double DryFadeMilliseconds = 2.5;
        /// <summary>At most one splice decision per 2.5 ms of audio (first report, section 4).</summary>
        public const double DecisionIntervalMilliseconds = 2.5;
        /// <summary>The correlation window: 5 ms, 240 samples (the first report's bench: 601 lags by 240 samples).</summary>
        public const double CorrelationWindowMilliseconds = 5;
        /// <summary>The coarse pass's stride, in samples and in lags; a refined pass follows (first report, section 4: 7 µs against 23 µs).</summary>
        public const int CoarseStride = 4;
        /// <summary>The sub-block the onset check reads, 2.5 ms.</summary>
        public const double OnsetBlockMilliseconds = 2.5;

        public static int MaxLagFramesAt(int sampleRate) => (int)Math.Round(sampleRate * MaxLagMilliseconds / 1000.0);
        public static int MaxGapFramesAt(int sampleRate) => (int)Math.Round(sampleRate * GapMaxSpeechMilliseconds / 1000.0);

        // ── The ring ──

        private readonly int _ch, _cap, _rate;
        // The ring, in segments of at most 64 KB, so no shifter puts anything on the
        // large-object heap. Found in repair 3: large-object allocations start background
        // collections, and a background collection retires every thread's allocation
        // block, which GC.GetAllocatedBytesForCurrentThread then reports as up to 8 KB
        // "allocated" on threads that allocated nothing. With the branch's rings three
        // times phase one's, the test suite's allocation checks failed at random.
        private readonly float[][] _seg;
        private readonly int _segShift, _segMask;
        private long _write, _read, _validFrom;

        // ── Per-block facts, 10 ms blocks aligned to the write position ──

        private readonly int _block;
        private readonly int _blocks;
        private readonly float[] _blockEnergy;   // per block per channel: mean square
        private readonly float[] _blockSum;      // per block: mean square over channels
        private readonly byte[] _blockFlags;
        private readonly long[] _blockId;        // which absolute block each slot holds
        private readonly double[] _acc;          // energy accumulators for the block being written
        private int _accFrames;
        private RxContentClass _accClass;

        // The floor: a percentile of recent block energies, per channel and combined.
        private readonly int _floorLen;
        private readonly float[] _floorRing;     // per entry: channels then combined
        private readonly float[] _floorScratch;
        private int _floorCount, _floorAt;
        private readonly float[] _floor;         // per channel, then combined at [_ch]
        private bool _floorDirty;

        // Seam marks, as absolute positions, oldest first.
        private readonly long[] _seams = new long[32];
        private int _seamHead, _seamCount;

        // -- Carried seams (owner one's pad lines: Codex's check of 2567a596, B1) --
        // Positions in this line's write timeline where a seam sits. They are not
        // joined here: each is reported, as an output position, when the read point
        // reaches it (or passes it in a splice), so the slot can say where the seam
        // comes OUT. Kept, not consumed, because a fixed pad re-reads its history.
        // 64: a pad line holds well under a second, and a lost-packet seam is rare; past
        // 64 unread the oldest goes (it is long played, or the line is far behind).
        private readonly long[] _carry = new long[64];
        private int _carryCount;
        private readonly long[] _emitOut = new long[8], _emitPos = new long[8];
        private int _emitCount;
        private long _lastEmitted = long.MinValue;

        // ── The request ──

        private long _pending;              // frames: > 0 remove, < 0 add
        private LagPolicy _policy = LagPolicy.EngineSwitch;
        private LagCause _cause;
        private bool _exact;
        private readonly RxLagBudget _budget;
        private readonly bool _ownsBudget;
        private bool _lastTonal;

        // ── The splice in progress ──

        private enum FadeKind : byte { None, Splice, Seam, Cut, Overflow }
        private FadeKind _fade;
        private int _fadeLen, _fadeAt, _fadePeriod, _fadeHold;
        private long _fadeA, _fadeB;          // old side and new side, at fade frame 0
        private readonly float[] _fadeR;
        private float[] _overflowTail, _overflowNext;
        // A fade whose old side lies further behind the read point than the history Write
        // keeps (a cut, which skips a whole backlog) plays its old side from this copy.
        private readonly float[] _oldCopy;
        private bool _oldFromCopy;

        private long _out;                    // frames rendered, ever
        private long _nextDecision;
        private long _lastTonalSplice = long.MinValue / 2, _lastCwGap = long.MinValue / 2;
        private long _growGuard = long.MinValue; // no repeat may reach back before this content position

        // ── Dry handling (owner two) ──

        private bool _dry;
        private int _fadeInLeft;

        // ── A cut (owner two, a backlog over the catch-up limit) ──

        private bool _cutPending;
        private long _cutAt;
        private long _cutFrames;

        // ── Search scratch ──

        private readonly int _minLag, _maxLag, _tonalMaxLag, _window;
        private readonly int _decisionInterval, _tonalInterval, _cwGapInterval, _tonalElement;
        private long _lastTonalRun = -1;
        private readonly int _pauseFloor, _gapMaxSpeech, _gapMaxCw, _gapMaxNoise, _gapMaxUncertain;
        private readonly int _gapFade, _cutFade, _seamFade, _dryFade, _onsetBlock;
        private readonly float[] _chScore;
        private readonly bool[] _earActive;

        // ── The event being recorded ──

        private RxLagEvent _event;
        private bool _eventActive;
        private long _eventStart;
        private RxLagEvent _completed;
        private int _completedSeq, _completedTaken;

        // ── Counters for tests and the per-minute line ──

        public long TotalRemoved { get; private set; }
        public long TotalAdded { get; private set; }
        public long TotalPeriodSplices { get; private set; }
        public long TotalGapSplices { get; private set; }
        public long TotalSeamJoins { get; private set; }
        public long TotalCutFrames { get; private set; }
        public long OverflowFrames { get; private set; }
        public long SilentFrames { get; private set; }

        /// <param name="channels">Interleaved channels (2 for every receive path today).</param>
        /// <param name="capacityFrames">Frames the ring holds: the longest delay or backlog, plus history for growth.</param>
        /// <param name="sampleRate">48 kHz on every receive path today.</param>
        /// <param name="budget">A budget shared with other shifters, advanced by its owner; null for one of its own, advanced as this renders.</param>
        /// <summary>
        /// The first <c>Span&lt;float&gt;.Sort</c> in a process sets up the runtime's
        /// sort helper, 24 bytes, on whatever thread sorts first; the floor's
        /// percentile sorts on the audio thread. Done here, off it (Codex's check of
        /// 2567a596, B4: the 24 bytes on the buffer after a first turn-off).
        /// </summary>
        static RxLagShifter() => WarmSort();

        /// <summary>Sort two floats once, so the sort helper exists before any audio thread sorts.</summary>
        public static void WarmSort()
        {
            Span<float> two = stackalloc float[2] { 2f, 1f };
            two.Sort();
        }

        public RxLagShifter(int channels, int capacityFrames, int sampleRate = 48000, RxLagBudget budget = null)
        {
            _ch = Math.Max(1, channels);
            _rate = sampleRate > 0 ? sampleRate : 48000;
            _block = Math.Max(1, _rate / 100);
            int minCap = 4 * _block + 4 * MaxLagFramesAt(_rate);
            _cap = Math.Max(capacityFrames, minCap);
            int segFrames = 1;
            while (segFrames * 2 * _ch * sizeof(float) <= 65536) segFrames *= 2;
            _segShift = System.Numerics.BitOperations.Log2((uint)segFrames);
            _segMask = segFrames - 1;
            int segments = (_cap + segFrames - 1) / segFrames;
            _seg = new float[segments][];
            for (int k = 0; k < segments; k++) _seg[k] = new float[Math.Min(segFrames, _cap - k * segFrames) * _ch];
            _blocks = _cap / _block + 4;
            _blockEnergy = new float[_blocks * _ch];
            _blockSum = new float[_blocks];
            _blockFlags = new byte[_blocks];
            _blockId = new long[_blocks];
            for (int i = 0; i < _blocks; i++) _blockId[i] = -1;
            _acc = new double[_ch];
            _floorLen = Math.Max(10, (int)(FloorWindowMilliseconds / 10));
            _floorRing = new float[_floorLen * (_ch + 1)];
            _floorScratch = new float[_floorLen];
            _floor = new float[_ch + 1];
            _fadeR = new float[_ch];
            _chScore = new float[_ch];
            _earActive = new bool[_ch];
            _minLag = Ms(MinLagMilliseconds);
            _maxLag = Ms(MaxLagMilliseconds);
            _tonalMaxLag = Ms(TonalMaxJumpMilliseconds);
            _window = Ms(CorrelationWindowMilliseconds);
            _decisionInterval = Ms(DecisionIntervalMilliseconds);
            _tonalInterval = Ms(TonalMinIntervalMilliseconds);
            _tonalElement = Ms(TonalElementMilliseconds);
            _cwGapInterval = Ms(CwGapMinIntervalMilliseconds);
            _pauseFloor = Ms(PauseFloorMilliseconds);
            _gapMaxSpeech = Ms(GapMaxSpeechMilliseconds);
            _gapMaxCw = Ms(GapMaxCwMilliseconds);
            _gapMaxNoise = Ms(GapMaxNoiseMilliseconds);
            _gapMaxUncertain = Ms(GapMaxUncertainMilliseconds);
            _gapFade = Ms(GapFadeMilliseconds);
            _cutFade = Ms(CutFadeMilliseconds);
            _overflowTail = new float[Math.Max(1, _cutFade) * _ch];
            _overflowNext = new float[_overflowTail.Length];
            // The longest old side a ring fade reads: a cut's fade, or one period (a period
            // splice fades over one lag; a seam's extension repeats one period).
            _oldCopy = new float[Math.Max(Math.Max(1, _cutFade), _maxLag) * _ch];
            _seamFade = Ms(SeamFadeMilliseconds);
            _dryFade = Math.Max(1, Ms(DryFadeMilliseconds));
            _onsetBlock = Math.Max(1, Ms(OnsetBlockMilliseconds));
            _ownsBudget = budget == null;
            _budget = budget ?? new RxLagBudget(_rate);
        }

        private int Ms(double ms) => (int)Math.Round(_rate * ms / 1000.0);

        /// <summary>The history Write keeps behind the read point: two longest lags and a cut's fade.</summary>
        private int HistoryFrames => 2 * _maxLag + _cutFade;

        // ── Facts ──

        public int Channels => _ch;
        public int SampleRate => _rate;
        public int CapacityFrames => _cap;
        /// <summary>The shortest splice, in frames: a remainder smaller than this is left as depth.</summary>
        public int MinLagFrames => _minLag;
        /// <summary>Frames written and not yet read: the delay of a pad line, the depth of a queue.</summary>
        public long Available => _write - _read;
        public long WritePosition => _write;
        public long ReadPosition => _read;
        /// <summary>Frames written since construction or the last <see cref="Clear"/>.</summary>
        public long Filled => _write - _validFrom;
        /// <summary>Frames still to remove (positive) or add (negative).</summary>
        public long Pending => _pending;
        public LagCause Cause => _cause;
        public bool Busy => _pending != 0 || _fade != FadeKind.None || _cutPending;
        /// <summary>True while a splice's fade is part-way through.</summary>
        public bool Fading => _fade != FadeKind.None;
        /// <summary>Played dry: the last render ran out of audio.</summary>
        public bool Dry => _dry;
        /// <summary>Owner two: the next audio played fades in (after a hold, or a stream's start).</summary>
        public void ArmFadeIn() { if (DryFades) _fadeInLeft = _dryFade; }

        /// <summary>The budget this shifter spends from.</summary>
        public RxLagBudget Budget => _budget;
        /// <summary>The last decision found tonal material.</summary>
        public bool LastTonal => _lastTonal;

        /// <summary>The class of the audio at the read point (the next to be played), or unclassified when its block is not in hand.</summary>
        public RxContentClass ClassAtRead => ClassAt(_read);

        /// <summary>
        /// Owner one: seams are CARRIED, not joined. A seam marked here (<see cref="MarkSeamAt"/>)
        /// is reported where it comes out (<see cref="EmergedCount"/>, <see cref="EmergedAt"/>)
        /// and nothing about the audio changes. Owner two (the queue) leaves this off and joins them.
        /// </summary>
        public bool CarrySeams { get; set; }

        /// <summary>Frames rendered, ever: the output timeline <see cref="EmergedAt"/> is in.</summary>
        public long OutputPosition => _out;

        /// <summary>
        /// A seam at <paramref name="writePosition"/> in this line's write timeline,
        /// which may be ahead of what is written yet. Carried or joined, as
        /// <see cref="CarrySeams"/> says.
        /// </summary>
        public void MarkSeamAt(long writePosition)
        {
            if (CarrySeams)
            {
                PruneCarry();
                if (_carryCount == _carry.Length) { Array.Copy(_carry, 1, _carry, 0, _carry.Length - 1); _carryCount--; }
                int i = _carryCount;
                while (i > 0 && _carry[i - 1] > writePosition) { _carry[i] = _carry[i - 1]; i--; }
                if (i > 0 && _carry[i - 1] == writePosition) { Array.Copy(_carry, i + 1, _carry, i, _carryCount - i); return; }
                _carry[i] = writePosition;
                _carryCount++;
            }
            else if (writePosition > _validFrom) AddSeam(writePosition);
        }

        /// <summary>Carried seams that came out since <see cref="ClearEmerged"/>.</summary>
        public int EmergedCount => _emitCount;

        /// <summary>Where the <paramref name="index"/>-th carried seam came out, in <see cref="OutputPosition"/>'s timeline.</summary>
        public long EmergedAt(int index) => _emitOut[index];

        /// <summary>Which seam (its write position) the <paramref name="index"/>-th to come out was.</summary>
        public long EmergedSeam(int index) => _emitPos[index];

        /// <summary>Forget the seams reported so far (the owner reads them after each call).</summary>
        public void ClearEmerged() => _emitCount = 0;

        private void PruneCarry()
        {
            int drop = 0;
            while (drop < _carryCount && _carry[drop] < _write - _cap) drop++;
            if (drop == 0) return;
            Array.Copy(_carry, drop, _carry, 0, _carryCount - drop);
            _carryCount -= drop;
        }

        /// <summary>A carried seam at <paramref name="pos"/> came out at output frame <paramref name="outAt"/>.</summary>
        private void Emit(long pos, long outAt)
        {
            if (pos <= _lastEmitted) return;
            _lastEmitted = pos;
            // Nothing from before a seam is ever repeated after it (as at a join).
            _growGuard = _growGuard == long.MinValue ? pos : Math.Max(_growGuard, pos);
            if (_emitCount == _emitOut.Length) return;
            _emitOut[_emitCount] = outAt;
            _emitPos[_emitCount] = pos;
            _emitCount++;
        }

        /// <summary>Report carried seams read in [from, from + n), the frame at from coming out at <paramref name="outAt"/>.</summary>
        private void EmitRange(long from, long n, long outAt)
        {
            for (int i = 0; i < _carryCount; i++)
            {
                long sp = _carry[i];
                if (sp >= from + n) break;
                if (sp >= from) Emit(sp, outAt + (sp - from));
            }
        }

        /// <summary>
        /// The read point moves to <paramref name="newRead"/> without playing what lies
        /// between: a seam passed over comes out here; a move back lets history's
        /// seams come out again when they are read.
        /// </summary>
        private void JumpRead(long newRead)
        {
            if (CarrySeams && _carryCount > 0)
            {
                if (newRead > _read) EmitRange(_read, newRead - _read, _out);
                else if (newRead < _read) _lastEmitted = Math.Min(_lastEmitted, newRead - 1);
            }
            _read = newRead;
        }

        /// <summary>Owner one: content at position p was classified this many frames earlier (the engine's own delay).</summary>
        public int ClassLagFrames { get; set; }

        /// <summary>Owner two: fade out when the audio runs out and fade in when it comes back.</summary>
        public bool DryFades { get; set; }

        /// <summary>Tests only: false searches no correlation and splices at the longest lag allowed (the positive control for pitch).</summary>
        internal bool WholePeriodSearch { get; set; } = true;

        /// <summary>Tests only: false lets a steady carrier down the gap path (the follow-up's failure, the positive control for the veto).</summary>
        internal bool TonalVeto { get; set; } = true;

        /// <summary>Tests only: the last splices, oldest overwritten first. Preallocated; written only when <see cref="LogSplices"/> is set.</summary>
        internal readonly struct SpliceRecord
        {
            public readonly long Out, OldSide, NewSide; public readonly int Lag, Fade; public readonly float Score; public readonly byte Kind;
            public SpliceRecord(long o, long a, long b, int lag, int fade, float score, byte kind) { Out = o; OldSide = a; NewSide = b; Lag = lag; Fade = fade; Score = score; Kind = kind; }
            public override string ToString() => $"out {Out} kind {Kind} a {OldSide} b {NewSide} lag {Lag} fade {Fade} score {Score:0.000}";
        }
        internal bool LogSplices { get; set; }
        internal readonly SpliceRecord[] SpliceLog = new SpliceRecord[512];
        internal int SpliceLogCount;
        private void Log(long a, long b, int lag, int fade, float score, byte kind)
        {
            if (!LogSplices) return;
            SpliceLog[SpliceLogCount % SpliceLog.Length] = new SpliceRecord(_out, a, b, lag, fade, score, kind);
            SpliceLogCount++;
        }

        /// <summary>Tests only: every read this shifter has made of the ring, checked against the write point (the look-ahead fuzz test).</summary>
        internal long ReadsPastWrite { get; private set; }

        // ── Writing ──

        /// <summary>
        /// Append interleaved frames. <paramref name="cls"/> describes the audio
        /// (owner two: the buffer's class; owner one: the input's class, read at
        /// <see cref="ClassLagFrames"/>). <paramref name="seamBefore"/> marks a
        /// join across lost audio just before these frames.
        /// </summary>
        public void Write(ReadOnlySpan<float> data, RxContentClass cls = RxContentClass.Unclassified, bool seamBefore = false)
        {
            int frames = data.Length / _ch;
            if (frames <= 0) return;
            if (seamBefore && _write > _validFrom) MarkSeamAt(_write);
            // Never overwrite audio not yet read, nor the history a fade needs:
            // the oldest unread audio is dropped instead, and counted (owner two
            // only reaches this when a backlog passes the ring). A fade's old side
            // further back than this history is copied when the fade begins
            // (BeginFade), so the room is measured from the read point.
            long room = _cap - HistoryFrames - (_write - _read) - frames;
            if (room < 0) Overflow(-room);
            int at = 0;
            while (at < frames)
            {
                int idx = (int)(_write % _cap);
                int n = Math.Min(frames - at, _cap - idx);
                n = Math.Min(n, _block - _accFrames);
                for (int f = 0; f < n; f++)
                {
                    int src = (at + f) * _ch, i = idx + f;
                    var segment = _seg[i >> _segShift];
                    int dst = (i & _segMask) * _ch;
                    for (int c = 0; c < _ch; c++)
                    {
                        float v = data[src + c];
                        segment[dst + c] = v;
                        _acc[c] += (double)v * v;
                    }
                }
                _accClass = cls;
                _accFrames += n;
                _write += n;
                at += n;
                if (_accFrames == _block) CompleteBlock();
            }
        }

        /// <summary>
        /// Tests only: false drops an overflow with no fade, as before repair 2 (the
        /// positive control for the faded overflow).
        /// </summary>
        internal bool OverflowFades { get; set; } = true;

        /// <summary>
        /// The ring is full: at least <paramref name="drop"/> unread frames must go
        /// before this write overwrites them (a backlog past the ring: a digital mode
        /// is never cut, or steering cannot keep up). Codex's re-check of 5a0a8c93:
        /// the drop used to jump with no fade. Now, BEFORE anything is overwritten,
        /// the drop is lengthened by up to one longest lag to the point whose audio
        /// best continues what was about to play (so a tone does not cancel itself
        /// in the fade), the next 10 ms that would have played are kept, and the
        /// output fades from them into the new point at the correlation measured
        /// there. Preallocated; the search is about 90 thousand multiply-adds, once
        /// per overflow.
        /// </summary>
        private void Overflow(long drop)
        {
            long avail = _write - _read;
            // An overflow not heard yet keeps its copy as it is: it IS the continuation of
            // what was last heard. Rendering its fade ahead instead would drift the copy
            // toward the dropped audio a little at every overflow of a long burst.
            bool pending = _fade == FadeKind.Overflow && _fadeAt == 0;
            int F = pending ? _fadeLen : (int)Math.Min(_cutFade, Math.Max(0, avail - drop));
            if (!OverflowFades || F < _window || drop + _maxLag + _window + F > avail)
            {
                JumpRead(_read + drop);
                _fade = FadeKind.None;
                OverflowFrames += drop;
                return;
            }
            // What the listener would hear next is the continuation of what was last
            // heard, kept until it is played (Codex's re-check 2 of 0d8ecb3c: the queue
            // writes every waiting packet before it renders, so a second overflow used to
            // recapture from the first one's dropped place and the jump came back). It is
            // kept as it is while an earlier overflow is not yet heard; otherwise (no fade, a
            // splice part-way, an overflow part-way) captured by rendering ahead on a copy
            // of the state.
            if (!pending) CaptureContinuation(F);
            long a = _read;
            int best = FindOverflowMatch(a + drop);
            long newSide = a + drop + best;
            MeasureTailR(newSide, F);
            Log(a, newSide, (int)(drop + best), F, _fadeR[0], 5);
            _fade = FadeKind.None;
            BeginFade(FadeKind.Overflow, a, newSide, F);
            // The kept audio carries any dry fade-in Render would have played over it, and
            // the fade brings the new side in from nothing: no fade-in is left to play
            // after it (it would be a dip in the middle of the audio).
            _fadeInLeft = 0;
            OverflowFrames += drop + best;
        }

        /// <summary>
        /// The extra e in [0, one longest lag) whose window at <paramref name="from"/> + e
        /// best continues the kept audio (worse ear). Coarse first, then every frame
        /// around the coarse best, scored on its own: the coarse score is subsampled and
        /// must not stand against full scores (Codex's re-check 2: it blocked better ones).
        /// </summary>
        internal int FindOverflowMatch(long from)
        {
            int coarse = 0; float coarseScore = float.MinValue;
            for (int e = 0; e < _maxLag; e += CoarseStride)
            {
                float sc = TailScore(from + e, CoarseStride);
                if (sc > coarseScore) { coarseScore = sc; coarse = e; }
            }
            int best = coarse;
            float fineScore = float.MinValue;
            for (int e = Math.Max(0, coarse - CoarseStride + 1); e <= Math.Min(_maxLag - 1, coarse + CoarseStride - 1); e++)
            {
                float sc = TailScore(from + e, 1);
                if (sc > fineScore) { fineScore = sc; best = e; }
            }
            return best;
        }

        /// <summary>The worse ear's correlation of the kept audio's first window with the ring at <paramref name="b"/>; an ear silent in both does not count.</summary>
        internal float TailScore(long b, int stride)
        {
            float worst = float.MaxValue;
            for (int c = 0; c < _ch; c++)
            {
                double xy = 0, xx = 0, yy = 0;
                int ib = Idx(b);
                for (int i = 0; i < _window; i += stride)
                {
                    float x = _overflowTail[i * _ch + c], y = _seg[ib >> _segShift][(ib & _segMask) * _ch + c];
                    xy += (double)x * y; xx += (double)x * x; yy += (double)y * y;
                    ib += stride; if (ib >= _cap) ib -= _cap;
                }
                if (xx < 1e-12 && yy < 1e-12) continue;
                float r = xx < 1e-12 || yy < 1e-12 ? 0f : (float)(xy / Math.Sqrt(xx * yy));
                if (r < worst) worst = r;
            }
            return worst == float.MaxValue ? 0f : worst;
        }

        /// <summary>Each ear's correlation of the kept audio with the ring from <paramref name="newSide"/>, over the fade.</summary>
        private void MeasureTailR(long newSide, int len)
        {
            for (int c = 0; c < _ch; c++)
            {
                double xy = 0, xx = 0, yy = 0;
                for (int k = 0; k < len; k++)
                {
                    float x = _overflowTail[k * _ch + c], y = R(newSide + k, c);
                    xy += (double)x * y; xx += (double)x * x; yy += (double)y * y;
                }
                _fadeR[c] = xx < 1e-12 || yy < 1e-12 ? 0f : (float)Math.Clamp(xy / Math.Sqrt(xx * yy), 0, 1);
            }
        }

        /// <summary>
        /// The next <paramref name="frames"/> frames as Render would play them from the
        /// state as it stands, PROVIDED Render starts nothing new in them: the fade in
        /// progress (a splice, seam join, cut or earlier overflow part-way) to its end,
        /// then the ring straight from the read point, with the dry fade-in Render would
        /// apply. Into the kept audio, with the state left as it was. Allocation-free.
        /// <para>
        /// Narrowed in repair 4 (Codex's re-check 3 of 7a8b2f90): this used to claim
        /// exact Render equivalence. It does NOT run Render's decision loop once the fade
        /// in progress ends, so a splice, seam join or cut Render would have started
        /// inside these frames is not in the copy. These frames are the old side of the
        /// overflow's fade, faded out over the same 10 ms: a splice or cut there would only
        /// remove or repeat audio the overflow is dropping anyway, and a seam inside them
        /// (a lost packet's join, inside the 10 ms being faded away) is played straight
        /// across, under the fade-out. The pending dry fade-in, which the old fast path for
        /// "no fade" skipped, IS applied: it is part of what is about to be heard.
        /// </para>
        /// </summary>
        private void CaptureContinuation(int frames)
        {
            var fade = _fade; int fadeAt = _fadeAt, fadeHold = _fadeHold, fadeLen = _fadeLen, fadePeriod = _fadePeriod;
            long fadeA = _fadeA, fadeB = _fadeB, read = _read, outPos = _out, past = ReadsPastWrite, grow = _growGuard, lastEmitted = _lastEmitted;
            int fadeIn = _fadeInLeft, emitCount = _emitCount;
            var dst = new Span<float>(_overflowNext, 0, frames * _ch);
            int got = 0;
            while (got < frames)
            {
                if (_fade != FadeKind.None) { got += StepFade(dst.Slice(got * _ch), frames - got); continue; }
                int n = (int)Math.Min(frames - got, _write - _read);
                if (n <= 0) { dst.Slice(got * _ch).Clear(); break; }
                CopyOut(dst.Slice(got * _ch), n, long.MaxValue);
                got += n;
            }
            _fade = fade; _fadeAt = fadeAt; _fadeHold = fadeHold; _fadeLen = fadeLen; _fadePeriod = fadePeriod;
            _fadeA = fadeA; _fadeB = fadeB; _read = read; _out = outPos; ReadsPastWrite = past; _growGuard = grow; _lastEmitted = lastEmitted;
            _fadeInLeft = fadeIn; _emitCount = emitCount;
            (_overflowTail, _overflowNext) = (_overflowNext, _overflowTail);
        }

        /// <summary>Tests only: keep the ring's audio from <paramref name="pos"/> as the kept continuation.</summary>
        internal void KeepForTest(long pos)
        {
            for (int f = 0; f < _cutFade; f++)
                for (int c = 0; c < _ch; c++) _overflowTail[f * _ch + c] = R(pos + f, c);
        }

        private void CompleteBlock()
        {
            long id = (_write - 1) / _block;
            int slot = (int)(id % _blocks);
            _blockId[slot] = id;
            double sum = 0;
            int fo = _floorAt * (_ch + 1);
            for (int c = 0; c < _ch; c++)
            {
                float e = (float)(_acc[c] / _block);
                _blockEnergy[slot * _ch + c] = e;
                _floorRing[fo + c] = e;
                sum += e;
                _acc[c] = 0;
            }
            float mean = (float)(sum / _ch);
            _blockSum[slot] = mean;
            _floorRing[fo + _ch] = mean;
            _floorAt = (_floorAt + 1) % _floorLen;
            if (_floorCount < _floorLen) _floorCount++;
            // The floor and the gap flags are worked out only when a decision needs
            // them (EnsureFloor): a fixed pad line writes every buffer and decides nothing.
            _floorDirty = true;
            _blockFlags[slot] = (byte)_accClass;
            _accFrames = 0;
        }

        /// <summary>The floor as it stands now, worked out if blocks have completed since it last was.</summary>
        private void EnsureFloor()
        {
            if (!_floorDirty) return;
            _floorDirty = false;
            for (int c = 0; c <= _ch; c++) _floor[c] = Percentile(c);
        }

        /// <summary>A block is a gap when it sits within 6 dB of the floor as it stands now.</summary>
        private bool IsGap(int slot) => _blockSum[slot] <= GapLimit(_floor[_ch]);

        private static float GapLimit(float floor) => Math.Max(floor * (float)Math.Pow(10, GapAboveFloorDb / 10), 1e-12f);

        private float Percentile(int column)
        {
            int n = _floorCount;
            for (int i = 0; i < n; i++) _floorScratch[i] = _floorRing[i * (_ch + 1) + column];
            var span = new Span<float>(_floorScratch, 0, n);
            span.Sort();
            return span[Math.Min(n - 1, n * FloorPercentile / 100)];
        }

        private void AddSeam(long at)
        {
            if (_seamCount == _seams.Length) { _seamHead = (_seamHead + 1) % _seams.Length; _seamCount--; }
            // In order: a mark inside a buffer (MarkSeamAt) can arrive after a later one.
            int i = _seamCount;
            while (i > 0 && _seams[(_seamHead + i - 1) % _seams.Length] > at)
            {
                _seams[(_seamHead + i) % _seams.Length] = _seams[(_seamHead + i - 1) % _seams.Length];
                i--;
            }
            if (i > 0 && _seams[(_seamHead + i - 1) % _seams.Length] == at)
            {
                for (int k = i; k < _seamCount; k++) _seams[(_seamHead + k) % _seams.Length] = _seams[(_seamHead + k + 1) % _seams.Length];
                return;
            }
            _seams[(_seamHead + i) % _seams.Length] = at;
            _seamCount++;
        }

        /// <summary>The next seam at or after the read point, or -1.</summary>
        private long NextSeam()
        {
            while (_seamCount > 0)
            {
                long s = _seams[_seamHead];
                if (s >= _read) return s;
                _seamHead = (_seamHead + 1) % _seams.Length; _seamCount--;
            }
            return -1;
        }

        /// <summary>Forget everything: the next reads are silence for what was never written.</summary>
        public void Clear()
        {
            foreach (var segment in _seg) Array.Clear(segment);
            _write = 0; _read = 0; _validFrom = 0;
            _accFrames = 0; Array.Clear(_acc);
            for (int i = 0; i < _blocks; i++) _blockId[i] = -1;
            _floorCount = 0; _floorAt = 0; Array.Clear(_floor); _floorDirty = false;
            _seamCount = 0; _seamHead = 0;
            _carryCount = 0; _lastEmitted = long.MinValue;
            _fade = FadeKind.None;
            _dry = false; _fadeInLeft = 0;
            _cutPending = false;
            _growGuard = long.MinValue;
            if (_pending != 0 || _eventActive) EndEvent(abandoned: true);
            _pending = 0;
        }

        // ── Fixed delays: PadLine's behaviour, exactly ──

        /// <summary>
        /// Delay <paramref name="audio"/> in place by <paramref name="pad"/> frames,
        /// as a plain delay line: write it, then read it <paramref name="pad"/>
        /// frames back. What is not yet written reads as silence. Any splice
        /// pending is held, not applied.
        /// </summary>
        public void ProcessFixed(Span<float> audio, int pad, RxContentClass cls = RxContentClass.Unclassified)
        {
            int frames = audio.Length / _ch;
            int chunk = ChunkFrames();
            for (int at = 0; at < frames; at += chunk)
            {
                int n = Math.Min(chunk, frames - at);
                var part = audio.Slice(at * _ch, n * _ch);
                Write(part, cls);
                _fade = FadeKind.None;
                JumpRead(_write - n - pad);
                CopyOut(part, n, fadeOutFrom: long.MaxValue);
            }
        }

        /// <summary>
        /// Put the read point <paramref name="frames"/> behind the write point at
        /// once, with no output and no fade: the line's next render starts there.
        /// A splice part-way is dropped; a request stays.
        /// </summary>
        public void SetDelay(long frames)
        {
            _fade = FadeKind.None;
            JumpRead(_write - Math.Max(0, frames));
        }

        /// <summary>One line, two taps: <paramref name="audio"/> at <paramref name="padA"/> and <paramref name="second"/> at <paramref name="padB"/>.</summary>
        public void ProcessFixedTwo(Span<float> audio, int padA, Span<float> second, int padB)
        {
            int frames = audio.Length / _ch;
            int chunk = ChunkFrames();
            for (int at = 0; at < frames; at += chunk)
            {
                int n = Math.Min(chunk, frames - at);
                var part = audio.Slice(at * _ch, n * _ch);
                Write(part);
                _fade = FadeKind.None;
                ReadInto(second.Slice(at * _ch, n * _ch), _write - n - padB, n);
                JumpRead(_write - n - padA);
                CopyOut(part, n, fadeOutFrom: long.MaxValue);
            }
        }

        /// <summary>The longest stretch written then read in one go, so a fixed pad never overwrites what it is about to read.</summary>
        private int ChunkFrames() => Math.Max(1, Math.Min(1024, _cap / 4));

        private void ReadInto(Span<float> dst, long from, int frames)
        {
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < _ch; c++) dst[f * _ch + c] = R(from + f, c);
        }

        // ── Steered: write, then render through the request ──

        /// <summary>
        /// Owner one: write <paramref name="audio"/> and render the same number of
        /// frames back into it, applying any request. The delay held is whatever
        /// the line held before, moved only by whole splices.
        /// </summary>
        public void ProcessSteered(Span<float> audio, RxContentClass cls = RxContentClass.Unclassified)
        {
            int frames = audio.Length / _ch;
            int chunk = ChunkFrames();
            for (int at = 0; at < frames; at += chunk)
            {
                int n = Math.Min(chunk, frames - at);
                var part = audio.Slice(at * _ch, n * _ch);
                Write(part, cls);
                Render(part);
            }
        }

        // ── Requests ──

        /// <summary>
        /// Ask for <paramref name="frames"/> to be removed (positive) or added
        /// (negative) on <paramref name="policy"/>. Replaces what was pending.
        /// Below the smallest splice it is done: the event ends and the remainder
        /// stays as depth. <paramref name="exact"/> asks that no splice leave a
        /// remainder smaller than the smallest splice, so a drain can land on its
        /// target exactly when the material allows.
        /// </summary>
        public void Request(long frames, in LagPolicy policy, LagCause cause, bool exact = false)
        {
            if (Math.Abs(frames) < _minLag)
            {
                if (_eventActive) { _event.Remainder = frames; EndEvent(abandoned: false); }
                _pending = 0;
                return;
            }
            bool sameEvent = _eventActive && _event.Cause == cause && Math.Sign(_event.Requested) == Math.Sign(frames);
            if (_eventActive && !sameEvent) EndEvent(abandoned: true);
            if (!_eventActive)
            {
                _event = new RxLagEvent { Cause = cause, Policy = policy.Kind, Requested = frames, WorstCorrelation = 1f };
                _eventActive = true;
                _eventStart = _out;
            }
            _event.Policy = policy.Kind;
            _pending = frames;
            _policy = policy;
            _cause = cause;
            _exact = exact;
        }

        /// <summary>Stop the request where it is (a starvation spent the excess; a newer decision). The event ends abandoned.</summary>
        public void Cancel()
        {
            _pending = 0;
            if (_eventActive) EndEvent(abandoned: true);
        }

        /// <summary>The owner fell back to its old behaviour; the event says so.</summary>
        public void MarkFallback()
        {
            if (_eventActive) _event.FellBack = true;
            else _completed.FellBack = true;
        }

        /// <summary>
        /// Owner two: cut <paramref name="frames"/> at <paramref name="atPosition"/>
        /// (an absolute read position), with an r = 0 fade over 10 ms. Not
        /// subject to the budget or the gates: the cut above the catch-up limit.
        /// </summary>
        public void RequestCut(long atPosition, long frames)
        {
            if (frames <= 0) { _cutPending = false; return; }
            _cutPending = true;
            _cutAt = Math.Max(atPosition, _read);
            _cutFrames = frames;
        }

        public bool CutPending => _cutPending;

        /// <summary>
        /// Owner two: find the quiet moment for a cut, looking no further than
        /// <paramref name="horizonFrames"/> ahead of the read point: the start of
        /// the first gap run of at least <paramref name="gapFrames"/>, else the
        /// quietest block. The research's rule: wait up to 1 s for a gap of 50 ms,
        /// else the quietest 10 ms in the next second (first report, section 5).
        /// </summary>
        public long FindQuietMoment(long horizonFrames, long gapFrames)
        {
            EnsureFloor();
            long from = _read, to = Math.Min(_write, _read + horizonFrames);
            long runStart = -1, quietAt = from;
            float quietest = float.MaxValue;
            for (long b = from / _block; b * _block < to; b++)
            {
                int slot = (int)(b % _blocks);
                if (_blockId[slot] != b) break;
                long start = Math.Max(from, b * _block);
                if (IsGap(slot)) { if (runStart < 0) runStart = start; if ((b + 1) * _block - runStart >= gapFrames) return runStart; }
                else runStart = -1;
                if (_blockSum[slot] < quietest) { quietest = _blockSum[slot]; quietAt = start; }
            }
            return quietAt;
        }

        /// <summary>The event that ended most recently, once: true when there is one not yet taken.</summary>
        public bool TryTakeCompleted(out RxLagEvent e)
        {
            if (_completedTaken == _completedSeq) { e = default; return false; }
            _completedTaken = _completedSeq;
            e = _completed;
            return true;
        }

        /// <summary>The event in progress, for its owner's trace (a copy).</summary>
        public bool TryPeekActive(out RxLagEvent e)
        {
            e = _event;
            if (_eventActive) e.FramesTaken = _out - _eventStart;
            return _eventActive;
        }

        private void EndEvent(bool abandoned)
        {
            _event.FramesTaken = _out - _eventStart;
            _event.Abandoned = abandoned;
            if (!abandoned && _event.Remainder == 0) _event.Remainder = _pending;
            _completed = _event;
            _completedSeq++;
            _eventActive = false;
        }

        // ── Rendering ──

        /// <summary>
        /// Render <paramref name="output"/>'s frames from the read point, applying
        /// any splice, seam join or cut. Returns the frames that had to be silence
        /// because the audio ran out (owner two's starvation).
        /// </summary>
        public long Render(Span<float> output)
        {
            int frames = output.Length / _ch;
            int j = 0;
            long silent = 0;
            if (_pending != 0) _budget.Touch(_policy, _lastTonal);
            while (j < frames)
            {
                var dst = output.Slice(j * _ch);
                int remaining = frames - j;
                if (_fade != FadeKind.None)
                {
                    j += StepFade(dst, remaining);
                    continue;
                }
                if (_cutPending && _read >= _cutAt && StartCut(remaining)) continue;
                long seam = _seamCount > 0 ? NextSeam() : -1;
                if (seam == _read && seam >= 0)
                {
                    _seamHead = (_seamHead + 1) % _seams.Length; _seamCount--;
                    // Nothing from before a seam is ever repeated after it.
                    _growGuard = _growGuard == long.MinValue ? seam : Math.Max(_growGuard, seam);
                    if (!_dry && StartSeam(remaining)) continue;
                }
                if (_pending != 0 && _out >= _nextDecision && !_dry)
                {
                    _nextDecision = _out + _decisionInterval;
                    if (TryStartSplice(remaining)) continue;
                }
                long avail = _write - _read;
                if (avail <= 0)
                {
                    // Dry: silence for the rest, and fade in when audio returns.
                    dst.Slice(0, remaining * _ch).Clear();
                    silent += remaining;
                    SilentFrames += remaining;
                    if (DryFades && !_dry) _fadeInLeft = _dryFade;
                    _dry = true;
                    j = frames;
                    _out += remaining;
                    break;
                }
                int n = remaining;
                if (_pending != 0) n = (int)Math.Max(1, Math.Min(n, _nextDecision - _out));
                if (_cutPending && _cutAt > _read) n = (int)Math.Min(n, _cutAt - _read);
                if (seam > _read) n = (int)Math.Min(n, seam - _read);
                if (n > avail) n = (int)avail;
                long fadeOutFrom = long.MaxValue;
                if (DryFades && remaining > avail) fadeOutFrom = _write - Math.Min(_dryFade, avail);
                _dry = false;
                CopyOut(dst, n, fadeOutFrom);
                j += n;
            }
            if (_ownsBudget) _budget.Advance(frames);
            return silent;
        }

        /// <summary>Copy <paramref name="n"/> frames from the read point, with the dry fades where they apply.</summary>
        private void CopyOut(Span<float> dst, int n, long fadeOutFrom)
        {
            for (int f = 0; f < n; f++)
            {
                long p = _read + f;
                if (p >= _write) ReadsPastWrite++;
                int i = Idx(p);
                var segment = _seg[i >> _segShift];
                int src = (i & _segMask) * _ch;
                float g = 1f;
                if (_fadeInLeft > 0)
                {
                    g = CrossFadeCurve.In((_dryFade - _fadeInLeft + 1f) / _dryFade);
                    _fadeInLeft--;
                }
                if (p >= fadeOutFrom)
                {
                    long len = _write - fadeOutFrom;
                    g *= CrossFadeCurve.Out((p - fadeOutFrom + 1f) / len);
                }
                for (int c = 0; c < _ch; c++) dst[f * _ch + c] = segment[src + c] * g;
            }
            if (CarrySeams && _carryCount > 0) EmitRange(_read, n, _out);
            _read += n;
            _out += n;
        }

        private int Idx(long pos)
        {
            long m = pos % _cap;
            if (m < 0) m += _cap;
            return (int)m;
        }

        /// <summary>Channel <paramref name="c"/> of the frame at <paramref name="pos"/> in the ring (no read-past check).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private float R(long pos, int c)
        {
            int i = Idx(pos);
            return _seg[i >> _segShift][(i & _segMask) * _ch + c];
        }

        /// <summary>Tests only: the ring's segments.</summary>
        internal float[][] SegmentsForTest => _seg;

        /// <summary>Tests only: channel <paramref name="c"/> of the ring at <paramref name="pos"/>.</summary>
        internal float RingForTest(long pos, int c) => R(pos, c);

        /// <summary>Tests only: the first <paramref name="frames"/> frames of the kept continuation (an overflow's old side).</summary>
        internal float[] KeptForTest(int frames) => _overflowTail.AsSpan(0, frames * _ch).ToArray();

        /// <summary>Tests only: what the fade in progress plays for its old side at <paramref name="pos"/>, from wherever it reads it.</summary>
        internal float OldSideForTest(long pos, int c) => _oldFromCopy ? _oldCopy[(int)(pos - _fadeA) * _ch + c] : R(pos, c);

        /// <summary>Tests only: the fade in progress reads its old side from a copy made when it began.</summary>
        internal bool OldSideCopiedForTest => _oldFromCopy;

        /// <summary>Tests only: the fade in progress (0 none, 1 splice, 2 seam, 3 cut, 4 overflow).</summary>
        internal int FadeKindForTest => (int)_fade;

        /// <summary>
        /// Tests only: the positions [From, To) whose audio the fade in progress still
        /// plays for its old side (from the ring or from its copy), or an empty range for
        /// none, or for an overflow (whose old side is the continuation kept).
        /// </summary>
        internal (long From, long To) OldSideStillPlayedForTest
        {
            get
            {
                if (_fade == FadeKind.None || _fade == FadeKind.Overflow) return (0, 0);
                if (_fade == FadeKind.Seam) return (_fadeA, _fadeA + Math.Min(_fadePeriod, _fadeHold + _fadeLen));
                return (_fadeA + _fadeAt, _fadeA + _fadeHold + _fadeLen);
            }
        }

        private float Sample(long pos, int c)
        {
            if (pos >= _write) ReadsPastWrite++;
            return R(pos, c);
        }

        /// <summary>One or more frames of the fade in progress.</summary>
        private int StepFade(Span<float> dst, int remaining)
        {
            int total = _fadeHold + _fadeLen;
            int n = Math.Min(remaining, total - _fadeAt);
            for (int f = 0; f < n; f++)
            {
                int k = _fadeAt + f;
                long oldPos = _fade == FadeKind.Seam ? _fadeA + (k % _fadePeriod) : _fadeA + k;
                if (k < _fadeHold)
                {
                    // A seam's extension of the audio before, alone, until its phase meets the audio after.
                    for (int c = 0; c < _ch; c++) dst[f * _ch + c] = OldSide(oldPos, c);
                    continue;
                }
                int j = k - _fadeHold;
                float t = (j + 1f) / _fadeLen;
                long newPos = _fadeB + j;
                for (int c = 0; c < _ch; c++)
                {
                    float r = _fade == FadeKind.Cut ? 0f : _fadeR[c];
                    dst[f * _ch + c] = OldSide(oldPos, c) * CrossFadeCurve.Out(t, r) + Sample(newPos, c) * CrossFadeCurve.In(t, r);
                }
            }
            if (CarrySeams && _carryCount > 0)
            {
                // The old side's frames are read at k, the new side's at k - hold.
                int k0 = _fadeAt;
                if (_fade != FadeKind.Seam) EmitRange(_fadeA + k0, n, _out);
                int j0 = Math.Max(0, k0 - _fadeHold), j1 = Math.Max(0, k0 + n - _fadeHold);
                if (j1 > j0) EmitRange(_fadeB + j0, j1 - j0, _out + (j0 + _fadeHold - k0));
            }
            _fadeAt += n;
            _read = _fadeB + Math.Max(0, _fadeAt - _fadeHold);
            _out += n;
            if (_fadeAt >= total) { _fade = FadeKind.None; _fadeHold = 0; }
            return n;
        }

        /// <summary>
        /// The fade's old side at <paramref name="oldPos"/>: an overflow's from the
        /// continuation kept, a far one's from its copy, any other's from the ring.
        /// </summary>
        private float OldSide(long oldPos, int c)
        {
            int k = (int)(oldPos - _fadeA);
            if (_fade == FadeKind.Overflow) return _overflowTail[k * _ch + c];
            if (_oldFromCopy) return _oldCopy[k * _ch + c];
            return Sample(oldPos, c);
        }

        private void BeginFade(FadeKind kind, long oldSide, long newSide, int length, int period = 0, int hold = 0)
        {
            _fadeHold = Math.Max(0, hold);
            _fade = kind;
            _fadeA = oldSide;
            _fadeB = newSide;
            _fadeLen = Math.Max(1, length);
            _fadeAt = 0;
            _fadePeriod = period > 0 ? period : int.MaxValue;
            // The read point moves to the new side now, but the fade reads its old side
            // until it ends, and Write keeps only HistoryFrames behind the read point. An
            // old side further back than that (a cut skips a whole backlog) would be
            // overwritten by a write that fits, so it is copied now, while it is intact
            // (Codex's re-check 3 of 7a8b2f90). A seam's old side stays put while the read
            // point moves on through the fade, so its distance is measured at the end.
            _oldFromCopy = false;
            if (kind != FadeKind.Overflow)
            {
                bool seam = kind == FadeKind.Seam;
                long farthest = newSide + (seam ? _fadeLen : 0) - oldSide;
                int span = seam ? Math.Min(_fadePeriod, _fadeHold + _fadeLen) : _fadeHold + _fadeLen;
                if (farthest > HistoryFrames && span * _ch <= _oldCopy.Length)
                {
                    for (int f = 0; f < span; f++)
                        for (int c = 0; c < _ch; c++) _oldCopy[f * _ch + c] = Sample(oldSide + f, c);
                    _oldFromCopy = true;
                }
            }
            // A removal never plays [old side's end, new side): a carried seam there comes out at the fade.
            if (CarrySeams && _carryCount > 0 && newSide > oldSide + _fadeLen) EmitRange(oldSide + _fadeLen, newSide - oldSide - _fadeLen, _out);
            _read = newSide;
        }

        // ── Block facts at a position ──

        private bool BlockKnown(long pos, out int slot)
        {
            long b = pos / _block;
            if (pos < 0) b = -1;
            slot = b >= 0 ? (int)(b % _blocks) : 0;
            return b >= 0 && _blockId[slot] == b;
        }

        private RxContentClass ClassAt(long pos)
        {
            long p = pos - ClassLagFrames;
            return BlockKnown(p, out int slot) ? (RxContentClass)_blockFlags[slot] : RxContentClass.Unclassified;
        }

        /// <summary>True when every block covering [from, to) is complete and a gap.</summary>
        private bool AllGap(long from, long to)
        {
            if (to <= from) return true;
            for (long b = from / _block; b * _block < to; b++)
            {
                if (b < 0) return false;
                int slot = (int)(b % _blocks);
                if (_blockId[slot] != b || !IsGap(slot)) return false;
            }
            return true;
        }

        /// <summary>
        /// The pause around <paramref name="pos"/> as it will be HEARD, in frames:
        /// the gap run's length in the blocks in hand (a lower bound when it runs
        /// on past the write point), less what earlier splices already removed
        /// from this same run. Without that second term every splice in a long
        /// pause saw the whole original pause and the floor was not kept.
        /// </summary>
        private long PauseLength(long pos)
        {
            long b0 = pos / _block;
            int s0 = b0 >= 0 ? (int)(b0 % _blocks) : 0;
            if (b0 < 0 || _blockId[s0] != b0 || !IsGap(s0)) return 0;
            long first = b0, last = b0;
            while (true)
            {
                long b = first - 1;
                if (b < 0) break;
                int slot = (int)(b % _blocks);
                if (_blockId[slot] != b || !IsGap(slot)) break;
                first = b;
                if (b0 - first > _blocks - 2) break;
            }
            while (true)
            {
                long b = last + 1;
                int slot = (int)(b % _blocks);
                if (_blockId[slot] != b || !IsGap(slot)) break;
                last = b;
                if (last - b0 > _blocks - 2) break;
            }
            if (first != _pauseRunFirst) { _pauseRunFirst = first; _pauseRunRemoved = 0; }
            return (last - first + 1) * _block - _pauseRunRemoved;
        }

        private long _pauseRunFirst = -1, _pauseRunRemoved;

        /// <summary>The first block of the run of blocks around <paramref name="pos"/> that share its gap state (a keyed element, a key-up, a carrier).</summary>
        private long RunFirstBlock(long pos)
        {
            long b0 = pos / _block;
            if (b0 < 0) return -1;
            int s0 = (int)(b0 % _blocks);
            if (_blockId[s0] != b0) return b0;
            bool gap = IsGap(s0);
            long first = b0;
            while (b0 - first < _blocks - 2)
            {
                long b = first - 1;
                if (b < 0) break;
                int slot = (int)(b % _blocks);
                if (_blockId[slot] != b || IsGap(slot) != gap) break;
                first = b;
            }
            return first;
        }

        // ── Correlation ──

        /// <summary>Normalised correlation of x[p..p+w) and x[q..q+w) on channel c, every stride-th sample; energies out.</summary>
        private float Corr(long p, long q, int w, int c, int stride, out double ep, out double eq)
        {
            double xy = 0, xx = 0, yy = 0;
            // The hot loop of every search: step through the segments directly, one
            // modulo per call, no call per sample (a Debug build does not inline R).
            var seg = _seg; int sh = _segShift, m = _segMask, ch = _ch, cap = _cap;
            int ip = Idx(p), iq = Idx(q);
            for (int i = 0; i < w; i += stride)
            {
                float x = seg[ip >> sh][(ip & m) * ch + c], y = seg[iq >> sh][(iq & m) * ch + c];
                xy += (double)x * y; xx += (double)x * x; yy += (double)y * y;
                ip += stride; if (ip >= cap) ip -= cap;
                iq += stride; if (iq >= cap) iq -= cap;
            }
            ep = xx; eq = yy;
            if (xx < 1e-12 || yy < 1e-12) return 0f;
            return (float)(xy / Math.Sqrt(xx * yy));
        }

        /// <summary>
        /// The splice score at one lag: the WORSE ear among those carrying sound
        /// (the first report, section 3: one splice point for both ears, scored by
        /// the worse ear). Which ears carry sound is decided once per decision by
        /// <see cref="DecideActiveEars"/>; when none does, the better ear's score.
        /// </summary>
        private float Score(long a, long b, int stride)
        {
            float worstActive = float.MaxValue, best = float.MinValue;
            bool any = false;
            for (int c = 0; c < _ch; c++)
            {
                float r = Corr(a, b, _window, c, stride, out _, out _);
                if (r > best) best = r;
                if (_earActive[c]) { any = true; if (r < worstActive) worstActive = r; }
            }
            return any ? worstActive : best;
        }

        /// <summary>
        /// Which ears carry sound at <paramref name="a"/>: above their own noise
        /// floor, OR periodic (their own best correlation over the lag range at
        /// least <see cref="TonalPeriodicity"/>). The second term matters: a steady
        /// carrier IS its own percentile floor (the follow-up, section 1), so by
        /// level alone a tone in one ear would not count and the other ear's
        /// correlation would choose a splice that steps it.
        /// </summary>
        private void DecideActiveEars(long a, bool forward, int lo, int hi)
        {
            for (int c = 0; c < _ch; c++)
            {
                double e = 0;
                var seg = _seg; int sh = _segShift, m = _segMask, ia = Idx(a);
                for (int i = 0; i < _window; i++) { float x = seg[ia >> sh][(ia & m) * _ch + c]; e += (double)x * x; if (++ia == _cap) ia = 0; }
                bool loud = e / _window > GapLimit(_floor[c]);
                bool periodic = false;
                if (!loud && hi >= lo)
                    for (int L = lo; L <= hi && !periodic; L += CoarseStride)
                        periodic = Corr(a, forward ? a + L : a - L, _window, c, CoarseStride, out _, out _) >= TonalPeriodicity;
                _earActive[c] = loud || periodic;
            }
        }

        /// <summary>
        /// The best lag in [lo, hi] for a splice from <paramref name="a"/>: forward
        /// (removal) or back (growth). A coarse pass at every fourth lag on every
        /// fourth sample, then a refined pass around its peak. Returns the lag, or
        /// 0 when the range is empty; <paramref name="score"/> its score.
        /// </summary>
        private int BestLag(long a, bool forward, int lo, int hi, out float score, bool exactRemainder, long need)
        {
            score = float.MinValue;
            if (hi < lo) return 0;
            int bestCoarse = lo;
            float bestCoarseScore = float.MinValue;
            for (int L = lo; L <= hi; L += CoarseStride)
            {
                long b = forward ? a + L : a - L;
                float s = Score(a, b, CoarseStride);
                if (s > bestCoarseScore) { bestCoarseScore = s; bestCoarse = L; }
            }
            int best = 0;
            int from = Math.Max(lo, bestCoarse - CoarseStride + 1), to = Math.Min(hi, bestCoarse + CoarseStride - 1);
            for (int L = from; L <= to; L++)
            {
                if (exactRemainder && !ExactOk(need, L)) continue;
                long b = forward ? a + L : a - L;
                float s = Score(a, b, 1);
                if (s > score) { score = s; best = L; }
            }
            if (best == 0 && exactRemainder && need >= lo && need <= hi)
            {
                long b = forward ? a + need : a - need;
                score = Score(a, b, 1);
                best = (int)need;
            }
            return best;
        }

        private bool ExactOk(long need, long L) => L == need || need - L >= _minLag;

        /// <summary>Per channel, the correlation of the fade's two sides, clamped 0 to 1, into <see cref="_fadeR"/>.</summary>
        private void MeasureFadeR(long oldSide, long newSide, int len, int period)
        {
            for (int c = 0; c < _ch; c++)
            {
                double xy = 0, xx = 0, yy = 0;
                for (int k = 0; k < len; k++)
                {
                    long op = period > 0 ? oldSide + (k % period) : oldSide + k;
                    float x = R(op, c), y = R(newSide + k, c);
                    xy += (double)x * y; xx += (double)x * x; yy += (double)y * y;
                }
                _fadeR[c] = xx < 1e-12 || yy < 1e-12 ? 0f : (float)Math.Clamp(xy / Math.Sqrt(xx * yy), 0, 1);
            }
        }

        /// <summary>The transient guard: the two sides within 3 dB on every ear that carries sound, and no onset in the span cut or repeated.</summary>
        private bool TransientOk(long oldSide, long newSide, int len, long spanFrom, long spanTo)
        {
            for (int c = 0; c < _ch; c++)
            {
                double eo = 0, en = 0;
                for (int k = 0; k < len; k++)
                {
                    float x = R(oldSide + k, c), y = R(newSide + k, c);
                    eo += (double)x * x; en += (double)y * y;
                }
                double floor = GapLimit(_floor[c]) * len;
                if (eo <= floor && en <= floor) continue;
                double db = 10 * Math.Log10((eo + 1e-20) / (en + 1e-20));
                if (Math.Abs(db) > TransientDb) return false;
            }
            // Onsets in the span: a 2.5 ms sub-block more than 6 dB above the one before it, and above the floor.
            double prev = -1;
            for (long p = spanFrom; p + _onsetBlock <= spanTo; p += _onsetBlock)
            {
                double e = 0;
                int ip = Idx(p);
                for (int k = 0; k < _onsetBlock; k++)
                {
                    var row = _seg[ip >> _segShift]; int o = (ip & _segMask) * _ch;
                    for (int c = 0; c < _ch; c++) { float x = row[o + c]; e += (double)x * x; }
                    if (++ip == _cap) ip = 0;
                }
                e /= (_onsetBlock * _ch);
                if (prev >= 0 && e > GapLimit(_floor[_ch]) && e > prev * Math.Pow(10, OnsetDb / 10)) return false;
                prev = e;
            }
            return true;
        }

        // ── The decision ──

        private bool TryStartSplice(int remaining)
        {
            EnsureFloor();
            long a = _read;
            var cls = ClassAt(a);
            if ((cls & RxContentClass.NoSplice) != 0) return false;
            bool removal = _pending > 0;
            long need = Math.Abs(_pending);
            double periodTokens = _budget.PeriodTokens, gapTokens = _budget.GapTokens;
            if (periodTokens < _minLag && gapTokens < _minLag) return false;

            // How far this splice may reach, before content and history decide.
            long reach;
            long ahead = _write - a;
            if (removal)
            {
                // After the splice this render must still be served from the new side.
                reach = Math.Min(need, ahead - remaining);
            }
            else
            {
                // A growth reads only audio already played, never a region repeated
                // already, and never back across a seam; its window and the fade's
                // old side read forward from a, so that much must be in hand.
                if (ahead < Math.Max(_window, _minLag)) return false;
                long oldest = Math.Max(_validFrom, _write - _cap + 2 * _maxLag + _cutFade);
                if (_growGuard != long.MinValue) oldest = Math.Max(oldest, _growGuard);
                reach = Math.Min(need, a - oldest);
            }
            if (reach < _minLag) return false;
            // Never splice across a seam still ahead: the join comes first.
            long seamAhead = NextSeam();
            if (seamAhead > a && seamAhead <= a + 2 * _maxLag + _gapMaxSpeech + _window) return false;

            // Periodicity, over the whole lag range the content allows, for the tonal measure.
            int searchHi = removal
                ? (int)Math.Min(_maxLag, ahead - _window)
                : (int)Math.Min(_maxLag, Math.Min(reach, ahead));
            float periodicity = float.MinValue;
            int periodLag = 0;
            if (searchHi >= _minLag && WholePeriodSearch)
            {
                DecideActiveEars(a, removal, _minLag, searchHi);
                periodLag = BestLag(a, removal, _minLag, searchHi, out periodicity, false, 0);
            }
            // Tonal is the shifter's OWN measure, whatever the speech detector says:
            // the stored v0.1.1 voice branch calls a steady 625 Hz carrier speech at
            // a probability of 0.999 (measured, RxContentClassifierTests), so a
            // speech flag must never lift the tonal veto, or a carrier goes down the
            // gap path and steps by a radian (the follow-up, section 1). Strongly
            // periodic voiced speech then takes the stricter gate: slower, never worse.
            bool tonal = periodicity >= TonalPeriodicity;
            _lastTonal = tonal;

            // Gaps first (first report, section 5), unless the material is tonal.
            if (_policy.AllowsGaps && !(tonal && TonalVeto) && TryGap(a, removal, need, reach, remaining, cls, gapTokens))
                return true;

            return TryPeriod(a, removal, need, reach, remaining, tonal, periodicity, periodLag, periodTokens);
        }

        private bool TryGap(long a, bool removal, long need, long reach, int remaining, RxContentClass cls, double tokens)
        {
            bool cw = (cls & RxContentClass.Cw) != 0;
            if (cw && _out - _lastCwGap < _cwGapInterval) return false;
            // The classes govern the gap path, not energy alone (Codex's check of
            // 2567a596, B2; the brief, item 2; the follow-up, section 6): the energy
            // gate alone calls 20 to 83 percent of weak speech a pause. Every block the
            // splice touches must be confirmed: no speech and no loudness in any of
            // them, and either 100 ms of consecutive not-voice (the fast noise policy)
            // or, in CW, a key-up. Unclassified audio is voice: it never takes this path.
            int F = _gapFade;
            long L = 0;
            bool uncertain = false;
            for (int pass = 0; pass < 2; pass++)
            {
                int cap = cw ? _gapMaxCw : uncertain ? _gapMaxUncertain : _gapMaxNoise;
                L = Math.Min(cap, Math.Min(reach, (long)tokens));
                if (_exact && !ExactOk(need, L))
                {
                    if (need <= L) L = need;
                    else L = Math.Min(L, need - _minLag);
                }
                if (L < _minLag) return false;
                bool wasUncertain = uncertain;
                if (!GapClassOk(removal ? a : a - L, removal ? a + L + F : a + F, out uncertain)) return false;
                // A block the detector was unsure of caps the splice at 20 ms: once more at that cap.
                if (!uncertain || wasUncertain || L <= _gapMaxUncertain) break;
            }
            long oldSide = a, newSide = removal ? a + L : a - L;
            if (SpanHas(removal ? a : newSide, removal ? newSide + F : a + F, RxContentClass.NoSplice)) return false;
            if (removal)
            {
                if (newSide + F > _write) return false;
                if (!AllGap(a, newSide + F)) return false;
                // The pause floor: what is left of this pause must still be 150 ms.
                if (PauseLength(a) - L < _pauseFloor) return false;
                if (!TransientOk(oldSide, newSide, F, a, newSide + F)) return false;
            }
            else
            {
                if (a + F > _write) return false;
                if (!AllGap(newSide, a + F)) return false;
                if (!TransientOk(oldSide, newSide, F, newSide, a + F)) return false;
            }
            if (!_budget.TakeGap((int)L)) return false;
            if (removal) _pauseRunRemoved += L;
            MeasureFadeR(oldSide, newSide, F, 0);
            Log(oldSide, newSide, (int)L, F, _fadeR[0], 1);
            BeginFade(FadeKind.Splice, oldSide, newSide, F);
            Account(removal, L, gap: true, score: 1f);
            if (cw) _lastCwGap = _out;
            if (!removal) _growGuard = a + F;
            return true;
        }

        /// <summary>
        /// True when every classification block the audio [<paramref name="from"/>,
        /// <paramref name="to"/>) touches may be gap-spliced by its class: no Speech
        /// and no Active, and confirmed not-voice (Classified with NotVoiceRun), or CW
        /// (the slice mode's flag, which holds with or without a detector). The
        /// interval is shifted into the classification timeline FIRST
        /// (<see cref="ClassLagFrames"/> back) and every block it touches is read,
        /// both partial ends included: Codex's re-check of 5a0a8c93 (B2) found that
        /// shifting sample points after walking audio blocks skipped a trailing
        /// partial speech block whenever the lag was not a whole number of blocks.
        /// A block not in hand reads as unclassified, so it refuses.
        /// <paramref name="uncertain"/> is set when any block's voice probability
        /// sat between 0.1 and 0.3.
        /// </summary>
        private bool GapClassOk(long from, long to, out bool uncertain)
        {
            uncertain = false;
            if (to <= from) return true;
            long cFrom = from - ClassLagFrames, cTo = to - ClassLagFrames;
            for (long b = FloorBlock(cFrom); b * _block < cTo; b++)
            {
                var c = ClassOfBlockAt(b);
                if ((c & (RxContentClass.Speech | RxContentClass.Active)) != 0) return false;
                // The CW exception is the BLOCK's, never the span's (Codex's re-check 2 of
                // 0d8ecb3c): a span starting in CW used to waive confirmation for every
                // block it touched, unknown voice-mode blocks included.
                if ((c & RxContentClass.Cw) == 0
                    && (c & (RxContentClass.Classified | RxContentClass.NotVoiceRun)) != (RxContentClass.Classified | RxContentClass.NotVoiceRun)) return false;
                if ((c & RxContentClass.Uncertain) != 0) uncertain = true;
            }
            return true;
        }

        /// <summary>True when any classification block the audio [<paramref name="from"/>, <paramref name="to"/>) touches carries <paramref name="flag"/>.</summary>
        private bool SpanHas(long from, long to, RxContentClass flag)
        {
            if (to <= from) return false;
            long cFrom = from - ClassLagFrames, cTo = to - ClassLagFrames;
            for (long b = FloorBlock(cFrom); b * _block < cTo; b++)
                if ((ClassOfBlockAt(b) & flag) != 0) return true;
            return false;
        }

        private long FloorBlock(long pos) => pos >= 0 ? pos / _block : -((-pos + _block - 1) / _block);

        /// <summary>The class of classification block <paramref name="b"/>, or unclassified when it is not in hand.</summary>
        private RxContentClass ClassOfBlockAt(long b)
        {
            if (b < 0) return RxContentClass.Unclassified;
            int slot = (int)(b % _blocks);
            return _blockId[slot] == b ? (RxContentClass)_blockFlags[slot] : RxContentClass.Unclassified;
        }

        private bool TryPeriod(long a, bool removal, long need, long reach, int remaining, bool tonal, float periodicity,
            int periodLag, double tokens)
        {
            if (tonal && _out - _lastTonalSplice < _tonalInterval) return false;
            long run = tonal ? RunFirstBlock(a) : -1;
            if (tonal && run == _lastTonalRun && a - run * _block < _tonalElement) return false;
            int maxJump = tonal ? _tonalMaxLag : _maxLag;
            long hi = Math.Min(maxJump, Math.Min(reach, (long)tokens));
            // A period splice fades over one lag, so the whole fade must be in hand
            // before it starts: twice the lag ahead of a for a removal (old side
            // and new side), the lag and the correlation window for a growth.
            long ahead = _write - a;
            if (removal) hi = Math.Min(hi, Math.Min(ahead / 2, ahead - _window));
            else hi = Math.Min(hi, ahead);
            if (hi < _minLag) return false;
            int L;
            float score;
            if (!WholePeriodSearch)
            {
                // The positive control: no correlation, the longest lag allowed.
                L = (int)hi;
                score = 1f;
            }
            else
            {
                L = BestLag(a, removal, _minLag, (int)hi, out score, _exact, need);
                if (L == 0) return false;
                float gate = tonal ? TonalGate : VoiceGate;
                if (score < gate) return false;
            }
            if (_exact && !ExactOk(need, L)) return false;
            int F = L;
            long newSide = removal ? a + L : a - L;
            // A digital mode anywhere in what the splice touches (a mode change part-way) refuses it.
            if (SpanHas(removal ? a : newSide, removal ? newSide + F : a + F, RxContentClass.NoSplice)) return false;
            if (removal ? newSide + F > _write : a + F > _write) return false;
            if (removal ? !TransientOk(a, newSide, F, a, newSide + F) : !TransientOk(a, newSide, F, newSide, a + F)) return false;
            if (!_budget.TakePeriod(L)) return false;
            MeasureFadeR(a, newSide, F, 0);
            Log(a, newSide, L, F, score, tonal ? (byte)3 : (byte)2);
            BeginFade(FadeKind.Splice, a, newSide, F);
            Account(removal, L, gap: false, score: score);
            if (tonal) { _lastTonalSplice = _out; _lastTonalRun = run; }
            if (!removal) _growGuard = a + F;
            return true;
        }

        private void Account(bool removal, long L, bool gap, float score)
        {
            if (removal) { _pending -= L; TotalRemoved += L; _event.Removed += L; }
            else { _pending += L; TotalAdded += L; _event.Added += L; }
            if (gap) { TotalGapSplices++; _event.GapSplices++; }
            else
            {
                TotalPeriodSplices++; _event.PeriodSplices++;
                if (score < _event.WorstCorrelation) _event.WorstCorrelation = score;
            }
            if (Math.Abs(_pending) < _minLag && _eventActive)
            {
                _event.Remainder = _pending;
                _pending = 0;
                EndEvent(abandoned: false);
            }
        }

        // ── Cut and seam ──

        /// <summary>The worse ear's correlation of the extension from its <paramref name="offset"/>-th frame on with x[after + i].</summary>
        private float SeamScoreFrom(long ext, int P, int offset, long after, int len, int stride)
        {
            float worst = float.MaxValue;
            for (int c = 0; c < _ch; c++)
            {
                double xy = 0, xx = 0, yy = 0;
                for (int i = 0; i < len; i += stride)
                {
                    float x = R(ext + ((offset + i) % P), c), y = R(after + i, c);
                    xy += (double)x * y; xx += (double)x * x; yy += (double)y * y;
                }
                float r = xx < 1e-12 || yy < 1e-12 ? 0f : (float)(xy / Math.Sqrt(xx * yy));
                if (r < worst) worst = r;
            }
            return worst;
        }

        private void MeasureSeamR(long ext, int P, int offset, long after, int len)
        {
            for (int c = 0; c < _ch; c++)
            {
                double xy = 0, xx = 0, yy = 0;
                for (int k = 0; k < len; k++)
                {
                    float x = R(ext + ((offset + k) % P), c), y = R(after + k, c);
                    xy += (double)x * y; xx += (double)x * x; yy += (double)y * y;
                }
                _fadeR[c] = xx < 1e-12 || yy < 1e-12 ? 0f : (float)Math.Clamp(xy / Math.Sqrt(xx * yy), 0, 1);
            }
        }

        /// <summary>The worse ear's correlation of the periodic extension x[ext + (i mod P)] with x[after + i], over len samples.</summary>
        private float SeamScore(long ext, int P, long after, int len, int stride)
        {
            float worst = float.MaxValue;
            for (int c = 0; c < _ch; c++)
            {
                double xy = 0, xx = 0, yy = 0;
                for (int i = 0; i < len; i += stride)
                {
                    float x = R(ext + (i % P), c), y = R(after + i, c);
                    xy += (double)x * y; xx += (double)x * x; yy += (double)y * y;
                }
                float r = xx < 1e-12 || yy < 1e-12 ? 0f : (float)(xy / Math.Sqrt(xx * yy));
                if (r < worst) worst = r;
            }
            return worst;
        }

        private bool StartCut(int remaining)
        {
            _cutPending = false;
            long a = _read;
            long C = Math.Min(_cutFrames, (_write - a) - Math.Max(remaining, _cutFade));
            if (C <= 0) return false;
            if (a + C + _cutFade > _write) return false;
            BeginFade(FadeKind.Cut, a, a + C, _cutFade);
            TotalCutFrames += C;
            if (_eventActive) { _event.Cuts++; _event.Removed += C; }
            return true;
        }

        private bool StartSeam(int remaining)
        {
            EnsureFloor();
            long s = _read;
            // The audio before, extended: its own period, from its tail.
            long oldest = Math.Max(_validFrom, _write - _cap + 2 * _maxLag + _cutFade);
            if (s - _window - _maxLag < oldest) return false;
            DecideActiveEars(s - _window, false, _minLag, _maxLag);
            int P = BestLag(s - _window, false, _minLag, _maxLag, out _, false, 0);
            if (P == 0) return false;
            int F = _seamFade;
            // The audio after the seam starts past the codec's own overlap (measured),
            // and the audio before is EXTENDED, one period at a time from what was
            // just played, for as long as it takes (under one period) for its phase
            // to meet the audio after, and never for less than the overlap skipped.
            // Adding, not dropping: a loss has already left the queue short, and
            // bridging rebuilds the reserve from there.
            int dMin = Ms(SeamSkipMilliseconds);
            if ((_write - s) < dMin + F) return false;
            long after = s + dMin;
            int hold = 0; float bestH = float.MinValue;
            for (int pass = 0; pass < 2; pass++)
            {
                int lo = pass == 0 ? 0 : Math.Max(0, hold - CoarseStride + 1);
                int hi = pass == 0 ? P - 1 : Math.Min(P - 1, hold + CoarseStride - 1);
                int step = pass == 0 ? CoarseStride : 1;
                for (int h = lo; h <= hi; h += step)
                {
                    float sc = SeamScoreFrom(s - P, P, h, after, F, pass == 0 ? CoarseStride : 1);
                    if (sc > bestH) { bestH = sc; hold = h; }
                }
            }
            // The skipped overlap is audio the listener no longer gets: the extension
            // must cover it, or the join removes net audio after a loss has already
            // spent the reserve (Codex's check of 2567a596, B3: a hold of 104 frames
            // against 120 skipped took the last 16 frames, a starvation, and the
            // 6.7 dB dip was that dry-out). Whole extra periods keep the phase match
            // just found: the extension is periodic in P.
            if (hold < dMin) hold += P * ((dMin - hold + P - 1) / P);
            MeasureSeamR(s - P, P, hold, after, F);
            Log(s - P, after, P, F, _fadeR[0], 4);
            BeginFade(FadeKind.Seam, s - P, after, F, P, hold);
            // Nothing before the join may be repeated after it.
            _growGuard = Math.Max(_growGuard == long.MinValue ? s : _growGuard, after);
            TotalSeamJoins++;
            TotalRemoved += dMin;
            TotalAdded += hold;
            if (_eventActive) { _event.SeamJoins++; _event.Removed += dMin; _event.Added += hold; }
            return true;
        }
    }
}
