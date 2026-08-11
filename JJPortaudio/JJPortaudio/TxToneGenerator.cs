using System;

namespace JJPortaudio
{
    /// <summary>
    /// TX test-tone source (Audio Track C). Lives at the exact injection
    /// point — the PortAudio input callback, ahead of the Opus encoder — and
    /// when engaged its samples REPLACE the microphone samples. Replacement,
    /// never mixing, is the design contract: the real input is discarded
    /// entirely while the tone runs, so only the clean reference tone reaches
    /// the transmitter, with zero room bleed. A tone mixed with a live mic is
    /// not a reference signal.
    ///
    /// Click-free by construction: a short state machine ramps the mic out,
    /// the tone in, the tone out, and the mic back in — about 10 ms per ramp —
    /// and at no point are mic and tone audible together (the mic ramps run on
    /// mic samples only, the tone ramps overwrite the buffer entirely). The
    /// sine phase accumulator is continuous, so frequency changes mid-tone
    /// glide without a discontinuity, and level changes are smoothed to avoid
    /// zipper noise.
    ///
    /// Leakage past unkey is impossible by construction: samples only flow
    /// while the TX input stream runs, and FlexBase stops that stream at
    /// unkey. When the stream has been stopped and restarts (unkey then
    /// re-key), Process detects the gap and restarts the tone from silence —
    /// no stale ramp state, no burst of mic audio at key-down.
    ///
    /// Thread model: Process runs on the PortAudio callback thread; every
    /// public setter runs on a UI thread. All shared fields are volatile and
    /// the state transitions are tolerant of a command landing mid-buffer
    /// (worst case a ramp restarts — inaudible). No locks in the audio path.
    /// </summary>
    public class TxToneGenerator
    {
        // States. The audio thread advances fades forward; the UI thread only
        // requests engage (-> MicFadeOut) or release (-> ToneFadeOut).
        private const int StIdle = 0;        // mic passes untouched
        private const int StMicFadeOut = 1;  // mic ramping to silence (mic only)
        private const int StToneFadeIn = 2;  // tone ramping up (mic discarded)
        private const int StTone = 3;        // tone at level (mic discarded)
        private const int StToneFadeOut = 4; // tone ramping down (mic discarded)
        private const int StMicFadeIn = 5;   // mic ramping back (mic only)

        private volatile int _state = StIdle;

        // Ramp position 0..1 within the current fade state (audio thread only).
        private float _fade;
        // Continuous sine phase in radians (audio thread only).
        private double _phase;
        // Smoothed current amplitude (audio thread only).
        private float _amp;
        // Last Process call, for stream-gap (unkey/re-key) detection.
        private long _lastProcessMs;

        private const float FadeSeconds = 0.010f; // 10 ms ramps
        private const long StreamGapMs = 100;     // gap => stream was stopped
        // One-pole amplitude smoothing per frame; ~20 ms settle at 48 kHz.
        private const float AmpSmooth = 0.001f;
        // The TX input stream is opened stereo interleaved (channelCount 2 in
        // AudioAnchor, Opus encoder Channels.Stereo). Identical samples go to
        // both channels.
        private const int Channels = 2;

        private volatile float _frequency = 440f;
        private volatile float _ampTarget = 0.31623f; // -10 dBFS
        private volatile float _levelDb = -10f;

        /// <summary>
        /// Tone frequency in hertz. Safe to change while the tone runs — the
        /// phase accumulator is continuous, so there is no click.
        /// </summary>
        public float Frequency
        {
            get { return _frequency; }
            set { _frequency = Math.Clamp(value, 10f, 20000f); }
        }

        /// <summary>
        /// Tone level in dBFS (-60..0). Default -10 dBFS, which lands in the
        /// "just right" band of the mic-audio verdict and matches the bench
        /// reference measurement that calibrated the honest meters.
        /// </summary>
        public float LevelDb
        {
            get { return _levelDb; }
            set
            {
                float db = Math.Clamp(value, -60f, 0f);
                _levelDb = db;
                _ampTarget = (float)Math.Pow(10.0, db / 20.0);
            }
        }

        /// <summary>
        /// True from Start() until Stop() begins releasing back to the mic.
        /// While true, the microphone is muted (its samples are discarded)
        /// whenever TX audio flows.
        /// </summary>
        public bool Engaged
        {
            get
            {
                int s = _state;
                return s == StMicFadeOut || s == StToneFadeIn || s == StTone;
            }
        }

        /// <summary>
        /// Engage the tone: the mic ramps out, then the tone ramps in. If TX
        /// audio is not flowing yet, the tone simply starts clean at the next
        /// key-down with no mic bleed at all.
        /// </summary>
        public void Start()
        {
            int s = _state;
            if (s == StMicFadeOut || s == StToneFadeIn || s == StTone) return;
            _state = StMicFadeOut;
        }

        /// <summary>
        /// Release the tone: it ramps out, then the mic ramps back in. If TX
        /// audio is not flowing, the release resolves instantly at the next
        /// stream start.
        /// </summary>
        public void Stop()
        {
            int s = _state;
            if (s == StIdle || s == StToneFadeOut || s == StMicFadeIn) return;
            _state = StToneFadeOut;
        }

        /// <summary>
        /// Called from the PortAudio input callback with one Opus frame of
        /// interleaved stereo mic samples, BEFORE the Opus encode. Depending
        /// on state this passes the buffer untouched, ramps it, or overwrites
        /// it entirely with the tone.
        /// </summary>
        /// <param name="buffer">interleaved stereo float samples (mic capture)</param>
        /// <param name="count">number of floats to process (frames * 2)</param>
        /// <param name="sampleRate">stream sample rate in Hz</param>
        public void Process(float[] buffer, int count, uint sampleRate)
        {
            // Stamp the clock on EVERY call, including idle passthrough —
            // otherwise an engage while mic audio is flowing reads as a cold
            // stream start and hard-cuts the mic (a click) instead of fading
            // it. Caught by the numerical harness, 2026-08-11.
            long now = Environment.TickCount64;
            long last = _lastProcessMs;
            _lastProcessMs = now;

            int state = _state;
            if (state == StIdle) return;

            // Stream-gap detection: if the input stream was stopped (unkey)
            // and has just restarted, there is no established mic audio to
            // ramp against and no tone in flight — resolve transition states
            // instantly and start any engaged tone from silence.
            bool cold = (now - last) > StreamGapMs;
            if (cold)
            {
                if (state == StMicFadeOut || state == StToneFadeIn || state == StTone)
                {
                    // Engaged: tone restarts from silence; the mic never passes.
                    state = StToneFadeIn;
                    _fade = 0f;
                    _amp = 0f;
                    _phase = 0.0;
                    _state = state;
                }
                else
                {
                    // Releasing: nothing in flight to ramp — mic passes now.
                    _state = StIdle;
                    return;
                }
            }

            if (sampleRate == 0) sampleRate = 48000;
            float fadeStep = 1f / (FadeSeconds * sampleRate); // per frame
            int frames = count / Channels;
            int i = 0;

            for (int f = 0; f < frames; f++)
            {
                switch (state)
                {
                    case StMicFadeOut:
                        {
                            // Mic only, ramping down. No tone yet — never mixed.
                            float g = 1f - _fade;
                            buffer[i] *= g;
                            buffer[i + 1] *= g;
                            _fade += fadeStep;
                            if (_fade >= 1f)
                            {
                                state = StToneFadeIn;
                                _state = state;
                                _fade = 0f;
                                _amp = 0f;
                                _phase = 0.0;
                            }
                        }
                        break;

                    case StToneFadeIn:
                        {
                            float s = NextToneSample(sampleRate) * _fade;
                            buffer[i] = s;
                            buffer[i + 1] = s;
                            _fade += fadeStep;
                            if (_fade >= 1f)
                            {
                                state = StTone;
                                _state = state;
                            }
                        }
                        break;

                    case StTone:
                        {
                            float s = NextToneSample(sampleRate);
                            buffer[i] = s;
                            buffer[i + 1] = s;
                        }
                        break;

                    case StToneFadeOut:
                        {
                            float s = NextToneSample(sampleRate) * (1f - _fade);
                            buffer[i] = s;
                            buffer[i + 1] = s;
                            _fade += fadeStep;
                            if (_fade >= 1f)
                            {
                                state = StMicFadeIn;
                                _state = state;
                                _fade = 0f;
                            }
                        }
                        break;

                    case StMicFadeIn:
                        {
                            // Tone gone; mic ramping back. Mic only — never mixed.
                            buffer[i] *= _fade;
                            buffer[i + 1] *= _fade;
                            _fade += fadeStep;
                            if (_fade >= 1f)
                            {
                                state = StIdle;
                                _state = state;
                            }
                        }
                        break;

                    case StIdle:
                        // Released mid-buffer: remaining mic samples pass untouched.
                        return;
                }

                // A UI-thread command may have moved the state mid-buffer
                // (Start during release, Stop during tone). Re-read so the
                // rest of the buffer follows the new state; ramp state carries
                // over, which at worst shortens a 10 ms fade.
                int commanded = _state;
                if (commanded != state)
                {
                    state = commanded;
                    _fade = 0f;
                }

                i += Channels;
            }
        }

        /// <summary>
        /// One sine sample at the current frequency and smoothed amplitude,
        /// advancing phase. Audio thread only.
        /// </summary>
        private float NextToneSample(uint sampleRate)
        {
            // Smooth amplitude toward the target so live level changes don't
            // produce zipper noise.
            float target = _ampTarget;
            _amp += (target - _amp) * AmpSmooth;

            float s = (float)(Math.Sin(_phase) * _amp);
            _phase += 2.0 * Math.PI * _frequency / sampleRate;
            if (_phase >= 2.0 * Math.PI) _phase -= 2.0 * Math.PI;
            return s;
        }
    }
}
