using System;
using System.Diagnostics;
using NAudio.Wave;
using RNNoise.NET;

namespace JJFlexWpf
{
    /// <summary>
    /// ISampleProvider wrapper that applies RNNoise neural noise reduction.
    /// Processes 480-sample frames (10ms at 48kHz) through RNNoise.
    /// Includes wet/dry mix control via Strength property.
    /// Sprint 25 Phase 10.
    /// </summary>
    public class NoiseReductionProvider : ISampleProvider, IDisposable
    {
        private readonly ISampleProvider _source;
        private Denoiser? _denoiser;
        private readonly float[] _frameBuffer;
        private readonly float[] _originalBuffer;

        // RNNoise expects 480-sample frames (10ms at 48kHz)
        private const int FrameSize = 480;

        /// <summary>Whether noise reduction is active.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Wet/dry mix: 0.0 = original signal only, 1.0 = fully processed.
        /// Default 0.8 to preserve some signal character.
        /// </summary>
        public float Strength { get; set; } = 0.8f;

        /// <summary>
        /// When true, auto-disables NR in CW/digital modes (RNNoise is speech-trained).
        /// </summary>
        public bool AutoDisableNonVoice { get; set; } = true;

        /// <summary>Current mode from radio — set externally to control auto-disable.</summary>
        public string CurrentMode { get; set; } = "";

        public WaveFormat WaveFormat => _source.WaveFormat;

        public NoiseReductionProvider(ISampleProvider source)
        {
            _source = source;
            _frameBuffer = new float[FrameSize];
            _originalBuffer = new float[FrameSize];

            try
            {
                _denoiser = new Denoiser();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"NoiseReductionProvider: RNNoise init failed: {ex.Message}");
                _denoiser = null;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int totalRead = _source.Read(buffer, offset, count);

            // Pass through if disabled, no denoiser, or auto-disabled for non-voice modes
            if (!Enabled || _denoiser == null || totalRead == 0)
                return totalRead;

            if (AutoDisableNonVoice && IsNonVoiceMode(CurrentMode))
                return totalRead;

            int channels = WaveFormat.Channels;
            ProcessSamples(buffer, offset, totalRead, channels);

            return totalRead;
        }

        private void ProcessSamples(float[] buffer, int offset, int count, int channels)
        {
            int sampleIdx = offset;
            int remaining = count;

            // Process full frames
            while (remaining >= FrameSize * channels)
            {
                // Extract mono samples (channel 0) and scale to RNNoise range
                for (int i = 0; i < FrameSize; i++)
                {
                    int srcIdx = sampleIdx + i * channels;
                    float sample = srcIdx < offset + count ? buffer[srcIdx] : 0f;
                    _originalBuffer[i] = sample;
                    _frameBuffer[i] = sample * 32767f;
                }

                // Process in-place through RNNoise
                _denoiser.Denoise(new Span<float>(_frameBuffer));

                // Scale back and write with wet/dry blend
                for (int i = 0; i < FrameSize; i++)
                {
                    float processed = _frameBuffer[i] / 32767f;
                    float blended = _originalBuffer[i] * (1f - Strength) + processed * Strength;
                    int dstIdx = sampleIdx + i * channels;
                    if (dstIdx < offset + count)
                    {
                        buffer[dstIdx] = blended;
                        // Copy to other channels if stereo
                        for (int ch = 1; ch < channels && dstIdx + ch < offset + count; ch++)
                            buffer[dstIdx + ch] = blended;
                    }
                }

                sampleIdx += FrameSize * channels;
                remaining -= FrameSize * channels;
            }

            // Remaining samples less than a full frame pass through unprocessed
        }

        private static bool IsNonVoiceMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return false;
            return mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase) ||
                   mode.StartsWith("DIG", StringComparison.OrdinalIgnoreCase) ||
                   mode.StartsWith("FDM", StringComparison.OrdinalIgnoreCase) ||
                   mode.Equals("RTTY", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            _denoiser?.Dispose();
            _denoiser = null;
        }
    }
}
