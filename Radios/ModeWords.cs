#nullable enable
namespace Radios
{
    /// <summary>
    /// How a mode is SPOKEN. The one place mode words come from: the short
    /// form every mode announcement uses, and the full words the station
    /// check uses (<see cref="ChainChecks.StationConditions.ModeInWords"/>,
    /// which reads them from here). The screen keeps the radio's own letters.
    /// </summary>
    /// <remarks>
    /// <para><b><see cref="Spoken"/>, ruled by Noel 2026-11-07 (J41, then
    /// 06:55):</b> change the spoken form only where a synthesizer mangles or
    /// blurs it. DIGL and DIGU are "Digital lower" and "Digital upper" (option
    /// (c): Eloquence ran "digl" together and buried the last letter that
    /// tells them apart); SAM is "Synchronous AM" (read as the name "Sam");
    /// NFM is "Narrow FM". USB, LSB, AM, FM, CW, DSB and RTTY stay letters.
    /// The same at Terse and Chatty. Anything else is said as the radio
    /// names it.</para>
    /// <para><b>DFM is "Digital FM"</b>, ruled by Noel 2026-11-08. J41 allowed
    /// it once verified to be what DFM is on Flex, and it was left as letters
    /// until then; Noel verified it from the FlexRadio manual (5 kHz
    /// deviation with no pre-emphasis, used for repeaters and
    /// satellites).</para>
    /// <para><b><see cref="InWords"/></b> is the station check's vocabulary,
    /// moved here unchanged so there is one source: "upper sideband" and
    /// "digital upper sideband" where a sentence about the radio's setting
    /// needs the words, ruled for that check on 2026-08-31.</para>
    /// <para>Every key is a full literal, so the lexicon's coverage test can
    /// see each one.</para>
    /// </remarks>
    public static class ModeWords
    {
        /// <summary>The mode as an announcement says it. Never empty for a non-empty mode.</summary>
        public static string Spoken(string? mode)
        {
            string m = (mode ?? "").Trim();
            switch (m.ToUpperInvariant())
            {
                case "DIGU": return Lexicon.Get("settings.mode.spoken.digu");
                case "DIGL": return Lexicon.Get("settings.mode.spoken.digl");
                case "SAM": return Lexicon.Get("settings.mode.spoken.sam");
                case "NFM": return Lexicon.Get("settings.mode.spoken.nfm");
                case "DFM": return Lexicon.Get("settings.mode.spoken.dfm");
                default: return m;
            }
        }

        /// <summary>
        /// The mode in full words, bare, for a sentence that says what the
        /// radio is set to. Unknown tokens pass through as themselves.
        /// </summary>
        public static string InWords(string? mode)
        {
            string m = (mode ?? "").Trim();
            switch (m.ToUpperInvariant())
            {
                case "USB": return Lexicon.Get("settings.mode.words.usb");
                case "LSB": return Lexicon.Get("settings.mode.words.lsb");
                case "DIGU": return Lexicon.Get("settings.mode.words.digu");
                case "DIGL": return Lexicon.Get("settings.mode.words.digl");
                default: return m;
            }
        }
    }
}
