using System;
using System.Globalization;

namespace Radios
{
    /// <summary>
    /// The unit words a hertz value is said or shown with. Each style is the
    /// wording one family of call sites already used, so moving a site onto
    /// the shared formatter changes how many decimals it carries, not its
    /// vocabulary.
    /// </summary>
    public enum HertzUnitStyle
    {
        /// <summary>"2.75 k" above 1 kHz, a bare "500" below — the bracket readout's "Filter 100 to 2900, 2.8 k".</summary>
        Bare,

        /// <summary>"2.75 k" above 1 kHz, "500 hertz" below. As a readout's Terse style (<see cref="HertzSpeech.ReadoutWidth(int, HertzUnitStyle)"/>) it gives "2.75 k" or, under 1 kHz, the bare "500" (J30).</summary>
        KiloLetter,

        /// <summary>"2.75 kHz" or "500 Hz" — text on a window, not speech.</summary>
        Symbols,
    }

    /// <summary>
    /// ONE formatter for hertz-valued filter WIDTHS, so a 50 Hz step can never
    /// say the same width twice (#747). Every spoken and displayed filter width
    /// goes through here; a source test refuses a new hand-rolled kilo format.
    /// </summary>
    /// <remarks>
    /// <para><b>Noel's ruling, 2026-11-05:</b> <i>"I'd say we could default to k
    /// on terse and 00 on chatty"</i>. Spoken widths of 1 kHz and up follow the
    /// verbosity dial: Terse speaks the kilo form ("2.8 k") and Chatty the
    /// whole number of hertz ("2800"). Critical speaks as Terse does and
    /// Diagnostic as Chatty does. Below 1 kHz a width is its plain number at
    /// every level, as it always was.</para>
    /// <para><b>A filter READOUT labels its width at Chatty</b> (Noel,
    /// 2026-11-06, J13 and then J15): "Filter 100 to 2900, width 2800
    /// hertz", through <see cref="ReadoutWidth(int, HertzUnitStyle)"/>. Terse
    /// keeps the kilo form.</para>
    /// <para><b>The kilo form never rounds</b> (Claude's reading of the ruling,
    /// to be confirmed with Noel): it carries exactly the decimals the value
    /// needs, so 2750 is "2.75" and 2800 is "2.8". One decimal place is what
    /// made 2350 and 2400 both say "2.8 k" — tenths cannot tell a 50 Hz step
    /// apart, and the old <c>ToString("F1")</c> rounded the halves both ways.</para>
    /// <para><b>Displayed text is not on the dial.</b> Verbosity is a speech
    /// setting, so a label or a list row a screen reader reads on arrow uses
    /// <see cref="DisplayedWidth"/>, the never-rounding kilo form, whatever the
    /// verbosity.</para>
    /// <para><b>Widths only, today.</b> Filter EDGES are spoken as exact
    /// integers and were never wrong, so they are not routed here. The shape
    /// takes any hertz value, so edges, shift and offsets could join later as
    /// a decision of their own. Frequency is deliberately NOT here: it has its
    /// own Hz/kHz/MHz setting (<c>PttConfig.FrequencyDisplayUnits</c>).</para>
    /// </remarks>
    public static class HertzSpeech
    {
        /// <summary>At and above this many hertz, a width can take the kilo form.</summary>
        public const int KiloThresholdHz = 1000;

        /// <summary>True when this verbosity speaks widths in the kilo form: Critical and Terse.</summary>
        public static bool SpeaksKiloForm(VerbosityLevel level) => level <= VerbosityLevel.Terse;

        /// <summary>
        /// The kilohertz digits for a whole number of hertz, exact: 2750 is
        /// "2.75", 2800 "2.8", 2000 "2", 1050 "1.05". A whole number of hertz
        /// has at most three decimal places in kilohertz, so nothing is ever
        /// rounded.
        /// </summary>
        public static string KiloDigits(int hz)
            => (hz / 1000m).ToString("0.###", CultureInfo.CurrentCulture);

        /// <summary>A width to SPEAK, at the current verbosity.</summary>
        public static string Width(int hz, HertzUnitStyle style)
            => Width(hz, style, ScreenReaderOutput.CurrentVerbosity);

        /// <summary>A width to SPEAK, at the given verbosity.</summary>
        public static string Width(int hz, HertzUnitStyle style, VerbosityLevel level)
            => Format(hz, style, kilo: Math.Abs(hz) >= KiloThresholdHz && SpeaksKiloForm(level));

        /// <summary>
        /// The width in a filter READOUT, spoken after the filter's edges:
        /// "Filter 100 to 2900, 2.8 k" at Terse and "Filter 100 to 2900, width
        /// 2800 hertz" at Chatty.
        /// </summary>
        /// <remarks>
        /// <b>Noel's ruling, 2026-11-06 11:38 and 11:39 (night question
        /// J13):</b> keep the Terse readout as it is, the never-rounding kilo
        /// form (<i>"This is great for terse"</i>); at Chatty the width gets a
        /// label, not a bare number (<i>"As currently designed, you just get a
        /// number thrown at you"</i>). <b>J15, 16:01, replacing J13's "hertz
        /// wide":</b> <i>"I didn't know we had width in there when I suggested
        /// wide. I'd do a combination. I'd say filter 100 to 2850, width 2750
        /// hertz"</i>, for every Chatty filter readout. So Critical and Terse
        /// speak <paramref name="terseStyle"/> exactly as
        /// <see cref="Width(int, HertzUnitStyle, VerbosityLevel)"/> does, and
        /// Chatty and Diagnostic speak "width", the whole number of hertz and
        /// "hertz", at every width, below 1 kHz too ("width 500 hertz"; J16
        /// keeps Terse's bare "500").
        /// </remarks>
        public static string ReadoutWidth(int hz, HertzUnitStyle terseStyle)
            => ReadoutWidth(hz, terseStyle, ScreenReaderOutput.CurrentVerbosity);

        /// <summary>A filter readout's width at the given verbosity; see <see cref="ReadoutWidth(int, HertzUnitStyle)"/>.</summary>
        /// <remarks>
        /// <b>J30 (Noel, 2026-11-06 17:34, "agreed"):</b> every Terse readout
        /// under 1 kHz is the bare number ("500"), whatever the route's style,
        /// as the bracket keys already were (J16). The units setting filed on
        /// #747 adds units back for anyone who wants them.
        /// </remarks>
        public static string ReadoutWidth(int hz, HertzUnitStyle terseStyle, VerbosityLevel level)
        {
            if (!SpeaksKiloForm(level))
                return Lexicon.Get("settings.filter.width_named_hertz", ("width", hz.ToString(CultureInfo.CurrentCulture)));
            if (Math.Abs(hz) < KiloThresholdHz) return hz.ToString(CultureInfo.CurrentCulture);
            return Width(hz, terseStyle, level);
        }

        /// <summary>
        /// A width to SHOW: the never-rounding kilo form at and above 1 kHz,
        /// whatever the verbosity.
        /// </summary>
        public static string DisplayedWidth(int hz, HertzUnitStyle style = HertzUnitStyle.Symbols)
            => Format(hz, style, kilo: Math.Abs(hz) >= KiloThresholdHz);

        private static string Format(int hz, HertzUnitStyle style, bool kilo)
        {
            string digits = kilo ? KiloDigits(hz) : hz.ToString(CultureInfo.CurrentCulture);
            switch (style)
            {
                case HertzUnitStyle.Bare:
                    return kilo ? Lexicon.Get("settings.filter.width_kilo", ("width", digits)) : digits;
                case HertzUnitStyle.KiloLetter:
                    return kilo
                        ? Lexicon.Get("settings.filter.width_kilo", ("width", digits))
                        : Lexicon.Get("settings.filter.width_hertz_words", ("width", digits));
                default:
                    return kilo
                        ? Lexicon.Get("audio.filter.presets.width_khz", ("width", digits))
                        : Lexicon.Get("audio.filter.presets.width_hz", ("width", digits));
            }
        }
    }
}
