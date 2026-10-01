#nullable enable
using System;

namespace Radios.Alarms
{
    /// <summary>
    /// The alarm service's view of time: a monotonic millisecond count for
    /// every freshness and interval decision, and UTC for the record. The
    /// two are never mixed — a suspend, a time-zone change or an NTP step
    /// moves UTC and leaves the monotonic count alone.
    /// </summary>
    public interface IAlarmClock
    {
        /// <summary>Milliseconds on a clock that only goes forward.</summary>
        long NowMs { get; }

        /// <summary>Wall time, for journal records and sentences about when.</summary>
        DateTime UtcNow { get; }
    }

    /// <summary>Production: the OS uptime counter and the system clock.</summary>
    public sealed class SystemAlarmClock : IAlarmClock
    {
        public long NowMs => Environment.TickCount64;
        public DateTime UtcNow => DateTime.UtcNow;
    }

    /// <summary>A clock a test moves by hand.</summary>
    public sealed class ManualAlarmClock : IAlarmClock
    {
        public long NowMs { get; set; }
        public DateTime UtcNow { get; set; } = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);

        public void Advance(long ms)
        {
            NowMs += ms;
            UtcNow = UtcNow.AddMilliseconds(ms);
        }
    }
}
