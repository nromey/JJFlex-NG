using System;

namespace Radios
{
    /// <summary>
    /// The state one transmission accumulates for the reflected-power rule: how
    /// much forward power this transmission has actually managed to make, and
    /// how many judgeable samples in a row have been bad.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the peak is measured rather than taken from the power setting
    /// (#453).</b> The floor below which a reflected share means nothing has to
    /// scale with the transmission, because an absolute one watt excludes
    /// almost nothing on a hundred-watt voice envelope — the envelope crosses
    /// one watt constantly on its way down between syllables.
    /// </para>
    /// <para>
    /// The obvious reference is the operator's SET power, and it is the wrong
    /// one. A Flex folds its power back when it sees a bad match: on 2026-08-22
    /// the bench 8600 made <b>101.2 W</b> into a properly connected dummy load
    /// and only <b>17.5 W</b> minutes earlier into an empty antenna port at the
    /// same setting. A floor derived from a hundred-watt SETTING would sit
    /// above everything a severely mismatched station can produce, and the
    /// alarm would go quiet in precisely the case it exists for. The share of a
    /// measured peak scales down with the foldback and keeps working.
    /// </para>
    /// <para>
    /// <b>Why the streak counts JUDGEABLE samples rather than ticks.</b> A
    /// voice envelope hands over two consecutive samples of anything for free,
    /// which is why the pre-existing "the warning fired on an earlier tick, the
    /// cut reads this one" rule was no defence here. But a plain
    /// consecutive-ticks rule fails the other way: with the floor set near the
    /// envelope peaks, most ticks are not judgeable at all, so a run of
    /// consecutive bad TICKS would almost never accumulate and the alarm would
    /// never fire. So a sample that cannot be judged — an incoherent pair, or
    /// forward power below the floor — is SKIPPED: it neither advances the
    /// streak nor resets it. Only a judgeable good sample resets it, because
    /// only a judgeable good sample is evidence that the antenna is fine.
    /// </para>
    /// <para>
    /// One of these belongs to one transmission. <see cref="Reset"/> it
    /// wherever the once-per-transmission warning flag is reset, or a previous
    /// transmission's peak sets the floor for this one.
    /// </para>
    /// </remarks>
    public sealed class ReflectedPowerRun
    {
        /// <summary>
        /// The highest forward power seen this transmission, in watts.
        /// </summary>
        public float ForwardPeakWatts { get; private set; }

        /// <summary>
        /// Judgeable samples in a row whose reflected share was over the
        /// threshold.
        /// </summary>
        public int BadSamples { get; private set; }

        /// <summary>
        /// Judgeable samples seen this transmission, good and bad. Zero after a
        /// whole transmission means the rule never got to look at anything —
        /// which is a fact worth being able to state, since a guard that
        /// declined to judge looks exactly like a guard that saw nothing wrong.
        /// </summary>
        public int JudgedSamples { get; private set; }

        /// <summary>
        /// Samples this transmission that could not be judged because forward
        /// and reflected were not one sample. Persistently non-zero means the
        /// radio is not delivering the two meters together and the alarm is not
        /// working — trace it rather than leaving it silent.
        /// </summary>
        public int IncoherentSamples { get; private set; }

        /// <summary>
        /// The power below which a reflected share means nothing on THIS
        /// transmission.
        /// </summary>
        public float FloorWatts => TransmitSafety.ReflectedWarnFloorWatts(ForwardPeakWatts);

        /// <summary>
        /// Whether enough judgeable samples in a row have been bad to believe
        /// them.
        /// </summary>
        public bool Sustained => BadSamples >= TransmitSafety.ReflectedWarnSustainedSamples;

        /// <summary>Start a fresh transmission.</summary>
        public void Reset()
        {
            ForwardPeakWatts = 0f;
            BadSamples = 0;
            JudgedSamples = 0;
            IncoherentSamples = 0;
        }

        /// <summary>
        /// Fold one reading in. Call once per meter poll, before asking
        /// <see cref="TransmitSafety.ShouldWarnReflected"/> anything.
        /// </summary>
        /// <returns>
        /// True when this reading was judgeable — for a caller that wants to
        /// trace the difference between "watched and found nothing" and "never
        /// got to look".
        /// </returns>
        public bool Observe(in TransmitPowerReading reading)
        {
            if (!reading.IsCoherent)
            {
                IncoherentSamples++;
                return false;
            }

            // The peak grows from coherent readings only. An incoherent pair's
            // forward number may be perfectly good, but taking it here would
            // mean the floor is set by data the rule has already refused to
            // trust, and there is no need: the peak is reached many times a
            // second on speech.
            if (reading.ForwardWatts > ForwardPeakWatts)
                ForwardPeakWatts = reading.ForwardWatts;

            if (reading.ForwardWatts < FloorWatts) return false;

            float share = reading.ReflectedShare;
            if (float.IsNaN(share)) return false;

            JudgedSamples++;
            if (share > TransmitSafety.ReflectedWarnFraction) BadSamples++;
            else BadSamples = 0;
            return true;
        }

        public override string ToString() =>
            "peak " + ForwardPeakWatts.ToString("F1") + " W, floor "
            + FloorWatts.ToString("F1") + " W, " + JudgedSamples + " judged, "
            + BadSamples + " bad in a row, " + IncoherentSamples + " not one sample";
    }
}
