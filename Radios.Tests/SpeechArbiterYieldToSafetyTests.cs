#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  #778b round five: an ordinary interrupt that WAITS for the safety
    //  speaking turn instead of cutting it (SpeechArbiter.EmitYieldingToSafety).
    //
    //  The value layer's refusal, when its tone cannot sound, is spoken at
    //  every verbosity, Off included, so it is tagged Critical. Critical is a
    //  verbosity level, not a priority: as a plain interrupt it took the
    //  reader from a transmit-safety sentence or an alarm mid-word (Codex,
    //  round-four re-verify, citing
    //  SpeechArbiterAlarmOutcomeTests.AnAlarmCutByAnOrdinaryInterrupt_IsTold_SoItsNextReadingSaysItAgain).
    //  Noel ruled that the refusal waits behind safety speech. These pin the
    //  arbiter's half: never cut, said after, late rather than lost, and
    //  exactly the old interrupt when nothing safety-shaped is in the way.
    //
    //  Production-shaped like SpeechArbiterAlarmOutcomeTests: tracked tickets
    //  answered through OnOutcome, the way the pump answers them, on the
    //  injected clock.
    // ────────────────────────────────────────────────────────────────
    public class SpeechArbiterYieldToSafetyTests
    {
        private sealed record SinkCall(string Message, long Ticket, bool Interrupt);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();
        private long _nextTicket;
        private bool _tracked = true;
        private readonly SpeechArbiter _arbiter;

        private const string Cut = "Transmit stopped. You are no longer on the air.";
        private const string Alarm = "PA temperature 61 degrees C. Release transmit now.";
        private const string Alarm2 = "SWR 3 to 1. Check the antenna.";
        private const string Refusal = "You can't use push to talk here.";
        private static readonly string LayerStatus = SpeechSubject.ValueLayerStatus("audio");
        private static string Subject(string id = "pa") => SpeechSubject.OperatorAlarm(id);
        private static int Words(string s) => NvdaCompletionChannel.SplitWords(s).Length;

        public SpeechArbiterYieldToSafetyTests()
        {
            _arbiter = new SpeechArbiter(
                _clock,
                () => VerbosityLevel.Critical,
                (message, interrupt, intent, level, origin, salvaged) =>
                {
                    long t = ++_nextTicket;
                    _calls.Add(new SinkCall(message, t, interrupt));
                    return _tracked ? SpeechHandoff.TrackedAs(t) : SpeechHandoff.Untracked;
                },
                () => { },
                (message, level, intent, origin) => { });
        }

        private IEnumerable<string> Messages => _calls.Select(c => c.Message);
        private long TicketOf(string m) => _calls.Last(c => c.Message == m).Ticket;

        private void Refuse(string text = Refusal)
            => _arbiter.EmitYieldingToSafety(text, VerbosityLevel.Critical, "ValueSubLayer", LayerStatus);

        private void Complete(string m, int elapsedMs = 2000)
            => _arbiter.OnOutcome(TicketOf(m), m, SpeechOutcome.Completed(Words(m), Words(m), elapsedMs));

        [Fact]
        public void Control_with_nothing_safety_shaped_in_the_way_it_is_the_old_interrupt_at_once()
        {
            Refuse();

            var call = Assert.Single(_calls);
            Assert.Equal(Refusal, call.Message);
            Assert.True(call.Interrupt);
            Assert.Equal(0, _arbiter.YieldingToSafetyCount);
        }

        [Fact]
        public void During_an_alarm_it_does_not_cut_it_and_is_said_after_it()
        {
            int told = 0;
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm, () => told++);
            _clock.Advance(400);

            Refuse();

            // Not handed to the reader while the alarm holds the turn: nothing
            // cut it, and the alarm has nothing to be told.
            Assert.Equal(new[] { Alarm }, Messages);
            Assert.Equal(1, _arbiter.YieldingToSafetyCount);

            _clock.Advance(1600);
            Complete(Alarm);
            _clock.Advance(200);

            Assert.Equal(new[] { Alarm, Refusal }, Messages);
            Assert.False(_calls.Last().Interrupt);   // queued once the turn is free: it takes nothing from anyone
            Assert.Equal(0, told);
            Assert.Equal(0, _arbiter.YieldingToSafetyCount);
        }

        [Fact]
        public void During_a_transmit_safety_sentence_it_waits_and_is_said_after_it()
        {
            _arbiter.Urgent(Cut, VerbosityLevel.Critical, "Ptt", SpeechSubject.ReflectedPowerCut);
            _clock.Advance(300);

            Refuse();
            Assert.Equal(new[] { Cut }, Messages);

            _clock.Advance(1700);
            Complete(Cut);
            _clock.Advance(200);

            Assert.Equal(new[] { Cut, Refusal }, Messages);
        }

        [Fact]
        public void Two_alarms_back_to_back_it_follows_the_second_late_rather_than_lost()
        {
            // The reason it waits in the arbiter and not in the reader's queue:
            // queued behind the first alarm, the second alarm's hand-over would
            // have discarded it.
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject("pa"), () => Alarm);
            _clock.Advance(200);
            _arbiter.UrgentAlarm(Alarm2, VerbosityLevel.Critical, "AlarmDelivery", Subject("swr"), () => Alarm2);
            Assert.Equal(1, _arbiter.AlarmPendingCount);

            Refuse();
            Assert.Equal(new[] { Alarm }, Messages);

            _clock.Advance(1800);
            Complete(Alarm);
            _clock.Advance(200);
            Assert.Equal(new[] { Alarm, Alarm2 }, Messages);   // the second alarm first

            _clock.Advance(1500);
            Complete(Alarm2);
            _clock.Advance(200);
            Assert.Equal(new[] { Alarm, Alarm2, Refusal }, Messages);
        }

        [Fact]
        public void An_untracked_safety_turn_is_waited_out_on_its_estimate()
        {
            _tracked = false;
            _arbiter.Urgent(Cut, VerbosityLevel.Critical, "Ptt", SpeechSubject.ReflectedPowerCut);

            Refuse();
            Assert.Equal(new[] { Cut }, Messages);

            _clock.Advance(SpeechArbiter.SalvageCapMs + 500);
            Assert.Equal(new[] { Cut, Refusal }, Messages);
        }

        [Fact]
        public void Several_refusals_during_one_alarm_are_answered_once_with_the_newest()
        {
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm);
            Refuse("You can't use push to talk here.");
            Refuse("You can't use the transmit lock here.");
            Assert.Equal(1, _arbiter.YieldingToSafetyCount);

            _clock.Advance(2000);
            Complete(Alarm);
            _clock.Advance(200);

            Assert.Equal(new[] { Alarm, "You can't use the transmit lock here." }, Messages);
        }

        [Fact]
        public void The_operators_silence_lets_it_go_as_it_lets_go_of_waiting_alarms()
        {
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm);
            Refuse();
            _arbiter.OnSilenced();
            Assert.Equal(0, _arbiter.YieldingToSafetyCount);

            _clock.Advance(SpeechArbiter.SalvageCapMs + 5000);
            Assert.DoesNotContain(Refusal, Messages);
        }

        [Fact]
        public void An_ordinary_interrupt_still_cuts_an_alarm_exactly_as_before()
        {
            // Opt-in, per call: nothing else changed how it interrupts.
            _arbiter.UrgentAlarm(Alarm, VerbosityLevel.Critical, "AlarmDelivery", Subject(), () => Alarm);
            _arbiter.Emit("Slice A", interrupt: true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "keys", SpeechSubject.WhereYouAre);
            Assert.Equal(new[] { Alarm, "Slice A" }, Messages);
        }
    }
}
