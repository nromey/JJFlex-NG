using System;

namespace Radios
{
    public partial class FlexBase
    {
        private Func<bool>? _transmitKeySource;

        /// <summary>
        /// Whether the radio is keyed, for the transmit audio chain (Sprint 49
        /// Track H, items B and E): our Mox OR the radio's own interlock saying
        /// it is transmitting. Not <see cref="Transmit"/> alone, because VOX
        /// (and a hardware key) keys only the interlock, so a key state built
        /// on our Mox would miss every VOX over: changes would land mid-over
        /// and the gate would never reset at a VOX key-down. FlexLib's own
        /// <c>IsInterlockMox</c> counts the same three interlock states as
        /// <see cref="RadioInterlockReportsTransmit"/> (PTTRequested,
        /// Transmitting, UnkeyRequested; verified read-only in
        /// <c>FlexLib_API/FlexLib/Radio.cs</c>, 2026-11-18). Anything that
        /// throws reads as keyed, which only ever defers a change.
        /// </summary>
        public bool TransmitKeyedOrInterlock
        {
            get
            {
                try { return Transmit || RadioInterlockReportsTransmit; }
                catch { return true; }
            }
        }

        /// <summary>One delegate for <see cref="TransmitKeyedOrInterlock"/>, made once, for the audio thread to ask every buffer.</summary>
        public Func<bool> TransmitKeySource => _transmitKeySource ??= () => TransmitKeyedOrInterlock;
    }
}
