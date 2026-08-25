using System.Globalization;

namespace Radios
{
    /// <summary>
    /// How an S-meter reading becomes something an operator hears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One rule, one place.</b> This arithmetic previously lived inline at
    /// two call sites — the spoken S-meter command and the status snapshot —
    /// and they drifted: one multiplied the excess by ten, the other by six.
    /// A blind operator has no second opinion about their signal strength, so
    /// the two surfaces disagreeing is worse than either being wrong alone.
    /// </para>
    /// <para>
    /// <b>THE TRAP, and it is not obvious from the property name.</b>
    /// <c>FlexBase.SMeter</c> does NOT return S-units above S9. It returns
    /// dB-over-S9 PLUS 9 — so a reading of 13 means four decibels over S9, not
    /// thirteen S-units and not thirteen decibels. The excess is therefore
    /// already in decibels and must be reported AS IS. Multiplying it by
    /// anything is the bug: at ten, four decibels over S9 was announced as
    /// "S9 plus 40"; at six, as "S9 plus 24".
    /// </para>
    /// <para>
    /// That bug shipped, and a tester reported hearing 5 read back as 50 and
    /// 10 as 100 before it was found. It was invisible in code review because
    /// multiplying looks like a unit conversion, and it is exactly the
    /// conversion you would need if the property meant what its name suggests.
    /// </para>
    /// </remarks>
    public static class SMeterReading
    {
        /// <summary>The reading at and below which the value is plain S-units.</summary>
        public const int TopSUnit = 9;

        /// <summary>
        /// Decibels over S9, from a raw <c>SMeter</c> reading.
        /// </summary>
        /// <remarks>
        /// Subtraction only. If a multiplier ever appears here, the reading is
        /// being inflated — see the remarks on the class.
        /// </remarks>
        public static int ExcessOverS9(int smeter) => smeter - TopSUnit;

        /// <summary>True when the reading is above S9 and reads as an excess.</summary>
        public static bool IsOverS9(int smeter) => smeter > TopSUnit;

        /// <summary>
        /// The reading as it is shown and spoken: "S5" at or below S9, and
        /// "S9 plus 4 dB" above it.
        /// </summary>
        public static string Display(int smeter)
        {
            if (!IsOverS9(smeter))
                return "S" + smeter.ToString(CultureInfo.InvariantCulture);

            return "S9 plus "
                 + ExcessOverS9(smeter).ToString(CultureInfo.InvariantCulture)
                 + " dB";
        }
        /// <summary>
        /// The reading where space is scarce: "S5", and "S9+4 dB" above S9.
        /// </summary>
        /// <remarks>
        /// <b>A second rendering, deliberately — not a second rule.</b> A
        /// braille display and a meter readout cannot spend three cells on the
        /// word "plus", and speech cannot use the plus SIGN because voices read
        /// it inconsistently or not at all. So the WORDS differ and the
        /// ARITHMETIC does not: both call
        /// <see cref="ExcessOverS9"/>, which is the part that broke.
        /// </remarks>
        public static string Compact(int smeter)
        {
            if (!IsOverS9(smeter))
                return "S" + smeter.ToString(CultureInfo.InvariantCulture);

            return "S9+"
                 + ExcessOverS9(smeter).ToString(CultureInfo.InvariantCulture)
                 + " dB";
        }
    }
}
