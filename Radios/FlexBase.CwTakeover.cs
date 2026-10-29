#nullable enable
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the CW counterpart of push to talk taking over a voice send
    /// (#835 fix round; Noel, 2026-10-28 21:25: <i>"when you're sending CW,
    /// pressing the code key should stop it, or pressing the virtual software
    /// based key we'll design at some point as well."</i>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A CW send STOPS, it does not pause</b>, exactly as a voice send taken
    /// over by push to talk stops: carrying on would mean the app keying the
    /// radio again by itself after the operator let go of the key.
    /// </para>
    /// <para>
    /// <b>The hook exists; nothing calls it yet, on purpose.</b> A CW message
    /// is text handed to the radio's own keyer (<see cref="SendCW(string)"/>,
    /// FlexLib's <c>CWX.Send</c>), and the radio reports no paddle or key
    /// closure that the vendored FlexLib parses: its only paddle-shaped items
    /// are the swap setting and <c>Radio.CWKey</c>, a COMMAND for keying from
    /// software, not a report. So there is no event today on which to call
    /// this, and none is invented. Its two callers-to-be: the on-screen
    /// software key Noel names, which will key through <c>Radio.CWKey</c> and
    /// must call this first; and a paddle detector, if the radio is ever found
    /// to report a closure (a bench question in the 2026-10-28 fix-round report:
    /// whether the radio already aborts a CWX message itself when the paddle is
    /// touched). Silent: the operator is sending, and speech over the sidetone
    /// would be in the way. Whether it should say anything is asked, not ruled.
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>
        /// The operator is keying CW by hand, <paramref name="by"/> naming how
        /// (the paddle, the key, the on-screen key), for the trace: any CW
        /// message this app handed to the radio's keyer stops now, its unsent
        /// text dropped. Harmless when nothing is being sent.
        /// </summary>
        public void CwSendTakenByOperator(string by)
        {
            Tracing.TraceLine("CW: the operator keyed by hand (" + by + "), so any CW message going out stops",
                TraceLevel.Info);
            StopCW();
        }
    }
}
