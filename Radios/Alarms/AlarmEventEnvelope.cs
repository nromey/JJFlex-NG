#nullable enable
using System;
using System.Collections.Generic;

namespace Radios.Alarms
{
    /// <summary>What an envelope carries. The set design section 7 names, and nothing spoken.</summary>
    public enum AlarmEnvelopeType
    {
        SessionMetadata,
        MeterDescriptor,
        Observation,
        AlarmDefinitionRevision,
        AlarmTransition,
        DeliveryOutcome,
        OperatorAcknowledgement,
        CaptureGap,
        SessionEnd,
    }

    /// <summary>Which clock a monotonic offset belongs to. Two sessions' counters are never on one axis.</summary>
    public sealed record ClockDomain(string SessionId, string Source)
    {
        public static ClockDomain ForSession(string sessionId) =>
            new ClockDomain(sessionId, "Environment.TickCount64 on the host that received the callbacks");
    }

    /// <summary>Where an envelope came from: the live engine, a replay, or a preview. Replay is explicitly historical.</summary>
    public enum AlarmEnvelopeOrigin { Live, Replay, Preview }

    /// <summary>
    /// The versioned, immutable envelope every observation and alarm event
    /// can travel in (design section 7): the seam through which a future,
    /// consented analysis subscriber would read evidence. Types only. No
    /// forwarder, no endpoint, no consent surface, no model call exists
    /// behind it, and arming an alarm consents to the local journal alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Engineering units stay typed and culture-independent</b>; localized
    /// speech is optional context, never the protocol. Events refer to
    /// definitions and episodes by id rather than by a rendered sentence, so
    /// a threshold change after the fact does not change what the record
    /// meant. Unknown values are explicit nulls, never zero.
    /// </para>
    /// <para>
    /// <b>The radio is a pseudonym here.</b> The serial-to-pseudonym mapping
    /// stays local unless a future consent decision includes it. Callsigns,
    /// network addresses, credentials, profile data, audio and unrelated
    /// speech are not in the payload by construction.
    /// </para>
    /// <para>
    /// <b>A subscriber has no authority.</b> Whatever reads these may offer
    /// hypotheses; it cannot key, stop, reconnect, power-cycle, change a
    /// profile, raise a threshold, acknowledge an alarm or suppress a
    /// warning. A remote acknowledgement, if one ever exists, confirms
    /// transport receipt and nothing about the operator.
    /// </para>
    /// </remarks>
    public sealed record AlarmEventEnvelope(
        int SchemaVersion,
        AlarmEnvelopeType Type,
        string EventId,
        long SessionSequence,
        string SessionId,
        int ConnectionGeneration,
        string RadioPseudonym,
        DateTime ReceiptUtc,
        DateTime EvaluationUtc,
        long? ReceiptMonotonicMs,
        long? EvaluationMonotonicMs,
        ClockDomain Clock,
        AlarmEnvelopeOrigin Origin,
        AlarmEnvelopePayload Payload)
    {
        public const int CurrentSchemaVersion = 1;

        /// <summary>A live envelope for one observation.</summary>
        public static AlarmEventEnvelope ForObservation(string sessionId, long sessionSequence, string radioPseudonym,
            in MeterObservation o, DateTime evaluationUtc, long evaluationMonotonicMs)
            => new AlarmEventEnvelope(CurrentSchemaVersion, AlarmEnvelopeType.Observation, Guid.NewGuid().ToString("N"),
                sessionSequence, sessionId, o.ConnectionGeneration, radioPseudonym, o.ReceiptUtc, evaluationUtc,
                o.ReceiptMonotonicMs, evaluationMonotonicMs, ClockDomain.ForSession(sessionId),
                o.Provenance == ObservationProvenance.Replay ? AlarmEnvelopeOrigin.Replay
                    : o.Provenance == ObservationProvenance.Preview ? AlarmEnvelopeOrigin.Preview : AlarmEnvelopeOrigin.Live,
                new ObservationPayload(MeterDescriptorPayload.From(o.Meter), o.Value, o.Validity, o.OutOfRange, o.Sequence,
                    SensorTimestampUtc: null, SensorTimestampBasis: MeterObservation.SensorTimestampBasis));

        /// <summary>A live envelope for one alarm event, referring to its observation by sequence.</summary>
        public static AlarmEventEnvelope ForAlarmEvent(string sessionId, long sessionSequence, string radioPseudonym,
            AlarmEvent e, DateTime evaluationUtc)
        {
            AlarmEnvelopeType type = e.Kind switch
            {
                AlarmEventKind.Acknowledged or AlarmEventKind.Snoozed or AlarmEventKind.Resumed or AlarmEventKind.SnoozeExpired
                    => AlarmEnvelopeType.OperatorAcknowledgement,
                AlarmEventKind.ConfigurationChanged or AlarmEventKind.Enabled or AlarmEventKind.Disabled
                    => AlarmEnvelopeType.AlarmDefinitionRevision,
                _ => AlarmEnvelopeType.AlarmTransition,
            };
            AlarmEnvelopeOrigin origin = e.Observation?.Provenance switch
            {
                ObservationProvenance.Replay => AlarmEnvelopeOrigin.Replay,
                ObservationProvenance.Preview => AlarmEnvelopeOrigin.Preview,
                _ => e.Detail == "preview" ? AlarmEnvelopeOrigin.Preview : AlarmEnvelopeOrigin.Live,
            };
            return new AlarmEventEnvelope(CurrentSchemaVersion, type, Guid.NewGuid().ToString("N"), sessionSequence, sessionId,
                e.Observation?.ConnectionGeneration ?? 0, radioPseudonym,
                e.Observation?.ReceiptUtc ?? evaluationUtc, evaluationUtc,
                e.Observation?.ReceiptMonotonicMs, e.AtMs, ClockDomain.ForSession(sessionId), origin,
                new AlarmTransitionPayload(e.Kind, e.Definition.Id, e.Definition.Revision, e.EpisodeId,
                    e.Definition.Condition, e.Definition.Direction, e.Definition.Action,
                    e.Observation?.Sequence,
                    float.IsNaN(e.Value) ? null : e.Value,
                    e.Definition.Selector.Units.ToString(),
                    e.Threshold, e.Definition.Hysteresis, e.PersistenceSamples,
                    e.Definition.FreshnessAllowanceSeconds,
                    double.IsNaN(e.AgeSeconds) ? null : e.AgeSeconds,
                    float.IsNaN(e.Change) ? null : e.Change,
                    float.IsNaN(e.Baseline) ? null : e.Baseline,
                    double.IsNaN(e.IntervalSeconds) ? null : e.IntervalSeconds,
                    e.ReminderReason, e.Transmitting, e.WasActive));
        }
    }

    /// <summary>The typed payloads. A closed set, one record per envelope type.</summary>
    public abstract record AlarmEnvelopePayload;

    public sealed record MeterDescriptorPayload(string Name, string Source, int SourceIndex, int SessionIndex,
        string Units, double AdvertisedLow, double AdvertisedHigh, string Description) : AlarmEnvelopePayload
    {
        public static MeterDescriptorPayload From(MeterDescriptor d) =>
            new MeterDescriptorPayload(d.Name, d.Source, d.SourceIndex, d.Index, d.Units.ToString(), d.Low, d.High, d.Description);
    }

    public sealed record ObservationPayload(MeterDescriptorPayload Meter, float Value, ObservationValidity Validity,
        bool OutOfRange, long CallbackSequence, DateTime? SensorTimestampUtc, string SensorTimestampBasis) : AlarmEnvelopePayload;

    public sealed record AlarmTransitionPayload(AlarmEventKind Kind, string AlarmId, int DefinitionRevision, string EpisodeId,
        AlarmCondition Condition, AlarmDirection Direction, AlarmActionClass Action, long? ObservationSequence,
        float? Value, string Units, double Threshold, double Hysteresis, int PersistenceSamples,
        double FreshnessAllowanceSeconds, double? AgeSeconds, float? Change, float? Baseline, double? IntervalSeconds,
        AlarmReminderReason ReminderReason, bool Transmitting, bool WasActive) : AlarmEnvelopePayload;

    public sealed record SessionMetadataPayload(string RadioModel, string Firmware, string Build, DateTime StartedUtc,
        string ConnectionRoute) : AlarmEnvelopePayload;

    public sealed record DeliveryOutcomePayload(string AlarmId, string EpisodeId, bool SoundRequested, bool SpeechRequested,
        string SpeechOutcome, string Backend) : AlarmEnvelopePayload;

    public sealed record CaptureGapPayload(long Dropped, long? FirstSequence, long? LastSequence, bool Exact) : AlarmEnvelopePayload;

    public sealed record SessionEndPayload(string Why, long LastSequence) : AlarmEnvelopePayload;

    /// <summary>
    /// Reads immutable envelopes asynchronously. Its failure, slowness or
    /// absence never delays a local alarm: the engine does not wait on it,
    /// and a subscriber that throws is dropped from the list, not retried
    /// into the meter path.
    /// </summary>
    public interface IAlarmEventSubscriber
    {
        /// <summary>One envelope. Must return promptly; queue internally if consumption is slow.</summary>
        void OnEnvelope(AlarmEventEnvelope envelope);

        /// <summary>Called when envelopes were produced faster than this subscriber took them, with the missing sequence range.</summary>
        void OnGap(long firstSessionSequence, long lastSessionSequence);
    }

    /// <summary>Reads nothing. The only subscriber that exists today.</summary>
    public sealed class NullAlarmEventSubscriber : IAlarmEventSubscriber
    {
        public static readonly NullAlarmEventSubscriber Instance = new NullAlarmEventSubscriber();
        public void OnEnvelope(AlarmEventEnvelope envelope) { }
        public void OnGap(long firstSessionSequence, long lastSessionSequence) { }
    }
}
