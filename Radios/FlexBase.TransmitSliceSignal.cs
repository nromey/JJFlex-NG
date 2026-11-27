#nullable enable
using System;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    public partial class FlexBase
    {
        /// <summary>
        /// The transmit slice of ours as one reading, for the TX band lockout
        /// (#1173): its frequency, mode and filter edges, all from the one
        /// slice the radio has designated for transmit. Null when none of our
        /// slices is designated, or its frequency reads 0.
        /// </summary>
        /// <remarks>
        /// The filter is that slice's own, as FlexLib reports it (signed), not
        /// the radio's transmit filter: FlexLib clamps <c>TXFilterLow</c> at 0 or
        /// above, which carries no side (see <see cref="TransmitBandLockout"/>).
        /// The frequency is the one <see cref="TXFrequency"/> reads for the
        /// designated slice, a tune still on its way included.
        /// <para><b>With XIT on, the frequency carries the XIT offset</b> (#1194):
        /// the slice transmits at its frequency plus <c>XITFreq</c>, up to
        /// 99,999 Hz either way, so that is the frequency judged. Read from the
        /// slice itself, as the filter is, so the reading stays one snapshot of
        /// one slice; never FlexBase's cached copy.</para>
        /// <para><b>In FM the slice's transmit offset is carried too</b> (#1207):
        /// <c>TXOffsetFreq</c>, signed MHz, the only field that moves the
        /// transmit frequency. Measured on an 8600 (readings of 2026-11-23,
        /// steps 3 and 4): in FM, 0.05 moved 14.250 to 14.300 MHz whatever the
        /// repeater direction said, at rest and keyed, and -0.05 moved it to
        /// 14.200; <c>FMRepeaterOffsetFreq</c> and <c>RepeaterOffsetDirection</c>
        /// moved nothing, as FlexLib's own comment says (stored in the radio,
        /// acted on by a client setting <c>TXOffsetFreq</c>), so neither is read
        /// here. This app never sets the offset today; another client can, and a
        /// value set earlier survives a simplex memory recall (2026-11-24,
        /// reading 1), so that is the slice this covers.</para>
        /// <para><b>Which modes, from the 2026-11-24 readings at rest</b>
        /// (<see cref="TxOffsetRuleFor"/>): in USB, plus or minus 0.05 left the
        /// transmit frequency on the dial, and the stored value came back into
        /// force the moment the mode returned to FM (reading a). So FM adds the
        /// offset and USB does not. Every other mode outside the FM family is
        /// judged as USB was, without the offset; that is presumed (LSB and the
        /// rest were not read). NFM, DFM and DSTR, the FM-like and repeater
        /// modes nobody read, are judged both with and without it and refused if
        /// either is outside the licence: failing toward refusing until a
        /// reading of each retires the second judgement.</para>
        /// <para><b>XIT and the offset add</b>, each with its sign (reading b:
        /// 29.640 FM with +0.05 and XIT +1,000 Hz transmitted on 29.691, and with
        /// XIT -1,000 on 29.689).</para>
        /// </remarks>
        public TransmitSliceReading? TransmitSliceSignal()
        {
            Slice? s = DesignatedTransmitSlice();
            if (s == null) return null;
            ulong hz = ValueOf(s);
            if (hz == 0UL) return null;
            string mode = s.DemodMode ?? "";
            var (judged, also) = TransmittedHz(hz, XitOffsetHz(s.XITOn, s.XITFreq), TxOffsetHz(s.TXOffsetFreq), mode);
            // The radio's transmit filter rides along for the 60 m rule's
            // emission judgement (#1161); the lockout never reads it.
            return new TransmitSliceReading(judged, mode, s.FilterLow, s.FilterHigh)
            {
                AlsoJudgeHz = also,
                TxFilterLow = TXFilterLow,
                TxFilterHigh = TXFilterHigh,
            };
        }

        /// <summary>How the radio treats a slice's <c>TXOffsetFreq</c> in a mode
        /// (#1207).</summary>
        internal enum TxOffsetRule
        {
            /// <summary>Measured: the radio transmits at the offset (FM).</summary>
            Applied,
            /// <summary>Measured in USB, presumed for every other mode outside the
            /// FM family: the radio transmits on the dial, the offset kept but idle.</summary>
            Ignored,
            /// <summary>Not measured (the FM-like modes other than FM): judge both
            /// frequencies and refuse if either is outside the licence.</summary>
            NotMeasured,
        }

        /// <summary>The rule for a mode, by FlexLib's mode name, case ignored.
        /// Pure. A slice with no mode at all is not measured either, so it is
        /// judged both ways.</summary>
        internal static TxOffsetRule TxOffsetRuleFor(string? mode)
        {
            if (string.IsNullOrEmpty(mode)) return TxOffsetRule.NotMeasured;
            switch (mode.ToUpperInvariant())
            {
                case "FM": return TxOffsetRule.Applied;
                case "NFM":
                case "DFM":
                case "DSTR":
                    return TxOffsetRule.NotMeasured;
                default: return TxOffsetRule.Ignored;
            }
        }

        /// <summary>
        /// The frequency the lockout judges for a slice at
        /// <paramref name="sliceHz"/>, and a second one it must also judge when
        /// which of two the radio uses is not established (null otherwise).
        /// Pure.
        /// </summary>
        /// <remarks>
        /// A sum at or below zero is judged at 1 Hz, which no band contains, so
        /// it is refused; never a null reading, which would hand the judgement
        /// to the selected slice. Still needed with the offset only in FM: an
        /// FM slice with a large negative offset reaches it.
        /// </remarks>
        internal static (ulong Hz, ulong? AlsoHz) TransmittedHz(ulong sliceHz, long xitHz, long txOffsetHz, string? mode)
        {
            long withoutOffset = (long)sliceHz + xitHz;
            long withOffset = withoutOffset + txOffsetHz;
            switch (TxOffsetRuleFor(mode))
            {
                case TxOffsetRule.Applied:
                    return (AtLeastOneHz(withOffset), null);
                case TxOffsetRule.NotMeasured:
                    return txOffsetHz == 0L ? (AtLeastOneHz(withoutOffset), null) : (AtLeastOneHz(withOffset), AtLeastOneHz(withoutOffset));
                default:
                    return (AtLeastOneHz(withoutOffset), null);
            }
        }

        private static ulong AtLeastOneHz(long hz) => hz <= 0L ? 1UL : (ulong)hz;

        /// <summary>The offset XIT puts on the transmitted frequency: the slice's
        /// XIT frequency when XIT is on, otherwise none (#1194). Pure.</summary>
        internal static long XitOffsetHz(bool xitOn, int xitFreqHz) => xitOn ? xitFreqHz : 0L;

        /// <summary>The offset the slice's <c>TXOffsetFreq</c> (signed MHz) puts
        /// on the transmitted frequency where the radio applies it, in hertz,
        /// rounded to the nearest hertz (#1207). Pure. A value that is not a
        /// number reads as no offset; one past a terahertz either way is held
        /// there, so the sum cannot wrap (above, outside every band; below,
        /// judged at 1 Hz).</summary>
        internal static long TxOffsetHz(double txOffsetMhz)
        {
            if (double.IsNaN(txOffsetMhz) || double.IsInfinity(txOffsetMhz)) return 0L;
            double hz = Math.Round(txOffsetMhz * 1_000_000d, MidpointRounding.AwayFromZero);
            const double Limit = 1_000_000_000_000d;
            return (long)Math.Clamp(hz, -Limit, Limit);
        }
    }
}
