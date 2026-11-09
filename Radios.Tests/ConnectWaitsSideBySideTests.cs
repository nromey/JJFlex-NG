using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #326, the latency half: the antenna-list wait and the station-name wait
    /// at the end of Start() run side by side, each inside its own unchanged
    /// bound (20 s and 45 s), instead of the station name not even being
    /// watched until the antenna list arrived. The entry's standing rule: do
    /// not just raise the timeout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven on a controlled clock through <see cref="ConnectSetupWait"/>,
    /// the wait Start() runs: each case scripts when the radio removes, adds
    /// and names our client and when the antenna list lands, and reads how the
    /// wait ended and when. Repair one replaced three source-spelling tests,
    /// which Codex showed all pass with the station decision unable to
    /// succeed.
    /// </para>
    /// <para>
    /// Each case names what the code before #326 (the waits one after the
    /// other) and the first cut of #326 did with the same events, from Codex's
    /// controlled-clock comparison of the two (2026-11-09).
    /// </para>
    /// </remarks>
    public sealed class ConnectWaitsSideBySideTests
    {
        /// <summary>A scripted radio and clock. Times are milliseconds from the start of the wait.</summary>
        private sealed class Script
        {
            public long Now;
            public long? AntennasAt, NamedAt, AddedAt, RemovedAt, ReaddedAt, CancelAt, DropAt;
            public readonly List<string> Events = new();

            private bool At(long? t) => t.HasValue && Now >= t.Value;
            private bool Removed => At(RemovedAt) && !(At(ReaddedAt) && ReaddedAt >= RemovedAt);
            private bool Added => At(AddedAt) || (At(ReaddedAt) && At(RemovedAt));

            public ConnectSetupWait.Outcome Run() => ConnectSetupWait.Run(new ConnectSetupWait.World
            {
                Now = () => Now,
                Sleep = ms => Now += ms,
                Cancelled = () => At(CancelAt),
                Connected = () => !At(DropAt),
                AntennasHere = () => At(AntennasAt),
                ClientRemoved = () => Removed,
                ClientRemovedAt = () => RemovedAt ?? 0,
                ClientAdded = () => Added,
                StationNamed = () => !Removed && At(NamedAt),
                Record = (name, _) => Events.Add(name),
                Trace = (_, _) => { },
            }, startedAt: 0);
        }

        private static void Ends(ConnectSetupWait.Outcome o, ConnectSetupWait.End end, long atLeast, long atMost)
        {
            Assert.Equal(end, o.Ended);
            Assert.InRange(o.EndedAt, atLeast, atMost);
        }

        // ── Codex's F1, the blocker repair one fixes ────────────────────────

        /// <summary>
        /// Removed at 800 ms, re-added with our name at 4,000 ms, antennas at
        /// 5,000 ms. Before #326: connected at 5,000 ms. First cut: gave the
        /// station up at about 1,825 ms, watched only the antennas, and at
        /// 5,000 ms disconnected a client that was back and correctly named, as
        /// a "station name timeout".
        /// </summary>
        [Fact]
        public void A_client_removed_and_re_added_before_the_antennas_still_connects()
        {
            var o = new Script { RemovedAt = 800, ReaddedAt = 4000, NamedAt = 4000, AntennasAt = 5000 }.Run();
            Ends(o, ConnectSetupWait.End.Ready, 5000, 5025);
            Assert.True(o.StationNamed);
            Assert.True(o.AntennasArrived);
        }

        /// <summary>
        /// The grace abort's twin: added, removed, and back with our name after
        /// the 15 s grace but before the antennas' 20 s bound. The first cut
        /// gave the station up at 15.5 s.
        /// </summary>
        [Fact]
        public void A_client_back_after_the_grace_but_before_the_antennas_still_connects()
        {
            var o = new Script { AddedAt = 100, RemovedAt = 500, ReaddedAt = 16000, NamedAt = 16000, AntennasAt = 18000 }.Run();
            Ends(o, ConnectSetupWait.End.Ready, 18000, 18025);
        }

        // ── The protection against a re-add that never comes, kept ─────────

        /// <summary>
        /// Removed with no add, never back, antennas at 5,000 ms: given up the
        /// moment the antennas are in, as fast as the first cut, and long before
        /// the 45 s the code before #326 would have spent (it had forgotten the
        /// removal by the time it looked).
        /// </summary>
        [Fact]
        public void A_re_add_that_never_comes_is_given_up_when_the_antennas_arrive()
        {
            var s = new Script { RemovedAt = 800, AntennasAt = 5000 };
            var o = s.Run();
            Ends(o, ConnectSetupWait.End.EarlyAbort, 5000, 5025);
            Assert.False(o.StationNamed);
            Assert.Contains("start_early_abort", s.Events);
        }

        /// <summary>With the antennas already in (a LAN connect), the early abort fires a second after the removal, as it always did.</summary>
        [Fact]
        public void With_the_antennas_in_the_early_abort_fires_a_second_after_the_removal()
        {
            var s = new Script { AntennasAt = 100, RemovedAt = 800 };
            var o = s.Run();
            Ends(o, ConnectSetupWait.End.EarlyAbort, 1800, 1850);
            Assert.Contains("start_early_abort", s.Events);
        }

        [Fact]
        public void A_client_added_then_removed_and_never_back_is_given_up_after_the_grace()
        {
            var s = new Script { AddedAt = 100, RemovedAt = 500, AntennasAt = 1000 };
            var o = s.Run();
            Ends(o, ConnectSetupWait.End.GraceAbort, 15500, 15550);
            Assert.Contains("start_grace_abort", s.Events);
        }

        [Fact]
        public void Added_removed_and_never_back_with_late_antennas_is_given_up_when_they_arrive()
        {
            var o = new Script { AddedAt = 100, RemovedAt = 500, AntennasAt = 18000 }.Run();
            Ends(o, ConnectSetupWait.End.GraceAbort, 18000, 18025);
        }

        // ── The side-by-side gain, and the controls ────────────────────────

        [Fact]
        public void Both_ready_early_connects_at_once()
        {
            Ends(new Script { AntennasAt = 100, NamedAt = 100 }.Run(), ConnectSetupWait.End.Ready, 100, 125);
        }

        [Fact]
        public void The_station_name_is_watched_while_the_antennas_are_on_their_way()
        {
            // The name lands first; the wait ends when the antennas do, not
            // 45 s after them.
            var s = new Script { NamedAt = 300, AntennasAt = 5000 };
            var o = s.Run();
            Ends(o, ConnectSetupWait.End.Ready, 5000, 5025);
            Assert.Equal(0, s.Events.IndexOf("start_station_name_wait_begin"));
        }

        [Fact]
        public void Antennas_first_then_the_name_connects_when_the_name_lands()
        {
            Ends(new Script { AntennasAt = 100, NamedAt = 5000 }.Run(), ConnectSetupWait.End.Ready, 5000, 5025);
        }

        // ── The bounds, unchanged ──────────────────────────────────────────

        [Fact]
        public void No_antenna_list_ends_on_its_own_20_second_bound()
        {
            var o = new Script { NamedAt = 100 }.Run();
            Ends(o, ConnectSetupWait.End.AntennasExpired, 20000, 20025);
            Assert.False(o.AntennasArrived);
        }

        [Fact]
        public void No_station_name_ends_on_its_own_45_second_bound()
        {
            var o = new Script { AntennasAt = 100 }.Run();
            Ends(o, ConnectSetupWait.End.StationTimeout, 45000, 45025);
            Assert.True(o.AntennasArrived);
            Assert.False(o.StationNamed);
        }

        [Fact]
        public void The_bounds_are_the_ones_the_entry_forbids_moving()
        {
            Assert.Equal(20000, ConnectSetupWait.AntennaWaitMs);
            Assert.Equal(45000, ConnectSetupWait.StationWaitMs);
            Assert.Equal(15000, ConnectSetupWait.RemovalGraceMs);
            Assert.Equal(1000, ConnectSetupWait.EarlyAbortMs);
        }

        [Fact]
        public void A_cancel_or_a_drop_ends_the_wait_at_once()
        {
            Ends(new Script { CancelAt = 800, AntennasAt = 5000, NamedAt = 5000 }.Run(), ConnectSetupWait.End.Cancelled, 800, 825);
            var drop = new Script { DropAt = 800, AntennasAt = 5000, NamedAt = 5000 }.Run();
            Ends(drop, ConnectSetupWait.End.ConnectionLost, 800, 825);
            Assert.False(drop.AntennasArrived);   // Start then gives the antenna verdict: connection lost during setup
        }

        /// <summary>
        /// Start() runs this wait, and no wait of its own beside it: without
        /// this the cases above would hold a method nothing calls.
        /// </summary>
        [Fact]
        public void Start_runs_this_wait()
        {
            string flex = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "Radios", "FlexBase.cs"));
            Assert.Contains("ConnectSetupWait.Outcome waited = ConnectSetupWait.Run(", flex, StringComparison.Ordinal);
            Assert.DoesNotContain("stationWaitDeadline", flex, StringComparison.Ordinal);
            Assert.DoesNotContain("antennaDeadline", flex, StringComparison.Ordinal);
        }
    }
}
