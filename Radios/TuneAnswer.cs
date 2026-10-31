using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// What the radio itself says about transmitting, at one moment. Nothing
    /// here is our request: <c>Radio.TXTune</c> is left out on purpose, because
    /// FlexLib's setter writes it before the command goes out, so reading it
    /// back only reads ourselves (#164).
    /// </summary>
    /// <param name="Keyed">The radio's interlock reports it is transmitting.</param>
    /// <param name="TxAllowed">
    /// <c>Radio.TXAllowed</c>, or null until the radio has reported it. FlexLib
    /// holds false until a status line says otherwise, so a bare false could be
    /// a radio that has said nothing yet.
    /// </param>
    /// <param name="NotReady">The interlock reports it cannot transmit.</param>
    /// <param name="Reason">The interlock's reason, <c>None</c> when it gave none.</param>
    public readonly record struct TuneReading(bool Keyed, bool? TxAllowed, bool NotReady, InterlockReason Reason)
    {
        /// <summary>No radio to ask.</summary>
        public static TuneReading Unknown => new(false, null, false, InterlockReason.None);

        /// <summary>
        /// The radio has positively said it will not transmit. An unread
        /// <see cref="TxAllowed"/> never counts: unknown is not a refusal.
        /// </summary>
        public bool RadioRefuses => !Keyed && (TxAllowed == false || NotReady);
    }

    /// <summary>
    /// What to say about a tune carrier, from what the radio did rather than
    /// from what we asked for (#447). The carrier command goes out regardless:
    /// this decides words and never decides whether to key, because nobody has
    /// yet read what <c>TXAllowed</c> reports on an out-of-band slice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Noel at 29.800 MHz, past the top of 10 metres: <i>"tuning should not say
    /// tune on and not key."</i> The radio was right to refuse. The sentence
    /// was the defect: the operator waits for RF that is never coming, and on a
    /// real antenna may blame the tuner or the feedline.
    /// </para>
    /// <para>
    /// <b>A refusal waits the whole window.</b> The interlock may already read
    /// not ready at the press, before the radio has answered it, so "not
    /// keyed yet" proves nothing until the time the transmit checks allow a
    /// key-up has passed.
    /// </para>
    /// </remarks>
    public static class TuneAnswer
    {
        /// <summary>How long the radio gets to report the key: the same
        /// allowance the transmit checks give it.</summary>
        public const int WaitMs = ChainChecks.TxTuneProbeRunner.KeyUpTimeoutMs;

        /// <summary>True once there is something to say.</summary>
        public static bool Settled(TuneReading now, long elapsedMs) => now.Keyed || elapsedMs >= WaitMs;

        /// <summary>The sentence for a settled reading.</summary>
        public static string Sentence(TuneReading now)
        {
            if (now.Keyed) return Lexicon.Get("audio.tune.tune_on");
            if (!now.RadioRefuses) return Lexicon.Get("audio.tune.unconfirmed");

            switch (now.Reason)
            {
                case InterlockReason.OUT_OF_BAND: return Lexicon.Get("audio.tune.refused_out_of_band");
                case InterlockReason.BAD_MODE: return Lexicon.Get("audio.tune.refused_bad_mode");
                case InterlockReason.PA_RANGE: return Lexicon.Get("audio.tune.refused_pa_range");
                case InterlockReason.CLIENT_TX_INHIBIT: return Lexicon.Get("audio.tune.refused_inhibited");
                case InterlockReason.XVTR_RX_ONLY: return Lexicon.Get("audio.tune.refused_rx_only");
                case InterlockReason.NO_TX_ASSIGNED: return Lexicon.Get("audio.tune.refused_no_tx_slice");
                case InterlockReason.TGXL: return Lexicon.Get("audio.tune.refused_amplifier");
                default: return Lexicon.Get("audio.tune.refused");
            }
        }
    }
}
