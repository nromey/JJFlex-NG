using System;
using System.Linq;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>The event seam, as types only: what an envelope carries, and that nothing behind it exists.</summary>
    public sealed class AlarmEventEnvelopeTests
    {
        [Fact]
        public void An_observation_envelope_carries_typed_units_the_sequence_and_an_explicit_null_sensor_time()
        {
            var o = MeterObservation.Measured(PaTemperatureReplayFixture.Meter, 25.703125f, 7, 56451,
                new DateTime(2026, 8, 23, 1, 36, 42, DateTimeKind.Utc), 3, null);
            var env = AlarmEventEnvelope.ForObservation("session-1", 42, "radio-A", o,
                new DateTime(2026, 8, 23, 1, 36, 42, 5, DateTimeKind.Utc), 56455);

            Assert.Equal(AlarmEventEnvelope.CurrentSchemaVersion, env.SchemaVersion);
            Assert.Equal(AlarmEnvelopeType.Observation, env.Type);
            Assert.Equal(42, env.SessionSequence);
            Assert.Equal(3, env.ConnectionGeneration);
            Assert.Equal("radio-A", env.RadioPseudonym);
            Assert.Equal(AlarmEnvelopeOrigin.Live, env.Origin);
            Assert.Equal("session-1", env.Clock.SessionId);
            var p = Assert.IsType<ObservationPayload>(env.Payload);
            Assert.Equal("DegreesC", p.Meter.Units);
            Assert.Equal(11, p.Meter.SessionIndex);
            Assert.Equal(7, p.CallbackSequence);
            Assert.Null(p.SensorTimestampUtc);
            Assert.Contains("not supplied", p.SensorTimestampBasis);
            Assert.NotEqual("", env.EventId);
        }

        [Fact]
        public void A_replayed_observation_is_marked_historical_and_a_preview_is_marked_preview()
        {
            var replay = MeterObservation.Replayed(PaTemperatureReplayFixture.Meter, 30f, 1, 1000, DateTime.UtcNow, 1, null);
            Assert.Equal(AlarmEnvelopeOrigin.Replay, AlarmEventEnvelope.ForObservation("s", 1, "r", replay, DateTime.UtcNow, 1000).Origin);
            var preview = MeterObservation.PreviewOf(PaTemperatureReplayFixture.Meter, 60f, 1000, DateTime.UtcNow, 1);
            Assert.Equal(AlarmEnvelopeOrigin.Preview, AlarmEventEnvelope.ForObservation("s", 1, "r", preview, DateTime.UtcNow, 1000).Origin);
        }

        [Fact]
        public void An_alarm_event_envelope_refers_to_its_definition_and_episode_by_id_and_to_its_observation_by_sequence()
        {
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, PaTemperatureReplayFixture.Meter, "1234", "pa") with { Revision = 2 };
            var o = MeterObservation.Measured(PaTemperatureReplayFixture.Meter, 61f, 9, 5000, DateTime.UtcNow, 1, null);
            var e = new AlarmEvent { Kind = AlarmEventKind.Fired, Definition = def, EpisodeId = "ep", AtMs = 5001, Observation = o, Value = 61f, Threshold = 60, PersistenceSamples = 1 };
            var env = AlarmEventEnvelope.ForAlarmEvent("s", 2, "r", e, DateTime.UtcNow);

            Assert.Equal(AlarmEnvelopeType.AlarmTransition, env.Type);
            var p = Assert.IsType<AlarmTransitionPayload>(env.Payload);
            Assert.Equal("pa", p.AlarmId);
            Assert.Equal(2, p.DefinitionRevision);
            Assert.Equal("ep", p.EpisodeId);
            Assert.Equal(9, p.ObservationSequence);
            Assert.Equal(61f, p.Value);
            Assert.Equal(60, p.Threshold);
            Assert.Null(p.Change);   // unknown is null, never zero
            Assert.Equal(AlarmEnvelopeOrigin.Live, env.Origin);

            var ack = AlarmEventEnvelope.ForAlarmEvent("s", 3, "r", e with { Kind = AlarmEventKind.Acknowledged, Observation = null }, DateTime.UtcNow);
            Assert.Equal(AlarmEnvelopeType.OperatorAcknowledgement, ack.Type);
            var revised = AlarmEventEnvelope.ForAlarmEvent("s", 4, "r", e with { Kind = AlarmEventKind.ConfigurationChanged, Observation = null }, DateTime.UtcNow);
            Assert.Equal(AlarmEnvelopeType.AlarmDefinitionRevision, revised.Type);
        }

        [Fact]
        public void Nothing_behind_the_seam_exists_but_the_null_subscriber()
        {
            var subscribers = typeof(IAlarmEventSubscriber).Assembly.GetTypes()
                .Where(t => typeof(IAlarmEventSubscriber).IsAssignableFrom(t) && !t.IsInterface)
                .Select(t => t.Name).ToList();
            Assert.Equal(new[] { nameof(NullAlarmEventSubscriber) }, subscribers);

            var forwarders = typeof(IAlarmEventSubscriber).Assembly.GetTypes()
                .Where(t => t.Namespace == "Radios.Alarms" && (t.Name.Contains("Forwarder") || t.Name.Contains("Endpoint") || t.Name.Contains("Consent")))
                .ToList();
            Assert.Empty(forwarders);
        }
    }
}
