#nullable enable
using System.Collections.Generic;

namespace Radios.Alarms
{
    /// <summary>
    /// Where the service hands every observation of every recorded meter and
    /// every alarm event, for the journal (design section 6). Called on the
    /// meter thread for observations, so an implementation must only enqueue;
    /// events arrive on the dispatch worker.
    /// </summary>
    public interface IAlarmObservationRecorder
    {
        /// <summary>A measured observation of a meter in the recorded set. Meter thread: enqueue only.</summary>
        void Record(in MeterObservation observation);

        /// <summary>An alarm event, after dispatch. Worker thread.</summary>
        void RecordEvent(AlarmEvent alarmEvent);

        /// <summary>The recorded set changed: these selectors resolved to these descriptors on this connection.</summary>
        void RecordedSetChanged(int connectionGeneration, IReadOnlyList<MeterDescriptor> recorded);

        /// <summary>Connection lifecycle, for the record's own generation bookkeeping.</summary>
        void ConnectionStarted(int connectionGeneration, string radioSerial);
        void ConnectionEnded(int connectionGeneration, string why);
    }

    /// <summary>Records nothing. The service's default when no journal is attached.</summary>
    public sealed class NullAlarmObservationRecorder : IAlarmObservationRecorder
    {
        public static readonly NullAlarmObservationRecorder Instance = new NullAlarmObservationRecorder();
        public void Record(in MeterObservation observation) { }
        public void RecordEvent(AlarmEvent alarmEvent) { }
        public void RecordedSetChanged(int connectionGeneration, IReadOnlyList<MeterDescriptor> recorded) { }
        public void ConnectionStarted(int connectionGeneration, string radioSerial) { }
        public void ConnectionEnded(int connectionGeneration, string why) { }
    }
}
