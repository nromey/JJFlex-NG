#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// What the radio has said about its mic input on each command connection,
    /// and which of it was this app's own writes coming back (round six; the
    /// state table is in the junk box 2026-11-02 rulings report, "Round six").
    /// One owner for the other-station choice count and the expected echoes,
    /// which rounds five and six found spread across three files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, per property (8600 over LAN, raw client, 2026-11-06):</b> a
    /// <c>mic input</c> write is echoed to its writer as
    /// <c>transmit ... mic_selection=</c>, before the reply
    /// (micechoprobe.log). A refused one, <c>mic input NOSUCH</c>, is answered
    /// SUCCESS and echoes the UNCHANGED input (micrefusedprobe.log). So a reply
    /// proves nothing, and a report is ours only if its VALUE is the value we
    /// wrote.
    /// </para>
    /// <para>
    /// <b>The rules.</b> Each non-PC mic write of ours registers before it is
    /// sent; its own outcome confirms or kills that exact registration. Any mic
    /// report retires the oldest live registration on its connection: the same
    /// value is our echo and is not counted; another value means the radio
    /// answered our write with something else, and is counted as a choice if it
    /// is not PC. A report taken as an echo whose write then turns out not to
    /// have gone out is counted after all. A registration lapses after
    /// <see cref="OwnEchoMs"/>. PC is never counted, so a PC write registers
    /// nothing.
    /// </para>
    /// </remarks>
    internal static class MicReports
    {
        /// <summary>How long one of our writes waits for its echo.</summary>
        internal const int OwnEchoMs = 5000;

        /// <summary>The registrations' clock. Tests may replace it.</summary>
        internal static Func<long> Clock = () => Environment.TickCount64;

        /// <summary>One connection's mic facts. Its lock guards both.</summary>
        internal sealed class OnConnection
        {
            public long Choices;
            public readonly List<OwnWrite> Expected = new();
        }

        private static readonly ConditionalWeakTable<CommandConnection, OnConnection> Connections = new();

        /// <summary>
        /// One mic write of ours, waiting for its echo. Hand its outcome to
        /// <see cref="Outcome"/> once the write has gone out or not.
        /// </summary>
        internal sealed class OwnWrite
        {
            private readonly OnConnection _on;
            internal OwnWrite(OnConnection on, string value, long until) { _on = on; Value = value; Until = until; }
            internal string Value { get; }
            internal long Until { get; }
            /// <summary>A report was taken as this write's echo.</summary>
            internal bool Taken;
            /// <summary>The write is known not to have gone out.</summary>
            internal bool Dead;

            /// <summary>
            /// The write went out (<paramref name="sent"/>) or did not. Not sent:
            /// this registration is killed, by identity, and a report already
            /// taken as its echo is counted as the choice it was.
            /// </summary>
            internal void Outcome(bool sent)
            {
                if (sent) return;
                lock (_on)
                {
                    if (Dead) return;
                    Dead = true;
                    _on.Expected.Remove(this);
                    if (Taken) _on.Choices++;
                }
            }
        }

        private static OnConnection On(CommandConnection connection) => Connections.GetValue(connection, _ => new OnConnection());

        /// <summary>
        /// How many mic inputs other than PC the radio has reported on
        /// <paramref name="connection"/> that were not this app's own writes
        /// coming back (repair round 8; rounds five and six). A choice made since
        /// a mic debt was decided settles that debt.
        /// </summary>
        internal static long ChoicesReported(CommandConnection? connection)
        {
            if (connection == null || !Connections.TryGetValue(connection, out var on)) return 0;
            lock (on) return on.Choices;
        }

        /// <summary>How many of our writes are waiting for their echo on <paramref name="connection"/>. For tests.</summary>
        internal static int Expecting(CommandConnection? connection)
        {
            if (connection == null || !Connections.TryGetValue(connection, out var on)) return 0;
            lock (on) return on.Expected.Count;
        }

        /// <summary>
        /// This app is about to write <c>mic input <paramref name="value"/></c> on
        /// <paramref name="connection"/>. Null for PC, which registers nothing, or
        /// for no connection. Give the result the write's outcome.
        /// </summary>
        internal static OwnWrite? Expect(CommandConnection? connection, string? value)
        {
            if (connection == null || string.IsNullOrEmpty(value) || string.Equals(value, "PC", StringComparison.OrdinalIgnoreCase)) return null;
            var on = On(connection);
            var write = new OwnWrite(on, value!.ToUpperInvariant(), Clock() + OwnEchoMs);
            lock (on) on.Expected.Add(write);
            return write;
        }

        /// <summary>
        /// The radio reported its mic input as <paramref name="value"/> on
        /// <paramref name="connection"/>. On the reader, under its dispatch gate;
        /// takes only this connection's own small lock.
        /// </summary>
        internal static void Reported(CommandConnection connection, string value)
        {
            var on = On(connection);
            string v = value.ToUpperInvariant();
            bool pc = v == "PC";
            long now = Clock();
            lock (on)
            {
                on.Expected.RemoveAll(e => e.Until <= now);
                if (on.Expected.Count > 0)
                {
                    var oldest = on.Expected[0];
                    on.Expected.RemoveAt(0);
                    if (oldest.Value == v)
                    {
                        oldest.Taken = true;   // our echo: not a choice, unless the write turns out not to have gone
                        return;
                    }
                    // The radio answered our write with another value (refused, or
                    // another station got in first): the registration is retired,
                    // and this report is what the radio holds.
                }
                if (!pc) on.Choices++;
            }
        }
    }
}
