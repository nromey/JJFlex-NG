using System;
using System.Globalization;

namespace Radios.ChainChecks
{
    /// <summary>
    /// The one place that reads and states the conditions a transmit
    /// measurement was taken under: frequency, mode and antenna port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists (#399).</b> Every transmit stage of the 2026-08-29 run
    /// on Don's radio recorded the frequency as unknown, and two of the three
    /// keying stages recorded no frequency at all. <i>A document describing an
    /// on-air transmission that cannot say where it transmitted is a defect in
    /// its own right</i>, and it is worse into a real antenna than into a dummy
    /// load.
    /// </para>
    /// <para>
    /// <b>It is also a dedup.</b> <see cref="TxTuneProbeRunner"/> and
    /// <see cref="TxDifferentialCapture"/> each carried their own byte-identical
    /// <c>SafeFrequency</c>, <c>SafeMode</c> and <c>SafeAntenna</c>. Two homes
    /// for one idea is this project's dominant defect, and it is what made the
    /// gap below need fixing twice. One home now, and every reader of a transmit
    /// condition goes through it.
    /// </para>
    /// <para>
    /// <b>The fallback, and why it is labelled rather than silent.</b>
    /// <c>FlexBase.TXFrequency</c> is a cached echo: it is filled when the radio
    /// reports a transmit slice or reports that slice's frequency changing, so a
    /// session where neither event has been seen holds zero — and zero, formatted,
    /// is "0.000000 MHz", which is a plausible-looking lie. Zero therefore falls
    /// back to the receive slice, <b>and says so in the same breath</b>. On a
    /// station that is not split those are the same slice and the number is the
    /// right one; on a split station they are not, and the reader has been told
    /// which one they are holding. What must never happen is a bare number whose
    /// provenance nobody can recover afterwards.
    /// </para>
    /// <para>
    /// <b>Never throws.</b> A condition that cannot be read says so; it never
    /// guesses, and it never takes the measurement down with it.
    /// </para>
    /// </remarks>
    public static class StationConditions
    {
        /// <summary>What a reader sees when the radio simply has no value.</summary>
        public const string NotReported = "not reported";

        /// <summary>What a reader sees when asking the radio threw.</summary>
        public const string CouldNotBeRead = "could not be read";

        /// <summary>
        /// Hertz as an operator says a frequency: six decimal places of
        /// megahertz. Invariant, because this string is read by a support desk
        /// as often as by the operator and a comma decimal separator would be
        /// read as a thousands separator.
        /// </summary>
        public static string Format(ulong hz)
            => (hz / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture) + " MHz";

        /// <summary>
        /// The transmit frequency, in words, with the receive-slice fallback
        /// named when it is used. Never empty.
        /// </summary>
        public static string Frequency(FlexBase rig)
        {
            ulong tx;
            try { tx = rig?.TXFrequency ?? 0UL; }
            catch { return CouldNotBeRead; }

            if (tx != 0UL) return Format(tx);

            ulong rx;
            try { rx = rig?.RXFrequency ?? 0UL; }
            catch { return NotReported; }

            if (rx == 0UL) return NotReported;

            // Named, not substituted. See the class remarks: a bare number here
            // would be indistinguishable from a transmit-slice reading, and on a
            // split station it would be the wrong one.
            return Format(rx)
                 + " (the receive slice — the radio has not reported a transmit "
                 + "slice frequency this session)";
        }

        /// <summary>The transmit frequency in hertz, or 0 when it is not known.
        /// For callers that need the number rather than the sentence — the
        /// frequency hand-off pre-fills its box from this.</summary>
        public static ulong FrequencyHz(FlexBase rig)
        {
            try
            {
                ulong tx = rig?.TXFrequency ?? 0UL;
                if (tx != 0UL) return tx;
                return rig?.RXFrequency ?? 0UL;
            }
            catch { return 0UL; }
        }

        /// <summary>The transmit mode, in words. Never empty.</summary>
        public static string Mode(FlexBase rig)
        {
            try
            {
                string m = rig?.TXMode;
                return string.IsNullOrWhiteSpace(m) ? NotReported : m;
            }
            catch { return CouldNotBeRead; }
        }

        /// <summary>The transmit antenna port, in words. Never empty.</summary>
        public static string Antenna(FlexBase rig)
        {
            try
            {
                string a = rig?.TXAntennaName;
                return string.IsNullOrWhiteSpace(a) ? NotReported : a;
            }
            catch { return CouldNotBeRead; }
        }

        // ------------------------------------------------------------------
        //  Changing a condition mid-run: the shared refusal and confirmation
        //  vocabulary (#399, #411).
        // ------------------------------------------------------------------
        //
        // The Fixer's frequency hand-off and its mode hand-off make the same
        // promise — refuse while keyed, then report what the radio NOW says
        // rather than what was asked for (#164: the ack is not proof). The
        // frequency hand-off carried these sentences inline, and the Sprint 42
        // integration pass found that inlined vocabulary is exactly how the
        // Fixer grew a second frequency parser beside the one Home already
        // had. So the words live here, once, and both hand-offs read them.

        /// <summary>
        /// Whether the radio is keyed, FAILING CLOSED: a rig that cannot be
        /// asked — null, torn down mid-read, mid-disconnect — reads as keyed,
        /// because the cost of wrongly allowing a change under a live carrier
        /// is RF, and the cost of wrongly refusing one is a second press.
        /// </summary>
        public static bool KeyedFailClosed(FlexBase rig)
        {
            try { return rig == null || rig.Transmit || rig.TxTune; }
            catch { return true; }
        }

        /// <summary>
        /// The refusal a hand-off speaks when <see cref="KeyedFailClosed"/>
        /// says no. Spoken rather than silently ignored: a button that does
        /// nothing reads as a broken button.
        /// </summary>
        public static string RefusedWhileKeyed(string what)
            => "The radio is transmitting. Wait until it stops, then change the "
             + what + ".";

        /// <summary>
        /// A transmit mode in WORDS, for anything a screen reader will read
        /// aloud. Returns the token unchanged when it is not one we know.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Noel, 2026-08-30, reading the Fixer's confirmations: <i>"expand the
        /// mode"</i>. The reason is sharper than tidiness. A reader says
        /// <c>USB</c> as three letters, and the one dialog these sentences are
        /// spoken in is the transmit-audio walk — where the operator's
        /// microphone is very often plugged into a USB interface, and where
        /// half the surrounding prose is about USB devices. "The radio now
        /// reports U S B" is a genuine collision, in the one place it can least
        /// afford one.
        /// </para>
        /// <para>
        /// Returns the BARE phrase, because Noel's own two examples need
        /// different endings — "set to upper sideband" reads naturally, while
        /// "reports upper sideband" does not and wants the noun: "reports
        /// upper sideband mode". So the caller adds "mode" where its sentence
        /// needs it. That noun is what finally kills the cable reading, so a
        /// caller writing a bare-sounding sentence should include it.
        /// </para>
        /// <para>
        /// Unknown tokens pass through as themselves rather than being
        /// swallowed or guessed at. The radio may report a mode this build has
        /// never heard of — saying it back verbatim is honest, and saying
        /// nothing is not.
        /// </para>
        /// </remarks>
        public static string ModeInWords(string mode)
        {
            switch ((mode ?? "").Trim().ToUpperInvariant())
            {
                case "USB":  return "upper sideband";
                case "LSB":  return "lower sideband";
                case "DIGU": return "digital upper sideband";
                case "DIGL": return "digital lower sideband";
                case "":     return "";
                default:     return (mode ?? "").Trim();
            }
        }

        // ── The confirmation vocabulary, said in as few words as it takes ──
        //
        // Ruled by Noel 2026-08-31, reading the long forms these replaced:
        // "Again, too many words. Just 'mode change not accepted'. It already
        // shows what the mode is." And: "This is stuff that a ham, being techie
        // people that we are, don't need to be told simple stuff like this like
        // we're five."
        //
        // The value is deliberately NOT repeated back. The dialog already
        // displays the mode and the frequency; saying them again spends the
        // operator's time telling them something on screen in front of them.
        // What they cannot see is whether the RADIO agreed, and that is the
        // whole content of these three lines.
        //
        // "accepted" is doing precise work and is not a synonym for "sent".
        // #164: the radio acks transmit writes it does not apply, so every
        // sentence here is built from what the radio REPORTS afterwards.
        // There is deliberately no sentence in this vocabulary for "we sent
        // the command" — that is the sentence the frequency hand-off shipped
        // with once, and it told an operator they had moved when they had not.

        /// <summary>"Mode change accepted." — the radio's own report agrees
        /// with what was asked for.</summary>
        public static string ChangeAccepted(string what)
            => Capitalize(what) + " change accepted.";

        /// <summary>"Mode change not accepted." — the radio answered with
        /// something else. What it reports instead is on screen.</summary>
        public static string ChangeNotAccepted(string what)
            => Capitalize(what) + " change not accepted.";

        /// <summary>
        /// The radio is reporting nothing at all for this condition. Three
        /// extra words, and they earn their place: every other line here is
        /// short because the display carries the value, and THIS is the case
        /// where the display has nothing to carry.
        /// </summary>
        public static string ChangeNotAcceptedNothingReported(string what)
            => Capitalize(what) + " change not accepted. No " + (what ?? "").ToLowerInvariant()
             + " reported.";

        private static string Capitalize(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>
        /// One evidence line naming all three, for a stage that keyed the
        /// transmitter. Read AT THE MOMENT OF THE MEASUREMENT, never afterwards
        /// — the operator may retune between stages, and the whole point of
        /// recording this per stage is that a run measured at two frequencies
        /// says which stage ran where (#399).
        /// </summary>
        public static string Line(FlexBase rig)
            => "Frequency: " + Frequency(rig) + Environment.NewLine
             + "Mode: " + Mode(rig) + Environment.NewLine
             + "Transmit antenna: " + Antenna(rig) + Environment.NewLine;

        /// <summary>
        /// " on 14.250000 MHz in USB", or as much of it as is actually known —
        /// for a sentence that tells an operator what pressing Run will do.
        /// Empty when nothing is known, because an empty clause is better than
        /// a clause that says nothing twice.
        /// </summary>
        public static string OnInPhrase(string frequency, string mode)
        {
            string s = "";
            if (!string.IsNullOrWhiteSpace(frequency)
                && frequency != NotReported && frequency != CouldNotBeRead)
                s += " on " + frequency.Trim();
            if (!string.IsNullOrWhiteSpace(mode)
                && mode != NotReported && mode != CouldNotBeRead)
                s += " in " + mode.Trim();
            return s;
        }

        /// <summary>
        /// Read a frequency the way an operator types one: megahertz with a
        /// decimal point, or the app's own dotted MHz.kHz.Hz grouping, or a
        /// bare number of kilohertz.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Accepted forms, and they are the same three the app's frequency entry
        /// has always accepted: <c>14.250.000</c> (MHz.kHz.Hz), <c>14.250</c>
        /// (MHz.kHz), and <c>14250</c> (kHz). A single group with a decimal
        /// point and more than three digits after it is read as megahertz, so
        /// <c>14.25</c> and <c>14.250000</c> both land on 14.250000 MHz.
        /// </para>
        /// <para>
        /// <b>There is a third copy of this idea in the VB app</b> —
        /// <c>FormatFreqForRadio</c> in <c>globals.vb</c>, which is <c>Friend</c>
        /// in the executable's own assembly and therefore unreachable from here
        /// or from JJFlexWpf. Unifying them means moving that function into
        /// Radios, which is a file this track does not own. Reported rather than
        /// done.
        /// </para>
        /// </remarks>
        /// <returns>True when <paramref name="text"/> named a frequency; false
        /// leaves <paramref name="hz"/> at zero and the caller refuses.</returns>
        public static bool TryParse(string text, out ulong hz)
        {
            hz = 0UL;
            string s = (text ?? "").Trim().Replace(" ", "");
            if (s.Length == 0) return false;

            foreach (char c in s)
                if (!char.IsDigit(c) && c != '.') return false;

            string[] parts = s.Split('.');
            switch (parts.Length)
            {
                case 1:
                {
                    // Bare digits are kilohertz — "14250" is 20 metres, not
                    // 14.25 kHz. That is the convention the app's own entry has
                    // always used and the one an operator types fastest.
                    if (!ulong.TryParse(parts[0], NumberStyles.None,
                                        CultureInfo.InvariantCulture, out ulong khz))
                        return false;
                    hz = khz * 1_000UL;
                    return hz > 0UL;
                }

                case 2:
                {
                    if (parts[0].Length == 0 || parts[1].Length == 0) return false;
                    if (!ulong.TryParse(parts[0], NumberStyles.None,
                                        CultureInfo.InvariantCulture, out ulong mhz))
                        return false;
                    // "14.250" is 14 MHz plus 250 kHz; "14.250000" is 14 MHz
                    // plus 250000 Hz. The digit count decides, which is what
                    // makes both an operator's shorthand and our own printed
                    // form read back correctly.
                    if (!ulong.TryParse(parts[1], NumberStyles.None,
                                        CultureInfo.InvariantCulture, out ulong frac))
                        return false;
                    int digits = parts[1].Length;
                    if (digits > 6) return false;
                    ulong scale = 1UL;
                    for (int i = digits; i < 6; i++) scale *= 10UL;
                    hz = mhz * 1_000_000UL + frac * scale;
                    return hz > 0UL;
                }

                case 3:
                {
                    if (parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
                        return false;
                    if (parts[1].Length > 3 || parts[2].Length > 3) return false;
                    if (!ulong.TryParse(parts[0], NumberStyles.None,
                                        CultureInfo.InvariantCulture, out ulong m)
                        || !ulong.TryParse(parts[1], NumberStyles.None,
                                           CultureInfo.InvariantCulture, out ulong k)
                        || !ulong.TryParse(parts[2], NumberStyles.None,
                                           CultureInfo.InvariantCulture, out ulong h))
                        return false;
                    // Each group is padded to its own width: "14.25.0" is
                    // 14.250000 MHz, the same as the display form it mirrors.
                    for (int i = parts[1].Length; i < 3; i++) k *= 10UL;
                    for (int i = parts[2].Length; i < 3; i++) h *= 10UL;
                    hz = m * 1_000_000UL + k * 1_000UL + h;
                    return hz > 0UL;
                }

                default:
                    return false;
            }
        }
    }
}
