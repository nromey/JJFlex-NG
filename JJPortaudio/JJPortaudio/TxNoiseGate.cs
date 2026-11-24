using System;
using System.Threading;

namespace JJPortaudio
{
    /// <summary>
    /// Podcast-style noise gate for the PC transmit path (Track I). Sits in
    /// the PortAudio input callback between the test-tone injection point and
    /// the LUFS meter, so the meter keeps measuring what actually goes to the
    /// encoder — gated audio and all.
    ///
    /// Three design points decide whether a gate is loved or switched off,
    /// and all three are structural here, not tuning suggestions:
    ///
    ///  * ATTACK IS FAST (default 3 ms). A slow attack eats the front of
    ///    words — the commonest complaint about every gate ever shipped. The
    ///    gate also RESETS OPEN on a cold stream start (key-down), so the
    ///    first syllable of a transmission can never be clipped by a gate
    ///    that woke up closed.
    ///  * HOLD (default 150 ms) keeps the gate open across the natural gaps
    ///    inside a sentence, so it does not chatter between words.
    ///  * IT NEVER GATES TO SILENCE. Full closure is fine in a podcast; on
    ///    SSB it makes the other operator think you dropped. Closed means
    ///    attenuated by <see cref="RangeDb"/> (default 25 dB, clamped to at
    ///    most 40) — a natural floor that reads as "still here, not talking."
    ///
    /// The threshold is DERIVED, not a constant: the app measures the room's
    /// noise floor and sets <see cref="ThresholdDb"/> to floor + 6..10 dB.
    /// Until a floor has been measured the default threshold is deliberately
    /// LOW (-60 dB): an unmeasured gate that does nothing is a far better
    /// failure than one that eats speech. Since Sprint 49 Track I (#1252) the
    /// floor is measured at THIS stage's input, after noise reduction, by
    /// <see cref="TxRoomFloor"/>; before that it was measured after the gate,
    /// which measured the gate's own effect and flipped from over to over.
    ///
    /// Level model: the detector is an RMS-style envelope of the per-frame
    /// channel-power sum, the same accounting BS.1770 uses — a -10 dBFS sine
    /// duplicated on both channels reads -10 dB here and -10 LUFS on the
    /// meter — so a threshold derived from a LUFS floor lands in the right
    /// place without unit conversion. (K-weighting makes the two scales
    /// differ by a couple of dB on real signals; the 6-10 dB margin absorbs
    /// that.)
    ///
    /// Thread model: Process runs on the PortAudio callback thread; property
    /// setters run on UI threads. All tunables are volatile floats read once
    /// per buffer. No locks, no allocation on the audio path — the same
    /// contract as TxToneGenerator and LufsMeter, and this file is
    /// System-only on purpose so the numerical harness can link it directly.
    ///
    /// <para><b>A DOWNWARD EXPANDER since Sprint 49 Track I (#432, ruled by
    /// Noel 2026-08-31: "we need to change the gate to a downward
    /// expander").</b> The class keeps its name on purpose: the settings that
    /// drive it are saved under the gate's names, and the operator meets ONE
    /// stage whose strongest setting is a gate (2026-11-23 11:32).</para>
    /// <list type="bullet">
    /// <item><b>The ratio</b> (<see cref="RatioToOne"/>): below the point
    /// where the decision closes (the threshold minus the 3 dB hysteresis),
    /// the attenuation is (ratio - 1) times the distance below that point,
    /// capped at <see cref="RangeDb"/>. Measured from the close point, so the
    /// attenuation starts at zero at the moment the decision closes and there
    /// is no step. 1:1 is no attenuation at all.</item>
    /// <item><b>The gate setting</b> (<see cref="GateSetting"/>): the fixed
    /// <see cref="RangeDb"/> attenuation whenever the decision is closed,
    /// which is the gate as it was before the conversion, sample for sample.
    /// It is a flag, not a large ratio, because no finite ratio reproduces
    /// the old output exactly, and that exactness is what lets a saved gate
    /// keep its sound (S7).</item>
    /// <item><b>The side-chain filter</b> (ruled 2026-11-23 11:35): a band-pass
    /// on what the DETECTOR hears, never on the audio passed, so rumble below
    /// the voice and hiss above it do not open the stage. Zero delay on the
    /// audio path; its state lives with the detector.</item>
    /// <item><b>Look-ahead</b> (ruled 11:35, adjustable 20:56): the audio
    /// path is delayed by up to <see cref="MaxLookAheadMs"/> while the
    /// detector reads the undelayed signal, so the stage has opened before
    /// the word arrives. It is the stage's only delay
    /// (<see cref="AppliedLookAheadFrames"/>). At 0 ms the audio path is the
    /// old one exactly.</item>
    /// </list>
    /// <para>The open, hold and close decision, its hysteresis, the reset
    /// OPEN at key-down and the never-silence cap all survive. What does not
    /// survive is reading the stage as "open" or "closed": #432, "do not leave
    /// IsOpen returning something plausible". The reading is
    /// <see cref="AttenuationDb"/>, the attenuation applied now.</para>
    /// </summary>
    public class TxNoiseGate
    {
        private const int Channels = 2;
        // Re-open faster than we close: hysteresis keeps a level hovering at
        // the threshold from toggling the gate on every syllable tail. The
        // expander keeps it for its open/hold/close decision; its ratio law is
        // measured from the close point this sets.
        private const float HysteresisDb = 3f;
        // A gap in Process calls longer than this means the input stream was
        // stopped (unkey) and restarted — reset, and reset OPEN.
        private const long StreamGapMs = 100;
        // Detector time constants: fast rise so the gate can open on a word
        // onset, moderate fall so it tracks speech level rather than the
        // waveform inside a syllable.
        private const float DetectorAttackSec = 0.0005f;
        private const float DetectorReleaseSec = 0.050f;

        /// <summary>How far below the threshold the decision closes, dB: the law's zero point is the threshold minus this.</summary>
        public const float CloseBelowThresholdDb = HysteresisDb;

        private volatile bool _enabled;
        private volatile float _thresholdDb = DefaultThresholdDb;
        private volatile float _attackMs = 3f;
        private volatile float _holdMs = 150f;
        private volatile float _releaseMs = 200f;
        private volatile float _rangeDb = 25f;
        // The expander (Sprint 49 Track I). The DSP's own defaults are the
        // gate as it was (gate setting, no side-chain filter, no look-ahead),
        // so a TxNoiseGate made and used directly behaves exactly as before;
        // the setup record carries the new expander's defaults (2:1, filter
        // on, 3 ms) and the conditioner puts them here.
        private volatile float _ratio = DefaultRatioToOne;
        private volatile bool _gateSetting = true;
        private volatile bool _sideChainEnabled;
        private volatile float _sideChainLowHz = DefaultSideChainLowHz;
        private volatile float _sideChainHighHz = DefaultSideChainHighHz;
        private volatile float _lookAheadMs;

        /// <summary>The do-nothing threshold shipped before any floor has been
        /// measured. See class remarks: unmeasured means inert, never hungry.</summary>
        public const float DefaultThresholdDb = -60f;

        /// <summary>The ratio a newly switched-on expander starts at (S7, ruled 2026-11-23 11:29).</summary>
        public const float DefaultRatioToOne = 2f;

        /// <summary>The ratio's range. 1:1 is no expansion.</summary>
        public const float MinRatioToOne = 1f, MaxRatioToOne = 20f;

        /// <summary>
        /// The side-chain filter's default corners: 150 Hz to 4 kHz. Inside
        /// what an SSB transmit filter passes, so the detector hears the voice
        /// band the radio will send and none of what it throws away (fan
        /// rumble below, hiss above). Sprint 49 Track I's judgement; see its report.
        /// </summary>
        public const float DefaultSideChainLowHz = 150f, DefaultSideChainHighHz = 4000f;

        /// <summary>The look-ahead's default when the expander is new (ruled 2026-11-23 20:56: adjustable, with a default starting point).</summary>
        public const float DefaultLookAheadMs = 3f;

        /// <summary>The longest look-ahead, ms.</summary>
        public const float MaxLookAheadMs = 10f;

        // 10 ms at 192 kHz: the look-ahead line is allocated once, here, and
        // never on the audio thread.
        private const int MaxLookAheadFrames = 1920;
        private readonly float[] _lookAhead = new float[MaxLookAheadFrames * Channels];
        private int _lookAheadAt;
        private int _lookAheadFrames;
        private int _appliedLookAheadFrames;
        private bool _wasEnabled;

        // The detector's side-chain filter: a high-pass at the low corner and a
        // low-pass at the high corner, per channel. Designed on the audio thread
        // when a corner or the rate changes (no allocation: a few trig calls).
        private readonly SideChainFilter _sideChainFilter = new SideChainFilter();

        // --- Audio-thread state ---
        private double _envPower;      // smoothed channel-power sum
        private float _gain = 1f;      // current applied gain (linear)
        private int _holdRemaining;    // frames of hold left once below threshold
        private bool _open = true;
        private long _lastProcessMs;
        private volatile float _attenuationDb;

        /// <summary>Master switch. Off means Process returns without touching
        /// a sample — bypassed produces silence in the residual monitor,
        /// which is exactly how an operator proves the pathway is live.</summary>
        public bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        /// <summary>
        /// Open threshold in dB (RMS-style, see class remarks; comparable to
        /// LUFS figures for this stream). Normally set by the app to
        /// measured-noise-floor + 6..10 dB rather than by hand.
        /// </summary>
        public float ThresholdDb
        {
            get { return _thresholdDb; }
            set { _thresholdDb = Math.Clamp(value, -80f, -10f); }
        }

        /// <summary>Gain rise time, ms (1-20). Fast so it does not clip the
        /// start of your words.</summary>
        public float AttackMs
        {
            get { return _attackMs; }
            set { _attackMs = Math.Clamp(value, 1f, 20f); }
        }

        /// <summary>How long the gate stays open after the level drops, ms
        /// (50-1000). Bridges the natural pauses inside a sentence so the
        /// gate does not chatter mid-thought.</summary>
        public float HoldMs
        {
            get { return _holdMs; }
            set { _holdMs = Math.Clamp(value, 50f, 1000f); }
        }

        /// <summary>Gain fall time once hold expires, ms (50-1000). Slow
        /// enough to sound like a fade, not a cut.</summary>
        public float ReleaseMs
        {
            get { return _releaseMs; }
            set { _releaseMs = Math.Clamp(value, 50f, 1000f); }
        }

        /// <summary>
        /// How far the gate attenuates when closed, dB (6-40). Deliberately
        /// capped well short of silence: a fully-muted SSB signal reads as a
        /// dropped contact, an attenuated one reads as "quiet between words."
        /// For the expander it is the DEPTH: the most the ratio law may take.
        /// </summary>
        public float RangeDb
        {
            get { return _rangeDb; }
            set { _rangeDb = Math.Clamp(value, 6f, 40f); }
        }

        /// <summary>
        /// The expander's ratio, n:1 (1 to 20). Used whenever
        /// <see cref="GateSetting"/> is off. See class remarks for the law.
        /// </summary>
        public float RatioToOne
        {
            get { return _ratio; }
            set { _ratio = float.IsFinite(value) ? Math.Clamp(value, MinRatioToOne, MaxRatioToOne) : DefaultRatioToOne; }
        }

        /// <summary>
        /// The top setting, the gate: a fixed <see cref="RangeDb"/> attenuation
        /// whenever the decision is closed, exactly as before the conversion.
        /// True by default on this class, so the DSP used directly is the old gate.
        /// </summary>
        public bool GateSetting
        {
            get { return _gateSetting; }
            set { _gateSetting = value; }
        }

        /// <summary>Whether the detector hears the voice band only (see class remarks). Off by default on this class.</summary>
        public bool SideChainEnabled
        {
            get { return _sideChainEnabled; }
            set { _sideChainEnabled = value; }
        }

        /// <summary>The side-chain filter's low corner, Hz (40 to 1000).</summary>
        public float SideChainLowHz
        {
            get { return _sideChainLowHz; }
            set { _sideChainLowHz = float.IsFinite(value) ? Math.Clamp(value, 40f, 1000f) : DefaultSideChainLowHz; }
        }

        /// <summary>The side-chain filter's high corner, Hz (1500 to 10000).</summary>
        public float SideChainHighHz
        {
            get { return _sideChainHighHz; }
            set { _sideChainHighHz = float.IsFinite(value) ? Math.Clamp(value, 1500f, 10000f) : DefaultSideChainHighHz; }
        }

        /// <summary>
        /// Look-ahead, ms (0 to <see cref="MaxLookAheadMs"/>): how far the audio
        /// path is delayed behind the detector. A change of length moves the
        /// stream in time, so the conditioner treats it as a switch that waits
        /// for the unkey; the line restarts from silence when the length changes.
        /// </summary>
        public float LookAheadMs
        {
            get { return _lookAheadMs; }
            set { _lookAheadMs = float.IsFinite(value) ? Math.Clamp(value, 0f, MaxLookAheadMs) : 0f; }
        }

        /// <summary>The look-ahead delay the last processed buffer went out with, frames. Zero when the stage is off: the stage's only delay.</summary>
        public int AppliedLookAheadFrames => Volatile.Read(ref _appliedLookAheadFrames);

        /// <summary>The look-ahead in frames at a rate, as <see cref="Process"/> applies it.</summary>
        public static int LookAheadFramesFor(float ms, uint sampleRate)
        {
            if (sampleRate == 0) sampleRate = 48000;
            if (!float.IsFinite(ms)) ms = 0f;
            return Math.Clamp((int)Math.Round(Math.Clamp(ms, 0f, MaxLookAheadMs) * 0.001 * sampleRate), 0, MaxLookAheadFrames);
        }

        private volatile bool _resetPending;

        /// <summary>
        /// Whether a gap of more than 100 ms between buffers resets the gate
        /// open. True when nothing better is known; the transmit conditioner
        /// turns it off whenever it has a real key state (Sprint 49 Track H,
        /// item E, #1075). Since #565 the microphone streams continuously, so a
        /// key-down no longer makes a gap; meanwhile ten-frame bursts arriving
        /// 100 ms apart, timed on a clock with about 15.6 ms resolution, could
        /// read as a gap and reset the gate open in the middle of an over.
        /// </summary>
        public bool ResetsOnStreamGap { get; set; } = true;

        /// <summary>
        /// Reset OPEN at the next buffer: the key went down (item E, #1075).
        /// The first syllable of an over must never meet a gate that woke up
        /// closed. Safe from any thread; the audio thread applies it.
        /// </summary>
        public void ResetOpen() => _resetPending = true;

        /// <summary>Times the gate has reset open since it was made: key-downs, or stream gaps where those still count. A test and trace counter.</summary>
        public long Resets { get; private set; }

        /// <summary>
        /// The attenuation applied at the end of the last buffer, dB: 0 while
        /// passing, up to <see cref="RangeDb"/> (never more than 40) below the
        /// threshold. The one reading the stage gives (#432): a number, not
        /// "open" or "closed", which stopped being true things to say when the
        /// gate became an expander.
        /// </summary>
        public float AttenuationDb => _attenuationDb;

        /// <summary>
        /// Process one buffer of interleaved stereo float samples in place.
        /// Called from the PortAudio input callback, after tone injection and
        /// noise reduction, before the LUFS meter and the Opus encode.
        /// </summary>
        /// <param name="buffer">interleaved stereo samples (mono mic
        /// duplicated on both channels)</param>
        /// <param name="count">number of floats to process (frames * 2)</param>
        /// <param name="sampleRate">stream sample rate in Hz</param>
        public void Process(float[] buffer, int count, uint sampleRate)
        {
            // Stamp the clock on every call, enabled or not, so toggling the
            // gate on mid-transmission does not read as a cold start.
            long now = Environment.TickCount64;
            long last = _lastProcessMs;
            _lastProcessMs = now;

            bool reset = _resetPending;
            if (reset) _resetPending = false;
            if (!_enabled)
            {
                _wasEnabled = false;
                Volatile.Write(ref _appliedLookAheadFrames, 0);
                _attenuationDb = 0f;
                return;
            }

            if (sampleRate == 0) sampleRate = 48000;

            // Look-ahead line: a stage switched on, or a new length, starts from
            // silence, never from audio left over from before (Track I, 1b).
            int laFrames = LookAheadFramesFor(_lookAheadMs, sampleRate);
            if (!_wasEnabled || laFrames != _lookAheadFrames)
            {
                Array.Clear(_lookAhead, 0, _lookAhead.Length);
                _lookAheadAt = 0;
                _lookAheadFrames = laFrames;
                _wasEnabled = true;
            }

            // Cold start (key-down): reset OPEN. The gate must never eat the
            // first syllable of a transmission while its detector warms up.
            // The key-down is told by ResetOpen; a stream gap still counts
            // only where nothing knows the key state.
            bool gap = ResetsOnStreamGap && (last == 0 || now - last > StreamGapMs);
            if (reset || gap)
            {
                Resets++;
                _envPower = 0.0;
                _gain = 1f;
                _open = true;
                _holdRemaining = int.MaxValue; // re-armed below on first frame
                // A restarted stream's look-ahead holds dead audio; a key-down
                // on a continuous stream (#565) does not, so only a gap clears it.
                if (gap) { Array.Clear(_lookAhead, 0, _lookAhead.Length); _lookAheadAt = 0; }
                _sideChainFilter.Clear();
            }

            // Read tunables once per buffer.
            float thresholdDb = _thresholdDb;
            float closeDb = thresholdDb - HysteresisDb;
            float rangeGain = (float)Math.Pow(10.0, -_rangeDb / 20.0);
            int holdFrames = (int)(_holdMs * 0.001f * sampleRate);
            float attackCoeff = CoeffForMs(_attackMs, sampleRate);
            float releaseCoeff = CoeffForMs(_releaseMs, sampleRate);
            float detAttack = CoeffForSec(DetectorAttackSec, sampleRate);
            float detRelease = CoeffForSec(DetectorReleaseSec, sampleRate);
            bool gateSetting = _gateSetting;
            double ratioLess1 = _ratio - 1.0;
            double rangeDb = _rangeDb;
            bool sideChain = _sideChainEnabled;
            if (sideChain) _sideChainFilter.Design(_sideChainLowHz, _sideChainHighHz, sampleRate);
            int la = _lookAheadFrames;
            int laLen = la * Channels;
            int laAt = _lookAheadAt;

            int frames = count / Channels;
            int i = 0;
            for (int f = 0; f < frames; f++)
            {
                // Detector: smoothed sum of channel powers (BS.1770-style
                // accounting — see class remarks on comparability to LUFS),
                // through the side-chain filter when it is on. The audio itself
                // never goes through that filter.
                float l = buffer[i];
                float r = buffer[i + 1];
                double power;
                if (sideChain)
                {
                    double dl = _sideChainFilter.Step(0, l);
                    double dr = _sideChainFilter.Step(1, r);
                    power = dl * dl + dr * dr;
                }
                else power = (double)l * l + (double)r * r;
                double coeff = power > _envPower ? detAttack : detRelease;
                _envPower += (power - _envPower) * coeff;

                double envDb = 10.0 * Math.Log10(_envPower + 1e-12);

                // Open/hold/close decision with hysteresis.
                if (envDb > (_open ? closeDb : thresholdDb))
                {
                    _open = true;
                    _holdRemaining = holdFrames;
                }
                else if (_holdRemaining > 0)
                {
                    if (_holdRemaining != int.MaxValue) _holdRemaining--;
                    else _holdRemaining = holdFrames;
                }
                else
                {
                    _open = false;
                }

                bool passing = _open || _holdRemaining > 0;
                float target;
                if (passing) target = 1f;
                else if (gateSetting) target = rangeGain;
                else
                {
                    // The expander's law, measured from the close point: zero
                    // attenuation where the decision closes, rising by
                    // (ratio - 1) dB per dB below it, never past the depth.
                    double attDb = Math.Min(rangeDb, ratioLess1 * (closeDb - envDb));
                    target = attDb <= 0.0 ? 1f : (float)Math.Exp(-attDb * DbToNeper);
                }

                // Gain smoothing: fast toward open (attack), slow toward
                // closed (release).
                float g = _gain;
                g += (target - g) * (target > g ? attackCoeff : releaseCoeff);
                _gain = g;

                if (la == 0)
                {
                    buffer[i] = l * g;
                    buffer[i + 1] = r * g;
                }
                else
                {
                    // Look-ahead: the gain decided on THIS sample is applied to
                    // the sample from la frames ago, so the gain has already
                    // risen when a word's first sample reaches the output.
                    float dl = _lookAhead[laAt];
                    float dr = _lookAhead[laAt + 1];
                    _lookAhead[laAt] = l;
                    _lookAhead[laAt + 1] = r;
                    laAt += Channels;
                    if (laAt >= laLen) laAt = 0;
                    buffer[i] = dl * g;
                    buffer[i + 1] = dr * g;
                }
                i += Channels;
            }
            _lookAheadAt = laAt;
            if (sideChain) _sideChainFilter.FlushTiny();
            Volatile.Write(ref _appliedLookAheadFrames, la);
            _attenuationDb = (float)(-20.0 * Math.Log10(Math.Max(_gain, 1e-6f)));
        }

        private static readonly double DbToNeper = Math.Log(10.0) / 20.0;

        private static float CoeffForMs(float ms, uint sampleRate)
        {
            return CoeffForSec(ms * 0.001f, sampleRate);
        }

        /// <summary>One-pole coefficient reaching ~63% of a step in the given
        /// time at the given rate.</summary>
        private static float CoeffForSec(float sec, uint sampleRate)
        {
            if (sec <= 0f) return 1f;
            return 1f - (float)Math.Exp(-1.0 / (sec * sampleRate));
        }
    }
}
