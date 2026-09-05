using System;

namespace Radios
{
    /// <summary>
    /// THE one words-scale for stereo pan (0 = hard left, 50 = centre,
    /// 100 = hard right). Two consumers on purpose: the pan value sub-layer's
    /// words form (#304) and the slice status summary
    /// (<see cref="RadioStatusBuilder"/>), which carried its own hardcoded
    /// copy of this idea until 2026-08-27. One value called "center" on one
    /// surface and "slightly left" on another is the two-homes defect this
    /// project keeps paying for — if a third surface needs pan in words, it
    /// calls this.
    ///
    /// <para>The bands are symmetric about centre and sized so that walking
    /// the layer's 5-step changes the word every few presses: centre is
    /// exactly 50 (a position you land on deliberately — Home puts you
    /// there), "slightly" is the one-or-two-nudges neighbourhood that the
    /// coarse Home-row keys cannot express, and "hard" is the rail.</para>
    /// </summary>
    public static class PanPhrase
    {
        /// <summary>
        /// The spoken words for a pan value, lowercase for mid-sentence use
        /// ("slightly left"). Speech-only — never displayed — so standalone
        /// utterances use it as-is.
        ///
        /// <para>In the pan sub-layer these words now follow the number rather
        /// than replacing it ("Pan 40, slightly left"), because a band twenty
        /// points wide cannot be stepped back to (#536). They are colour beside
        /// the figure, not a substitute for it. The slice status summary still
        /// uses them bare: that is a census of every slice, not a control being
        /// moved, and a number per slice would bury the sentence.</para>
        /// </summary>
        public static string Words(int pan)
        {
            pan = Math.Clamp(pan, 0, 100);
            string key =
                pan <= 2 ? "audio.pan.position_hard_left"
                : pan <= 14 ? "audio.pan.position_far_left"
                : pan <= 34 ? "audio.pan.position_left"
                : pan <= 49 ? "audio.pan.position_slightly_left"
                : pan == 50 ? "audio.pan.position_center"
                : pan <= 65 ? "audio.pan.position_slightly_right"
                : pan <= 85 ? "audio.pan.position_right"
                : pan <= 97 ? "audio.pan.position_far_right"
                : "audio.pan.position_hard_right";
            return Lexicon.Get(key);
        }
    }
}
