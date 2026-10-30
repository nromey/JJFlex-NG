#nullable enable
using System;

namespace Radios
{
    /// <summary>
    /// The transmit lock's timeout clock: how long the operator has held the
    /// lock BY HAND, which is what the warning ladder and the lock's timeout
    /// measure (#835; Noel, 2026-10-30 00:00).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ruling.</b> A send of a recording riding the lock is not cut at
    /// the lock's timeout (three minutes by default): <i>"we have a ten minute
    /// for longer recordings, I'm thinking amateur radio news line or another
    /// bulletin."</i> The send's own ten-minute limit and its pause govern it
    /// instead. The timeout for an operator who locked transmit by hand is
    /// unchanged.
    /// </para>
    /// <para>
    /// <b>So the clock stops while a recording rides the lock, and starts again
    /// where it stopped when the recording stops riding</b> (sent, stopped,
    /// paused at ten minutes, taken over). The time the recording was going out
    /// is never counted against the operator, and none is given back that the
    /// operator had already used: an operator who locked by hand two minutes
    /// before the bulletin has the same minute left after it. That reading is
    /// this class's, not the ruling's; the alternative, a fresh timeout after
    /// each send, would give an operator who walked away from a locked radio
    /// more dead air than the lock allows.
    /// </para>
    /// <para>
    /// <b>What this never touches:</b> the fifteen-minute hard limit, which is
    /// its own timer from the moment the lock began and runs regardless, so no
    /// transmission under the lock is unbounded.
    /// </para>
    /// <para>Pure: the caller passes the time, so a test can hold every order.</para>
    /// </remarks>
    public sealed class LockTimeoutClock
    {
        private long _startMs;
        private long _rideStartMs;
        private long _riddenMs;
        private bool _running;
        private bool _riding;

        /// <summary>True while a recording rides the lock and the timeout stands down.</summary>
        public bool SendRiding => _riding;

        /// <summary>
        /// The lock began (or a held push to talk became one). A recording
        /// already riding the transmission keeps riding, from now.
        /// </summary>
        public void Start(long nowMs)
        {
            _startMs = nowMs;
            _riddenMs = 0;
            _running = true;
            if (_riding) _rideStartMs = nowMs;
        }

        /// <summary>
        /// The lock ended. A ride is the send's to end, not the lock's: a send
        /// still riding (a hardware line can hold the radio up after an unlock)
        /// keeps riding into a lock begun later.
        /// </summary>
        public void Reset()
        {
            _running = false;
            _riddenMs = 0;
        }

        /// <summary>
        /// A recording's send began riding the operator's transmission. Harmless
        /// when not locked: a lock begun later starts the ride's clock then.
        /// True when this began a ride.
        /// </summary>
        public bool BeginSendRide(long nowMs)
        {
            if (_riding) return false;
            _riding = true;
            _rideStartMs = nowMs;
            return true;
        }

        /// <summary>
        /// The recording stopped riding. The clock carries on from where it
        /// stopped. True when this ended a ride.
        /// </summary>
        public bool EndSendRide(long nowMs)
        {
            if (!_riding) return false;
            _riding = false;
            if (_running) _riddenMs += Math.Max(0, nowMs - _rideStartMs);
            return true;
        }

        /// <summary>
        /// Seconds the operator has held the lock by hand: the lock's age less
        /// every stretch a recording rode it. Zero when not locked.
        /// </summary>
        public double ElapsedSeconds(long nowMs)
        {
            if (!_running) return 0;
            long ridden = _riddenMs + (_riding ? Math.Max(0, nowMs - _rideStartMs) : 0);
            return Math.Max(0, nowMs - _startMs - ridden) / 1000.0;
        }
    }
}
