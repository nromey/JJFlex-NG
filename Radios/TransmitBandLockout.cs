#nullable enable
using System;
using System.Collections.Generic;
using HamBands;

namespace Radios
{
    /// <summary>
    /// The transmit slice as one reading, for the TX band lockout: its
    /// frequency, its mode and its own filter edges, all from the same slice
    /// object (<see cref="FlexBase.TransmitSliceSignal"/>).
    /// </summary>
    /// <param name="Hz">The transmit slice's frequency, in hertz.</param>
    /// <param name="Mode">The transmit slice's mode, as FlexLib names it ("LSB", "DIGU", ...).</param>
    /// <param name="FilterLow">That slice's filter low edge, as FlexLib reports it (signed: negative in LSB).</param>
    /// <param name="FilterHigh">That slice's filter high edge, as FlexLib reports it.</param>
    public readonly record struct TransmitSliceReading(ulong Hz, string Mode, int FilterLow, int FilterHigh)
    {
        /// <summary>
        /// A second frequency the slice may transmit on, judged as well as
        /// <see cref="Hz"/> and refused if either is outside the licence; null
        /// when there is only one (#1207: a transmit offset in an FM-like mode
        /// nobody has read on a radio, which may or may not apply it).
        /// </summary>
        public ulong? AlsoJudgeHz { get; init; }
    }

    /// <summary>
    /// The TX band lockout's judgement: whether the signal a slice would send
    /// sits wholly inside one band division the operator's licence allows.
    /// Pure, so <c>Radios.Tests</c> tests the very code
    /// <c>FreqOutHandlers.CanTransmitHere</c> runs (#1173).
    /// </summary>
    /// <remarks>
    /// <para><b>The transmit slice is judged, not the selected one</b> (#1173).
    /// On a split the receive slice and the transmit slice differ, and the
    /// lockout used to judge the receive frequency with the selected slice's
    /// filter: a transmit slice outside the licence keyed, and one inside it
    /// was refused when the receive slice was outside.</para>
    /// <para><b>The edges are the transmit slice's own receive filter, never the
    /// radio's transmit filter.</b> FlexLib clamps <c>Radio.TXFilterLow</c> at 0
    /// or above, so it carries no side: put into this arithmetic it would judge
    /// every lower-sideband signal on the wrong side of the carrier.</para>
    /// <para><b>No transmit slice of ours</b> (<c>TXFrequency</c> reads 0, for
    /// instance while another client holds transmit): judged exactly as before
    /// this change, the selected slice's frequency and filter, and the caller
    /// traces that it did. Never a refusal on a 0 frequency, which would speak
    /// the licence sentence for a reason that has nothing to do with the
    /// licence.</para>
    /// </remarks>
    public static class TransmitBandLockout
    {
        /// <summary>
        /// Which slice the lockout judges: the transmit slice when there is one
        /// of ours with a frequency, otherwise the selected slice.
        /// </summary>
        /// <param name="transmit">The transmit slice's reading, or null when none of ours is designated.</param>
        /// <param name="selectedHz">The selected slice's frequency (today's <c>GetRXFrequency</c>).</param>
        /// <param name="selectedLow">The selected slice's filter low edge.</param>
        /// <param name="selectedHigh">The selected slice's filter high edge.</param>
        /// <param name="license">The operator's licence class.</param>
        /// <param name="judgedSelected">True when there was no transmit slice to judge, so the selected slice was.</param>
        /// <returns>True when transmitting is allowed.</returns>
        public static bool Judge(TransmitSliceReading? transmit, ulong selectedHz, int selectedLow, int selectedHigh,
                                 Bands.Licenses license, out bool judgedSelected)
        {
            if (transmit is { } t && t.Hz != 0UL)
            {
                judgedSelected = false;
                if (!Allows(t.Hz, t.Mode, t.FilterLow, t.FilterHigh, license)) return false;
                // Not established which of two frequencies the radio uses: both must pass (#1207).
                return t.AlsoJudgeHz is not { } also || Allows(also, t.Mode, t.FilterLow, t.FilterHigh, license);
            }
            judgedSelected = true;
            // Exactly as before #1173: no mode, so today's arithmetic.
            return Allows(selectedHz, null, selectedLow, selectedHigh, license);
        }

        /// <summary>
        /// Whether a signal at <paramref name="carrierHz"/> in
        /// <paramref name="mode"/> with these filter edges lies wholly inside
        /// one division the licence allows: the band by the signal's low edge,
        /// that band's divisions for the licence, and the whole signal inside
        /// one of them. The arithmetic <c>CanTransmitHere</c> has always used;
        /// only the extent now goes through <see cref="SignalExtent"/>.
        /// </summary>
        public static bool Allows(ulong carrierHz, string? mode, int filterLow, int filterHigh, Bands.Licenses license)
        {
            var (signalLow, signalHigh) = SignalExtent(carrierHz, mode, filterLow, filterHigh);

            var bandInfo = Bands.Query((ulong)signalLow);
            if (bandInfo == null) return false;   // not on any band

            var licensedBand = Bands.Query(bandInfo.Band, license);
            if (licensedBand?.Divisions == null) return false;   // nothing for this licence on this band

            foreach (var div in licensedBand.Divisions)
            {
                if ((ulong)signalLow >= div.Low && (ulong)signalHigh <= div.High)
                    return true;
            }
            return false;
        }

        // ---------------- the mode, as well as the frequency (#1310) ----------------

        /// <summary>
        /// What Part 97 calls a FlexLib slice mode, for judging which divisions
        /// allow it (#1310, ruled by Noel 2026-11-26: the lockout judges mode as
        /// well as frequency).
        /// </summary>
        public enum EmissionKind
        {
            /// <summary>CW: allowed anywhere in the class's privileges (97.305(a)).</summary>
            Cw,
            /// <summary>Single-sideband voice, J3E (USB, LSB).</summary>
            SsbPhone,
            /// <summary>Any other phone (97.3(c)(5)): AM, SAM, DSB, the FM modes,
            /// D-STAR and FreeDV.</summary>
            OtherPhone,
            /// <summary>A sound-card digital mode (DIGU, DIGL): data, or image
            /// such as SSTV, so it is allowed wherever either is.</summary>
            DataOrImage,
            /// <summary>The radio's own RTTY mode.</summary>
            Rtty,
        }

        /// <summary>
        /// The kind of emission a FlexLib mode name sends, case ignored; null for
        /// no mode or one this table does not know, which is then judged on
        /// frequency alone, as the lockout always judged it.
        /// </summary>
        /// <remarks>
        /// <para><b>FreeDV is phone.</b> 97.3(c)(5): "Speech and other sound
        /// emissions having designators with A, C, D, F, G, H, J or R as the first
        /// symbol; 1, 2, 3 or X as the second symbol; E as the third symbol"
        /// (eCFR, Title 47, current to 2026-11-20, read 2026-11-26). FreeDV sent
        /// through a sideband transmitter is J2E. It is not single sideband voice
        /// (J3E or R3E), so it is not allowed where only those are (Novice and
        /// Technician in 28.3 to 28.5 MHz, 97.307(f)(10)). FlexLib names the
        /// FreeDV waveform's modes FDV, FDVU and FDVL; anything starting FDV is
        /// read as FreeDV.</para>
        /// <para><b>DIGU and DIGL are data or image</b>, because the program
        /// feeding them decides which (an FT8 program sends data, an SSTV program
        /// image), so they are refused only where neither is allowed.</para>
        /// </remarks>
        public static EmissionKind? KindOf(string? mode)
        {
            string m = (mode ?? "").Trim().ToUpperInvariant();
            if (m.Length == 0) return null;
            if (m.StartsWith("FDV", StringComparison.Ordinal)) return EmissionKind.OtherPhone;
            return m switch
            {
                "CW" => EmissionKind.Cw,
                "USB" or "LSB" => EmissionKind.SsbPhone,
                "AM" or "SAM" or "DSB" or "FM" or "NFM" or "DFM" or "DSTR" => EmissionKind.OtherPhone,
                "DIGU" or "DIGL" => EmissionKind.DataOrImage,
                "RTTY" => EmissionKind.Rtty,
                _ => null,
            };
        }

        /// <summary>Whether a division with these modes allows <paramref name="kind"/>.
        /// A division that lists no modes allows every one.</summary>
        internal static bool Permits(Bands.Modes[]? modes, EmissionKind kind)
        {
            if (kind == EmissionKind.Cw || modes == null || modes.Length == 0) return true;
            foreach (var m in modes)
            {
                switch (kind)
                {
                    case EmissionKind.SsbPhone when m is Bands.Modes.PhoneCW or Bands.Modes.USB or Bands.Modes.SSB:
                    case EmissionKind.OtherPhone when m is Bands.Modes.PhoneCW:
                    case EmissionKind.DataOrImage when m is Bands.Modes.RTTYData or Bands.Modes.M60Data or Bands.Modes.Data or Bands.Modes.Image:
                    case EmissionKind.Rtty when m is Bands.Modes.RTTYData:
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// <see cref="Judge"/>, returning WHY a transmission is refused (#1310),
        /// and judging the mode as well as the frequency.
        /// </summary>
        /// <param name="modeUnrecognised">True when the judged slice had a mode
        /// <see cref="KindOf"/> does not know, which was then judged on frequency
        /// alone (the caller traces it).</param>
        /// <remarks>
        /// With no transmit slice of ours the selected slice is judged with no
        /// mode, exactly as before #1173, so on frequency alone: another client
        /// holding transmit can never get a mode refusal.
        /// </remarks>
        public static LockoutReason JudgeReason(TransmitSliceReading? transmit, ulong selectedHz, int selectedLow, int selectedHigh,
                                                Bands.Licenses license, out bool judgedSelected, out bool modeUnrecognised)
        {
            modeUnrecognised = false;
            if (transmit is { } t && t.Hz != 0UL)
            {
                judgedSelected = false;
                var first = AllowsReason(t.Hz, t.Mode, t.FilterLow, t.FilterHigh, license, out modeUnrecognised);
                if (first != LockoutReason.None) return first;
                // Not established which of two frequencies the radio uses: both must pass (#1207).
                return t.AlsoJudgeHz is not { } also ? LockoutReason.None
                    : AllowsReason(also, t.Mode, t.FilterLow, t.FilterHigh, license, out _);
            }
            judgedSelected = true;
            return Allows(selectedHz, null, selectedLow, selectedHigh, license) ? LockoutReason.None : LockoutReason.Frequency;
        }

        /// <summary>
        /// Whether a signal at <paramref name="carrierHz"/> in
        /// <paramref name="mode"/> is lawful for the licence (#1310): the whole
        /// signal inside the class's privileges where its mode is allowed.
        /// </summary>
        /// <remarks>
        /// <para><b>Contiguous divisions are one stretch.</b> The signal may
        /// cross a seam between two divisions that both allow its mode, and CW,
        /// which every division allows (97.305(a)), may cross any seam inside the
        /// class's privileges. Until this change a CW signal straddling 7.125,
        /// 14.150 or 28.3 MHz was refused, by a few hundred hertz, though CW is
        /// legal on both sides.</para>
        /// <para><b>Frequency or mode.</b> A signal wholly inside the class's
        /// privileges whose mode is not allowed where it sits is refused for its
        /// MODE; one with any part outside them, for its FREQUENCY.</para>
        /// <para><b>No mode, or one <see cref="KindOf"/> does not know</b>, is
        /// judged exactly as <see cref="Allows"/> always judged it, on frequency
        /// alone, and never refused for its mode.</para>
        /// </remarks>
        public static LockoutReason AllowsReason(ulong carrierHz, string? mode, int filterLow, int filterHigh,
                                                 Bands.Licenses license, out bool modeUnrecognised)
        {
            var kind = KindOf(mode);
            modeUnrecognised = kind == null && !string.IsNullOrWhiteSpace(mode);
            if (kind == null)
                return Allows(carrierHz, mode, filterLow, filterHigh, license) ? LockoutReason.None : LockoutReason.Frequency;

            var (signalLow, signalHigh) = SignalExtent(carrierHz, mode, filterLow, filterHigh);
            var bandInfo = Bands.Query((ulong)signalLow);
            if (bandInfo == null) return LockoutReason.Frequency;
            var licensedBand = Bands.Query(bandInfo.Band, license);
            if (licensedBand?.Divisions == null) return LockoutReason.Frequency;

            if (Inside(Stretches(licensedBand.Divisions, d => Permits(d.Mode, kind.Value)), signalLow, signalHigh))
                return LockoutReason.None;
            return Inside(Stretches(licensedBand.Divisions, _ => true), signalLow, signalHigh)
                ? LockoutReason.Mode : LockoutReason.Frequency;
        }

        /// <summary>The chosen divisions as contiguous stretches: divisions that
        /// overlap or meet are joined.</summary>
        private static List<(ulong Low, ulong High)> Stretches(Bands.BandDivision[] divisions, Func<Bands.BandDivision, bool> chosen)
        {
            var spans = new List<(ulong Low, ulong High)>();
            foreach (var d in divisions)
                if (chosen(d)) spans.Add((d.Low, d.High));
            spans.Sort((a, b) => a.Low.CompareTo(b.Low));
            var joined = new List<(ulong Low, ulong High)>();
            foreach (var s in spans)
            {
                if (joined.Count > 0 && s.Low <= joined[^1].High)
                    joined[^1] = (joined[^1].Low, Math.Max(joined[^1].High, s.High));
                else
                    joined.Add(s);
            }
            return joined;
        }

        private static bool Inside(List<(ulong Low, ulong High)> stretches, long low, long high)
        {
            foreach (var s in stretches)
                if ((ulong)low >= s.Low && (ulong)high <= s.High) return true;
            return false;
        }

        /// <summary>
        /// The mode table: where the signal sits, in hertz, given the carrier
        /// and a slice's filter edges.
        /// </summary>
        /// <remarks>
        /// <list type="bullet">
        /// <item>LSB and DIGL: the edges below the carrier.</item>
        /// <item>USB and DIGU: the edges above it.</item>
        /// <item>CW, AM, FM and every other mode, or none: the carrier plus each
        /// edge as given, which is what the lockout has always done.</item>
        /// </list>
        /// <para>FlexLib already signs a slice's filter (negative edges in LSB),
        /// so for its edges every row gives the same answer as before. The two
        /// sideband rows also put an edge pair written without its sign (both
        /// at or above 0 for a lower sideband, both at or below 0 for an upper
        /// one) on the side the mode transmits, so an unsigned pair can never
        /// be judged as the mirror image of the real signal. The extent is
        /// clamped at 0, as before.</para>
        /// </remarks>
        public static (long Low, long High) SignalExtent(ulong carrierHz, string? mode, int filterLow, int filterHigh)
        {
            long carrier = (long)carrierHz;
            long a = filterLow, b = filterHigh;
            long low, high;
            switch ((mode ?? "").Trim().ToUpperInvariant())
            {
                case "LSB":
                case "DIGL":
                    if (a >= 0 && b >= 0 && (a | b) != 0)
                    {
                        // Written without the sign: the signal is below the carrier.
                        low = carrier - Math.Max(a, b);
                        high = carrier - Math.Min(a, b);
                    }
                    else
                    {
                        low = carrier + Math.Min(a, b);
                        high = carrier + Math.Max(a, b);
                    }
                    break;
                case "USB":
                case "DIGU":
                    if (a <= 0 && b <= 0 && (a | b) != 0)
                    {
                        // Written below zero: the signal is above the carrier.
                        low = carrier + Math.Min(-a, -b);
                        high = carrier + Math.Max(-a, -b);
                    }
                    else
                    {
                        low = carrier + Math.Min(a, b);
                        high = carrier + Math.Max(a, b);
                    }
                    break;
                default:
                    // CW, AM, SAM, FM, NFM, DFM, RTTY and the rest, as before.
                    low = carrier + a;
                    high = carrier + b;
                    break;
            }
            if (low < 0) low = 0;
            if (high < 0) high = 0;
            return (low, high);
        }
    }
}
