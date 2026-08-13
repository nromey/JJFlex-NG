using System;
using System.Collections.Generic;

namespace JJPortaudio
{
    /// <summary>
    /// Loudness meter per ITU-R BS.1770-4 / EBU R128 (Audio Arc Engine Track).
    /// K-weighted and gated, computed PC-side on the raw float samples in the
    /// PortAudio input callback — pre-Opus, pristine, and AFTER the TX test
    /// tone's injection point, so it measures whatever is actually being
    /// transmitted, tone or mic.
    ///
    /// Why LUFS and not RMS or peak: the integrated measurement is GATED — 400
    /// ms blocks below an absolute -70 LUFS floor, or more than 10 LU below the
    /// running level, are dropped from the average. That natively discards the
    /// silent gaps between spoken words that make an ungated meter false-alarm
    /// on every breath. And the K-weighting (a ~+4 dB high shelf above ~1.5 kHz
    /// and a ~38 Hz high-pass) tracks PERCEIVED loudness, which is what "how do
    /// I sound" actually means.
    ///
    /// Three figures, per the spec:
    ///   * Momentary  — 400 ms sliding window, updated every 100 ms. Live
    ///     coaching.
    ///   * Short-term — 3 s sliding window, updated every 100 ms. The "how's
    ///     my audio" query and the auto-set target.
    ///   * Integrated — gated average since the last ResetIntegrated(). A
    ///     calibration sample.
    ///
    /// Channel model: the TX input stream is stereo interleaved with the mono
    /// mic duplicated onto both channels. BS.1770 sums channel powers (weight
    /// 1.0 for L and R), and for identical L/R content that lands exactly on
    /// the dBFS-style number the radio's meters use: a -10 dBFS sine on both
    /// channels reads -10.0 LUFS (a single-channel-only signal reads 3 dB
    /// lower, the spec's own stated behaviour). So these figures are directly
    /// comparable to the SC_MIC anchor measured at the radio.
    ///
    /// Thread model: Process runs on the PortAudio callback thread; the
    /// property getters run on UI threads. Momentary and short-term are
    /// published through volatile floats — no locks on the hot path. The
    /// integrated block history takes a tiny lock only when a 400 ms block
    /// completes (10x/sec) and when a reader snapshots it; the callback path
    /// already allocates (the Opus encode), so this is well within budget.
    ///
    /// No dependencies beyond System, on purpose: the numerical harness links
    /// this exact source file and drives it with synthetic signals.
    /// </summary>
    public class LufsMeter
    {
        /// <summary>Reported when there is no signal or no data yet. Matches the
        /// -150 dBFS floor the FlexBase transmit meters already use.</summary>
        public const float Floor = -150f;

        private const int Channels = 2;
        // 100 ms sub-blocks; momentary = 4 (400 ms), short-term = 30 (3 s).
        private const int MomentarySubBlocks = 4;
        private const int ShortTermSubBlocks = 30;
        // A gap in Process calls longer than this means the input stream was
        // stopped (unkey) and restarted: stale window/filter state is dropped.
        private const long StreamGapMs = 500;
        // BS.1770-4 gating: absolute threshold -70 LUFS; relative threshold
        // 10 LU below the absolute-gated mean.
        private const double AbsoluteGateLufs = -70.0;
        private const double RelativeGateLu = 10.0;
        // BS.1770 offset: loudness = -0.691 + 10 log10(sum of channel powers).
        private const double LoudnessOffset = -0.691;
        // Cap on stored 400 ms block powers (10/sec => 2 hours). On overflow
        // the oldest half is dropped; a calibration sample is seconds long, so
        // this exists only to bound memory on a forgotten meter.
        private const int MaxBlocks = 72000;
        // --- Noise-floor estimate (Levels Track, 2026-08-12) -----------------
        // The blocks the gate THROWS AWAY are the quiet stretches between
        // words, and their level is the thing nobody was reading. A low
        // percentile of the block distribution is the standard robust estimate
        // of a noise floor: it lands in the quiet population without being the
        // single quietest block (which is noise on the noise). 10% of blocks
        // sit at or below it.
        private const double NoiseFloorPercentile = 0.10;
        // Blocks needed before the floor estimate means anything. Blocks land
        // one per 100 ms once the first 400 ms has filled, so 30 blocks is
        // ~3.3 s of transmit — long enough to contain several word gaps.
        // Below this the profile reports invalid rather than guessing, because
        // a two-word radio check genuinely does not carry the evidence.
        public const int MinProfileBlocks = 30;

        // --- K-weighting filter (two biquads per channel, double precision) --
        private double _fs;                 // sample rate the coefficients are for
        private double _b0s1, _b1s1, _b2s1, _a1s1, _a2s1; // stage 1: high shelf
        private double _b0s2, _b1s2, _b2s2, _a1s2, _a2s2; // stage 2: high-pass
        // Direct Form I state, [channel]: stage 1 then stage 2.
        private readonly double[] _x1s1 = new double[Channels];
        private readonly double[] _x2s1 = new double[Channels];
        private readonly double[] _y1s1 = new double[Channels];
        private readonly double[] _y2s1 = new double[Channels];
        private readonly double[] _x1s2 = new double[Channels];
        private readonly double[] _x2s2 = new double[Channels];
        private readonly double[] _y1s2 = new double[Channels];
        private readonly double[] _y2s2 = new double[Channels];

        // --- Sub-block accumulation (audio thread only) ----------------------
        private int _subBlockFrames;        // frames per 100 ms sub-block
        private int _framesIntoSubBlock;
        private double _sumSquares;         // K-weighted, both channels
        // Ring of the last 30 sub-block powers (mean square, channels summed).
        private readonly double[] _subRing = new double[ShortTermSubBlocks];
        private int _subRingNext;
        private int _subRingCount;

        // --- Published outputs ----------------------------------------------
        private volatile float _momentary = Floor;
        private volatile float _shortTerm = Floor;
        private long _lastProcessMs;        // Environment.TickCount64 (64-bit
                                            // reads/writes are atomic on x64;
                                            // staleness here is harmless anyway)

        // --- Integrated block history (guarded by _blockLock) ----------------
        private readonly object _blockLock = new object();
        private readonly List<double> _blockPowers = new List<double>();
        // --- Profile cache (guarded by _profileLock; always taken BEFORE
        // _blockLock, never the other way round) -----------------------------
        private readonly object _profileLock = new object();
        private LoudnessProfile _profileCache;
        private int _profileCacheBlocks = -1;   // -1 = nothing cached yet
        // After ResetIntegrated, this many FRESH sub-blocks must complete
        // before 400 ms blocks resume — otherwise the first blocks of a new
        // calibration sample would blend audio from before the reset that is
        // still sitting in the sub-block ring. Caught by the numerical
        // harness (a -20 dBFS sample right after a -10 dBFS run read -18.96).
        private int _freshSubBlocksNeeded;

        /// <summary>
        /// Momentary loudness, LUFS: a 400 ms window updated every 100 ms.
        /// <see cref="Floor"/> when silent or when no audio has flowed.
        /// </summary>
        public float MomentaryLufs => _momentary;

        /// <summary>
        /// Short-term loudness, LUFS: a 3 s window updated every 100 ms.
        /// <see cref="Floor"/> when silent or when no audio has flowed.
        /// </summary>
        public float ShortTermLufs => _shortTerm;

        /// <summary>
        /// Integrated loudness, LUFS, gated per BS.1770-4 (absolute -70 LUFS,
        /// then relative -10 LU), accumulated since the last
        /// <see cref="ResetIntegrated"/>. Computed on demand — cheap enough for
        /// a readout, not meant to be polled from a tight loop.
        /// </summary>
        public float IntegratedLufs => GatedLoudness(SnapshotBlocks());

        /// <summary>The BS.1770-4 two-stage gated mean of a block-power set.</summary>
        private static float GatedLoudness(double[] blocks)
        {
            if (blocks.Length == 0) return Floor;

            // Stage 1: absolute gate.
            double absGatePower = PowerFromLoudness(AbsoluteGateLufs);
            double sum = 0.0; int n = 0;
            foreach (double p in blocks)
            {
                if (p > absGatePower) { sum += p; n++; }
            }
            if (n == 0) return Floor;

            // Stage 2: relative gate, 10 LU below the absolute-gated mean.
            double relGatePower = PowerFromLoudness(
                LoudnessFromPower(sum / n) - RelativeGateLu);
            sum = 0.0; n = 0;
            foreach (double p in blocks)
            {
                if (p > absGatePower && p > relGatePower) { sum += p; n++; }
            }
            if (n == 0) return Floor;
            return ClampToFloor(LoudnessFromPower(sum / n));
        }

        /// <summary>
        /// The same accumulation with NO gating — the plain mean of every 400 ms
        /// block. Diagnostic contrast for the geek readout ("what would a naive
        /// meter say"), and the harness's proof that the gate earns its keep.
        /// </summary>
        public float IntegratedUngatedLufs
        {
            get
            {
                double[] blocks = SnapshotBlocks();
                if (blocks.Length == 0) return Floor;
                double sum = 0.0;
                foreach (double p in blocks) sum += p;
                return ClampToFloor(LoudnessFromPower(sum / blocks.Length));
            }
        }

        /// <summary>
        /// Number of 400 ms block powers held since the last
        /// <see cref="ResetIntegrated"/>. Unlike <see cref="HasRecentData"/>
        /// this survives the stream stopping, so it is the honest "is there a
        /// finished sample to report on" test after an unkey.
        /// </summary>
        public int IntegratedBlockCount
        {
            get { lock (_blockLock) return _blockPowers.Count; }
        }

        /// <summary>
        /// The whole-sample loudness picture: how loud the speech is, how loud
        /// whatever runs underneath it is, and the distance between the two.
        /// <see cref="SpeechToNoiseLu"/> is the interesting figure — a level
        /// that reads healthy with only a few LU of daylight under it is a
        /// microphone hearing a room rather than a person.
        /// </summary>
        public readonly struct LoudnessProfile
        {
            internal LoudnessProfile(float speech, float noiseFloor, int blocks, bool valid)
            {
                SpeechLufs = speech;
                NoiseFloorLufs = noiseFloor;
                BlockCount = blocks;
                IsValid = valid;
            }

            /// <summary>Gated loudness — the same figure as <see cref="IntegratedLufs"/>.</summary>
            public float SpeechLufs { get; }

            /// <summary>Loudness of the quiet stretches: the low percentile of
            /// the 400 ms block distribution, i.e. the level the gate discards.</summary>
            public float NoiseFloorLufs { get; }

            /// <summary>How many 400 ms blocks the estimate rests on.</summary>
            public int BlockCount { get; }

            /// <summary>False when the sample is too short or held no signal at
            /// all. Consumers must not report figures from an invalid profile.</summary>
            public bool IsValid { get; }

            /// <summary>Speech level minus noise floor, in LU (1 LU = 1 dB).
            /// Large is good: the voice stands well clear of the room.</summary>
            public float SpeechToNoiseLu => SpeechLufs - NoiseFloorLufs;
        }

        /// <summary>
        /// Speech level, noise floor, and the gap between them, computed from
        /// one snapshot of the block history so the three figures can never
        /// disagree with each other. See <see cref="LoudnessProfile"/>.
        ///
        /// Cached against the block count, which is the only thing the answer
        /// depends on. The reading fields poll twice a second and each
        /// computation copies and sorts the whole history — 9000 doubles by the
        /// end of a long transmit. While receiving, the count is frozen and
        /// every poll after the first is free; while transmitting it recomputes,
        /// which is exactly when the figures are moving and being watched.
        ///
        /// Deliberately NOT time-based. An age guard would let a caller that
        /// feeds audio faster than real time — the numerical harness does
        /// exactly this — read a profile from before the audio it just fed.
        /// A cache that can hand back an answer to the wrong question is worse
        /// than no cache.
        /// </summary>
        public LoudnessProfile Profile
        {
            get
            {
                lock (_profileLock)
                {
                    int count = IntegratedBlockCount;
                    if (_profileCacheBlocks == count) return _profileCache;

                    _profileCache = ComputeProfile();
                    _profileCacheBlocks = count;
                    return _profileCache;
                }
            }
        }

        private LoudnessProfile ComputeProfile()
        {
            double[] blocks = SnapshotBlocks();
            if (blocks.Length < MinProfileBlocks)
                return new LoudnessProfile(Floor, Floor, blocks.Length, false);

            float speech = GatedLoudness(blocks);
            // Sorting is safe: SnapshotBlocks hands back a private copy.
            Array.Sort(blocks);
            int idx = (int)Math.Round(NoiseFloorPercentile * (blocks.Length - 1));
            float noise = ClampToFloor(LoudnessFromPower(blocks[idx]));
            return new LoudnessProfile(speech, noise, blocks.Length, speech > Floor);
        }

        /// <summary>
        /// True while samples are actually flowing (a Process call within the
        /// last half second). The TX input stream only runs while transmitting
        /// PC audio, so this is the honest "is this meter measuring anything"
        /// signal — when false, consumers must fall back to the radio-side
        /// SC_MIC/ALC meters rather than reporting a stale number.
        /// </summary>
        public bool HasRecentData =>
            Environment.TickCount64 - _lastProcessMs < StreamGapMs;

        /// <summary>
        /// Forget the integrated accumulation (momentary and short-term are
        /// unaffected). Call at the start of a calibration sample.
        /// </summary>
        public void ResetIntegrated()
        {
            System.Threading.Interlocked.Exchange(
                ref _freshSubBlocksNeeded, MomentarySubBlocks);
            lock (_blockLock) _blockPowers.Clear();
            // Reset lands the count back on 0, which a previous 0-block cache
            // would match — so invalidate explicitly rather than relying on the
            // count to differ.
            lock (_profileLock) _profileCacheBlocks = -1;
        }

        /// <summary>
        /// Process one buffer of interleaved stereo float samples. Called from
        /// the PortAudio input callback AFTER the test-tone injection point.
        /// </summary>
        /// <param name="buffer">interleaved stereo samples</param>
        /// <param name="count">number of floats to read (frames * 2)</param>
        /// <param name="sampleRate">stream sample rate in Hz</param>
        public void Process(float[] buffer, int count, uint sampleRate)
        {
            if (sampleRate == 0) sampleRate = 48000;

            long now = Environment.TickCount64;
            long last = _lastProcessMs;
            _lastProcessMs = now;

            // Sample-rate change (device fell back to its default rate) or a
            // stream stop/restart: the windows and filter state belong to dead
            // audio. Drop them. The integrated history survives a re-key — the
            // gate handles the gap, which is the entire point of gating.
            if (sampleRate != _fs || (last != 0 && now - last > StreamGapMs))
            {
                DesignFilters(sampleRate);
                ResetWindows();
            }

            int frames = count / Channels;
            int i = 0;
            for (int f = 0; f < frames; f++)
            {
                // K-weight both channels: stage 1 shelf, then stage 2 high-pass.
                for (int ch = 0; ch < Channels; ch++)
                {
                    double x = buffer[i + ch];
                    double y1 = _b0s1 * x + _b1s1 * _x1s1[ch] + _b2s1 * _x2s1[ch]
                              - _a1s1 * _y1s1[ch] - _a2s1 * _y2s1[ch];
                    _x2s1[ch] = _x1s1[ch]; _x1s1[ch] = x;
                    _y2s1[ch] = _y1s1[ch]; _y1s1[ch] = y1;

                    double y2 = _b0s2 * y1 + _b1s2 * _x1s2[ch] + _b2s2 * _x2s2[ch]
                              - _a1s2 * _y1s2[ch] - _a2s2 * _y2s2[ch];
                    _x2s2[ch] = _x1s2[ch]; _x1s2[ch] = y1;
                    _y2s2[ch] = _y1s2[ch]; _y1s2[ch] = y2;

                    _sumSquares += y2 * y2;
                }
                i += Channels;

                if (++_framesIntoSubBlock >= _subBlockFrames)
                {
                    CompleteSubBlock();
                }
            }
        }

        /// <summary>
        /// A 100 ms sub-block just filled: push its power into the ring,
        /// publish momentary (last 4) and short-term (last 30), and when a full
        /// 400 ms exists, append a 400 ms block power to the integrated history
        /// (one per 100 ms = the spec's 75% overlap).
        /// </summary>
        private void CompleteSubBlock()
        {
            // Power = mean square per frame with channels summed, i.e.
            // sum(G_i * z_i) in the spec's terms with G = 1 for L and R.
            double power = _sumSquares / _subBlockFrames;
            _sumSquares = 0.0;
            _framesIntoSubBlock = 0;

            _subRing[_subRingNext] = power;
            _subRingNext = (_subRingNext + 1) % ShortTermSubBlocks;
            if (_subRingCount < ShortTermSubBlocks) _subRingCount++;

            _momentary = ClampToFloor(LoudnessFromPower(
                MeanOfLast(Math.Min(MomentarySubBlocks, _subRingCount))));
            _shortTerm = ClampToFloor(LoudnessFromPower(
                MeanOfLast(_subRingCount)));

            // Hold off appending integrated blocks until the ring holds only
            // audio from after the last ResetIntegrated.
            if (_freshSubBlocksNeeded > 0)
            {
                System.Threading.Interlocked.Decrement(ref _freshSubBlocksNeeded);
                return;
            }

            if (_subRingCount >= MomentarySubBlocks)
            {
                double blockPower = MeanOfLast(MomentarySubBlocks);
                lock (_blockLock)
                {
                    if (_blockPowers.Count >= MaxBlocks)
                    {
                        _blockPowers.RemoveRange(0, _blockPowers.Count / 2);
                    }
                    _blockPowers.Add(blockPower);
                }
            }
        }

        /// <summary>Mean of the most recent n sub-block powers. Audio thread only.</summary>
        private double MeanOfLast(int n)
        {
            if (n <= 0) return 0.0;
            double sum = 0.0;
            int idx = _subRingNext;
            for (int k = 0; k < n; k++)
            {
                idx = (idx + ShortTermSubBlocks - 1) % ShortTermSubBlocks;
                sum += _subRing[idx];
            }
            return sum / n;
        }

        private double[] SnapshotBlocks()
        {
            lock (_blockLock) return _blockPowers.ToArray();
        }

        private void ResetWindows()
        {
            Array.Clear(_subRing, 0, _subRing.Length);
            _subRingNext = 0;
            _subRingCount = 0;
            _framesIntoSubBlock = 0;
            _sumSquares = 0.0;
            Array.Clear(_x1s1); Array.Clear(_x2s1);
            Array.Clear(_y1s1); Array.Clear(_y2s1);
            Array.Clear(_x1s2); Array.Clear(_x2s2);
            Array.Clear(_y1s2); Array.Clear(_y2s2);
            _momentary = Floor;
            _shortTerm = Floor;
        }

        private static double LoudnessFromPower(double power)
        {
            if (power <= 0.0) return double.NegativeInfinity;
            return LoudnessOffset + 10.0 * Math.Log10(power);
        }

        private static double PowerFromLoudness(double loudness)
        {
            return Math.Pow(10.0, (loudness - LoudnessOffset) / 10.0);
        }

        private static float ClampToFloor(double loudness)
        {
            if (double.IsNaN(loudness) || loudness < Floor) return Floor;
            return (float)loudness;
        }

        /// <summary>
        /// K-weighting coefficients for an arbitrary sample rate, from the
        /// analog prototype behind ITU-R BS.1770 (the rederivation published by
        /// Brecht De Man, used by pyloudnorm and friends): stage 1 is a high
        /// shelf at 1681.97 Hz, +3.99984 dB, Q 0.70718; stage 2 is a high-pass
        /// at 38.1355 Hz, Q 0.50033. At 48 kHz these reproduce the ITU table
        /// coefficients to better than 1e-6 — the harness asserts exactly that.
        /// The TX stream normally runs at 48 kHz, but the device open path can
        /// fall back to the device's default rate, so this must not assume.
        /// </summary>
        private void DesignFilters(double fs)
        {
            _fs = fs;

            // Stage 1: high shelf.
            double f0 = 1681.974450955533;
            double gainDb = 3.999843853973347;
            double q = 0.7071752369554196;
            double k = Math.Tan(Math.PI * f0 / fs);
            double vh = Math.Pow(10.0, gainDb / 20.0);
            double vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1.0 + k / q + k * k;
            _b0s1 = (vh + vb * k / q + k * k) / a0;
            _b1s1 = 2.0 * (k * k - vh) / a0;
            _b2s1 = (vh - vb * k / q + k * k) / a0;
            _a1s1 = 2.0 * (k * k - 1.0) / a0;
            _a2s1 = (1.0 - k / q + k * k) / a0;

            // Stage 2: high-pass. The spec's numerator really is (1, -2, 1)
            // un-normalized — passband gain is unity by construction.
            f0 = 38.13547087602444;
            q = 0.5003270373238773;
            k = Math.Tan(Math.PI * f0 / fs);
            double denom = 1.0 + k / q + k * k;
            _b0s2 = 1.0;
            _b1s2 = -2.0;
            _b2s2 = 1.0;
            _a1s2 = 2.0 * (k * k - 1.0) / denom;
            _a2s2 = (1.0 - k / q + k * k) / denom;

            _subBlockFrames = Math.Max(1, (int)Math.Round(fs / 10.0));
        }

        /// <summary>
        /// The designed coefficients, exposed for the numerical harness to
        /// check against the ITU-published 48 kHz table. Order: stage 1
        /// b0 b1 b2 a1 a2, then stage 2 b0 b1 b2 a1 a2. Computed on a scratch
        /// instance — never disturbs a live meter.
        /// </summary>
        public static double[] CoefficientsForTest(double fs)
        {
            var m = new LufsMeter();
            m.DesignFilters(fs);
            return new[]
            {
                m._b0s1, m._b1s1, m._b2s1, m._a1s1, m._a2s1,
                m._b0s2, m._b1s2, m._b2s2, m._a1s2, m._a2s2
            };
        }
    }
}
