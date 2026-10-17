using System;
using System.Diagnostics;
using RNNoise.NET;

namespace JJFlexWpf
{
    /// <summary>
    /// How hot the samples are when they reach RNNoise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found 2026-10-17 by decompiling the wrapper (Sprint 48 Track G).</b>
    /// RNNoise.NET's <c>Denoiser.Denoise</c> multiplies every sample by 32767
    /// before calling <c>rnnoise_process_frame</c> and by 1/32767 after, so it
    /// already takes floats in [-1, 1]. The app ALSO multiplied by 32767 before
    /// calling it, ever since Sprint 25, so RNNoise has been receiving audio
    /// 32767 times (about 90 dB) hotter than the 16-bit range it was trained on.
    /// </para>
    /// <para>
    /// <see cref="Historical"/> is what the app does and keeps doing until the
    /// operator has heard the difference and ruled on it: changing it changes
    /// the sound of receive AND transmit noise reduction for everyone, and
    /// nothing has been listened to yet. <see cref="Corrected"/> exists so the
    /// offline bench can render both.
    /// </para>
    /// </remarks>
    public enum RnnoiseScaling
    {
        /// <summary>The app's level since Sprint 25: scaled by 32767 here AND inside the wrapper.</summary>
        Historical,

        /// <summary>Scaled once, by the wrapper only: the 16-bit range RNNoise expects.</summary>
        Corrected,
    }

    /// <summary>
    /// RNNoise (Xiph, through the RNNoise.NET wrapper) as a mono engine:
    /// 480-sample frames, 10 ms at 48 kHz.
    /// </summary>
    /// <remarks>
    /// Moved out of <see cref="NoiseReductionProvider"/> in Sprint 48 Track G
    /// with its arithmetic unchanged: each frame is scaled, denoised in place
    /// and scaled back exactly as the provider did it, so for any one channel
    /// the output is the same as before. What changed is around it: there is
    /// now one of these per channel instead of one for channel 0.
    /// </remarks>
    public sealed class RnnoiseEngine : INeuralDenoiseEngine
    {
        /// <summary>RNNoise's fixed frame: 10 ms at 48 kHz.</summary>
        public const int Frame = 480;

        private const float HistoricalScale = 32767f;

        private readonly RnnoiseScaling _scaling;
        private readonly float[] _scratch = new float[Frame];
        private Denoiser? _denoiser;

        public RnnoiseEngine(RnnoiseScaling scaling = RnnoiseScaling.Historical)
        {
            _scaling = scaling;
            _denoiser = TryCreate();
        }

        public string Id => _scaling == RnnoiseScaling.Historical ? "rnnoise" : "rnnoise-corrected";

        public int SampleRate => 48000;

        public int FrameLength => Frame;

        public bool IsAvailable => _denoiser != null;

        /// <summary>
        /// 480 samples (10 ms): RNNoise analyses a 20 ms window every 10 ms
        /// and its output is one frame behind its input. Measured, not just
        /// quoted: <c>EngineLatencyTests</c> finds it by cross-correlation, and
        /// tools/nrbench reports the same 10.0 ms.
        /// </summary>
        public int LatencySamples => Frame;

        /// <summary>Which input level this instance feeds RNNoise.</summary>
        public RnnoiseScaling Scaling => _scaling;

        public void ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            var d = _denoiser;
            if (d == null)
                throw new InvalidOperationException("RNNoise is not available.");
            if (input.Length != Frame || output.Length != Frame)
                throw new ArgumentException($"RNNoise frames are exactly {Frame} samples.");

            // The same three steps NoiseReductionProvider took, in the same
            // order, so the floats come out bit-for-bit as they used to.
            if (_scaling == RnnoiseScaling.Historical)
            {
                for (int i = 0; i < Frame; i++)
                    _scratch[i] = input[i] * HistoricalScale;
                d.Denoise(new Span<float>(_scratch));
                for (int i = 0; i < Frame; i++)
                    output[i] = _scratch[i] / HistoricalScale;
            }
            else
            {
                input.CopyTo(_scratch);
                d.Denoise(new Span<float>(_scratch));
                _scratch.AsSpan().CopyTo(output);
            }
        }

        public void Reset()
        {
            _denoiser?.Dispose();
            _denoiser = TryCreate();
        }

        public void Dispose()
        {
            _denoiser?.Dispose();
            _denoiser = null;
        }

        private static Denoiser? TryCreate()
        {
            try
            {
                return new Denoiser();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"RnnoiseEngine: RNNoise init failed: {ex.Message}");
                return null;
            }
        }
    }
}
