#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  What the arbiter does when it CANNOT ask an alarm whether it is
    //  still true, and when it cannot keep a waiting alarm at all (Sol's
    //  Track K review section 2; Astra's Track I review finding 5).
    //
    //  A refresh that throws used to be caught into null, and null means
    //  "nothing left to say", so a failed re-read removed the warning as if
    //  the condition had cleared. And the waiting set, when full, evicted the
    //  alarm that had waited longest and told nobody. Both now end the same
    //  honest way: the alarm is TOLD it was not delivered, and its own next
    //  fresh sample says what is true.
    // ────────────────────────────────────────────────────────────────
    public class SpeechArbiterAlarmRefreshTests
    {
        private sealed record SinkCall(string Message, long Ticket, double AtMs);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();
        private long _nextTicket;
        private readonly SpeechArbiter _arbiter;

        private const string Cut = "Transmit stopped. You are no longer on the air.";
        private const string Alarm = "PA temperature 61 degrees C. Release transmit now.";
        private static string Subject(string id = "pa") => SpeechSubject.OperatorAlarm(id);

        public SpeechArbiterAlarmRefreshTests()
        {
            _arbiter = new SpeechArbiter(
                _clock,
                () => VerbosityLevel.Chatty,
                (message, interrupt, intent, level, origin, salvaged) =>
                {
                    long t = ++_nextTicket;
                    _calls.Add(new SinkCall(message, t, _clock.ElapsedMs));
                    return SpeechHandoff.TrackedAs(t);
                },
                () => { },
                (message, level, intent, origin) => { });
        }

        private IEnumerable<string> Messages => _calls.Select(c => c.Message);
        private long TicketOf(string m) => _calls.Last(c => c.Message == m).Ticket;

        /// <summary>A cut holds the turn, so the alarm waits; then the cut completes and the alarm's turn comes.</summary>
        private void HoldTheTurnWithACutThenRelease(Func<string?> refresh, Action? notDelivered)
        {
            _arbiter.Urgent(Cut, VerbosityLevel.Critical, "Ptt", SpeechSubject.ReflectedPowerCut);
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), refresh, notDelivered);
            Assert.Equal(1, _arbiter.AlarmPendingCount);
            int words = NvdaCompletionChannel.SplitWords(Cut).Length;
            _clock.Advance(1500);
            _arbiter.OnOutcome(TicketOf(Cut), Cut, SpeechOutcome.Completed(words, words, 1500));
        }

        [Fact]
        public void ARefreshThatThrows_KeepsTheAlarmWaiting_AndAsksAgainAfterTheSettle()
        {
            int asked = 0, told = 0;
            Func<string?> refresh = () =>
            {
                asked++;
                if (asked <= 2) throw new InvalidOperationException("the snapshot is unavailable right now");
                return Alarm;
            };
            HoldTheTurnWithACutThenRelease(refresh, () => told++);

            // The alarm timer fires, the refresh throws: still waiting, not
            // spoken, not dropped.
            _clock.Advance(60);
            Assert.Equal(new[] { Cut }, Messages);
            Assert.Equal(1, _arbiter.AlarmPendingCount);
            Assert.Equal(1, asked);

            // Asked again after the retry settle; throws again; still waiting.
            _clock.Advance(SpeechArbiter.AlarmRetrySettleMs);
            Assert.Equal(2, asked);
            Assert.Equal(1, _arbiter.AlarmPendingCount);

            // Third ask answers: spoken, with the answer, and nobody was told
            // it was undelivered because it was delivered.
            _clock.Advance(SpeechArbiter.AlarmRetrySettleMs);
            Assert.Equal(3, asked);
            Assert.Equal(new[] { Cut, Alarm }, Messages);
            Assert.Equal(0, _arbiter.AlarmPendingCount);
            Assert.Equal(0, told);
        }

        [Fact]
        public void ARefreshThatKeepsThrowing_LetsTheAlarmGoWithoutSpeakingAStaleSentence_AndTellsIt()
        {
            int asked = 0, told = 0;
            HoldTheTurnWithACutThenRelease(() => { asked++; throw new InvalidOperationException("still broken"); }, () => told++);

            _clock.Advance(60);
            for (int i = 1; i < SpeechArbiter.AlarmRefreshFailureCap; i++) _clock.Advance(SpeechArbiter.AlarmRetrySettleMs);

            Assert.Equal(SpeechArbiter.AlarmRefreshFailureCap, asked);
            Assert.Equal(new[] { Cut }, Messages);          // the queued sentence is NOT spoken: it may be stale
            Assert.Equal(0, _arbiter.AlarmPendingCount);     // and it is no longer waiting
            Assert.Equal(1, told);                           // but the alarm knows, and will say it on its next reading
        }

        [Fact]
        public void PositiveControl_ARefreshThatReturnsNull_IsTheAlarmSayingItCleared_AndNobodyIsTold()
        {
            int told = 0;
            HoldTheTurnWithACutThenRelease(() => null, () => told++);
            _clock.Advance(60);
            Assert.Equal(new[] { Cut }, Messages);
            Assert.Equal(0, _arbiter.AlarmPendingCount);
            Assert.Equal(0, told);   // cleared is the alarm's own verdict, not a failure
        }

        [Fact]
        public void TheOneRetry_IsNotMadeOnAThrowingRefresh_AndTheAlarmIsTold()
        {
            int asked = 0, told = 0;
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(),
                () => { asked++; throw new InvalidOperationException("broken at retry"); }, () => told++);
            long ticket = TicketOf(Alarm);
            int words = NvdaCompletionChannel.SplitWords(Alarm).Length;

            // Cancelled at word zero by a cause nobody can name: the one retry is armed.
            _clock.Advance(300);
            _arbiter.OnOutcome(ticket, Alarm, SpeechOutcome.Cancelled(0, words, byUs: false, elapsedMs: 300));
            _clock.Advance(SpeechArbiter.AlarmRetrySettleMs + 10);

            Assert.Equal(1, asked);
            Assert.Equal(new[] { Alarm }, Messages);   // no second hand-over of a sentence that could not be re-read
            Assert.Equal(1, told);
        }

        [Fact]
        public void AFullWaitingSet_RefusesTheNewest_AndTellsIt_RatherThanEvictingTheOneThatWaitedLongest()
        {
            _arbiter.Urgent(Cut, VerbosityLevel.Critical, "Ptt", SpeechSubject.ReflectedPowerCut);
            var told = new List<string>();
            for (int i = 0; i < SpeechArbiter.AlarmPendingCap; i++)
            {
                string id = "alarm-" + i;
                _arbiter.UrgentAlarm("Warning " + i, VerbosityLevel.Critical, "AlarmDelivery", Subject(id),
                    () => "Warning " + id, () => told.Add(id));
            }
            Assert.Equal(SpeechArbiter.AlarmPendingCap, _arbiter.AlarmPendingCount);
            Assert.Empty(told);

            _arbiter.UrgentAlarm("Warning late", VerbosityLevel.Critical, "AlarmDelivery", Subject("late"),
                () => "Warning late", () => told.Add("late"));

            Assert.Equal(SpeechArbiter.AlarmPendingCap, _arbiter.AlarmPendingCount);
            Assert.Equal(new[] { "late" }, told);

            // Positive control: the first to wait is the first to speak when
            // the cut completes — it was not the one evicted.
            int words = NvdaCompletionChannel.SplitWords(Cut).Length;
            _arbiter.OnOutcome(TicketOf(Cut), Cut, SpeechOutcome.Completed(words, words, 2000));
            _clock.Advance(60);
            Assert.Equal("Warning alarm-0", Messages.Last());
        }

        [Fact]
        public void ASilence_LetsWaitingAlarmsGoWithoutTellingThem()
        {
            // The operator asked for quiet. Telling the alarm "not delivered"
            // would make its next reading speak again, which is the shut-up
            // key being overruled by the thing it shut up (#617: silence lasts
            // until the condition worsens, and worsening is the alarm's own
            // event, not this callback).
            int told = 0;
            _arbiter.Urgent(Cut, VerbosityLevel.Critical, "Ptt", SpeechSubject.ReflectedPowerCut);
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm, () => told++);
            _arbiter.OnSilenced();
            Assert.Equal(0, _arbiter.AlarmPendingCount);
            Assert.Equal(0, told);
        }
    }
}
