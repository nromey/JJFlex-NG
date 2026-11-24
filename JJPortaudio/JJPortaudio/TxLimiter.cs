using System;
using System.Threading;

namespace JJPortaudio
{
    /// <summary>
    /// Every setting of the transmit limiter (Sprint 49 Track CL, #1278).
    /// Immutable; change it with a <c>with</c> expression. Every value is
    /// clamped to its range as it is set.
    /// </summary>
    public sealed record TxLimiterSettings
    {
        public const float MinCeilingDb = -12f, MaxCeilingDb = 0f, DefaultCeilingDb = 0f;
        public const float MinReleaseMs = 10f, MaxReleaseMs = 1000f, DefaultReleaseMs = 100f;
        public const float MinLookAheadMs = 0f, MaxLookAheadMs = 5f, DefaultLookAheadMs = 0f;

        private readonly float _ceiling = DefaultCeilingDb, _release = DefaultReleaseMs, _lookAhead = DefaultLookAheadMs;

        /// <summary>The settings a new limiter holds: off, at full scale, with no look-ahead.</summary>
        public static TxLimiterSettings Default { get; } = new();

        public bool Enabled { get; init; }

        /// <summary>
        /// The ceiling, dBFS of the sample peak: no sample leaves above it.
        /// The default, 0 dBFS, is full scale, so the limiter only ever acts
        /// on a sample that would have gone past full scale, and cannot lower
        /// the peak drive of audio that was within it (#1278).
        /// </summary>
        public float CeilingDb { get => _ceiling; init => _ceiling = float.IsFinite(value) ? Math.Clamp(value, MinCeilingDb, MaxCeilingDb) : DefaultCeilingDb; }

        /// <summary>How fast the gain comes back after an over, ms.</summary>
        public float ReleaseMs { get => _release; init => _release = float.IsFinite(value) ? Math.Clamp(value, MinReleaseMs, MaxReleaseMs) : DefaultReleaseMs; }

        /// <summary>Look-ahead, ms (0 to 5; Advanced): the gain starts down before the over arrives, for cleaner limiting at the price of this much delay. Its length is a switch.</summary>
        public float LookAheadMs { get => _lookAhead; init => _lookAhead = float.IsFinite(value) ? Math.Clamp(value, MinLookAheadMs, MaxLookAheadMs) : DefaultLookAheadMs; }
    }

    /// <summary>
    /// The transmit limiter (Sprint 49 Track CL, #1278): the last stage of the
    /// PC transmit chain, the chain's guarantee on peaks. No sample leaves
    /// above the ceiling, and audio that never exceeds the ceiling passes bit
    /// for bit, so at its default ceiling of full scale it cannot lower peak
    /// drive.
    /// </summary>
    /// <remarks>
    /// <para><b>How it works.</b> For each frame the gain that would bring its
    /// larger channel to the ceiling is worked out (one when it is within the
    /// ceiling). The gain applied falls to that at once and comes back by the
    /// release time constant. With look-ahead of D frames the audio is delayed
    /// D frames; the applied gain is the smallest needed gain over the D + 1
    /// frames up to now, released, then averaged over D + 1 frames. Every
    /// frame's own needed gain is inside every term of the average that
    /// reaches it, so the gain on a frame is never above what that frame
    /// needs, and the gain glides down over the look-ahead before an over
    /// instead of stepping at it. With D = 0 the same arithmetic is the
    /// zero-delay limiter: an instant gain step at the over, a smooth
    /// release.</para>
    /// <para><b>The guarantee is on sample peaks.</b> A reconstruction filter
    /// or a resampler later in the path can still overshoot between samples;
    /// a true-peak detector is not built (#1278's brief: propose one only if
    /// the bench shows overs).</para>
    /// <para><b>It counts.</b> Per over, on keyed buffers: the samples it
    /// caught (over the ceiling at its input), the samples that left over full
    /// scale (zero while it is on; counted while it is off too, so a bench
    /// can see what an unlimited chain sends), and the deepest reduction.</para>
    /// <para>No lock and no allocation on the audio thread: the delay line,
    /// the sliding-minimum queue and the averaging ring are made once, for the
    /// longest look-ahead (5 ms at 192 kHz). System-only.</para>
    /// </remarks>
    public sealed class TxLimiter
    {
        private const int Channels = 2;
        private const int MaxLookAheadFrames = 960; // 5 ms at 192 kHz
        private const int QueueSize = 1024;         // a power of two above the longest window
        private const int QueueMask = QueueSize - 1;

        private volatile TxLimiterSettings _settings = TxLimiterSettings.Default;

        /// <summary>The settings in force from the next buffer. Any thread: one reference.</summary>
        public TxLimiterSettings Settings
        {
            get => _settings;
            set => _settings = value ?? TxLimiterSettings.Default;
        }

        private readonly float[] _delay = new float[(MaxLookAheadFrames + 1) * Channels];
        private readonly double[] _queueValue = new double[QueueSize];
        private readonly long[] _queueFrame = new long[QueueSize];
        private readonly double[] _box = new double[MaxLookAheadFrames + 1];
        private int _queueHead, _queueTail, _delayAt, _boxAt, _boxBelowUnity;
        private double _boxSum, _released = 1.0;
        private long _frame;
        private int _lookAhead = -1;
        private uint _rate;
        private bool _wasEnabled;
        private float _ceilingDb = float.NaN, _ceiling = 1f;

        private volatile int _appliedLookAhead;
        private volatile float _reductionDb;
        private long _overCaught, _overOutputOver, _overLimitedFrames;
        private int _overMaxCentiDb;

        /// <summary>The look-ahead for a setting at a rate, frames.</summary>
        public static int LookAheadFramesFor(float ms, uint sampleRate) =>
            float.IsFinite(ms) && ms > 0
                ? Math.Min(MaxLookAheadFrames, (int)Math.Round(Math.Clamp(ms, 0f, TxLimiterSettings.MaxLookAheadMs) * 0.001 * (sampleRate == 0 ? 48000 : sampleRate)))
                : 0;

        /// <summary>The delay this stage added on the last buffer, frames: its look-ahead while on, zero while off.</summary>
        public int AppliedLookAheadFrames => _appliedLookAhead;

        /// <summary>The gain reduction at the end of the last buffer, dB (positive). Zero while off or idle.</summary>
        public float ReductionDb => _reductionDb;

        /// <summary>
        /// One over's counts, taken and zeroed: samples caught (over the
        /// ceiling at the input), samples that left over full scale (zero
        /// while on), frames on which the gain was reduced, and the deepest
        /// reduction, dB. Any thread.
        /// </summary>
        public (long Caught, long OutputOverFullScale, long LimitedFrames, float MaxReductionDb) TakeOverStats()
        {
            long caught = Interlocked.Exchange(ref _overCaught, 0);
            long over = Interlocked.Exchange(ref _overOutputOver, 0);
            long limited = Interlocked.Exchange(ref _overLimitedFrames, 0);
            int max = Interlocked.Exchange(ref _overMaxCentiDb, 0);
            return (caught, over, limited, max / 100f);
        }

        private void Restart(int lookAhead, uint sampleRate)
        {
            Array.Clear(_delay, 0, _delay.Length);
            int window = lookAhead + 1;
            for (int i = 0; i < window; i++) _box[i] = 1.0;
            _boxSum = window; _boxAt = 0; _boxBelowUnity = 0;
            _queueHead = _queueTail = 0;
            _delayAt = 0;
            _released = 1.0;
            _frame = 0;
            _lookAhead = lookAhead;
            _rate = sampleRate;
        }

        /// <summary>
        /// Process one buffer of interleaved stereo in place.
        /// <paramref name="countStats"/> says whether this buffer counts toward
        /// the over's counts (keyed buffers).
        /// </summary>
        public void Process(float[] buffer, int count, uint sampleRate, bool countStats = true)
        {
            var s = _settings;
            if (!s.Enabled)
            {
                if (_wasEnabled)
                {
                    _wasEnabled = false;
                    _appliedLookAhead = 0;
                    _reductionDb = 0f;
                }
                // Off, it touches nothing; it still says what left over full
                // scale, so the bench can read an unlimited chain.
                if (countStats)
                {
                    long over = 0;
                    for (int i = 0; i < count; i++)
                        if (Math.Abs(buffer[i]) > 1f) over++;
                    if (over > 0) Interlocked.Add(ref _overOutputOver, over);
                }
                return;
            }
            if (sampleRate == 0) sampleRate = 48000;
            int lookAhead = LookAheadFramesFor(s.LookAheadMs, sampleRate);
            if (!_wasEnabled || lookAhead != _lookAhead || sampleRate != _rate)
            {
                Restart(lookAhead, sampleRate);
                _wasEnabled = true;
            }
            if (s.CeilingDb != _ceilingDb)
            {
                _ceilingDb = s.CeilingDb;
                _ceiling = _ceilingDb >= 0f ? 1f : (float)Math.Pow(10.0, _ceilingDb / 20.0);
            }
            float ceiling = _ceiling;
            double release = 1.0 - Math.Exp(-1.0 / (s.ReleaseMs * 0.001 * sampleRate));
            int window = lookAhead + 1;
            int delayFloats = lookAhead * Channels;
            long caught = 0, outputOver = 0, limited = 0;
            double smallest = 1.0, gain = 1.0;

            int frames = count / Channels;
            int at = 0;
            for (int f = 0; f < frames; f++, at += Channels)
            {
                float xl = buffer[at], xr = buffer[at + 1];
                float al = Math.Abs(xl), ar = Math.Abs(xr);
                float peak = al > ar ? al : ar;
                double needed = 1.0;
                if (peak > ceiling)
                {
                    needed = (double)ceiling / peak;
                    if (al > ceiling) caught++;
                    if (ar > ceiling) caught++;
                }

                // The smallest needed gain over the last `window` frames.
                double least = needed;
                if (lookAhead > 0)
                {
                    while (_queueTail != _queueHead && _queueValue[(_queueTail - 1) & QueueMask] >= needed) _queueTail = (_queueTail - 1) & QueueMask;
                    _queueValue[_queueTail] = needed;
                    _queueFrame[_queueTail] = _frame;
                    _queueTail = (_queueTail + 1) & QueueMask;
                    while (_queueFrame[_queueHead] <= _frame - window) _queueHead = (_queueHead + 1) & QueueMask;
                    least = _queueValue[_queueHead];
                }

                // Down at once, back by the release.
                double released = _released + (1.0 - _released) * release;
                if (released > least) released = least;
                // Home at last: a step of a hundred-thousandth is 100 dB down, and
                // from here on the audio passes bit for bit again.
                if (1.0 - released < 1e-5) released = 1.0;
                _released = released;

                gain = released;
                float yl = xl, yr = xr;
                if (lookAhead > 0)
                {
                    // The average over the window: a glide down ahead of the over.
                    double leaving = _box[_boxAt];
                    _box[_boxAt] = released;
                    if (++_boxAt == window) _boxAt = 0;
                    if (leaving != 1.0) _boxBelowUnity--;
                    if (released != 1.0) _boxBelowUnity++;
                    if (_boxBelowUnity == 0) { _boxSum = window; gain = 1.0; }
                    else
                    {
                        _boxSum += released - leaving;
                        gain = _boxSum / window;
                        if (gain > 1.0) gain = 1.0;
                    }
                    yl = _delay[_delayAt]; yr = _delay[_delayAt + 1];
                    _delay[_delayAt] = xl; _delay[_delayAt + 1] = xr;
                    _delayAt += Channels;
                    if (_delayAt >= delayFloats) _delayAt = 0;
                }

                if (gain < 1.0)
                {
                    yl = (float)(yl * gain); yr = (float)(yr * gain);
                    // The arithmetic already keeps each sample at or under the
                    // ceiling; this makes the guarantee independent of rounding.
                    if (yl > ceiling) yl = ceiling; else if (yl < -ceiling) yl = -ceiling;
                    if (yr > ceiling) yr = ceiling; else if (yr < -ceiling) yr = -ceiling;
                    limited++;
                    if (gain < smallest) smallest = gain;
                    buffer[at] = yl; buffer[at + 1] = yr;
                }
                else if (lookAhead > 0)
                {
                    buffer[at] = yl; buffer[at + 1] = yr;
                }
                if (Math.Abs(yl) > 1f) outputOver++;
                if (Math.Abs(yr) > 1f) outputOver++;
                _frame++;
            }

            _appliedLookAhead = lookAhead;
            _reductionDb = gain < 1.0 ? (float)(-20.0 * Math.Log10(gain)) : 0f;
            if (countStats)
            {
                if (caught > 0) Interlocked.Add(ref _overCaught, caught);
                if (outputOver > 0) Interlocked.Add(ref _overOutputOver, outputOver);
                if (limited > 0) Interlocked.Add(ref _overLimitedFrames, limited);
                int centi = smallest < 1.0 ? (int)(-2000.0 * Math.Log10(smallest)) : 0;
                int was;
                while (centi > (was = Volatile.Read(ref _overMaxCentiDb)))
                    if (Interlocked.CompareExchange(ref _overMaxCentiDb, centi, was) == was) break;
            }
        }
    }
}
