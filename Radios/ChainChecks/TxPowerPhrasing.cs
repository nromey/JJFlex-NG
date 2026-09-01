using System.Globalization;

namespace Radios.ChainChecks
{
    /// <summary>
    /// The one place JJ Flexible puts a unit on the radio's transmit power
    /// SETTING.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#444.</b> One number — FlexLib's <c>Radio.RFPower</c>, read through
    /// <c>FlexBase.XmitPower</c> — was rendered three ways in one Fixer report:
    /// <i>"at 10 watts into ANT1"</i> in the stage sentence, <i>"RF power: 10
    /// watts"</i> in the settings fingerprint, and <i>"Transmit power setting:
    /// 10 percent"</i> in the readings block. On a hundred-watt radio all three
    /// are the same digits, so nothing looked wrong and nothing ever would —
    /// until somebody ran an amplifier, a transverter, or a radio whose maximum
    /// is not a hundred.
    /// </para>
    /// <para>
    /// <b>This class does not settle watts against percent, and must not be read
    /// as having settled it.</b> FlexLib documents <c>RFPower</c>,
    /// <c>TunePower</c> AND <c>AMCarrierLevel</c> as watts on a nought-to-a-
    /// hundred scale, which is exactly what a percentage would look like, and
    /// #426 is the same ambiguity in the AM carrier. That question is answered
    /// on the bench with a wattmeter, for all three together — not from a vendor
    /// comment, and not here. What this class fixes is the contradiction: one
    /// value, one unit word, one home to change when the bench answers.
    /// </para>
    /// <para>
    /// <b>Why watts and not percent.</b> Not because watts was proved right —
    /// it was not. Because every other surface in the app already says watts
    /// (the Power dialog, the profile report, the settings fingerprint, the
    /// stage sentences) and exactly one said percent. Changing the outlier
    /// leaves the app with one vocabulary and one open question; changing the
    /// majority would have invented a second vocabulary while leaving the same
    /// question open.
    /// </para>
    /// <para>
    /// <b>It lives in <c>Radios.ChainChecks</c> because everything that needs it
    /// can already reach here.</b> <c>TxChainFacts</c> is in this namespace,
    /// <c>Radios.Fixer.TransmitStageSet</c> already calls
    /// <c>ChainChecks.StationConditions</c>, and <c>Radios.FixerEvidence</c> has
    /// no boundary against it. A fourth consumer should call this rather than
    /// grow a fourth literal — <c>TransmitPowerUnitAgreementTests</c> is what
    /// notices when one does.
    /// </para>
    /// </remarks>
    public static class TxPowerPhrasing
    {
        /// <summary>
        /// The unit word for the transmit power setting, for a caller that
        /// supplies its own number formatting — above all
        /// <see cref="DiagnosticFact.Measure"/>, which takes units separately so
        /// that a rule writing <c>{rf-power-setting}</c> gets them too.
        /// </summary>
        public const string SettingUnits = "watts";

        /// <summary>
        /// The transmit power setting as an operator reads it: the number and
        /// its unit, singular at one.
        /// </summary>
        /// <remarks>
        /// The singular matters more than it looks. These sentences are read
        /// aloud, and "at 1 watts into ANT1" is the kind of stumble that makes a
        /// listener stop trusting the rest of the sentence.
        /// </remarks>
        public static string Setting(int value)
            => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " watt" : " " + SettingUnits);
    }
}
