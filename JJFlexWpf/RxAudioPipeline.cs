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

        /// <summary>
        /// Create the pipeline for the given audio format.
        /// Both providers are created in standalone mode (no ISampleProvider source).
        /// </summary>
        public RxAudioPipeline(int sampleRate = 48000, int channels = 2)
        {
            _channels = channels;
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

        /// <summary>Start sampling noise. Call when the band is quiet.</summary>
        public void StartNoiseSampling(int durationSeconds = 2)
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
        }

        public void Dispose()
        {
            _rnnoise.Dispose();
            Trace.WriteLine("RxAudioPipeline: disposed");
        }
    }
}
