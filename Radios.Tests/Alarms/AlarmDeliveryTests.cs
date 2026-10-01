using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios.Alarms;
using Radios.Speech;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>Sound, then speech at Critical and Urgent, from the dispatch worker, with the honest preview.</summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class AlarmDeliveryTests : IDisposable
    {
        private sealed class FakeSpeaker : IAlarmSpeaker
        {
            public readonly List<(string Text, string Subject, Func<string?> Refresh)> Warnings = new();
            public readonly List<(string Text, VerbosityLevel Level, string Subject)> Status = new();
            public readonly List<Action?> NotDelivered = new();
            public readonly List<Action?> Silenced = new();
            public void SpeakWarning(string text, string subject, Func<string?> refresh, Action? notDelivered = null, Action? silenced = null)
            {
                lock (Warnings) { Warnings.Add((text, subject, refresh)); NotDelivered.Add(notDelivered); Silenced.Add(silenced); }
            }
            public void SpeakStatus(string text, VerbosityLevel level, string subject) { lock (Status) Status.Add((text, level, subject)); }
        }

        private const string Serial = "1234-5678-9012-3456";
        private static readonly MeterDescriptor Pa = PaTemperatureReplayFixture.Meter;

        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-alarmdel-" + Guid.NewGuid().ToString("N"));
        private readonly FakeAlarmFeed _feed = new();
        private readonly ManualAlarmClock _clock = new() { NowMs = 10_000 };
        private readonly FakeSpeechClock _speechClock = new();
        private readonly FakeSpeaker _speaker = new();
        private readonly List<string> _sounds = new();
        private readonly List<AlarmDeliveryReport> _reports = new();
        private AlarmService? _service;
        private AlarmDelivery? _delivery;
        private bool _warningsSoundOn = true;
        private long _cohort;

        public void Dispose()
        {
            _delivery?.Dispose();
            _service?.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private AlarmService Up(bool withSound = true)
        {
            _service = new AlarmService(_feed, new AlarmDefinitionStore(_root), _clock, null, startWatchdog: false);
            _delivery = new AlarmDelivery(_service, _speaker, withSound ? () => _sounds.Add("tone") : null, _speechClock,
                () => _warningsSoundOn, () => false, () => true, () => _cohort);
            _delivery.Reported += r => { lock (_reports) _reports.Add(r); };
            _feed.Connect(Serial, Pa);
            Assert.True(_service.Add(AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa") with { Enabled = true }));
            return _service;
        }

        private void Deliver(float value)
        {
            _clock.Advance(2000);
            _feed.Deliver(Pa, value);
            Assert.True(_service!.DrainDispatch(2000));
        }

        [Fact]
        public void A_firing_sounds_the_tone_first_and_speaks_the_warning_after_the_tone_lead()
        {
            Up();
            Deliver(63.5f);

            Assert.Equal(new[] { "tone" }, _sounds);
            Assert.Empty(_speaker.Warnings);   // not yet: the tone is playing
            _speechClock.Advance(AlarmDelivery.ToneLeadMs - 1);
            Assert.Empty(_speaker.Warnings);
            _speechClock.Advance(1);

            var w = Assert.Single(_speaker.Warnings);
            Assert.Equal("Stay in receive. PA temperature is 63.5 degrees C. Let the radio cool.", w.Text);
            Assert.Equal(SpeechSubject.OperatorAlarm("pa"), w.Subject);
            var report = Assert.Single(_reports);
            Assert.True(report.SoundRequested);
            Assert.Equal(AlarmDelivery.ToneLeadMs, report.ToneLeadMs);
        }

        [Fact]
        public void With_the_warning_sound_off_speech_is_immediate_and_the_tone_is_still_asked_for_so_the_gate_decides()
        {
            _warningsSoundOn = false;
            Up();
            Deliver(63.5f);
            Assert.Single(_speaker.Warnings);
            Assert.Single(_sounds);   // EarconPlayer gates on its category; the call proves the wiring
            Assert.False(_reports[0].SoundRequested);
        }

        /// <summary>
        /// Astra's Track IJK review, blocker 3. With the warning sound off the
        /// immediate branch handed the EVENT's own sentence to speech without
        /// asking the refresh first, and the event had come through an
        /// asynchronous dispatch queue: a worker delayed past the readings that
        /// cleared the episode, or past the operator disabling the alarm, spoke
        /// the old measurement as though it were current. The tone path
        /// re-read before speaking; the sound-off path did not. Driven with the
        /// dispatch worker genuinely held, which is the delay the finding
        /// names, not a stub.
        /// </summary>
        [Theory]
        [InlineData("cleared")]
        [InlineData("disabled")]
        [InlineData("none")]   // the positive control: nothing changed while the worker was held, and it speaks
        public void With_the_sound_off_a_warning_dispatched_late_is_re_read_before_it_speaks(string meanwhile)
        {
            _warningsSoundOn = false;
            _service = new AlarmService(_feed, new AlarmDefinitionStore(_root), _clock, null, startWatchdog: false);
            var gate = new ManualResetEventSlim(false);
            // Subscribed BEFORE the delivery, so it runs first on the worker
            // and holds the Fired event there until the test lets go.
            _service.EventDispatched += e => { if (e.Kind == AlarmEventKind.Fired) gate.Wait(5000); };
            _delivery = new AlarmDelivery(_service, _speaker, () => _sounds.Add("tone"), _speechClock,
                () => _warningsSoundOn, () => false, () => true, () => _cohort);
            _delivery.Reported += r => { lock (_reports) _reports.Add(r); };
            _feed.Connect(Serial, Pa);
            Assert.True(_service.Add(AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa") with { Enabled = true }));

            _clock.Advance(2000); _feed.Deliver(Pa, 63.5f);   // Fired: the worker takes it and is held
            Thread.Sleep(100);

            switch (meanwhile)
            {
                case "cleared":
                    _clock.Advance(2000); _feed.Deliver(Pa, 57f);
                    _clock.Advance(2000); _feed.Deliver(Pa, 57f);
                    _clock.Advance(2000); _feed.Deliver(Pa, 57f);
                    Assert.Equal(AlarmConditionState.Normal, _service.SnapshotOf("pa")!.Condition);
                    break;
                case "disabled":
                    Assert.True(_service.SetEnabled("pa", false));
                    break;
            }

            gate.Set();
            Assert.True(_service.DrainDispatch(5000));

            if (meanwhile == "none")
            {
                var w = Assert.Single(_speaker.Warnings);
                Assert.Contains("63.5", w.Text);
                Assert.Single(_reports, r => r.Event.Kind == AlarmEventKind.Fired && r.SpeechRequested);
                return;
            }

            // The stale sentence is NOT spoken, and the report says speech was
            // withdrawn rather than requested.
            Assert.Empty(_speaker.Warnings);
            var report = Assert.Single(_reports, r => r.Event.Kind == AlarmEventKind.Fired);
            Assert.False(report.SpeechRequested);
            if (meanwhile == "cleared")
                Assert.Single(_speaker.Status, s => s.Text.StartsWith("High PA temperature cleared"));
        }

        /// <summary>
        /// Astra's Track IJK2 review, blocker 3 — introduced on the sound-off
        /// route by the re-read above. The refresh correctly withdraws a
        /// cleared or disabled episode, but an episode inside its clear margin
        /// is still ACTIVE, so the re-read substituted the current reading into
        /// the Fired frame: "Your alarm named Heat fired: PATEMP (PA
        /// Temperature) is 59 degrees C, at or above 60 degrees C." The
        /// comparison was false of the number stated. Driven exactly as the
        /// finding describes: a custom level alarm, dispatch worker held on
        /// the Fired event, a fresh in-band reading, release with the sound
        /// off. Both directions.
        /// </summary>
        [Theory]
        [InlineData("above")]
        [InlineData("below")]
        public void With_the_sound_off_a_late_warning_whose_reading_is_now_inside_the_margin_states_the_reading_without_the_false_comparison(string side)
        {
            var supplyA = new MeterDescriptor(2, "+13.8A", "+13.8V at PA", "RAD", 2, MeterUnits.Volts, 10.5, 15);
            _warningsSoundOn = false;
            _service = new AlarmService(_feed, new AlarmDefinitionStore(_root), _clock, null, startWatchdog: false);
            var gate = new ManualResetEventSlim(false);
            _service.EventDispatched += e => { if (e.Kind == AlarmEventKind.Fired) gate.Wait(5000); };
            _delivery = new AlarmDelivery(_service, _speaker, () => _sounds.Add("tone"), _speechClock,
                () => _warningsSoundOn, () => false, () => true, () => _cohort);
            _delivery.Reported += r => { lock (_reports) _reports.Add(r); };
            _feed.Connect(Serial, Pa, supplyA);

            MeterDescriptor meter;
            float fired, inBand;
            string expected;
            if (side == "above")
            {
                Assert.True(_service.Add(AlarmDefinition.NewLevel("heat", "Heat", Serial, MeterSelector.From(Pa), AlarmDirection.AtOrAbove, 60, 2)
                    with { Enabled = true, Action = AlarmActionClass.NotifyOnly }));
                meter = Pa; fired = 61f; inBand = 59f;
                expected = "Your alarm named Heat is still active: PATEMP (PA Temperature) is 59 degrees C. The alarm clears at or below 58 degrees C.";
            }
            else
            {
                Assert.True(_service.Add(AlarmDefinition.NewLevel("lv", "Low volts", Serial, MeterSelector.From(supplyA), AlarmDirection.AtOrBelow, 12, 0.2)
                    with { Enabled = true, Action = AlarmActionClass.NotifyOnly }));
                meter = supplyA; fired = 11.9f; inBand = 12.1f;
                expected = "Your alarm named Low volts is still active: +13.8A (+13.8V at PA) is 12.10 volts. The alarm clears at or above 12.20 volts.";
            }

            _clock.Advance(2000); _feed.Deliver(meter, fired);   // Fired: the worker takes it and is held
            Thread.Sleep(100);
            _clock.Advance(2000); _feed.Deliver(meter, inBand);  // inside the margin: still active, nothing to say
            string id = side == "above" ? "heat" : "lv";
            Assert.Equal(AlarmConditionState.Active, _service.SnapshotOf(id)!.Condition);

            gate.Set();
            Assert.True(_service.DrainDispatch(5000));

            var w = Assert.Single(_speaker.Warnings);
            Assert.Equal(expected, w.Text);
            Assert.DoesNotContain(side == "above" ? "at or above" : "at or below", w.Text);
            Assert.Single(_reports, r => r.Event.Kind == AlarmEventKind.Fired && r.SpeechRequested);
        }

        /// <summary>
        /// The same question on the tone route's refresh, which had the
        /// pre-existing form of the defect: the closure handed to speech is
        /// asked again later, with the reading wherever it has gone. Inside
        /// the margin it states the reading and what clears the alarm; back on
        /// the alarm side it is the fired frame again, whose comparison is
        /// true; past the clear line, after the clearing samples, nothing.
        /// </summary>
        [Fact]
        public void The_refresh_closure_follows_the_reading_through_the_margin_and_back_without_a_false_comparison()
        {
            _service = new AlarmService(_feed, new AlarmDefinitionStore(_root), _clock, null, startWatchdog: false);
            _delivery = new AlarmDelivery(_service, _speaker, () => _sounds.Add("tone"), _speechClock,
                () => _warningsSoundOn, () => false, () => true, () => _cohort);
            _feed.Connect(Serial, Pa);
            Assert.True(_service.Add(AlarmDefinition.NewLevel("heat", "Heat", Serial, MeterSelector.From(Pa), AlarmDirection.AtOrAbove, 60, 2)
                with { Enabled = true, Action = AlarmActionClass.NotifyOnly }));

            Deliver(61f);
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            var refresh = _speaker.Warnings[0].Refresh;
            Assert.Equal("Your alarm named Heat fired: PATEMP (PA Temperature) is 61 degrees C, at or above 60 degrees C.", refresh());

            Deliver(59f);   // inside the margin
            Assert.Equal("Your alarm named Heat is still active: PATEMP (PA Temperature) is 59 degrees C. The alarm clears at or below 58 degrees C.", refresh());

            Deliver(62f);   // back on the alarm side: the fired frame is true again
            Assert.Equal("Your alarm named Heat fired: PATEMP (PA Temperature) is 62 degrees C, at or above 60 degrees C.", refresh());

            Deliver(58f);   // one clearing sample: still active, and still no false comparison
            Assert.Equal("Your alarm named Heat is still active: PATEMP (PA Temperature) is 58 degrees C. The alarm clears at or below 58 degrees C.", refresh());

            Deliver(58f);   // the second clearing sample: cleared, nothing to say
            Assert.Equal(AlarmConditionState.Normal, _service.SnapshotOf("heat")!.Condition);
            Assert.Null(refresh());
        }

        [Fact]
        public void The_refresh_re_reads_the_current_value_and_says_nothing_once_cleared_or_acknowledged()
        {
            var s = Up();
            Deliver(61f);
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            var refresh = _speaker.Warnings[0].Refresh;
            Assert.Equal("Stay in receive. PA temperature is 61 degrees C. Let the radio cool.", refresh());

            Deliver(62.4f);   // in the reminder interval: no new event, but the value moved
            Assert.Equal("Stay in receive. PA temperature is 62.4 degrees C. Let the radio cool.", refresh());

            s.Acknowledge("pa");
            Assert.Null(refresh());
            s.Resume("pa");
            Deliver(57f); Deliver(57f); Deliver(57f);   // clears
            Assert.Null(refresh());
        }

        [Fact]
        public void A_clear_during_the_tone_withdraws_the_sentence_and_speaks_the_clearance_as_a_queued_state_update()
        {
            Up();
            Deliver(61f);
            Deliver(57f); Deliver(57f); Deliver(57f);   // cleared before the tone lead elapses on the speech clock
            _speechClock.Advance(AlarmDelivery.ToneLeadMs + 10);

            Assert.Empty(_speaker.Warnings);
            var cleared = Assert.Single(_speaker.Status, s => s.Text.StartsWith("High PA temperature cleared"));
            Assert.Equal(VerbosityLevel.Terse, cleared.Level);
            Assert.Equal(SpeechSubject.OperatorAlarmStatus, cleared.Subject);
        }

        [Fact]
        public void A_newer_warning_for_the_same_alarm_inside_the_lead_replaces_the_words_and_keeps_the_first_deadline()
        {
            // Astra's additional observation: a repeated worsening restarted
            // the 750 ms timer, so a steadily worsening meter could postpone
            // its own sentence indefinitely. The words move; the deadline is
            // the FIRST warning's.
            Up();
            Deliver(61f);
            _speechClock.Advance(300);
            Deliver(63.2f);   // a worsening: 2.2 over the announced 61
            _speechClock.Advance(AlarmDelivery.ToneLeadMs - 300 - 1);
            Assert.Empty(_speaker.Warnings);
            _speechClock.Advance(1);   // 750 ms after the FIRST warning, not the second
            var w = Assert.Single(_speaker.Warnings);
            Assert.Contains("63.2", w.Text);
            Assert.Equal(2, _sounds.Count);
        }

        [Fact]
        public void A_worsening_after_the_operator_silenced_speech_is_a_new_fact_and_is_spoken()
        {
            // #617, ruled 2026-09-24: silence lasts until the condition gets
            // worse; a worse reading is a new fact, not a replay. The
            // replacement inside the lead re-reads the quiet cohort for
            // exactly that reason.
            Up();
            Deliver(61f);
            _cohort = 1;           // Ctrl, during the tone
            _speechClock.Advance(300);
            Deliver(63.2f);        // worsening, after the Ctrl
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            var w = Assert.Single(_speaker.Warnings);
            Assert.Contains("63.2", w.Text);
        }

        [Fact]
        public void A_silence_after_the_replacement_still_stands_the_sentence_down_and_the_alarm_is_told()
        {
            Up();
            Deliver(61f);
            _speechClock.Advance(300);
            Deliver(63.2f);
            _cohort = 1;           // Ctrl, after the worsening
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            Assert.Empty(_speaker.Warnings);
            var snap = _service!.SnapshotOf("pa")!;
            Assert.Equal(AlarmConditionState.Active, snap.Condition);
            // #617: the alarm knows it was silenced — not acknowledged — so its
            // interval reminder of the same reading is withheld until worse.
            Assert.True(snap.SilencedUntilWorse);
            Assert.Equal(AlarmNotificationState.Unacknowledged, snap.Notification);
        }

        [Fact]
        public void A_silence_during_the_tone_withholds_the_interval_reminder_until_the_reading_worsens()
        {
            // The #617 gap the predecessor named: Ctrl stood the current
            // presentation down, and the next 30-second interval reminder of
            // the SAME reading spoke anyway. Through the real delivery, service
            // and monitor on manual clocks; the speech layer is the fake
            // speaker, so the silence here is the one the delivery itself
            // detects during the tone.
            Up();
            Deliver(61f);
            _cohort = 1;                                   // Ctrl, during the tone
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            Assert.Empty(_speaker.Warnings);

            // Thirty-odd seconds of the same reading: no reminder.
            for (int i = 0; i < 18; i++) { Deliver(61f); _speechClock.Advance(AlarmDelivery.ToneLeadMs); }
            Assert.Empty(_speaker.Warnings);
            Assert.True(_service!.SnapshotOf("pa")!.SilencedUntilWorse);

            // A worse reading is a new fact and speaks; the silence is over.
            Deliver(63.5f);
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            var w = Assert.Single(_speaker.Warnings);
            Assert.Contains("63.5", w.Text);
            Assert.False(_service.SnapshotOf("pa")!.SilencedUntilWorse);

            // And the routine reminders are back: thirty seconds on, it says so.
            for (int i = 0; i < 18; i++) { Deliver(63.5f); _speechClock.Advance(AlarmDelivery.ToneLeadMs); }
            Assert.Equal(2, _speaker.Warnings.Count);
        }

        [Fact]
        public void PositiveControl_WithoutASilence_TheIntervalReminderOfTheSameReadingSpeaks()
        {
            Up();
            Deliver(61f);
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            Assert.Single(_speaker.Warnings);
            for (int i = 0; i < 18; i++) { Deliver(61f); _speechClock.Advance(AlarmDelivery.ToneLeadMs); }
            Assert.Equal(2, _speaker.Warnings.Count);
        }

        [Fact]
        public void Data_lost_while_transmitting_is_a_warning_and_in_receive_a_queued_critical_line()
        {
            var s = Up(withSound: false);
            Deliver(30f);
            _feed.Key(true);
            // Stale is declared on the first tick past the five-second
            // allowance: 5.25 s. Drained HERE, because the sentence is now
            // re-read when it is spoken (blocker 3) and carries the age at
            // that moment — ticking on to six seconds before draining would
            // honestly say six.
            for (int i = 0; i < 21; i++) { _clock.Advance(250); s.Tick(_clock.NowMs); }
            Assert.True(s.DrainDispatch(2000));
            var w = Assert.Single(_speaker.Warnings);
            Assert.Equal("High PA temperature: no PATEMP (PA Temperature) reading for 5 seconds. It cannot be watched. Stop the transmission.", w.Text);
            for (int i = 0; i < 3; i++) { _clock.Advance(250); s.Tick(_clock.NowMs); }

            _feed.Key(false);
            Deliver(30f); Deliver(30f);   // resumes
            for (int i = 0; i < 24; i++) { _clock.Advance(250); s.Tick(_clock.NowMs); }
            Assert.True(s.DrainDispatch(2000));
            var line = Assert.Single(_speaker.Status, x => x.Text.Contains("cannot be watched"));
            Assert.Equal(VerbosityLevel.Critical, line.Level);
            Assert.DoesNotContain("Stop the transmission", line.Text);
        }

        [Fact]
        public void The_preview_goes_down_the_real_path_labelled_a_test_and_never_touches_the_monitor()
        {
            var s = Up();
            s.Preview("pa");
            Assert.True(s.DrainDispatch(2000));
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);

            Assert.Single(_sounds);
            var w = Assert.Single(_speaker.Warnings);
            Assert.StartsWith("Test warning from the alarm High PA temperature. Nothing is wrong. A real warning would say: Stay in receive. PA temperature is 60 degrees C.", w.Text);
            Assert.True(_reports[0].IsPreview);
            Assert.Equal(AlarmConditionState.Normal, s.SnapshotOf("pa")!.Condition);
            Assert.Equal(w.Text, w.Refresh());   // a preview never re-reads a live value
        }

        [Fact]
        public void The_preview_says_when_the_warning_sound_is_off()
        {
            _warningsSoundOn = false;
            var s = Up();
            s.Preview("pa");
            Assert.True(s.DrainDispatch(2000));
            var w = Assert.Single(_speaker.Warnings);
            Assert.EndsWith("The warning sound is switched off in your earcon settings, so a real warning would be speech only.", w.Text);
        }

        [Fact]
        public void A_worsening_after_acknowledgement_is_delivered_as_a_new_warning_through_the_tone_path()
        {
            // Astra's Track I review, finding 1: the old version of this test
            // used withSound:false, which skips the continuation whose refresh
            // dropped the sentence. On the DEFAULT tone path the acknowledged
            // alarm played the tone and said nothing. Through the tone, and
            // through the refresh, now.
            var s = Up();
            Deliver(61f);
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            Assert.Single(_speaker.Warnings);

            s.Acknowledge("pa");
            Deliver(63.5f);   // worsened by 2.5 over the announced 61
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);

            Assert.Equal(2, _speaker.Warnings.Count);
            Assert.Contains("63.5", _speaker.Warnings[1].Text);
            // And the refresh the arbiter would call on a deferral agrees.
            Assert.Contains("63.5", _speaker.Warnings[1].Refresh());
        }

        [Fact]
        public void An_acknowledgement_made_AFTER_the_worsening_was_raised_withdraws_it_during_the_tone()
        {
            // The other half of finding 1: an earlier acknowledgement is
            // overridden by a worsening, a LATER one answers it. The operator
            // hears the tone, presses Acknowledge, and the sentence does not
            // then arrive anyway.
            var s = Up();
            Deliver(61f);
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            Deliver(63.5f);
            _speechClock.Advance(300);
            s.Acknowledge("pa");
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            Assert.Single(_speaker.Warnings);   // only the first
        }

        [Fact]
        public void A_snoozed_alarm_that_worsens_is_spoken_and_a_plain_reminder_is_not()
        {
            var s = Up();
            Deliver(61f);
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            s.Snooze("pa", 120);

            // Thirty-odd seconds of the same reading: the interval reminder
            // is owed to the snooze and stays quiet.
            for (int i = 0; i < 16; i++) Deliver(61f);
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            Assert.Single(_speaker.Warnings);

            Deliver(63.5f);
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            Assert.Equal(2, _speaker.Warnings.Count);
            Assert.Contains("63.5", _speaker.Warnings[1].Text);
        }

        [Fact]
        public void A_refresh_from_an_older_episode_says_nothing_once_a_new_episode_has_fired()
        {
            var s = Up(withSound: false);
            Deliver(61f);
            var first = _speaker.Warnings[0].Refresh;
            Deliver(57f); Deliver(57f); Deliver(57f);   // cleared
            Deliver(62f);                                // a NEW episode fires and speaks for itself
            Assert.Equal(2, _speaker.Warnings.Count);
            Assert.Null(first());
        }
    }
}
