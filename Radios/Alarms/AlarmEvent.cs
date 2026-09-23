#nullable enable
using System;

namespace Radios.Alarms
{
    /// <summary>Everything the monitor can conclude. Each one is a named event, never a silent state change.</summary>
    public enum AlarmEventKind
    {
        /// <summary>A new episode: the condition is met. The one that sounds the tone.</summary>
        Fired,

        /// <summary>The current warning again, on a fresh sample after the interval — or for a stated reason.</summary>
        Reminder,

        /// <summary>A further step in the bad direction since the last announced value. Bypasses acknowledgement and snooze.</summary>
        Worsened,

        /// <summary>The clear rule was satisfied. An accessible state update; no tone, no permission to transmit.</summary>
        Cleared,

        /// <summary>A sustained trigger has started counting.</summary>
        Pending,

        /// <summary>The first sample of this connection arrived.</summary>
        FirstSample,

        /// <summary>No sample inside the freshness allowance. The numeric condition is no longer judged.</summary>
        DataStale,

        /// <summary>A sample arrived after staleness or absence.</summary>
        DataResumed,

        /// <summary>The meter is not there — detached, disconnected, removed, or a new connection has not published it yet.</summary>
        DataMissing,

        /// <summary>The selector cannot name one meter on this connection.</summary>
        DataAmbiguous,

        /// <summary>NaN, infinity or the documented sentinel arrived. Not counted, not zero.</summary>
        InvalidSample,

        /// <summary>A finite value outside the advertised range was judged anyway, and flagged.</summary>
        OutOfRangeSample,

        /// <summary>A callback carrying an older connection generation was dropped.</summary>
        OldGenerationDiscarded,

        Acknowledged,
        Snoozed,
        SnoozeExpired,
        Resumed,

        BaselineCaptured,
        BaselineRefused,
        BaselineInvalidated,

        /// <summary>The trend cannot be judged yet: fewer than a full window of observations.</summary>
        TrendWarmingUp,

        /// <summary>The trend cannot be judged: a gap, or too few samples in an endpoint band, or medians too far apart.</summary>
        TrendInsufficientCoverage,

        /// <summary>The trend became judgeable again.</summary>
        TrendCovered,

        Enabled,
        Disabled,

        /// <summary>A saved edit replaced the definition. The old episode closed as configuration changed — never as cleared.</summary>
        ConfigurationChanged,

        /// <summary>The condition left the transmit-only scope's window; pending dwell reset.</summary>
        ScopeLeft,
        ScopeEntered,
    }

    /// <summary>Why a reminder was raised, so the sentence and the record can say.</summary>
    public enum AlarmReminderReason
    {
        None,

        /// <summary>The routine interval elapsed and a fresh sample arrived.</summary>
        Interval,

        /// <summary>A transmit transition while the condition remained active.</summary>
        TransmitResumed,

        /// <summary>The reading was lost and has returned while the condition remains active.</summary>
        DataResumed,

        /// <summary>The operator pressed Resume notifications.</summary>
        OperatorResumed,
    }

    /// <summary>
    /// One conclusion of one monitor at one instant, with everything a
    /// sentence, a journal record or a list row needs — so nothing downstream
    /// re-reads live state to describe a moment that has passed.
    /// </summary>
    public sealed record AlarmEvent
    {
        public AlarmEventKind Kind { get; init; }

        /// <summary>The definition as it stood when this was judged.</summary>
        public AlarmDefinition Definition { get; init; } = new AlarmDefinition();

        /// <summary>Empty when no episode is involved.</summary>
        public string EpisodeId { get; init; } = "";

        /// <summary>Monotonic time of the evaluation.</summary>
        public long AtMs { get; init; }

        /// <summary>The observation that caused it, or null for a watchdog or operator event.</summary>
        public MeterObservation? Observation { get; init; }

        /// <summary>The value judged, or NaN.</summary>
        public float Value { get; init; } = float.NaN;

        /// <summary>The threshold in force.</summary>
        public double Threshold { get; init; }

        /// <summary>The rise or fall measured, for delta and trend events; NaN otherwise.</summary>
        public float Change { get; init; } = float.NaN;

        /// <summary>The baseline in force, for delta events; NaN otherwise.</summary>
        public float Baseline { get; init; } = float.NaN;

        /// <summary>The actual interval between the median observation times, for trend events; NaN otherwise.</summary>
        public double IntervalSeconds { get; init; } = double.NaN;

        /// <summary>Seconds since the last fresh receipt, for data events; NaN otherwise.</summary>
        public double AgeSeconds { get; init; } = double.NaN;

        /// <summary>Whether this client was transmitting at the moment — picks the tx or rx sentence.</summary>
        public bool Transmitting { get; init; }

        /// <summary>For a data-loss event: an episode was active when the data stopped.</summary>
        public bool WasActive { get; init; }

        public AlarmReminderReason ReminderReason { get; init; } = AlarmReminderReason.None;

        /// <summary>Seconds of snooze remaining, for snooze events.</summary>
        public double SnoozeSeconds { get; init; }

        /// <summary>For a firing: how many fresh samples the persistence rule counted.</summary>
        public int PersistenceSamples { get; init; }

        /// <summary>Free text for the trace only. Never spoken.</summary>
        public string Detail { get; init; } = "";

        /// <summary>The kinds that carry a warning to the operator's ears.</summary>
        public bool IsWarning => Kind is AlarmEventKind.Fired or AlarmEventKind.Reminder or AlarmEventKind.Worsened;

        public override string ToString()
        {
            string v = float.IsNaN(Value) ? "" : " value=" + Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string e = EpisodeId.Length == 0 ? "" : " episode=" + EpisodeId;
            return Kind + " " + Definition.Name + e + v + " at " + AtMs + " ms" + (Detail.Length == 0 ? "" : " (" + Detail + ")");
        }
    }
}
