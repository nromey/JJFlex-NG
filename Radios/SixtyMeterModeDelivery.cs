using Radios.Speech;

namespace Radios
{
    /// <summary>
    /// Asks for a 60 m mode correction and says only what is known when it is
    /// said (#312, repair rounds one and two). Shared by the three ways onto
    /// 60 m that correct the mode: the band jump, the channel rotation and
    /// typed entry.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> All three paths used to assign
    /// <c>Mode</c> and then say "mode set to CW for digital segment" on the
    /// next line. But <c>FlexBase.Mode</c> returns silently while
    /// transmitting, <c>RXMode</c> does nothing when there is no slice of ours
    /// to write, and a write that does go out is only queued. So an ordinary
    /// press could announce a mode that was never set.</para>
    /// <para><b>What it says, ruled by Noel 2026-10-20 (round two).</b> A
    /// refusal from <see cref="FlexBase.RequestMode"/> is certain, so it is
    /// said: the segment needs that mode, and the mode was not changed. A
    /// request that went out is said as exactly that, "switching", and
    /// nothing more. <b>No confirmation is claimed.</b> Round one polled the
    /// slice's DemodMode and called agreement "mode set", but FlexLib's
    /// setter writes that field before the command is sent, so the poll read
    /// our own echo, not the radio (Codex, round-one re-verify). Nothing on
    /// this path receives an independent reply from the radio, so nothing
    /// here may say the radio agreed.</para>
    /// <para><b>The refusal clauses speak about the MODE only.</b> The arrival
    /// before them names a frequency, and while transmitting the frequency
    /// setter refuses too — that older false claim is a separate follow-up.
    /// Round one's "nothing was changed" contradicted the arrival in the same
    /// breath ("Frequency set to 5.360 megahertz, ..., and nothing was
    /// changed"); a clause about the mode alone cannot.</para>
    /// </remarks>
    public static class SixtyMeterModeDelivery
    {
        /// <summary>
        /// The sentence for <paramref name="outcome"/>: the arrival, then the
        /// clause that is true of the mode request. Pure.
        /// </summary>
        public static string Compose(string arrival, SixtyMeterChannels.ModeCorrection correction,
            ModeRequestOutcome outcome)
        {
            var args = new (string, object?)[] { ("mode", correction.RequiredMode), ("segment", correction.SegmentLabel) };
            // DRAFT PROSE (#312): awaiting Noel.
            string key = outcome switch
            {
                ModeRequestOutcome.Sent => "settings.band.mode_switching",
                ModeRequestOutcome.RefusedTransmitting => "settings.band.mode_not_set_transmitting",
                _ => "settings.band.mode_not_set",
            };
            return arrival + Lexicon.Get(key, args);
        }

        /// <summary>
        /// Request <paramref name="correction"/>'s mode and speak the sentence
        /// that is true of the request. Returns the sentence.
        /// </summary>
        public static string Deliver(FlexBase rig, string arrival, SixtyMeterChannels.ModeCorrection correction,
            SpeechIntent intent)
        {
            var outcome = rig.RequestMode(correction.RequiredMode);
            string sentence = Compose(arrival, correction, outcome);
            ScreenReaderOutput.Speak(sentence, intent, VerbosityLevel.Terse, subject: SpeechSubject.SixtyMeterStop);
            return sentence;
        }
    }
}
