#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The one-time SmartLink tier offer is put at EVERY point where a
    /// connect's walk gives up, once per connect, and a pending offer that is
    /// not put is cleared with a reason (the tiers work, Track A repair round
    /// 1, Codex's B2).
    /// </summary>
    /// <remarks>
    /// <para>The walk lives in <c>globals.vb</c>, which this project cannot
    /// run, so most of these are source pins: they hold each walk end's call
    /// and the order around it, not the behaviour. Whether the box takes the
    /// foreground, and what a Yes does to a real connect, is the harness's
    /// (named in the repair report). <see cref="FlexBase.DismissTierOffer"/>
    /// is driven for real.</para>
    /// <para>No test here saves SmartLink accounts.</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TierOfferWalkEndTests
    {
        private static string Globals() => File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "globals.vb"));

        /// <summary>The body of a VB Function or Sub, from its declaration to its End line.</summary>
        private static string Body(string src, string declaration, string end = "\n    End Function")
        {
            int start = src.IndexOf(declaration, StringComparison.Ordinal);
            Assert.True(start >= 0, declaration + " was not found in globals.vb");
            int stop = src.IndexOf(end, start, StringComparison.Ordinal);
            Assert.True(stop > start, "no end for " + declaration);
            return src.Substring(start, stop - start);
        }

        [Fact]
        public void Every_walk_end_puts_the_offer()
        {
            string g = Globals();
            // The selector's walk and the walk resumed after a failed open share the re-plan.
            Assert.Contains("OfferTierAndReplanSmartLink(serial, lowBW, pathForced, \"wpfSelectorProc\")", g);
            Assert.Contains("OfferTierAndReplanSmartLink(walk.Serial, walk.LowBW, walk.Forced, \"connect walk (after failed open)\")", g);
            // The retry paths and the startup auto-connect, each named.
            Assert.Contains("OfferHigherSmartLinkTier(RigControl, \"startup auto-connect\")", g);
            Assert.Contains("OfferHigherSmartLinkTier(tryAgainRig, \"startup auto-connect, try again\")", g);
            Assert.Contains("OfferHigherSmartLinkTier(tierRig, \"OpenTheRadio remote retry\")", g);
            Assert.Contains("OfferHigherSmartLinkTier(autoRetryRig, \"OpenTheRadio auto-connect retry\")", g);
            // Six callers in all, and no other: a new walk end must be named here.
            Assert.Equal(6, g.Split("OfferHigherSmartLinkTier(").Length - 1 - 1 /* the declaration */
                + g.Split("OfferTierAndReplanSmartLink(").Length - 1 - 1 /* its declaration */
                - 1 /* OfferTierAndReplanSmartLink's own call to OfferHigherSmartLinkTier */);
        }

        [Fact]
        public void The_resumed_walk_puts_the_offer_when_it_is_exhausted_too()
        {
            // Codex's B2: Local's session up, Local's open failed, the resumed
            // SmartLink leg refused, the walk exhausted. It used to return
            // False straight from the exhausted branch, past the offer.
            string body = Body(Globals(), "Private Function ResumeWalkAfterFailedOpen() As Boolean");
            int exhausted = body.IndexOf("is exhausted", StringComparison.Ordinal);
            int offer = body.IndexOf("OfferTierAndReplanSmartLink(", StringComparison.Ordinal);
            Assert.True(exhausted > 0 && offer > exhausted, "the offer must follow the exhausted branch");
            Assert.DoesNotContain("Return ", body.Substring(exhausted, offer - exhausted));
            // And a Yes opens what it re-planned.
            Assert.Contains("tierRig.Start()", body.Substring(offer));
        }

        [Fact]
        public void The_offer_is_asked_once_per_connect_and_every_unasked_offer_is_cleared_with_a_reason()
        {
            string g = Globals();
            string offer = Body(g, "Private Function OfferHigherSmartLinkTier(rig As FlexBase, where As String) As Boolean");
            int asked = offer.IndexOf("If _tierOfferAskedThisConnect Then", StringComparison.Ordinal);
            int set = offer.IndexOf("_tierOfferAskedThisConnect = True", StringComparison.Ordinal);
            int show = offer.IndexOf("MessageBox.Show(", StringComparison.Ordinal);
            Assert.True(asked > 0 && set > asked && show > set, "checked, then set, then shown");
            Assert.True(offer.IndexOf("rig.CancelRequested", StringComparison.Ordinal) < show, "a cancelled connect is never asked");
            // Cancelled, already asked, answered Cancel: each cleared, saying why.
            Assert.Equal(3, offer.Split("rig.DismissTierOffer(").Length - 1);
            // The connecting window may still be up and reclaiming the foreground (#331).
            Assert.True(offer.IndexOf("PushAttentionWindow()", StringComparison.Ordinal) < show);
            Assert.Contains("PopAttentionWindow()", offer.Substring(show));

            // Reset at the start of each connect.
            string core = Body(g, "Private Function openTheRadioCore(initialCall As Boolean) As Boolean");
            int reset = core.IndexOf("_tierOfferAskedThisConnect = False", StringComparison.Ordinal);
            Assert.True(reset > 0 && reset < core.IndexOf("TryAutoConnectOnStartup()", StringComparison.Ordinal),
                "the flag is reset before the startup auto-connect and the selector can ask");

            // The last sweep, before the connecting window closes.
            int sweep = core.IndexOf("RigControl?.DismissTierOffer(", StringComparison.Ordinal);
            Assert.True(sweep > 0 && sweep < core.IndexOf("CloseConnectingWindowOntoTheShell(If(rv", StringComparison.Ordinal));
        }

        [Fact]
        public void The_retry_ladders_put_the_offer_after_they_give_up_and_retry_once_on_Yes()
        {
            string core = Body(Globals(), "Private Function openTheRadioCore(initialCall As Boolean) As Boolean");
            int remote = core.IndexOf("\"OpenTheRadio remote retry\"", StringComparison.Ordinal);
            Assert.True(remote > core.IndexOf("For attempt = 2 To maxAttempts", StringComparison.Ordinal), "after the ladder");
            Assert.True(remote < core.IndexOf("\"OpenTheRadio:all retry attempts failed\"", StringComparison.Ordinal), "before the verdict");
            Assert.Contains("tierRig.RetryConnect()", core.Substring(remote, 600));

            int auto = core.IndexOf("\"OpenTheRadio auto-connect retry\"", StringComparison.Ordinal);
            Assert.True(auto > 0);
            Assert.Contains("Loop While autoTierRaised", core.Substring(auto, 200));
        }

        [Fact]
        public void DismissTierOffer_clears_a_pending_offer_and_saves_nothing()
        {
            using var scope = new RadioConfigStaticsScope(nameof(TierOfferWalkEndTests));
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
            try
            {
                var prop = typeof(FlexBase).GetProperty(nameof(FlexBase.PendingTierOffer))!;
                var offer = new SmartLinkTierOffer("4711-1111-2222-3333", "owner@example.invalid",
                    SmartLinkConnectionMode.AutomaticHolePunch, SmartLinkConnectionMode.ManualPortForwardOnly,
                    SmartLinkRefusal.TierForbidsWhatTheRadioOffers);
                prop.SetValue(rig, offer);
                Assert.Same(offer, rig.PendingTierOffer);

                rig.DismissTierOffer("the radio opened over another path");
                Assert.Null(rig.PendingTierOffer);
                rig.DismissTierOffer("nothing pending: a no-op");
                Assert.Null(rig.PendingTierOffer);
            }
            finally
            {
                rig.theRadio = null;
                rig.Dispose();
            }
        }
    }
}
