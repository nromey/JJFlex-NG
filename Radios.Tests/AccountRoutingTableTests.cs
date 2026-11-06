#nullable enable

using System;
using System.Collections.Generic;
using Radios;
using Xunit;

using Plan = Radios.FlexBase.SmartLinkSignInPlanKind;

namespace Radios.Tests
{
    /// <summary>
    /// The account-routing state table, every row (#798 and #713, repair
    /// round 2). The plan is a pure function of ONE picture: each saved
    /// account's list state, the radio's chosen (preferred) and last-seen
    /// accounts. No sessions, no clock; each row is the whole input.
    /// </summary>
    /// <remarks>
    /// List-state codes, one per account: <c>-</c> no held session;
    /// <c>C</c> connected, list not arrived on this connection yet; <c>E</c>
    /// arrived and empty of this radio; <c>R</c> arrived and lists this radio;
    /// <c>S</c> stale: the session is not connected, its old list named the
    /// radio.
    /// </remarks>
    public sealed class AccountRoutingTableTests
    {
        private const string A = "a@example.com";
        private const string B = "b@example.com";
        private const string Serial = "1111-2222-6300-4444";

        private static FlexBase.AccountListView? View(string account, char code) => code switch
        {
            '-' => null,
            'C' => new FlexBase.AccountListView(account, "s-" + account, 1, Connected: true, Arrived: false, null, ListsSerial: false),
            'E' => new FlexBase.AccountListView(account, "s-" + account, 1, Connected: true, Arrived: true, new DateTime(2026, 11, 6), ListsSerial: false),
            'R' => new FlexBase.AccountListView(account, "s-" + account, 1, Connected: true, Arrived: true, new DateTime(2026, 11, 6), ListsSerial: true),
            'S' => new FlexBase.AccountListView(account, "s-" + account, 1, Connected: false, Arrived: false, new DateTime(2026, 11, 5), ListsSerial: true),
            _ => throw new ArgumentException(code.ToString()),
        };

        private static FlexBase.AccountPicture Picture(char a, char b, string preferred, string lastSeen, string saved = "AB")
        {
            var lists = new List<FlexBase.AccountListView>();
            foreach (var v in new[] { View(A, a), View(B, b) }) if (v != null) lists.Add(v.Value);
            var savedAccounts = new List<string>();
            if (saved.Contains('A')) savedAccounts.Add(A);
            if (saved.Contains('B')) savedAccounts.Add(B);
            return new FlexBase.AccountPicture(Serial, lists, preferred, lastSeen, OnLan: false, savedAccounts);
        }

        private static string Acct(string code) => code switch { "A" => A, "B" => B, "X" => "x@example.com", _ => "" };

        /// <summary>
        /// Columns: A's list, B's list, chosen account, last-seen account,
        /// saved accounts ("AB", "A" or "" for none), expected plan, expected
        /// plan account, expected route account and source.
        /// </summary>
        [Theory]
        // An answering list that names the radio wins: use that held session.
        [InlineData('R', 'E', "", "", "AB", nameof(Plan.UseHeldSession), "A", "A", AccountRouteSource.LiveAttribution)]
        [InlineData('E', 'R', "", "", "AB", nameof(Plan.UseHeldSession), "B", "B", AccountRouteSource.LiveAttribution)]
        [InlineData('E', 'R', "A", "A", "AB", nameof(Plan.UseHeldSession), "B", "B", AccountRouteSource.LiveAttribution)]  // live beats chosen
        // Two accounts list it (a club rig): chosen, then last seen, then by name.
        [InlineData('R', 'R', "B", "", "AB", nameof(Plan.UseHeldSession), "B", "B", AccountRouteSource.LiveAttribution)]
        [InlineData('R', 'R', "", "B", "AB", nameof(Plan.UseHeldSession), "B", "B", AccountRouteSource.LiveAttribution)]
        [InlineData('R', 'R', "", "", "AB", nameof(Plan.UseHeldSession), "A", "A", AccountRouteSource.LiveAttribution)]
        // Every saved account answers, none lists it: the verdict, whatever was chosen.
        [InlineData('E', 'E', "", "", "AB", nameof(Plan.NotListedByAnySavedAccount), "", "", AccountRouteSource.None)]
        [InlineData('E', 'E', "A", "", "AB", nameof(Plan.NotListedByAnySavedAccount), "", "A", AccountRouteSource.Preferred)]
        // One saved account has not answered: no verdict; the radio's own account if saved.
        [InlineData('E', 'C', "B", "", "AB", nameof(Plan.SignInAsRadiosAccount), "B", "B", AccountRouteSource.Preferred)]
        [InlineData('E', '-', "", "B", "AB", nameof(Plan.SignInAsRadiosAccount), "B", "B", AccountRouteSource.LastSeen)]
        [InlineData('E', '-', "A", "B", "AB", nameof(Plan.SignInAsRadiosAccount), "A", "A", AccountRouteSource.Preferred)]  // chosen beats last seen
        [InlineData('E', '-', "", "", "AB", nameof(Plan.SignInAsUsual), "", "", AccountRouteSource.None)]
        // A stale session's old list is not live: the chosen account wins (Codex B1).
        [InlineData('S', 'E', "B", "", "AB", nameof(Plan.SignInAsRadiosAccount), "B", "B", AccountRouteSource.Preferred)]
        [InlineData('S', '-', "", "A", "AB", nameof(Plan.SignInAsRadiosAccount), "A", "A", AccountRouteSource.LastSeen)]
        [InlineData('S', 'C', "", "", "AB", nameof(Plan.SignInAsUsual), "", "", AccountRouteSource.None)]
        // A chosen account nobody saved cannot be signed in as.
        [InlineData('C', 'C', "X", "", "AB", nameof(Plan.SignInAsUsual), "", "X", AccountRouteSource.Preferred)]
        // No saved accounts: never a vacuous verdict; a live held session is still used.
        [InlineData('E', '-', "", "", "", nameof(Plan.SignInAsUsual), "", "", AccountRouteSource.None)]
        [InlineData('R', '-', "", "", "", nameof(Plan.UseHeldSession), "A", "A", AccountRouteSource.LiveAttribution)]
        // Only A saved, A answers empty, B (unsaved) unknown: the verdict covers saved accounts.
        [InlineData('E', 'C', "", "", "A", nameof(Plan.NotListedByAnySavedAccount), "", "", AccountRouteSource.None)]
        // Repair round 3 (Codex B2-R2): the saved set is part of the picture. B saved and not
        // answering (no session, connected with no list yet, or stale) is unknown, and an
        // unknown blocks the verdict.
        [InlineData('E', 'C', "", "", "AB", nameof(Plan.SignInAsUsual), "", "", AccountRouteSource.None)]
        [InlineData('E', 'S', "", "", "AB", nameof(Plan.SignInAsUsual), "", "", AccountRouteSource.None)]
        // Codex's coverage notes: a live list on an account nobody saved is still used, and
        // a stale list beside a pending one never makes a verdict.
        [InlineData('R', 'C', "", "", "B", nameof(Plan.UseHeldSession), "A", "A", AccountRouteSource.LiveAttribution)]
        [InlineData('S', 'C', "B", "", "AB", nameof(Plan.SignInAsRadiosAccount), "B", "B", AccountRouteSource.Preferred)]
        public void Every_row_of_the_routing_table(char a, char b, string preferred, string lastSeen, string saved,
            string expected, string planAccount, string routeAccount, AccountRouteSource routeSource)
        {
            var picture = Picture(a, b, Acct(preferred), Acct(lastSeen), saved);

            var plan = FlexBase.PlanSmartLinkSignIn(picture);

            Assert.Equal(expected, plan.Kind.ToString());
            Assert.Equal(Acct(planAccount), plan.Account);
            Assert.Equal(Acct(routeAccount), picture.Route.Account);
            Assert.Equal(routeSource, picture.Route.Source);
        }

        /// <summary>
        /// Two readings are the same moment only when every list and both
        /// accounts are unchanged; any received change makes them differ.
        /// </summary>
        [Fact]
        public void Two_readings_match_only_when_nothing_was_received_between_them()
        {
            var first = Picture('E', 'E', "", "");
            Assert.True(first.SameAs(Picture('E', 'E', "", "")));
            Assert.False(first.SameAs(Picture('R', 'E', "", "")));     // a new list naming the radio
            Assert.False(first.SameAs(Picture('C', 'E', "", "")));     // a reconnect, no list yet
            Assert.False(first.SameAs(Picture('E', 'E', "B", "")));    // the chosen account changed
            Assert.False(Picture('E', '-', "", "", "A").SameAs(Picture('E', '-', "", "", "AB")));   // an account saved meanwhile (round 3)
            Assert.True(Picture('E', '-', "", "", "AB").SameAs(Picture('E', '-', "", "", "AB")));
            var later = new FlexBase.AccountPicture(Serial, new[]
            {
                new FlexBase.AccountListView(A, "s-" + A, 2, true, true, new DateTime(2026, 11, 6), false),
                View(B, 'E')!.Value,
            }, "", "", false, new[] { A, B });
            Assert.False(first.SameAs(later));                         // same answer, newer connection
        }
    }
}
