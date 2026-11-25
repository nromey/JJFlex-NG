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
        /// What the antenna-tuner status handler says for a bypass status
        /// (<c>Bypass</c> or <c>ManualBypass</c>): "ATU bypassed", or nothing
        /// (null). Pure. Every other status returns null; this rule is for the
        /// bypass branch only.
        /// </summary>
        /// <param name="status">The event's status name.</param>
        /// <param name="isAutoAtu">The tuner type was automatic when the radio
        /// reported it, and the radio has an ATU.</param>
        /// <param name="ourCycleRunning">An ATU cycle the operator started (a
        /// tune press) was running when the status arrived, so the bypass is
        /// that cycle's result.</param>
        /// <remarks>
        /// <para><b>Ruled by Noel 2026-11-23 17:10 (#1221):</b> a frequency the
        /// ATU has no memory for, where the radio bypasses the ATU on its own
        /// (<c>TUNE_MANUAL_BYPASS</c>, <c>using_mem=0</c>), says nothing: the
        /// ATU stays on and the operator changed nothing. The 8600 reading of
        /// 2026-11-23 found a memory HIT whose stored solution is bypass
        /// (<c>TUNE_BYPASS</c>, <c>using_mem=1</c>) said "ATU bypassed" too; it
        /// is silent as well, for now: its confirmation is #1223's ruled
        /// "Tuned from memory", which waits for the earcon work. Neither comes
        /// with a cycle of ours, which is how they are told apart.</para>
        /// <para><b>Still said:</b> a tune press whose ATU cycle ends in a bypass
        /// (#1188, ruled 2026-11-19, J8: a tune that ends in bypass is a result,
        /// said as one).</para>
        /// <para><b>Never said here, and not silenced by this:</b> the operator's
        /// own bypass (ATU off, or ATU Mode to bypassed). The setter changes
        /// the type to manual before it sends the bypass, so its status arrives
        /// out of automatic; and the route that asked says it itself ("ATU
        /// mode bypassed.", "ATU off").</para>
        /// </remarks>
        public static string AtuBypassStatusSentence(string status, bool isAutoAtu, bool ourCycleRunning)
        {
            if (status != nameof(ATUTuneStatus.Bypass) && status != nameof(ATUTuneStatus.ManualBypass)) return null;
            if (!isAutoAtu || !ourCycleRunning) return null;
            return Lexicon.Get("audio.tune.atu_bypassed");
        }

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
