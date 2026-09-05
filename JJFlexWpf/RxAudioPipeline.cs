using System;
using System.Diagnostics;

namespace JJFlexWpf
{
    /// <summary>
    /// PC-side audio processing pipeline for RX audio.
    /// Chains processing stages between Opus decode and PortAudio playback:
    ///   Decoded PCM → SpectralSubtraction → RNNoise → PortAudio queue
    ///
    /// Works on ALL radios (6000/8000/Aurora) since processing runs on the PC,
    /// unlike radio-side NR which requires 8000/Aurora hardware DSP.
    ///
    /// Designed as the single insertion point for all PC-side audio processing.
    /// Future stages (waterfall FFT tap, recording tap, DSP filters) plug in here.
    ///
    /// Thread safety: Process() is called from FlexBase's remoteAudioProc thread.
    /// Property setters are called from the UI thread. Properties use volatile reads
    /// to avoid torn values — no locks needed since each property is a single word.
    /// Sprint 25 Phase 20.
    /// </summary>
    public class RxAudioPipeline : IDisposable
    {
        private readonly NoiseReductionProvider _rnnoise;
        private readonly SpectralSubtractionProvider _spectralSub;
        private readonly int _channels;

        /// <summary>Kept for the duck's ramp, which has to convert a time
        /// constant into a per-frame increment.</summary>
        private readonly int _sampleRate;

        /// <summary>
        /// Create the pipeline for the given audio format.
        /// Both providers are created in standalone mode (no ISampleProvider source).
        /// </summary>
        public RxAudioPipeline(int sampleRate = 48000, int channels = 2)
        {
            _channels = channels;
            _sampleRate = sampleRate;
            _rnnoise = new NoiseReductionProvider(sampleRate, channels);
            _spectralSub = new SpectralSubtractionProvider(sampleRate, channels);

            Trace.WriteLine($"RxAudioPipeline: created ({sampleRate}Hz, {channels}ch)");
        }

        // --- RNNoise properties ---

        /// <summary>Enable/disable PC-side neural noise reduction (RNNoise).</summary>
        public bool RnnEnabled
        {
            get => _rnnoise.Enabled;
            set => _rnnoise.Enabled = value;
        }

        /// <summary>RNNoise wet/dry mix: 0.0 = bypass, 1.0 = fully processed. Default 0.8.</summary>
        public float RnnStrength
        {
            get => _rnnoise.Strength;
            set => _rnnoise.Strength = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>Auto-disable RNNoise for CW/digital modes (it's speech-trained).</summary>
        public bool RnnAutoDisableNonVoice
        {
            get => _rnnoise.AutoDisableNonVoice;
            set => _rnnoise.AutoDisableNonVoice = value;
        }

        // --- Spectral subtraction properties ---

        /// <summary>Enable/disable PC-side spectral subtraction.</summary>
        public bool SpectralEnabled
        {
            get => _spectralSub.Enabled;
            set => _spectralSub.Enabled = value;
        }

        /// <summary>Subtraction aggressiveness: 0.0 = none, 1.0 = max. Default 0.7.</summary>
        public float SpectralStrength
        {
            get => _spectralSub.Strength;
            set => _spectralSub.Strength = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>Spectral floor to prevent musical noise artifacts. Default 0.02.</summary>
        public float SpectralFloor
        {
            get => _spectralSub.SpectralFloor;
            set => _spectralSub.SpectralFloor = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>True while actively sampling noise profile.</summary>
        public bool IsNoiseSampling => _spectralSub.IsSampling;

        /// <summary>Noise sampling progress 0.0-1.0.</summary>
        public float NoiseSamplingProgress => _spectralSub.SamplingProgress;

        /// <summary>True if a noise profile has been captured.</summary>
        public bool HasNoiseProfile => _spectralSub.HasProfile;

        /// <summary>Name of the loaded noise profile.</summary>
        public string NoiseProfileName => _spectralSub.ProfileName;

        // --- Mode tracking ---

        /// <summary>
        /// Set the current radio mode (SSB, CW, etc.) so RNNoise can
        /// auto-disable for non-voice modes. Called by the UI when mode changes.
        /// </summary>
        public void SetCurrentMode(string mode)
        {
            _rnnoise.CurrentMode = mode ?? "";
        }

        // --- Noise profile management ---

        /// <summary>Start sampling noise. Call when the band is quiet.
        /// Default 3 seconds (range 1-5) per the ratified capture-window decision.</summary>
        public void StartNoiseSampling(int durationSeconds = 3)
        {
            _spectralSub.StartSampling(durationSeconds);
        }

        /// <summary>Cancel an in-progress noise sampling session.</summary>
        public void CancelNoiseSampling()
        {
            _spectralSub.CancelSampling();
        }

        /// <summary>Load a noise profile from file.</summary>
        public bool LoadNoiseProfile(string filePath)
        {
            return _spectralSub.LoadProfile(filePath);
        }

        /// <summary>Save the current noise profile to file.</summary>
        public bool SaveNoiseProfile(string filePath, string name, string band = "", string antenna = "")
        {
            return _spectralSub.SaveProfile(filePath, name, band, antenna);
        }

        /// <summary>Clear the current noise profile.</summary>
        public void ClearNoiseProfile()
        {
            _spectralSub.ClearProfile();
        }

        // --- Core processing ---

        /// <summary>
        /// Process a decoded audio buffer in-place.
        /// Chain order: SpectralSubtraction first (removes band-specific noise floor),
        /// then RNNoise (neural cleanup of remaining signal).
        ///
        /// Called from FlexBase.remoteAudioProc thread via the PostDecodeProcessor delegate.
        /// </summary>
        public void Process(float[] buffer)
        {
            int count = buffer.Length;
            _spectralSub.ProcessInPlace(buffer, 0, count, _channels);
            _rnnoise.ProcessInPlace(buffer, 0, count, _channels);
            ApplyDuck(buffer, count);
        }

        // --- Warning duck (#116) ---
        //
        // Last in the chain on purpose. The noise reducers are level-sensitive
        // — spectral subtraction works against a measured noise floor, and
        // RNNoise was trained on speech at natural levels — so pulling the
        // signal down in front of them would move the floor they are working
        // against every time a warning sounded. The duck is a listening
        // adjustment, not part of the cleanup, so it goes after both.

        /// <summary>Where the duck gain is right now, glided per frame toward
        /// <see cref="RxDuck.TargetGain"/>.</summary>
        private float _duckGain = 1f;

        /// <summary>
        /// Ramp the buffer toward the duck's target gain.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Ramped, never stepped.</b> A hard gain change mid-buffer is a
        /// discontinuity, and a discontinuity is a click — precisely the
        /// artifact class this whole area of the app exists to remove. So the
        /// gain moves by a fixed increment per FRAME (not per sample, or a
        /// stereo stream would ramp twice as fast as a mono one).
        /// </para>
        /// <para>
        /// The increment is sized so a full excursion takes
        /// <see cref="RxDuck.AttackMs"/> going down and
        /// <see cref="RxDuck.ReleaseMs"/> coming back. Both are read here on
        /// every buffer rather than cached, because since #535 they follow the
        /// operator's timing preset live — and the release also grows with
        /// the depth, so a deeper dip comes back at the same RATE rather than
        /// in the same time. Changing the depth changes how deep it goes and
        /// how long it takes to come home, never how steep the return is.
        /// </para>
        /// <para>
        /// <b>It converges on 1.0 whenever nothing is asking for a duck</b>,
        /// including after a request expires, after ducking is switched off
        /// mid-duck, and after anything at all goes wrong upstream. There is
        /// no state that has to be unwound; the only way to stay attenuated is
        /// for something to keep asking.
        /// </para>
        /// </remarks>
        private void ApplyDuck(float[] buffer, int count)
        {
            float target = RxDuck.TargetGain;

            // Nothing to do: not ducking, and not still on the way home.
            if (target >= 0.9999f && _duckGain >= 0.9999f) return;

            int channels = Math.Max(_channels, 1);
            float span = 1f - RxDuck.DuckedGain;
            if (span < 0.0001f) span = 1f; // depth of zero: glide home promptly

            float ms = target < _duckGain ? RxDuck.AttackMs : RxDuck.ReleaseMs;
            float step = span / Math.Max(1f, _sampleRate * (ms / 1000f));

            for (int i = 0; i < count; i += channels)
            {
                if (_duckGain > target) _duckGain = Math.Max(target, _duckGain - step);
                else if (_duckGain < target) _duckGain = Math.Min(target, _duckGain + step);

                for (int c = 0; c < channels && i + c < count; c++)
                    buffer[i + c] *= _duckGain;
            }
        }

        public void Dispose()
        {
            _rnnoise.Dispose();
            Trace.WriteLine("RxAudioPipeline: disposed");
        }
    }
}
