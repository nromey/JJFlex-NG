#define CWMonitor
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the transmit control settings the TX Controls dialog reads and writes.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        #region Transmit control settings (TX Controls dialog)

        // Sprint 33 Track J, #109. TXControlsDialog was built in Sprint 9 Track B
        // against fourteen FlexLib properties, but only RemoteOnEnabled above
        // ever got a wrapper, and theRadio is internal — so the app side had no
        // way to reach the other thirteen and the dialog was never wired up.
        // Same shape as RemoteOnEnabled: read through to the radio, return a
        // benign default when nothing is connected, trace every write.
        //
        // These are radio-persistent settings, not per-session state. The RCA
        // and ACC jacks they control only do anything if something is physically
        // wired to them, which the dialog cannot know and does not claim.
        //
        // #477 (2026-11-07): each is written through its Set method, which
        // returns false when there is no radio or the change-nothing hold
        // refused it, so the dialog can put the control back and play no
        // success tone. The write is one step with the hold
        // (OperatorWriteUnlessHeld); the properties are read-only, so there is
        // no second, unguarded route. Not an ownership check: a guest
        // legitimately sets these on a borrowed radio (Noel, 2026-08-29).

        /// <summary>One transmit control written as one step with the hold.</summary>
        private bool WriteTxControl(string name, object value, System.Action<Flex.Smoothlake.FlexLib.Radio> write)
        {
            var radio = theRadio;
            if (radio == null)
            {
                Tracing.TraceLine(name + " set: no radio connected", TraceLevel.Warning);
                return false;
            }
            if (!OperatorWriteUnlessHeld("settings.guard.action.tx_controls", () => write(radio)))
                return false;
            Tracing.TraceLine($"{name} set: {value}", TraceLevel.Info);
            return true;
        }

        /// <summary>TX request input on the RCA jack. False when no radio.</summary>
        public bool TXReqRCAEnabled => theRadio?.TXReqRCAEnabled ?? false;

        /// <summary>Write <see cref="TXReqRCAEnabled"/>; false when there is no radio or the hold refused it.</summary>
        public bool SetTXReqRCAEnabled(bool value) => WriteTxControl(nameof(TXReqRCAEnabled), value, r => r.TXReqRCAEnabled = value);

        /// <summary>TX request RCA polarity. True = active high, false = active low.</summary>
        public bool TXReqRCAPolarity => theRadio?.TXReqRCAPolarity ?? false;

        /// <summary>Write <see cref="TXReqRCAPolarity"/>; false when there is no radio or the hold refused it.</summary>
        public bool SetTXReqRCAPolarity(bool value) => WriteTxControl(nameof(TXReqRCAPolarity), value, r => r.TXReqRCAPolarity = value);

        /// <summary>TX request input on the accessory connector. False when no radio.</summary>
        public bool TXReqACCEnabled => theRadio?.TXReqACCEnabled ?? false;

        /// <summary>Write <see cref="TXReqACCEnabled"/>; false when there is no radio or the hold refused it.</summary>
        public bool SetTXReqACCEnabled(bool value) => WriteTxControl(nameof(TXReqACCEnabled), value, r => r.TXReqACCEnabled = value);

        /// <summary>TX request ACC polarity. True = active high, false = active low.</summary>
        public bool TXReqACCPolarity => theRadio?.TXReqACCPolarity ?? false;

        /// <summary>Write <see cref="TXReqACCPolarity"/>; false when there is no radio or the hold refused it.</summary>
        public bool SetTXReqACCPolarity(bool value) => WriteTxControl(nameof(TXReqACCPolarity), value, r => r.TXReqACCPolarity = value);

        /// <summary>TX1 RCA output enable. False when no radio.</summary>
        public bool TX1Enabled => theRadio?.TX1Enabled ?? false;

        /// <summary>Write <see cref="TX1Enabled"/>; false when there is no radio or the hold refused it.</summary>
        public bool SetTX1Enabled(bool value) => WriteTxControl(nameof(TX1Enabled), value, r => r.TX1Enabled = value);

        /// <summary>TX1 RCA output delay in milliseconds. Zero when no radio.</summary>
        public int TX1Delay => theRadio?.TX1Delay ?? 0;

        /// <summary>Write <see cref="TX1Delay"/>; false when there is no radio or the hold refused it.</summary>
        public bool SetTX1Delay(int value) => WriteTxControl(nameof(TX1Delay), value, r => r.TX1Delay = value);

        /// <summary>TX2 RCA output enable. False when no radio.</summary>
        public bool TX2Enabled => theRadio?.TX2Enabled ?? false;

        /// <summary>Write <see cref="TX2Enabled"/>; false when there is no radio or the hold refused it.</summary>
        public bool SetTX2Enabled(bool value) => WriteTxControl(nameof(TX2Enabled), value, r => r.TX2Enabled = value);

        /// <summary>TX2 RCA output delay in milliseconds. Zero when no radio.</summary>
        public int TX2Delay => theRadio?.TX2Delay ?? 0;

        /// <summary>Write <see cref="TX2Delay"/>; false when there is no radio or the hold refused it.</summary>
        public bool SetTX2Delay(int value) => WriteTxControl(nameof(TX2Delay), value, r => r.TX2Delay = value);

        /// <summary>TX3 RCA output enable. False when no radio.</summary>
        public bool TX3Enabled => theRadio?.TX3Enabled ?? false;

        /// <summary>Write <see cref="TX3Enabled"/>; false when there is no radio or the hold refused it.</summary>
        public bool SetTX3Enabled(bool value) => WriteTxControl(nameof(TX3Enabled), value, r => r.TX3Enabled = value);

        /// <summary>TX3 RCA output delay in milliseconds. Zero when no radio.</summary>
        public int TX3Delay => theRadio?.TX3Delay ?? 0;

        /// <summary>Write <see cref="TX3Delay"/>; false when there is no radio or the hold refused it.</summary>
        public bool SetTX3Delay(int value) => WriteTxControl(nameof(TX3Delay), value, r => r.TX3Delay = value);

        /// <summary>TX accessory-connector output enable. False when no radio.</summary>
        public bool TXACCEnabled => theRadio?.TXACCEnabled ?? false;

        /// <summary>Write <see cref="TXACCEnabled"/>; false when there is no radio or the hold refused it.</summary>
        public bool SetTXACCEnabled(bool value) => WriteTxControl(nameof(TXACCEnabled), value, r => r.TXACCEnabled = value);

        /// <summary>TX accessory-connector output delay in milliseconds. Zero when no radio.</summary>
        public int TXACCDelay => theRadio?.TXACCDelay ?? 0;

        /// <summary>Write <see cref="TXACCDelay"/>; false when there is no radio or the hold refused it.</summary>
        public bool SetTXACCDelay(int value) => WriteTxControl(nameof(TXACCDelay), value, r => r.TXACCDelay = value);

        /// <summary>Hardware ALC (automatic level control) enable. False when no radio.</summary>
        public bool HWAlcEnabled => theRadio?.HWAlcEnabled ?? false;

        /// <summary>Write <see cref="HWAlcEnabled"/>; false when there is no radio or the hold refused it.</summary>
        public bool SetHWAlcEnabled(bool value) => WriteTxControl(nameof(HWAlcEnabled), value, r => r.HWAlcEnabled = value);

        #endregion
    }
}
