using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1292: the full restore capture puts back the TX and mic profiles in
    /// use BEFORE its first walk, not the ones its global walk selected.
    ///
    /// <para>Measured on the 8600: the global walk left SO2RDefault loaded,
    /// which selected SO2R_TX1; the TX walk read its start only then and put
    /// back SO2R_TX1 (RF power 100 W, tune 10 W) where Default (0 W, 0 W) had
    /// been in use.</para>
    ///
    /// <para>No FlexLib Radio can be built here, so the station below stands
    /// in for one: it answers selection reads, and loading a global profile
    /// moves its TX and mic selections the way the 8600's did. It records
    /// every read made after a load, because the walk must never ask where
    /// the radio is once it has started moving it (#469 round three).</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RestoreCapturePutBackTests
    {
        private static readonly ProfileTypes[] Order = { ProfileTypes.global, ProfileTypes.tx, ProfileTypes.mic };

        private sealed class Station
        {
            public string Global, Tx, Mic;
            public bool Keyed;
            public Func<string, bool> KeyAfterLoading = _ => false;
            public readonly Dictionary<string, (string Tx, string Mic)> GlobalCarries
                = new Dictionary<string, (string, string)>();
            public readonly Dictionary<ProfileTypes, string[]> Stored = new Dictionary<ProfileTypes, string[]>();
            public readonly List<string> Loads = new List<string>();
            public readonly List<string> ReadsAfterALoad = new List<string>();
            public readonly List<string> Spoken = new List<string>();
            /// <summary>Loads refused before dispatch (repair round 1): as
            /// SelectProfileGuarded refuses once Protect this radio's setup is
            /// on, the selection does not move and the walk is told NotQueued.</summary>
            public Func<ProfileTypes, string, bool> Refuses = (_, _) => false;
            public readonly List<string> Refused = new List<string>();

            public string Read(ProfileTypes t)
            {
                if (Loads.Count > 0) ReadsAfterALoad.Add(t.ToString());
                switch (t)
                {
                    case ProfileTypes.global: return Global;
                    case ProfileTypes.tx: return Tx;
                    default: return Mic;
                }
            }

            public ProfileReporter.ProfileLoadResult Load(ProfileTypes t, string name)
            {
                Assert.False(Keyed, "a profile load was sent while keyed: " + t + " " + name);
                if (Refuses(t, name))
                {
                    Refused.Add(t + ":" + name);
                    return ProfileReporter.ProfileLoadResult.NotQueued;
                }
                Loads.Add(t + ":" + name);
                switch (t)
                {
                    case ProfileTypes.global:
                        Global = name;
                        if (GlobalCarries.TryGetValue(name, out var carried))
                        {
                            Tx = carried.Tx;
                            Mic = carried.Mic;
                        }
                        break;
                    case ProfileTypes.tx: Tx = name; break;
                    default: Mic = name; break;
                }
                if (KeyAfterLoading(name)) Keyed = true;
                return ProfileReporter.ProfileLoadResult.QueuedAndSettled;
            }

            /// <summary>One kind's walk, as the production walker runs it
            /// once it has its put-back target: RunWalk, with this station's
            /// load and transmit state.</summary>
            /// <summary>The radio's own list for a kind, as both reports read
            /// it before walking: reported with its names, or never arrived.</summary>
            public FlexBase.RadioProfileList List(ProfileTypes t)
                => Unreported.Contains(t)
                    ? new FlexBase.RadioProfileList { ProfileType = t, Reported = false }
                    : new FlexBase.RadioProfileList
                    {
                        ProfileType = t,
                        Reported = true,
                        Names = (Stored.TryGetValue(t, out var n) ? n : new string[0]).ToList(),
                    };

            /// <summary>Kinds whose list never arrived, although their
            /// selection did (the create-on-select route of #1292 repair 1).</summary>
            public readonly HashSet<ProfileTypes> Unreported = new HashSet<ProfileTypes>();

            /// <summary>One kind's step through the PRODUCTION walker,
            /// <see cref="ProfileReporter.WalkOneKind"/>, with this station's
            /// load, transmit state and selection read in place of the rig's.
            /// <paramref name="notWalkedOutcome"/> is the restore capture's
            /// behaviour (the walker is still called); the comparison report's
            /// is null.</summary>
            public ProfileReporter.ProfileWalkOutcome WalkOne(ProfileTypes t, ProfileReporter.RecordedSelection before,
                ProfileReporter.KindStep step, bool notWalkedOutcome)
            {
                if (step == ProfileReporter.KindStep.NotWalked && !notWalkedOutcome) return null;
                return ProfileReporter.WalkOneKind(t, List(t), before,
                    putBackOnly: step == ProfileReporter.KindStep.PutBackOnly,
                    haveRadio: true,
                    readStartingSelection: () => Read(t),
                    load: n => Load(t, n),
                    keyed: () => Keyed,
                    captureWhileLoaded: null, progressCallback: Spoken.Add, unreadableCallback: null);
            }

            /// <summary>The full restore capture: every kind with stored
            /// profiles is walked.</summary>
            public List<ProfileReporter.ProfileWalkOutcome> Capture()
                => ProfileReporter.WalkKindsPuttingBackTheStart(Order,
                    k => !Unreported.Contains(k) && Stored.TryGetValue(k, out var n) && n.Length > 0, Read,
                    (k, before, step) => WalkOne(k, before, step, notWalkedOutcome: true), null);

            /// <summary>The comparison report: only the plan's kinds are
            /// walked (never mic).</summary>
            public List<ProfileReporter.ProfileWalkOutcome> Compare(params ProfileTypes[] typesToLoad)
                => ProfileReporter.WalkKindsPuttingBackTheStart(Order,
                    k => typesToLoad.Contains(k), Read,
                    (k, before, step) => WalkOne(k, before, step, notWalkedOutcome: false), null);
        }

        /// <summary>The 8600 as batch 22 and batch 24 found it: no global
        /// selected, Default TX and mic, and global profiles that each select
        /// their own TX and mic.</summary>
        private static Station TheBench(string globalBefore = "")
        {
            var s = new Station { Global = globalBefore, Tx = "Default", Mic = "Default" };
            s.GlobalCarries["JJRadioDefault"] = ("JJ_TX", "JJ_MIC");
            s.GlobalCarries["SO2RDefault"] = ("SO2R_TX1", "SO2R_MIC");
            s.Stored[ProfileTypes.global] = new[] { "JJRadioDefault", "SO2RDefault" };
            s.Stored[ProfileTypes.tx] = new[] { "Default", "SO2R_TX1", "SO2R_TX2" };
            s.Stored[ProfileTypes.mic] = new[] { "Default", "SO2R_MIC", "Voice" };
            return s;
        }

        [Fact]
        public void A_global_load_that_moved_the_TX_profile_ends_on_the_TX_profile_in_use_before()
        {
            // Red on 9ee43cb6, which read the TX start after the global walk
            // and put back SO2R_TX1.
            var s = TheBench();
            var outcomes = s.Capture();

            Assert.Equal("Default", s.Tx);
            var tx = outcomes.Single(o => o.ProfileType == ProfileTypes.tx);
            Assert.Equal("Default", tx.OriginalSelection);
            Assert.True(tx.RestoreAttempted);
            Assert.DoesNotContain("tx:SO2R_TX1", s.Loads.SkipWhile(l => l != "tx:SO2R_TX2"));
        }

        [Fact]
        public void A_global_load_that_moved_the_mic_profile_ends_on_the_mic_profile_in_use_before()
        {
            var s = TheBench();
            var outcomes = s.Capture();

            Assert.Equal("Default", s.Mic);
            var mic = outcomes.Single(o => o.ProfileType == ProfileTypes.mic);
            Assert.Equal("Default", mic.OriginalSelection);
            Assert.Equal("mic:Default", s.Loads.Last());
        }

        [Fact]
        public void Every_selection_is_read_before_the_first_load_and_never_after()
        {
            // The put-back targets come from one reading taken before anything
            // moved. A reading after a load is the walk's own request echoed
            // back, which is how SO2R_TX1 became the TX "start".
            var s = TheBench();
            s.Capture();
            Assert.Empty(s.ReadsAfterALoad);
        }

        [Fact]
        public void A_global_selected_before_is_put_back_first_and_then_TX_and_mic()
        {
            var s = TheBench(globalBefore: "JJRadioDefault");
            var outcomes = s.Capture();

            // Final state: exactly what was in use before.
            Assert.Equal("JJRadioDefault", s.Global);
            Assert.Equal("Default", s.Tx);
            Assert.Equal("Default", s.Mic);

            // The global put-back lands before any TX or mic put-back, so it
            // cannot drag them off again.
            int globalBack = s.Loads.LastIndexOf("global:JJRadioDefault");
            int txBack = s.Loads.LastIndexOf("tx:Default");
            int micBack = s.Loads.LastIndexOf("mic:Default");
            Assert.True(globalBack >= 0 && globalBack < txBack && txBack < micBack,
                "put-backs in order global, TX, mic: " + string.Join(", ", s.Loads));
            Assert.Empty(ProfileReporter.KindsNotPutBack(outcomes));
        }

        [Fact]
        public void A_global_that_was_none_before_is_reported_as_not_put_back()
        {
            var s = TheBench(globalBefore: "");
            var outcomes = s.Capture();

            Assert.Equal(new[] { ProfileTypes.global }, ProfileReporter.KindsNotPutBack(outcomes));
            var global = outcomes.Single(o => o.ProfileType == ProfileTypes.global);
            Assert.False(global.RestoreAttempted);
            Assert.Equal("nothing was selected before the walk, and no radio command clears a global selection; "
                + "the last global profile the walk loaded was SO2RDefault",
                ProfileReporter.NotPutBackValue(global));

            // The kinds that were put back carry no such line.
            foreach (var o in outcomes.Where(o => o.ProfileType != ProfileTypes.global))
                Assert.Null(ProfileReporter.NotPutBackValue(o));
        }

        [Fact]
        public void A_selection_nobody_recorded_is_reported_as_not_put_back_too()
        {
            // FlexLib leaves a selection null until the radio reports it; null
            // stays null (#469 round three) and is not dressed up as "none".
            var s = TheBench();
            s.Global = null;
            var outcomes = s.Capture();
            var global = outcomes.Single(o => o.ProfileType == ProfileTypes.global);
            Assert.Contains(ProfileTypes.global, ProfileReporter.KindsNotPutBack(outcomes));
            Assert.StartsWith("no global selection was recorded before the walk, so there was nothing to put back",
                ProfileReporter.NotPutBackValue(global));
        }

        [Fact]
        public void A_walk_stopped_for_transmit_still_puts_nothing_back_and_walks_no_later_kind()
        {
            // Keyed after the first TX profile: the TX walk stops, puts nothing
            // back, and the mic walk never starts. The station fails the test
            // if anything is loaded while keyed.
            var s = TheBench();
            s.KeyAfterLoading = n => n == "Default" && s.Loads.Count > 2;
            var outcomes = s.Capture();

            Assert.Equal(new[] { ProfileTypes.global, ProfileTypes.tx }, outcomes.Select(o => o.ProfileType));
            var tx = outcomes.Last();
            Assert.True(tx.StoppedForTransmit);
            Assert.False(tx.RestoreAttempted);
            Assert.DoesNotContain(s.Loads, l => l.StartsWith("mic:", StringComparison.Ordinal));
            Assert.Equal(new[] { "global:JJRadioDefault", "global:SO2RDefault", "tx:Default" }, s.Loads);
            // A stopped kind is not listed as "not put back"; its stop
            // sentence speaks for it.
            Assert.DoesNotContain(ProfileTypes.tx, ProfileReporter.KindsNotPutBack(outcomes));
            Assert.Null(ProfileReporter.NotPutBackValue(tx));
        }

        [Fact]
        public void Keyed_after_the_last_global_profile_puts_back_nothing_and_starts_no_TX_walk()
        {
            var s = TheBench(globalBefore: "JJRadioDefault");
            s.KeyAfterLoading = n => n == "SO2RDefault";
            var outcomes = s.Capture();

            Assert.Single(outcomes);
            Assert.True(outcomes[0].StoppedForTransmit);
            Assert.False(outcomes[0].RestoreAttempted);
            Assert.Equal(new[] { "global:JJRadioDefault", "global:SO2RDefault" }, s.Loads);
        }

        // ------------------------------------------------------------------
        // Repair 1: a put-back never creates a profile. It goes through
        // SelectProfileGuarded, which on an owner's radio creates a TX or mic
        // profile its list does not show before loading it; so a put-back
        // names only a profile the radio's own list reports.
        // ------------------------------------------------------------------

        [Fact]
        public void A_kind_whose_list_never_arrived_is_not_put_back_and_says_so()
        {
            // The TX list did not arrive but the TX selection did. The global
            // walk moved TX, so TX is put-back-only; sending Default would go
            // through create-on-select, so nothing is sent.
            var s = TheBench();
            s.Unreported.Add(ProfileTypes.tx);
            var outcomes = s.Capture();

            Assert.DoesNotContain(s.Loads, l => l.StartsWith("tx:", StringComparison.Ordinal));
            var tx = outcomes.Single(o => o.ProfileType == ProfileTypes.tx);
            Assert.True(tx.PutBackOnly);
            Assert.True(tx.PutBackNotListed);
            Assert.False(tx.RestoreAttempted);
            Assert.Contains(ProfileTypes.tx, ProfileReporter.KindsNotPutBack(outcomes));
            Assert.Equal("Default was in use before the walk, and the radio's TX profile list never arrived, and putting "
                + "back a profile the list does not show could create a new one; a global profile the walk loaded may "
                + "have changed it", ProfileReporter.NotPutBackValue(tx));
            Assert.Equal("Default, the app's last recorded TX selection before the walk, was not put back: the radio's "
                + "TX profile list never arrived, and putting back a profile the list does not show could create a new one.",
                ProfileReporter.PutBackNote(tx));
            // The mic, walked and listed, still goes back.
            Assert.Equal("Default", s.Mic);
        }

        [Fact]
        public void A_kind_the_capture_cannot_walk_because_its_list_is_empty_is_not_put_back()
        {
            var s = TheBench();
            s.Stored.Remove(ProfileTypes.mic);
            var outcomes = s.Capture();

            Assert.DoesNotContain(s.Loads, l => l.StartsWith("mic:", StringComparison.Ordinal));
            var mic = outcomes.Single(o => o.ProfileType == ProfileTypes.mic);
            Assert.True(mic.PutBackOnly);
            Assert.True(mic.PutBackNotListed);
            Assert.Contains(ProfileTypes.mic, ProfileReporter.KindsNotPutBack(outcomes));
            Assert.StartsWith("Default, the app's last recorded mic selection before the walk, was not put back: "
                + "the radio's mic profile list does not name it", ProfileReporter.AfterTheWalkValue(mic));
        }

        [Fact]
        public void A_walked_kind_whose_recorded_name_its_list_does_not_show_is_not_put_back()
        {
            // Pre-existing exposure, closed the same way: a TX selection the
            // TX list does not name is never sent back.
            var s = TheBench();
            s.Tx = "Ghost";
            var outcomes = s.Capture();

            Assert.DoesNotContain("tx:Ghost", s.Loads);
            var tx = outcomes.Single(o => o.ProfileType == ProfileTypes.tx);
            Assert.False(tx.PutBackOnly);
            Assert.True(tx.PutBackNotListed);
            Assert.Contains(ProfileTypes.tx, ProfileReporter.KindsNotPutBack(outcomes));
        }

        [Fact]
        public void An_empty_TX_or_mic_selection_is_not_said_to_be_uncleareable()
        {
            // Measured for global only (no radio command clears a global
            // selection); not claimed for TX or mic.
            var s = TheBench();
            s.Tx = "";
            var outcomes = s.Capture();
            var tx = outcomes.Single(o => o.ProfileType == ProfileTypes.tx);
            Assert.Equal("nothing was selected before the walk, so there was nothing to put back; the last TX profile "
                + "the walk loaded was SO2R_TX2", ProfileReporter.NotPutBackValue(tx));
            Assert.DoesNotContain("clears", ProfileReporter.NotPutBackValue(tx));
        }

        // ------------------------------------------------------------------
        // The comparison report walks global and TX (never mic) through the
        // same sequence, and was wrong in the same way.
        // ------------------------------------------------------------------

        [Fact]
        public void The_comparison_ends_on_the_TX_profile_in_use_before_its_global_walk()
        {
            // Red on 75a8ffd6, whose comparison read the TX start after the
            // global walk and put back SO2R_TX1.
            var s = TheBench();
            var outcomes = s.Compare(ProfileTypes.global, ProfileTypes.tx);

            Assert.Equal("Default", s.Tx);
            Assert.Equal("Default", outcomes.Single(o => o.ProfileType == ProfileTypes.tx).OriginalSelection);
            Assert.Empty(s.ReadsAfterALoad);
        }

        [Fact]
        public void The_comparison_puts_the_mic_profile_back_although_it_never_walks_mic()
        {
            var s = TheBench();
            var outcomes = s.Compare(ProfileTypes.global, ProfileTypes.tx);

            Assert.Equal("Default", s.Mic);
            var mic = outcomes.Single(o => o.ProfileType == ProfileTypes.mic);
            Assert.True(mic.PutBackOnly);
            Assert.True(mic.RestoreAttempted);
            // Global's put-back would come first; TX's and mic's follow it.
            Assert.Equal(new[] { "tx:Default", "mic:Default" }, s.Loads.Skip(s.Loads.Count - 2));
            Assert.DoesNotContain(s.Loads, l => l.StartsWith("mic:", StringComparison.Ordinal) && l != "mic:Default");
        }

        [Fact]
        public void The_comparison_does_not_put_the_mic_back_when_its_list_never_arrived()
        {
            var s = TheBench();
            s.Unreported.Add(ProfileTypes.mic);
            var outcomes = s.Compare(ProfileTypes.global, ProfileTypes.tx);

            Assert.DoesNotContain(s.Loads, l => l.StartsWith("mic:", StringComparison.Ordinal));
            Assert.Contains(ProfileTypes.mic, ProfileReporter.KindsNotPutBack(outcomes));
            Assert.Equal("Default", s.Tx);
        }

        [Fact]
        public void The_comparison_puts_global_back_first_when_one_was_selected()
        {
            var s = TheBench(globalBefore: "JJRadioDefault");
            s.Compare(ProfileTypes.global, ProfileTypes.tx);

            Assert.Equal("JJRadioDefault", s.Global);
            Assert.Equal("Default", s.Tx);
            Assert.Equal("Default", s.Mic);
            int globalBack = s.Loads.LastIndexOf("global:JJRadioDefault");
            Assert.True(globalBack < s.Loads.LastIndexOf("tx:Default")
                && s.Loads.LastIndexOf("tx:Default") < s.Loads.LastIndexOf("mic:Default"),
                "put-backs in order global, TX, mic: " + string.Join(", ", s.Loads));
        }

        [Fact]
        public void The_comparison_file_says_a_global_that_was_none_before_was_not_put_back()
        {
            var s = TheBench(globalBefore: "");
            var outcomes = s.Compare(ProfileTypes.global, ProfileTypes.tx);

            Assert.Equal(new[] { ProfileTypes.global }, ProfileReporter.KindsNotPutBack(outcomes));
            var sb = new System.Text.StringBuilder();
            ProfileReporter.AppendComparisonNotPutBack(sb, outcomes);
            Assert.Equal(Environment.NewLine
                + "The global profile was not put back: nothing was selected before the walk, and no radio command "
                + "clears a global selection; the last global profile the walk loaded was SO2RDefault."
                + Environment.NewLine, sb.ToString());
        }

        [Fact]
        public void A_comparison_that_walks_no_global_leaves_the_mic_alone()
        {
            // Nothing moved the mic, so nothing puts it back: a TX-only plan
            // sends no mic load at all.
            var s = TheBench();
            var outcomes = s.Compare(ProfileTypes.tx);

            Assert.DoesNotContain(s.Loads, l => l.StartsWith("mic:", StringComparison.Ordinal));
            Assert.DoesNotContain(s.Loads, l => l.StartsWith("global:", StringComparison.Ordinal));
            Assert.Equal(new[] { ProfileTypes.tx }, outcomes.Select(o => o.ProfileType));
        }

        [Fact]
        public void A_comparison_keyed_during_its_TX_walk_puts_nothing_back_and_leaves_the_mic_alone()
        {
            var s = TheBench();
            s.KeyAfterLoading = n => n == "SO2R_TX1" && s.Loads.Count > 3;
            var outcomes = s.Compare(ProfileTypes.global, ProfileTypes.tx);

            var tx = outcomes.Last();
            Assert.Equal(ProfileTypes.tx, tx.ProfileType);
            Assert.True(tx.StoppedForTransmit);
            Assert.False(tx.RestoreAttempted);
            Assert.DoesNotContain(s.Loads, l => l.StartsWith("mic:", StringComparison.Ordinal));
        }

        [Fact]
        public void A_comparison_keyed_before_the_mic_put_back_sends_it_not_and_says_so()
        {
            // Keyed by the TX put-back itself (the last TX load): the TX walk
            // finished unkeyed, so the mic put-back is the next load, and it is
            // refused like any put-back while keyed.
            var s = TheBench();
            int txLoads = 0;
            s.KeyAfterLoading = n => s.Loads.Last().StartsWith("tx:", StringComparison.Ordinal) && ++txLoads == 4;
            var outcomes = s.Compare(ProfileTypes.global, ProfileTypes.tx);

            var mic = outcomes.Last();
            Assert.Equal(ProfileTypes.mic, mic.ProfileType);
            Assert.True(mic.PutBackOnly);
            Assert.True(mic.StoppedForTransmit);
            Assert.False(mic.RestoreAttempted);
            Assert.DoesNotContain(s.Loads, l => l.StartsWith("mic:", StringComparison.Ordinal));
            Assert.Equal(TransmitStop, s.Spoken.Last());
            var sb = new System.Text.StringBuilder();
            ProfileReporter.AppendComparisonStopped(sb, mic);
            Assert.Contains("The radio was keyed before the mic profile was put back, so nothing was put back.", sb.ToString());
            Assert.StartsWith("NO — the radio was keyed", ProfileReporter.AfterTheWalkValue(mic));
            Assert.DoesNotContain(ProfileTypes.mic, ProfileReporter.KindsNotPutBack(outcomes));
        }

        private const string TransmitStop =
            "Profile loading stopped because the radio is transmitting. Your profile may not be the one you "
            + "started with; load it again from Profiles, in the Radio menu, if needed.";

        [Fact]
        public void The_restore_capture_hands_its_early_recording_to_the_one_walker()
        {
            // SOURCE PIN for the wiring no test can execute (it needs a
            // FlexLib Radio): the restore capture walks through the seam
            // above and passes each kind's early recording to the same walker
            // WalkProfiles uses, which then does not read its own start.
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                FlexBasePartials.RepoRoot(), "Radios", "ProfileReporter.cs"));
            Assert.Contains("outcomes = WalkKindsPuttingBackTheStart(profileTypes,", src);
            Assert.Contains("lists[t], recordedBefore, putBackOnly: step == KindStep.PutBackOnly);", src);
            Assert.Contains("outcome.OriginalSelection = putBackRecordedEarlier.Value;", src);
            Assert.Equal(2, Count(src, "result.NotPutBack = KindsNotPutBack(outcomes);"));
            // The comparison report walks through the same sequence, with the
            // plan's kinds, and hands each walk its early recording.
            Assert.Contains("var outcomes = WalkKindsPuttingBackTheStart(types,", src);
            Assert.Contains("k => plan.TypesToLoad.Contains(k),", src);
            Assert.Contains("CaptureAllProfiles(rig, ptype, recordedBefore, out var walked, progressCallback, readings[ptype]);", src);
            Assert.Contains("readings[ptype], recordedBefore, putBackOnly: true);", src);
            Assert.Contains("progressCallback, null, radioList, recordedBefore, putBackOnly: false);", src);
            Assert.Contains("AppendComparisonNotPutBack(sb, outcomes);", src);
            // Only the public walker, which nothing in the app calls now, still
            // reads its own start.
            Assert.Equal(1, Count(src, "putBackRecordedEarlier: null"));
        }

        // ------------------------------------------------------------------
        // The spoken "not put back" sentence (Noel 2026-11-24: be specific,
        // name the type and the profile, "select your Default transmit
        // profile"). The words are DRAFT; these pin the facts they carry.
        // ------------------------------------------------------------------

        private const string Path = @"C:\captures\restore.txt";

        private static ProfileReporter.RestoreGradeExport ExportFrom(List<ProfileReporter.ProfileWalkOutcome> outcomes)
            => new ProfileReporter.RestoreGradeExport
            {
                WalkRan = true,
                AnyLoadRequested = true,
                NotPutBack = ProfileReporter.KindsNotPutBack(outcomes),
                NotPutBackItems = ProfileReporter.NotPutBackItems(outcomes),
            };

        private static ProfileReporter.ComparisonReport ReportFrom(List<ProfileReporter.ProfileWalkOutcome> outcomes)
            => new ProfileReporter.ComparisonReport
            {
                AnyLoadRequested = true,
                NotPutBack = ProfileReporter.KindsNotPutBack(outcomes),
                NotPutBackItems = ProfileReporter.NotPutBackItems(outcomes),
            };

        [Fact]
        public void The_items_carry_the_same_kinds_as_the_kind_list_with_their_names()
        {
            var s = TheBench(globalBefore: "");
            s.Unreported.Add(ProfileTypes.tx);
            var outcomes = s.Capture();

            var items = ProfileReporter.NotPutBackItems(outcomes);
            Assert.Equal(ProfileReporter.KindsNotPutBack(outcomes), items.Select(i => i.Kind));
            Assert.Equal("", items[0].Before);
            Assert.Equal("SO2RDefault", items[0].LastLoaded);
            Assert.Equal("Default", items[1].Before);
        }

        [Fact]
        public void A_transmit_profile_not_put_back_is_named_with_its_kind_and_where_to_select_it()
        {
            var s = TheBench(globalBefore: "JJRadioDefault");
            s.Unreported.Add(ProfileTypes.tx);
            var outcomes = s.Capture();

            string said = ProfileReporter.RestoreFinishedSentence(ExportFrom(outcomes), Path);
            Assert.Contains(Path, said);
            Assert.Contains("select your Default transmit profile", said);
            Assert.Contains("Profiles, in the Radio menu", said);
            Assert.DoesNotContain("global", said);
        }

        [Fact]
        public void A_microphone_profile_not_put_back_is_named_with_its_kind()
        {
            var s = TheBench(globalBefore: "JJRadioDefault");
            s.Stored.Remove(ProfileTypes.mic);
            var outcomes = s.Capture();

            string said = ProfileReporter.RestoreFinishedSentence(ExportFrom(outcomes), Path);
            Assert.Contains("select your Default microphone profile", said);
            Assert.DoesNotContain("transmit profile", said);
        }

        [Fact]
        public void A_global_that_was_none_before_names_the_last_global_loaded_and_that_it_cannot_be_cleared()
        {
            var s = TheBench(globalBefore: "");
            var outcomes = s.Capture();

            string said = ProfileReporter.RestoreFinishedSentence(ExportFrom(outcomes), Path);
            Assert.Contains("global profile JJ Flexible loaded was SO2RDefault", said);
            Assert.Contains("cannot be cleared", said);
            AssertNoClaimOfWhereTheRadioIs(said);
            // TX and mic were put back, so they are not named as not put back.
            Assert.DoesNotContain("transmit", said);
            Assert.DoesNotContain("microphone", said);
        }

        [Fact]
        public void A_selection_nobody_recorded_names_the_last_loaded_without_a_profile_to_select()
        {
            var s = TheBench();
            s.Global = null;
            var outcomes = s.Capture();

            string said = ProfileReporter.RestoreFinishedSentence(ExportFrom(outcomes), Path);
            Assert.Contains("could not be read", said);
            Assert.Contains("global profile JJ Flexible loaded was SO2RDefault", said);
            Assert.DoesNotContain("select your", said);
            AssertNoClaimOfWhereTheRadioIs(said);
        }

        [Fact]
        public void An_empty_TX_selection_names_the_last_one_loaded_and_claims_no_clearing()
        {
            var s = TheBench(globalBefore: "JJRadioDefault");
            s.Tx = "";
            var outcomes = s.Capture();

            string said = ProfileReporter.RestoreFinishedSentence(ExportFrom(outcomes), Path);
            Assert.Contains("transmit profile JJ Flexible loaded was SO2R_TX2", said);
            Assert.DoesNotContain("cleared", said);
            AssertNoClaimOfWhereTheRadioIs(said);
        }

        [Fact]
        public void With_nothing_left_unput_back_both_finished_sentences_are_unchanged()
        {
            var s = TheBench(globalBefore: "JJRadioDefault");
            var outcomes = s.Capture();
            Assert.Empty(ProfileReporter.KindsNotPutBack(outcomes));

            Assert.Equal(Radios.Lexicon.Get("settings.restore_export.saved_loaded", ("path", Path)),
                ProfileReporter.RestoreFinishedSentence(ExportFrom(outcomes), Path));
            Assert.Equal(Radios.Lexicon.Get("settings.profile.report_saved_loaded", ("path", Path)),
                ProfileReporter.ComparisonFinishedSentence(ReportFrom(outcomes), Path));
        }

        [Fact]
        public void The_comparison_names_a_global_that_was_none_before_the_same_way()
        {
            var s = TheBench(globalBefore: "");
            var outcomes = s.Compare(ProfileTypes.global, ProfileTypes.tx);

            string said = ProfileReporter.ComparisonFinishedSentence(ReportFrom(outcomes), Path);
            Assert.StartsWith("Profile report saved to " + Path, said);
            Assert.Contains("global profile JJ Flexible loaded was SO2RDefault", said);
            Assert.Contains("cannot be cleared", said);
            AssertNoClaimOfWhereTheRadioIs(said);
        }

        /// <summary>
        /// Repair round 1 (the checker's B1): with no global selected before,
        /// every global load refused before dispatch (Protect this radio's
        /// setup turned on after the walk was planned) leaves the radio on no
        /// global at all. The walk still counts the attempts, so global is
        /// listed as not put back, with nothing loaded. Neither finisher may
        /// then say a global profile was loaded; each says it is not known,
        /// and where to check. Goes red if the sentence asserts a loaded
        /// global again.
        /// </summary>
        [Fact]
        public void Every_load_refused_leaves_no_claim_of_a_loaded_global_in_either_finished_sentence()
        {
            foreach (bool comparison in new[] { false, true })
            {
                var s = TheBench(globalBefore: "");
                s.Refuses = (_, _) => true;
                var outcomes = comparison ? s.Compare(ProfileTypes.global, ProfileTypes.tx) : s.Capture();

                // The case is real: attempts were counted, nothing loaded, the
                // radio never moved, and global is listed with no profile.
                Assert.Empty(s.Loads);
                Assert.NotEmpty(s.Refused);
                Assert.Equal("", s.Global);
                var global = Assert.Single(ProfileReporter.NotPutBackItems(outcomes), i => i.Kind == ProfileTypes.global);
                Assert.Null(global.LastLoaded);

                string said = comparison
                    ? ProfileReporter.ComparisonFinishedSentence(ReportFrom(outcomes), Path)
                    : ProfileReporter.RestoreFinishedSentence(ExportFrom(outcomes), Path);
                Assert.Contains(Path, said);
                Assert.Contains("not known", said);
                Assert.Contains("Profiles, in the Radio menu", said);
                Assert.DoesNotContain("profile loaded", said, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("JJ Flexible loaded", said, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("JJRadioDefault", said);
                Assert.DoesNotContain("SO2RDefault", said);
                AssertNoClaimOfWhereTheRadioIs(said);
            }
        }

        /// <summary>A load read back from FlexLib's field is not the radio's own
        /// report (LoadProfileAndWait), so no "not put back" sentence says where
        /// the radio IS; it says what JJ Flexible last loaded (repair round 1).</summary>
        private static void AssertNoClaimOfWhereTheRadioIs(string said)
        {
            Assert.DoesNotContain("radio stays on", said, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("radio is now on", said, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("radio is on", said, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_finished_sentences_read_the_items_the_two_walkers_fill()
        {
            // SOURCE PIN: both walkers fill the named list beside the kinds.
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                FlexBasePartials.RepoRoot(), "Radios", "ProfileReporter.cs"));
            Assert.Equal(2, Count(src, "result.NotPutBackItems = NotPutBackItems(outcomes);"));
        }

        private static int Count(string s, string token)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(token, i, StringComparison.Ordinal)) >= 0) { n++; i += token.Length; }
            return n;
        }
    }
}
