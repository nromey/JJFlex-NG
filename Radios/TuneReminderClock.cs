#nullable enable

namespace Radios
{
    /// <summary>What a tick of the tune reminder calls for.</summary>
    public enum TuneReminderStep
    {
        /// <summary>Nothing to do.</summary>
        Nothing,

        /// <summary>The carrier has been up the set length: say so.</summary>
        Remind,

        /// <summary>A carrier being watched has gone: anything still unheard about it is out of date.</summary>
        Over,
    }

    /// <summary>
    /// When to tell the operator tune is still on (#860, ruled by Noel 2026-11-01
    /// 07:52). After the set length of a carrier being up, and again at the same
    /// length for as long as it stays up. Pure: it is told what the radio says
    /// and what the time is, and it holds no opinion of its own about the carrier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It only ever speaks. The carrier is never touched</b>: tune stays
    /// unheld, by his ruling of 2026-10-22.
    /// </para>
    /// <para>
    /// <b>It is told the radio's state each tick rather than the door's</b>, so
    /// however tune goes off, the kill switch, the radio's own limit, another
    /// client or a lost radio, the clock ends with it. That is #453's lesson: a
    /// flag latched from the press cannot see those.
    /// </para>
    /// <para>
    /// A carrier the Fix tool or a transmit check raised is exempt for as long
    /// as that check holds it, and if one were left up afterwards the clock
    /// starts then, so a stuck carrier still gets told.
    /// </para>
    /// </remarks>
    public sealed class TuneReminderClock
    {
        private long? _since;

        /// <param name="carrierUp">The radio reports this client's tune carrier up.</param>
        /// <param name="exempt">The carrier belongs to a check.</param>
        /// <param name="nowMs">A monotonic clock, in milliseconds.</param>
        /// <param name="seconds">The set length, read each tick so a change in Settings takes effect on the carrier in hand.</param>
        public TuneReminderStep Tick(bool carrierUp, bool exempt, long nowMs, int seconds)
        {
            if (!carrierUp || exempt)
            {
                bool wasWatching = _since != null;
                _since = null;
                return wasWatching ? TuneReminderStep.Over : TuneReminderStep.Nothing;
            }
            if (_since == null)
            {
                _since = nowMs;
                return TuneReminderStep.Nothing;
            }
            if (nowMs - _since.Value < seconds * 1000L) return TuneReminderStep.Nothing;
            _since = nowMs;
            return TuneReminderStep.Remind;
        }
    }
}
