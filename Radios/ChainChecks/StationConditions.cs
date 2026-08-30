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
