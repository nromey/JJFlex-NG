using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Task #402, the second defect: FlexLib raises ONE RadioRemoved event
    /// for three different stories, and our handler told all three as "gone
    /// from discovery". On 2026-08-30 that turned our own station-name-timeout
    /// teardown into the roster dropping a radio whose LAN broadcasts were
    /// arriving the whole time — and 3 ms into the resulting hole the connect
    /// walk read the empty roster as "radio not on the LAN" and denied the
    /// retry its local leg (the sighting was re-added 119 ms later).
    ///
    /// <para>The classification and the suppression rule are pure statics on
    /// FlexBase precisely so this truth table cannot rot silently.</para>
    /// </summary>
    public sealed class RadioRemovalClassificationTests
    {
        // ── ClassifyRadioRemoval ────────────────────────────────────────

        [Fact]
        public void OurOwnTeardownDisconnectIsSelfInitiated()
        {
            // The 2026-08-30 20:02 case: station-name timeout →
            // teardownDisconnect → FlexLib's Disconnect tail raises the
            // removal synchronously for the very object we hold.
            Assert.Equal(FlexBase.RadioRemovalKind.SelfInitiated,
                FlexBase.ClassifyRadioRemoval(
                    selfDisconnectActive: true, disconnectingFlag: false, sameObject: true));
        }

        [Fact]
        public void TheOrderlyDisconnectPathIsSelfInitiatedToo()
        {
            // FlexBase.Disconnect() sets the Disconnecting latch before it
            // calls into FlexLib; the removal it triggers is still ours.
            Assert.Equal(FlexBase.RadioRemovalKind.SelfInitiated,
                FlexBase.ClassifyRadioRemoval(
                    selfDisconnectActive: false, disconnectingFlag: true, sameObject: true));
        }

        [Fact]
        public void AnUnexpectedDropOfOurRadioIsNotCalledADiscoveryLoss()
        {
            // TCP died and FlexLib's own drop handler called Disconnect: our
            // object, but nothing we initiated. The radio may still be on the
            // air — this must be labelled as a connection loss, not as the
            // radio leaving the network.
            Assert.Equal(FlexBase.RadioRemovalKind.ConnectionLostOurRadio,
                FlexBase.ClassifyRadioRemoval(
                    selfDisconnectActive: false, disconnectingFlag: false, sameObject: true));
        }

        [Fact]
        public void AnUnrelatedRadioAgingOutIsADiscoveryLoss()
        {
            Assert.Equal(FlexBase.RadioRemovalKind.DiscoveryLoss,
                FlexBase.ClassifyRadioRemoval(
                    selfDisconnectActive: false, disconnectingFlag: false, sameObject: false));
        }

        [Fact]
        public void TheDisconnectingLatchAloneNeverClaimsAnotherObjectsRemoval()
        {
            // Disconnecting is a one-way latch that never resets for the life
            // of the instance. If it could classify by itself, a LATER,
            // genuine discovery loss of the re-added sighting (a different
            // object, same serial) would be labelled self-initiated and its
            // roster raise swallowed — a stale row for a radio that really
            // left. Reference identity is required.
            Assert.Equal(FlexBase.RadioRemovalKind.DiscoveryLoss,
                FlexBase.ClassifyRadioRemoval(
                    selfDisconnectActive: false, disconnectingFlag: true, sameObject: false));
            Assert.Equal(FlexBase.RadioRemovalKind.DiscoveryLoss,
                FlexBase.ClassifyRadioRemoval(
                    selfDisconnectActive: true, disconnectingFlag: true, sameObject: false));
        }

        // ── RemovalSuppressesRosterRaise ────────────────────────────────

        [Fact]
        public void OnlyASelfInitiatedRemovalIsKeptFromTheRoster()
        {
            Assert.True(FlexBase.RemovalSuppressesRosterRaise(
                FlexBase.RadioRemovalKind.SelfInitiated));
            // Both other kinds must keep reaching the roster: it consults
            // RadioAvailability for the truth, and a genuine loss must
            // still be told.
            Assert.False(FlexBase.RemovalSuppressesRosterRaise(
                FlexBase.RadioRemovalKind.ConnectionLostOurRadio));
            Assert.False(FlexBase.RemovalSuppressesRosterRaise(
                FlexBase.RadioRemovalKind.DiscoveryLoss));
        }

        // ── LanSeenRecently ─────────────────────────────────────────────

        [Fact]
        public void FreshLanEvidenceCounts()
        {
            // The field case: evidence 3 ms old when the walk asked.
            Assert.True(FlexBase.LanSeenRecently(lastSeenTick: 100_000, nowTick: 100_003));
        }

        [Fact]
        public void EvidenceAtTheWindowEdgeStillCounts()
        {
            Assert.True(FlexBase.LanSeenRecently(
                lastSeenTick: 100_000, nowTick: 100_000 + FlexBase.LanRecencyWindowMs));
        }

        [Fact]
        public void StaleEvidenceDoesNot()
        {
            Assert.False(FlexBase.LanSeenRecently(
                lastSeenTick: 100_000, nowTick: 100_001 + FlexBase.LanRecencyWindowMs));
        }

        [Fact]
        public void NeverSeenDoesNot()
        {
            Assert.False(FlexBase.LanSeenRecently(lastSeenTick: 0, nowTick: 100_000));
        }

        [Fact]
        public void TheRecencyWindowStaysInsideFlexLibsOwnDiscoveryTimeout()
        {
            // FlexLib's RadioListMaid retires unheard-from radios after
            // API.RADIOLIST_TIMEOUT_SECONDS (17 s). Our recency window must
            // never outlive what FlexLib itself considers a current sighting,
            // or availability would vouch for radios discovery has given up on.
            Assert.True(FlexBase.LanRecencyWindowMs / 1000.0 < Flex.Smoothlake.FlexLib.API.RADIOLIST_TIMEOUT_SECONDS);
        }

        // ── StationEvidenceVerdict ──────────────────────────────────────
        //
        // The discriminating instrument for the station-name timeout: the
        // radio's UDP discovery broadcasts carry the station roll call on a
        // path that does not depend on the TCP receive channel the wait
        // starves on. Four verdicts, four different defects.

        [Fact]
        public void AppliedNameMeansTheReceivePathStalled()
        {
            // The 2026-08-30 field case: discovery said 'k5ner' 122 ms into a
            // wait that then starved for 45 s. The verdict must place the
            // defect with US, not the radio.
            var verdict = FlexBase.StationEvidenceVerdict(
                sawHandle: true, discoveryStation: "k5ner", requestedName: "k5ner");
            Assert.Contains("APPLIED", verdict);
            Assert.Contains("OUR defect", verdict);
        }

        [Fact]
        public void EmptyStationMeansTheRadioNeverAppliedIt()
        {
            var verdict = FlexBase.StationEvidenceVerdict(
                sawHandle: true, discoveryStation: "", requestedName: "k5ner");
            Assert.Contains("never applied", verdict);
            Assert.Contains("radio-side", verdict);
        }

        [Fact]
        public void ADifferentNameMeansARename()
        {
            // Track A's signature 1: the radio (or a stale merge) holds a
            // different name, so an equality wait can never succeed.
            var verdict = FlexBase.StationEvidenceVerdict(
                sawHandle: true, discoveryStation: "k5ner1", requestedName: "k5ner");
            Assert.Contains("rename", verdict);
            Assert.Contains("k5ner1", verdict);
        }

        [Fact]
        public void NoRecordOfOurClientSaysSo()
        {
            var verdict = FlexBase.StationEvidenceVerdict(
                sawHandle: false, discoveryStation: "", requestedName: "k5ner");
            Assert.Contains("no record", verdict);
        }
    }
}
