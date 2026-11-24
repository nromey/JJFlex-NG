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
            public ProfileReporter.ProfileWalkOutcome WalkOne(ProfileTypes t, ProfileReporter.RecordedSelection before)
            {
                var o = new ProfileReporter.ProfileWalkOutcome { ProfileType = t, OriginalSelection = before.Value };
                ProfileReporter.RunWalk(o, Stored[t], n => Load(t, n), () => Keyed, null, Spoken.Add, null);
                return o;
            }

            public List<ProfileReporter.ProfileWalkOutcome> Capture()
                => ProfileReporter.WalkKindsPuttingBackTheStart(Order, Read, WalkOne, null);
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
            Assert.Contains("lists[t], recordedBefore);", src);
            Assert.Contains("outcome.OriginalSelection = putBackRecordedEarlier.Value;", src);
            Assert.Contains("result.NotPutBack = KindsNotPutBack(outcomes);", src);
            // The comparison report keeps reading its own start.
            Assert.Contains("unreadableCallback, radioList, putBackRecordedEarlier: null);", src);
        }
    }
}
