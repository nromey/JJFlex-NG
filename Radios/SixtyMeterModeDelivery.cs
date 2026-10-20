using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Applies a 60 m mode correction and says only what is true when it is
    /// said (#312, repair round one). Shared by the three ways onto 60 m that
    /// correct the mode: the band jump, the channel rotation and typed entry.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> All three paths used to assign
    /// <c>Mode</c> and then say "mode set to CW for digital segment" on the
    /// next line. But <c>FlexBase.Mode</c> returns silently while
    /// transmitting, <c>RXMode</c> does nothing when there is no slice of ours
    /// to write, and even a write that goes out is only queued. So an ordinary
    /// press could announce a mode that was never set, on the path where the
    /// operator's next act is plausibly to transmit.</para>
    /// <para><b>The mechanism chosen, and why.</b> Ask with
    /// <see cref="FlexBase.RequestMode"/>, which says whether the request left.
    /// If it did not, say at once that the segment needs that mode and that
    /// nothing, or the mode, was not changed: that is known for certain and
    /// waiting would only delay it. If it did, wait for
    /// <see cref="FlexBase.RXModeAsReported"/> to agree, bounded, and only
    /// then say "mode set"; if the window closes first, say the radio has not
    /// confirmed it. Speaking the clause only on agreement is the Fixer's
    /// #164 discipline for a transmit-side mode, applied to receive. The wait
    /// is normally one pass of the command loop, a few milliseconds.</para>
    /// <para><b>A newer arrival retires an older one.</b> Every entry point
    /// calls <see cref="CancelPending"/> (or <see cref="DeliverAsync"/>, which
    /// does), so a press still waiting for agreement does not speak late over
    /// the place the operator has since moved to.</para>
    /// </remarks>
    public static class SixtyMeterModeDelivery
    {
        /// <summary>How long agreement is waited for. The Fixer's figure.</summary>
        public const int ConfirmWindowMs = 1500;

        private const int PollMs = 50;

        private static int _generation;

        /// <summary>
        /// Retire any delivery still waiting for agreement. Called by every
        /// 60 m entry point, whether or not it has a correction of its own.
        /// </summary>
        public static void CancelPending() => Interlocked.Increment(ref _generation);

        /// <summary>
        /// Request <paramref name="correction"/>'s mode, then speak
        /// <paramref name="arrival"/> with the clause that is true. Returns
        /// the sentence spoken, or null when a newer arrival retired this one.
        /// </summary>
        /// <param name="delay">Test seam for the poll's wait; production waits for real.</param>
        public static async Task<string?> DeliverAsync(
            FlexBase rig, string arrival, SixtyMeterChannels.ModeCorrection correction,
            Speech.SpeechIntent intent,
            Func<int, Task>? delay = null, int confirmWindowMs = ConfirmWindowMs)
        {
            // Called fire-and-forget from key handlers, so nothing may escape.
            // A failure is traced and said as unconfirmed, never as "set".
            try
            {
                return await DeliverCoreAsync(rig, arrival, correction, intent, delay, confirmWindowMs);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("SixtyMeterModeDelivery: " + ex.Message, TraceLevel.Warning);
                try
                {
                    string fallback = arrival + Lexicon.Get("settings.band.mode_unconfirmed",
                        ("mode", correction.RequiredMode), ("segment", correction.SegmentLabel));
                    ScreenReaderOutput.Speak(fallback, intent, VerbosityLevel.Terse,
                        subject: Speech.SpeechSubject.SixtyMeterStop);
                    return fallback;
                }
                catch { return null; }
            }
        }

        private static async Task<string?> DeliverCoreAsync(
            FlexBase rig, string arrival, SixtyMeterChannels.ModeCorrection correction,
            Speech.SpeechIntent intent, Func<int, Task>? delay, int confirmWindowMs)
        {
            int mine = Interlocked.Increment(ref _generation);
            delay ??= ms => Task.Delay(ms);

            var outcome = rig.RequestMode(correction.RequiredMode);
            string sentence;
            if (outcome != ModeRequestOutcome.Sent)
            {
                sentence = arrival + NotChangedClause(outcome, correction);
            }
            else
            {
                bool agreed = false;
                for (int waited = 0; ; waited += PollMs)
                {
                    if (string.Equals(rig.RXModeAsReported, correction.RequiredMode, StringComparison.OrdinalIgnoreCase))
                    {
                        agreed = true;
                        break;
                    }
                    if (waited >= confirmWindowMs) break;
                    await delay(PollMs);
                    if (Volatile.Read(ref _generation) != mine)
                    {
                        Tracing.TraceLine("SixtyMeterModeDelivery: a newer 60 m arrival retired the wait for "
                            + correction.RequiredMode + " — saying nothing for this one", TraceLevel.Info);
                        return null;
                    }
                }

                if (agreed)
                {
                    sentence = arrival + correction.Advisory;
                }
                else
                {
                    Tracing.TraceLine("SixtyMeterModeDelivery: the slice did not report " + correction.RequiredMode
                        + " within " + confirmWindowMs + " ms; it reports \"" + rig.RXModeAsReported + "\"",
                        TraceLevel.Warning);
                    // DRAFT PROSE (#312): awaiting Noel.
                    sentence = arrival + Lexicon.Get("settings.band.mode_unconfirmed",
                        ("mode", correction.RequiredMode), ("segment", correction.SegmentLabel));
                }
            }

            if (Volatile.Read(ref _generation) != mine) return null;
            ScreenReaderOutput.Speak(sentence, intent, VerbosityLevel.Terse,
                subject: Speech.SpeechSubject.SixtyMeterStop);
            return sentence;
        }

        private static string NotChangedClause(ModeRequestOutcome outcome, SixtyMeterChannels.ModeCorrection c)
        {
            // DRAFT PROSE (#312): awaiting Noel. While transmitting the
            // frequency setter refuses too, so the honest claim is that
            // NOTHING changed, not just the mode.
            return outcome == ModeRequestOutcome.RefusedTransmitting
                ? Lexicon.Get("settings.band.mode_not_set_transmitting", ("mode", c.RequiredMode), ("segment", c.SegmentLabel))
                : Lexicon.Get("settings.band.mode_not_set", ("mode", c.RequiredMode), ("segment", c.SegmentLabel));
        }
    }
}
