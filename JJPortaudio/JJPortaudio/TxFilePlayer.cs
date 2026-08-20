using System;

namespace JJPortaudio
{
    /// <summary>
    /// Plays a known recording down the transmit path in place of the
    /// microphone (Sprint 33 Track I).
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS. Every transmit-audio measurement taken up to now has
    /// been of a human talking into a microphone, and a human is a different
    /// signal every time — different level, different distance, different
    /// words, different timing. Two such measurements cannot be compared, so
    /// "did that change help?" has never had an evidence-based answer. A known
    /// file removes the variable: the same audio, every run, forever.
    /// </para>
    /// <para>
    /// WHERE IT SITS, AND WHY THAT MATTERS. It occupies the same injection
    /// slot as <see cref="TxToneGenerator"/> — the PortAudio input callback,
    /// ahead of the conditioning chain, the loudness meter and the Opus
    /// encoder. That is the exact point the microphone reaches. A file played
    /// anywhere else (a virtual audio cable, a media player, the radio's own
    /// voice keyer) travels a DIFFERENT chain than the one under test, and
    /// measuring a different chain is worse than not measuring, because the
    /// numbers still look like answers.
    /// </para>
    /// <para>
    /// Because it is not a calibrated reference tone but a voice, it does NOT
    /// bypass the conditioning chain — see
    /// <see cref="BypassesConditioning"/>. The gate and the noise reducer run
    /// on it exactly as they run on speech, which is the whole point.
    /// </para>
    /// <para>
    /// Replacement, never mixing, is inherited from the tone generator's
    /// contract and matters more here: a reference file summed with live room
    /// audio is no longer a reference file. The same six-state ramp machine
    /// keeps the swap click-free (about 10 ms per ramp), and the same
    /// stream-gap detection restarts cleanly when the transmit stream stops at
    /// unkey and starts again at the next key-down.
    /// </para>
    /// <para>
    /// Samples are held as MONO and duplicated to both channels, which is how
    /// the transmit stream carries a mono microphone. The player will resample
    /// by linear interpolation if it is handed a stream rate its content does
    /// not match, but that is a fallback with an audible cost: load content at
    /// the stream's rate (the app layer resamples properly when loading) and
    /// this path never runs.
    /// </para>
    /// <para>
    /// Thread model matches the tone generator's: Process runs on the
    /// PortAudio callback thread, every setter runs on a UI thread, shared
    /// fields are volatile, and there are no locks in the audio path. Loading
    /// swaps a whole array in one reference store, so the audio thread either
    /// sees the old content or the new one, never a half-filled buffer.
    /// </para>
    /// </remarks>
    public class TxFilePlayer : ITxInputSource
    {
        // States, mirroring TxToneGenerator so the two behave identically at
        // the swap boundary. The audio thread advances fades forward; the UI
        // thread only requests engage (-> MicFadeOut) or release (-> FileFadeOut).
        private const int StIdle = 0;        // mic passes untouched
        private const int StMicFadeOut = 1;  // mic ramping to silence (mic only)
        private const int StFileFadeIn = 2;  // file ramping up (mic discarded)
        private const int StFile = 3;        // file at level (mic discarded)
        private const int StFileFadeOut = 4; // file ramping down (mic discarded)
        private const int StMicFadeIn = 5;   // mic ramping back (mic only)

        private const float FadeSeconds = 0.010f; // 10 ms ramps
        private const long StreamGapMs = 100;     // gap => stream was stopped
        // The TX input stream is opened stereo interleaved; identical samples
        // go to both channels, as a mono microphone does.
        private const int Channels = 2;

        private volatile int _state = StIdle;

        /// <summary>The loaded content. Swapped whole; never mutated in place.</summary>
        private volatile float[] _samples = Array.Empty<float>();
        private volatile int _contentRate = 48000;

        // Playback position as a double so a rate mismatch can interpolate.
        // Audio thread only.
        private double _pos;
        private float _fade;
        private long _lastProcessMs;

        private volatile bool _loop;
        private volatile float _gain = 1f;
        private volatile bool _reachedEnd;
        private volatile int _playedFrames;

        /// <summary>
        /// A short human-readable name for whatever is loaded, so a surface
        /// can say what it is about to transmit without holding the path.
        /// Empty when nothing is loaded.
        /// </summary>
        public string ContentName { get; private set; } = "";

        /// <summary>Seconds of content loaded; zero when nothing is loaded.</summary>
        public double ContentSeconds
        {
            get
            {
                float[] s = _samples;
                int rate = _contentRate;
                return (s.Length == 0 || rate <= 0) ? 0.0 : (double)s.Length / rate;
            }
        }

        /// <summary>Sample rate the loaded content is held at.</summary>
        public int ContentSampleRate => _contentRate;

        /// <summary>True when content is loaded and ready to play.</summary>
        public bool HasContent => _samples.Length > 0;

        /// <summary>
        /// Play the content over and over rather than stopping at the end.
        /// Useful for a long adjustment session; off by default, because a
        /// measurement wants one known pass, not an unknown number of them.
        /// </summary>
        public bool Loop
        {
            get { return _loop; }
            set { _loop = value; }
        }

        /// <summary>
        /// Playback trim in dB, default 0 — content plays at exactly the level
        /// it was stored at.
        /// </summary>
        /// <remarks>
        /// Deliberately defaulted to no change. The level of a reference file
        /// is part of what makes it a reference; a player that quietly
        /// normalises would make the same file measure differently on two
        /// machines. The trim exists for the case where an operator's own
        /// recording came out quiet and they want to hear it through the chain
        /// anyway, and it is reported wherever it is non-zero.
        /// </remarks>
        public float TrimDb
        {
            get { return (float)(20.0 * Math.Log10(Math.Max(_gain, 1e-6f))); }
            set
            {
                float db = Math.Clamp(value, -40f, 20f);
                _gain = (float)Math.Pow(10.0, db / 20.0);
            }
        }

        /// <summary>
        /// True once playback has run off the end of the content. Cleared by
        /// <see cref="Start"/>. A UI polls this to announce that the pass
        /// finished rather than having to time it.
        /// </summary>
        public bool ReachedEnd => _reachedEnd;

        /// <summary>Frames played since the last <see cref="Start"/>.</summary>
        public int PlayedFrames => _playedFrames;

        /// <summary>Seconds played since the last <see cref="Start"/>.</summary>
        public double PlayedSeconds
        {
            get
            {
                int rate = _contentRate;
                return rate > 0 ? (double)_playedFrames / rate : 0.0;
            }
        }

        /// <inheritdoc/>
        public bool Engaged
        {
            get
            {
                int s = _state;
                return s == StMicFadeOut || s == StFileFadeIn || s == StFile;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Always false. A voice file must travel the conditioning chain, or
        /// it is measuring a chain the operator's voice never uses.
        /// </remarks>
        public bool BypassesConditioning => false;

        /// <summary>
        /// Hand the player its content: mono samples in the range −1..1, the
        /// rate they are held at, and a name to say out loud.
        /// </summary>
        /// <remarks>
        /// Loading while playing is allowed and stops playback first — the
        /// alternative is a swap mid-word, which sounds like a fault.
        /// </remarks>
        public void Load(float[] monoSamples, int sampleRate, string name)
        {
            Stop();
            _samples = monoSamples ?? Array.Empty<float>();
            _contentRate = sampleRate > 0 ? sampleRate : 48000;
            ContentName = name ?? "";
            _pos = 0.0;
            _reachedEnd = false;
            _playedFrames = 0;
        }

        /// <summary>Forget the content. Playback stops first.</summary>
        public void Unload()
        {
            Stop();
            _samples = Array.Empty<float>();
            ContentName = "";
            _pos = 0.0;
            _reachedEnd = false;
            _playedFrames = 0;
        }

        /// <summary>
        /// Engage playback from the beginning: the mic ramps out, then the
        /// file ramps in. If transmit audio is not flowing yet, playback
        /// simply starts clean at the next key-down with no mic bleed at all.
        /// Does nothing when no content is loaded.
        /// </summary>
        public void Start()
        {
            if (_samples.Length == 0) return;
            int s = _state;
            if (s == StMicFadeOut || s == StFileFadeIn || s == StFile) return;
            _pos = 0.0;
            _reachedEnd = false;
            _playedFrames = 0;
            _state = StMicFadeOut;
        }

        /// <summary>
        /// Release playback: the file ramps out, then the mic ramps back in.
        /// If transmit audio is not flowing, the release resolves instantly at
        /// the next stream start.
        /// </summary>
        public void Stop()
        {
            int s = _state;
            if (s == StIdle || s == StFileFadeOut || s == StMicFadeIn) return;
            _state = StFileFadeOut;
        }

        /// <summary>
        /// Called from the PortAudio input callback with one Opus frame of
        /// interleaved stereo mic samples, before the Opus encode. Depending
        /// on state this passes the buffer untouched, ramps it, or overwrites
        /// it entirely with the file.
        /// </summary>
        public void Process(float[] buffer, int count, uint sampleRate)
        {
            // Stamp the clock on EVERY call, idle passthrough included, or an
            // engage while mic audio is flowing reads as a cold stream start
            // and hard-cuts the mic instead of fading it. Same lesson the tone
            // generator learned from the numerical harness, 2026-08-11.
            long now = Environment.TickCount64;
            long last = _lastProcessMs;
            _lastProcessMs = now;

            int state = _state;
            if (state == StIdle) return;

            float[] samples = _samples;
            if (samples.Length == 0)
            {
                // Content vanished under us. Let the mic back rather than
                // transmitting a buffer of whatever was there.
                _state = StIdle;
                return;
            }

            // Stream-gap detection: if the input stream stopped at unkey and
            // has just restarted, there is no established mic audio to ramp
            // against and nothing in flight — resolve transition states now
            // and restart engaged playback from silence.
            bool cold = (now - last) > StreamGapMs;
            if (cold)
            {
                if (state == StMicFadeOut || state == StFileFadeIn || state == StFile)
                {
                    // Engaged across a stream stop: start the file AGAIN from
                    // the beginning rather than resuming where it was. An
                    // operator who unkeyed and keyed again wants another pass,
                    // and a pass that picks up mid-word is not a repeatable
                    // stimulus — which is the only reason a known file exists.
                    state = StFileFadeIn;
                    _fade = 0f;
                    _pos = 0.0;
                    _reachedEnd = false;
                    _playedFrames = 0;
                    _state = state;
                }
                else
                {
                    _state = StIdle;
                    return;
                }
            }

            if (sampleRate == 0) sampleRate = 48000;
            float fadeStep = 1f / (FadeSeconds * sampleRate); // per frame
            // Advance through the content at whatever ratio the stream rate
            // demands. Normally exactly 1.0 — the loader resamples properly.
            double step = (double)_contentRate / sampleRate;
            float gain = _gain;
            int frames = count / Channels;
            int i = 0;
            int played = _playedFrames;

            for (int f = 0; f < frames; f++)
            {
                switch (state)
                {
                    case StMicFadeOut:
                        {
                            // Mic only, ramping down. No file yet — never mixed.
                            float g = 1f - _fade;
                            buffer[i] *= g;
                            buffer[i + 1] *= g;
                            _fade += fadeStep;
                            if (_fade >= 1f)
                            {
                                state = StFileFadeIn;
                                _state = state;
                                _fade = 0f;
                            }
                        }
                        break;

                    case StFileFadeIn:
                        {
                            float s = NextSample(samples, step, gain, ref played);
                            s *= _fade;
                            buffer[i] = s;
                            buffer[i + 1] = s;
                            _fade += fadeStep;
                            if (_fade >= 1f)
                            {
                                state = StFile;
                                _state = state;
                            }
                            if (_reachedEnd) { state = StFileFadeOut; _state = state; _fade = 0f; }
                        }
                        break;

                    case StFile:
                        {
                            float s = NextSample(samples, step, gain, ref played);
                            buffer[i] = s;
                            buffer[i + 1] = s;
                            if (_reachedEnd) { state = StFileFadeOut; _state = state; _fade = 0f; }
                        }
                        break;

                    case StFileFadeOut:
                        {
                            // Past the end there is nothing left to fade, so
                            // ramp silence rather than reading off the array.
                            float s = _reachedEnd ? 0f : NextSample(samples, step, gain, ref played);
                            s *= (1f - _fade);
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
                            // File gone; mic ramping back. Mic only — never mixed.
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
                        _playedFrames = played;
                        return;
                }
                i += Channels;
            }

            _playedFrames = played;
        }

        /// <summary>
        /// One mono sample from the content, advancing the position. Linear
        /// interpolation covers a rate mismatch; at the normal 1:1 ratio it
        /// degenerates to a plain array read.
        /// </summary>
        private float NextSample(float[] samples, double step, float gain, ref int played)
        {
            double p = _pos;
            int n = samples.Length;

            if (p >= n)
            {
                if (_loop)
                {
                    p -= n;
                    if (p < 0 || p >= n) p = 0.0;
                }
                else
                {
                    _reachedEnd = true;
                    _pos = n;
                    return 0f;
                }
            }

            int idx = (int)p;
            float a = samples[idx];
            int next = idx + 1;
            float b;
            if (next < n) b = samples[next];
            else if (_loop) b = samples[0];
            else b = a;
            float frac = (float)(p - idx);
            float s = a + (b - a) * frac;

            _pos = p + step;
            played++;
            return s * gain;
        }
    }
}
