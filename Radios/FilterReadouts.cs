using System;

namespace Radios
{
    /// <summary>
    /// What a receive filter WRITE says, from what the filter became (round
    /// six of the 2026-11-06 junk box, #747). Every route that writes the
    /// receive filter and then speaks a filter value goes through
    /// <see cref="FlexBase.SetFilter(int, int, Action{FilterApplied})"/>, so
    /// the sentence is built from the edges FlexLib's <c>Slice.UpdateFilter</c>
    /// actually produced, never from the edges asked for.
    /// </summary>
    /// <remarks>
    /// <para>Why: UpdateFilter refuses FM and NFM outright and clamps every
    /// other mode to its own range (RTTY around its mark and shift, CW around
    /// its pitch, USB at 0 and 12000). The bracket keys, the filter layer and
    /// the Filter menu each clamped to their own bounds and then spoke their
    /// own numbers, so in those modes they could say a filter the radio never
    /// took. Codex found it for presets (round five, B2); this closes the
    /// class.</para>
    /// <para>Three outcomes, the same three a preset has:</para>
    /// <list type="bullet">
    /// <item>Taken as asked: the route's own readout of those edges.</item>
    /// <item>Held back by the mode: the whole filter it became, marked as at
    /// the limit in the route's own words.</item>
    /// <item>Not changed at all: "Filter unchanged at -6000 to 6000, width
    /// 12000 hertz" (DRAFT).</item>
    /// </list>
    /// </remarks>
    public static class FilterReadouts
    {
        /// <summary>The filter did not change: its edges as they stand, at the given verbosity. DRAFT.</summary>
        public static string Unchanged(FilterApplied applied, VerbosityLevel level)
            => Lexicon.Get("settings.filter.unchanged", ("low", applied.Low), ("high", applied.High),
                ("width", HertzSpeech.ReadoutWidth(applied.High - applied.Low, HertzUnitStyle.KiloLetter, level)));

        /// <summary>
        /// The bracket keys' readout after their write: "Filter 100 to 2900,
        /// 2.8 k" or "Filter 100 to 2900, width 2800 hertz", from the edges the
        /// write produced. <paramref name="ownLimit"/> is the bracket handler's
        /// own clamp suffix (", lower limit" and so on), which stands only when
        /// FlexLib took the edges as asked; held back further by the mode, the
        /// suffix is ", at limit". In edge mode at Chatty the held edge leads,
        /// as every edge-mode step does.
        /// </summary>
        public static string Bracket(FilterApplied applied, string ownLimit, FilterEdge held, VerbosityLevel level)
        {
            string sentence;
            if (applied.Unchanged)
                sentence = Unchanged(applied, level);
            else
                sentence = Lexicon.Get("settings.filter.range", ("low", applied.Low), ("high", applied.High),
                    ("width", HertzSpeech.ReadoutWidth(applied.High - applied.Low, HertzUnitStyle.Bare, level)),
                    ("limit", applied.Exact ? ownLimit ?? "" : Lexicon.Get("settings.filter.suffix_at_limit")));
            return held == FilterEdge.None ? sentence : FilterBracketKeys.HeldStepSentence(held, sentence, level);
        }

        /// <summary>
        /// A readout whose words the route already has, for the edges the write
        /// produced: <paramref name="asked"/> of those edges when taken as
        /// asked; <paramref name="heldBack"/> of them when the mode held the
        /// write back; the unchanged sentence when nothing moved.
        /// </summary>
        /// <remarks>
        /// The held-back readout is the WHOLE filter, not the one edge the route
        /// moved: a mode that clamps one edge may move the other (RTTY pulls
        /// the high edge to its own minimum), and "Low edge -270, at the limit"
        /// would hide that the high edge went too. The filter layer passes its
        /// "{what}, at the limit" around "Filter -270 to 50"; the Filter menu
        /// passes <see cref="RangeAtLimit"/>.
        /// </remarks>
        public static string Said(FilterApplied applied, Func<int, int, string> asked, Func<int, int, string> heldBack, VerbosityLevel level)
        {
            if (applied.Unchanged) return Unchanged(applied, level);
            return applied.Exact ? asked(applied.Low, applied.High) : heldBack(applied.Low, applied.High);
        }

        /// <summary>
        /// A transmit filter write's readout, from what FlexLib set (#747 round
        /// seven): <paramref name="asked"/> of the edges when taken as asked
        /// (the route's own words, as before); "TX filter 2750 to 2800, at
        /// limit" when FlexLib held it back (the whole filter, because the
        /// rule that held it is the gap to the other edge); "TX filter
        /// unchanged at 100 to 150, width 50 hertz" when nothing moved. DRAFT.
        /// </summary>
        public static string Tx(FilterApplied applied, Func<int, int, string> asked, VerbosityLevel level)
        {
            if (applied.Unchanged)
                return Lexicon.Get("audio.tx_filter.unchanged", ("low", applied.Low), ("high", applied.High),
                    ("width", HertzSpeech.ReadoutWidth(applied.High - applied.Low, HertzUnitStyle.KiloLetter, level)));
            if (applied.Exact) return asked(applied.Low, applied.High);
            return TxCorrection(applied);
        }

        /// <summary>"TX filter 2750 to 2800, at limit": the transmit filter FlexLib held a write back to.</summary>
        private static string TxCorrection(FilterApplied applied)
            => Lexicon.Get("audio.tx_filter.range", ("low", applied.Low), ("high", applied.High))
               + Lexicon.Get("settings.filter.suffix_at_limit");

        /// <summary>
        /// For a route that has ALREADY said the value it asked for (a value
        /// field speaks its own number as it moves): null when FlexLib took it
        /// as asked, else the sentence that corrects it.
        /// </summary>
        public static string? TxCorrectionIfAny(FilterApplied applied, VerbosityLevel level)
            => applied.Exact ? null : Tx(applied, (_, _) => "", level);

        /// <summary>"Filter -270 to 50, at limit": the whole filter, marked as the bracket keys mark a clamp.</summary>
        public static string RangeAtLimit(int low, int high)
            => Lexicon.Get("audio.filter.range", ("low", low), ("high", high)) + Lexicon.Get("settings.filter.suffix_at_limit");
    }
}
