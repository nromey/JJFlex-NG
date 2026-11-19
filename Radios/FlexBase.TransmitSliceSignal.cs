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
        /// </remarks>
        public TransmitSliceReading? TransmitSliceSignal()
        {
            Slice? s = DesignatedTransmitSlice();
            if (s == null) return null;
            ulong hz = ValueOf(s);
            if (hz == 0UL) return null;
            return new TransmitSliceReading(hz, s.DemodMode ?? "", s.FilterLow, s.FilterHigh);
        }
    }
}
