namespace Radios
{
    /// <summary>
    /// How a display shows <see cref="FlexBase.ComputedSWR"/>: a number when
    /// there is one, and the no-reading words when there is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The braille status line and the Audio Workshop's live meters used to
    /// read <c>SWRValue</c>, the radio's raw meter: the one that read 1.008 with
    /// 76 percent of the power coming back off an empty port. Moving them to
    /// <see cref="FlexBase.ComputedSWR"/> means each needs a no-reading form,
    /// because it returns NaN when the forward and reflected readings did not
    /// come from one moment, or there is too little forward power to derive one.
    /// </para>
    /// <para>
    /// The rule for "no reading" is the one <c>FlexBase.SWRText</c> applies: NaN,
    /// or anything below 1, which a standing wave ratio cannot be. The words are
    /// that method's too (<c>audio.tune.swr_no_reading</c>).
    /// </para>
    /// </remarks>
    public static class SwrReadout
    {
        /// <summary>True when <paramref name="computedSwr"/> is a measurement.</summary>
        public static bool IsReading(float computedSwr) =>
            !float.IsNaN(computedSwr) && computedSwr >= 1f;

        /// <summary>
        /// The braille status line's SWR cell. Wide displays (80 cells) say it
        /// in words; narrower ones get a short cell, because the line drops
        /// every field after the first one that does not fit.
        /// </summary>
        public static string BrailleCell(float computedSwr, bool wide)
        {
            if (IsReading(computedSwr))
            {
                string n = computedSwr.ToString("F1");
                return wide ? "SWR " + n : "SW" + n;
            }
            return wide
                ? Lexicon.Get("audio.tune.swr_no_reading")
                : Lexicon.Get("connect.home.braille_swr_no_reading");
        }

        /// <summary>The Audio Workshop's SWR meter box.</summary>
        public static string MeterBoxText(float computedSwr) =>
            IsReading(computedSwr)
                ? "SWR: " + computedSwr.ToString("F1")
                : Lexicon.Get("audio.tune.swr_no_reading");
    }
}
