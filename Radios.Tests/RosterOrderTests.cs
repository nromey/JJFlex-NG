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
            bool IRosterOrderKey.WanAvailable => Wan;
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
        /// Noel's 2026-08-05 rule. Pressing Remote means "show me my remote
        /// radios", so they must not sit below locally discovered ones nobody
        /// asked about.
        /// </summary>
        [Fact]
        public void Remote_capable_sorts_above_local_only()
        {
            var order = Order(
                new Row { Name = "lan-only", Lan = true },
                new Row { Name = "dual-homed", Lan = true, Wan = true });

            Assert.Equal(new[] { "dual-homed", "lan-only" }, order);
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
        /// reachable: the order must be the order they were met in, whichever
        /// account was refreshed last. This is what the fix delivers — not by
        /// changing the sort, but by the flag it reads meaning "reachable now",
        /// which is true of both radios at once.
        /// </summary>
        [Fact]
        public void Two_reachable_accounts_do_not_reorder_each_other()
        {
            var mine = new Row { Name = "my-8600", Lan = true, Wan = true };
            var dons = new Row { Name = "dons-6300", Wan = true };

            Assert.Equal(new[] { "my-8600", "dons-6300" }, Order(mine, dons));
        }

        /// <summary>
        /// And the shape of the old defect, stated so it is recognisable: a
        /// radio whose flag still claims SmartLink after nothing is vouching for
        /// it climbs over one that is genuinely on the local network. The sort
        /// is doing exactly what it was told; the input was a lie.
        /// </summary>
        [Fact]
        public void A_stale_smartlink_flag_would_promote_an_unreachable_radio()
        {
            var stale = new Row { Name = "stale-wan-flag", Wan = true };
            var real = new Row { Name = "really-here", Lan = true };

            Assert.Equal(new[] { "stale-wan-flag", "really-here" }, Order(real, stale));

            // With the flag telling the truth, the order is the one the operator
            // would expect: nothing moved, because nothing changed group.
            var honest = new Row { Name = "stale-wan-flag" };
            Assert.Equal(new[] { "really-here", "stale-wan-flag" }, Order(real, honest));
        }

        [Fact]
        public void An_empty_list_orders_without_complaint()
        {
            Assert.Empty(RosterOrder.Apply(new List<Row>()));
            Assert.Empty(RosterOrder.Apply<Row>(null));
        }
    }
}
