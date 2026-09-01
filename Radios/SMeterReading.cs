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

        /// <summary>Decibels per S-unit. IARU R.1, and already what the app used.</summary>
        public const int DbPerSUnit = 6;

        /// <summary>IARU R.1's S9 reference below 30 MHz: 50 microvolts into
        /// 50 ohms.</summary>
        public const int S9DbmHf = -73;

        /// <summary>IARU R.1's S9 reference at and above 30 MHz: 5 microvolts
        /// into 50 ohms — twenty decibels weaker than the HF reference.</summary>
        public const int S9DbmVhf = -93;

        /// <summary>The frequency IARU R.1 splits its two S9 references on.</summary>
        /// <remarks>
        /// A plain threshold, deliberately, and NOT a lookup in
        /// <c>HamBands.Bands</c>. The recommendation splits on frequency, not
        /// on band membership, and this radio receives well outside the
        /// amateur bands — a general-coverage listen at 15 MHz has no band
        /// entry and must still get a calibration. 30.000000 MHz exactly falls
        /// in no amateur band, so which side it lands on is arbitrary; it is
        /// written as "at or above" so the rule can be stated in one sentence.
        /// </remarks>
        public const ulong VhfBoundaryHz = 30_000_000UL;

        /// <summary>
        /// Which of IARU R.1's two S9 references applies at a frequency.
        /// </summary>
        public enum Band
        {
            /// <summary>Below 30 MHz. S9 is -73 dBm.</summary>
            Hf,

            /// <summary>At or above 30 MHz — 6 m included. S9 is -93 dBm.</summary>
            VhfAndAbove,
        }

        /// <summary>
        /// The band whose calibration applies at a receive frequency.
        /// </summary>
        /// <remarks>
        /// A frequency of zero means "not known". It answers <see cref="Band.Hf"/>
        /// because an answer is required, and every caller that can encounter an
        /// unknown frequency must SAY it assumed HF rather than let the
        /// assumption pass silently — that silence is the whole defect this
        /// method was added to end.
        /// </remarks>
        public static Band BandFor(ulong frequencyHz)
            => frequencyHz >= VhfBoundaryHz ? Band.VhfAndAbove : Band.Hf;

        /// <summary>The dBm value S9 stands for on a band.</summary>
        public static int S9Dbm(Band band)
            => band == Band.VhfAndAbove ? S9DbmVhf : S9DbmHf;

        /// <summary>
        /// A dBm value as the app's integer S-meter reading: plain S-units at
        /// or below S9, dB-over-S9 plus 9 above it (see the class remarks).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the app's meter calibration, and it is IARU R.1's</b> —
        /// the live readout and anything that analyses recorded dBm agree to
        /// the digit: six decibels per S-unit, S9 at -73 dBm below 30 MHz and
        /// -93 dBm at or above it, S0 nine S-units under whichever applies
        /// (-127 and -147), dB-over-S9 above S9.
        /// </para>
        /// <para>
        /// <b>Both of those were wrong until Sprint 37 (#296), and both were
        /// wrong invisibly.</b> The anchor carried a hand-written 3 dB shift —
        /// the constant read <c>+ 127 - 3</c>, so someone started at the
        /// standard and subtracted, leaving the subtraction visible and
        /// writing no reason; it dates to the repository's initial import, so
        /// it is Jim-era. And nothing anywhere branched on frequency, so the
        /// HF calibration was applied at every frequency. Noel ruled on
        /// 2026-08-27 that the IARU values are correct.
        /// </para>
        /// <para>
        /// <b>Both errors ran the SAME way, and it is the opposite of the
        /// intuitive one: the app read LOW.</b> Shifting the anchor from -73
        /// to -70 means a weaker signal is needed to call S9, so every
        /// reported S-unit came out about half a unit short. Above 30 MHz the
        /// standard's S9 is weaker still (-93), so applying the HF number
        /// there cost three to four whole S-units. Correcting both makes
        /// readings go UP — noticeably so on 6 m and above. Measured against
        /// the old arithmetic: -79 dBm read S7 and now reads S8 on HF and S23
        /// on 2 m.
        /// </para>
        /// <para>
        /// <b>There is deliberately no single-argument overload.</b> A default
        /// band would be an HF assumption that no call site had to think
        /// about, which is exactly how the second error survived. Requiring
        /// the band makes the compiler name every consumer.
        /// </para>
        /// <para>
        /// The truncation toward zero is the getter's own historical
        /// behaviour, kept because the QSO signal analyzer must report the
        /// same S-unit the operator's Ctrl+S would have spoken at that
        /// instant.
        /// </para>
        /// </remarks>
        public static int FromDbm(double dbm, Band band)
        {
            // The live path stores (int)data — truncation toward zero — before
            // converting. Reproduce it exactly; do not "fix" it to rounding
            // here alone, or the analyzer and the live readout drift by one.
            //
            // Stated as "distance above this band's S0" rather than as a magic
            // constant: S0 sits nine S-units below S9, so the offset falls out
            // of the reference instead of being maintained beside it.
            int val = (int)dbm - S9Dbm(band) + (TopSUnit * DbPerSUnit);
            if (val < 0) val = 0;
            int s = val / DbPerSUnit; // S-unit
            // Above S9 the reading becomes dB-over-S9 plus 9.
            return (s <= TopSUnit) ? s : val - (TopSUnit * DbPerSUnit) + TopSUnit;
        }

        /// <summary>
        /// A dBm value as an S-meter reading, band chosen from the receive
        /// frequency in hertz.
        /// </summary>
        public static int FromDbm(double dbm, ulong frequencyHz)
            => FromDbm(dbm, BandFor(frequencyHz));

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
        /// A dBm reading as speech: "S meter minus 97 dBm".
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The sign is a WORD, never a hyphen.</b> A bare "-97" is read
        /// differently by every voice and punctuation setting — "dash 97",
        /// "97", "minus 97" — and the operator has no second opinion about
        /// their signal strength. The same house rule already governs stereo
        /// pan (<c>MetersPanel.DescribePan</c>), for the same reason.
        /// </para>
        /// <para>
        /// <b>The unit is never omitted.</b> Two keys read this meter, one in
        /// S-units and one in dBm (#306), and the whole point of having two is
        /// that the operator need not remember which they asked for.
        /// </para>
        /// <para>
        /// Lives here rather than beside its callers so the flat Ctrl+S dBm
        /// MODE and the Ctrl+J, Ctrl+S chord cannot grow two vocabularies for
        /// one measurement.
        /// </para>
        /// </remarks>
        public static string SpokenDbm(int dbm)
            => dbm < 0
                ? Lexicon.Get("audio.smeter.dbm_negative", ("smeter", -dbm))
                : Lexicon.Get("audio.smeter.dbm", ("smeter", dbm));

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

        /// <summary>
        /// The whole spoken answer to "what is the meter reading?", in every
        /// state the meter has: transmitting, receiving in dBm, receiving in
        /// S-units, and receiving above S9.
        /// </summary>
        /// <param name="transmitting">Keyed. The meter is not describing
        /// anyone's signal, so the honest answer is forward power.</param>
        /// <param name="spokenForwardPower">Forward power already rendered
        /// with its unit — <c>FlexBase.FormatForwardPowerSpoken</c>. Passed in
        /// rather than computed here so this class stays a pure function of
        /// numbers and can be tested without a radio.</param>
        /// <param name="inDbm">The operator's persisted unit choice for this
        /// radio.</param>
        /// <param name="rawDbm">The meter as the radio sent it. Used only when
        /// <paramref name="inDbm"/>, and read from <c>RawSMeter</c> so it does
        /// not depend on a mode being read twice.</param>
        /// <param name="sUnits">The meter in the app's S-unit encoding — which
        /// is dB-over-S9 plus 9 above S9. See the trap in this class's own
        /// remarks.</param>
        /// <remarks>
        /// <para>
        /// <b>Written because two surfaces answered the same question
        /// differently and one of them was wrong (#353).</b> Ctrl+S composed
        /// this correctly. The Home S-meter field read its own DISPLAY back
        /// verbatim into "S meter {reading}", so in dBm it said "S meter -97"
        /// — a hyphen-minus, at the mercy of the reader's punctuation level,
        /// which speaks as "dash 97", "minus 97" or just "97" depending on
        /// settings nobody in this app controls. A reading of "97" that means
        /// minus 97 dBm is not a degraded announcement, it is a wrong one.
        /// </para>
        /// <para>
        /// <b>The same read-back also broke above S9</b>, where the display
        /// carries "+4" and the field spoke "S meter plus 4" — four of what,
        /// over what. Both are the one defect: a surface rendering a
        /// measurement instead of asking the thing that knows how.
        /// </para>
        /// <para>
        /// <b>Why this matters more than it used to.</b> dBm was a mode with
        /// no key, no Settings control and no persistence — nobody could stay
        /// in it by accident or across a restart. Since #337 it persists per
        /// radio, so an operator who prefers dBm lives there for months and
        /// every press of Space on that field was wrong for all of them.
        /// </para>
        /// </remarks>
        public static string Spoken(bool transmitting, string spokenForwardPower,
                                    bool inDbm, int rawDbm, int sUnits)
        {
            // Transmit first, and the unit choice does not change it: while
            // keyed the meter is not a signal report at all.
            if (transmitting)
                return Lexicon.Get("audio.smeter.power", ("power", spokenForwardPower));

            if (inDbm) return SpokenDbm(rawDbm);

            return IsOverS9(sUnits)
                ? Lexicon.Get("audio.smeter.over_s9", ("over", ExcessOverS9(sUnits)))
                : Lexicon.Get("audio.smeter.s_units", ("smeter", sUnits));
        }
    }
}
