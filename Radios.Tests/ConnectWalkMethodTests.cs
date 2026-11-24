#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The connect walk's SmartLink method field, its rule for moving on, and
    /// its one voice (the tiers plan, section 4; Track B). The sentences are
    /// read as assembled sentences, through the speech seam, never by ear.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ConnectWalkMethodTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ConnectWalkMethodTests));
        private readonly Action<string> _seam = ConnectWalkAnnouncer.Speak;
        private readonly List<string> _said = new();

        public ConnectWalkMethodTests() => ConnectWalkAnnouncer.Speak = _said.Add;

        public void Dispose()
        {
            ConnectWalkAnnouncer.Speak = _seam;
            _scope.Dispose();
        }

        private static ConnectWalk Walk(ConnectPathKind chosen, bool forced, params ConnectPathKind[] fallbacks) =>
            ConnectWalk.Build("1111-2222-8600-3333", lowBW: false, chosen, fallbacks, forced);

        // ── The walk ────────────────────────────────────────────────────────

        [Fact]
        public void A_cancel_is_never_walked_on_and_neither_is_an_exhausted_walk()
        {
            var walk = Walk(ConnectPathKind.SmartLink, false, ConnectPathKind.Local);
            Assert.True(walk.MayMoveOn(cancelRequested: false));
            Assert.False(walk.MayMoveOn(cancelRequested: true));

            walk.MoveNext();
            Assert.False(walk.MayMoveOn(cancelRequested: false));        // nothing left

            // A forced path has one leg by construction: it never moves on.
            Assert.False(Walk(ConnectPathKind.SmartLink, true, ConnectPathKind.Local).MayMoveOn(false));
        }

        [Fact]
        public void The_SmartLink_leg_carries_the_method_it_reached_and_the_itinerary_is_unchanged()
        {
            var walk = Walk(ConnectPathKind.SmartLink, false, ConnectPathKind.Local);
            Assert.Null(walk.SmartLinkMethod);
            Assert.Equal("SmartLink,Local", walk.DescribeWithMethod());

            walk.NoteSmartLinkMethod(SmartLinkMethod.ManualPorts);
            walk.NoteSmartLinkMethod(SmartLinkMethod.UpnpPorts);          // the leg moved on to its next method
            walk.NoteSmartLinkMethod(null);                               // "none dialled" never erases what was reached

            Assert.Equal(SmartLinkMethod.UpnpPorts, walk.SmartLinkMethod);
            Assert.Equal("SmartLink,Local", walk.Describe());             // the legs are still paths, as saved in chains and history
            Assert.Equal(new[] { ConnectPathKind.SmartLink, ConnectPathKind.Local }, walk.Legs);
            Assert.Contains("UpnpPorts", walk.DescribeWithMethod());

            // On the Local leg a method means nothing, and is not taken.
            walk.MoveNext();
            walk.NoteSmartLinkMethod(SmartLinkMethod.HolePunch);
            Assert.Equal(SmartLinkMethod.UpnpPorts, walk.SmartLinkMethod);
        }

        [Fact]
        public void A_forced_path_still_leaves_the_methods_to_the_policy()
        {
            // Decision 9: a forced PATH is not a forced method. The walk has
            // one leg; which methods that leg tries is the policy's, with
            // Automatic still every method the tier allows.
            var walk = Walk(ConnectPathKind.SmartLink, true, ConnectPathKind.Local);
            Assert.Single(walk.Legs);
            var both = new RadioPortReport(
                new AdvertisedPortPair(PortPairSource.Manual, 4994, 4993, true),
                new AdvertisedPortPair(PortPairSource.Upnp, 21000, 21001, true), false, true);
            var forcedPath = SmartLinkMethodPolicy.Decide(new SmartLinkPolicyInput(
                SmartLinkConnectionMode.ManualPlusUpnp, true, RadioConnectionPreference.Auto, ForcedPath: walk.Forced, both));
            Assert.Equal(new[] { SmartLinkMethod.ManualPorts, SmartLinkMethod.UpnpPorts }, forcedPath.Methods);

            // "Hole punch always" pins the METHOD, and the leg then has nothing to move on to.
            var pinned = SmartLinkMethodPolicy.Decide(new SmartLinkPolicyInput(
                SmartLinkConnectionMode.AutomaticHolePunch, true, RadioConnectionPreference.HolePunch, false, both));
            Assert.Equal(new[] { SmartLinkMethod.HolePunch }, pinned.Methods);
        }

        [Fact]
        public void The_saved_path_values_are_untouched()
        {
            Assert.Equal(0, (int)ConnectPathKind.Local);
            Assert.Equal(1, (int)ConnectPathKind.SmartLink);
            Assert.Equal(2, Enum.GetValues<ConnectPathKind>().Length);    // 2 stays reserved for Connect; no method became a path
        }

        // ── The voice ───────────────────────────────────────────────────────

        [Fact]
        public void A_move_between_paths_is_said_once_in_the_walks_existing_sentence()
        {
            string returned = ConnectWalkAnnouncer.SayMove(ConnectPathKind.SmartLink, ConnectPathKind.Local);

            Assert.Equal("Could not connect over SmartLink. Trying the local network.", Assert.Single(_said));
            Assert.Equal(returned, _said[0]);

            _said.Clear();
            ConnectWalkAnnouncer.SayMove(ConnectPathKind.Local, ConnectPathKind.SmartLink);
            Assert.Equal("Could not connect over the local network. Trying SmartLink.", Assert.Single(_said));
        }

        [Fact]
        public void A_move_between_SmartLink_methods_is_one_whole_sentence_naming_both()
        {
            var methods = Enum.GetValues<SmartLinkMethod>();
            Assert.Equal(methods.Length, methods.Select(ConnectWalkAnnouncer.MethodLegName).Distinct().Count());

            foreach (var from in methods)
                foreach (var to in methods.Where(m => m != from))
                {
                    _said.Clear();
                    ConnectWalkAnnouncer.SayMethodMove(from, to);
                    string sentence = Assert.Single(_said);
                    Assert.StartsWith("Could not connect over ", sentence);
                    Assert.Contains(ConnectWalkAnnouncer.MethodLegName(from) + ". Trying " + ConnectWalkAnnouncer.MethodLegName(to) + ".", sentence);
                    Assert.DoesNotContain("{", sentence);
                    Assert.DoesNotContain("connect.walk", sentence);
                    Assert.DoesNotContain("#", sentence);                 // no task number in a shipped sentence
                }
        }

        [Fact]
        public void The_method_names_read_as_ways_of_using_SmartLink()
        {
            // The assembled sentence an operator hears when a dead forwarded
            // pair falls back to the radio's own UPnP pair (approved words, 2026-11-20).
            ConnectWalkAnnouncer.SayMethodMove(SmartLinkMethod.ManualPorts, SmartLinkMethod.UpnpPorts);
            Assert.Equal(
                "Could not connect over SmartLink on the radio's forwarded ports. Trying SmartLink on the ports the radio opened by UPnP.",
                Assert.Single(_said));
            Assert.Equal("SmartLink by hole punching", ConnectWalkAnnouncer.MethodLegName(SmartLinkMethod.HolePunch));
        }

        [Fact]
        public void The_proof_failure_reason_reads_as_a_sentence_where_the_app_says_it()
        {
            // The connect driver appends a full stop to the open's reason and
            // puts it in the retry verdict (approved words, 2026-11-20).
            string reason = Lexicon.Get("connect.start.no_audio_or_data");
            Assert.False(reason.EndsWith(".", StringComparison.Ordinal), "the driver adds the full stop");
            Assert.Equal(
                "Connection failed. No audio or data arrived from the radio before the connection attempt timed out. Please try Remote again.",
                Lexicon.Get("connect.walk.retry_failed_with_reason", ("retryReason", reason + ".")));
        }
    }
}
