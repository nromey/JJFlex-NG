using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>FlexBase, what the radio says about transmitting (#447).</summary>
    public partial class FlexBase
    {
        // FlexLib has no "reported" flag for this. It holds false from
        // construction, so the notification is our only proof the value is the
        // radio's: the same reasoning as _radioReportedAutosave (#499), and like
        // that one it belongs to a connection. Cleared at Connect, at
        // RetryConnect and when the connection falls.
        private volatile bool _txAllowedReported;

        /// <summary>
        /// This client's tune carrier is up, by the radio's own report (#860).
        /// A carrier another client holds is not ours to speak for. Read-only,
        /// and safe with no radio.
        /// </summary>
        public bool TuneCarrierUp
        {
            get
            {
                Radio radio = theRadio;
                return radio != null && radio.TXTune && TransmitIsOurs(radio);
            }
        }

        /// <summary>
        /// A tune carrier or an ATU cycle is up here: the air carries a plain
        /// carrier, so nothing can modulate it (#860).
        /// </summary>
        public bool TuningNow
        {
            get
            {
                Radio radio = theRadio;
                return radio != null && TransmitIsOurs(radio)
                    && (radio.TXTune || radio.ATUTuneStatus == ATUTuneStatus.InProgress);
            }
        }

        private bool TransmitIsOurs(Radio radio)
        {
            uint txClient = radio.TXClientHandle;
            return txClient == 0 || txClient == clientHandle;
        }

        /// <summary>
        /// The radio's own word on transmitting right now. Read-only: nothing
        /// here is a command and nothing here gates one.
        /// </summary>
        public TuneReading ReadTuneStanding()
        {
            Radio radio = theRadio;
            if (radio == null) return TuneReading.Unknown;
            InterlockState state = radio.InterlockState;
            uint txClient = radio.TXClientHandle;
            return new TuneReading(
                Transmitting: state == InterlockState.Transmitting,
                Busy: RadioInterlockReportsTransmit,
                TxAllowed: _txAllowedReported ? radio.TXAllowed : (bool?)null,
                NotReady: state == InterlockState.NotReady,
                Reason: radio.InterlockReason,
                Source: radio.PTTSource,
                AnotherClient: txClient != 0 && txClient != clientHandle,
                RawSource: RawPttSource,
                TxClient: txClient,
                OwnClient: clientHandle);
        }
    }
}
