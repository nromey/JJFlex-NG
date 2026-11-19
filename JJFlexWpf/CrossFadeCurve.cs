using System;

namespace JJFlexWpf
{
    /// <summary>
    /// THE ramp (#199's ruling, 2026-10-18: "SilkTune and that fade share one
    /// ramp primitive, not two"; #873). One pair of gains for every fade
    /// between two signals, parameterised by how alike the two are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>r = 0 is the equal-power pair the app already had</b>, sample for
    /// sample: the band's rising sine in <see cref="RxAudioPipeline.GlideGain"/>
    /// and the recording's falling cosine in <c>RecordingPlayer</c> (bench
    /// 2026-10-25, Noel: "perfect"). Both now ask this class, with r = 0, and get
    /// the same float arithmetic they always did.
    /// </para>
    /// <para>
    /// <b>For two signals that are alike</b> (two noise engines' outputs lined
    /// up to the sample, r measured between them), equal power is wrong: two
    /// correlated signals add in amplitude, so the sine and cosine bump about
    /// 3 dB in the middle. The correlation-matched pair (Fink, Holters and
    /// Zölzer, DAFx 2016, signal-matched power-complementary cross-fading) keeps
    /// out² + in² + 2r·out·in = 1 throughout, which is constant power for
    /// signals of correlation r. It is the same sine and cosine divided by one
    /// normaliser, so at r = 1 the pair sums to exactly 1 (a linear fade in
    /// effect) and at r = 0 the normaliser is exactly 1.
    /// </para>
    /// <para>No allocation, no state: safe on the audio thread.</para>
    /// </remarks>
    public static class CrossFadeCurve
    {
        /// <summary>
        /// The rising gain at <paramref name="t"/> (0 to 1) for a fade between two
        /// signals of correlation <paramref name="r"/> (clamped to 0 to 1; a
        /// negative correlation is treated as none).
        /// </summary>
        public static float In(float t, float r = 0f)
        {
            float s = MathF.Sin(t * MathF.PI / 2f);
            if (!(r > 0f)) return s;
            float c = MathF.Cos(t * MathF.PI / 2f);
            return s / MathF.Sqrt(1f + 2f * MathF.Min(r, 1f) * s * c);
        }

        /// <summary>The falling gain at <paramref name="t"/>, the partner of <see cref="In"/> at the same t and r.</summary>
        public static float Out(float t, float r = 0f)
        {
            float c = MathF.Cos(t * MathF.PI / 2f);
            if (!(r > 0f)) return c;
            float s = MathF.Sin(t * MathF.PI / 2f);
            return c / MathF.Sqrt(1f + 2f * MathF.Min(r, 1f) * s * c);
        }
    }
}
