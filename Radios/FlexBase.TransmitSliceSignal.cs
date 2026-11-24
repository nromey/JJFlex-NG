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
        /// <para><b>The slice's transmit offset is carried too</b> (#1207):
        /// <c>TXOffsetFreq</c>, signed MHz. Measured on an 8600 (2026-11-23),
        /// this is the only field that moves the transmit frequency: 0.05 moved
        /// 14.250 to 14.300 MHz whatever the repeater direction said, and -0.05
        /// moved it to 14.200. <c>FMRepeaterOffsetFreq</c> and
        /// <c>RepeaterOffsetDirection</c> moved nothing, at rest or keyed, as
        /// FlexLib's own comment says (stored in the radio, acted on by a client
        /// setting <c>TXOffsetFreq</c>), so neither is read here. This app never
        /// sets the offset today; another client or a memory recall can, and
        /// that is the slice this covers.</para>
        /// <para>Applied in every mode: FlexLib carries the field without a mode
        /// and says only "an offset applied to the transmitter frequency"; the
        /// reading was in FM, and whether the radio honours it in other modes
        /// was not measured. With XIT on as well, the two are added: neither
        /// FlexLib nor the reading says how the radio combines them.</para>
        /// </remarks>
        public TransmitSliceReading? TransmitSliceSignal()
        {
            Slice? s = DesignatedTransmitSlice();
            if (s == null) return null;
            ulong hz = ValueOf(s);
            if (hz == 0UL) return null;
            long transmitted = (long)hz + XitOffsetHz(s.XITOn, s.XITFreq) + TxOffsetHz(s.TXOffsetFreq);
            // An offset that carries the transmitter to or below zero is judged
            // at 1 Hz, which no band contains, so it is refused; never null,
            // which would judge the selected slice instead.
            if (transmitted <= 0L) transmitted = 1L;
            return new TransmitSliceReading((ulong)transmitted, s.DemodMode ?? "", s.FilterLow, s.FilterHigh);
        }

        /// <summary>The offset XIT puts on the transmitted frequency: the slice's
        /// XIT frequency when XIT is on, otherwise none (#1194). Pure.</summary>
        internal static long XitOffsetHz(bool xitOn, int xitFreqHz) => xitOn ? xitFreqHz : 0L;

        /// <summary>The offset the slice's <c>TXOffsetFreq</c> (signed MHz) puts
        /// on the transmitted frequency, in hertz, rounded to the nearest hertz
        /// (#1207). Pure. A value that is not a number reads as no offset; one
        /// past a terahertz either way is held there, so the sum cannot wrap
        /// (above, outside every band; below, judged at 1 Hz).</summary>
        internal static long TxOffsetHz(double txOffsetMhz)
        {
            if (double.IsNaN(txOffsetMhz) || double.IsInfinity(txOffsetMhz)) return 0L;
            double hz = Math.Round(txOffsetMhz * 1_000_000d, MidpointRounding.AwayFromZero);
            const double Limit = 1_000_000_000_000d;
            return (long)Math.Clamp(hz, -Limit, Limit);
        }
    }
}
