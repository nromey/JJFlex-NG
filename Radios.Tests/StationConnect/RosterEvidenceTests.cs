using System.Linq;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// #577: the roster guard's INPUT, event-maintained, and what the
    /// production guard concludes from it. Design section 6, group 2.
    /// These deliver the events the client handlers deliver and read the
    /// production snapshot and verdict; setting OnlyStation directly is not
    /// this test.
    /// </summary>
    public sealed class RosterEvidenceTests
    {
        private const uint Us = 0x1111;
        private const uint Them = 0x2222;

        private static (RosterTracker tracker, int gen) Fresh()
        {
            var t = new RosterTracker(null); // the monotonic clock; nothing here depends on time
            t.Reset(7);
            return (t, 7);
        }

        private static RosterEntry Ours(string id = "our-id") => new RosterEntry(Us, id, true, "K5NER", "JJFlex");
        private static RosterEntry Theirs(string id = "their-id") => new RosterEntry(Them, id, false, "W1AW", "SmartSDR");

        // ── the positive control ──

        [Fact]
        public void OurAddAlone_IsOnlyUs_AndAuthoritativeUnderTheRadioStatusPolicy()
        {
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);

            Assert.Equal(RosterVerdict.OnlyUs, RosterGuard.Evaluate(t.Snapshot()).Verdict);
            Assert.Equal(RosterVerdict.OnlyUs,
                RosterGuard.ForAutomaticWrite(t.Snapshot(), RosterAuthorityFromRadioStatusPolicy.Instance).Verdict);
        }

        // ── the input changes and the verdict follows, without repeating our add ──

        [Fact]
        public void AnotherClientsAdd_WithoutRepeatingOurs_FlipsToOthersPresent()
        {
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);
            long genBefore = t.Generation;

            t.ClientAdded(Theirs(), g);

            var j = RosterGuard.Evaluate(t.Snapshot());
            Assert.Equal(RosterVerdict.OthersPresent, j.Verdict);
            Assert.True(t.Generation > genBefore, "the roster generation did not advance on another client's add");
        }

        [Fact]
        public void AnotherClientsUpdate_IsStillOthersPresent()
        {
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);
            t.ClientAdded(Theirs(id: ""), g);
            t.ClientUpdated(Theirs(id: "their-id"), g);

            Assert.Equal(RosterVerdict.OthersPresent, RosterGuard.Evaluate(t.Snapshot()).Verdict);
        }

        // ── removals carry their origin (Track G2; review section 3A; ruled 2026-09-22) ──

        [Fact]
        public void AnotherClientsRemovalByRadioStatus_IsThemGone_ForBothVerdicts()
        {
            // The positive control for the removal semantics: the radio's
            // own status said "disconnected", so they are gone, and the
            // authoritative roster is only us.
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);
            t.ClientAdded(Theirs(), g);
            t.ClientRemoved(Them, g, RosterRemovalOrigin.RadioStatus);

            Assert.Equal(RosterVerdict.OnlyUs, RosterGuard.Evaluate(t.Snapshot()).Verdict);
            Assert.Equal(RosterVerdict.OnlyUs,
                RosterGuard.ForAutomaticWrite(t.Snapshot(), RosterAuthorityByLiveMembershipPolicy.Instance).Verdict);
            Assert.False(t.Snapshot().RemovalUnconfirmed);
            Assert.Single(t.Snapshot().Entries);
        }

        [Fact]
        public void AnotherClientsRemovalByDiscoveryAlone_IsTreatedAsStillPresent()
        {
            // Discovery can omit a live client. Ruled: treat them as present.
            // Until Track G2 this was an Unknown that a bounded wait might
            // clear; it is OthersPresent now, for every verdict, and it says
            // why.
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);
            t.ClientAdded(Theirs(), g);
            t.ClientRemoved(Them, g, RosterRemovalOrigin.Discovery);

            var raw = RosterGuard.Evaluate(t.Snapshot());
            Assert.Equal(RosterVerdict.OthersPresent, raw.Verdict);
            Assert.Contains("discovery", raw.Reason);
            Assert.Equal(RosterVerdict.OthersPresent,
                RosterGuard.ForAutomaticWrite(t.Snapshot(), RosterAuthorityByLiveMembershipPolicy.Instance).Verdict);
            Assert.True(t.Snapshot().RemovalUnconfirmed);
            Assert.True(t.Snapshot().Others.Single().ReportedGoneByDiscovery);
        }

        [Fact]
        public void AnUnrelatedUpdateOfOurOwnRecord_DoesNotMakeADiscoveryOnlyDisappearanceALeave()
        {
            // The review's exact objection: our own record being updated
            // proves nothing about whether the OTHER handle really left.
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);
            t.ClientAdded(Theirs(), g);
            t.ClientRemoved(Them, g, RosterRemovalOrigin.Discovery);
            t.ClientUpdated(Ours(), g);

            Assert.Equal(RosterVerdict.OthersPresent, RosterGuard.Evaluate(t.Snapshot()).Verdict);
            Assert.True(t.Snapshot().RemovalUnconfirmed);
        }

        [Fact]
        public void TheRadiosOwnStatusForThatHandle_SettlesADiscoveryOnlyDisappearance_EitherWay()
        {
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);
            t.ClientAdded(Theirs(), g);
            t.ClientRemoved(Them, g, RosterRemovalOrigin.Discovery);

            // The radio lists them again: present, no longer ambiguous.
            t.ClientUpdated(Theirs(), g);
            Assert.Equal(RosterVerdict.OthersPresent, RosterGuard.Evaluate(t.Snapshot()).Verdict);
            Assert.False(t.Snapshot().RemovalUnconfirmed);

            // The radio reports them disconnected: gone.
            t.ClientRemoved(Them, g, RosterRemovalOrigin.RadioStatus);
            Assert.Equal(RosterVerdict.OnlyUs, RosterGuard.Evaluate(t.Snapshot()).Verdict);
            Assert.Equal(RosterVerdict.OnlyUs,
                RosterGuard.ForAutomaticWrite(t.Snapshot(), RosterAuthorityByLiveMembershipPolicy.Instance).Verdict);
        }

        [Fact]
        public void ARemovalOfUnstatedOrigin_IsTheAmbiguousKind()
        {
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);
            t.ClientAdded(Theirs(), g);
            t.ClientRemoved(Them, g);

            Assert.Equal(RosterVerdict.OthersPresent, RosterGuard.Evaluate(t.Snapshot()).Verdict);
        }

        [Fact]
        public void AClientAlreadyListedAtAttachment_IsSeeded_AndCountsAsPresent()
        {
            // Discovery fills the vendor's list before we connect and no add
            // event fires for a record already there.
            var (t, g) = Fresh();
            t.Seed(new[] { Theirs(id: "") }, g);
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);

            var j = RosterGuard.Evaluate(t.Snapshot());
            Assert.Equal(RosterVerdict.OthersPresent, j.Verdict);
            Assert.Equal(2, t.Snapshot().Entries.Count);
        }

        [Fact]
        public void TheSeedNeverOverwritesALaterAddOfTheSameHandle()
        {
            var (t, g) = Fresh();
            t.ClientAdded(Theirs(id: "their-id"), g);
            t.Seed(new[] { Theirs(id: "") }, g);
            Assert.Equal("their-id", t.Snapshot().Entries.Single().ClientId);
        }

        [Fact]
        public void TheRuledAuthority_IsPendingUntilOurRecordCarriesIdentity_ThenAuthoritative()
        {
            var (t, g) = Fresh();
            t.ClientAdded(new RosterEntry(Us, "", false, "K5NER", "JJFlex"), g); // discovery-built
            t.OwnHandleEstablished(Us, g);
            Assert.Equal(RosterAuthority.Pending, RosterAuthorityByLiveMembershipPolicy.Instance.Judge(t.Snapshot()));
            Assert.True(RosterGuard.ForAutomaticWrite(t.Snapshot(), RosterAuthorityByLiveMembershipPolicy.Instance).MayChange);

            t.ClientUpdated(Ours(), g); // the radio's own status: client_id
            Assert.Equal(RosterAuthority.Authoritative, RosterAuthorityByLiveMembershipPolicy.Instance.Judge(t.Snapshot()));
            Assert.Equal(RosterVerdict.OnlyUs,
                RosterGuard.ForAutomaticWrite(t.Snapshot(), RosterAuthorityByLiveMembershipPolicy.Instance).Verdict);
        }

        [Fact]
        public void TheProductionDefaultRosterAuthority_IsTheRuledLiveMembershipPolicy()
        {
            Assert.IsType<RosterAuthorityByLiveMembershipPolicy>(StationPolicies.Defaults().RosterAuthority);
            // The other two stay closed.
            Assert.IsType<LoadCompletionUnconfirmedPolicy>(StationPolicies.Defaults().LoadCompletion);
            Assert.IsType<MaterializationUnknownPolicy>(StationPolicies.Defaults().InitialMaterialization);
        }

        [Fact]
        public void OurOwnRemoval_IsUnknown_AndReAddWithTheSameHandleRestoresOnlyUs()
        {
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);
            t.ClientRemoved(Us, g, RosterRemovalOrigin.RadioStatus);

            var gone = RosterGuard.Evaluate(t.Snapshot());
            Assert.Equal(RosterVerdict.Unknown, gone.Verdict);
            Assert.Contains("does not contain our own handle", gone.Reason);
            Assert.True(gone.MayChange);

            // The SmartLink remove/re-add dance: rebuilt from discovery.
            t.ClientAdded(new RosterEntry(Us, "", false, "K5NER", "JJFlex"), g);
            t.OwnHandleEstablished(Us, g);
            Assert.Equal(RosterVerdict.OnlyUs, RosterGuard.Evaluate(t.Snapshot()).Verdict);
            // ...but a record without identity is not yet authoritative.
            Assert.Equal(RosterAuthority.Pending,
                RosterAuthorityFromRadioStatusPolicy.Instance.Judge(t.Snapshot()));
        }

        [Fact]
        public void AnotherHandleWithAnEmptyClientId_IsNotDiscarded()
        {
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);
            t.ClientAdded(Theirs(id: ""), g);

            var j = RosterGuard.Evaluate(t.Snapshot());
            Assert.Equal(RosterVerdict.OthersPresent, j.Verdict);
            Assert.Contains("no client_id", j.Reason);
        }

        [Fact]
        public void NoOwnHandleYet_IsUnknown_NotOnlyUs_EvenWithOneRecord()
        {
            // The old snapshot read GuiClients.Count == 1 and called that
            // "only us"; without knowing which record is ours, one record
            // proves nothing.
            var (t, g) = Fresh();
            t.ClientAdded(Theirs(), g);

            var j = RosterGuard.Evaluate(t.Snapshot());
            Assert.Equal(RosterVerdict.Unknown, j.Verdict);
            Assert.Contains("not established yet", j.Reason);
        }

        [Fact]
        public void CallbacksFromAnOldAttempt_AreIgnored()
        {
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);

            t.ClientAdded(Theirs(), g - 1);
            t.ClientRemoved(Us, g - 1, RosterRemovalOrigin.RadioStatus);

            Assert.Equal(RosterVerdict.OnlyUs, RosterGuard.Evaluate(t.Snapshot()).Verdict);
            Assert.Single(t.Snapshot().Entries);
        }

        [Fact]
        public void ResetForgetsThePreviousConnectionEntirely()
        {
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);
            t.ClientAdded(Theirs(), g);

            t.Reset(g + 1);

            var s = t.Snapshot();
            Assert.Empty(s.Entries);
            Assert.False(s.OwnHandleKnown);
            Assert.False(s.RemovalUnconfirmed);
        }

        [Fact]
        public void TheUnknownPolicy_IsUnknownForGood_SoTheGuardDoesNotAskToWait()
        {
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            t.OwnHandleEstablished(Us, g);

            var j = RosterGuard.ForAutomaticWrite(t.Snapshot(), RosterAuthorityUnknownPolicy.Instance);
            Assert.Equal(RosterVerdict.Unknown, j.Verdict);
            Assert.False(j.MayChange);
        }

        [Fact]
        public void SnapshotsAreCopies()
        {
            var (t, g) = Fresh();
            t.ClientAdded(Ours(), g);
            var before = t.Snapshot();
            t.ClientAdded(Theirs(), g);
            Assert.Single(before.Entries);
            Assert.Equal(2, t.Snapshot().Entries.Count);
        }
    }

    /// <summary>Own-station evidence: identities, order, removal, generations.</summary>
    public sealed class StationEvidenceTests
    {
        private const uint Us = 0x1111;

        private static (StationTracker tracker, int gen) Fresh()
        {
            var t = new StationTracker(null);
            t.Reset(3);
            return (t, 3);
        }

        [Fact]
        public void TheDefaultStationIsAbsent()
        {
            var (t, _) = Fresh();
            Assert.False(t.Snapshot().StationPresent);
            Assert.Equal(0, t.Snapshot().OwnSliceCount);
        }

        [Fact]
        public void SlicesArrivingOutOfOrder_AreHeldByIdentityAndOrderedByIndex()
        {
            var (t, g) = Fresh();
            t.OwnSliceAdded(2, "C", Us, 0x3, g);
            t.OwnSliceAdded(0, "A", Us, 0x1, g);
            t.OwnSliceAdded(1, "B", Us, 0x2, g);

            Assert.Equal(new[] { 0, 1, 2 }, t.Snapshot().Slices.Select(s => s.Index).ToArray());
            Assert.True(t.Snapshot().StationPresent);
        }

        [Fact]
        public void RemovalAndReAdd_KeepIdentityStraight()
        {
            var (t, g) = Fresh();
            t.OwnSliceAdded(0, "A", Us, 0x1, g);
            t.OwnSliceAdded(1, "B", Us, 0x2, g);
            t.OwnSliceRemoved(0, g);
            Assert.Equal(new[] { 1 }, t.Snapshot().Slices.Select(s => s.Index).ToArray());
            t.OwnSliceAdded(0, "A", Us, 0x1, g);
            Assert.Equal(new[] { 0, 1 }, t.Snapshot().Slices.Select(s => s.Index).ToArray());
        }

        [Fact]
        public void NewSince_IsAnIdentityTest_NotACount()
        {
            var (t, g) = Fresh();
            t.OwnSliceAdded(0, "A", Us, 0x1, g);
            var before = t.Snapshot();
            long armed = t.Sequence;

            // A re-add of the SAME identity after arming is not new.
            t.OwnSliceRemoved(0, g);
            t.OwnSliceAdded(0, "A", Us, 0x1, g);
            Assert.Empty(t.Snapshot().NewSince(armed, before));

            // A genuinely new identity is.
            t.OwnSliceAdded(1, "B", Us, 0x2, g);
            Assert.Equal(new[] { 1 }, t.Snapshot().NewSince(armed, before).Select(s => s.Index).ToArray());
        }

        [Fact]
        public void AHeadlessSliceIsAResolvedAssociation()
        {
            var (t, g) = Fresh();
            t.OwnSliceAdded(0, "A", Us, 0, g);
            var s = t.Snapshot().Slices.Single();
            Assert.True(s.Headless);
        }

        [Fact]
        public void OldAttemptObservationsAreIgnored()
        {
            var (t, g) = Fresh();
            t.OwnSliceAdded(0, "A", Us, 0x1, g - 1);
            Assert.False(t.Snapshot().StationPresent);
        }
    }
}
