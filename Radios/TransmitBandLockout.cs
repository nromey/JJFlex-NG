#nullable enable
using System;
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
