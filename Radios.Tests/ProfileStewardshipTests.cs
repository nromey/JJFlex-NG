using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #450 and #451: JJ Flexible must stop changing profiles on radios it does
    /// not own, and must put back what it does change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this file is the deliverable and not just a check on it.</b> The
    /// radio these decisions protect is three states away and belongs to
    /// somebody else. It cannot be connected to for testing, it must not be
    /// experimented on, and the symptom of a wrong answer is that its owner
    /// picks up his hand microphone one evening and transmits through settings
    /// meant for a stranger's studio interface, with nothing having told him.
    /// There is no feedback loop there at all. So every decision lives in pure
    /// functions and every one of them is pinned here: a radio state goes in,
    /// an action list comes out, and no radio, window or thread is involved.
    /// </para>
    /// <para>
    /// The shape follows <see cref="TransmitSafety"/> and its tests, for the
    /// same reason that class exists.
    /// </para>
    /// </remarks>
    public sealed class ProfileStewardshipTests
    {
        // ------------------------------------------------------------------
        // Builders. Everything a test needs to say, said in one line.
        // ------------------------------------------------------------------

        private static ProfileTypeState Type(
            ProfileTypes type,
            string selection = "Default",
            string wanted = "K5NER",
            IEnumerable<string> names = null,
            bool reported = true,
            bool unsaved = false)
        {
            return new ProfileTypeState
            {
                ProfileType = type,
                Reported = reported,
                Names = (names ?? new[] { "Default", "K5NER" }).ToList(),
                Selection = selection,
                UnsavedChanges = unsaved,
                Wanted = wanted,
            };
        }

        private static ProfileSituation Situation(
            ProfileGuestIntent intent = ProfileGuestIntent.LoadMineAndPutBack,
            RadioOwnership ownership = RadioOwnership.Unset,
            bool changeNothing = false,
            bool onlyStation = true,
            bool connected = true,
            params ProfileTypeState[] types)
        {
            var s = new ProfileSituation
            {
                Intent = intent,
                Ownership = ownership,
                ChangeNothingArmed = changeNothing,
                OnlyStation = onlyStation,
                Connected = connected,
            };
            if (types.Length == 0)
            {
                foreach (var t in ProfileStewardship.GovernedTypes) s.Types.Add(Type(t));
            }
            else
            {
                s.Types.AddRange(types);
            }
            return s;
        }

        private static ProfileAction Only(ProfilePlan plan, ProfileActionKind kind, ProfileTypes type) =>
            plan.Actions.Single(a => a.Kind == kind && a.ProfileType == type);

        // ==================================================================
        // THE HEADLINE: the first connect to a radio changes NOTHING
        // ==================================================================

        [Fact]
        public void ARadioNeverAnsweredFor_IsNotTouchedAtAll()
        {
            var plan = ProfileStewardship.PlanConnect(
                Situation(intent: ProfileGuestIntent.NotAnswered));

            Assert.True(plan.ChangesNothing);
            Assert.Empty(plan.Actions);
            Assert.Empty(plan.Record);
            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                Assert.True(plan.Skipped(t, ProfileSkipReason.NotOptedIn));
            }
        }

        [Fact]
        public void ARadioNeverAnsweredFor_RaisesTheQuestionOnce()
        {
            var plan = ProfileStewardship.PlanConnect(
                Situation(intent: ProfileGuestIntent.NotAnswered));

            Assert.True(plan.AskWhoseRadioThisIs);
        }

        [Fact]
        public void OwnershipAloneDoesNotOptARadioIn()
        {
            // The inference this whole area exists to refuse. "This radio is
            // mine" answers whether things may be CREATED here; it does not
            // answer "load my profiles here and put the radio's back", which
            // has a different blast radius. A radio marked Mine and never
            // answered for is still left alone.
            var plan = ProfileStewardship.PlanConnect(
                Situation(intent: ProfileGuestIntent.NotAnswered,
                          ownership: RadioOwnership.Mine));

            Assert.True(plan.ChangesNothing);
            Assert.True(plan.AskWhoseRadioThisIs);
            // …but it is the one case worth pre-selecting "yes" for.
            Assert.Equal(ProfileGuestIntent.LoadMineAndPutBack, plan.Suggestion);
        }

        [Fact]
        public void ARadioMarkedSomeoneElses_SuggestsLeaveAlone()
        {
            var plan = ProfileStewardship.PlanConnect(
                Situation(intent: ProfileGuestIntent.NotAnswered,
                          ownership: RadioOwnership.SomeoneElses));

            Assert.Equal(ProfileGuestIntent.LeaveAlone, plan.Suggestion);
        }

        [Fact]
        public void ARadioTheOperatorSaidToLeaveAlone_IsNotAskedAgain()
        {
            var plan = ProfileStewardship.PlanConnect(
                Situation(intent: ProfileGuestIntent.LeaveAlone));

            Assert.True(plan.ChangesNothing);
            Assert.False(plan.AskWhoseRadioThisIs);
        }

        // ==================================================================
        // THE POSITIVE CONTROL. Without this, every test above passes on a
        // function that returns an empty plan for everything.
        // ==================================================================

        [Fact]
        public void AnOptedInRadio_CapturesFirstAndLoadsSecond()
        {
            var plan = ProfileStewardship.PlanConnect(Situation());

            Assert.NotEmpty(plan.Actions);
            foreach (var type in ProfileStewardship.GovernedTypes)
            {
                var capture = Only(plan, ProfileActionKind.CaptureRestorePoint, type);
                var load = Only(plan, ProfileActionKind.LoadOurs, type);

                // ORDER IS THE SAFETY PROPERTY. A restore point captured after
                // the state it records has been overwritten holds OUR settings,
                // which is worse than no restore point at all because it looks
                // like a rescue.
                Assert.True(plan.Actions.IndexOf(capture) < plan.Actions.IndexOf(load),
                    "the restore point must be captured before ours is loaded");

                Assert.Equal(ProfileRestorePoints.NameFor(type), capture.ProfileName);
                Assert.Equal("K5NER", load.ProfileName);
            }
        }

        [Fact]
        public void AnOptedInRadio_RecordsTheNameItWasOn()
        {
            var plan = ProfileStewardship.PlanConnect(Situation());

            foreach (var type in ProfileStewardship.GovernedTypes)
            {
                var rec = plan.Record.Single(r => r.ProfileType == type);
                Assert.Equal("Default", rec.TheirSelection);
                Assert.Equal("K5NER", rec.WeLoaded);
                Assert.True(rec.RestorePointLeft);
            }
        }

        // ==================================================================
        // The refusals, one at a time
        // ==================================================================

        [Fact]
        public void UnsavedWorkOnTheRadio_StopsEverythingForThatType()
        {
            // The radio itself reports this. Capturing a restore point over
            // somebody's half-finished microphone edit freezes the unfinished
            // state; applying ours discards it. Both are harm.
            var s = Situation(types: new[]
            {
                Type(ProfileTypes.global),
                Type(ProfileTypes.tx, unsaved: true),
                Type(ProfileTypes.mic),
            });

            var plan = ProfileStewardship.PlanConnect(s);

            Assert.True(plan.Skipped(ProfileTypes.tx, ProfileSkipReason.OwnerHasUnsavedWork));
            Assert.DoesNotContain(plan.Actions, a => a.ProfileType == ProfileTypes.tx);
            // …and the other two are untouched by the refusal.
            Assert.Contains(plan.Actions, a => a.ProfileType == ProfileTypes.mic);
        }

        [Fact]
        public void AnotherOperatorOnTheRadio_StopsEverything()
        {
            var plan = ProfileStewardship.PlanConnect(Situation(onlyStation: false));

            Assert.True(plan.ChangesNothing);
            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                Assert.True(plan.Skipped(t, ProfileSkipReason.AnotherOperatorIsConnected));
            }
        }

        [Fact]
        public void TheChangeNothingHoldOutranksAnOptIn()
        {
            // Your own radio can be the one that must not change today, and
            // the hold is a decision made for a reason that outlives any
            // standing declaration.
            var plan = ProfileStewardship.PlanConnect(
                Situation(intent: ProfileGuestIntent.LoadMineAndPutBack,
                          ownership: RadioOwnership.Mine,
                          changeNothing: true));

            Assert.True(plan.ChangesNothing);
            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                Assert.True(plan.Skipped(t, ProfileSkipReason.ChangeNothingArmed));
            }
        }

        [Fact]
        public void AListTheRadioNeverReported_IsNotAnEmptyList()
        {
            // An absence is not evidence. Without the radio's own inventory we
            // cannot tell whether our profile is there, whether a restore point
            // is already sitting on it, or what we would be capturing.
            var s = Situation(types: new[]
            {
                Type(ProfileTypes.global, reported: false, names: Array.Empty<string>()),
                Type(ProfileTypes.tx),
                Type(ProfileTypes.mic),
            });

            var plan = ProfileStewardship.PlanConnect(s);

            Assert.True(plan.Skipped(ProfileTypes.global,
                ProfileSkipReason.RadioDidNotReportItsList));
            Assert.DoesNotContain(plan.Actions, a => a.ProfileType == ProfileTypes.global);
        }

        [Fact]
        public void AnUnreadableSelection_IsAOneWayDoorAndIsRefused()
        {
            var s = Situation(types: new[]
            {
                Type(ProfileTypes.global, selection: null),
                Type(ProfileTypes.tx),
                Type(ProfileTypes.mic),
            });

            var plan = ProfileStewardship.PlanConnect(s);

            Assert.True(plan.Skipped(ProfileTypes.global,
                ProfileSkipReason.SelectionUnreadable));
            Assert.DoesNotContain(plan.Actions, a => a.ProfileType == ProfileTypes.global);
        }

        [Fact]
        public void WhatWeWantIsAlreadyLoaded_SoNothingIsWrittenAtAll()
        {
            // The best outcome there is: no write, no restore point, nothing
            // to put back, nothing to go wrong.
            var s = Situation(types: new[]
            {
                Type(ProfileTypes.global, selection: "K5NER", wanted: "K5NER"),
                Type(ProfileTypes.tx, selection: "K5NER", wanted: "K5NER"),
                Type(ProfileTypes.mic, selection: "K5NER", wanted: "K5NER"),
            });

            var plan = ProfileStewardship.PlanConnect(s);

            Assert.True(plan.ChangesNothing);
            Assert.Empty(plan.Record);
            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                Assert.True(plan.Skipped(t, ProfileSkipReason.AlreadyLoaded));
            }
        }

        [Fact]
        public void NoPerRadioChoiceAndNoDefault_MeansNothingIsLoaded()
        {
            var s = Situation(types: new[]
            {
                Type(ProfileTypes.global, wanted: ""),
                Type(ProfileTypes.tx, wanted: ""),
                Type(ProfileTypes.mic, wanted: ""),
            });

            var plan = ProfileStewardship.PlanConnect(s);

            Assert.True(plan.ChangesNothing);
            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                Assert.True(plan.Skipped(t, ProfileSkipReason.NothingWanted));
            }
        }

        // ==================================================================
        // Creating a profile is a separate permission from loading one
        // ==================================================================

        [Fact]
        public void AProfileMissingFromSomeoneElsesRadio_IsNotInvented()
        {
            // This is #403's write, refused. The old connect path created the
            // operator's transmit and microphone profiles ON THE RADIO when
            // they were absent, from a list with no radio in it.
            var s = Situation(
                ownership: RadioOwnership.Unset,
                types: new[]
                {
                    Type(ProfileTypes.global, names: new[] { "Default" }),
                    Type(ProfileTypes.tx, names: new[] { "Default" }),
                    Type(ProfileTypes.mic, names: new[] { "Default" }),
                });

            var plan = ProfileStewardship.PlanConnect(s);

            Assert.True(plan.ChangesNothing);
            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                Assert.True(plan.Skipped(t,
                    ProfileSkipReason.ProfileNotOnThisRadioAndNotOurs));
            }
        }

        [Fact]
        public void AProfileMissingFromYourOwnRadio_MayBeCreated()
        {
            var s = Situation(
                ownership: RadioOwnership.Mine,
                types: new[]
                {
                    Type(ProfileTypes.tx, names: new[] { "Default" }),
                });
            s.Types.Add(Type(ProfileTypes.global, wanted: ""));
            s.Types.Add(Type(ProfileTypes.mic, wanted: ""));

            var plan = ProfileStewardship.PlanConnect(s);

            var load = Only(plan, ProfileActionKind.LoadOurs, ProfileTypes.tx);
            Assert.True(load.MayCreate);
        }

        [Fact]
        public void AProfileTheRadioAlreadyHas_IsNeverCreated()
        {
            var s = Situation(ownership: RadioOwnership.Mine);
            var plan = ProfileStewardship.PlanConnect(s);

            foreach (var load in plan.Actions.Where(a => a.Kind == ProfileActionKind.LoadOurs))
            {
                Assert.False(load.MayCreate);
            }
        }

        // ==================================================================
        // A restore point left by an earlier session
        // ==================================================================

        [Fact]
        public void AStrandedRestorePointIsReportedAndNothingIsCapturedOverIt()
        {
            // What is loaded RIGHT NOW is our profile from the session that
            // crashed, not the owner's state. Capturing over the restore point
            // would destroy the only record of what this radio was on.
            var marker = ProfileRestorePoints.NameFor(ProfileTypes.mic);
            var s = Situation(types: new[]
            {
                Type(ProfileTypes.global, wanted: ""),
                Type(ProfileTypes.tx, wanted: ""),
                Type(ProfileTypes.mic, selection: "K5NER", wanted: "K5NER2",
                     names: new[] { "Default", "K5NER", "K5NER2", marker }),
            });

            var plan = ProfileStewardship.PlanConnect(s);

            Assert.Contains(ProfileTypes.mic, plan.StrandedRestorePoints);
            Assert.True(plan.Skipped(ProfileTypes.mic,
                ProfileSkipReason.RestorePointAlreadyPresent));
            Assert.DoesNotContain(plan.Actions, a => a.ProfileType == ProfileTypes.mic);
        }

        [Fact]
        public void AStrandedRestorePointIsReportedEvenUnderTheHold()
        {
            // Finding one is a READ. It is also the single most important
            // thing to tell the operator, so no refusal may suppress it.
            var marker = ProfileRestorePoints.NameFor(ProfileTypes.tx);
            var s = Situation(
                intent: ProfileGuestIntent.LeaveAlone,
                changeNothing: true,
                types: new[]
                {
                    Type(ProfileTypes.global, wanted: ""),
                    Type(ProfileTypes.tx, names: new[] { "Default", marker }),
                    Type(ProfileTypes.mic, wanted: ""),
                });

            var plan = ProfileStewardship.PlanConnect(s);

            Assert.Contains(ProfileTypes.tx, plan.StrandedRestorePoints);
            Assert.True(plan.ChangesNothing);
        }

        [Fact]
        public void NothingRestoresAStrandedRestorePointOnItsOwn()
        {
            // THE RULE THAT PREVENTS THE WORST FAILURE. We crash, the owner
            // reconnects, notices his audio is wrong and fixes it himself, we
            // reconnect — and an automatic restore undoes the repair he just
            // made. A late restore is not obviously safer than none.
            var marker = ProfileRestorePoints.NameFor(ProfileTypes.mic);
            var s = Situation(types: new[]
            {
                Type(ProfileTypes.global, wanted: ""),
                Type(ProfileTypes.tx, wanted: ""),
                Type(ProfileTypes.mic, names: new[] { "Default", "K5NER", marker }),
            });

            var plan = ProfileStewardship.PlanConnect(s);

            Assert.DoesNotContain(plan.Actions,
                a => a.Kind == ProfileActionKind.LoadRestorePoint);
        }

        [Fact]
        public void AnAcceptedOfferDoesRestoreIt()
        {
            // The positive control for the rule above: the same situation
            // restores the moment a human says so, so the refusal is a
            // decision and not a broken path.
            var marker = ProfileRestorePoints.NameFor(ProfileTypes.mic);
            var s = Situation(types: new[]
            {
                Type(ProfileTypes.global, wanted: ""),
                Type(ProfileTypes.tx, wanted: ""),
                Type(ProfileTypes.mic, names: new[] { "Default", "K5NER", marker }),
            });

            var plan = ProfileStewardship.PlanOfferedRestore(s, new[] { ProfileTypes.mic });

            var load = Only(plan, ProfileActionKind.LoadRestorePoint, ProfileTypes.mic);
            Assert.Equal(marker, load.ProfileName);
        }

        [Fact]
        public void AnAcceptedOfferStillRefusesWhileAnotherOperatorIsOn()
        {
            var marker = ProfileRestorePoints.NameFor(ProfileTypes.mic);
            var s = Situation(onlyStation: false, types: new[]
            {
                Type(ProfileTypes.mic, names: new[] { "Default", marker }),
            });

            var plan = ProfileStewardship.PlanOfferedRestore(s, new[] { ProfileTypes.mic });

            Assert.Empty(plan.Actions);
            Assert.True(plan.Skipped(ProfileTypes.mic,
                ProfileSkipReason.AnotherOperatorIsConnected));
        }

        // ==================================================================
        // Putting it back
        // ==================================================================

        private static List<ProfileSessionRecord> RecordFor(params ProfileTypes[] types) =>
            types.Select(t => new ProfileSessionRecord
            {
                ProfileType = t,
                TheirSelection = "Default",
                RestorePointLeft = true,
                WeLoaded = "K5NER",
            }).ToList();

        [Fact]
        public void TheFastPath_LoadsTheirNameBackAndRemovesTheRestorePoint()
        {
            var s = Situation(types: ProfileStewardship.GovernedTypes
                .Select(t => Type(t, selection: "K5NER",
                    names: new[] { "Default", "K5NER", ProfileRestorePoints.NameFor(t) }))
                .ToArray());

            var plan = ProfileStewardship.PlanPutBack(s, RecordFor(ProfileStewardship.GovernedTypes));

            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                var back = Only(plan, ProfileActionKind.LoadTheirNameBack, t);
                var remove = Only(plan, ProfileActionKind.RemoveRestorePoint, t);
                Assert.Equal("Default", back.ProfileName);
                Assert.Equal(ProfileRestorePoints.NameFor(t), remove.ProfileName);
                Assert.True(plan.Actions.IndexOf(back) < plan.Actions.IndexOf(remove),
                    "the radio must be back on its own profile before the restore point goes");
            }
        }

        [Fact]
        public void TheFallback_SelectsTheRestorePointAndKeepsIt()
        {
            // Their profile name is gone from the radio — deleted, renamed, or
            // wiped. The restore point still holds the state, so selecting it
            // IS the restore. It is deliberately not deleted afterwards: its
            // contents are now the live state, and deleting it would leave one
            // copy where there had been two.
            var marker = ProfileRestorePoints.NameFor(ProfileTypes.mic);
            var s = Situation(types: new[]
            {
                Type(ProfileTypes.mic, selection: "K5NER", names: new[] { "K5NER", marker }),
            });

            var plan = ProfileStewardship.PlanPutBack(s, RecordFor(ProfileTypes.mic));

            var load = Only(plan, ProfileActionKind.LoadRestorePoint, ProfileTypes.mic);
            Assert.Equal(marker, load.ProfileName);
            Assert.DoesNotContain(plan.Actions,
                a => a.Kind == ProfileActionKind.RemoveRestorePoint);
        }

        [Fact]
        public void NothingWasChanged_MeansNothingIsPutBack()
        {
            var plan = ProfileStewardship.PlanPutBack(Situation(), null);

            Assert.True(plan.ChangesNothing);
            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                Assert.True(plan.Skipped(t, ProfileSkipReason.NothingWasChanged));
            }
        }

        [Fact]
        public void PuttingBackRefusesWhileAnotherOperatorIsOnTheRadio()
        {
            // Restoring now would change the station under someone using it.
            // The restore point stays where it is, which is what it is for.
            var s = Situation(onlyStation: false, types: ProfileStewardship.GovernedTypes
                .Select(t => Type(t, names: new[] { "Default", ProfileRestorePoints.NameFor(t) }))
                .ToArray());

            var plan = ProfileStewardship.PlanPutBack(s, RecordFor(ProfileStewardship.GovernedTypes));

            Assert.True(plan.ChangesNothing);
            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                Assert.True(plan.Skipped(t, ProfileSkipReason.AnotherOperatorIsConnected));
            }
        }

        [Fact]
        public void PuttingBackRefusesWhenTheRadioReportsUnsavedWork()
        {
            var s = Situation(types: new[]
            {
                Type(ProfileTypes.mic, unsaved: true,
                     names: new[] { "Default", ProfileRestorePoints.NameFor(ProfileTypes.mic) }),
            });

            var plan = ProfileStewardship.PlanPutBack(s, RecordFor(ProfileTypes.mic));

            Assert.True(plan.ChangesNothing);
            Assert.True(plan.Skipped(ProfileTypes.mic, ProfileSkipReason.OwnerHasUnsavedWork));
        }

        [Fact]
        public void PuttingBackNeverCreatesAProfile()
        {
            // A restore that invents a profile is not a restore.
            var s = Situation(types: ProfileStewardship.GovernedTypes
                .Select(t => Type(t, names: new[] { "Default", ProfileRestorePoints.NameFor(t) }))
                .ToArray());

            foreach (var plan in new[]
            {
                ProfileStewardship.PlanPutBack(s, RecordFor(ProfileStewardship.GovernedTypes)),
                ProfileStewardship.PlanOfferedRestore(s, ProfileStewardship.GovernedTypes),
            })
            {
                Assert.All(plan.Actions, a => Assert.False(a.MayCreate));
            }
        }

        // ==================================================================
        // The restore-point name is the protocol between clients
        // ==================================================================

        [Theory]
        [InlineData(ProfileTypes.global)]
        [InlineData(ProfileTypes.tx)]
        [InlineData(ProfileTypes.mic)]
        public void RestorePointNamesRoundTrip(ProfileTypes type)
        {
            var name = ProfileRestorePoints.NameFor(type);
            Assert.True(ProfileRestorePoints.IsRestorePoint(name));
            Assert.True(ProfileRestorePoints.IsWellFormed(name));
            Assert.Equal(type, ProfileRestorePoints.TypeOf(name));
        }

        [Fact]
        public void RestorePointNamesAvoidTheCharactersThatWouldBreakThem()
        {
            // Not cosmetic. A caret separates entries in the radio's own
            // profile-list status, the transmit and microphone create commands
            // STRIP an asterisk from the name they are given, and the command
            // wraps the name in quotes. Any of the three and a restore point
            // could be written under one name and looked for under another.
            foreach (var type in ProfileStewardship.GovernedTypes)
            {
                var name = ProfileRestorePoints.NameFor(type);
                Assert.DoesNotContain('^', name);
                Assert.DoesNotContain('*', name);
                Assert.DoesNotContain('"', name);
            }
        }

        [Fact]
        public void SomebodyElsesProfileIsNotMistakenForARestorePoint()
        {
            // The positive control for the recogniser: it finds ours, and does
            // NOT find a profile an operator happened to name something else.
            Assert.False(ProfileRestorePoints.IsRestorePoint("Default"));
            Assert.False(ProfileRestorePoints.IsRestorePoint("K5NER contest"));
            Assert.False(ProfileRestorePoints.IsRestorePoint(""));
            Assert.False(ProfileRestorePoints.IsRestorePoint(null));
            Assert.Equal(ProfileTypes.none, ProfileRestorePoints.TypeOf("Default"));

            Assert.True(ProfileRestorePoints.IsRestorePoint(
                ProfileRestorePoints.NameFor(ProfileTypes.mic)));
        }

        [Fact]
        public void AMalformedRestorePointNameIsNotWellFormed()
        {
            // Guards the one place this code creates a profile on a radio.
            Assert.False(ProfileRestorePoints.IsWellFormed(
                ProfileRestorePoints.Prefix + "something else"));
            Assert.False(ProfileRestorePoints.IsWellFormed("K5NER"));
        }

        // ==================================================================
        // #225: what a restore point does NOT cover, said out loud
        // ==================================================================

        [Fact]
        public void TheCoverageStatementNamesBothHalvesHonestly()
        {
            // A profile restores what a profile stores. Anything JJ Flexible
            // can change on a radio that lives OUTSIDE profile scope is not
            // covered by a restore point, and no amount of restore-point
            // machinery makes it so. If this list ever contains only covered
            // entries, somebody has quietly turned an honest limitation into
            // an implied promise.
            var coverage = ProfileStewardship.RestorePointCoverage();

            Assert.Contains(coverage, c => c.CoveredByAProfile);
            Assert.Contains(coverage, c => !c.CoveredByAProfile);
            Assert.All(coverage, c => Assert.False(string.IsNullOrWhiteSpace(c.What)));

            // Every not-covered entry says where the setting lives instead —
            // "not covered" with no explanation is a shrug, not a statement.
            Assert.All(coverage.Where(c => !c.CoveredByAProfile),
                c => Assert.False(string.IsNullOrWhiteSpace(c.Note)));
        }

        [Fact]
        public void TheCoverageListReachesSomewhereAnOperatorCanReadIt()
        {
            // A list nobody renders is a design document pretending to be
            // code. Its one consumer is the profile report — pinned here
            // because the honest-limitation half of #225 is the half that
            // quietly stops being printed.
            var reporter = File.ReadAllText(Path.Combine(
                RepoRootFor(nameof(ProfileStewardshipTests)),
                "Radios", "ProfileReporter.cs"));
            Assert.Contains("ProfileStewardship.RestorePointCoverage()", reporter,
                StringComparison.Ordinal);
            Assert.Contains("It does NOT put these back", reporter, StringComparison.Ordinal);
        }

        private static string RepoRootFor(string _)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        // ==================================================================
        // Guard rails on the inputs themselves
        // ==================================================================

        [Fact]
        public void NoRadio_PlansNothing()
        {
            var plan = ProfileStewardship.PlanConnect(Situation(connected: false));
            Assert.True(plan.ChangesNothing);
            foreach (var t in ProfileStewardship.GovernedTypes)
            {
                Assert.True(plan.Skipped(t, ProfileSkipReason.NotConnected));
            }
        }

        [Fact]
        public void NullSituation_IsNotACrash()
        {
            Assert.True(ProfileStewardship.PlanConnect(null).ChangesNothing);
            Assert.True(ProfileStewardship.PlanPutBack(null, null).ChangesNothing);
            Assert.True(ProfileStewardship.PlanOfferedRestore(null, null).ChangesNothing);
            Assert.Empty(ProfileStewardship.StrandedRestorePoints(null));
        }
    }

    /// <summary>
    /// The per-radio store half: the opt-in and the three profile choices
    /// round-trip by serial, and — the upgrade guarantee — a config.xml
    /// written before any of it existed reads as "never answered", which is
    /// the state in which nothing is touched.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ProfileGuestConfigTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope =
            new(nameof(ProfileGuestConfigTests));

        private string Dir => _scope.Directory;

        public void Dispose() => _scope.Dispose();

        private const string Serial = "1234-5678-9012-3456";

        [Fact]
        public void ANewConfigHasNeverBeenAnswered()
        {
            var cfg = new RadioConfig();
            Assert.Equal(ProfileGuestIntent.NotAnswered, cfg.ProfileIntent);
            Assert.Equal("", cfg.ProfileChoiceFor(ProfileTypes.global));
            Assert.Equal("", cfg.ProfileChoiceFor(ProfileTypes.tx));
            Assert.Equal("", cfg.ProfileChoiceFor(ProfileTypes.mic));
        }

        [Fact]
        public void AConfigWrittenBeforeThisExisted_ReadsAsNeverAnswered()
        {
            // THE UPGRADE GUARANTEE, and note which way it points. Every
            // other append-only block in this file guarantees that shipping it
            // changes nothing. This one guarantees the opposite on purpose: a
            // radio that used to have profiles applied silently has none
            // applied until the operator says so once. Making the absent
            // element mean "yes" would carry the old behaviour forward on
            // every radio in the roster, which is the behaviour this exists to
            // stop.
            var dir = Path.Combine(Dir, "radios", Serial);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Version>1</Version>\n" +
                "  <RadioId>" + Serial + "</RadioId>\n" +
                "  <Nickname>Bench8600</Nickname>\n" +
                "</RadioConfig>\n");

            var cfg = RadioConfig.LoadForRadio(Serial);
            Assert.Equal(ProfileGuestIntent.NotAnswered, cfg.ProfileIntent);
            Assert.Equal(ProfileGuestIntent.NotAnswered, RadioConfig.ProfileIntentOf(Serial));
        }

        [Fact]
        public void TheAnswerRoundTripsBySerial()
        {
            Assert.True(RadioConfig.RecordProfileIntent(
                Serial, ProfileGuestIntent.LoadMineAndPutBack));

            Assert.Equal(ProfileGuestIntent.LoadMineAndPutBack,
                RadioConfig.ProfileIntentOf(Serial));

            // …and it is PER RADIO. A second radio is unaffected, which is the
            // whole point of #451: one default for all radios cannot express
            // what an operator with two stations wants.
            Assert.Equal(ProfileGuestIntent.NotAnswered,
                RadioConfig.ProfileIntentOf("9999-8888-7777-6666"));
        }

        [Fact]
        public void TheThreeChoicesAreIndependentAndRoundTrip()
        {
            var cfg = RadioConfig.LoadForRadio(Serial);
            cfg.SetProfileChoiceFor(ProfileTypes.global, "Station");
            cfg.SetProfileChoiceFor(ProfileTypes.mic, "EVO8");
            Assert.True(cfg.SaveForRadio(Serial));

            var back = RadioConfig.LoadForRadio(Serial);
            Assert.Equal("Station", back.ProfileChoiceFor(ProfileTypes.global));
            Assert.Equal("EVO8", back.ProfileChoiceFor(ProfileTypes.mic));
            // The one not set stays empty — the radio stores the three
            // independently, so three is the honest shape.
            Assert.Equal("", back.ProfileChoiceFor(ProfileTypes.tx));
        }

        [Fact]
        public void AnUnknownSerialReadsAsNeverAnswered()
        {
            Assert.Equal(ProfileGuestIntent.NotAnswered, RadioConfig.ProfileIntentOf(""));
            Assert.Equal(ProfileGuestIntent.NotAnswered, RadioConfig.ProfileIntentOf(null));
        }
    }
}
