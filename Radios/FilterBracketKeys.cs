using System;

namespace Radios
{
    /// <summary>The filter edge the bracket keys hold, if any.</summary>
    public enum FilterEdge
    {
        /// <summary>Not in filter edge mode: the brackets widen, slide, squeeze and pull.</summary>
        None,

        /// <summary>The low edge is held: plain brackets move it alone.</summary>
        Low,

        /// <summary>The high edge is held: plain brackets move it alone.</summary>
        High,
    }

    /// <summary>What one bracket key press does.</summary>
    public enum BracketAction
    {
        /// <summary>Not a filter chord here: leave the key unhandled, so the registry gets its turn (the transmit filter's Ctrl+Shift and Ctrl+Alt chords).</summary>
        NotOurs,

        /// <summary>Alt with a bracket: the previous or next filter preset.</summary>
        PresetCycle,

        /// <summary>A plain bracket outside edge mode: left bracket moves the low edge down, right bracket the high edge up.</summary>
        Widen,

        /// <summary>Shift with a bracket outside edge mode: slide the whole passband.</summary>
        Slide,

        /// <summary>Ctrl with the left bracket outside edge mode: narrow both edges.</summary>
        Squeeze,

        /// <summary>Ctrl with the right bracket outside edge mode: widen both edges.</summary>
        Pull,

        /// <summary>A plain bracket in edge mode: move the held edge alone.</summary>
        MoveHeldEdge,

        /// <summary>A grab key outside edge mode: enter it, holding that edge.</summary>
        GrabEdge,

        /// <summary>The OTHER edge's grab key in edge mode: hold that edge instead, staying in the mode.</summary>
        SwitchEdge,

        /// <summary>The held edge's own grab key: leave edge mode.</summary>
        LeaveEdgeMode,

        /// <summary>Shift or Ctrl with a bracket in edge mode: change nothing and say which edge is held.</summary>
        RefusedWhileHeld,
    }

    /// <summary>
    /// One press, decided. <see cref="Edge"/> is the edge the action concerns
    /// (the held one, or the one being grabbed); <see cref="CloseBracket"/> is
    /// true for the right bracket, which moves things up.
    /// </summary>
    public readonly record struct BracketDecision(BracketAction Action, FilterEdge Edge, bool CloseBracket)
    {
        /// <summary>For <see cref="BracketAction.RefusedWhileHeld"/>: the whole-filter action the key would have done outside edge mode.</summary>
        public BracketAction WouldHaveDone { get; init; }
    }

    /// <summary>
    /// The bracket keys' grammar for the receive filter, and the sentences
    /// edge mode speaks (#748). Pure: no window, no radio, so every rule here
    /// is tested directly.
    /// </summary>
    /// <remarks>
    /// <para><b>Noel's rulings, 2026-10-17:</b> retire the bracket double-tap
    /// entirely (ruling 4); <c>Alt+Shift+[</c> grabs the low edge and
    /// <c>Alt+Shift+]</c> the high edge (ruling 5). The double-tap entered edge
    /// mode by accident three times in a minute while he widened a filter,
    /// because a held bracket repeats at about the speed of a deliberate
    /// double-tap.</para>
    /// <para><b>2026-11-06 (night question J2):</b> the held edge's own grab key
    /// leaves edge mode, saying "Filter edge mode off"; the other edge's grab
    /// key switches edges, seamlessly; Shift or Ctrl with a bracket while an
    /// edge is held changes nothing and says which edge is held, and at Chatty
    /// also says how to do what the key pressed suggests. Before this, Shift and
    /// Ctrl with a bracket moved the whole filter in edge mode as if it were not
    /// there.</para>
    /// <para><b>Modifiers are strict</b>, as they were (QB Track H): a chord
    /// with an extra modifier is not this handler's, so Ctrl+Shift and Ctrl+Alt
    /// with a bracket still reach the registry's transmit filter commands.</para>
    /// <para>Every sentence here is DRAFT wording, for Noel to correct at the
    /// keyboard audit.</para>
    /// </remarks>
    public static class FilterBracketKeys
    {
        /// <summary>Decide what a bracket press does, given the edge held now.</summary>
        public static BracketDecision Decide(FilterEdge held, bool closeBracket, bool shift, bool ctrl, bool alt)
        {
            bool inEdgeMode = held != FilterEdge.None;

            if (alt && !ctrl && !shift)
                return new BracketDecision(BracketAction.PresetCycle, held, closeBracket);

            if (alt && shift && !ctrl)
            {
                FilterEdge grabbed = closeBracket ? FilterEdge.High : FilterEdge.Low;
                if (!inEdgeMode) return new BracketDecision(BracketAction.GrabEdge, grabbed, closeBracket);
                if (grabbed == held) return new BracketDecision(BracketAction.LeaveEdgeMode, held, closeBracket);
                return new BracketDecision(BracketAction.SwitchEdge, grabbed, closeBracket);
            }

            if (shift && !ctrl && !alt)
            {
                return inEdgeMode
                    ? new BracketDecision(BracketAction.RefusedWhileHeld, held, closeBracket) { WouldHaveDone = BracketAction.Slide }
                    : new BracketDecision(BracketAction.Slide, held, closeBracket);
            }

            if (ctrl && !shift && !alt)
            {
                BracketAction whole = closeBracket ? BracketAction.Pull : BracketAction.Squeeze;
                return inEdgeMode
                    ? new BracketDecision(BracketAction.RefusedWhileHeld, held, closeBracket) { WouldHaveDone = whole }
                    : new BracketDecision(whole, held, closeBracket);
            }

            if (!ctrl && !shift && !alt)
            {
                return inEdgeMode
                    ? new BracketDecision(BracketAction.MoveHeldEdge, held, closeBracket)
                    : new BracketDecision(BracketAction.Widen, held, closeBracket);
            }

            return new BracketDecision(BracketAction.NotOurs, held, closeBracket);
        }

        /// <summary>
        /// Entering edge mode: names the mode and the edge at every level, and
        /// at Chatty how to leave it. DRAFT.
        /// </summary>
        public static string EntrySentence(FilterEdge edge, VerbosityLevel level)
            => edge == FilterEdge.High
                ? Lexicon.Get("settings.filter.edge_mode_on_high", level)
                : Lexicon.Get("settings.filter.edge_mode_on_low", level);

        /// <summary>
        /// "Low edge held." / "High edge held." — said on switching edges, at
        /// every level. DRAFT, from Noel's own draft of the refusal.
        /// </summary>
        public static string HeldSentence(FilterEdge edge)
            => edge == FilterEdge.High
                ? Lexicon.Get("settings.filter.edge_held_high")
                : Lexicon.Get("settings.filter.edge_held_low");

        /// <summary>
        /// Leaving edge mode, by Escape or by the held edge's grab key:
        /// "Filter edge mode off" (Noel, 2026-11-04: "cancelled" said the
        /// change was thrown away, and it was kept).
        /// </summary>
        public static string OffSentence() => Lexicon.Get("settings.filter.edge_mode_off");

        /// <summary>
        /// Shift or Ctrl with a bracket while an edge is held: which edge is
        /// held, and at Chatty how to do what the key pressed suggests. DRAFT,
        /// from Noel's draft of 2026-11-06. Full literal keys, one per edge and
        /// whole-filter action, so the lexicon scanners can see every one.
        /// </summary>
        public static string RefusalSentence(FilterEdge held, BracketAction wouldHaveDone, VerbosityLevel level)
        {
            bool high = held == FilterEdge.High;
            switch (wouldHaveDone)
            {
                case BracketAction.Squeeze:
                    return high
                        ? Lexicon.Get("settings.filter.edge_refused_high_squeeze", level)
                        : Lexicon.Get("settings.filter.edge_refused_low_squeeze", level);
                case BracketAction.Pull:
                    return high
                        ? Lexicon.Get("settings.filter.edge_refused_high_pull", level)
                        : Lexicon.Get("settings.filter.edge_refused_low_pull", level);
                default:
                    return high
                        ? Lexicon.Get("settings.filter.edge_refused_high_slide", level)
                        : Lexicon.Get("settings.filter.edge_refused_low_slide", level);
            }
        }

        /// <summary>
        /// The readout after a plain bracket moves the held edge: the filter
        /// sentence, and at Chatty the held edge before it, so the mode is
        /// said WHILE you are in it and not only on the way in. DRAFT.
        /// </summary>
        public static string HeldStepSentence(FilterEdge held, string filterSentence, VerbosityLevel level)
            => held == FilterEdge.High
                ? Lexicon.Get("settings.filter.edge_step_high", level, ("filter", filterSentence))
                : Lexicon.Get("settings.filter.edge_step_low", level, ("filter", filterSentence));
    }
}
