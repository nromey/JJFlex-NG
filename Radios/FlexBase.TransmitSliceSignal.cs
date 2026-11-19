#nullable enable
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
        /// one slice; never FlexBase's cached copy. An FM repeater offset is
        /// the same class and is not applied here (a separate entry: which
        /// field the radio applies is not established).</para>
        /// </remarks>
        public TransmitSliceReading? TransmitSliceSignal()
        {
            Slice? s = DesignatedTransmitSlice();
            if (s == null) return null;
            ulong hz = ValueOf(s);
            if (hz == 0UL) return null;
            long transmitted = (long)hz + XitOffsetHz(s.XITOn, s.XITFreq);
            if (transmitted <= 0L) return null;
            return new TransmitSliceReading((ulong)transmitted, s.DemodMode ?? "", s.FilterLow, s.FilterHigh);
        }

        /// <summary>The offset XIT puts on the transmitted frequency: the slice's
        /// XIT frequency when XIT is on, otherwise none (#1194). Pure.</summary>
        internal static long XitOffsetHz(bool xitOn, int xitFreqHz) => xitOn ? xitFreqHz : 0L;
    }
}
