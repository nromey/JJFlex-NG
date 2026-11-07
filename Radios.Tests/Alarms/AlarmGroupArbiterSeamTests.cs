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
    /// <summary>
    /// #593, repair round one: a coalesced group must survive the speech
    /// arbiter. Codex's reproduction, across the delivery-to-arbiter seam with
    /// a REAL <see cref="SpeechArbiter"/>: a cut holds the turn; the shipped
    /// low-voltage and PA-temperature presets fire together and their joined
    /// warning waits behind the cut; the voltage worsens and its own warning
    /// reaches the arbiter while the group still waits. The arbiter keeps one
    /// pending sentence per subject, so a group sent under the voltage alarm's
    /// subject was replaced and the PA warning was never said.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class AlarmGroupArbiterSeamTests : IDisposable
    {
        private const string Serial = "1234-5678-9012-3456";
        private static readonly MeterDescriptor Pa = PaTemperatureReplayFixture.Meter;
        private static readonly MeterDescriptor Supply =
            new MeterDescriptor(2, "+13.8A", "+13.8V at PA", "RAD", 2, MeterUnits.Volts, 10.5, 15);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-alarmseam-" + Guid.NewGuid().ToString("N"));
        private readonly FakeAlarmFeed _feed = new();
        private readonly ManualAlarmClock _clock = new() { NowMs = 10_000 };
        private readonly FakeSpeechClock _speechClock = new();
        private readonly List<string> _spoken = new();
        private readonly List<string> _sounds = new();
        private SpeechArbiter _arbiter = null!;
        private AlarmService? _service;
        private AlarmDelivery? _delivery;

        /// <summary>The production route, ScreenReaderAlarmSpeaker to
        /// SpeechArbiter.UrgentAlarm, on an arbiter this test owns.</summary>
        private sealed class ArbiterSpeaker : IAlarmSpeaker
        {
            private readonly Func<SpeechArbiter> _arbiter;
            public ArbiterSpeaker(Func<SpeechArbiter> arbiter) { _arbiter = arbiter; }
            public void SpeakWarning(string text, string subject, Func<string?> refresh, Action? notDelivered = null, Action? silenced = null)
                => _arbiter().UrgentAlarm(text, VerbosityLevel.Critical, "alarm", subject, refresh, notDelivered, silenced);
            public void SpeakStatus(string text, VerbosityLevel level, string subject) { }
        }

        public void Dispose()
        {
            _delivery?.Dispose();
            _service?.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private void Up()
        {
            _arbiter = new SpeechArbiter(_speechClock, () => VerbosityLevel.Chatty,
                (message, interrupt, intent, level, origin, salvaged) => { _spoken.Add(message); return SpeechHandoff.Untracked; },
                () => { }, (message, level, intent, origin) => { });
            _service = new AlarmService(_feed, new AlarmDefinitionStore(_root), _clock, null, startWatchdog: false);
            _delivery = new AlarmDelivery(_service, new ArbiterSpeaker(() => _arbiter), () => _sounds.Add("tone"), _speechClock,
                () => true, () => false, () => true, () => 0L);
            _feed.Connect(Serial, Pa, Supply);
            Assert.True(_service.Add(AlarmPresets.Build(AlarmPresets.VoltageLow, Supply, Serial, "lo") with { Enabled = true }));
            Assert.True(_service.Add(AlarmPresets.Build(AlarmPresets.PaTemperature, Pa, Serial, "pa") with { Enabled = true }));
        }

        private void Deliver(params (MeterDescriptor Meter, float Value)[] readings)
        {
            _clock.Advance(2000);
            foreach (var (meter, value) in readings) _feed.Deliver(meter, value);
            Assert.True(_service!.DrainDispatch(2000));
        }

        private void HoldTheTurnWithACut()
            => _arbiter.Urgent("Reflected power cut the transmission. Twelve watts came back. You are no longer on the air. "
                + "Check the antenna before you transmit again, and listen for the radio's own warning.", VerbosityLevel.Critical, "cut");

        [Fact]
        public void A_worsening_first_member_does_not_erase_the_others_while_the_group_waits_behind_a_cut()
        {
            Up();
            HoldTheTurnWithACut();
            Deliver((Supply, 11.90f), (Pa, 63.5f));               // both fire on one reading
            Assert.Equal(new[] { "tone" }, _sounds);               // one tone: they joined
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);        // the group is handed over and waits
            Assert.Equal(1, _arbiter.AlarmPendingCount);

            Deliver((Supply, 11.60f));                             // the voltage worsens past its step
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);        // its own warning reaches the arbiter

            _speechClock.Advance(20_000);                          // the cut ends; the waiting alarms speak
            string said = string.Join(" | ", _spoken.Skip(1));
            Assert.Contains("PA temperature", said, StringComparison.Ordinal);     // the member that used to vanish
            Assert.Contains("11.6", said, StringComparison.Ordinal);               // the worsened voltage
            Assert.Equal(1, _spoken.Skip(1).Count(s => s.Contains("PA temperature", StringComparison.Ordinal)));
            // And the voltage is said once: the group leaves out a member
            // that has since been handed over on its own.
            Assert.Equal(1, _spoken.Skip(1).Count(s => s.Contains("11.6", StringComparison.Ordinal)));
            Assert.Equal(0, _arbiter.AlarmPendingCount);
        }

        [Fact]
        public void Without_the_worsening_the_group_speaks_both_members_in_one_utterance()
        {
            // Positive control for the instrument: the same readings and
            // clocks with no newer submission, and the combined sentence
            // reaches the sink naming both distinct conditions.
            Up();
            HoldTheTurnWithACut();
            Deliver((Supply, 11.90f), (Pa, 63.5f));
            _speechClock.Advance(AlarmDelivery.ToneLeadMs);
            _speechClock.Advance(20_000);
            string combined = Assert.Single(_spoken.Skip(1));
            Assert.StartsWith("2 alarms at once. ", combined, StringComparison.Ordinal);
            Assert.Contains("PA temperature", combined, StringComparison.Ordinal);
            Assert.Contains("11.9", combined, StringComparison.Ordinal);
        }
    }
}
