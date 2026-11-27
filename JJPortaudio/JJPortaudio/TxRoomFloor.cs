using System;
using System.Threading;

namespace JJPortaudio
{
    /// <summary>
    /// The room's noise floor, measured where the expander listens (Sprint 49
    /// Track I, #1252), and the rule that turns it into the expander's
    /// automatic threshold. Radio-neutral: it lives beside the conditioner, so
    /// any radio that owns a <see cref="TxAudioConditioner"/> gets it (ruled
    /// 2026-11-23 11:22: the gate must work for sound-card radios too).
    /// </summary>
    /// <remarks>
    /// <para><b>Why here and not after the chain.</b> The old rule read the
    /// floor from the meter AFTER the conditioner, so it measured the gate's
    /// own effect: an over gated hard read a low floor, which set a low
    /// threshold for the next over, which gated nothing, read the real room
    /// and set a high threshold again, for ever (#1252). This meter is fed by
    /// the conditioner at the expander's INPUT, after noise reduction (never
    /// the raw microphone: a raw floor would sit well above the cleaned noise
    /// whenever noise reduction is on), and it hears through the same
    /// side-chain filter the detector does, on the detector's own scale (the
    /// channel-power sum, in dB).</para>
    /// <para><b>It reads the room with the expander's own detector (#1349).</b>
    /// The floor is a high percentile (<see cref="ReadingPercentile"/>) of
    /// the detector's envelope (<see cref="ExpanderDetector"/>, the same
    /// definition the stage uses), sampled once a millisecond through each
    /// 400 ms block, so "floor plus margin" is a margin over what the stage
    /// will actually read on the room. It was the block's MEAN power until
    /// #1349: the detector rides peaks, so on steady noise it read several dB
    /// over that mean (measured about 7.7 dB on pink and on low rumble through
    /// the side-chain filter), crossed the close point more often than every
    /// hold time, and at the default margin of 8 dB the stage never closed at
    /// any ratio. The percentile adapts to how peaky the room's noise is,
    /// which a fixed margin cannot. The mean is still kept, tracked the same
    /// way (<see cref="MeanFloorDb"/>), for a detector that AVERAGES, as
    /// WDSP's expander does: its threshold stays a margin over the room's
    /// average (<see cref="TxAudioConditioner.AveragingDetectorThreshold"/>).</para>
    /// <para><b>It learns only while the radio is NOT keyed</b> (S15: the
    /// threshold holds still while keyed, and speech would read as noise).
    /// The microphone runs while receiving (#565), so by the first over a
    /// few seconds after it opens there is already a floor. Before any
    /// measurement there is no floor and the threshold keeps its inert
    /// default; nothing is ever clipped for want of a reading.</para>
    /// <para><b>It moves slowly (Track I's judgement, weighed in its report).</b>
    /// 400 ms blocks every 100 ms; the target is the quietest block of the
    /// last <see cref="TrackWindowSeconds"/>, so the floor rides underneath
    /// intermittent speech or receive audio from a shack loudspeaker; the
    /// floor then follows the target down with a <see cref="FallSeconds"/>
    /// time constant and up with a <see cref="RiseSeconds"/> one. A loud
    /// stretch shorter than the window cannot move it at all.</para>
    /// <para><b>"Learn the room"</b> (Noel's ruling, 2026-11-23 11:12):
    /// <see cref="Learn"/> collects the next <see cref="LearnSeconds"/> of
    /// unkeyed audio, sets the floor to its quiet level (the 20th percentile
    /// of its blocks' readings, so a cough does not decide it; the mean
    /// floor likewise from its blocks' means), and restarts the tracking from
    /// there. Asked while keyed, it simply waits: keyed buffers never reach
    /// it.</para>
    /// <para>Audio thread: <see cref="Process"/> only. No lock and no
    /// allocation there; every array is made here. System-only.</para>
    /// </remarks>
    public sealed class TxRoomFloor
    {
        private const int Channels = 2;

        /// <summary>The window the quietest block is taken from, seconds.</summary>
        public const double TrackWindowSeconds = 8.0;
        /// <summary>The floor's fall time constant toward a quieter target, seconds.</summary>
        public const double FallSeconds = 1.0;
        /// <summary>The floor's rise time constant toward a louder target, seconds.</summary>
        public const double RiseSeconds = 10.0;
        /// <summary>How much unkeyed audio a first floor needs, seconds (20 blocks after the first 400 ms).</summary>
        public const double FirstFloorSeconds = 2.0;
        /// <summary>How long "Learn the room" listens, seconds.</summary>
        public const double LearnSeconds = 3.0;
        /// <summary>The percentile of the learn's blocks taken as the floor.</summary>
        public const double LearnPercentile = 0.20;
        /// <summary>
        /// The percentile of the detector's readings, through one 400 ms block,
        /// taken as that block's level (#1349): high, so the floor sits at the
        /// top of what the detector reads on the room. The 90th and the 95th
        /// measured within a few tenths of a decibel of each other on steady
        /// noise; 95 is the research's "high percentile".
        /// </summary>
        public const double ReadingPercentile = 0.95;

        private const int SubBlocksPerBlock = 4;              // 400 ms blocks
        private const int TrackBlocks = 80;                   // 8 s at one block per 100 ms
        private const int FirstFloorBlocks = 20;
        private const int LearnBlocks = 30;
        // The detector's readings: one a millisecond, at most this many in a 100 ms sub-block (100 at any whole-kHz rate).
        private const int MaxReadingsPerSub = 128;

        // Side-chain, as the detector hears it (set by the conditioner from the expander's settings).
        private volatile bool _sideChain;
        private volatile float _scLowHz = TxNoiseGate.DefaultSideChainLowHz, _scHighHz = TxNoiseGate.DefaultSideChainHighHz;
        private readonly SideChainFilter _filter = new SideChainFilter();
        private bool _designedOn;
        private float _designedLow = float.NaN, _designedHigh = float.NaN;
        private uint _designedRate;

        // Sub-blocks and blocks.
        private uint _rate;
        private int _subFrames, _framesIn;
        private double _sum;
        private readonly double[] _sub = new double[SubBlocksPerBlock];
        private int _subAt, _subCount;

        // The detector's readings (#1349), per sub-block, and a scratch the block's percentile is sorted in.
        private ExpanderDetector _detector;
        private int _readEvery, _readCount;
        private readonly double[] _readings = new double[SubBlocksPerBlock * MaxReadingsPerSub];
        private readonly int[] _readingsIn = new int[SubBlocksPerBlock];
        private readonly double[] _readingScratch = new double[SubBlocksPerBlock * MaxReadingsPerSub];

        // Tracking: the detector's reading, and the mean beside it.
        private readonly double[] _track = new double[TrackBlocks];
        private readonly double[] _trackMean = new double[TrackBlocks];
        private int _trackAt, _trackCount;
        private double _floor = double.NaN, _meanFloor = double.NaN;

        // Learning.
        private readonly double[] _learn = new double[LearnBlocks];
        private readonly double[] _learnMean = new double[LearnBlocks];
        private int _learnCount;
        private volatile bool _learnAsked;
        private bool _learning;
        private long _learnsFinished;
        private volatile float _learnedDb = float.NaN;
        private volatile float _learnedMeanDb = float.NaN;

        private volatile float _floorDb = float.NaN;
        private volatile float _meanFloorDb = float.NaN;
        private volatile bool _hasFloor;
        private long _blocks;

        /// <summary>Whether a floor has been measured.</summary>
        public bool HasFloor => _hasFloor;

        /// <summary>
        /// The floor, dB on the detector's scale: what the expander's detector
        /// reads on the room, at <see cref="ReadingPercentile"/> (#1349). NaN
        /// before any measurement. The threshold rule adds the margin to this.
        /// </summary>
        public float FloorDb => _floorDb;

        /// <summary>
        /// The room's average level, dB on the same scale (the channel-power
        /// sum through the same filter), tracked the same way: what the floor
        /// was before #1349, and what a detector that averages reads on the
        /// room. NaN before any measurement. It sits under <see cref="FloorDb"/>.
        /// </summary>
        public float MeanFloorDb => _meanFloorDb;

        /// <summary>400 ms blocks measured since this meter was made. A test and trace counter.</summary>
        public long Blocks => Interlocked.Read(ref _blocks);

        /// <summary>Whether a learn has been asked and has not finished.</summary>
        public bool Learning => _learnAsked || Volatile.Read(ref _learning);

        /// <summary>Learns finished since this meter was made; a caller notices a finished learn by this changing.</summary>
        public long LearnsFinished => Interlocked.Read(ref _learnsFinished);

        /// <summary>The floor the most recent finished learn found, dB; NaN before any.</summary>
        public float LearnedDb => _learnedDb;

        /// <summary>The room's average level the most recent finished learn found, dB; NaN before any.</summary>
        public float LearnedMeanDb => _learnedMeanDb;

        /// <summary>Hear through the side-chain filter, as the detector does. Set by the conditioner from the expander's settings in force.</summary>
        public void SetSideChain(bool on, float lowHz, float highHz)
        {
            _scLowHz = lowHz; _scHighHz = highHz; _sideChain = on;
        }

        /// <summary>"Learn the room": the next <see cref="LearnSeconds"/> of unkeyed audio sets the floor afresh. Any thread.</summary>
        public void Learn() => _learnAsked = true;

        /// <summary>Forget everything measured (tests, and a changed microphone). Takes effect at the next buffer.</summary>
        public void Forget() => _forgetAsked = true;
        private volatile bool _forgetAsked;

        /// <summary>
        /// Measure one buffer of interleaved stereo. The conditioner calls this
        /// with unkeyed buffers only, at the expander's input.
        /// </summary>
        public void Process(float[] buffer, int count, uint sampleRate)
        {
            if (sampleRate == 0) sampleRate = 48000;
            if (_forgetAsked)
            {
                _forgetAsked = false;
                _trackCount = 0; _trackAt = 0; _subCount = 0; _subAt = 0; _framesIn = 0; _sum = 0;
                _floor = double.NaN; _floorDb = float.NaN; _hasFloor = false;
                _meanFloor = double.NaN; _meanFloorDb = float.NaN;
                _learning = false; _learnCount = 0;
                ClearReadings();
                _detector.Power = 0;
            }
            if (sampleRate != _rate)
            {
                _rate = sampleRate;
                _subFrames = Math.Max(1, (int)(sampleRate / 10));
                _readEvery = Math.Max(1, (int)(sampleRate / 1000));
                _detector.Design(sampleRate);
                _framesIn = 0; _sum = 0; _subCount = 0; _subAt = 0;
                ClearReadings();
            }
            bool sc = _sideChain;
            float lo = _scLowHz, hi = _scHighHz;
            if (sc != _designedOn || lo != _designedLow || hi != _designedHigh || sampleRate != _designedRate)
            {
                // What the detector hears changed: the floor's scale changed with
                // it, so the window restarts (the floor itself is kept until the
                // new window says otherwise).
                _filter.Design(lo, hi, sampleRate);
                if (_designedRate != 0) { _trackCount = 0; _trackAt = 0; _subCount = 0; _subAt = 0; _framesIn = 0; _sum = 0; ClearReadings(); }
                _designedOn = sc; _designedLow = lo; _designedHigh = hi; _designedRate = sampleRate;
            }
            if (_learnAsked)
            {
                _learnAsked = false;
                Volatile.Write(ref _learning, true);
                _learnCount = 0;
                // Fresh: no block of the learn may hold audio from before it was asked.
                _subCount = 0; _subAt = 0; _framesIn = 0; _sum = 0;
                ClearReadings();
            }

            int frames = count / Channels;
            int i = 0;
            for (int f = 0; f < frames; f++)
            {
                double l = buffer[i], r = buffer[i + 1];
                if (sc)
                {
                    l = _filter.Step(0, l);
                    r = _filter.Step(1, r);
                }
                double power = l * l + r * r;
                _sum += power;
                // The detector's reading, once a millisecond (#1349).
                double reading = _detector.Step(power);
                if (++_readCount >= _readEvery)
                {
                    _readCount = 0;
                    int n = _readingsIn[_subAt];
                    if (n < MaxReadingsPerSub) { _readings[_subAt * MaxReadingsPerSub + n] = reading; _readingsIn[_subAt] = n + 1; }
                }
                i += Channels;
                if (++_framesIn < _subFrames) continue;

                _sub[_subAt] = _sum / _framesIn;
                _subAt = (_subAt + 1) % SubBlocksPerBlock;
                if (_subCount < SubBlocksPerBlock) _subCount++;
                _framesIn = 0; _sum = 0;
                if (_subCount == SubBlocksPerBlock) Block();
                // The sub-block now starting is the oldest: its readings go.
                _readingsIn[_subAt] = 0;
            }
            if (sc) _filter.FlushTiny();
        }

        private void ClearReadings()
        {
            Array.Clear(_readingsIn, 0, _readingsIn.Length);
            _readCount = 0;
        }

        /// <summary>A 400 ms block has completed (every 100 ms once the first has filled).</summary>
        private void Block()
        {
            double mean = 0;
            for (int k = 0; k < SubBlocksPerBlock; k++) mean += _sub[k];
            mean /= SubBlocksPerBlock;
            double meanDb = 10.0 * Math.Log10(mean + 1e-12);
            // The block's reading: a high percentile of the detector through it.
            int m = 0;
            for (int k = 0; k < SubBlocksPerBlock; k++)
                for (int j = 0; j < _readingsIn[k]; j++) _readingScratch[m++] = _readings[k * MaxReadingsPerSub + j];
            double reading = mean;
            if (m > 0)
            {
                Array.Sort(_readingScratch, 0, m); // in place; no allocation for a double[]
                reading = _readingScratch[(int)Math.Round(ReadingPercentile * (m - 1))];
            }
            double db = 10.0 * Math.Log10(reading + 1e-12);
            Interlocked.Increment(ref _blocks);

            if (Volatile.Read(ref _learning))
            {
                _learn[_learnCount] = db;
                _learnMean[_learnCount] = meanDb;
                _learnCount++;
                if (_learnCount == LearnBlocks)
                {
                    int at = (int)Math.Round(LearnPercentile * (LearnBlocks - 1));
                    Array.Sort(_learn, 0, LearnBlocks); // in place; no allocation for a double[]
                    Array.Sort(_learnMean, 0, LearnBlocks);
                    double learned = _learn[at], learnedMean = _learnMean[at];
                    _floor = learned;
                    _meanFloor = learnedMean;
                    _trackCount = 0; _trackAt = 0;
                    Publish();
                    _learnedDb = (float)learned;
                    _learnedMeanDb = (float)learnedMean;
                    Volatile.Write(ref _learning, false);
                    Interlocked.Increment(ref _learnsFinished);
                }
                return; // a learn's blocks do not also move the tracking
            }

            _track[_trackAt] = db;
            _trackMean[_trackAt] = meanDb;
            _trackAt = (_trackAt + 1) % TrackBlocks;
            if (_trackCount < TrackBlocks) _trackCount++;

            double target = double.MaxValue, meanTarget = double.MaxValue;
            for (int k = 0; k < _trackCount; k++)
            {
                if (_track[k] < target) target = _track[k];
                if (_trackMean[k] < meanTarget) meanTarget = _trackMean[k];
            }

            if (double.IsNaN(_floor))
            {
                if (_trackCount < FirstFloorBlocks) return;
                _floor = target;
                _meanFloor = meanTarget;
            }
            else
            {
                _floor = Follow(_floor, target);
                _meanFloor = Follow(_meanFloor, meanTarget);
            }
            Publish();
        }

        private static double Follow(double floor, double target)
        {
            double tau = target < floor ? FallSeconds : RiseSeconds;
            return floor + (target - floor) * (1.0 - Math.Exp(-0.1 / tau));
        }

        private void Publish()
        {
            _floorDb = (float)_floor;
            _meanFloorDb = (float)_meanFloor;
            _hasFloor = true;
        }

        /// <summary>
        /// THE threshold rule (Sprint 49 Track I, #1252): the floor plus the
        /// margin, or null when the threshold must be left alone: it is set by
        /// hand, the radio is keyed (S15: it holds still while keyed), or there
        /// is no floor yet. One rule, radio-neutral, used by the app's policy
        /// tick through <see cref="TxAudioConditioner.RefreshDerivedThreshold"/>.
        /// </summary>
        public static float? ThresholdRule(bool autoInForce, bool keyed, bool hasFloor, float floorDb, float marginDb)
        {
            if (!autoInForce || keyed || !hasFloor || !float.IsFinite(floorDb)) return null;
            return floorDb + marginDb;
        }
    }
}
