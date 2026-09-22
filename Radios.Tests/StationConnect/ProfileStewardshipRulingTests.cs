using System.Linq;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// Group 1 additions: the pure layer under the 2026-09-21 ruling (#590),
    /// the phase-limited plan, the RosterUnknown refusal, and the
    /// StationPresent default the #563 review asked for.
    /// </summary>
    public sealed class ProfileStewardshipRulingTests
    {
        private static ProfileTypeState Type(ProfileTypes type, string selection = "Default", string wanted = "K5NER",
            string[] names = null, bool reported = true)
            => new ProfileTypeState
            {
                ProfileType = type,
                Reported = reported,
                Names = (names ?? new[] { "Default", "K5NER" }).ToList(),
                Selection = selection,
                Wanted = wanted,
            };

        private static ProfileSituation Situation(RadioOwnership ownership, ProfileGuestIntent intent = ProfileGuestIntent.LoadMineAndPutBack)
        {
            var s = new ProfileSituation
            {
                Ownership = ownership,
                Intent = intent,
                OnlyStation = true,
                StationPresent = false,
                RadioAutosave = true,
                LocalTransmitAudioProfile = "Studio",
                LocalTransmitAudioProfileExists = true,
            };
            foreach (var t in ProfileStewardship.GovernedTypes) s.Types.Add(Type(t));
            return s;
        }

        [Fact]
        public void AProfileSituation_DefaultsToNoStation()
        {
            // The #563 review's request: the test helper always set it, so a
            // production default of true would have passed every test.
            Assert.False(new ProfileSituation().StationPresent);
        }

        // ── the ruling ──

        [Theory]
        [InlineData(RadioOwnership.Unset)]
        [InlineData(RadioOwnership.SomeoneElses)]
        public void ANonOwnerOptedIn_HasEveryNamedLoadReplacedByTheRulingSkip_AndNoPutBackRecord(RadioOwnership ownership)
        {
            var plan = ProfileStewardship.PlanConnectRuled(Situation(ownership));

            Assert.DoesNotContain(plan.Actions, a => a.Kind == ProfileActionKind.LoadOurs);
            Assert.Empty(plan.Actions);
            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                Assert.True(plan.Skipped(t, ProfileSkipReason.NotTheDeclaredOwner), t.ToString());
            }
            Assert.Empty(plan.Record);
        }

        [Fact]
        public void TheOwnerOptedIn_StillGetsTheLoads_ThePositiveControl()
        {
            var plan = ProfileStewardship.PlanConnectRuled(Situation(RadioOwnership.Mine));

            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                Assert.Single(plan.Actions, a => a.Kind == ProfileActionKind.LoadOurs && a.ProfileType == t);
            }
            Assert.DoesNotContain(plan.Skips, s => s.Reason == ProfileSkipReason.NotTheDeclaredOwner);
        }

        [Fact]
        public void TheRulingLeavesThePreservedGuestPathAlone()
        {
            // UseMyTransmitAudio on a radio that is not ours: autosave off,
            // capture, apply, and the live record — exactly as before.
            var plan = ProfileStewardship.PlanConnectRuled(
                Situation(RadioOwnership.SomeoneElses, ProfileGuestIntent.UseMyTransmitAudio));

            Assert.Equal(new[]
            {
                ProfileActionKind.TurnAutosaveOff,
                ProfileActionKind.CaptureLiveTransmitAudio,
                ProfileActionKind.ApplyLocalTransmitAudio,
            }, plan.Actions.Select(a => a.Kind).ToArray());
            var rec = Assert.Single(plan.Record);
            Assert.True(rec.LiveTransmitAudio);
            Assert.DoesNotContain(plan.Skips, s => s.Reason == ProfileSkipReason.NotTheDeclaredOwner);
        }

        [Fact]
        public void TheUnruledPlannerStillPlansTheGuestLoad_SoTheRulingIsTheComposition()
        {
            // PlanConnect's per-type rules are unchanged; the ruling is applied
            // on top. If this stops holding, either rewrite the guest tests in
            // ProfileStewardshipTests or this one, deliberately.
            var plain = ProfileStewardship.PlanConnect(Situation(RadioOwnership.Unset));
            Assert.Contains(plain.Actions, a => a.Kind == ProfileActionKind.LoadOurs);
        }

        // ── phase limiting ──

        [Fact]
        public void TheGlobalPhase_DecidesOnlyTheGlobal()
        {
            var plan = ProfileStewardship.PlanConnectRuled(Situation(RadioOwnership.Mine), new[] { ProfileTypes.global });
            Assert.All(plan.Actions, a => Assert.Equal(ProfileTypes.global, a.ProfileType));
            Assert.All(plan.Skips, s => Assert.Equal(ProfileTypes.global, s.ProfileType));
            Assert.Single(plan.Actions);
        }

        [Fact]
        public void ThePostStationPhase_DecidesTransmitAndMicrophone_NeverTheGlobal()
        {
            var plan = ProfileStewardship.PlanConnectRuled(Situation(RadioOwnership.Mine), ProfileStewardship.TransmitAudioTypes);
            Assert.DoesNotContain(plan.Actions, a => a.ProfileType == ProfileTypes.global);
            Assert.DoesNotContain(plan.Skips, s => s.ProfileType == ProfileTypes.global);
            Assert.Equal(2, plan.Actions.Count);
        }

        [Fact]
        public void AutosaveActionsTravelWithTheTransmitPhase()
        {
            var plan = ProfileStewardship.PlanConnectRuled(
                Situation(RadioOwnership.SomeoneElses, ProfileGuestIntent.UseMyTransmitAudio),
                ProfileStewardship.TransmitAudioTypes);
            Assert.Contains(plan.Actions, a => a.Kind == ProfileActionKind.TurnAutosaveOff);

            var globalOnly = ProfileStewardship.PlanConnectRuled(
                Situation(RadioOwnership.SomeoneElses, ProfileGuestIntent.UseMyTransmitAudio),
                new[] { ProfileTypes.global });
            Assert.Empty(globalOnly.Actions);
        }

        // ── uncertainty is named, not announced as a person ──

        [Fact]
        public void AnUnknownRoster_RefusesAsRosterUnknown_NotAsAnotherOperator()
        {
            var s = Situation(RadioOwnership.Mine);
            s.OnlyStation = false;
            s.OnlyStationUnknown = true;

            var plan = ProfileStewardship.PlanConnect(s);

            Assert.Empty(plan.Actions);
            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                Assert.True(plan.Skipped(t, ProfileSkipReason.RosterUnknown));
                Assert.False(plan.Skipped(t, ProfileSkipReason.AnotherOperatorIsConnected));
            }
        }

        [Fact]
        public void AKnownOtherOperator_StillRefusesAsAnotherOperator()
        {
            var s = Situation(RadioOwnership.Mine);
            s.OnlyStation = false;
            var plan = ProfileStewardship.PlanConnect(s);
            Assert.True(plan.Skipped(ProfileTypes.global, ProfileSkipReason.AnotherOperatorIsConnected));
        }

        [Fact]
        public void TheLiveAudioPath_AlsoNamesUncertainty()
        {
            var s = Situation(RadioOwnership.SomeoneElses, ProfileGuestIntent.UseMyTransmitAudio);
            s.OnlyStation = false;
            s.OnlyStationUnknown = true;
            var plan = ProfileStewardship.PlanConnect(s);
            Assert.True(plan.Skipped(ProfileTypes.tx, ProfileSkipReason.RosterUnknown));
        }

        // ── the shared predicate ──

        [Theory]
        [InlineData(true, false, RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack, true)]
        [InlineData(false, false, RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack, false)]
        [InlineData(true, true, RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack, false)]
        [InlineData(true, false, RadioOwnership.Unset, ProfileGuestIntent.LoadMineAndPutBack, false)]
        [InlineData(true, false, RadioOwnership.SomeoneElses, ProfileGuestIntent.LoadMineAndPutBack, false)]
        [InlineData(true, false, RadioOwnership.Mine, ProfileGuestIntent.NotAnswered, false)]
        [InlineData(true, false, RadioOwnership.Mine, ProfileGuestIntent.LeaveAlone, false)]
        [InlineData(true, false, RadioOwnership.Mine, ProfileGuestIntent.UseMyTransmitAudio, false)]
        public void MayWriteSharedStateAutomatically_IsExactlyTheOwnersOptedInUnheldConnection(
            bool connected, bool hold, RadioOwnership ownership, ProfileGuestIntent intent, bool expected)
        {
            var s = new ProfileSituation { Connected = connected, ChangeNothingArmed = hold, Ownership = ownership, Intent = intent };
            Assert.Equal(expected, ProfileStewardship.MayWriteSharedStateAutomatically(s));
        }

        // ── the coordinator's refusal sentence agrees with the predicate ──

        [Fact]
        public void TheCoordinatorsRefusal_AndThePredicate_Agree()
        {
            foreach (RadioOwnership o in System.Enum.GetValues(typeof(RadioOwnership)))
            foreach (ProfileGuestIntent i in System.Enum.GetValues(typeof(ProfileGuestIntent)))
            foreach (bool hold in new[] { false, true })
            {
                var facts = new StationPolicyFacts { Connected = true, HoldArmed = hold, Ownership = o, Intent = i, WantedGlobal = "K5NER" };
                var s = new ProfileSituation { Connected = true, ChangeNothingArmed = hold, Ownership = o, Intent = i };
                bool permitted = StationCoordinator.AutomaticStewardshipRefusal(facts) == null;
                Assert.Equal(ProfileStewardship.MayWriteSharedStateAutomatically(s), permitted);
            }
        }
    }
}
