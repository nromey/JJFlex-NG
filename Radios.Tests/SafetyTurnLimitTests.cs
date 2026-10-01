#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  The maximum safety turn, T, as an ESCAPE HANDSHAKE (#611, Sol's
    //  Track K review, section 1).
    //
    //  Reaching T used to clear the turn and nothing else, so a waiting
    //  alarm saw a free turn and interrupted the sink on top of a tracked
    //  cut the pump said was still sounding. There was no test of T at all:
    //  the existing case outwaits a short ESTIMATE, not the maximum, and so
    //  could not have seen this. These drive the exact limit on an injected
    //  clock, first at the token and then through the arbiter.
    // ────────────────────────────────────────────────────────────────
    public class SafetyTurnLimitTests
    {
        private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        private static DateTime At(int ms) => T0.AddMilliseconds(ms);
        private const int T = SafetyDeliveryCoordinator.MaxAutomaticTurnMs;
        private const int Grace = SafetyDeliveryCoordinator.MaxTurnCancelGraceMs;

        // ── The token alone ──

        [Fact]
        public void ReachingTheMaximumTurn_RequestsCancellationOnce_AndKeepsTheTurnOccupied()
        {
            int cancels = 0;
            var c = new SafetyDeliveryCoordinator(() => cancels++);
            var turn = c.ReserveForSafety(SpeechSubject.ReflectedPowerCut, T0);
            c.Bind(turn, SpeechHandoff.TrackedAs(7), estimateMs: 3000, T0);

            Assert.True(c.IsBusy(At(T - 1)));
            Assert.Equal(0, cancels);

            // Exactly at T: asked to stop, and STILL occupied.
            Assert.True(c.IsBusy(At(T)));
            Assert.Equal(1, cancels);
            Assert.True(turn.CancelRequested);
            Assert.Same(turn, c.Current);

            // Asking again inside the grace does not ask the backend again,
            // and an alarm may not take the turn from an attempt that is
            // being stopped but has not yet said it stopped.
            Assert.True(c.IsBusy(At(T + Grace - 1)));
            Assert.Equal(1, cancels);
            Assert.Null(c.TryReserveForAlarm("alarm", At(T + Grace - 1)));
        }

        [Fact]
        public void TheAttemptsOwnAnswerToTheCancellation_EndsTheTurn_AsTheLimitAndNotAsHeard()
        {
            var c = new SafetyDeliveryCoordinator(() => { });
            var turn = c.ReserveForSafety(SpeechSubject.ReflectedPowerCut, T0);
            c.Bind(turn, SpeechHandoff.TrackedAs(7), estimateMs: 3000, T0);
            Assert.True(c.IsBusy(At(T)));

            // The pump reports the cut stopped: the handshake completes.
            var ended = c.TakeOutcome(7, completed: false, At(T + 40));
            Assert.Same(turn, ended);
            Assert.Null(c.Current);
            Assert.NotNull(c.TryReserveForAlarm("alarm", At(T + 41)));
        }

        [Fact]
        public void NoAnswerWithinTheGrace_IsolatesTheAttempt_AndOnlyThenTransfersTheTurn()
        {
            var c = new SafetyDeliveryCoordinator(() => { });
            var turn = c.ReserveForSafety(SpeechSubject.ReflectedPowerCut, T0);
            c.Bind(turn, SpeechHandoff.TrackedAs(7), estimateMs: 3000, T0);
            Assert.True(c.IsBusy(At(T)));

            Assert.True(c.IsBusy(At(T + Grace - 1)));
            Assert.False(c.IsBusy(At(T + Grace)));
            Assert.Null(c.Current);

            // The grace is the pump's own cancel-then-escape path plus room
            // for the report to travel; pin the derivation so a change to
            // either pump constant moves this with it.
            Assert.Equal(PacedSpeechDelivery.CancelGraceMs + PacedSpeechDelivery.EscapeGraceMs + 500, Grace);
        }

        [Fact]
        public void ALateAnswerAfterIsolation_ReleasesNothing()
        {
            var c = new SafetyDeliveryCoordinator(() => { });
            var turn = c.ReserveForSafety(SpeechSubject.ReflectedPowerCut, T0);
            c.Bind(turn, SpeechHandoff.TrackedAs(7), estimateMs: 3000, T0);
            // The handshake starts when the limit is first OBSERVED, as the
            // alarm timer observes it at T; then the grace runs from there.
            Assert.True(c.IsBusy(At(T)));
            Assert.False(c.IsBusy(At(T + Grace)));

            // An alarm has the turn now; the isolated cut's answer finally
            // arrives and must not touch it.
            var alarm = c.TryReserveForAlarm("alarm", At(T + Grace + 1));
            Assert.NotNull(alarm);
            c.Bind(alarm!, SpeechHandoff.TrackedAs(8), 1000, At(T + Grace + 1));
            Assert.Null(c.TakeOutcome(7, completed: false, At(T + Grace + 50)));
            Assert.Same(alarm, c.Current);
        }

        [Fact]
        public void AnUntrackedAttempt_HasNobodyToAnswer_SoTheLimitCutsTheBackendAndTransfersAtOnce()
        {
            int cancels = 0;
            var c = new SafetyDeliveryCoordinator(() => cancels++);
            var turn = c.ReserveForSafety(SpeechSubject.ReflectedPowerCut, T0);
            // An untracked estimate clamps to the maximum, so the estimate
            // and the hard deadline coincide and the limit is what ends it.
            c.Bind(turn, SpeechHandoff.Untracked, estimateMs: T + 5000, T0);
            Assert.True(c.IsBusy(At(T - 1)));
            Assert.False(c.IsBusy(At(T)));
            Assert.Equal(1, cancels);
        }

        [Fact]
        public void PositiveControl_AnUntrackedEstimateShorterThanT_EndsOnTheEstimateWithoutAnyCancellation()
        {
            int cancels = 0;
            var c = new SafetyDeliveryCoordinator(() => cancels++);
            var turn = c.ReserveForSafety(SpeechSubject.ReflectedPowerCut, T0);
            c.Bind(turn, SpeechHandoff.Untracked, estimateMs: 2000, T0);
            Assert.True(c.IsBusy(At(1999)));
            Assert.False(c.IsBusy(At(2000)));
            Assert.Equal(0, cancels);
        }

        // ── Through the arbiter, on the fake clock ──

        private sealed record SinkCall(string Message, bool Interrupt, SpeechIntent? Intent, long Ticket, double AtMs);

        private sealed class Harness
        {
            public readonly FakeSpeechClock Clock = new();
            public readonly List<SinkCall> Calls = new();
            public int Silences;
            private long _nextTicket;
            public readonly SpeechArbiter Arbiter;

            public Harness()
            {
                Arbiter = new SpeechArbiter(
                    Clock,
                    () => VerbosityLevel.Chatty,
                    (message, interrupt, intent, level, origin, salvaged) =>
                    {
                        long ticket = ++_nextTicket;
                        Calls.Add(new SinkCall(message, interrupt, intent, ticket, Clock.ElapsedMs));
                        return SpeechHandoff.TrackedAs(ticket);
                    },
                    () => Silences++,
                    (message, level, intent, origin) => { });
            }

            public IEnumerable<string> Messages => Calls.Select(c => c.Message);
            public long TicketOf(string message) => Calls.Last(c => c.Message == message).Ticket;
        }

        private const string Cut = "Transmit stopped. Eighty percent of your power is coming back on ANT2. You are no longer on the air.";
        private const string Alarm = "PA temperature 61 degrees C. Release transmit now.";

        [Fact]
        public void AWaitingAlarm_DoesNotStartOnTopOfATrackedCutAtT_ButAfterTheCutAnswersTheCancellation()
        {
            var h = new Harness();
            h.Arbiter.Urgent(Cut, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerCut);
            long cutTicket = h.TicketOf(Cut);
            int silencesForTheCut = h.Silences;   // Urgent silences once itself

            h.Arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", SpeechSubject.OperatorAlarm("pa"), () => Alarm);
            Assert.Equal(1, h.Arbiter.AlarmPendingCount);

            // The alarm's timer fires at T. Under the old code this is where
            // the alarm interrupted the still-sounding cut.
            h.Clock.Advance(T + 60);
            Assert.Equal(new[] { Cut }, h.Messages);
            Assert.Equal(silencesForTheCut + 1, h.Silences);   // the cancellation request, and nothing else
            Assert.Equal(1, h.Arbiter.AlarmPendingCount);

            // The pump answers for the cut: cancelled, by us. Now, and only
            // now, the alarm gets its turn.
            int words = NvdaCompletionChannel.SplitWords(Cut).Length;
            h.Arbiter.OnOutcome(cutTicket, Cut, SpeechOutcome.Cancelled(marksReached: 9, markCount: words, byUs: true, elapsedMs: T + 60));
            h.Clock.Advance(100);
            Assert.Equal(new[] { Cut, Alarm }, h.Messages);
            Assert.Equal(0, h.Arbiter.AlarmPendingCount);

            // And the cut is still owed, delivery unknown, because a clock
            // stopped it and nobody heard the end.
            Assert.Contains(h.Arbiter.OwedSafetyObligations, o => o.Message == Cut);
        }

        [Fact]
        public void WhenTheCutNeverAnswers_TheAlarmWaitsOutTheWholeGrace_ThenGoes()
        {
            var h = new Harness();
            h.Arbiter.Urgent(Cut, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerCut);
            h.Arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", SpeechSubject.OperatorAlarm("pa"), () => Alarm);

            h.Clock.Advance(T + Grace - 200);
            Assert.Equal(new[] { Cut }, h.Messages);

            h.Clock.Advance(400);
            Assert.Equal(new[] { Cut, Alarm }, h.Messages);
            Assert.Contains(h.Arbiter.OwedSafetyObligations, o => o.Message == Cut);
        }

        [Fact]
        public void PositiveControl_TheCutsOwnCompletionBeforeT_ReleasesTheAlarmAtOnce()
        {
            var h = new Harness();
            h.Arbiter.Urgent(Cut, VerbosityLevel.Critical, "PttSafetyController", SpeechSubject.ReflectedPowerCut);
            h.Arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", SpeechSubject.OperatorAlarm("pa"), () => Alarm);
            int words = NvdaCompletionChannel.SplitWords(Cut).Length;

            h.Clock.Advance(4000);
            h.Arbiter.OnOutcome(h.TicketOf(Cut), Cut, SpeechOutcome.Completed(words, words, 4000));
            h.Clock.Advance(100);
            Assert.Equal(new[] { Cut, Alarm }, h.Messages);
            Assert.DoesNotContain(h.Arbiter.OwedSafetyObligations, o => o.Message == Cut);
        }
    }
}
