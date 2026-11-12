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
    }
}
