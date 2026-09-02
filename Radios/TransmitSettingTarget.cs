using System;

namespace Radios
{
    /// <summary>
    /// Which slice a transmit-chain setting goes to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The transmit slice, never the selected one</b> — the antenna, the
    /// transmit offset and anything else that decides where and how the RF
    /// leaves belong to the slice the radio transmits from. The slice the
    /// operator is navigating is a different object on a multi-slice radio,
    /// and setting a transmit property there succeeds, echoes, and changes
    /// nothing on the air (#496: two hours, four wrong hypotheses, and a
    /// transmit antenna the operator was told had moved and had not).
    /// </para>
    /// <para>
    /// When no slice is transmitting there is nothing to prefer, so the
    /// selected slice takes the setting — it will carry it into transmit if
    /// it is designated later, which is what the old behaviour did and what an
    /// operator setting up a slice before designating it expects. The
    /// <see cref="Basis"/> says which happened, so the trace can say it too.
    /// </para>
    /// <para>
    /// Generic so the rule is testable without a FlexLib radio: the slice type
    /// is FlexLib's and cannot be constructed outside a connection.
    /// </para>
    /// </remarks>
    public static class TransmitSettingTarget
    {
        /// <summary>Why a given slice was chosen.</summary>
        public enum Basis
        {
            /// <summary>No slice at all. The setting has nowhere to go.</summary>
            None,
            /// <summary>The transmit slice — the RF leaves from it.</summary>
            TransmitSlice,
            /// <summary>No slice is transmitting, so the selected slice takes
            /// the setting and will use it if it becomes the transmit slice.</summary>
            SelectedSliceBecauseNothingTransmits
        }

        /// <summary>
        /// The slice a transmit-chain setting goes to. Never the selected slice
        /// while a transmit slice exists.
        /// </summary>
        public static T Resolve<T>(T transmitSlice, T selectedSlice, out Basis basis) where T : class
        {
            if (transmitSlice != null)
            {
                basis = Basis.TransmitSlice;
                return transmitSlice;
            }
            if (selectedSlice != null)
            {
                basis = Basis.SelectedSliceBecauseNothingTransmits;
                return selectedSlice;
            }
            basis = Basis.None;
            return null;
        }

        /// <summary>
        /// The basis in words for a trace. Names the selected slice when it is
        /// a different one from the transmit slice, because that difference is
        /// the entire content of #496 and a reader must be able to see it.
        /// </summary>
        public static string Describe(Basis basis, int transmitSliceIndex, int selectedSliceIndex)
        {
            switch (basis)
            {
                case Basis.TransmitSlice:
                    if (selectedSliceIndex < 0)
                        return "the transmit slice; no slice is selected";
                    if (selectedSliceIndex == transmitSliceIndex)
                        return "the transmit slice, which is also the selected slice";
                    return "the transmit slice; the selected slice is " + selectedSliceIndex
                           + " and it is NOT transmitting, so it is untouched";
                case Basis.SelectedSliceBecauseNothingTransmits:
                    return "the selected slice, since no slice is transmitting; it takes effect if this slice becomes the transmit slice";
                default:
                    return "no slice";
            }
        }
    }
}
