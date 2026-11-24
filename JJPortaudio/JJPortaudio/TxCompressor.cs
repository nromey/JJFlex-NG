using System;
using System.Threading;

namespace JJPortaudio
{
    /// <summary>One band of the compressor's split-band mode (Sprint 49 Track CL, phase 2): its own threshold, ratio, attack, release and makeup. Immutable; every value is clamped to its range as it is set.</summary>
    public sealed record TxCompressorBand
    {
        private readonly float _threshold = TxCompressorSettings.DefaultThresholdDb, _ratio = 2f, _attack = TxCompressorSettings.DefaultAttackMs,
            _release = TxCompressorSettings.DefaultReleaseMs, _makeup;

        /// <summary>dBFS, the band's own sample peak; see <see cref="TxCompressorSettings.ThresholdDb"/>.</summary>
        public float ThresholdDb { get => _threshold; init => _threshold = TxCompressorSettings.Clamp(value, TxCompressorSettings.MinThresholdDb, TxCompressorSettings.MaxThresholdDb, TxCompressorSettings.DefaultThresholdDb); }
        public float RatioToOne { get => _ratio; init => _ratio = TxCompressorSettings.Clamp(value, TxCompressorSettings.MinRatio, TxCompressorSettings.MaxRatio, 2f); }
        public float AttackMs { get => _attack; init => _attack = TxCompressorSettings.Clamp(value, TxCompressorSettings.MinAttackMs, TxCompressorSettings.MaxAttackMs, TxCompressorSettings.DefaultAttackMs); }
        public float ReleaseMs { get => _release; init => _release = TxCompressorSettings.Clamp(value, TxCompressorSettings.MinReleaseMs, TxCompressorSettings.MaxReleaseMs, TxCompressorSettings.DefaultReleaseMs); }
        public float MakeupDb { get => _makeup; init => _makeup = TxCompressorSettings.Clamp(value, TxCompressorSettings.MinMakeupDb, TxCompressorSettings.MaxMakeupDb, 0f); }
    }

    /// <summary>
    /// Every setting of the transmit compressor stage (Sprint 49 Track CL,
    /// #1278): the phase rotator, the drive, the compressor itself and its
    /// split-band mode. Immutable, so one reference can be handed to the audio
    /// thread; change it with a <c>with</c> expression. Every value is clamped
    /// to its range as it is set, so no setting can reach the audio thread out
    /// of range.
    /// </summary>
    /// <remarks>
    /// <b>Ranges and where they came from.</b> Ratio 1 (no compression) to 20
    /// and drive 0 to 18 dB are the reference design's; attack 0.1 to 300 ms,
    /// release 5 to 2000 ms, knee 0 to 24 dB and makeup -12 to +24 dB are the
    /// 2026-10-20 parity research's. The threshold runs -60 to 0 dBFS. The
    /// defaults are Track CL's judgement (its report): a plain, moderate
    /// compressor with no drive and no rotator.
    /// </remarks>
    public sealed record TxCompressorSettings
    {
        public const float MinThresholdDb = -60f, MaxThresholdDb = 0f, DefaultThresholdDb = -18f;
        public const float MinRatio = 1f, MaxRatio = 20f, DefaultRatio = 3f;
        public const float MinAttackMs = 0.1f, MaxAttackMs = 300f, DefaultAttackMs = 5f;
        public const float MinReleaseMs = 5f, MaxReleaseMs = 2000f, DefaultReleaseMs = 150f;
        public const float MinKneeDb = 0f, MaxKneeDb = 24f, DefaultKneeDb = 6f;
        public const float MinMakeupDb = -12f, MaxMakeupDb = 24f, DefaultMakeupDb = 6f;
        public const float MinDriveDb = 0f, MaxDriveDb = 18f;
        public const float MinCrossoverLowHz = 60f, MaxCrossoverLowHz = 1000f, DefaultCrossoverLowHz = 300f;
        public const float MinCrossoverHighHz = 800f, MaxCrossoverHighHz = 6000f, DefaultCrossoverHighHz = 1800f;

        /// <summary>The split's two crossovers stay at least this far apart, as a ratio, so the middle band is a band.</summary>
        public const float MinCrossoverSpacing = 1.5f;

        internal static float Clamp(float value, float low, float high, float fallback) =>
            float.IsFinite(value) ? Math.Clamp(value, low, high) : fallback;

        private readonly float _threshold = DefaultThresholdDb, _ratio = DefaultRatio, _attack = DefaultAttackMs, _release = DefaultReleaseMs,
            _knee = DefaultKneeDb, _makeup = DefaultMakeupDb, _drive, _crossLow = DefaultCrossoverLowHz, _crossHigh = DefaultCrossoverHighHz;
        private readonly int _rotator;
        private readonly TxCompressorBand _low = DefaultLow, _mid = DefaultMid, _high = DefaultHigh;

        /// <summary>A newly switched-on split's bands (Track CL's judgement): the low band held firmly, the middle and high bands gently.</summary>
        public static TxCompressorBand DefaultLow { get; } = new() { ThresholdDb = -24f, RatioToOne = 4f, AttackMs = 10f, ReleaseMs = 200f };
        public static TxCompressorBand DefaultMid { get; } = new() { ThresholdDb = -18f, RatioToOne = 2.5f, AttackMs = 5f, ReleaseMs = 150f };
        public static TxCompressorBand DefaultHigh { get; } = new() { ThresholdDb = -20f, RatioToOne = 2.5f, AttackMs = 2f, ReleaseMs = 100f };

        /// <summary>The settings a new compressor holds: off, and plain when switched on.</summary>
        public static TxCompressorSettings Default { get; } = new();

        public bool Enabled { get; init; }

        /// <summary>
        /// Where compression starts, dBFS of ONE channel's SAMPLE PEAK (0 dBFS
        /// is a sample at full scale), read after the drive. Not the expander's
        /// scale: that detector sums both channels' power, which reads about
        /// 3 dB above one channel's average power. The unit is named in the
        /// lever's words so the two thresholds are never confused.
        /// </summary>
        public float ThresholdDb { get => _threshold; init => _threshold = Clamp(value, MinThresholdDb, MaxThresholdDb, DefaultThresholdDb); }

        /// <summary>The ratio, n to 1: above the threshold, n dB more in gives 1 dB more out. 1 is no compression.</summary>
        public float RatioToOne { get => _ratio; init => _ratio = Clamp(value, MinRatio, MaxRatio, DefaultRatio); }

        public float AttackMs { get => _attack; init => _attack = Clamp(value, MinAttackMs, MaxAttackMs, DefaultAttackMs); }
        public float ReleaseMs { get => _release; init => _release = Clamp(value, MinReleaseMs, MaxReleaseMs, DefaultReleaseMs); }

        /// <summary>The knee's width, dB, centred on the threshold: 0 is a hard corner. Shared by every band in split-band mode.</summary>
        public float KneeDb { get => _knee; init => _knee = Clamp(value, MinKneeDb, MaxKneeDb, DefaultKneeDb); }

        /// <summary>Gain after the compression, dB. In split-band mode it follows the bands' own makeup.</summary>
        public float MakeupDb { get => _makeup; init => _makeup = Clamp(value, MinMakeupDb, MaxMakeupDb, DefaultMakeupDb); }

        /// <summary>Gain before the detector and the curve, dB (0 to 18): pushes more of the voice over the threshold.</summary>
        public float DriveDb { get => _drive; init => _drive = Clamp(value, MinDriveDb, MaxDriveDb, 0f); }

        /// <summary>The phase rotator's sections, 0 (off) to <see cref="PhaseRotator.MaxStages"/>.</summary>
        public int RotatorStages { get => _rotator; init => _rotator = Math.Clamp(value, 0, PhaseRotator.MaxStages); }

        /// <summary>Split-band mode: three bands, each compressed by its own settings, in place of the single band.</summary>
        public bool SplitBand { get; init; }

        /// <summary>The crossover between the low and middle bands, Hz, as chosen; see <see cref="TxCompressor.AppliedCrossovers"/> for what runs.</summary>
        public float CrossoverLowHz { get => _crossLow; init => _crossLow = Clamp(value, MinCrossoverLowHz, MaxCrossoverLowHz, DefaultCrossoverLowHz); }

        /// <summary>The crossover between the middle and high bands, Hz, as chosen.</summary>
        public float CrossoverHighHz { get => _crossHigh; init => _crossHigh = Clamp(value, MinCrossoverHighHz, MaxCrossoverHighHz, DefaultCrossoverHighHz); }

        public TxCompressorBand Low { get => _low; init => _low = value ?? DefaultLow; }
        public TxCompressorBand Mid { get => _mid; init => _mid = value ?? DefaultMid; }
        public TxCompressorBand High { get => _high; init => _high = value ?? DefaultHigh; }

        /// <summary>Band 0, 1 or 2: low, middle, high.</summary>
        public TxCompressorBand Band(int index) => index <= 0 ? Low : index == 1 ? Mid : High;
    }

    /// <summary>
    /// The transmit compressor stage (Sprint 49 Track CL, #1278): a phase
    /// rotator, a drive, then a feed-forward compressor with a soft knee and a
    /// makeup gain, in one stage; and, as a mode of the same stage, a
    /// three-band split-band compressor. It sits after the de-esser (and after
    /// JJ Clarity when that is built) and before the limiter.
    /// </summary>
    /// <remarks>
    /// <para><b>The static curve</b> is the soft-knee gain computer of
    /// Giannoulis, Massberg and Reiss, "Digital Dynamic Range Compressor
    /// Design: A Tutorial and Analysis", J. Audio Eng. Soc. 60(6), 2012: with
    /// level x, threshold T, ratio R and knee width W, all in dB, the output
    /// level is x below the knee, x + (1/R - 1)(x - T + W/2)^2 / (2W) inside
    /// it, and T + (x - T)/R above it. <see cref="ReductionDb"/> is that
    /// curve's reduction, x minus the output level.</para>
    /// <para><b>The detector follows the PEAK, in the linear domain</b>, and
    /// is converted to dB afterwards: the same paper's decoupled peak detector,
    /// a peak follower that jumps to a new peak at once and falls at the
    /// release rate, then a one-pole smoothing at the attack rate. Smoothing
    /// the level in dB instead would settle several dB under a sine's peak
    /// (the mean of a rectified sine's logarithm is about 6 dB under its
    /// peak), so a steady tone would be compressed less than its peak calls
    /// for. Here a steady tone settles to the reduction its peak calls for.
    /// One detector serves both channels (the stream is one microphone on two
    /// channels): the larger channel's magnitude, and one gain for both.</para>
    /// <para><b>The measured times.</b> On a step up, the detector reaches 63
    /// percent of the step in the attack time. On a step down it falls to 37
    /// percent in the release time plus about the attack time, because the
    /// attack smoothing follows the release (the paper's own caveat for the
    /// decoupled form).</para>
    /// <para><b>The order inside the stage:</b> the phase rotator, the drive,
    /// the detector and the curve, the makeup. With ratio 1, drive 0, makeup 0
    /// and the rotator off the output is the input, bit for bit; and audio
    /// that stays under the knee with drive and makeup at 0 is never
    /// touched.</para>
    /// <para><b>Split-band mode</b> (phase 2) splits the driven signal into
    /// three bands with fourth-order Linkwitz-Riley crossovers (two
    /// second-order Butterworth sections each way, 24 dB an octave), the low
    /// band passed through the second crossover's all-pass so the three sum
    /// flat in level. The sum is an all-pass of the input, not the input
    /// itself: level is unchanged at every frequency, phase is not, which is
    /// acceptable after the point where "what was removed" is measured. The
    /// steep slopes are why: with a subtractive split the middle band would
    /// still carry most of a low tone, and a band's settings would not act on
    /// its band alone. Each band has its own detector and curve; the knee, the
    /// drive and the rotator are shared.</para>
    /// <para><b>Lever changes are smoothed</b> (threshold, ratio, knee, makeup
    /// and drive glide to a new value over a few milliseconds), so turning one
    /// while keyed makes no click. A switch (on or off, the rotator's stage
    /// count, the mode) is made only on an unkeyed buffer by the conditioner,
    /// and restarts the stage from silence.</para>
    /// <para>Zero added delay. No lock and no allocation on the audio thread.
    /// System-only.</para>
    /// </remarks>
    public sealed class TxCompressor
    {
        private const int Channels = 2;
        private const double Ln10Over20 = 0.11512925464970228;
        private const double Butterworth = 0.70710678118654752;
        private const double LeverGlideSec = 0.005;

        private volatile TxCompressorSettings _settings = TxCompressorSettings.Default;

        /// <summary>The settings in force from the next buffer. Any thread: one reference.</summary>
        public TxCompressorSettings Settings
        {
            get => _settings;
            set => _settings = value ?? TxCompressorSettings.Default;
        }

        private struct Band
        {
            public double Threshold, Slope, Makeup;   // gliding toward the settings
            public double Peak, Envelope;             // the detector, linear
            public double Attack, Release;            // one-pole coefficients
            public double Reduction;                  // dB, the last frame's
        }

        private readonly Band[] _bands = new Band[3];
        private readonly PhaseRotator _rotator = new PhaseRotator();
        private double _drive, _knee, _makeup;
        private bool _wasEnabled, _wasSplit;
        private int _wasRotator = -1;
        private uint _rate;

        // The split's filters: per channel, two sections each of the low
        // crossover's low-pass and high-pass, the same for the high crossover,
        // and the low band's all-pass. Nine states a channel.
        private BiquadCoefficients _lp1, _hp1, _lp2, _hp2, _ap2;
        private readonly BiquadState[] _split = new BiquadState[Channels * 9];
        private float _designedLow = float.NaN, _designedHigh = float.NaN;
        private uint _designedRate;

        private volatile float _reductionDb, _detectorDb = -120f;
        private readonly float[] _bandReductionDb = new float[3];
        private long _overFrames, _overSumMilliDb;
        private int _overMaxCentiDb;

        /// <summary>The gain reduction at the end of the last buffer, dB (positive); in split-band mode, the deepest band's. Zero while off.</summary>
        public float ReductionDb => _reductionDb;

        /// <summary>One band's gain reduction at the end of the last buffer, dB: bands 0, 1, 2 in split-band mode; band 0 is the whole signal otherwise.</summary>
        public float BandReductionDb(int band) => Volatile.Read(ref _bandReductionDb[Math.Clamp(band, 0, 2)]);

        /// <summary>What the detector read at the end of the last buffer, dBFS of the sample peak after the drive (single-band mode; the low band's in split-band mode).</summary>
        public float DetectorDb => _detectorDb;

        /// <summary>Added delay, frames: always zero. The rotator's and the split's group delay depends on frequency and is not a delay of the stream.</summary>
        public int LatencyFrames => 0;

        /// <summary>
        /// One over's readings, taken and zeroed: the deepest gain reduction,
        /// the average over the over's frames, and the frames counted. Any
        /// thread.
        /// </summary>
        public (float MaxReductionDb, float AverageReductionDb, long Frames) TakeOverStats()
        {
            long frames = Interlocked.Exchange(ref _overFrames, 0);
            long sum = Interlocked.Exchange(ref _overSumMilliDb, 0);
            int max = Interlocked.Exchange(ref _overMaxCentiDb, 0);
            return (max / 100f, frames > 0 ? (float)(sum / 1000.0 / frames) : 0f, frames);
        }

        /// <summary>
        /// The static curve's reduction, dB (zero or positive), for a level:
        /// the published soft-knee gain computer (see the class remarks).
        /// <paramref name="slope"/> is 1 - 1/ratio.
        /// </summary>
        public static double ReductionFor(double levelDb, double thresholdDb, double slope, double kneeDb)
        {
            double over = levelDb - thresholdDb;
            if (2.0 * over < -kneeDb) return 0.0;
            if (kneeDb > 0.0 && 2.0 * Math.Abs(over) <= kneeDb)
            {
                double into = over + kneeDb / 2.0;
                return slope * into * into / (2.0 * kneeDb);
            }
            return over > 0.0 ? slope * over : 0.0;
        }

        /// <summary>The crossovers as they run at a sample rate: the chosen ones, kept at least <see cref="TxCompressorSettings.MinCrossoverSpacing"/> apart and the higher under 45 percent of the rate.</summary>
        public static (float LowHz, float HighHz) AppliedCrossovers(float lowHz, float highHz, uint sampleRate)
        {
            float top = Math.Min(TxCompressorSettings.MaxCrossoverHighHz, 0.45f * (sampleRate == 0 ? 48000 : sampleRate));
            float high = Math.Clamp(Math.Max(highHz, lowHz * TxCompressorSettings.MinCrossoverSpacing), TxCompressorSettings.MinCrossoverHighHz, top);
            float low = Math.Min(Math.Clamp(lowHz, TxCompressorSettings.MinCrossoverLowHz, TxCompressorSettings.MaxCrossoverLowHz),
                high / TxCompressorSettings.MinCrossoverSpacing);
            return (low, high);
        }

        private static double Coefficient(double ms, uint sampleRate) => 1.0 - Math.Exp(-1.0 / (Math.Max(ms, 0.01) * 0.001 * sampleRate));

        private static void Glide(ref double value, double target, double k)
        {
            double d = target - value;
            if (d == 0.0) return;
            if (Math.Abs(d) < 1e-4) value = target;
            else value += d * k;
        }

        private static double Slope(float ratio) => 1.0 - 1.0 / Math.Max(1.0, ratio);

        /// <summary>
        /// Process one buffer of interleaved stereo in place.
        /// <paramref name="countStats"/> says whether this buffer counts toward
        /// the over's readings (keyed buffers).
        /// </summary>
        public void Process(float[] buffer, int count, uint sampleRate, bool countStats = true)
        {
            var s = _settings;
            if (!s.Enabled)
            {
                if (_wasEnabled)
                {
                    _wasEnabled = false;
                    _reductionDb = 0f;
                    _detectorDb = -120f;
                    for (int b = 0; b < 3; b++) Volatile.Write(ref _bandReductionDb[b], 0f);
                }
                return;
            }
            if (sampleRate == 0) sampleRate = 48000;
            int rotator = s.RotatorStages;
            bool split = s.SplitBand;
            int bands = split ? 3 : 1;
            bool restart = !_wasEnabled || sampleRate != _rate || split != _wasSplit || rotator != _wasRotator;

            for (int b = 0; b < bands; b++)
            {
                // Single-band mode runs band 0 on the stage's own settings.
                float attack = split ? s.Band(b).AttackMs : s.AttackMs;
                float release = split ? s.Band(b).ReleaseMs : s.ReleaseMs;
                _bands[b].Attack = Coefficient(attack, sampleRate);
                _bands[b].Release = Coefficient(release, sampleRate);
            }
            if (split)
            {
                var (low, high) = AppliedCrossovers(s.CrossoverLowHz, s.CrossoverHighHz, sampleRate);
                if (low != _designedLow || high != _designedHigh || sampleRate != _designedRate)
                {
                    _lp1 = BiquadCoefficients.Design(BiquadKind.LowPass, low, 0, Butterworth, sampleRate);
                    _hp1 = BiquadCoefficients.Design(BiquadKind.HighPass, low, 0, Butterworth, sampleRate);
                    _lp2 = BiquadCoefficients.Design(BiquadKind.LowPass, high, 0, Butterworth, sampleRate);
                    _hp2 = BiquadCoefficients.Design(BiquadKind.HighPass, high, 0, Butterworth, sampleRate);
                    _ap2 = PhaseRotator.AllPass(high, Butterworth, sampleRate);
                    _designedLow = low; _designedHigh = high; _designedRate = sampleRate;
                }
            }
            if (restart)
            {
                _rotator.Clear();
                for (int i = 0; i < _split.Length; i++) _split[i].Clear();
                for (int b = 0; b < 3; b++)
                {
                    _bands[b].Peak = 0; _bands[b].Envelope = 0; _bands[b].Reduction = 0;
                    _bands[b].Threshold = TargetThreshold(s, split, b);
                    _bands[b].Slope = TargetSlope(s, split, b);
                    _bands[b].Makeup = split ? s.Band(b).MakeupDb : 0.0;
                }
                _drive = s.DriveDb; _knee = s.KneeDb; _makeup = s.MakeupDb;
                _wasEnabled = true; _wasSplit = split; _wasRotator = rotator; _rate = sampleRate;
            }

            double glide = 1.0 - Math.Exp(-1.0 / (LeverGlideSec * sampleRate));
            double driveTarget = s.DriveDb, kneeTarget = s.KneeDb, makeupTarget = s.MakeupDb;
            double sum = 0, deepest = 0;
            int frames = count / Channels;
            int i2 = 0;
            for (int f = 0; f < frames; f++, i2 += Channels)
            {
                double l = buffer[i2], r = buffer[i2 + 1];
                bool touched = false;
                if (rotator > 0)
                {
                    _rotator.Step(rotator, sampleRate, ref l, ref r);
                    touched = true;
                }
                Glide(ref _drive, driveTarget, glide);
                Glide(ref _knee, kneeTarget, glide);
                Glide(ref _makeup, makeupTarget, glide);
                if (_drive != 0.0)
                {
                    double g = Math.Exp(_drive * Ln10Over20);
                    l *= g; r *= g;
                    touched = true;
                }

                double frameReduction;
                if (!split)
                {
                    ref Band band = ref _bands[0];
                    Glide(ref band.Threshold, s.ThresholdDb, glide);
                    Glide(ref band.Slope, Slope(s.RatioToOne), glide);
                    double reduction = Detect(ref band, Math.Max(Math.Abs(l), Math.Abs(r)), _knee);
                    double gainDb = _makeup - reduction;
                    if (gainDb != 0.0)
                    {
                        double g = Math.Exp(gainDb * Ln10Over20);
                        l *= g; r *= g;
                        touched = true;
                    }
                    frameReduction = reduction;
                }
                else
                {
                    // Left channel's states are 0..8, right's 9..17.
                    double lowL = _split[1].Step(in _lp1, _split[0].Step(in _lp1, l));
                    double restL = _split[3].Step(in _hp1, _split[2].Step(in _hp1, l));
                    double midL = _split[5].Step(in _lp2, _split[4].Step(in _lp2, restL));
                    double highL = _split[7].Step(in _hp2, _split[6].Step(in _hp2, restL));
                    lowL = _split[8].Step(in _ap2, lowL);
                    double lowR = _split[10].Step(in _lp1, _split[9].Step(in _lp1, r));
                    double restR = _split[12].Step(in _hp1, _split[11].Step(in _hp1, r));
                    double midR = _split[14].Step(in _lp2, _split[13].Step(in _lp2, restR));
                    double highR = _split[16].Step(in _hp2, _split[15].Step(in _hp2, restR));
                    lowR = _split[17].Step(in _ap2, lowR);

                    double g0 = BandGain(ref _bands[0], s.Low, Math.Max(Math.Abs(lowL), Math.Abs(lowR)), glide);
                    double g1 = BandGain(ref _bands[1], s.Mid, Math.Max(Math.Abs(midL), Math.Abs(midR)), glide);
                    double g2 = BandGain(ref _bands[2], s.High, Math.Max(Math.Abs(highL), Math.Abs(highR)), glide);
                    double m = _makeup == 0.0 ? 1.0 : Math.Exp(_makeup * Ln10Over20);
                    l = (lowL * g0 + midL * g1 + highL * g2) * m;
                    r = (lowR * g0 + midR * g1 + highR * g2) * m;
                    touched = true;
                    frameReduction = Math.Max(_bands[0].Reduction, Math.Max(_bands[1].Reduction, _bands[2].Reduction));
                }

                if (touched)
                {
                    buffer[i2] = (float)l;
                    buffer[i2 + 1] = (float)r;
                }
                sum += frameReduction;
                if (frameReduction > deepest) deepest = frameReduction;
            }

            _rotator.SettleSections();
            if (split) for (int i = 0; i < _split.Length; i++) _split[i].FlushTiny();
            for (int b = 0; b < 3; b++)
            {
                if (_bands[b].Peak < 1e-25) _bands[b].Peak = 0;
                if (_bands[b].Envelope < 1e-25) _bands[b].Envelope = 0;
                Volatile.Write(ref _bandReductionDb[b], b < bands ? (float)_bands[b].Reduction : 0f);
            }
            _reductionDb = split
                ? (float)Math.Max(_bands[0].Reduction, Math.Max(_bands[1].Reduction, _bands[2].Reduction))
                : (float)_bands[0].Reduction;
            _detectorDb = (float)(20.0 * Math.Log10(_bands[0].Envelope + 1e-12));

            if (countStats && frames > 0)
            {
                Interlocked.Add(ref _overFrames, frames);
                Interlocked.Add(ref _overSumMilliDb, (long)(sum * 1000.0));
                int centi = (int)(deepest * 100.0);
                int was;
                while (centi > (was = Volatile.Read(ref _overMaxCentiDb)))
                    if (Interlocked.CompareExchange(ref _overMaxCentiDb, centi, was) == was) break;
            }
        }

        private static double TargetThreshold(TxCompressorSettings s, bool split, int band) => split ? s.Band(band).ThresholdDb : s.ThresholdDb;

        private static double TargetSlope(TxCompressorSettings s, bool split, int band) => Slope(split ? s.Band(band).RatioToOne : s.RatioToOne);

        /// <summary>The decoupled peak detector and the curve for one band: returns the reduction, dB.</summary>
        private static double Detect(ref Band band, double magnitude, double kneeDb)
        {
            // The peak follower: up at once, down at the release rate.
            double fallen = band.Peak + (magnitude - band.Peak) * band.Release;
            band.Peak = magnitude > fallen ? magnitude : fallen;
            // Then the attack smoothing, still in the linear domain.
            band.Envelope += (band.Peak - band.Envelope) * band.Attack;
            double levelDb = 20.0 * Math.Log10(band.Envelope + 1e-12);
            double reduction = ReductionFor(levelDb, band.Threshold, band.Slope, kneeDb);
            band.Reduction = reduction;
            return reduction;
        }

        /// <summary>One split band's linear gain for this frame: its own curve and makeup.</summary>
        private double BandGain(ref Band band, TxCompressorBand settings, double magnitude, double glide)
        {
            Glide(ref band.Threshold, settings.ThresholdDb, glide);
            Glide(ref band.Slope, Slope(settings.RatioToOne), glide);
            Glide(ref band.Makeup, settings.MakeupDb, glide);
            double gainDb = band.Makeup - Detect(ref band, magnitude, _knee);
            return gainDb == 0.0 ? 1.0 : Math.Exp(gainDb * Ln10Over20);
        }
    }
}
