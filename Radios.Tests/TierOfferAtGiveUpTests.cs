#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The last walk end (the tiers work, Track A repair round 2, from Codex's
    /// re-check): a connect whose open failed puts a pending tier offer before
    /// it gives up, even when the command connection is still up and no walk
    /// ran, which is startup auto-connect's SmartLink-refused, Local-connected,
    /// Local-open-failed case.
    /// </summary>
    /// <remarks>
    /// <see cref="TierOfferAtGiveUp.RecoverAfterFailedOpen"/> is the code the
    /// app runs, driven here with fakes for its boundaries: the offer box, the
    /// re-plan, the open, the history and the dismissal. No account is saved
    /// and no window is shown. That the app calls it at the right place, with
    /// no transport or walk gate in front of it, is pinned from the source at
    /// the end, because <c>globals.vb</c> cannot be run from this project.
    /// </remarks>
    public sealed class TierOfferAtGiveUpTests
    {
        private static SmartLinkTierOffer Offer() => new("4711-1111-2222-3333", "owner@example.invalid",
            SmartLinkConnectionMode.ManualPlusUpnp, SmartLinkConnectionMode.ManualPortForwardOnly,
            SmartLinkRefusal.TierForbidsWhatTheRadioOffers);

        /// <summary>Every boundary call, in order.</summary>
        private sealed class Fakes
        {
            public readonly List<string> Calls = new();
            public bool Answer;          // the operator's Yes, and the re-plan coming up
            public bool Opens;           // the re-planned open succeeds
            public SmartLinkTierOffer? Offered;

            public bool Run(SmartLinkTierOffer? pending, bool cancelled, bool transportUp) =>
                TierOfferAtGiveUp.RecoverAfterFailedOpen(pending, cancelled, transportUp,
                    offer => { Offered = offer; Calls.Add("offer"); return Answer; },
                    () => { Calls.Add("open"); return Opens; },
                    opened => Calls.Add("commit:" + opened),
                    why => Calls.Add("dismiss:" + why));
        }

        [Fact]
        public void Auto_connect_local_open_failed_with_the_transport_still_up_puts_the_offer_and_opens_on_Yes()
        {
            // Codex's exact state: SmartLink refused by the tier (offer
            // pending), Local's session up, Local's open failed, IsConnected
            // still true, no walk. Yes re-plans the same radio and opens it.
            var offer = Offer();
            var f = new Fakes { Answer = true, Opens = true };
            Assert.True(f.Run(offer, cancelled: false, transportUp: true));
            Assert.Same(offer, f.Offered);
            Assert.Equal(new[] { "offer", "open", "commit:True" }, f.Calls);
        }

        [Fact]
        public void The_offer_is_put_whether_or_not_the_transport_fell()
        {
            foreach (bool transportUp in new[] { true, false })
            {
                var f = new Fakes { Answer = false };
                Assert.False(f.Run(Offer(), cancelled: false, transportUp));
                Assert.Equal(new[] { "offer" }, f.Calls);
            }
        }

        [Fact]
        public void No_or_Cancel_opens_nothing_and_commits_nothing()
        {
            // The presenting delegate owns No, Cancel and once-per-connect, and
            // returns False for each: nothing is re-planned or opened here.
            var f = new Fakes { Answer = false };
            Assert.False(f.Run(Offer(), cancelled: false, transportUp: true));
            Assert.Equal(new[] { "offer" }, f.Calls);
        }

        [Fact]
        public void Yes_whose_re_planned_open_fails_commits_the_failure_and_gives_up()
        {
            var f = new Fakes { Answer = true, Opens = false };
            Assert.False(f.Run(Offer(), cancelled: false, transportUp: true));
            Assert.Equal(new[] { "offer", "open", "commit:False" }, f.Calls);
        }

        [Fact]
        public void A_cancelled_connect_is_never_asked_and_its_offer_is_dismissed_saying_why()
        {
            var f = new Fakes { Answer = true, Opens = true };
            Assert.False(f.Run(Offer(), cancelled: true, transportUp: true));
            Assert.Single(f.Calls);
            Assert.StartsWith("dismiss:", f.Calls[0]);
            Assert.Contains("cancelled", f.Calls[0]);
        }

        [Fact]
        public void Nothing_pending_changes_nothing_so_a_failed_open_is_not_turned_into_a_retry()
        {
            foreach (bool transportUp in new[] { true, false })
            {
                var f = new Fakes { Answer = true, Opens = true };
                Assert.False(f.Run(null, cancelled: false, transportUp));
                Assert.Empty(f.Calls);
            }
        }

        // ── Where the app calls it ──────────────────────────────────────

        private static string Core()
        {
            string g = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "globals.vb"));
            const string decl = "Private Function openTheRadioCore(initialCall As Boolean) As Boolean";
            int start = g.IndexOf(decl, StringComparison.Ordinal);
            Assert.True(start >= 0, "openTheRadioCore was not found in globals.vb");
            int stop = g.IndexOf("\n    End Function", start, StringComparison.Ordinal);
            return g.Substring(start, stop - start);
        }

        [Fact]
        public void The_app_puts_it_after_every_ladder_and_before_the_give_up_with_no_transport_or_walk_gate()
        {
            string core = Core();
            int call = core.IndexOf("TierOfferAtGiveUp.RecoverAfterFailedOpen(", StringComparison.Ordinal);
            Assert.True(call > 0, "openTheRadioCore must call the last walk end");
            Assert.Equal(call, core.LastIndexOf("TierOfferAtGiveUp.RecoverAfterFailedOpen(", StringComparison.Ordinal));

            // After the resumed walk and both ladders...
            Assert.True(call > core.IndexOf("rv = ResumeWalkAfterFailedOpen()", StringComparison.Ordinal));
            Assert.True(call > core.IndexOf("Loop While autoTierRaised", StringComparison.Ordinal));
            Assert.True(call > core.IndexOf("\"OpenTheRadio remote retry\"", StringComparison.Ordinal));
            // ...and before the connect gives up and the last sweep drops the offer.
            Assert.True(call < core.IndexOf("radioSelected = DialogResult.Abort", StringComparison.Ordinal));
            Assert.True(call < core.IndexOf("RigControl?.DismissTierOffer(", StringComparison.Ordinal));

            // The guard in front of it: the open failed and a rig exists. The
            // bug was a gate on the transport (the ladders' Not IsConnected)
            // and on the walk (_pendingWalk); neither may come back here.
            int guard = core.LastIndexOf("If Not rv AndAlso", call, StringComparison.Ordinal);
            Assert.True(guard > 0);
            string guardLine = core.Substring(guard, core.IndexOf('\n', guard) - guard);
            Assert.DoesNotContain("IsConnected", guardLine);
            Assert.DoesNotContain("_pendingWalk", guardLine);
            Assert.DoesNotContain("CancelRequested", guardLine); // the helper dismisses a cancel, saying why

            // It is handed the real state, and a Yes re-plans through the shared helper.
            string args = core.Substring(call, 700);
            Assert.Contains(".PendingTierOffer", args);
            Assert.Contains(".CancelRequested", args);
            Assert.Contains(".IsConnected", args);
            Assert.Contains("OfferTierAndReplanSmartLink(offer.Serial", args);
            Assert.Contains(".Start()", args);
            Assert.Contains("DismissTierOffer(why)", args);
        }

        [Fact]
        public void A_new_dial_clears_an_unshown_offer_through_the_traced_dismissal()
        {
            string src = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "Radios", "FlexBase.ConnectTiers.cs"));
            int start = src.IndexOf("private void ResetConnectTierState()", StringComparison.Ordinal);
            Assert.True(start > 0);
            string body = src.Substring(start, src.IndexOf("\n        }", start, StringComparison.Ordinal) - start);
            Assert.Contains("DismissTierOffer(", body);
            Assert.DoesNotContain("PendingTierOffer = null", body);
        }
    }
}
