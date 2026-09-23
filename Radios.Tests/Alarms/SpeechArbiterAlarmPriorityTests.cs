using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.Speech;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// The alarm-aware urgent contract (#566, design section 5), on the fake
    /// clock: a cut always wins, two alarms take turns, a waiting alarm
    /// re-reads itself before it speaks, and a foreign cancellation earns one
    /// retry. Every existing Urgent caller is untouched by it.
    /// </summary>
    public sealed class SpeechArbiterAlarmPriorityTests
    {
        private sealed record SinkCall(string Message, bool Interrupt, SpeechIntent? Intent, double AtMs);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();
        private int _silences;
        private long _nextTicket = 100;
        private bool _tracked;

        private SpeechArbiter NewArbiter() => new SpeechArbiter(
            _clock,
            () => VerbosityLevel.Chatty,
            (message, interrupt, intent, level, origin, salvaged) =>
            {
                _calls.Add(new SinkCall(message, interrupt, intent, _clock.ElapsedMs));
                return _tracked ? SpeechHandoff.TrackedAs(_nextTicket++) : SpeechHandoff.Untracked;
            },
            () => _silences++,
            (message, level, intent, origin) => { });

        private static string Subject(string id) => SpeechSubject.OperatorAlarm(id);

        [Fact]
        public void An_alarm_with_nothing_sounding_speaks_at_once_as_an_urgent()
        {
            var a = NewArbiter();
            a.Emit("stale chatter", interrupt: false, SpeechIntent.Queue, VerbosityLevel.Chatty, "t");
            a.UrgentAlarm("PA temperature 63 degrees C. Release transmit now.", VerbosityLevel.Critical, "t",
                Subject("pa"), () => "PA temperature 63 degrees C. Release transmit now.");

            var last = _calls.Last();
            Assert.Equal(SpeechIntent.Urgent, last.Intent);
            Assert.True(last.Interrupt);
            Assert.Equal(1, _silences);
            Assert.Equal(0, a.AlarmPendingCount);
        }

        [Fact]
        public void A_pending_or_sounding_cut_always_wins_and_the_alarm_follows_after_it_still_true()
        {
            var a = NewArbiter();
            string cut = "Reflected power cut the transmission. Twelve watts came back. You are no longer on the air.";
            a.Urgent(cut, VerbosityLevel.Critical, "cut");
            int estimate = SpeechArbiter.EstimateSpokenMs(cut);

            string current = "PA temperature 63 degrees C. Release transmit now.";
            a.UrgentAlarm("PA temperature 62 degrees C. Release transmit now.", VerbosityLevel.Critical, "alarm",
                Subject("pa"), () => current);

            // Not spoken yet; the cut was the last thing handed over.
            Assert.Equal(cut, _calls.Last().Message);
            Assert.Equal(1, a.AlarmPendingCount);

            _clock.Advance(estimate - 100);
            Assert.Equal(cut, _calls.Last().Message);

            _clock.Advance(200);
            Assert.Equal(current, _calls.Last().Message);   // the RE-READ sentence, not the queued one
            Assert.Equal(SpeechIntent.Urgent, _calls.Last().Intent);
            Assert.Equal(0, a.AlarmPendingCount);
        }

        [Fact]
        public void Two_alarms_take_turns_rather_than_cancelling_each_other()
        {
            var a = NewArbiter();
            string pa = "PA temperature 63 degrees C. Release transmit now.";
            string volts = "Supply voltage, at the PA, is 11.90 volts. Release transmit and have the supply path checked.";
            a.UrgentAlarm(pa, VerbosityLevel.Critical, "pa", Subject("pa"), () => pa);
            a.UrgentAlarm(volts, VerbosityLevel.Critical, "v", Subject("v"), () => volts);

            Assert.Single(_calls);
            Assert.Equal(pa, _calls[0].Message);
            Assert.Equal(1, a.AlarmPendingCount);

            _clock.Advance(SpeechArbiter.EstimateSpokenMs(pa) + 100);
            Assert.Equal(2, _calls.Count);
            Assert.Equal(volts, _calls[1].Message);
            Assert.Equal(2, _silences);   // one cut per hand-over, and the second came AFTER the first finished
        }

        [Fact]
        public void A_newer_statement_on_the_same_subject_replaces_the_waiting_one_and_keeps_its_place()
        {
            var a = NewArbiter();
            a.Urgent("Hard stop.", VerbosityLevel.Critical, "stop");
            a.UrgentAlarm("PA temperature 61 degrees C. Release transmit now.", VerbosityLevel.Critical, "pa",
                Subject("pa"), () => "PA temperature 61 degrees C. Release transmit now.");
            a.UrgentAlarm("Supply voltage is 11.90 volts.", VerbosityLevel.Critical, "v",
                Subject("v"), () => "Supply voltage is 11.90 volts.");
            a.UrgentAlarm("PA temperature 63 degrees C. Release transmit now.", VerbosityLevel.Critical, "pa",
                Subject("pa"), () => "PA temperature 63 degrees C. Release transmit now.");
            Assert.Equal(2, a.AlarmPendingCount);

            _clock.Advance(SpeechArbiter.EstimateSpokenMs("Hard stop.") + 100);
            Assert.Equal("PA temperature 63 degrees C. Release transmit now.", _calls[1].Message);   // PA first, newest words
            _clock.Advance(SpeechArbiter.EstimateSpokenMs(_calls[1].Message) + 100);
            Assert.Equal("Supply voltage is 11.90 volts.", _calls[2].Message);
        }

        [Fact]
        public void A_waiting_alarm_whose_refresh_says_nothing_is_dropped_not_spoken_stale()
        {
            var a = NewArbiter();
            a.Urgent("Hard stop.", VerbosityLevel.Critical, "stop");
            bool stillActive = true;
            a.UrgentAlarm("PA temperature 61 degrees C. Release transmit now.", VerbosityLevel.Critical, "pa",
                Subject("pa"), () => stillActive ? "PA temperature 61 degrees C. Release transmit now." : null);
            stillActive = false;   // cleared while the stop was sounding

            _clock.Advance(SpeechArbiter.EstimateSpokenMs("Hard stop.") + 100);
            Assert.Single(_calls);
            Assert.Equal(0, a.AlarmPendingCount);
        }

        [Fact]
        public void The_operator_silence_lets_waiting_alarms_go_and_does_not_retry()
        {
            var a = NewArbiter();
            a.Urgent("Hard stop.", VerbosityLevel.Critical, "stop");
            a.UrgentAlarm("PA temperature 61 degrees C.", VerbosityLevel.Critical, "pa", Subject("pa"), () => "PA temperature 61 degrees C.");
            a.OnSilenced();
            Assert.Equal(0, a.AlarmPendingCount);
            _clock.Advance(SpeechArbiter.EstimateSpokenMs("Hard stop.") + 100);
            Assert.Single(_calls);
        }

        [Fact]
        public void The_pending_set_is_bounded_and_drops_the_oldest()
        {
            var a = NewArbiter();
            a.Urgent("Hard stop.", VerbosityLevel.Critical, "stop");
            for (int i = 0; i < SpeechArbiter.AlarmPendingCap + 2; i++)
            {
                string m = "Alarm " + i;
                a.UrgentAlarm(m, VerbosityLevel.Critical, "x", Subject("a" + i), () => m);
            }
            Assert.Equal(SpeechArbiter.AlarmPendingCap, a.AlarmPendingCount);
        }

        [Fact]
        public void A_cancellation_not_by_us_earns_exactly_one_retry_after_the_reader_settles_if_still_true()
        {
            _tracked = true;
            var a = NewArbiter();
            string current = "PA temperature 63 degrees C. Release transmit now.";
            a.UrgentAlarm("PA temperature 62 degrees C. Release transmit now.", VerbosityLevel.Critical, "pa",
                Subject("pa"), () => current);
            long ticket = _nextTicket - 1;

            _clock.Advance(300);
            a.OnOutcome(ticket, _calls[0].Message, SpeechOutcome.Cancelled(marksReached: 2, markCount: 8, byUs: false, elapsedMs: 300));
            Assert.Single(_calls);
            _clock.Advance(SpeechArbiter.AlarmRetrySettleMs);
            Assert.Equal(2, _calls.Count);
            Assert.Equal(current, _calls[1].Message);   // re-read

            // A second foreign cancellation does not earn a second retry.
            long ticket2 = _nextTicket - 1;
            _clock.Advance(300);
            a.OnOutcome(ticket2, _calls[1].Message, SpeechOutcome.Cancelled(2, 8, byUs: false, elapsedMs: 300));
            _clock.Advance(SpeechArbiter.AlarmRetrySettleMs + 100);
            Assert.Equal(2, _calls.Count);
        }

        [Fact]
        public void A_cancellation_by_us_or_a_completion_earns_no_retry()
        {
            _tracked = true;
            var a = NewArbiter();
            a.UrgentAlarm("PA temperature 62 degrees C.", VerbosityLevel.Critical, "pa", Subject("pa"), () => "PA temperature 62 degrees C.");
            long ticket = _nextTicket - 1;
            _clock.Advance(300);
            a.OnOutcome(ticket, _calls[0].Message, SpeechOutcome.Cancelled(2, 8, byUs: true, elapsedMs: 300));
            _clock.Advance(SpeechArbiter.AlarmRetrySettleMs + 100);
            Assert.Single(_calls);

            a.UrgentAlarm("Again.", VerbosityLevel.Critical, "pa", Subject("pa"), () => "Again.");
            long ticket2 = _nextTicket - 1;
            _clock.Advance(300);
            a.OnOutcome(ticket2, "Again.", SpeechOutcome.Completed(1, 1, 300));
            _clock.Advance(SpeechArbiter.AlarmRetrySettleMs + 100);
            Assert.Equal(2, _calls.Count);
        }

        [Fact]
        public void A_retry_outside_the_five_second_window_is_not_made()
        {
            _tracked = true;
            var a = NewArbiter();
            a.UrgentAlarm("PA temperature 62 degrees C.", VerbosityLevel.Critical, "pa", Subject("pa"), () => "PA temperature 62 degrees C.");
            long ticket = _nextTicket - 1;
            _clock.Advance(SpeechArbiter.AlarmRetryWindowMs + 1);
            a.OnOutcome(ticket, _calls[0].Message, SpeechOutcome.Cancelled(2, 8, byUs: false, elapsedMs: 5001));
            _clock.Advance(SpeechArbiter.AlarmRetrySettleMs + 100);
            Assert.Single(_calls);
        }

        [Fact]
        public void A_completion_releases_a_waiting_alarm_early()
        {
            _tracked = true;
            var a = NewArbiter();
            string pa = "PA temperature 63 degrees C. Release transmit now.";
            string v = "Supply voltage is 11.90 volts.";
            a.UrgentAlarm(pa, VerbosityLevel.Critical, "pa", Subject("pa"), () => pa);
            long ticket = _nextTicket - 1;
            a.UrgentAlarm(v, VerbosityLevel.Critical, "v", Subject("v"), () => v);
            Assert.Single(_calls);

            _clock.Advance(500);
            a.OnOutcome(ticket, pa, SpeechOutcome.Completed(8, 8, 500));
            _clock.Advance(100);
            Assert.Equal(2, _calls.Count);
            Assert.Equal(v, _calls[1].Message);
        }

        [Fact]
        public void Existing_untagged_urgent_callers_behave_exactly_as_before()
        {
            var a = NewArbiter();
            a.Emit("queued one", false, SpeechIntent.Queue, VerbosityLevel.Chatty, "q");
            a.Urgent("Hard stop.", VerbosityLevel.Critical, "stop");
            Assert.Equal("Hard stop.", _calls.Last().Message);
            Assert.Equal(1, _silences);
            _clock.Advance(SpeechArbiter.SalvageSettleMs + 1);
            Assert.DoesNotContain(_calls, c => c.Message == "queued one" && c.AtMs > 0);   // discarded, never salvaged
        }
    }
}
