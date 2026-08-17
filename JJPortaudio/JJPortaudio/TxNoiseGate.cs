using System;

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
    /// noise floor (LufsMeter.Profile.NoiseFloorLufs, the same figure the
    /// Microphone Check reports) and sets <see cref="ThresholdDb"/> to
    /// floor + 6..10 dB. Until a floor has been measured the default
    /// threshold is deliberately LOW (-60 dB): an unmeasured gate that does
    /// nothing is a far better failure than one that eats speech.
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
    /// </summary>
    public class TxNoiseGate
    {
        private const int Channels = 2;
        // Re-open faster than we close: hysteresis keeps a level hovering at
        // the threshold from toggling the gate on every syllable tail.
        private const float HysteresisDb = 3f;
        // A gap in Process calls longer than this means the input stream was
        // stopped (unkey) and restarted — reset, and reset OPEN.
        private const long StreamGapMs = 100;
        // Detector time constants: fast rise so the gate can open on a word
        // onset, moderate fall so it tracks speech level rather than the
        // waveform inside a syllable.
        private const float DetectorAttackSec = 0.0005f;
        private const float DetectorReleaseSec = 0.050f;

        private volatile bool _enabled;
        private volatile float _thresholdDb = DefaultThresholdDb;
        private volatile float _attackMs = 3f;
        private volatile float _holdMs = 150f;
        private volatile float _releaseMs = 200f;
        private volatile float _rangeDb = 25f;

        /// <summary>The do-nothing threshold shipped before any floor has been
        /// measured. See class remarks: unmeasured means inert, never hungry.</summary>
        public const float DefaultThresholdDb = -60f;

        // --- Audio-thread state ---
        private double _envPower;      // smoothed channel-power sum
        private float _gain = 1f;      // current applied gain (linear)
        private int _holdRemaining;    // frames of hold left once below threshold
        private bool _open = true;
        private long _lastProcessMs;

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
        /// </summary>
        public float RangeDb
        {
            get { return _rangeDb; }
            set { _rangeDb = Math.Clamp(value, 6f, 40f); }
        }

        /// <summary>True while the gate is passing speech at unity (including
        /// the hold time). For UI/announcement use.</summary>
        public bool IsOpen => _open;

        /// <summary>The gain currently applied, dB (0 when open, approaching
        /// -RangeDb when closed). For UI/announcement use.</summary>
        public float CurrentGainDb =>
            (float)(20.0 * Math.Log10(Math.Max(_gain, 1e-6f)));

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

            if (!_enabled) return;

            if (sampleRate == 0) sampleRate = 48000;

            // Cold start (key-down after an unkey): reset OPEN. The gate must
            // never eat the first syllable of a transmission while its
            // detector warms up.
            if (last == 0 || now - last > StreamGapMs)
            {
                _envPower = 0.0;
                _gain = 1f;
                _open = true;
                _holdRemaining = int.MaxValue; // re-armed below on first frame
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

            int frames = count / Channels;
            int i = 0;
            for (int f = 0; f < frames; f++)
            {
                // Detector: smoothed sum of channel powers (BS.1770-style
                // accounting — see class remarks on comparability to LUFS).
                float l = buffer[i];
                float r = buffer[i + 1];
                double power = (double)l * l + (double)r * r;
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
                float target = passing ? 1f : rangeGain;

                // Gain smoothing: fast toward open (attack), slow toward
                // closed (release).
                float g = _gain;
                g += (target - g) * (target > g ? attackCoeff : releaseCoeff);
                _gain = g;

                buffer[i] = l * g;
                buffer[i + 1] = r * g;
                i += Channels;
            }
        }

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
