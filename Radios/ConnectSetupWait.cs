using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Radios
{
    /// <summary>
    /// The two waits at the end of <see cref="FlexBase.Start"/>, side by side
    /// (#326): the radio's antenna list (20 s) and the radio taking our station
    /// name (45 s). Pulled out of Start() so the order of events can be driven
    /// on a controlled clock, which is the only way to test it: a source test
    /// can say the right words and still decide wrongly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bounds are the old ones, unchanged.</b> The entry's standing rule
    /// is not to just move a timeout: 20 s for the antenna list, 45 s for the
    /// station name, 15 s of grace after a removal that followed an add, and
    /// 1 s after a removal with no add before it.
    /// </para>
    /// <para>
    /// <b>Deadlines, not counts of sleeps</b> (#293): a loop that counted 25 ms
    /// sleeps let the declared 45 s run to about 56 s of wall clock, because
    /// every turn's work came on top. Each bound here is a deadline read from
    /// the clock.
    /// </para>
    /// <para>
    /// <b>A station verdict waits for the antenna list (repair one).</b> Run
    /// one after the other, the station-name wait began only once the
    /// antennas were in, and its removal and add flags were reset then, so a
    /// SmartLink removal and correctly named re-add that happened while the
    /// antenna list was on its way were simply never seen. Side by side, the
    /// first cut saw the removal, gave the station up a second later, then
    /// watched only the antenna list, and when the antennas came, a client
    /// that was back and correctly named was disconnected as a "station name
    /// timeout" (Codex's check, F1: removed at 800 ms, re-added at 4,000 ms,
    /// antennas at 5,000 ms). Now giving the station up (the early abort or
    /// the grace abort) takes effect only once the antenna list is in, since
    /// the wait has to go on for it anyway; until then the station is still
    /// watched, so a re-add clears the removal and the name can still finish.
    /// A re-add that never comes is given up the moment the antennas arrive,
    /// so the protection against a failed re-add stands, and is no slower than
    /// the first cut.
    /// </para>
    /// </remarks>
    internal static class ConnectSetupWait
    {
        public const int AntennaWaitMs = 20000;
        public const int StationWaitMs = 45000;      // SmartLink re-add can take 30 s+ over WAN
        public const int RemovalGraceMs = 15000;     // a 6300 over WAN needs 10 s+
        public const int EarlyAbortMs = 1000;        // removed without ever being added: abort fast for retry
        public const int IntervalMs = 25;

        /// <summary>How the shared wait ended.</summary>
        public enum End
        {
            /// <summary>Both in: the antenna list and our station name.</summary>
            Ready,
            Cancelled,
            ConnectionLost,
            /// <summary>The antenna list's own 20 s bound passed.</summary>
            AntennasExpired,
            /// <summary>Removed with no add before it, and the re-add never came.</summary>
            EarlyAbort,
            /// <summary>Removed after an add, and not back within the grace.</summary>
            GraceAbort,
            /// <summary>The station name's 45 s bound passed.</summary>
            StationTimeout,
        }

        /// <summary>What the wait reads and does, so a test can stand in for the radio and the clock.</summary>
        public sealed class World
        {
            public Func<long> Now = () => Environment.TickCount64;
            public Action<int> Sleep = ms => System.Threading.Thread.Sleep(ms);
            public Func<bool> Cancelled = () => false;
            public Func<bool> Connected = () => true;
            public Func<bool> AntennasHere = () => false;
            /// <summary>Our client was removed during Start, and when (the radio's own report).</summary>
            public Func<bool> ClientRemoved = () => false;
            public Func<long> ClientRemovedAt = () => 0;
            /// <summary>Our client was added (or re-added) during Start.</summary>
            public Func<bool> ClientAdded = () => false;
            /// <summary>One look at the station name; true when it is ours. Records its own source.</summary>
            public Func<bool> StationNamed = () => false;
            public Action<string, Dictionary<string, object>> Record = (_, _) => { };
            public Action<string, TraceLevel> Trace = (_, _) => { };
        }

        /// <summary>The result: how it ended, and what had arrived by then.</summary>
        public readonly struct Outcome
        {
            public Outcome(End end, bool antennasArrived, bool stationNamed, long endedAt)
            {
                Ended = end; AntennasArrived = antennasArrived; StationNamed = stationNamed; EndedAt = endedAt;
            }
            public End Ended { get; }
            public bool AntennasArrived { get; }
            public bool StationNamed { get; }
            public long EndedAt { get; }
        }

        /// <summary>
        /// Run the shared wait to its end. Never throws on its own account.
        /// <paramref name="startedAt"/> is Start's own beginning, for the trace.
        /// </summary>
        public static Outcome Run(World w, long startedAt)
        {
            long begin = w.Now();
            long antennaDeadline = begin + AntennaWaitMs;
            long stationDeadline = begin + StationWaitMs;
            bool antennas = false, named = false;

            w.Record("start_station_name_wait_begin", new Dictionary<string, object>
            {
                { "maxWaitMs", StationWaitMs },
                { "earlyAbortMs", EarlyAbortMs },
                { "removalGraceMs", RemovalGraceMs },
            });

            while (true)
            {
                long now = w.Now();
                if (w.Cancelled())
                {
                    w.Record("start_cancelled_in_station_wait", new Dictionary<string, object>
                    {
                        { "msSinceStartBegin", now - startedAt },
                    });
                    w.Trace("start: cancelled during station-name wait", TraceLevel.Info);
                    return new Outcome(End.Cancelled, antennas, named, now);
                }
                if (!w.Connected())
                {
                    w.Trace("start:connection dropped while waiting for station name", TraceLevel.Error);
                    return new Outcome(End.ConnectionLost, antennas, named, now);
                }

                if (!antennas)
                {
                    if (w.AntennasHere())
                    {
                        antennas = true;
                        w.Record("start_antenna_available", null);
                    }
                    else if (now >= antennaDeadline)
                    {
                        return new Outcome(End.AntennasExpired, antennas, named, now);
                    }
                }

                if (!named)
                {
                    // 45 s from the start of the wait, longer than the antennas'
                    // 20 s, so this is only reached with the antenna list in.
                    if (now >= stationDeadline)
                        return new Outcome(End.StationTimeout, antennas, named, now);

                    // Giving the station up waits for the antenna list: the wait
                    // goes on for it anyway, and until it is in, a re-add may
                    // still come (repair one). Read live each turn: a re-add
                    // clears the removal.
                    bool removed = w.ClientRemoved();
                    bool added = w.ClientAdded();
                    long sinceRemoval = removed ? now - w.ClientRemovedAt() : 0;
                    if (antennas && removed && !added && sinceRemoval > EarlyAbortMs)
                    {
                        w.Record("start_early_abort", new Dictionary<string, object>
                        {
                            { "msSinceRemoval", sinceRemoval },
                            { "msSinceStartBegin", now - startedAt },
                            { "clientAddedDuringStart", added },
                        });
                        w.Trace($"start:client removed without prior add during Start(), aborting after {EarlyAbortMs}ms for retry",
                            TraceLevel.Warning);
                        return new Outcome(End.EarlyAbort, antennas, named, now);
                    }
                    if (antennas && removed && added && sinceRemoval > RemovalGraceMs)
                    {
                        w.Record("start_grace_abort", new Dictionary<string, object>
                        {
                            { "msSinceRemoval", sinceRemoval },
                            { "msSinceStartBegin", now - startedAt },
                        });
                        w.Trace($"start:client removed {RemovalGraceMs}ms ago without re-add, aborting for retry",
                            TraceLevel.Warning);
                        return new Outcome(End.GraceAbort, antennas, named, now);
                    }
                    if (w.StationNamed()) named = true;
                }

                if (named && antennas) return new Outcome(End.Ready, antennas, named, now);
                w.Sleep(IntervalMs);
            }
        }
    }
}
