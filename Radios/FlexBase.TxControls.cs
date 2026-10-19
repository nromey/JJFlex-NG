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

        /// <summary>TX request input on the RCA jack. False when no radio.</summary>
        public bool TXReqRCAEnabled
        {
            get => theRadio?.TXReqRCAEnabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TXReqRCAEnabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TXReqRCAEnabled = value;
                Tracing.TraceLine($"TXReqRCAEnabled set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX request RCA polarity. True = active high, false = active low.</summary>
        public bool TXReqRCAPolarity
        {
            get => theRadio?.TXReqRCAPolarity ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TXReqRCAPolarity set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TXReqRCAPolarity = value;
                Tracing.TraceLine($"TXReqRCAPolarity set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX request input on the accessory connector. False when no radio.</summary>
        public bool TXReqACCEnabled
        {
            get => theRadio?.TXReqACCEnabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TXReqACCEnabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TXReqACCEnabled = value;
                Tracing.TraceLine($"TXReqACCEnabled set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX request ACC polarity. True = active high, false = active low.</summary>
        public bool TXReqACCPolarity
        {
            get => theRadio?.TXReqACCPolarity ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TXReqACCPolarity set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TXReqACCPolarity = value;
                Tracing.TraceLine($"TXReqACCPolarity set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX1 RCA output enable. False when no radio.</summary>
        public bool TX1Enabled
        {
            get => theRadio?.TX1Enabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TX1Enabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TX1Enabled = value;
                Tracing.TraceLine($"TX1Enabled set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX1 RCA output delay in milliseconds. Zero when no radio.</summary>
        public int TX1Delay
        {
            get => theRadio?.TX1Delay ?? 0;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TX1Delay set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TX1Delay = value;
                Tracing.TraceLine($"TX1Delay set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX2 RCA output enable. False when no radio.</summary>
        public bool TX2Enabled
        {
            get => theRadio?.TX2Enabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TX2Enabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TX2Enabled = value;
                Tracing.TraceLine($"TX2Enabled set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX2 RCA output delay in milliseconds. Zero when no radio.</summary>
        public int TX2Delay
        {
            get => theRadio?.TX2Delay ?? 0;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TX2Delay set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TX2Delay = value;
                Tracing.TraceLine($"TX2Delay set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX3 RCA output enable. False when no radio.</summary>
        public bool TX3Enabled
        {
            get => theRadio?.TX3Enabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TX3Enabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TX3Enabled = value;
                Tracing.TraceLine($"TX3Enabled set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX3 RCA output delay in milliseconds. Zero when no radio.</summary>
        public int TX3Delay
        {
            get => theRadio?.TX3Delay ?? 0;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TX3Delay set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TX3Delay = value;
                Tracing.TraceLine($"TX3Delay set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX accessory-connector output enable. False when no radio.</summary>
        public bool TXACCEnabled
        {
            get => theRadio?.TXACCEnabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TXACCEnabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TXACCEnabled = value;
                Tracing.TraceLine($"TXACCEnabled set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>TX accessory-connector output delay in milliseconds. Zero when no radio.</summary>
        public int TXACCDelay
        {
            get => theRadio?.TXACCDelay ?? 0;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("TXACCDelay set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.TXACCDelay = value;
                Tracing.TraceLine($"TXACCDelay set: {value}", TraceLevel.Info);
            }
        }

        /// <summary>Hardware ALC (automatic level control) enable. False when no radio.</summary>
        public bool HWAlcEnabled
        {
            get => theRadio?.HWAlcEnabled ?? false;
            set
            {
                if (theRadio == null)
                {
                    Tracing.TraceLine("HWAlcEnabled set: no radio connected", TraceLevel.Warning);
                    return;
                }
                theRadio.HWAlcEnabled = value;
                Tracing.TraceLine($"HWAlcEnabled set: {value}", TraceLevel.Info);
            }
        }

        #endregion
    }
}
