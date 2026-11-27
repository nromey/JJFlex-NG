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

        /// <summary>
        /// The radio's transmit filter, low edge, in hertz, as FlexLib reports it
        /// (at or above 0, so it carries no side; the mode says which side). Read
        /// for the 60 m rule's emission judgement (#1161), and by the lockout for
        /// USB, LSB, Digital USB and Digital LSB only
        /// (<see cref="TransmitBandLockout.JudgedEdges"/>, repair round 1; CW is
        /// judged at a fixed 150 Hz since repair round 2, and every other mode by the
        /// slice's own filter). 0 with <see cref="TxFilterHigh"/> 0 means not
        /// reported.
        /// </summary>
        public int TxFilterLow { get; init; }

        /// <summary>The radio's transmit filter, high edge, in hertz (see <see cref="TxFilterLow"/>).</summary>
        public int TxFilterHigh { get; init; }
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
    /// <para><b>The edges.</b> <see cref="Judge"/> and <see cref="Allows"/> judge
    /// the edges they are given. <see cref="JudgeReason"/>, the judge the app runs,
    /// gives them the radio's transmit filter for USB, LSB, Digital USB and Digital
    /// LSB, placed on the mode's side because FlexLib clamps
    /// <c>Radio.TXFilterLow</c> at 0 or above and so carries no side (repair
    /// round 1 of junk box batch 31); CW as 150 Hz centred on the carrier (repair
    /// round 2); and the transmit slice's own filter for every other mode
    /// (<see cref="JudgedEdges"/>).</para>
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
            /// <summary>Any other phone (97.3(c)(5)): AM, SAM, DSB, AME (FlexLib's
            /// other AM-family mode, known since repair round 2), the FM modes,
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
                "AM" or "SAM" or "DSB" or "AME" or "FM" or "NFM" or "DFM" or "DSTR" => EmissionKind.OtherPhone,
                "DIGU" or "DIGL" => EmissionKind.DataOrImage,
                "RTTY" => EmissionKind.Rtty,
                _ => null,
            };
        }

        /// <summary>
        /// The app's own default transmit filter, <see cref="AudioChainPreset.TxFilterLow"/>
        /// to <see cref="AudioChainPreset.TxFilterHigh"/> (100 to 2,900 Hz), read from
        /// the preset's own defaults so the two cannot drift.
        /// </summary>
        private static readonly (int Low, int High) DefaultTxFilter =
            (new AudioChainPreset().TxFilterLow, new AudioChainPreset().TxFilterHigh);

        /// <summary>
        /// The radio's transmit filter as FlexLib reports it (unsigned), or, when it
        /// is not reported (both edges 0), the app's default transmit filter, with
        /// <c>Assumed</c> set so a refusal can say the width was assumed.
        /// </summary>
        /// <remarks>
        /// The orchestrator's decision for repair round 1 of junk box batch 31 (night
        /// question J4): during the moment a radio has not reported its filter, a real
        /// filter the operator widened is judged as the default, so it would be
        /// admitted. A RigSurface read just after connecting settles whether that
        /// moment exists. The 3 kHz snapping margin it replaces refused a correct
        /// channel dial and blamed a filter nobody had measured.
        /// </remarks>
        public static (int Low, int High, bool Assumed) TransmitFilterOrDefault(int txLow, int txHigh)
        {
            if (txLow == 0 && txHigh == 0) return (DefaultTxFilter.Low, DefaultTxFilter.High, true);
            return (Math.Max(0, Math.Min(txLow, txHigh)), Math.Max(0, Math.Max(txLow, txHigh)), false);
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
                var (low, high) = JudgedEdges(t);
                var first = AllowsReason(t.Hz, t.Mode, low, high, license, out modeUnrecognised);
                if (first != LockoutReason.None) return first;
                // Not established which of two frequencies the radio uses: both must pass (#1207).
                return t.AlsoJudgeHz is not { } also ? LockoutReason.None
                    : AllowsReason(also, t.Mode, low, high, license, out _);
            }
            judgedSelected = true;
            return Allows(selectedHz, null, selectedLow, selectedHigh, license) ? LockoutReason.None : LockoutReason.Frequency;
        }

        /// <summary>
        /// The filter edges the lockout judges a transmit slice's signal by
        /// (repair round 1 of junk box batch 31, from #1161's 2026-11-19 05:33
        /// note): for USB, LSB, Digital USB and Digital LSB, the radio's TRANSMIT
        /// filter, unsigned, which <see cref="SignalExtent"/> places on the mode's
        /// side, or the app's default when it is not reported
        /// (<see cref="TransmitFilterOrDefault"/>); for CW, 150 Hz centred on the
        /// carrier (repair round 2); for every other mode, the slice's own filter,
        /// as before.
        /// </summary>
        /// <remarks>
        /// <para>A sideband signal is shaped by the transmit filter, so judging it by
        /// the slice's receive filter refused a lawful channel transmission whenever
        /// the receive filter was wider than 2.8 kHz (General, 5330.5 USB, transmit
        /// filter 100 to 2,900, slice filter 100 to 3,000: refused for its frequency).
        /// The old reason for avoiding the transmit filter, that FlexLib clamps it at 0
        /// or above and so carries no side, is answered for these four modes by the
        /// mode giving the side. It is not answered for the rest: an FM signal's width
        /// comes from its deviation, which no transmit filter describes, so FM, AM,
        /// SAM, DSB, AME, FreeDV, RTTY and an unrecognised mode keep the slice
        /// filter.</para>
        /// <para><b>CW at 150 Hz on every band</b> (repair round 2 of junk box
        /// batch 31): a TRANSMIT-PATH POLICY CHANGE, the orchestrator's decision,
        /// awaiting Noel in night question J4. The slice's receive filter is not
        /// the CW signal (the shipped Normal preset is plus and minus 250 Hz, the
        /// Monitor preset 2 kHz), so lawful CW within a few hundred hertz of a
        /// privilege edge was refused. 150HA1A, the designator 97.303(h)(3) gives
        /// for CW on the 60 m channels, is used everywhere, the same width the 60 m
        /// rule uses (<see cref="SixtyMeterChannels.CwWidthHz"/>, one value). No
        /// class gains a mode anywhere: CW is permitted in every division already
        /// (<see cref="Permits"/>, 97.305(a)); only signals near an edge change, CW
        /// within 75 Hz to 1,925 Hz of it newly admitted, and CW with a filter
        /// narrower than 150 Hz near an edge newly refused.</para>
        /// <para><b>Two orchestrator's decisions</b> (night question J4): the
        /// unreported fallback (100 to 2,900 Hz) now applies to the lockout on every
        /// band, not only 60 m; and for Digital USB and Digital LSB this rests on the
        /// assumption, not established, that a Flex radio applies the transmit filter
        /// in those modes as in USB and LSB. A RigSurface or panadapter reading of a
        /// Digital USB test tone would settle it.</para>
        /// </remarks>
        internal static (int Low, int High) JudgedEdges(TransmitSliceReading t)
        {
            switch ((t.Mode ?? "").Trim().ToUpperInvariant())
            {
                case "USB":
                case "LSB":
                case "DIGU":
                case "DIGL":
                    var (low, high, _) = TransmitFilterOrDefault(t.TxFilterLow, t.TxFilterHigh);
                    return (low, high);
                case "CW":
                    return (-(int)(SixtyMeterChannels.CwWidthHz / 2), (int)(SixtyMeterChannels.CwWidthHz / 2));
                default:
                    return (t.FilterLow, t.FilterHigh);
            }
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
        /// edge as given, which is what the lockout has always done. (Which edges
        /// it is given is <see cref="JudgedEdges"/>'s: since repair round 2, CW's are
        /// a fixed 150 Hz.)</item>
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
