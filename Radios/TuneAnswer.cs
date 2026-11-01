using System;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// What the radio itself says about transmitting, at one moment. Nothing
    /// here is our request: <c>Radio.TXTune</c> is left out on purpose, because
    /// FlexLib's setter writes it before the command goes out, so reading it
    /// back only reads ourselves (#164).
    /// </summary>
    /// <param name="Transmitting">The radio's interlock reports TRANSMITTING. On
    /// its own this says RF is up, not whose: another client, a MOX or a
    /// hardware line can be what is transmitting.</param>
    /// <param name="Busy">The interlock is anywhere in a transmission, on the
    /// way up or down as well as at TRANSMITTING.</param>
    /// <param name="TxAllowed">
    /// <c>Radio.TXAllowed</c>, or null until the radio has reported it on this
    /// connection. FlexLib holds false until a status line says otherwise, so a
    /// bare false could be a radio that has said nothing yet.
    /// </param>
    /// <param name="NotReady">The interlock reports it cannot transmit.</param>
    /// <param name="Reason">The interlock's reason, <c>None</c> when it gave none.</param>
    /// <param name="Source">The radio's PTT source, as FlexLib parsed it.</param>
    /// <param name="AnotherClient">The radio names a transmitting client that
    /// is not this one.</param>
    /// <param name="RawSource">The radio's own <c>source=</c> word, for the
    /// trace, or null when none has been read: FlexLib's parse hides a list.</param>
    /// <param name="TxClient">The client handle the radio says is transmitting,
    /// for the trace. Zero when it names none.</param>
    /// <param name="OwnClient">Ours, for the trace.</param>
    public readonly record struct TuneReading(bool Transmitting, bool Busy, bool? TxAllowed, bool NotReady,
                                              InterlockReason Reason, PTTSource Source, bool AnotherClient,
                                              string? RawSource = null, uint TxClient = 0, uint OwnClient = 0)
    {
        /// <summary>No radio to ask.</summary>
        public static TuneReading Unknown => new(false, false, null, false, InterlockReason.None, PTTSource.None, false);

        /// <summary>
        /// The radio has positively said it will not transmit. An unread
        /// <see cref="TxAllowed"/> never counts: unknown is not a refusal.
        /// </summary>
        public bool RadioRefuses => !Transmitting && (TxAllowed == false || NotReady);
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

        /// <summary>
        /// Queued, not interrupting. An interrupter is never in the speech
        /// ledger, so a meter reading or the next key would cut the reason off
        /// for good, and the reason is the point of the sentence. Queued, a cut
        /// sentence is held and said again (#447). So the host must retire it,
        /// by its subject, the moment it stops being true: tune off, a new
        /// press, a tune that keys, a lost radio, shutdown.
        /// </summary>
        public const Speech.SpeechIntent NotKeyedIntent = Speech.SpeechIntent.Queue;

        /// <summary>
        /// Is what the radio reports THIS tune? <paramref name="now"/> being on
        /// the air is not enough: a hardware key, a MOX or another client can
        /// be what is transmitting, and that is RF we did not make.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The radio naming TUNE as the source, on no other client's account,
        /// is the plain case.
        /// </para>
        /// <para>
        /// <b>The second case is here because no trace yet shows what a real
        /// tune reports as its source.</b> A radio that was idle at the press,
        /// and is now transmitting on a software source or none, on no other
        /// client's account, has done what we asked: nothing else on this
        /// station was asked to key. If the bench reads TUNE, this case can go.
        /// A transmission already up at the press never qualifies, so a tune
        /// pressed over one gets the unconfirmed sentence.
        /// </para>
        /// </remarks>
        public static bool ThisTuneKeyed(TuneReading now, TuneReading atPress)
        {
            if (!now.Transmitting || now.AnotherClient) return false;
            if (now.Source == PTTSource.TUNE) return true;
            return !atPress.Busy && now.Source is PTTSource.SW or PTTSource.None;
        }

        /// <summary>The sentence once the radio has not keyed in time, or has
        /// keyed and we cannot say it was this tune.</summary>
        public static string NotKeyedSentence(TuneReading now)
        {
            // Never deny what the radio reported: RF is up here, whoever's it is.
            if (now.Transmitting) return Lexicon.Get("audio.tune.transmitting_unconfirmed");
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

    /// <summary>
    /// Whether a tune is up, from what the radio reported (#860). What FlexLib
    /// holds in <c>Radio.TXTune</c> is our own request, written before the
    /// command goes out, so on its own it proves nothing: a denied tune can
    /// leave it true while nothing is on the air.
    /// </summary>
    public static class TuneFact
    {
        /// <summary>
        /// The carrier is on the air, and it is this tune's: the radio reports
        /// transmitting, on the terms of <see cref="TuneAnswer.ThisTuneKeyed"/>.
        /// </summary>
        /// <param name="requestFlag"><c>Radio.TXTune</c>.</param>
        /// <param name="now">The radio's report now.</param>
        /// <param name="atRequest">Its report when the tune was asked for.</param>
        public static bool CarrierUp(bool requestFlag, TuneReading now, TuneReading atRequest) =>
            requestFlag && TuneAnswer.ThisTuneKeyed(now, atRequest);

        /// <summary>
        /// A tune has been asked for and the radio has had no time to answer. It
        /// counts for a door, because a request queued a moment ago reaches the
        /// radio first. It stops counting at <see cref="TuneAnswer.WaitMs"/>,
        /// the time the radio gets to report the key, so a denied tune cannot go
        /// on refusing an ordinary send.
        /// </summary>
        public static bool Pending(bool requested, long msSinceRequest, TuneReading now) =>
            requested && !now.AnotherClient && msSinceRequest < TuneAnswer.WaitMs;
    }

    /// <summary>What a <see cref="TuneAnswerWatch"/> found this time round.</summary>
    public enum TuneWatchStep
    {
        /// <summary>Nothing to say yet.</summary>
        Wait,

        /// <summary>The radio or the session has gone: say nothing, ever.</summary>
        Abandon,

        /// <summary>This tune keyed.</summary>
        Keyed,

        /// <summary>The window has passed without this tune keying.</summary>
        NotKeyed,
    }

    /// <summary>
    /// One tune's wait for the radio's answer, with no timer and no window of
    /// its own, so the host's tick is all that is left to test on a radio.
    /// Pinned to the radio it was asked of: an answer from a session that has
    /// ended is transmit narration about nothing (#447).
    /// </summary>
    public sealed class TuneAnswerWatch
    {
        private readonly TuneReading _atPress;
        private readonly Func<TuneReading> _read;
        private readonly Func<bool> _stillOwed;
        private readonly Func<long> _elapsedMs;

        /// <param name="atPress">The radio just after the carrier was asked for.</param>
        /// <param name="read">The radio now.</param>
        /// <param name="stillOwed">False once the session the tune belonged to
        /// has ended, however it ended.</param>
        /// <param name="elapsedMs">Time since the press.</param>
        public TuneAnswerWatch(TuneReading atPress, Func<TuneReading> read, Func<bool> stillOwed, Func<long> elapsedMs)
        {
            _atPress = atPress;
            _read = read;
            _stillOwed = stillOwed;
            _elapsedMs = elapsedMs;
        }

        /// <summary>The reading the last step decided on.</summary>
        public TuneReading Last { get; private set; }

        /// <summary>One look at the radio.</summary>
        public TuneWatchStep Step()
        {
            if (!_stillOwed()) return TuneWatchStep.Abandon;
            Last = _read();
            if (TuneAnswer.ThisTuneKeyed(Last, _atPress)) return TuneWatchStep.Keyed;
            return _elapsedMs() >= TuneAnswer.WaitMs ? TuneWatchStep.NotKeyed : TuneWatchStep.Wait;
        }
    }
}
