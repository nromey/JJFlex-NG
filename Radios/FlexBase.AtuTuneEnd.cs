using Flex.Smoothlake.FlexLib;

namespace Radios
{
    public partial class FlexBase
    {
        /// <summary>
        /// Whether the radio's automatic tuner status means the tune we
        /// started is over, so the tuner latch (<see cref="FlexTunerOn"/>)
        /// must be cleared. The one rule the <c>ATUTuneStatus</c> handler
        /// reads.
        /// </summary>
        /// <remarks>
        /// <para>Every status the radio reports at a tune's end: not started,
        /// aborted, successful, OK, failed, bypassed, failed and bypassed by
        /// the radio itself, and bypassed by hand. Until this rule a tune that
        /// ended in a bypass the radio made itself (TUNE_FAIL_BYPASS), or one
        /// bypassed by hand, left the latch set: the next tune press then
        /// found the latch already on and sent nothing at all, while saying
        /// the tuner was tuning, which sounds exactly like the tuner refusing
        /// to tune again. In automatic mode clearing the latch is also what
        /// queues our unkey, so such a tune never queued one.</para>
        /// <para>Not an end: <c>InProgress</c>, <c>None</c> (no report yet),
        /// and the Tuner Genius statuses, which the handler has never acted
        /// on and this rule does not start acting on.</para>
        /// </remarks>
        internal static bool AtuStatusEndsTheTune(ATUTuneStatus status) => status switch
        {
            ATUTuneStatus.NotStarted => true,
            ATUTuneStatus.Aborted => true,
            ATUTuneStatus.Successful => true,
            ATUTuneStatus.OK => true,
            ATUTuneStatus.Fail => true,
            ATUTuneStatus.FailBypass => true,
            ATUTuneStatus.Bypass => true,
            ATUTuneStatus.ManualBypass => true,
            _ => false,
        };

        /// <summary>
        /// Whether the tune carrier falling ends the tuner latch
        /// (<see cref="FlexTunerOn"/>): only when the latch is set and the
        /// tuner type is not automatic.
        /// </summary>
        /// <remarks>
        /// <para>Out of automatic type, a tune press keys the radio's tune
        /// carrier and sets the latch, and the latch is all a later press
        /// reads. Stopping that carrier by any other route than the latch's own
        /// toggle (the tune carrier key, the emergency stop, the radio dropping
        /// it) used to leave the latch set, so every later tune press found it
        /// set and sent nothing. In that type the latch means "our tune carrier
        /// is up", so it follows the carrier down.</para>
        /// <para>Automatic type is left as it was: there the latch means "an ATU
        /// cycle of ours is running", which the ATU's own status ends (see
        /// <see cref="AtuStatusEndsTheTune"/>).</para>
        /// </remarks>
        internal static bool CarrierFallEndsTheLatch(FlexTunerTypes type, bool latch) =>
            latch && type != FlexTunerTypes.auto;

        /// <summary>
        /// The tune carrier has fallen: out of automatic type, clear the tuner
        /// latch. Clears the field only and sends nothing: the carrier is
        /// already down, and the setter's manual branch would wait on this
        /// receive thread for the standing wave ratio and queue a tune-off.
        /// </summary>
        private void ReleaseTunerLatchOnCarrierFall()
        {
            if (!CarrierFallEndsTheLatch(_FlexTunerType, _FlexTunerOn)) return;
            _FlexTunerOn = false;
            JJTrace.Tracing.TraceLine("FlexTunerOn: the tune carrier fell with the tuner type " + _FlexTunerType
                + "; the latch is cleared, nothing sent", System.Diagnostics.TraceLevel.Info);
        }
    }
}
