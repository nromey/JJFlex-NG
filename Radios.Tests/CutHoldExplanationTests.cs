#nullable enable
using System.Collections.Generic;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  #571, item 3: the explanation survives the keypress.
    //
    //  On 2026-10-21 the cut sentence was cancelled at word 1 of 37 by the
    //  Shift+Space that landed 149 ms after the cut, and then WITHDRAWN,
    //  because that press re-keyed the radio and the sentence's own validity
    //  answer ("you are no longer on the air") became false. The cause still
    //  held; only the keying changed. With the hold, the press no longer
    //  keys: the episode does not advance, the press speaks the refusal on
    //  the cut's own subject, and the answer stays yes until the operator
    //  really does key again — at which point an unheard copy is withdrawn
    //  rather than replayed into the live transmission (Astra's Track IJK
    //  review, blocker 6), exactly as before.
    //
    //  Same harness as SafetyValidityTests, with the REAL TransmitEpisode
    //  standing where the controller's does.
    // ────────────────────────────────────────────────────────────────
    public sealed class CutHoldExplanationTests
    {
        private sealed record SinkCall(string Message, bool Salvaged, long Ticket);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();
        private long _nextTicket;

        private const int Settle = SpeechArbiter.SalvageSettleMs;
        private const string Cut =
            "Transmit stopped. 58 percent of your power was coming back on ANT2, and your reflected power cutoff is turned on, "
            + "so your transmission was ended. You are no longer on the air. Check ANT2 before keying again.";
        private const string Refusal =
            "Not keyed. 58 percent of your power was coming back on ANT2 a moment ago, so keying is held for 5 more seconds. "
            + "Check ANT2, or change your antenna, power, frequency or tuner to key now.";

        private SpeechArbiter NewArbiter() => new SpeechArbiter(
            _clock,
            () => VerbosityLevel.Critical,   // Off: the whole of this must work there
            (message, interrupt, intent, level, origin, salvaged) =>
            {
                long t = ++_nextTicket;
                _calls.Add(new SinkCall(message, salvaged, t));
                return SpeechHandoff.TrackedAs(t);
            },
            () => { },
            (message, level, intent, origin) => { });

        private IEnumerable<string> Salvaged() => _calls.Where(c => c.Salvaged).Select(c => c.Message);
        private IEnumerable<string> Handed() => _calls.Select(c => c.Message);
        private long TicketOf(string m) => _calls.Last(c => c.Message == m).Ticket;

        private void CutPartWayByNobodyWeCanName(SpeechArbiter a, string message, int at, int of, int ms)
            => a.OnOutcome(TicketOf(message), message, SpeechOutcome.Cancelled(at, of, byUs: false, ms));

        /// <summary>A transmission, then the cut ends it: RF on, RF off, the sentence with its validity answer.</summary>
        private static TransmitEpisode TransmitAndCut(SpeechArbiter a)
        {
            var episode = new TransmitEpisode();
            episode.RfIs(true);
            episode.RfIs(false);    // GoIdle: SetTx(false), then the sentence
            a.Urgent(Cut, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerCut,
                stillValid: episode.NoTransmissionSince());
            return episode;
        }

        [Fact]
        public void Mains_defect_as_the_negative_control_the_press_rekeyed_and_the_cut_was_withdrawn()
        {
            var a = NewArbiter();
            var episode = TransmitAndCut(a);
            CutPartWayByNobodyWeCanName(a, Cut, at: 1, of: 37, ms: 149);   // the press's own speech cut it
            Assert.Contains(a.OwedSafetyObligations, o => o.Message == Cut);

            episode.RfIs(true);   // main: ToggleLock from Idle keyed the radio again

            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Critical, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);

            Assert.DoesNotContain(Cut, Salvaged());
            Assert.DoesNotContain(a.OwedSafetyObligations, o => o.Message == Cut);   // withdrawn: the operator never heard why
        }

        [Fact]
        public void With_the_hold_the_press_does_not_key_and_the_explanation_is_repeated_whole_on_the_cuts_subject()
        {
            var a = NewArbiter();
            var episode = TransmitAndCut(a);

            // 149 ms later: the press. The hold refuses it and speaks the
            // refusal, Urgent, same subject, same validity answer — what
            // ReflectedCutHold.RefusePress does. The episode does not move.
            // Urgent silences the backend, and the reader then reports the
            // cut sentence cancelled at word 1 of 37 — BY US this time, where
            // on 2026-10-21 it was the press's own "Transmitting, locked".
            _clock.Advance(149);
            a.Urgent(Refusal, VerbosityLevel.Critical, "ReflectedCutHold", SpeechSubject.ReflectedPowerCut,
                stillValid: episode.NoTransmissionSince());
            a.OnOutcome(TicketOf(Cut), Cut, SpeechOutcome.Cancelled(1, 37, byUs: true, 149));

            Assert.Contains(Refusal, Handed());
            Assert.Contains(a.OwedSafetyObligations, o => o.Message == Refusal);

            // The refusal is a newer episode on the SAME subject, so it
            // retires the interrupted cut sentence rather than queueing behind
            // it: a later ordinary interrupt rescues nothing of the old copy.
            // The operator still hears why, because the refusal says it.
            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Critical, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);
            Assert.DoesNotContain(Cut, Salvaged());
        }

        [Fact]
        public void An_interrupted_refusal_is_replayed_while_the_operator_is_still_off_the_air()
        {
            var a = NewArbiter();
            var episode = TransmitAndCut(a);
            _clock.Advance(149);
            a.Urgent(Refusal, VerbosityLevel.Critical, "ReflectedCutHold", SpeechSubject.ReflectedPowerCut,
                stillValid: episode.NoTransmissionSince());
            CutPartWayByNobodyWeCanName(a, Refusal, at: 2, of: 32, ms: 400);   // NVDA's own window chatter, say

            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Critical, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);

            Assert.Contains(Refusal, Salvaged());   // survives: nothing keyed, the claim still holds
        }

        [Fact]
        public void Once_the_operator_really_keys_again_an_unheard_copy_is_withdrawn_not_replayed()
        {
            // The hold lifted (a changed antenna, or the window ran out) and the
            // operator keyed legitimately. "You are no longer on the air" is now
            // false, so the copy must not be rescued into the new transmission.
            var a = NewArbiter();
            var episode = TransmitAndCut(a);
            _clock.Advance(149);
            a.Urgent(Refusal, VerbosityLevel.Critical, "ReflectedCutHold", SpeechSubject.ReflectedPowerCut,
                stillValid: episode.NoTransmissionSince());
            CutPartWayByNobodyWeCanName(a, Refusal, at: 2, of: 32, ms: 400);

            episode.RfIs(true);   // EnterLocked after the hold ended: a new transmission

            _clock.Advance(1000);
            a.Emit("Slice A", true, SpeechIntent.Interrupt, VerbosityLevel.Critical, "keys", SpeechSubject.WhereYouAre);
            _clock.Advance(Settle);

            Assert.DoesNotContain(Refusal, Salvaged());
            Assert.DoesNotContain(Cut, Salvaged());
            Assert.DoesNotContain(a.OwedSafetyObligations, o => o.Message == Refusal || o.Message == Cut);
        }
    }
}
