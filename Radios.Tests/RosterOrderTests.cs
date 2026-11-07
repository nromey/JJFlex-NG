using System.Collections.Generic;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 36 Track J, task #254: the radio list's order must express what
    /// the operator chose and what is true, and nothing else.
    ///
    /// <para><b>The symptom.</b> Noel, 2026-08-26: "when you press enter to
    /// reload SmartLink, whichever one you get reloaded, i.e. Don's, shows up
    /// at the top of the list." The order had come to record which SmartLink
    /// account had been refreshed most recently — internal bookkeeping,
    /// surfacing in the one property a keyboard user navigates by.</para>
    ///
    /// <para><b>The cause was not this rule.</b> It was the flag feeding it:
    /// <c>WanAvailable</c> was set when a SmartLink list mentioned a radio and
    /// never cleared, so it had quietly changed meaning from "is reachable" to
    /// "was reachable once". The clause faithfully expressed a fact that had
    /// stopped being true. These tests pin the rule so that the next person to
    /// meet an ordering complaint fixes the flag rather than deleting a clause
    /// that is load-bearing.</para>
    /// </summary>
    public sealed class RosterOrderTests
    {
        private sealed class Row : IRosterOrderKey
        {
            public string Name { get; init; } = "";
            public bool IsFavorite { get; init; }
            public bool Lan { get; init; }
            public bool Wan { get; init; }
            public bool IsLive => Lan || Wan;
            bool IRosterOrderKey.LanAvailable => Lan;
            public override string ToString() => Name;
        }

        private static string[] Order(params Row[] rows) =>
            RosterOrder.Apply(rows).Select(r => r.Name).ToArray();

        [Fact]
        public void Favourites_come_first_even_when_they_are_offline()
        {
            var order = Order(
                new Row { Name = "live-local", Lan = true },
                new Row { Name = "favourite-offline", IsFavorite = true });

            Assert.Equal(new[] { "favourite-offline", "live-local" }, order);
        }

        [Fact]
        public void A_radio_you_can_dial_beats_one_that_is_only_history()
        {
            var order = Order(
                new Row { Name = "roster-only" },
                new Row { Name = "live" , Lan = true });

            Assert.Equal(new[] { "live", "roster-only" }, order);
        }

        /// <summary>
        /// Noel's ruling of 2026-10-10 (#711): "Local radios should be on top".
        /// This test replaced Remote_capable_sorts_above_local_only, which pinned
        /// the old remote-first order as desired behaviour; that order was written
        /// for a Remote button's list that no longer exists.
        /// </summary>
        [Fact]
        public void Local_sorts_above_remote_only()
        {
            var order = Order(
                new Row { Name = "smartlink-only", Wan = true },
                new Row { Name = "lan-only", Lan = true });

            Assert.Equal(new[] { "lan-only", "smartlink-only" }, order);
        }

        /// <summary>
        /// A radio on the local network that is ALSO reachable over SmartLink is
        /// local: it sits with the local radios, above a SmartLink-only one, and
        /// among the local radios it keeps its arrival order.
        /// </summary>
        [Fact]
        public void A_radio_reachable_both_ways_counts_as_local()
        {
            var order = Order(
                new Row { Name = "smartlink-only", Wan = true },
                new Row { Name = "lan-only", Lan = true },
                new Row { Name = "dual-homed", Lan = true, Wan = true });

            Assert.Equal(new[] { "lan-only", "dual-homed", "smartlink-only" }, order);
        }

        /// <summary>
        /// The stability clause, and the bug it exists for. A LAN radio
        /// re-announces about once a second; if equal rows were free to move,
        /// "the row below mine" would mean a different radio between the moment
        /// a screen reader read it and the moment Enter was pressed.
        /// </summary>
        [Fact]
        public void Equal_rows_never_move_relative_to_each_other()
        {
            var rows = new[]
            {
                new Row { Name = "a", Lan = true },
                new Row { Name = "b", Lan = true },
                new Row { Name = "c", Lan = true },
            };

            Assert.Equal(new[] { "a", "b", "c" }, Order(rows));

            // Re-sorting an already-sorted list is a no-op, so a re-announcement
            // that changes nothing cannot rearrange anything.
            Assert.Equal(new[] { "a", "b", "c" },
                RosterOrder.Apply(RosterOrder.Apply(rows)).Select(r => r.Name).ToArray());
        }

        /// <summary>
        /// THE #254 REGRESSION. Two accounts, one radio each, both genuinely
        /// reachable over SmartLink: the order must be the order they were met
        /// in, whichever account was refreshed last. This is what the fix
        /// delivers — not by changing the sort, but by the flag meaning
        /// "reachable now", which is true of both radios at once. (Rewritten
        /// 2026-11-07: it used a local radio as one of the two, which local-first
        /// now lifts on its own, so it had stopped testing arrival order.)
        /// </summary>
        [Fact]
        public void Two_reachable_accounts_do_not_reorder_each_other()
        {
            var first = new Row { Name = "account-one-radio", Wan = true };
            var second = new Row { Name = "account-two-radio", Wan = true };

            Assert.Equal(new[] { "account-one-radio", "account-two-radio" }, Order(first, second));
            Assert.Equal(new[] { "account-two-radio", "account-one-radio" }, Order(second, first));
        }

        /// <summary>
        /// And the shape of the old defect, stated so it is recognisable. In
        /// #254 it was the SmartLink flag; the sort now reads the local flag, so
        /// the same lie would come from there: a radio whose flag still claims
        /// the local network after it has gone climbs over one that is genuinely
        /// reachable. The sort is doing exactly what it was told; the input was
        /// a lie. (Rewritten 2026-11-07 for the local-first order; it asserted
        /// the stale SmartLink flag winning, which local-first no longer allows.)
        /// </summary>
        [Fact]
        public void A_stale_local_flag_would_promote_an_unreachable_radio()
        {
            var stale = new Row { Name = "stale-lan-flag", Lan = true };
            var real = new Row { Name = "really-reachable", Wan = true };

            Assert.Equal(new[] { "stale-lan-flag", "really-reachable" }, Order(real, stale));

            // With the flag telling the truth, the radio that has gone is a
            // roster row, below the one that can be dialled.
            var honest = new Row { Name = "stale-lan-flag" };
            Assert.Equal(new[] { "really-reachable", "stale-lan-flag" }, Order(real, honest));

            // The old shape still holds one clause up: a stale SmartLink flag
            // makes a row live, so it climbs over a roster row.
            var staleWan = new Row { Name = "stale-wan-flag", Wan = true };
            var roster = new Row { Name = "roster-only" };
            Assert.Equal(new[] { "stale-wan-flag", "roster-only" }, Order(roster, staleWan));
        }

        [Fact]
        public void An_empty_list_orders_without_complaint()
        {
            Assert.Empty(RosterOrder.Apply(new List<Row>()));
            Assert.Empty(RosterOrder.Apply<Row>(null));
        }
    }
}
