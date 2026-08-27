using System;
using System.Collections.Generic;
using System.Text;

namespace Radios
{
    /// <summary>
    /// Composes the Ctrl+F1 explanation for the Home Frequency field (#184).
    ///
    /// The Frequency field has TWO key maps, and which one is live depends on
    /// a mode the operator set at some earlier point and may not currently
    /// remember. This text leads with that state — the mode, and the thing the
    /// keys act on (the cursor digit in Classic, the step values in Modern) —
    /// then reads ONLY the live map, then names the way to the other mode.
    ///
    /// SINGLE SOURCE: the key rows are passed in VERBATIM from
    /// KeyInventory (JJFlexWpf), the same table that drives the '?' handler,
    /// the per-field help dialog, the Keys dialog, the Command Finder and the
    /// exported key manifest. This class adds no key knowledge of its own —
    /// it may only say the mode, the live values, and where the switch is.
    /// Writing a key name or a key meaning into this file (or its lexicon
    /// strings) would create the third hand-maintained copy of the map that
    /// #184 exists to prevent. See #274 for what the second copy cost.
    ///
    /// Lives in Radios rather than JJFlexWpf so Radios.Tests can assert the
    /// assembled sentences directly — help resolution is a pure lookup and is
    /// tested without constructing a window.
    /// </summary>
    public static class TuningContextHelp
    {
        /// <summary>
        /// Assemble the spoken Ctrl+F1 answer for the Frequency field.
        /// </summary>
        /// <param name="modern">True when Modern tuning is live, false for Classic.</param>
        /// <param name="chatty">True at Chatty verbosity. Chatty adds connective
        /// coaching; the key rows themselves are identical at every level,
        /// because the keys are what the operator asked for.</param>
        /// <param name="switchKeyDisplay">The CURRENT binding of the tuning-mode
        /// switch (for example "Ctrl+Shift+M"), resolved from the live registry
        /// so a rebound key is never misquoted. Null when unbound — the menu
        /// route is offered instead.</param>
        /// <param name="coarseStep">Spoken coarse step, e.g. "5 kilohertz". Modern only.</param>
        /// <param name="fineStep">Spoken fine step, e.g. "100 hertz". Modern only.</param>
        /// <param name="cursorStepName">Spoken name of the digit under the cursor,
        /// e.g. "1 kilohertz". Classic only; null omits the cursor sentence.</param>
        /// <param name="liveKeys">The live map, verbatim inventory rows.</param>
        public static string ComposeFrequencyField(
            bool modern,
            bool chatty,
            string? switchKeyDisplay,
            string coarseStep,
            string fineStep,
            string? cursorStepName,
            IReadOnlyList<(string Key, string Description)> liveKeys)
        {
            var sb = new StringBuilder();

            // State first. The operator pressing this key is lost about STATE
            // — which map is live, and what the keys will act on — before they
            // are lost about any individual key.
            if (modern)
            {
                sb.Append(Lexicon.Get(
                    chatty ? "help.tuning.modern_state_chatty" : "help.tuning.modern_state_terse",
                    ("coarse", coarseStep), ("fine", fineStep)));
            }
            else if (!string.IsNullOrWhiteSpace(cursorStepName))
            {
                sb.Append(Lexicon.Get(
                    chatty ? "help.tuning.classic_state_chatty" : "help.tuning.classic_state_terse",
                    ("digit", cursorStepName)));
            }
            else
            {
                // Defensive: the cursor name should always resolve on the Freq
                // field, but a missing value must degrade to a true sentence,
                // not a sentence with a hole in it.
                sb.Append(Lexicon.Get("help.tuning.classic_state_bare"));
            }

            // The live map, one row per sentence, inventory text verbatim.
            foreach (var (key, description) in liveKeys)
            {
                sb.Append(' ').Append(Lexicon.Get("help.tuning.key_row",
                    ("key", key), ("description", description)));
            }

            // The way to the other mode. Named LAST so the map is not
            // interrupted, and named always — the other map being reachable is
            // half the reason the mode is safe to forget.
            string otherMode = Lexicon.Get(
                modern ? "help.tuning.mode_classic" : "help.tuning.mode_modern");
            if (!string.IsNullOrWhiteSpace(switchKeyDisplay))
            {
                sb.Append(' ').Append(Lexicon.Get(
                    chatty ? "help.tuning.switch_chatty" : "help.tuning.switch_terse",
                    ("key", switchKeyDisplay), ("mode", otherMode)));
            }
            else
            {
                sb.Append(' ').Append(Lexicon.Get("help.tuning.switch_unbound",
                    ("mode", otherMode)));
            }

            // Chatty closes with the door to the rest of the field's keys.
            // Supplementary wayfinding, so it follows the verbosity doctrine:
            // hints live at Chatty.
            if (chatty)
                sb.Append(' ').Append(Lexicon.Get("help.tuning.more_keys"));

            return sb.ToString();
        }
    }
}
