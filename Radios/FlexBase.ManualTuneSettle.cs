using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    public partial class FlexBase
    {
        /// <summary>
        /// Runs the manual tuner's SWR settle (#1163). The thread pool by
        /// default; a test hands it a list so it can see the work was NOT run
        /// on the caller's thread, then run it itself.
        /// </summary>
        internal Action<Action> ManualSettleRunner { get; set; } = work => Task.Run(work);

        /// <summary>
        /// The manual tuner's OFF (#1163): wait for the SWR to stop falling,
        /// report it, then drop the carrier, as the setter always did, but on
        /// <see cref="ManualSettleRunner"/>, never on the caller's thread.
        /// </summary>
        /// <remarks>
        /// <para>The loop sleeps at least 100 ms, and for as long as the SWR keeps
        /// falling. On the caller's thread that blocked the UI thread for a
        /// button, and FlexLib's read thread when an ATU status ended a latch
        /// set in manual type (the <c>ATUTuneStatus</c> case clears the latch
        /// whatever the type is by then), which holds up every status the radio
        /// sends behind it.</para>
        /// <para>The order is unchanged: the carrier stays up while the SWR
        /// settles, because the reading needs it, and the write that drops it
        /// is queued after the report.</para>
        /// <para><b>The release is never skipped.</b> A tune press that lands
        /// while a settle is still waiting is queued first, so this release then
        /// drops that new carrier too; the latch follows the carrier down
        /// (<see cref="CarrierFallEndsTheLatch"/>). Skipping the release instead
        /// would leave a carrier up whenever the newer press was itself refused
        /// at its write, so an unkey is the failure chosen.</para>
        /// </remarks>
        private void SettleManualTuneThenRelease()
        {
            FlexTunerTypes type = _FlexTunerType;
            ManualSettleRunner(() =>
            {
                float highSWR = _SWR;
                try
                {
                    // Look for minimum SWR.
                    do
                    {
                        highSWR = _SWR;
                        Thread.Sleep(100);
                    } while (highSWR > _SWR);
                    // Report status.
                    RaiseFlexAntTuneStartStop(new FlexAntTunerArg(type, ATUTuneStatus.OK, highSWR));
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("FlexTunerOn: the manual tune's SWR report failed: " + ex.Message, TraceLevel.Error);
                }
                finally
                {
                    // Whatever the report did, the carrier comes down.
                    EnqueueTuneWrite(false, "antenna tuner", () => _FlexTunerOn = false);
                }
            });
        }
    }
}
