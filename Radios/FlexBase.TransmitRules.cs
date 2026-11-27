#nullable enable
using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The rules JJ Flexible adds on top of the radio's own, as the radio layer
    /// sees them: the TX band lockout (#1310) and the 60 m rules (#1161), judged
    /// by the application and asked for here.
    /// </summary>
    /// <remarks>
    /// <para><b>One judge.</b> The judgement lives in the application
    /// (<c>FreqOutHandlers.JudgeTransmitHere</c>, which holds the operator's
    /// licence), and this layer cannot see it, so the application hands the radio
    /// a way to ask (<see cref="OpenParms.JudgeTransmitHere"/>), exactly as it
    /// hands it the operator's callsign (#335). The doors (push to talk, the
    /// lock, the sends) and the radio's own chokepoints ask the same judge.</para>
    /// <para><b>When nothing was handed in</b> (a radio object built by a test,
    /// or by code that never sets the delegate), nothing is refused; and a judge
    /// that throws admits and is traced. That is how the lockout has always
    /// failed: open, never by refusing a key for a reason the operator cannot
    /// see.</para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>
        /// The verdict of the application's judge on transmitting now, with the
        /// 60 m rule asked or not (<paramref name="sixtyMeterRule"/>: false for
        /// the antenna tuner, which raises a tune carrier). Allowed when no judge
        /// was handed in, or it throws.
        /// </summary>
        public TransmitVerdict TransmitRulesVerdict(bool sixtyMeterRule)
        {
            var judge = Callouts?.JudgeTransmitHere;
            if (judge == null) return TransmitVerdict.Allowed;
            try { return judge(sixtyMeterRule); }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitRules: the transmit rules could not be judged, so the key is admitted: "
                    + ex.Message, TraceLevel.Warning);
                return TransmitVerdict.Allowed;
            }
        }
    }
}
