namespace Radios
{
    /// <summary>
    /// The DIAGNOSTIC reflected-power ladder, stated in standing wave ratio:
    /// how bad the match is. Suspect, warn and abort, and every percentage of
    /// reflected power anywhere in the tree is derived from one of these three
    /// numbers rather than written as a literal of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ruled by Noel, 2026-08-26 (#237): SWR 2.5 suspect, 4 warn, 5 abort.</b>
    /// On 2026-11-02 he extended the warn rung to the live warning as well:
    /// "Yes, SWR 4 everywhere". So the warning heard while transmitting, the
    /// transmit chain check's power-coming-back rule and the transmit checks'
    /// tune probe all stand on this one ladder.
    /// </para>
    /// <para>
    /// <b>Why SWR is the stored unit and the percentage is derived.</b> The
    /// ladder used to be three round percentages, 20, 40 and 50, which in SWR
    /// terms are 2.62, 4.44 and 5.83. Nobody chose those; they fell out of
    /// picking round percentages, and hams do not reason in reflected percent.
    /// Stated as percentages, the tune probe's abort (50 percent, SWR 5.83)
    /// disagreed with its own SWR abort (5) and nobody could see it, because
    /// one number was in each unit. Stated in SWR, a future threshold is
    /// comparable by inspection instead of by algebra.
    /// IntegrationPassRuleTests.Every_reflected_power_threshold_derives_from_the_swr_ladder
    /// finds any reflected percentage that is not one of these rungs.
    /// </para>
    /// <para>
    /// <b>THERE ARE TWO LADDERS, WITH TWO JOBS AND TWO UNITS. Do not unify
    /// them.</b> This one is DIAGNOSTIC: a ratio, describing the antenna,
    /// feedline or tuner, which an operator acts on to fix the station. The
    /// other is PROTECTIVE, <see cref="TransmitSafety.ReflectedCutWatts"/>:
    /// absolute reflected watts, describing how much energy is landing in the
    /// finals as heat. The two can disagree, and both answers are right:
    /// 100 W at SWR 2.5 sends about 18 W back, over the 10 W cut, so a
    /// mediocre match at full power trips the protective cut; 10 W at SWR 5
    /// sends about 4.4 W back, under it, so a genuinely bad match at low power
    /// trips nothing protective, correctly, because 4.4 W is not hurting
    /// anything. Watts measure heat; ratio measures match. Neither ladder is
    /// ever to be stated in the other's unit.
    /// </para>
    /// <para>
    /// Any SWR judged against this ladder must be COMPUTED from forward and
    /// reflected power, never read from the radio's own SWR meter, which read
    /// 1.008 into an open antenna port on 2026-08-22.
    /// </para>
    /// </remarks>
    public static class SwrLadder
    {
        /// <summary>
        /// Suspect: worth reporting. Above where a real antenna runs at a
        /// band edge (2 to 1 is routine) and below where anything is
        /// genuinely wrong.
        /// </summary>
        public const double Suspect = 2.5;

        /// <summary>
        /// Warn: a round number a ham reads as "something is broken". The live
        /// reflected-power warning and the power-coming-back chain rule fire
        /// here.
        /// </summary>
        public const double Warn = 4.0;

        /// <summary>
        /// Abort: the transmit checks stop a tune carrier here. One number with
        /// one meaning; before the ruling the percent-side abort sat at 5.83.
        /// </summary>
        public const double Abort = 5.0;

        /// <summary><see cref="Suspect"/> as reflected percent of forward, about 18.4.</summary>
        public const double SuspectReflectedPercent =
            100.0 * ((Suspect - 1.0) / (Suspect + 1.0)) * ((Suspect - 1.0) / (Suspect + 1.0));

        /// <summary><see cref="Warn"/> as reflected percent of forward, 36.</summary>
        public const double WarnReflectedPercent =
            100.0 * ((Warn - 1.0) / (Warn + 1.0)) * ((Warn - 1.0) / (Warn + 1.0));

        /// <summary><see cref="Abort"/> as reflected percent of forward, about 44.4.</summary>
        public const double AbortReflectedPercent =
            100.0 * ((Abort - 1.0) / (Abort + 1.0)) * ((Abort - 1.0) / (Abort + 1.0));

        /// <summary>
        /// The share of forward power that comes back at a given SWR, as a
        /// percentage: the square of the reflection coefficient,
        /// ((SWR - 1) / (SWR + 1)) squared. NaN for anything that is not an
        /// SWR (below 1, or not a number).
        /// </summary>
        /// <remarks>
        /// The same arithmetic, in the same order, as the three constants
        /// above, which have to be written out because a constant cannot call
        /// a method. A test holds the two together.
        /// </remarks>
        public static double ReflectedPercentAt(double swr)
        {
            if (double.IsNaN(swr) || swr < 1.0) return double.NaN;
            if (double.IsPositiveInfinity(swr)) return 100.0;
            return 100.0 * ((swr - 1.0) / (swr + 1.0)) * ((swr - 1.0) / (swr + 1.0));
        }
    }
}
