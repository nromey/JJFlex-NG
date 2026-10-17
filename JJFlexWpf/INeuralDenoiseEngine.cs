using System;

namespace JJFlexWpf
{
    /// <summary>
    /// A mono, fixed-frame neural noise reducer: one channel in, one channel
    /// out, exactly <see cref="FrameLength"/> samples at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Mono on purpose (#732).</b> Every engine behind this interface sees
    /// ONE channel. Stereo is the job of <see cref="PerChannelDenoiserBank"/>,
    /// which holds one engine per channel, so a slice panned right is cleaned
    /// as itself and never replaced by the left channel. The providers used to
    /// fold to channel 0 and copy the result everywhere; making the engine
    /// mono-only is what keeps that from coming back through a new engine.
    /// </para>
    /// <para>
    /// <b>Samples are floats in [-1, 1].</b> An engine that wants another
    /// range (RNNoise works in the 16-bit range) does its own scaling inside
    /// <see cref="ProcessFrame"/>; callers never scale for an engine.
    /// </para>
    /// <para>
    /// <b>Engines hold state between frames</b> (that is how they learn the
    /// noise), so one instance must only ever be fed one continuous channel.
    /// Never alternate two sources through one instance.
    /// </para>
    /// <para>
    /// Not an operator surface. Which engine the app uses is decided in code;
    /// nothing here is a setting.
    /// </para>
    /// </remarks>
    public interface INeuralDenoiseEngine : IDisposable
    {
        /// <summary>Short stable identifier, for traces and the offline bench ("rnnoise", "deepfilternet3").</summary>
        string Id { get; }

        /// <summary>The only sample rate the engine works at, in Hz.</summary>
        int SampleRate { get; }

        /// <summary>Samples per frame. <see cref="ProcessFrame"/> takes exactly this many.</summary>
        int FrameLength { get; }

        /// <summary>
        /// False when the engine could not start: its native library is
        /// missing or failed to load, or its model would not initialise. An
        /// unavailable engine must not be asked to process.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Denoise one frame. Both spans are exactly <see cref="FrameLength"/>
        /// long, samples in [-1, 1]. Input and output may not overlap.
        /// </summary>
        void ProcessFrame(ReadOnlySpan<float> input, Span<float> output);

        /// <summary>Forget everything learned so far, as if newly created.</summary>
        void Reset();
    }
}
