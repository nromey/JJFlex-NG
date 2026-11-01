using System;
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

        // What this client last asked of the tune carrier, for TuneFact. Noted
        // beside the request, which goes on exactly as before: nothing here
        // gates a carrier or changes when one is written.
        private readonly object _tuneFactGate = new();
        private bool _tuneRequested;
        private long _tuneRequestedAtMs;
        private TuneReading _tuneAtRequest = TuneReading.Unknown;

        /// <summary>The clock a request is timed by. For tests.</summary>
        internal Func<long> TuneFactClock { get; set; } = () => Environment.TickCount64;

        /// <summary>A tune was asked for, or asked off. On the caller's thread, so a door on that thread sees it at once, ahead of the command queue.</summary>
        private void NoteTuneRequest(bool on)
        {
            TuneReading standing = on ? ReadTuneStanding() : default;
            lock (_tuneFactGate)
            {
                _tuneRequested = on;
                if (!on) return;
                _tuneRequestedAtMs = TuneFactClock();
                _tuneAtRequest = standing;
            }
        }

        /// <summary>
        /// The radio or FlexLib has dropped the tune flag, however that came
        /// about (#860). For whoever has something to retire the moment tune
        /// ends. Raised on FlexLib's read loop, so a subscriber must not wait.
        /// </summary>
        public event Action TuneCarrierFell;

        private void OnTuneFlagFell()
        {
            NoteTuneRequest(false);
            TuneCarrierFell?.Invoke();
        }

        /// <summary>
        /// This client's tune carrier is on the air, by the radio's own report
        /// (#860): <see cref="TuneFact.CarrierUp"/>. Read-only, and safe with no
        /// radio.
        /// </summary>
        public bool TuneCarrierUp
        {
            get
            {
                Radio radio = theRadio;
                if (radio == null) return false;
                TuneReading atRequest;
                lock (_tuneFactGate) atRequest = _tuneAtRequest;
                return TuneFact.CarrierUp(radio.TXTune, ReadTuneStanding(), atRequest);
            }
        }

        /// <summary>
        /// A tune of ours is up or on its way, or an ATU cycle of ours is
        /// running: the air carries a plain carrier, so nothing can modulate
        /// it (#860). A tune the radio denied stops counting once it has had
        /// time to answer.
        /// </summary>
        public bool TuningNow
        {
            get
            {
                Radio radio = theRadio;
                if (radio == null) return false;
                TuneReading now = ReadTuneStanding();
                if (now.AnotherClient) return false;
                if (radio.ATUTuneStatus == ATUTuneStatus.InProgress) return true;
                bool requested;
                long at;
                TuneReading atRequest;
                lock (_tuneFactGate)
                {
                    requested = _tuneRequested;
                    at = _tuneRequestedAtMs;
                    atRequest = _tuneAtRequest;
                }
                return TuneFact.CarrierUp(radio.TXTune, now, atRequest)
                    || TuneFact.Pending(requested, TuneFactClock() - at, now);
            }
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
