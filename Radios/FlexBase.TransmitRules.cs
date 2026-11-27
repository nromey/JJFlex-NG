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
    /// <para><b>Where it is asked</b> (#1307): the operator's doors (push to talk,
    /// the lock, the voice sends, recorder playback, the VOX doors) and, beside
    /// every <see cref="RefusedForNoCallsign"/> chokepoint except tune, the radio's
    /// admission boundary: transmit going on (<c>EnqueueKeyOn</c>), the antenna
    /// tuner going on (lockout only, no 60 m rule), VOX or break-in going on, both
    /// forms of <c>SendCW</c>, the Fix tool's injected and spoken transmit checks,
    /// and <c>TransmitKillSwitch.RaiseCarrier</c> for MOX. So a route with no door
    /// of its own (CW typing, the Fix tool) is refused too. Tune, the Fix tool's
    /// tune check and a tune carrier are never asked (ruled 2026-11-18). An unkey,
    /// tune off, tuner off and VOX off are never refused.</para>
    /// <para><b>A limit:</b> turning VOX or break-in on is judged at that moment
    /// only. Once either is on, the radio keys itself on voice or the key wherever
    /// it is tuned, and no chokepoint of ours sees that.</para>
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

        private long _lastRulesRefusalSpokenMs;

        /// <summary>
        /// The admission boundary's transmit-rules refusal (#1307, ruled by Noel
        /// 2026-11-26, question 7 of the 41-question sheet: the lockout asks on
        /// every route the callsign check asks on, except tune): null when
        /// <paramref name="what"/> may go ahead; otherwise its sentence,
        /// traced and spoken (throttled, so a held key or a burst of queued
        /// sends says it once). Asked only on the way ON, beside
        /// <see cref="RefusedForNoCallsign"/>, and never for tune.
        /// </summary>
        /// <param name="what">What would have keyed, for the trace.</param>
        /// <param name="sixtyMeterRule">False for the antenna tuner, which raises a tune carrier.</param>
        /// <param name="spokenPrefix">Said before the sentence where what is refused is not a
        /// transmission (turning VOX or break-in on).</param>
        internal string? TransmitRulesRefusal(string what, bool sixtyMeterRule = true, string? spokenPrefix = null)
        {
            var verdict = TransmitRulesVerdict(sixtyMeterRule);
            if (verdict.IsAllowed) return null;
            string sentence = verdict.RefusalSentence() ?? "";
            if (spokenPrefix != null) sentence = spokenPrefix + " " + sentence;
            Tracing.TraceLine("Refused by the transmit rules: " + what + " (lockout " + verdict.Lockout
                + ", 60 m " + verdict.SixtyMeter + ")", TraceLevel.Warning);
            long now = Environment.TickCount64;
            if (now - System.Threading.Interlocked.Read(ref _lastRulesRefusalSpokenMs) > 1500)
            {
                System.Threading.Interlocked.Exchange(ref _lastRulesRefusalSpokenMs, now);
                ScreenReaderOutput.Speak(sentence, Speech.SpeechIntent.Interrupt, VerbosityLevel.Critical,
                    subject: Speech.SpeechSubject.TransmitKeyRefused);
            }
            return sentence;
        }

        /// <summary><see cref="TransmitRulesRefusal"/> as yes or no, for a chokepoint.</summary>
        internal bool RefusedByTransmitRules(string what, bool sixtyMeterRule = true, string? spokenPrefix = null)
            => TransmitRulesRefusal(what, sixtyMeterRule, spokenPrefix) != null;
    }
}
