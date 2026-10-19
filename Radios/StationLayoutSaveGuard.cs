// Junkbox #777 — whose radio is it, asked before the station is saved to it.
//
// A global profile is STATION state: one per radio, loaded by everyone who
// connects. Saving the station layout writes it. On a radio the operator does
// not own — Don's 6300 over SmartLink, say, with nobody else on it — the save
// used to go through and replace Don's profile with the guest's layout, which
// Don's radio then loaded for Don on his next connect. The blocker refused for
// four reasons and ownership was not one of them. Same shape as #590 (a guest
// connect padding the owner's slices) and #397 (our port written into Don's
// radio): a write nobody had classified as a write to somebody else's station.
//
// WHY THIS IS A PURE FUNCTION AND NOT FOUR MORE LINES IN FlexBase. The radio
// it protects is three states away and belongs to somebody else, so the
// decision cannot be exercised against the case that matters by constructing a
// FlexBase. Here a test puts an ownership, a hold, a roster verdict and a
// profile name in and reads the refusal out — the ProfileStewardship pattern.
// FlexBase keeps only the gathering of facts and the sentence for each answer.

using Radios.StationConnect;

namespace Radios
{
    /// <summary>
    /// Why a station-layout save is refused, or <see cref="None"/> when it may
    /// go ahead. One value, the FIRST reason in the order
    /// <see cref="StationLayoutSaveGuard.Decide"/> explains, so the operator
    /// hears one sentence and it is the one that matters most.
    /// </summary>
    public enum StationLayoutSaveRefusal
    {
        /// <summary>The save may go ahead.</summary>
        None = 0,

        /// <summary>No radio, or not connected to it.</summary>
        NoRadio,

        /// <summary>The operator has declared this radio someone else's.</summary>
        NotOurRadio,

        /// <summary>The ownership question has never been answered for this
        /// radio. Refuses, in the same fail-safe direction as
        /// <see cref="RosterUnknown"/>: refusing costs one retry after a trip to
        /// Settings; guessing wrong costs somebody their layout.</summary>
        OwnershipUnknown,

        /// <summary>The change-nothing hold is armed for this radio.</summary>
        ChangeNothing,

        /// <summary>Another operator is on the radio right now; the save would
        /// store their slices too.</summary>
        OthersPresent,

        /// <summary>Whether another operator is on cannot yet be told.</summary>
        RosterUnknown,

        /// <summary>The radio has no global profile loaded to save into.</summary>
        NoGlobalProfile,
    }

    /// <summary>
    /// The decision behind <c>FlexBase.StationLayoutSaveBlocker()</c> and the
    /// ownership half of <c>FlexBase.SaveProfile</c>'s global-write funnel.
    /// </summary>
    public static class StationLayoutSaveGuard
    {
        /// <summary>
        /// The first reason the station layout may not be saved, or
        /// <see cref="StationLayoutSaveRefusal.None"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>The order, and why.</b> More than one reason is often true
        /// at once — a guest radio commonly has the hold armed and its owner on
        /// it — and the operator hears exactly one sentence, so the order
        /// decides what they are told. The rule: say the reason that cannot be
        /// removed by acting on a later one.</para>
        /// <list type="number">
        /// <item><b>No radio.</b> Nothing else can be true of a radio that is
        /// not there.</item>
        /// <item><b>Ownership</b> — declared someone else's, or never answered.
        /// Ahead of the hold because the hold is a setting on the same Settings
        /// tab that the operator could lift and try again, and would then hit
        /// this; ahead of the roster because the roster sentence says "try
        /// again in a moment" or "their setup as well as yours", both of which
        /// imply the save would be fine once the other operator leaves — and on
        /// a radio that is not yours it would not be. Whose radio it is comes
        /// before what you asked us to do on it. The Unknown case refuses too,
        /// and its sentence names where the question is answered (#638: an
        /// owner can sit at an unanswered question for weeks without knowing
        /// one was asked), because a refusal that cannot be acted on is a dead
        /// end.</item>
        /// <item><b>The change-nothing hold.</b> The operator's own standing
        /// order about this radio, so the sentence names it (the reasoning
        /// Sprint 33 Track K gave, unchanged, now second rather than
        /// first).</item>
        /// <item><b>Another operator present</b>, then <b>roster unknown</b>.
        /// Transient facts about this moment; the load-bearing MultiFlex
        /// refusal, which under Mine ownership is the case that remains.</item>
        /// <item><b>No global profile loaded.</b> There is nothing to save
        /// into, which only matters once everything above is clear.</item>
        /// </list>
        /// </remarks>
        public static StationLayoutSaveRefusal Decide(
            bool connected,
            RadioOwnership ownership,
            bool holdArmed,
            RosterVerdict presence,
            bool hasGlobalProfile)
        {
            if (!connected) return StationLayoutSaveRefusal.NoRadio;

            var owner = OwnershipRefusal(ownership);
            if (owner != StationLayoutSaveRefusal.None) return owner;

            if (holdArmed) return StationLayoutSaveRefusal.ChangeNothing;

            if (presence == RosterVerdict.OthersPresent) return StationLayoutSaveRefusal.OthersPresent;
            if (presence != RosterVerdict.OnlyUs) return StationLayoutSaveRefusal.RosterUnknown;

            if (!hasGlobalProfile) return StationLayoutSaveRefusal.NoGlobalProfile;

            return StationLayoutSaveRefusal.None;
        }

        /// <summary>
        /// The ownership half alone, for the funnels every global-profile
        /// write passes through — the named save and, since round two, the
        /// delete: neither has a loaded-profile or layout question, but both
        /// change the same shared state and ask the same owner. <see cref="RadioOwnership.Mine"/> is
        /// the only answer that returns <see cref="StationLayoutSaveRefusal.None"/>;
        /// Unset and SomeoneElses refuse, and any value this enum grows later
        /// refuses until somebody decides otherwise here.
        /// </summary>
        public static StationLayoutSaveRefusal OwnershipRefusal(RadioOwnership ownership)
        {
            switch (ownership)
            {
                case RadioOwnership.Mine: return StationLayoutSaveRefusal.None;
                case RadioOwnership.SomeoneElses: return StationLayoutSaveRefusal.NotOurRadio;
                default: return StationLayoutSaveRefusal.OwnershipUnknown;
            }
        }
    }
}
