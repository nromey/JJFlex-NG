using System;
using System.Threading;

namespace JJPortaudio
{
    /// <summary>
    /// The transmit de-esser (Sprint 49 Track I, item 4). WDSP has none, so
    /// this is ours: a split-band de-esser that turns down only the sibilant
    /// band, and only while sibilance is there.
    /// </summary>
    /// <remarks>
    /// <para><b>How it works.</b> A band-pass at <see cref="FrequencyHz"/>
    /// splits the voice into the band and the rest (the input minus the band).
    /// A detector follows the band's level; while it is above the
    /// sensitivity (<see cref="ThresholdDb"/>) the band is turned down,
    /// by at most <see cref="AmountDb"/>. The output is the rest plus the band
    /// at that gain, written as input minus (1 - gain) times the band.</para>
    /// <para><b>The split is complementary, so idle is exact.</b> With no
    /// reduction the gain is exactly 1 and the output IS the input, sample for
    /// sample. A crossover pair (two filters whose outputs are summed) would
    /// sum to an all-pass, not to the input, and fail that.</para>
    /// <para><b>The band is tied to the radio's transmit filter,
    /// radio-neutrally.</b> On SSB the transmit filter throws away most
    /// classic sibilance above about 3 kHz, so a band set above it would do
    /// nothing on the air. The policy layer tells this stage the passband's
    /// high edge (<see cref="PassbandHighEdgeHz"/>; on a Flex, from the
    /// transmit filter); the operator's chosen frequency stays the intent and
    /// only what is APPLIED is clamped (<see cref="AppliedFrequencyHz"/>,
    /// <see cref="Clamped"/>). This class knows nothing of any radio.</para>
    /// <para><b>Design reference, ideas only, no code taken.</b> The split-band
    /// shape (a band-pass side chain, an envelope follower, a cut capped at an
    /// amount, a tight attack) and the ranges for frequency, Q and amount
    /// were read from a GPL program's design for comparison; nothing was
    /// copied. Zero latency: the band-pass is a recursive section run in place.</para>
    /// <para>No lock and no allocation on the audio thread. System-only.</para>
    /// </remarks>
    public sealed class DeEsser
    {
        private const int Channels = 2;

        /// <summary>The band's Q: about an octave and a half wide, the width a sibilant band needs.</summary>
        public const double BandQ = 1.0;

        /// <summary>Defaults for a newly made de-esser (Track I's judgement, in its report).</summary>
        public const float DefaultFrequencyHz = 5000f, DefaultAmountDb = 6f, DefaultThresholdDb = -30f;

        /// <summary>How close to the passband's high edge the band may sit: its centre at most this fraction of the edge.</summary>
        public const float PassbandFraction = 0.85f;

        // The compression of the band above the sensitivity: 4:1, so reduction
        // grows by three quarters of a dB for each dB above it.
        private const double Slope = 0.75;
        private const double AttackSec = 0.001, ReleaseSec = 0.060;

        private volatile bool _enabled;
        private volatile float _frequencyHz = DefaultFrequencyHz;
        private volatile float _amountDb = DefaultAmountDb;
        private volatile float _thresholdDb = DefaultThresholdDb;
        private volatile float _passbandHighEdgeHz;

        private BiquadCoefficients _band = BiquadCoefficients.Identity;
        private BiquadState _bandL, _bandR;
        private float _designedHz = float.NaN;
        private uint _designedRate;
        private bool _wasEnabled;
        private double _env;
        private volatile float _appliedHz;
        private volatile float _reductionDb;

        // Per-over statistics, accumulated on the audio thread as plain numbers.
        private long _reducedFrames;
        private int _maxReductionCentiDb;

        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>The band's centre the operator chose, Hz (1000 to 12000). The intent; see <see cref="AppliedFrequencyHz"/>.</summary>
        public float FrequencyHz
        {
            get => _frequencyHz;
            set => _frequencyHz = float.IsFinite(value) ? Math.Clamp(value, 1000f, 12000f) : DefaultFrequencyHz;
        }

        /// <summary>The most the band may be turned down, dB (1 to 24).</summary>
        public float AmountDb
        {
            get => _amountDb;
            set => _amountDb = float.IsFinite(value) ? Math.Clamp(value, 1f, 24f) : DefaultAmountDb;
        }

        /// <summary>The sensitivity: the band level above which it is turned down, dB on the detector's scale (-60 to -10). Lower is more sensitive.</summary>
        public float ThresholdDb
        {
            get => _thresholdDb;
            set => _thresholdDb = float.IsFinite(value) ? Math.Clamp(value, -60f, -10f) : DefaultThresholdDb;
        }

        /// <summary>The transmit passband's high edge, Hz, set by the policy layer; 0 when unknown (nothing is clamped).</summary>
        public float PassbandHighEdgeHz
        {
            get => _passbandHighEdgeHz;
            set => _passbandHighEdgeHz = float.IsFinite(value) && value > 0 ? value : 0f;
        }

        /// <summary>The band's centre as applied: the chosen frequency, clamped below the passband's high edge.</summary>
        public float AppliedFrequencyHz => ApplyClamp(_frequencyHz, _passbandHighEdgeHz);

        /// <summary>Whether the chosen band is being clamped to the passband.</summary>
        public bool Clamped => AppliedFrequencyHz < _frequencyHz;

        /// <summary>The clamp, as a rule: the chosen centre, at most <see cref="PassbandFraction"/> of the edge.</summary>
        public static float ApplyClamp(float chosenHz, float edgeHz) =>
            edgeHz > 0 ? Math.Min(chosenHz, Math.Max(500f, edgeHz * PassbandFraction)) : chosenHz;

        /// <summary>The reduction applied at the end of the last buffer, dB.</summary>
        public float ReductionDb => _reductionDb;

        /// <summary>Added delay, frames: always zero.</summary>
        public int LatencyFrames => 0;

        /// <summary>Statistics since the last take, then zeroed: frames reduced by more than 1 dB, and the deepest reduction, dB. Any thread.</summary>
        public (long ReducedFrames, float MaxReductionDb) TakeOverStats()
        {
            long frames = Interlocked.Exchange(ref _reducedFrames, 0);
            int centi = Interlocked.Exchange(ref _maxReductionCentiDb, 0);
            return (frames, centi / 100f);
        }

        /// <summary>Process one buffer of interleaved stereo in place. <paramref name="countStats"/> says whether this buffer counts toward the over's statistics.</summary>
        public void Process(float[] buffer, int count, uint sampleRate, bool countStats = true)
        {
            if (!_enabled)
            {
                _wasEnabled = false;
                _reductionDb = 0f;
                return;
            }
            if (sampleRate == 0) sampleRate = 48000;
            float applied = AppliedFrequencyHz;
            if (!_wasEnabled)
            {
                _bandL.Clear(); _bandR.Clear(); _env = 0; _wasEnabled = true;
            }
            if (applied != _designedHz || sampleRate != _designedRate)
            {
                _band = BiquadCoefficients.Design(BiquadKind.BandPass, applied, 0, BandQ, sampleRate);
                _designedHz = applied; _designedRate = sampleRate;
                _appliedHz = applied;
            }
            double threshold = _thresholdDb;
            double amount = _amountDb;
            double att = 1.0 - Math.Exp(-1.0 / (AttackSec * sampleRate));
            double rel = 1.0 - Math.Exp(-1.0 / (ReleaseSec * sampleRate));

            int frames = count / Channels;
            long reduced = 0;
            double deepest = 0;
            double red = 0;
            int i = 0;
            for (int f = 0; f < frames; f++)
            {
                float xl = buffer[i], xr = buffer[i + 1];
                double bl = _bandL.Step(in _band, xl);
                double br = _bandR.Step(in _band, xr);
                double p = bl * bl + br * br;
                _env += (p - _env) * (p > _env ? att : rel);
                double levelDb = 10.0 * Math.Log10(_env + 1e-12);
                red = levelDb > threshold ? Math.Min(amount, (levelDb - threshold) * Slope) : 0.0;
                if (red > 0.0)
                {
                    float k = (float)(1.0 - Math.Exp(-red * (Math.Log(10.0) / 20.0)));
                    buffer[i] = xl - k * (float)bl;
                    buffer[i + 1] = xr - k * (float)br;
                    if (red > 1.0) reduced++;
                    if (red > deepest) deepest = red;
                }
                // else: the output is the input, exactly.
                i += Channels;
            }
            _bandL.FlushTiny(); _bandR.FlushTiny();
            _reductionDb = (float)red;
            if (countStats)
            {
                if (reduced > 0) Interlocked.Add(ref _reducedFrames, reduced);
                int centi = (int)(deepest * 100);
                int was;
                while (centi > (was = Volatile.Read(ref _maxReductionCentiDb)))
                    if (Interlocked.CompareExchange(ref _maxReductionCentiDb, centi, was) == was) break;
            }
        }
    }
}
