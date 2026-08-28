using System;
using System.Diagnostics;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;
using Radios.ChainChecks;

namespace Radios
{
    /// <summary>
    /// FlexBase, receive-traffic side: the one thing in this application that
    /// reads how much the radio is actually sending us.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A separate file rather than a region in FlexBase.cs, deliberately</b>,
    /// for the same reason <c>FlexBase.Amplifiers.cs</c> is one: FlexBase is
    /// seventeen thousand lines with several tracks editing it at once, and
    /// nothing here changes an existing member. The only edit this work makes to
    /// FlexBase.cs itself is a single call in the connect-time handler block.
    /// </para>
    /// <para>
    /// <b>What was wrong.</b> The receive check reported nine facts and every one
    /// of them was a SETTING — mutes, levels, a routing switch, the model and
    /// serial. Not one was a measurement of anything that had happened, so the
    /// whole report could be verifiably correct while no audio had ever reached
    /// the computer. That is the first thing a radio manufacturer's support desk
    /// asks about, and we had no answer to it.
    /// </para>
    /// <para>
    /// <b>The measurement already existed.</b> FlexLib counts every arriving Opus
    /// byte and publishes <c>AvgRXOpuskbps</c>, <c>AvgRXTotalkbps</c> and
    /// <c>AvgMeterkbps</c> once a second. Nothing outside the vendored tree had
    /// ever read them. This file is the join, and nothing more: it takes no
    /// decisions, it changes nothing on the radio, and it exists only so the
    /// receive report can say what arrived instead of only what is set.
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        // ── Receive traffic sampler ──────────────────────────────────────────

        private readonly RxTrafficWindow _rxTraffic = new RxTrafficWindow();
        private readonly object _rxTrafficTimerLock = new object();
        private Timer _rxTrafficTimer;

        /// <summary>Roughly a second, matching the rate FlexLib republishes the
        /// figures at. Sampling faster would not see more, because the underlying
        /// counters only move once a second.</summary>
        private const int RxTrafficSampleMs = 1000;

        /// <summary>
        /// What has actually been arriving from the radio, over the last half
        /// minute or so. Never null.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A reading with no samples is the honest answer immediately after a
        /// connect and after a radio goes away; the fact source turns it into
        /// "could not be read" rather than into a zero, because a fabricated
        /// zero here would accuse a station that is working perfectly.
        /// </para>
        /// <para>
        /// <b>Zero is also the CORRECT answer in the most common setup.</b> The
        /// Opus receive stream only carries sound to this computer when radio
        /// audio through this computer is switched on. An operator listening on
        /// the radio's own speaker or headphone jack has no such stream and no
        /// such traffic, and nothing is wrong. Read this beside
        /// <c>PCAudio</c>, never on its own.
        /// </para>
        /// </remarks>
        public RxTrafficReading RxTraffic
        {
            get
            {
                try { return _rxTraffic.Snapshot(); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("RxTraffic: snapshot failed — " + ex.Message, TraceLevel.Warning);
                    return new RxTrafficWindow().Snapshot();
                }
            }
        }

        /// <summary>
        /// Begin watching what the radio sends. Called once per connect, from the
        /// handler-wiring block; safe to call again, and clears anything the
        /// previous radio left behind so a reconnect can never be described with
        /// the old radio's numbers.
        /// </summary>
        /// <remarks>
        /// There is no matching Stop in the disconnect path on purpose. The tick
        /// checks for the radio itself and shuts the timer down when it has gone,
        /// which means the teardown cannot be missed by a disconnect path that
        /// forgets to call it — and the disconnect paths in this class are many.
        /// </remarks>
        internal void StartRxTrafficWatch()
        {
            lock (_rxTrafficTimerLock)
            {
                StopRxTrafficWatchLocked();
                _rxTraffic.Clear();

                try
                {
                    _rxTrafficTimer = new Timer(RxTrafficTick, null,
                                                RxTrafficSampleMs, RxTrafficSampleMs);
                    Tracing.TraceLine("RxTraffic: watching receive traffic, one reading a second",
                                      TraceLevel.Info);
                }
                catch (Exception ex)
                {
                    // A diagnostic that cannot start must never take the connect
                    // with it. The report then says it has no readings, which is
                    // true.
                    _rxTrafficTimer = null;
                    Tracing.TraceLine("RxTraffic: could not start the sampler — " + ex.Message,
                                      TraceLevel.Warning);
                }
            }
        }

        /// <summary>
        /// Stop watching and forget what was seen. Called by the tick itself when
        /// the radio has gone; available to a caller that wants it immediate.
        /// </summary>
        internal void StopRxTrafficWatch()
        {
            lock (_rxTrafficTimerLock) StopRxTrafficWatchLocked();
            _rxTraffic.Clear();
        }

        private void StopRxTrafficWatchLocked()
        {
            if (_rxTrafficTimer == null) return;
            try { _rxTrafficTimer.Dispose(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("RxTraffic: stopping the sampler threw — " + ex.Message,
                                  TraceLevel.Warning);
            }
            _rxTrafficTimer = null;
        }

        /// <summary>
        /// One reading. Deliberately silent in the trace: at one a second it would
        /// bury everything else in the log, and the readings that matter are the
        /// ones quoted in a report.
        /// </summary>
        private void RxTrafficTick(object state)
        {
            try
            {
                Radio r = theRadio;
                if (r == null)
                {
                    // The radio has gone. Shut down from here rather than relying
                    // on a disconnect path remembering to.
                    StopRxTrafficWatch();
                    return;
                }

                _rxTraffic.Add(r.AvgRXOpuskbps, r.AvgRXTotalkbps, r.AvgMeterkbps, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                // Never allowed to throw on a timer thread: an unhandled
                // exception there takes the process down, and this is a
                // diagnostic.
                try
                {
                    Tracing.TraceLine("RxTraffic: reading the receive rates failed — " + ex.Message,
                                      TraceLevel.Warning);
                }
                catch { }
            }
        }
    }
}
