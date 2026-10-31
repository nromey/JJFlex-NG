using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>FlexBase, what the radio says about transmitting (#447).</summary>
    public partial class FlexBase
    {
        // FlexLib has no "reported" flag for this. It holds false from
        // construction, so the notification is our only proof the value is the
        // radio's: the same reasoning as _radioReportedAutosave (#499).
        private volatile bool _txAllowedReported;

        /// <summary>
        /// The radio's own word on transmitting right now. Read-only: nothing
        /// here is a command and nothing here gates one.
        /// </summary>
        public TuneReading ReadTuneStanding()
        {
            Radio radio = theRadio;
            if (radio == null) return TuneReading.Unknown;
            InterlockState state = radio.InterlockState;
            return new TuneReading(
                Keyed: state == InterlockState.Transmitting,
                TxAllowed: _txAllowedReported ? radio.TXAllowed : (bool?)null,
                NotReady: state == InterlockState.NotReady,
                Reason: radio.InterlockReason);
        }
    }
}
