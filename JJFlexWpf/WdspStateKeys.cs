using System.Collections.Generic;

namespace JJFlexWpf
{
    /// <summary>
    /// The lexicon key Track E speaks for each helper state change. A pure
    /// function: no window, no clock, no speech, and no lexicon entry here.
    /// The words, the lexicon entries and the speaking are Track E's; this
    /// only names the keys, so the event and its sentence cannot drift apart.
    /// </summary>
    /// <remarks>
    /// One key for a single engine and one for several, because the sentence
    /// differs ("its helper" against "their helper"). The engines themselves
    /// fill a placeholder in the sentence. Nothing to say when no helper
    /// engine is selected.
    /// </remarks>
    public static class WdspStateKeys
    {
        public const string CapReachedOne = "audio.denoise.helper.cap_reached.one";
        public const string CapReachedSeveral = "audio.denoise.helper.cap_reached.several";
        public const string BackOne = "audio.denoise.helper.back.one";
        public const string BackSeveral = "audio.denoise.helper.back.several";

        public static string? KeyFor(WdspHelperStateChange change, IReadOnlyCollection<string> engines)
        {
            if (engines.Count == 0) return null;
            bool one = engines.Count == 1;
            return change switch
            {
                WdspHelperStateChange.CapReached => one ? CapReachedOne : CapReachedSeveral,
                WdspHelperStateChange.BackAfterCap => one ? BackOne : BackSeveral,
                _ => null,
            };
        }
    }
}
