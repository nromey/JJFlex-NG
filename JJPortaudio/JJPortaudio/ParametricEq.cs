using System;
using System.Collections.Generic;
using System.Threading;

namespace JJPortaudio
{
    /// <summary>How a published equaliser setting takes over (Sprint 49 Track I, repair 1, B4; the receive switch choices of #873).</summary>
    public enum ParametricEqSwitch
    {
        /// <summary>A short cross-fade from the old setting to the new (band edits always; a switch with cross-fade on).</summary>
        Fade = 0,
        /// <summary>A straight cut on the buffer boundary (a switch with cross-fade off).</summary>
        Cut = 1,
        /// <summary>The Mute gap: the old ramps out, silence, the new ramps in (a switch with Mute on).</summary>
        Mute = 2,
    }

    /// <summary>One band of the parametric equaliser: its shape, frequency, gain and Q (Sprint 49 Track I, #1240).</summary>
    public readonly struct ParametricEqBand : IEquatable<ParametricEqBand>
    {
        public ParametricEqBand(BiquadKind kind, double frequencyHz, double gainDb, double q)
        {
            Kind = kind; FrequencyHz = frequencyHz; GainDb = gainDb; Q = q;
        }

        public BiquadKind Kind { get; }
        public double FrequencyHz { get; }
        /// <summary>Boost (positive) or cut (negative), dB. Ignored by the high-pass, low-pass and notch.</summary>
        public double GainDb { get; }
        /// <summary>Width: higher is narrower. For a shelf it sets the slope; 0.7071 is the plain shelf.</summary>
        public double Q { get; }

        public bool Equals(ParametricEqBand o) => Kind == o.Kind && FrequencyHz == o.FrequencyHz && GainDb == o.GainDb && Q == o.Q;
        public override bool Equals(object obj) => obj is ParametricEqBand b && Equals(b);
        public override int GetHashCode() => HashCode.Combine(Kind, FrequencyHz, GainDb, Q);
    }

    /// <summary>
    /// One complete equaliser setting, IMMUTABLE once made: its bands, its
    /// output gain, and the bands' coefficients at <see cref="DesignRate"/>,
    /// computed here, off the audio thread (the conditioner's
    /// immutable-publish pattern). A change is a new design published whole.
    /// </summary>
    public sealed class ParametricEqDesign
    {
        /// <summary>The rate the coefficients are computed for: the transmit and receive streams both run at 48 kHz.</summary>
        public const uint DesignRate = 48000;

        private readonly ParametricEqBand[] _bands;
        private readonly BiquadCoefficients[] _coefficients;

        public ParametricEqDesign(IEnumerable<ParametricEqBand> bands, double outputGainDb = 0)
        {
            var list = new List<ParametricEqBand>();
            if (bands != null)
                foreach (var b in bands)
                {
                    if (list.Count == ParametricEq.MaxBands) break;
                    list.Add(b);
                }
            _bands = list.ToArray();
            OutputGainDb = double.IsFinite(outputGainDb) ? Math.Clamp(outputGainDb, -24.0, 24.0) : 0.0;
            OutputGain = (float)Math.Pow(10.0, OutputGainDb / 20.0);
            _coefficients = new BiquadCoefficients[_bands.Length];
            for (int i = 0; i < _bands.Length; i++) _coefficients[i] = Coefficients(_bands[i], DesignRate);
        }

        /// <summary>The bands, in order. A copy.</summary>
        public ParametricEqBand[] Bands => (ParametricEqBand[])_bands.Clone();

        public int BandCount => _bands.Length;

        public ParametricEqBand Band(int i) => _bands[i];

        public double OutputGainDb { get; }

        /// <summary>The output gain as a multiplier.</summary>
        public float OutputGain { get; }

        /// <summary>True when this design passes audio unchanged: no bands and 0 dB out.</summary>
        public bool IsIdentity => _bands.Length == 0 && OutputGainDb == 0.0;

        internal BiquadCoefficients CoefficientsAt(int i) => _coefficients[i];

        /// <summary>A band's coefficients at a rate. A peaking or shelving band at 0 dB is a straight wire.</summary>
        public static BiquadCoefficients Coefficients(ParametricEqBand b, uint rate) =>
            BiquadCoefficients.Design(b.Kind, b.FrequencyHz, b.GainDb, b.Q, rate);

        /// <summary>The whole design's magnitude response at a frequency, dB, at a rate. For tests and readouts.</summary>
        public double MagnitudeDb(double frequencyHz, uint rate = DesignRate)
        {
            double db = OutputGainDb;
            foreach (var b in _bands) db += Coefficients(b, rate).MagnitudeDb(frequencyHz, rate);
            return db;
        }
    }

    /// <summary>
    /// The parametric equaliser (Sprint 49 Track I, #1240): a cascade of
    /// second-order sections, minimum phase and ZERO latency (each section is
    /// a recursive filter run in place; there is no look-ahead and no block),
    /// direction-agnostic, so the same core serves transmit and receive.
    /// </summary>
    /// <remarks>
    /// <para><b>Publish, never mutate.</b> <see cref="Use"/> takes a whole
    /// immutable <see cref="ParametricEqDesign"/>, from any thread; the audio
    /// thread adopts it at its next buffer.</para>
    /// <para><b>No click on a change.</b> When a new design arrives the old
    /// cascade keeps running beside the new one for
    /// <see cref="CrossFadeMs"/> and the output fades from one to the other.
    /// The new cascade starts from the old one's state band for band, so
    /// tweaking a parameter fades between two nearly identical signals.</para>
    /// <para><b>Off is exact.</b> With no design, or an identity design, and
    /// no fade in progress, <see cref="Process"/> returns without touching a
    /// sample.</para>
    /// <para><b>Never clips silently.</b> Samples past full scale after the
    /// equaliser are counted (<see cref="TakeOverFullScale"/>), on the audio
    /// thread as a plain number; whoever owns the trace writes it, off that
    /// thread. There is no limiter here: a limiter is a stage of its own.</para>
    /// <para>Double-precision state; states that decay into the subnormal
    /// range are flushed once per buffer. No lock and no allocation on the
    /// audio thread. System-only so the numerical harness can link it.</para>
    /// </remarks>
    public sealed class ParametricEq
    {
        /// <summary>The most bands one equaliser holds: a bound on processing cost, far past what a voice needs (Track I's judgement).</summary>
        public const int MaxBands = 32;

        /// <summary>How long a change fades from the old setting to the new one.</summary>
        public const float CrossFadeMs = 20f;

        private const int Channels = 2;

        /// <summary>One published request: the design, how to switch to it, and its coefficients at the rate last seen, computed by the publisher.</summary>
        private sealed class Request
        {
            public ParametricEqDesign Design;
            public ParametricEqSwitch Style;
            public int MuteFrames, RampFrames;
            public uint Rate;
            public BiquadCoefficients[] Coefficients;   // null: use the design's own (DesignRate) or design at adoption
            public long Version;
        }

        private long _published;                 // the last version handed out
        private long _adoptedVersion;            // audio thread
        // The audio thread's own publishing road (the transmit conditioner's
        // ApplyLive runs ON the audio thread): two preallocated requests, so it
        // allocates nothing. A version number, not the object, says what is new.
        private readonly Request _audioA = new Request(), _audioB = new Request();
        private bool _audioFlip;
        private ParametricEqDesign _audioDesign;
        private bool _audioPublished;

        private Request _wanted;                 // published by Use
        private ParametricEqDesign _current;     // audio thread
        private bool _fading;                    // audio thread: a fade or a mute gap is running
        private bool _muting;
        private int _fadeAt, _fadeLength, _muteFrames, _rampFrames;
        private uint _rate;
        private int _lastRate;                   // the rate the audio thread last ran at, for the publisher

        private readonly BiquadCoefficients[] _co = new BiquadCoefficients[MaxBands];
        private readonly BiquadCoefficients[] _coOld = new BiquadCoefficients[MaxBands];
        private readonly BiquadState[] _st = new BiquadState[MaxBands * Channels];
        private readonly BiquadState[] _stOld = new BiquadState[MaxBands * Channels];
        private int _bands, _bandsOld;
        private float _gain = 1f, _gainOld = 1f;

        private long _overFullScale;

        /// <summary>Publish a design (null for off), faded. Safe from any thread; adopted at the next buffer.</summary>
        public void Use(ParametricEqDesign design) => Use(design, ParametricEqSwitch.Fade, 0, 0);

        /// <summary>
        /// Publish a design with a switch style (Sprint 49 Track I, repair 1, B4):
        /// a fade, a straight cut, or the Mute gap (the old setting ramps out over
        /// <paramref name="rampFrames"/>, <paramref name="muteFrames"/> of silence,
        /// the new ramps in). The coefficients for the rate the stream last ran at
        /// are computed HERE, on the caller's thread (repair 1, N4), so the audio
        /// thread designs nothing unless the rate changes after the publish.
        /// </summary>
        public void Use(ParametricEqDesign design, ParametricEqSwitch style, int muteFrames, int rampFrames)
        {
            uint rate = (uint)Volatile.Read(ref _lastRate);
            if (rate == 0) rate = ParametricEqDesign.DesignRate;
            BiquadCoefficients[] co = null;
            if (design != null)
            {
                co = new BiquadCoefficients[design.BandCount];
                for (int b = 0; b < co.Length; b++)
                    co[b] = rate == ParametricEqDesign.DesignRate ? design.CoefficientsAt(b) : ParametricEqDesign.Coefficients(design.Band(b), rate);
            }
            Volatile.Write(ref _wanted, new Request
            {
                Design = design, Style = style, MuteFrames = Math.Max(0, muteFrames), RampFrames = Math.Max(1, rampFrames), Rate = rate, Coefficients = co,
                Version = Interlocked.Increment(ref _published),
            });
        }

        /// <summary>
        /// Publish a design FROM the audio thread (the transmit conditioner's
        /// one-owner record, applied there), faded, allocating nothing: the
        /// same design published again is ignored, and the coefficients are the
        /// design's own, computed when the design was made (off the audio thread).
        /// </summary>
        public void UseFromAudioThread(ParametricEqDesign design)
        {
            if (_audioPublished && ReferenceEquals(design, _audioDesign)) return;
            var r = _audioFlip ? _audioA : _audioB;
            _audioFlip = !_audioFlip;
            r.Design = design; r.Style = ParametricEqSwitch.Fade; r.MuteFrames = 0; r.RampFrames = 1;
            r.Rate = 0; r.Coefficients = null;
            r.Version = Interlocked.Increment(ref _published);
            Volatile.Write(ref _wanted, r);
            _audioDesign = design;
            _audioPublished = true;
        }

        /// <summary>The design most recently published (not necessarily adopted yet).</summary>
        public ParametricEqDesign Wanted => Volatile.Read(ref _wanted)?.Design;

        /// <summary>Whether the equaliser does anything: a non-identity design wanted or in force, or a fade running.</summary>
        public bool IsActive
        {
            get
            {
                var w = Volatile.Read(ref _wanted)?.Design;
                return (w != null && !w.IsIdentity) || (_current != null && !_current.IsIdentity) || _fading;
            }
        }

        /// <summary>Added delay, frames: always zero (minimum-phase recursive sections, run in place).</summary>
        public int LatencyFrames => 0;

        /// <summary>Samples past full scale counted since the last take, then zeroed. Any thread.</summary>
        public long TakeOverFullScale() => Interlocked.Exchange(ref _overFullScale, 0);

        /// <summary>
        /// Process one buffer of interleaved stereo in place.
        /// <paramref name="countClips"/> says whether samples past full scale
        /// in this buffer are counted (the transmit chain counts only keyed buffers).
        /// </summary>
        public void Process(float[] buffer, int count, uint sampleRate, bool countClips = true) =>
            Process(buffer, count, sampleRate, countClips, Channels);

        /// <summary>
        /// Process one buffer of interleaved audio of one or two channels in
        /// place (receive audio may be mono). Any other channel count is left
        /// untouched.
        /// </summary>
        public void Process(float[] buffer, int count, uint sampleRate, bool countClips, int channels)
        {
            if (channels < 1 || channels > Channels) return;
            if (sampleRate == 0) sampleRate = 48000;
            var wanted = Volatile.Read(ref _wanted);
            // A design published while a fade is running waits for the fade to
            // end (at most CrossFadeMs), so a fade is never cut short into a step.
            if ((wanted != null && wanted.Version != _adoptedVersion && !_fading) || sampleRate != _rate)
                Adopt(wanted, sampleRate);

            bool fading = _fading;
            if (!fading && (_current == null || _current.IsIdentity)) return;

            int frames = count / channels;
            int bands = _bands;
            float gain = _gain;
            long clips = 0;
            int i = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    double x = buffer[i + ch];
                    double y = x;
                    for (int b = 0; b < bands; b++) y = _st[b * Channels + ch].Step(in _co[b], y);
                    y *= gain;
                    if (fading)
                    {
                        double yo = x;
                        for (int b = 0; b < _bandsOld; b++) yo = _stOld[b * Channels + ch].Step(in _coOld[b], yo);
                        yo *= _gainOld;
                        if (_muting)
                        {
                            // The Mute gap: the old setting ramps out, silence, the new ramps in.
                            if (_fadeAt < _rampFrames) y = yo * (1.0 - (double)_fadeAt / _rampFrames);
                            else if (_fadeAt < _rampFrames + _muteFrames) y = 0.0;
                            else y *= (double)(_fadeAt - _rampFrames - _muteFrames) / _rampFrames;
                        }
                        else
                        {
                            double t = (double)_fadeAt / _fadeLength;
                            y = yo + (y - yo) * t;
                        }
                    }
                    float o = (float)y;
                    if (o > 1f || o < -1f) clips++;
                    buffer[i + ch] = o;
                }
                i += channels;
                if (fading && ++_fadeAt >= _fadeLength)
                {
                    _fading = false;
                    fading = false;
                }
            }
            for (int s = 0; s < bands * Channels; s++) _st[s].FlushTiny();
            if (fading) for (int s = 0; s < _bandsOld * Channels; s++) _stOld[s].FlushTiny();
            if (countClips && clips > 0) Interlocked.Add(ref _overFullScale, clips);
        }

        /// <summary>Start a fade from what is in force to the request's design. Audio thread; no allocation.</summary>
        private void Adopt(Request request, uint rate)
        {
            var next = request?.Design;
            _adoptedVersion = request?.Version ?? 0;
            Volatile.Write(ref _lastRate, (int)rate);
            // What is in force becomes the "old" cascade, state and all.
            Array.Copy(_co, _coOld, MaxBands);
            Array.Copy(_st, _stOld, _st.Length);
            _bandsOld = _bands;
            _gainOld = _gain;

            int n = next?.BandCount ?? 0;
            for (int b = 0; b < n; b++)
                // Designed by the publisher for this rate; only a rate that changed
                // after the publish is designed here (allocation-free, no lock).
                _co[b] = request.Rate == rate && request.Coefficients != null ? request.Coefficients[b]
                    : rate == ParametricEqDesign.DesignRate ? next.CoefficientsAt(b) : ParametricEqDesign.Coefficients(next.Band(b), rate);
            // The new cascade starts from the old one's state band for band (a
            // tweak fades between near-identical signals); bands beyond the old
            // count start from rest.
            for (int s = _bandsOld * Channels; s < n * Channels; s++) _st[s].Clear();
            _bands = n;
            _gain = next?.OutputGain ?? 1f;

            bool wasDoingSomething = (_current != null && !_current.IsIdentity) || _fading;
            _current = next;
            var style = request?.Style ?? ParametricEqSwitch.Fade;
            if (_rate != 0 && rate != _rate)
            {
                // A rate change: the old state belongs to dead audio; no fade.
                for (int s = 0; s < _st.Length; s++) _st[s].Clear();
                _fading = false;
            }
            else if (style == ParametricEqSwitch.Cut)
            {
                _fading = false; // a straight cut on the buffer boundary (cross-fade off)
            }
            else if (wasDoingSomething || (next != null && !next.IsIdentity))
            {
                _fading = true;
                _fadeAt = 0;
                _muting = style == ParametricEqSwitch.Mute;
                _muteFrames = request.MuteFrames;
                _rampFrames = request.RampFrames;
                _fadeLength = _muting ? _rampFrames * 2 + _muteFrames : Math.Max(1, (int)(CrossFadeMs * 0.001 * rate));
            }
            _rate = rate;
        }
    }
}
