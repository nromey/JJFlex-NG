using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            public void SpeakWarning(string text, string subject, Func<string?> refresh, Action? notDelivered = null)
            {
                lock (Warnings) { Warnings.Add((text, subject, refresh)); NotDelivered.Add(notDelivered); }
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
                () => _warningsSoundOn, () => false, () => true);
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
            Assert.Equal("PA temperature 63.5 degrees C. Stay in receive and let the radio cool.", w.Text);
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

        [Fact]
        public void The_refresh_re_reads_the_current_value_and_says_nothing_once_cleared_or_acknowledged()
        {
            var s = Up();
            Deliver(61f);
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            var refresh = _speaker.Warnings[0].Refresh;
            Assert.Equal("PA temperature 61 degrees C. Stay in receive and let the radio cool.", refresh());

            Deliver(62.4f);   // in the reminder interval: no new event, but the value moved
            Assert.Equal("PA temperature 62.4 degrees C. Stay in receive and let the radio cool.", refresh());

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
        public void A_newer_warning_for_the_same_alarm_inside_the_lead_replaces_the_waiting_one()
        {
            Up();
            Deliver(61f);
            _speechClock.Advance(300);
            Deliver(63.2f);   // a worsening: 2.2 over the announced 61
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            var w = Assert.Single(_speaker.Warnings);
            Assert.Contains("63.2", w.Text);
            Assert.Equal(2, _sounds.Count);
        }

        [Fact]
        public void Data_lost_while_transmitting_is_a_warning_and_in_receive_a_queued_critical_line()
        {
            var s = Up(withSound: false);
            Deliver(30f);
            _feed.Key(true);
            for (int i = 0; i < 24; i++) { _clock.Advance(250); s.Tick(_clock.NowMs); }
            Assert.True(s.DrainDispatch(2000));
            var w = Assert.Single(_speaker.Warnings);
            // Stale is declared on the first tick past the five-second allowance: 5.25 s, spoken as 5.
            Assert.Equal("High PA temperature: no PATEMP (PA Temperature) reading for 5 seconds. It cannot be watched. Stop the transmission.", w.Text);

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
            Assert.StartsWith("Test warning from the alarm High PA temperature. Nothing is wrong. A real warning would say: PA temperature 60 degrees C.", w.Text);
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
        public void A_worsening_after_acknowledgement_is_delivered_as_a_new_warning()
        {
            var s = Up(withSound: false);
            Deliver(61f);
            s.Acknowledge("pa");
            Deliver(63.5f);
            Assert.Equal(2, _speaker.Warnings.Count);
            Assert.Contains("63.5", _speaker.Warnings[1].Text);
        }
    }
}
