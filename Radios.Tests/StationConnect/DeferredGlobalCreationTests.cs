using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// #578, design step 11: the disconnect-time create of a global that was
    /// definitely missing at connect, as a pure rule with every condition
    /// named. Design section 6, group 10 (the decision half; the readback
    /// half needs a radio and is pinned by source in StationFirstWiringTests).
    /// </summary>
    public sealed class DeferredGlobalCreationTests
    {
        private static CreationFacts Clean(ConnectionAttempt attempt)
        {
            var pending = new PendingGlobalCreation("K5NER-8600", "1234", attempt.Generation, "absent at connect");
            return new CreationFacts
            {
                Pending = pending,
                Attempt = attempt,
                Connected = true,
                HoldArmed = false,
                Ownership = RadioOwnership.Mine,
                Intent = ProfileGuestIntent.LoadMineAndPutBack,
                WantedGlobalNow = "K5NER-8600",
                Serial = "1234",
                Roster = new RosterJudgement(RosterVerdict.OnlyUs, "only us", 1),
                Inventory = new InventoryObservation(new[] { "Default" }, ObservationProvenance.RadioReported, 5, attempt.Generation, 0),
                StationOutcome = StationOutcome.FreshStationConfirmed,
                LoadOutstanding = false,
            };
        }

        [Fact]
        public void TheCleanDisconnect_Creates()
        {
            var attempt = new ConnectionAttempt("1234");
            var d = DeferredGlobalCreation.Decide(Clean(attempt));
            Assert.True(d.Create, d.Reason);
        }

        [Fact]
        public void NothingPending_DoesNotCreate()
        {
            Assert.False(DeferredGlobalCreation.Decide(null).Create);
            var f = Clean(new ConnectionAttempt("1234"));
            f.Pending = null;
            Assert.False(DeferredGlobalCreation.Decide(f).Create);
        }

        [Fact]
        public void CrashEquivalentAbandonment_ADifferentAttempt_DoesNotCreate()
        {
            var first = new ConnectionAttempt("1234");
            var f = Clean(first);
            f.Attempt = new ConnectionAttempt("1234"); // the next connection, same serial
            var d = DeferredGlobalCreation.Decide(f);
            Assert.False(d.Create);
            Assert.Contains("different connection attempt", d.Reason);
        }

        [Fact]
        public void NotConnected_DoesNotCreate()
        {
            var f = Clean(new ConnectionAttempt("1234"));
            f.Connected = false;
            Assert.False(DeferredGlobalCreation.Decide(f).Create);
        }

        [Fact]
        public void TheHold_DoesNotCreate()
        {
            var f = Clean(new ConnectionAttempt("1234"));
            f.HoldArmed = true;
            Assert.Contains("hold", DeferredGlobalCreation.Decide(f).Reason);
        }

        [Theory]
        [InlineData(RadioOwnership.Unset)]
        [InlineData(RadioOwnership.SomeoneElses)]
        public void NotOurs_DoesNotCreate(RadioOwnership ownership)
        {
            var f = Clean(new ConnectionAttempt("1234"));
            f.Ownership = ownership;
            Assert.False(DeferredGlobalCreation.Decide(f).Create);
        }

        [Theory]
        [InlineData(ProfileGuestIntent.NotAnswered)]
        [InlineData(ProfileGuestIntent.LeaveAlone)]
        [InlineData(ProfileGuestIntent.UseMyTransmitAudio)]
        public void NotOptedIn_DoesNotCreate(ProfileGuestIntent intent)
        {
            var f = Clean(new ConnectionAttempt("1234"));
            f.Intent = intent;
            Assert.False(DeferredGlobalCreation.Decide(f).Create);
        }

        [Fact]
        public void NameChangedBeforeSave_DoesNotCreate_AndDoesNotChaseTheNewName()
        {
            var f = Clean(new ConnectionAttempt("1234"));
            f.WantedGlobalNow = "SO2RDefault";
            var d = DeferredGlobalCreation.Decide(f);
            Assert.False(d.Create);
            Assert.Contains("name changed", d.Reason);
        }

        [Fact]
        public void ADifferentRadio_DoesNotCreate()
        {
            var f = Clean(new ConnectionAttempt("1234"));
            f.Serial = "9999";
            Assert.False(DeferredGlobalCreation.Decide(f).Create);
        }

        [Theory]
        [InlineData(RosterVerdict.OthersPresent)]
        [InlineData(RosterVerdict.Unknown)]
        public void CompanyOrUncertainty_DoesNotCreate(RosterVerdict verdict)
        {
            var f = Clean(new ConnectionAttempt("1234"));
            f.Roster = new RosterJudgement(verdict, "x", 1);
            Assert.False(DeferredGlobalCreation.Decide(f).Create);
        }

        [Theory]
        [InlineData(StationOutcome.Unconfirmed)]
        [InlineData(StationOutcome.Failed)]
        [InlineData(StationOutcome.Cancelled)]
        [InlineData(StationOutcome.PolicySkipped)]
        [InlineData(StationOutcome.RestoredConfirmed)]
        [InlineData(StationOutcome.ExistingStationConfirmed)]
        public void AnythingButAConfirmedFreshStation_DoesNotSaveAPartialOrAmbiguousLayout(StationOutcome outcome)
        {
            var f = Clean(new ConnectionAttempt("1234"));
            f.StationOutcome = outcome;
            Assert.False(DeferredGlobalCreation.Decide(f).Create);
        }

        [Fact]
        public void AnOutstandingLoad_DoesNotCreate()
        {
            var f = Clean(new ConnectionAttempt("1234"));
            f.LoadOutstanding = true;
            Assert.False(DeferredGlobalCreation.Decide(f).Create);
        }

        [Fact]
        public void TheTargetAppearingBeforeSave_NeverOverwrites()
        {
            var f = Clean(new ConnectionAttempt("1234"));
            f.Inventory = new InventoryObservation(new[] { "Default", "K5NER-8600" }, ObservationProvenance.RadioReported, 9, f.Attempt.Generation, 0);
            var d = DeferredGlobalCreation.Decide(f);
            Assert.False(d.Create);
            Assert.Contains("never overwrite", d.Reason);
        }

        [Fact]
        public void AnInventoryNeverReported_DoesNotEstablishAbsence()
        {
            var f = Clean(new ConnectionAttempt("1234"));
            f.Inventory = null;
            Assert.Contains("never reported", DeferredGlobalCreation.Decide(f).Reason);
            f.Inventory = new InventoryObservation(new[] { "Default" }, ObservationProvenance.LocalEcho, 9, f.Attempt.Generation, 0);
            Assert.False(DeferredGlobalCreation.Decide(f).Create);
        }
    }
}
