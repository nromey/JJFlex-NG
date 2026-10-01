#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  What the arbiter tells an alarm about an attempt that did NOT end
    //  in the reader saying the sentence (Astra's Track IJK review,
    //  blocker 2).
    //
    //  Production's pump accepts a tracked ticket before the backend has
    //  answered, so the answer about an alarm's hand-over arrives LATER, by
    //  ticket: Completed, Cancelled, Unknown or Refused. The accounting used
    //  to run only when that answer ended the current turn, and then only
    //  for a cancellation — so a refusal, an unknown, an attempt pre-empted
    //  by a cut, a retry that found the turn held, and a second
    //  cancellation after the one retry were all dropped on the floor with
    //  the alarm never told. Every case here is a production-shaped outcome
    //  fed back through OnOutcome, the way the pump feeds it.
    // ────────────────────────────────────────────────────────────────
    public class SpeechArbiterAlarmOutcomeTests
    {
        private sealed record SinkCall(string Message, long Ticket, bool Interrupt);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();
        private long _nextTicket;
        private readonly SpeechArbiter _arbiter;

        private const string Cut = "Transmit stopped. You are no longer on the air.";
        private const string Alarm = "PA temperature 61 degrees C. Release transmit now.";
        private static string Subject(string id = "pa") => SpeechSubject.OperatorAlarm(id);
        private static int Words(string s) => NvdaCompletionChannel.SplitWords(s).Length;

        public SpeechArbiterAlarmOutcomeTests()
        {
            _arbiter = new SpeechArbiter(
                _clock,
                () => VerbosityLevel.Chatty,
                (message, interrupt, intent, level, origin, salvaged) =>
                {
                    long t = ++_nextTicket;
                    _calls.Add(new SinkCall(message, t, interrupt));
                    return SpeechHandoff.TrackedAs(t);
                },
                () => { },
                (message, level, intent, origin) => { });
        }

        private IEnumerable<string> Messages => _calls.Select(c => c.Message);
        private long TicketOf(string m) => _calls.Last(c => c.Message == m).Ticket;

        private int SpeakAlarmTold(Func<string?>? refresh = null)
        {
            int told = 0;
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), refresh ?? (() => Alarm), () => told++);
            return told;   // always 0 here; the closure is what the caller wants
        }

        [Fact]
        public void AnAlarmTheReaderRefused_AfterAcceptingItsTicket_IsReportedNotDelivered()
        {
            // The production shape: EmitCore returns an accepted tracked ticket,
            // and the refusal arrives afterwards as the ticket's outcome.
            int told = 0;
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm, () => told++);
            Assert.Equal(new[] { Alarm }, Messages);

            _clock.Advance(40);
            _arbiter.OnOutcome(TicketOf(Alarm), Alarm, SpeechOutcome.Unknown(SpeechUnknownReason.Refused, "RC 2", 40));

            Assert.Equal(1, told);
            // And the turn is free for whatever comes next — a refusal is not an occupancy.
            _arbiter.Urgent(Cut, VerbosityLevel.Critical, "Ptt", SpeechSubject.ReflectedPowerCut);
            Assert.Equal(Cut, Messages.Last());
        }

        [Fact]
        public void AnAlarmWhoseDeliveryEndedUnknown_IsReportedNotDelivered_BecauseUnknownIsNotHeard()
        {
            int told = 0;
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm, () => told++);
            _clock.Advance(3000);
            _arbiter.OnOutcome(TicketOf(Alarm), Alarm, SpeechOutcome.Unknown(SpeechUnknownReason.Timeout, "the call never returned", 3000));
            Assert.Equal(1, told);
        }

        [Fact]
        public void AnAlarmPreEmptedByACut_IsToldItWasNotDelivered_AlthoughItsAnswerNoLongerOwnsTheTurn()
        {
            // The cut takes the turn; the pump cancels the sounding alarm BY US
            // and that answer arrives for a ticket that is no longer the
            // current turn's. The #611 gate rightly releases nothing for it —
            // and the alarm must still be told.
            int told = 0;
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm, () => told++);
            long alarmTicket = TicketOf(Alarm);
            _clock.Advance(200);
            _arbiter.Urgent(Cut, VerbosityLevel.Critical, "Ptt", SpeechSubject.ReflectedPowerCut);
            _clock.Advance(20);
            _arbiter.OnOutcome(alarmTicket, Alarm, SpeechOutcome.Cancelled(2, Words(Alarm), byUs: true, elapsedMs: 220));

            Assert.Equal(1, told);
            // The cut still holds the turn: the late answer released nothing.
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm);
            Assert.Equal(1, _arbiter.AlarmPendingCount);
        }

        [Fact]
        public void ARetryThatFindsACutHoldingTheTurn_WaitsBehindIt_AndSpeaksWhenTheCutEnds_WithNobodyTold()
        {
            // The first hand-over is cancelled at word zero by a cause nobody
            // can name: the one retry is armed. Before the settle elapses a cut
            // takes the turn. The retry used to find the turn held and simply
            // return — neither waiting nor telling — and the warning was gone.
            int told = 0;
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm, () => told++);
            long first = TicketOf(Alarm);
            _clock.Advance(300);
            _arbiter.OnOutcome(first, Alarm, SpeechOutcome.Cancelled(0, Words(Alarm), byUs: false, elapsedMs: 300));

            _clock.Advance(200);
            _arbiter.Urgent(Cut, VerbosityLevel.Critical, "Ptt", SpeechSubject.ReflectedPowerCut);
            Assert.Equal(0, _arbiter.AlarmPendingCount);

            _clock.Advance(SpeechArbiter.AlarmRetrySettleMs);   // the retry fires into a held turn
            Assert.Equal(1, _arbiter.AlarmPendingCount);          // and WAITS
            Assert.Equal(new[] { Alarm, Cut }, Messages);        // nothing spoke over the cut
            Assert.Equal(0, told);

            // The cut's own completion lets the waiting warning through.
            int words = Words(Cut);
            _clock.Advance(1500);
            _arbiter.OnOutcome(TicketOf(Cut), Cut, SpeechOutcome.Completed(words, words, 1500));
            _clock.Advance(60);
            Assert.Equal(new[] { Alarm, Cut, Alarm }, Messages);
            Assert.Equal(0, _arbiter.AlarmPendingCount);
            Assert.Equal(0, told);   // delivered in the end: nobody is told
        }

        [Fact]
        public void ASecondForeignCancellation_AfterTheOneRetry_TellsTheAlarm_RatherThanDroppingIt()
        {
            int told = 0;
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm, () => told++);
            long first = TicketOf(Alarm);
            _clock.Advance(300);
            _arbiter.OnOutcome(first, Alarm, SpeechOutcome.Cancelled(0, Words(Alarm), byUs: false, elapsedMs: 300));
            _clock.Advance(SpeechArbiter.AlarmRetrySettleMs);
            Assert.Equal(2, Messages.Count(m => m == Alarm));   // the one retry was made
            Assert.Equal(0, told);

            long retry = TicketOf(Alarm);
            Assert.NotEqual(first, retry);
            _clock.Advance(250);
            _arbiter.OnOutcome(retry, Alarm, SpeechOutcome.Cancelled(0, Words(Alarm), byUs: false, elapsedMs: 250));

            Assert.Equal(1, told);
            Assert.Equal(2, Messages.Count(m => m == Alarm));   // and no third attempt from here
        }

        [Fact]
        public void AnAlarmCutByAnOrdinaryInterrupt_IsTold_SoItsNextReadingSaysItAgain()
        {
            int told = 0;
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm, () => told++);
            long ticket = TicketOf(Alarm);
            _clock.Advance(400);
            _arbiter.Emit("Slice A", interrupt: true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            _arbiter.OnOutcome(ticket, Alarm, SpeechOutcome.Cancelled(3, Words(Alarm), byUs: true, elapsedMs: 400));
            Assert.Equal(1, told);
        }

        [Fact]
        public void PositiveControl_ACompletedAlarm_TellsNobody()
        {
            int told = 0;
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm, () => told++);
            int words = Words(Alarm);
            _clock.Advance(2000);
            _arbiter.OnOutcome(TicketOf(Alarm), Alarm, SpeechOutcome.Completed(words, words, 2000));
            Assert.Equal(0, told);
        }

        [Fact]
        public void TheSilenceContract_ASilencedAttemptsLateCancellation_TellsNobody()
        {
            // The operator asked for quiet. The cancellation that follows is
            // theirs, and the alarm is not told: telling it would make its
            // next reading overrule the shut-up key (#617).
            int told = 0;
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm, () => told++);
            long ticket = TicketOf(Alarm);
            _arbiter.OnSilenced();
            _clock.Advance(50);
            _arbiter.OnOutcome(ticket, Alarm, SpeechOutcome.Cancelled(1, Words(Alarm), byUs: false, elapsedMs: 50));
            Assert.Equal(0, told);

            // And a retry that was settling when the silence came is not made.
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject("b"), () => Alarm, () => told++);
            long b = TicketOf(Alarm);
            _arbiter.OnOutcome(b, Alarm, SpeechOutcome.Cancelled(0, Words(Alarm), byUs: false, elapsedMs: 10));
            _arbiter.OnSilenced();
            _clock.Advance(SpeechArbiter.AlarmRetrySettleMs + 10);
            Assert.Equal(2, Messages.Count(m => m == Alarm));   // no retry hand-over
            Assert.Equal(0, told);
        }

        [Fact]
        public void AnOlderAttemptsLateAnswer_IsNotReported_WhenANewerHandOverOnTheSameAlarmCarriesIt()
        {
            // The alarm said it again on its own (a worsening) before the first
            // attempt's answer came back. The newer hand-over carries the
            // obligation; the older answer must not tell the alarm about a
            // warning that has since been said.
            int told = 0;
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm, () => told++);
            long first = TicketOf(Alarm);
            int words = Words(Alarm);
            _clock.Advance(2000);
            _arbiter.OnOutcome(first, Alarm, SpeechOutcome.Completed(words, words, 2000));
            const string Worse = "PA temperature 63 degrees C. Release transmit now.";
            _arbiter.UrgentAlarm(Worse, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Worse, () => told++);

            // A stray late answer for the FIRST ticket, after the second is out.
            _arbiter.OnOutcome(first, Alarm, SpeechOutcome.Unknown(SpeechUnknownReason.Refused, "late", 10));
            Assert.Equal(0, told);

            // The positive control: the second attempt's own refusal is reported.
            _arbiter.OnOutcome(TicketOf(Worse), Worse, SpeechOutcome.Unknown(SpeechUnknownReason.Refused, "RC 2", 10));
            Assert.Equal(1, told);
        }
    }
}
