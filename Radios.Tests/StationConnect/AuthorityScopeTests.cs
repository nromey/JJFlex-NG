using System;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// The roster relaxation is scoped to the owner ruling (Track G2
    /// re-review, section 4), and the two startup station-global writes
    /// have a gate (section 2). Pure rules over the production guard.
    /// </summary>
    public sealed class AuthorityScopeTests
    {
        private static RosterSnapshot OneIdentityBearingHandle()
        {
            var clock = new FakeStationClock();
            var roster = new RosterTracker(clock);
            roster.Reset(1);
            roster.ClientAdded(new RosterEntry(StationHarness.OurHandle, "our-client-id", true, "K5NER", "JJFlex"), 1);
            roster.OwnHandleEstablished(StationHarness.OurHandle, 1);
            return roster.Snapshot();
        }

        [Fact]
        public void TheSameOneHandleSnapshot_IsOnlyUsForTheOwner_AndUnknownForTheGuest()
        {
            var snapshot = OneIdentityBearingHandle();
            var policies = StationPolicies.Defaults();

            var owner = RosterGuard.ForAutomaticWrite(snapshot, policies.RosterAuthority);
            var guest = RosterGuard.ForAutomaticWrite(snapshot, policies.GuestSharedWriteAuthority);

            Assert.Equal(RosterVerdict.OnlyUs, owner.Verdict);
            Assert.Equal(RosterVerdict.Unknown, guest.Verdict);
            Assert.False(guest.MayChange, "Unknown by policy: waiting cannot change it");
            Assert.Contains("authority is not established", guest.Reason);
        }

        [Fact]
        public void ATestPositiveControl_CanOpenTheGuestAuthority_ProductionDoesNot()
        {
            var snapshot = OneIdentityBearingHandle();
            var opened = new StationPolicies { GuestSharedWriteAuthority = RosterAuthorityByLiveMembershipPolicy.Instance };
            Assert.Equal(RosterVerdict.OnlyUs, RosterGuard.ForAutomaticWrite(snapshot, opened.GuestSharedWriteAuthority).Verdict);
            Assert.Same(RosterAuthorityUnknownPolicy.Instance, StationPolicies.Current.GuestSharedWriteAuthority);
        }

        // ── the owner's station-global operating writes: TNF, shack-speaker mute ──

        private static StationPolicyFacts Owner() => new StationPolicyFacts
        {
            Connected = true, Ownership = RadioOwnership.Mine, Intent = ProfileGuestIntent.LoadMineAndPutBack, Serial = "1234",
        };

        private static RosterJudgement OnlyUs() => new RosterJudgement(RosterVerdict.OnlyUs, "test", 1);

        [Fact]
        public void TheOwnerAloneOnTheirRadio_MayMakeTheWrite()
        {
            Assert.Null(OwnerSharedWriteGate.Refusal(Owner(), OnlyUs()));
        }

        [Fact]
        public void TheIntentIsNotConsulted_ItIsAnOperatingWrite_NotAProfileChoice()
        {
            var f = Owner();
            f.Intent = ProfileGuestIntent.LeaveAlone;
            Assert.Null(OwnerSharedWriteGate.Refusal(f, OnlyUs()));
        }

        [Theory]
        [InlineData("hold")]
        [InlineData("guest")]
        [InlineData("unset")]
        [InlineData("company")]
        [InlineData("unknown roster")]
        [InlineData("not connected")]
        public void AnythingElse_Refuses(string what)
        {
            var f = Owner();
            var roster = OnlyUs();
            switch (what)
            {
                case "hold": f.HoldArmed = true; break;
                case "guest": f.Ownership = RadioOwnership.SomeoneElses; break;
                case "unset": f.Ownership = RadioOwnership.Unset; break;
                case "company": roster = new RosterJudgement(RosterVerdict.OthersPresent, "W1AW", 1); break;
                case "unknown roster": roster = new RosterJudgement(RosterVerdict.Unknown, "not established", 1); break;
                case "not connected": f.Connected = false; break;
            }
            Assert.NotNull(OwnerSharedWriteGate.Refusal(f, roster));
        }
    }
}
