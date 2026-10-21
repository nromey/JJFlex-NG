using Radios.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Junkbox #777: the decision behind the station-layout save. Not ours
    /// refuses, unknown refuses, ours still saves — and when more than one
    /// reason is true, the operator hears the one that cannot be removed by
    /// acting on a later one.
    /// </summary>
    /// <remarks>
    /// <para>Shown reachable on 2026-10-19 by the mutation this file's
    /// companion wiring tests cannot make: with the two ownership lines in
    /// <c>StationLayoutSaveGuard.Decide</c> commented out, every test here
    /// that names <c>NotOurRadio</c> or <c>OwnershipUnknown</c> went red and
    /// nothing else moved. The wiring tests in
    /// <see cref="StationLayoutSaveOwnershipWiringTests"/> were red on the
    /// base commit instead, because this type did not exist there.</para>
    /// </remarks>
    public sealed class StationLayoutSaveGuardTests
    {
        private static StationLayoutSaveRefusal Decide(
            RadioOwnership ownership,
            bool connected = true,
            bool holdArmed = false,
            RosterVerdict presence = RosterVerdict.OnlyUs,
            bool hasGlobalProfile = true)
            => StationLayoutSaveGuard.Decide(connected, ownership, holdArmed, presence, hasGlobalProfile);

        // ------------------------------------------------------------------
        // The three answers the brief asks for
        // ------------------------------------------------------------------

        [Fact]
        public void ARadioDeclaredSomeoneElsesRefuses()
        {
            Assert.Equal(StationLayoutSaveRefusal.NotOurRadio, Decide(RadioOwnership.SomeoneElses));
        }

        [Fact]
        public void ARadioWhoseOwnershipWasNeverAnsweredRefuses()
        {
            // #638: Don's own radio sat here for weeks. Still refuses — the
            // sentence tells him where to answer — because a guess the other
            // way writes a guest's layout into an owner's profile.
            Assert.Equal(StationLayoutSaveRefusal.OwnershipUnknown, Decide(RadioOwnership.Unset));
        }

        [Fact]
        public void OurOwnRadioAloneWithAProfileLoadedStillSaves()
        {
            Assert.Equal(StationLayoutSaveRefusal.None, Decide(RadioOwnership.Mine));
        }

        [Fact]
        public void AnOwnershipValueThisCodeDoesNotKnowRefuses()
        {
            // The enum may grow. Anything that is not Mine is not a yes.
            Assert.Equal(StationLayoutSaveRefusal.OwnershipUnknown, Decide((RadioOwnership)99));
        }

        // ------------------------------------------------------------------
        // The funnel's half, used by SaveProfile for a named global save
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(RadioOwnership.Mine, StationLayoutSaveRefusal.None)]
        [InlineData(RadioOwnership.SomeoneElses, StationLayoutSaveRefusal.NotOurRadio)]
        [InlineData(RadioOwnership.Unset, StationLayoutSaveRefusal.OwnershipUnknown)]
        public void TheOwnershipHalfAgreesWithTheWholeDecision(RadioOwnership ownership, StationLayoutSaveRefusal expected)
        {
            Assert.Equal(expected, StationLayoutSaveGuard.OwnershipRefusal(ownership));
            Assert.Equal(expected, Decide(ownership));
        }

        // ------------------------------------------------------------------
        // The order: the sentence the operator hears when several are true
        // ------------------------------------------------------------------

        [Fact]
        public void NoRadioComesBeforeEverything()
        {
            Assert.Equal(StationLayoutSaveRefusal.NoRadio,
                Decide(RadioOwnership.SomeoneElses, connected: false, holdArmed: true,
                    presence: RosterVerdict.OthersPresent, hasGlobalProfile: false));
        }

        [Fact]
        public void OwnershipComesBeforeTheHold()
        {
            // The common guest configuration: hold armed on a radio that is not
            // ours. Hearing "change nothing is on" would send the operator to
            // lift the hold and try again, to be refused for the real reason
            // on the second attempt.
            Assert.Equal(StationLayoutSaveRefusal.NotOurRadio,
                Decide(RadioOwnership.SomeoneElses, holdArmed: true));
            Assert.Equal(StationLayoutSaveRefusal.OwnershipUnknown,
                Decide(RadioOwnership.Unset, holdArmed: true));
        }

        [Fact]
        public void OwnershipComesBeforeTheRoster()
        {
            // The owner on their own radio while a guest tries to save: the
            // roster sentence says "their setup as well as yours", which
            // implies the save would be fine once the owner leaves. It would
            // not be.
            Assert.Equal(StationLayoutSaveRefusal.NotOurRadio,
                Decide(RadioOwnership.SomeoneElses, presence: RosterVerdict.OthersPresent));
            Assert.Equal(StationLayoutSaveRefusal.OwnershipUnknown,
                Decide(RadioOwnership.Unset, presence: RosterVerdict.Unknown));
        }

        [Fact]
        public void OwnershipComesBeforeTheMissingProfile()
        {
            Assert.Equal(StationLayoutSaveRefusal.NotOurRadio,
                Decide(RadioOwnership.SomeoneElses, hasGlobalProfile: false));
        }

        [Fact]
        public void OnOurOwnRadioTheHoldComesBeforeTheRoster()
        {
            // Sprint 33 Track K's reasoning, unchanged: the hold is the
            // operator's own standing order about this radio.
            Assert.Equal(StationLayoutSaveRefusal.ChangeNothing,
                Decide(RadioOwnership.Mine, holdArmed: true, presence: RosterVerdict.OthersPresent));
        }

        [Fact]
        public void OnOurOwnRadioAnotherOperatorRefusesAndAnUnknownRosterRefuses()
        {
            Assert.Equal(StationLayoutSaveRefusal.OthersPresent,
                Decide(RadioOwnership.Mine, presence: RosterVerdict.OthersPresent));
            Assert.Equal(StationLayoutSaveRefusal.RosterUnknown,
                Decide(RadioOwnership.Mine, presence: RosterVerdict.Unknown));
        }

        [Fact]
        public void OnOurOwnRadioTheRosterComesBeforeTheMissingProfile()
        {
            Assert.Equal(StationLayoutSaveRefusal.OthersPresent,
                Decide(RadioOwnership.Mine, presence: RosterVerdict.OthersPresent, hasGlobalProfile: false));
            Assert.Equal(StationLayoutSaveRefusal.NoGlobalProfile,
                Decide(RadioOwnership.Mine, hasGlobalProfile: false));
        }
    }
}
