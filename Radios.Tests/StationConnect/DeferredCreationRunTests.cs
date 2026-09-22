using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// Track G review, section 1 step 11 and section 6 group 10: the
    /// disconnect-time create's ACTUAL delegate — decision inside it, against
    /// a fresh inventory request, with a live operation; a timed-out queue
    /// item invalidated; an uncertain send retained as uncertain.
    /// </summary>
    public sealed class DeferredCreationRunTests
    {
        private static StationResult FreshStation() => new StationResult
        {
            Outcome = StationOutcome.FreshStationConfirmed, Route = GlobalRoute.MissingOwnedGlobal,
            CreationArmed = true, PendingCreateName = "K5NER-8600", OwnSlicesAtEnd = 2,
        };

        private static StationHarness Armed()
        {
            var h = new StationHarness();
            h.Port.Facts.WantedGlobal = "K5NER-8600";
            h.Port.InventoryNames = new List<string> { "Default" };
            h.Port.FreshInventoryNames = new List<string> { "Default" };
            h.OurClientAdded();
            h.RadioReportsGlobalList("Default");
            return h;
        }

        private static PendingGlobalCreation Pending(StationHarness h) =>
            new PendingGlobalCreation("K5NER-8600", h.Port.Facts.Serial, h.Gen, "test");

        [Fact]
        public void StillMissingOnAFreshRequest_SavesOnce_AndConfirmsByReadback()
        {
            // The positive control.
            var h = Armed();
            h.Port.OnGlobalSaved = name => h.RadioReportsGlobalList("Default", name);

            var r = h.RunCreation(Pending(h), FreshStation());

            Assert.Equal(CreationOutcome.Confirmed, r.Outcome);
            Assert.Equal(new[] { "K5NER-8600" }, h.Port.GlobalSavesSent);
            Assert.Equal(1, h.Port.InventoryRequests);      // asked fresh, inside the delegate
        }

        [Fact]
        public void TheDecisionUsesTheFreshRequest_NotTheCachedList()
        {
            // The cached list lacks the name; the FRESH answer has it (the
            // operator saved it from another client meanwhile). Never
            // overwrite an intervening profile of that name.
            var h = Armed();
            h.Port.FreshInventoryNames = new List<string> { "Default", "K5NER-8600" };

            var r = h.RunCreation(Pending(h), FreshStation());

            Assert.Equal(CreationOutcome.Refused, r.Outcome);
            Assert.Contains("never overwrite", r.Reason);
            Assert.Empty(h.Port.GlobalSavesSent);
            Assert.Equal(1, h.Port.InventoryRequests);
        }

        [Fact]
        public void HeldSave_NameAppearsBeforeRelease_SendsNothing()
        {
            var h = Armed();
            h.Port.HoldDispatch = true;
            h.Waiter.Then(() => { h.Port.FreshInventoryNames = new List<string> { "Default", "K5NER-8600" }; h.Port.ReleaseHeld(); });

            var r = h.RunCreation(Pending(h), FreshStation());

            Assert.Equal(CreationOutcome.Refused, r.Outcome);
            Assert.Empty(h.Port.GlobalSavesSent);
        }

        [Theory]
        [InlineData("hold")]
        [InlineData("join")]
        [InlineData("intent")]
        public void HeldSave_PermissionWithdrawnBeforeRelease_SendsNothing(string what)
        {
            var h = Armed();
            h.Port.HoldDispatch = true;
            h.Waiter.Then(() =>
            {
                switch (what)
                {
                    case "hold": h.Port.Facts.HoldArmed = true; break;
                    case "join": h.OtherClientAdded(); break;
                    case "intent": h.Port.Facts.Intent = ProfileGuestIntent.LeaveAlone; break;
                }
                h.Port.ReleaseHeld();
            });

            var r = h.RunCreation(Pending(h), FreshStation());

            Assert.Equal(CreationOutcome.Refused, r.Outcome);
            Assert.Empty(h.Port.GlobalSavesSent);
        }

        [Fact]
        public void HeldSave_ReleasedAfterItsBound_IsInvalidated_AndSendsNothing()
        {
            var h = Armed();
            h.Port.HoldDispatch = true;
            h.Waiter.Then(() => { h.Clock.Advance(h.Deadlines.ProfileReadMs + h.Deadlines.DisconnectCreateConfirmMs + 1); h.Port.ReleaseHeld(); });

            var r = h.RunCreation(Pending(h), FreshStation());

            Assert.NotEqual(CreationOutcome.Confirmed, r.Outcome);
            Assert.Empty(h.Port.GlobalSavesSent);
            Assert.False(r.SaveSent);
        }

        [Fact]
        public void HeldSave_ReleasedUnchanged_Sends()
        {
            // The positive control for the held cases.
            var h = Armed();
            h.Port.HoldDispatch = true;
            h.Port.OnGlobalSaved = name => h.RadioReportsGlobalList("Default", name);
            h.Waiter.Then(() => h.Port.ReleaseHeld());

            var r = h.RunCreation(Pending(h), FreshStation());

            Assert.Equal(CreationOutcome.Confirmed, r.Outcome);
            Assert.Single(h.Port.GlobalSavesSent);
        }

        [Fact]
        public void ASaveWithNoReadback_IsRetainedAsUncertain_AndNotSentAgain()
        {
            var h = Armed();
            // The radio never lists it.

            var r = h.RunCreation(Pending(h), FreshStation());

            Assert.Equal(CreationOutcome.Unconfirmed, r.Outcome);
            Assert.True(r.SaveSent);
            Assert.Contains("not sent again", r.Reason);
            Assert.Single(h.Port.GlobalSavesSent);
        }

        [Fact]
        public void TheFreshRequestThatNeverAnswers_IsNotAbsenceEvidence()
        {
            var h = Armed();
            h.Port.FreshInventoryNames = null;            // no answer inside the bound
            h.Profiles.Reset(h.Gen);                       // and nothing reported before either
            h.OurClientAdded();

            var r = h.RunCreation(Pending(h), FreshStation());

            Assert.Equal(CreationOutcome.Refused, r.Outcome);
            Assert.Contains("never reported", r.Reason);
            Assert.Empty(h.Port.GlobalSavesSent);
        }

        [Fact]
        public void ACancelledAttempt_IsRefusedByTheDecision()
        {
            var h = Armed();
            var pending = Pending(h);
            h.Attempt.Cancel("disposed");

            var decision = DeferredGlobalCreation.Decide(new CreationFacts
            {
                Pending = pending, Attempt = h.Attempt, Connected = true, Ownership = RadioOwnership.Mine,
                Intent = ProfileGuestIntent.LoadMineAndPutBack, WantedGlobalNow = pending.Name, Serial = pending.Serial,
                Roster = new RosterJudgement(RosterVerdict.OnlyUs, "x", 1),
                Inventory = h.Profiles.Snapshot().GlobalList,
                StationOutcome = StationOutcome.FreshStationConfirmed,
            });
            Assert.False(decision.Create);
            Assert.Contains("cancelled", decision.Reason);
        }
    }
}
