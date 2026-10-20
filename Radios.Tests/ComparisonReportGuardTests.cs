using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #469: the profile comparison report walks profiles the way the restore
    /// capture does, and both now carry the same guards: the walk is reserved
    /// at the press and released on every exit; what is loaded is decided
    /// once, before anything is said; keyed transmit refuses only a run that
    /// would load, with a true reason; and transmit is asked before every
    /// profile the walk loads, with nothing put back while keyed.
    ///
    /// <para>No radio under test, so no profile is ever actually loaded here.
    /// What these tests drive is the guard itself, through the real entry
    /// points: each walk is started while the OTHER one genuinely holds the
    /// flag, via <see cref="ProfileReporter.WalkHeldProbe"/>, which production
    /// code calls the moment it has taken the flag.</para>
    /// </summary>
    // In the RadioConfig statics collection for two reasons: the walk flag is
    // process-wide, and RestoreGradeExportTests (which needs the flag free) is
    // in the same collection, so this class holding it on purpose cannot
    // refuse that class's exports; and the sentences are read through the
    // process-wide Lexicon.
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ComparisonReportGuardTests
    {
        private static FlexBase MakeRig()
            => new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        private static readonly FieldInfo Flag =
            typeof(ProfileReporter).GetField("_walkInFlight", BindingFlags.NonPublic | BindingFlags.Static)!;

        private static void Key(FlexBase rig, bool keyed)
            => typeof(FlexBase).GetField("_Transmit", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(rig, keyed);

        // ------------------------------------------------------------------
        // One walk at a time, from either menu item, in either order
        // ------------------------------------------------------------------

        [Fact]
        public void A_walk_already_running_refuses_the_comparison_report_and_leaves_the_flag_with_its_owner()
        {
            // Red against 4d7cc805: the report ran straight through a held flag.
            Flag.SetValue(null, 1);
            try
            {
                Assert.Null(ProfileReporter.GenerateReport(MakeRig(), null, out var why));
                Assert.Equal(ProfileReporter.WalkRefusal.WalkAlreadyRunning, why);
                // The refuser must not release a flag it never took: that
                // would hand the running walk's protection to the next caller.
                Assert.True(ProfileReporter.WalkInProgress);
            }
            finally { Flag.SetValue(null, 0); }
        }

        [Fact]
        public void A_restore_capture_started_during_a_comparison_report_is_refused()
        {
            var rig = MakeRig();
            int held = 0;
            ProfileReporter.RestoreGradeExport nested = null;
            string nestedReport = "not attempted";
            ProfileReporter.WalkRefusal nestedWhy = ProfileReporter.WalkRefusal.None;
            ProfileReporter.WalkHeldProbe = who =>
            {
                if (who != "comparison report") return;
                held++;
                Assert.True(ProfileReporter.WalkInProgress);
                nested = ProfileReporter.GenerateRestoreGradeExport(rig);
                nestedReport = ProfileReporter.GenerateReport(rig, null, out nestedWhy);
            };
            try
            {
                Assert.NotNull(ProfileReporter.GenerateReport(rig));
            }
            finally { ProfileReporter.WalkHeldProbe = null; }

            Assert.Equal(1, held);
            Assert.Null(nested);
            Assert.Null(nestedReport);
            Assert.Equal(ProfileReporter.WalkRefusal.WalkAlreadyRunning, nestedWhy);
            Assert.False(ProfileReporter.WalkInProgress);
        }

        [Fact]
        public void A_comparison_report_started_during_a_restore_capture_is_refused()
        {
            var rig = MakeRig();
            int held = 0;
            string nestedReport = "not attempted";
            ProfileReporter.WalkRefusal nestedWhy = ProfileReporter.WalkRefusal.None;
            ProfileReporter.WalkHeldProbe = who =>
            {
                if (who != "restore capture") return;
                held++;
                nestedReport = ProfileReporter.GenerateReport(rig, null, out nestedWhy);
            };
            try
            {
                Assert.NotNull(ProfileReporter.GenerateRestoreGradeExport(rig));
            }
            finally { ProfileReporter.WalkHeldProbe = null; }

            Assert.Equal(1, held);
            Assert.Null(nestedReport);
            Assert.Equal(ProfileReporter.WalkRefusal.WalkAlreadyRunning, nestedWhy);
            Assert.False(ProfileReporter.WalkInProgress);
        }

        // ------------------------------------------------------------------
        // Repair round one, blocker 3: the walk is reserved at the press
        // ------------------------------------------------------------------

        [Fact]
        public void A_reservation_holds_the_flag_until_disposed_and_both_reports_answer_busy_meanwhile()
        {
            var rig = MakeRig();
            var first = ProfileReporter.TryReserveWalk();
            Assert.NotNull(first);
            try
            {
                Assert.True(ProfileReporter.WalkInProgress);
                // A second press of either item, inside the settle, is refused.
                Assert.Null(ProfileReporter.TryReserveWalk());
                Assert.Null(ProfileReporter.GenerateReport(rig, null, out var why));
                Assert.Equal(ProfileReporter.WalkRefusal.WalkAlreadyRunning, why);
                Assert.Null(ProfileReporter.GenerateRestoreGradeExport(rig));
                Assert.True(ProfileReporter.WalkInProgress);

                // The holder runs under it rather than taking the flag twice.
                var report = ProfileReporter.GenerateReport(first, ProfileReporter.PlanComparison(rig), rig, null);
                Assert.NotNull(report.Text);
                var export = ProfileReporter.GenerateRestoreGradeExport(first, rig, null);
                Assert.NotNull(export.Text);
                Assert.True(ProfileReporter.WalkInProgress);
            }
            finally { first.Dispose(); }

            Assert.False(ProfileReporter.WalkInProgress);
            Assert.NotNull(ProfileReporter.GenerateReport(rig));
        }

        [Fact]
        public void Disposing_a_reservation_twice_never_clears_a_later_walks_flag()
        {
            var a = ProfileReporter.TryReserveWalk();
            a.Dispose();
            var b = ProfileReporter.TryReserveWalk();
            try
            {
                a.Dispose();
                Assert.True(ProfileReporter.WalkInProgress);
                Assert.Null(ProfileReporter.TryReserveWalk());
            }
            finally { b.Dispose(); }
            Assert.False(ProfileReporter.WalkInProgress);
        }

        [Fact]
        public void A_released_reservation_cannot_run_a_walk()
        {
            var rig = MakeRig();
            var r = ProfileReporter.TryReserveWalk();
            r.Dispose();
            Assert.Throws<ArgumentException>(() =>
                ProfileReporter.GenerateReport(r, ProfileReporter.PlanComparison(rig), rig, null));
            Assert.Throws<ArgumentException>(() =>
                ProfileReporter.GenerateRestoreGradeExport(r, rig, null));
            Assert.Throws<ArgumentException>(() =>
                ProfileReporter.GenerateRestoreGradeExport(null, rig, null));
        }

        // ------------------------------------------------------------------
        // Repair round one, blocker 1: one decider for what is loaded, and a
        // start sentence that says only that
        // ------------------------------------------------------------------

        private static FlexBase.RadioProfileList Listed(ProfileTypes t, params string[] names)
            => new FlexBase.RadioProfileList { ProfileType = t, Reported = true, Names = names.ToList() };

        private static Dictionary<ProfileTypes, FlexBase.RadioProfileList> Lists(int global, int tx, int mic)
            => new Dictionary<ProfileTypes, FlexBase.RadioProfileList>
            {
                [ProfileTypes.global] = Listed(ProfileTypes.global, Enumerable.Range(1, global).Select(i => "G" + i).ToArray()),
                [ProfileTypes.tx] = Listed(ProfileTypes.tx, Enumerable.Range(1, tx).Select(i => "T" + i).ToArray()),
                [ProfileTypes.mic] = Listed(ProfileTypes.mic, Enumerable.Range(1, mic).Select(i => "M" + i).ToArray()),
            };

        [Fact]
        public void The_comparison_loads_global_and_TX_only_where_there_are_two_to_compare_and_never_mic()
        {
            Assert.Equal(new[] { ProfileTypes.global, ProfileTypes.tx },
                ProfileReporter.TypesTheComparisonLoads(true, Lists(3, 2, 9)));
            Assert.Equal(new[] { ProfileTypes.global },
                ProfileReporter.TypesTheComparisonLoads(true, Lists(3, 1, 9)));
            Assert.Equal(new[] { ProfileTypes.tx },
                ProfileReporter.TypesTheComparisonLoads(true, Lists(0, 4, 0)));
            Assert.Empty(ProfileReporter.TypesTheComparisonLoads(true, Lists(1, 1, 9)));
            // Not permitted on this radio: nothing, whatever it holds.
            Assert.Empty(ProfileReporter.TypesTheComparisonLoads(false, Lists(5, 5, 5)));
            // A list the radio never reported loads nothing.
            var unreported = Lists(3, 3, 0);
            unreported[ProfileTypes.global].Reported = false;
            Assert.Equal(new[] { ProfileTypes.tx }, ProfileReporter.TypesTheComparisonLoads(true, unreported));
        }

        [Fact]
        public void The_start_sentence_names_exactly_what_will_be_loaded()
        {
            // Red against 24154dab: one sentence, "Each stored profile is
            // loaded on the radio in turn", for every run that might load.
            Assert.Equal(
                "Writing the profile comparison report. Your stored global and TX profiles are loaded on the radio in turn, "
                + "and you will hear things change as it goes. This takes a minute or two; progress is spoken.",
                ProfileReporter.ComparisonStartSentence(ProfileReporter.MakeComparisonPlan(true, Lists(3, 2, 9))));
            Assert.Contains("Your stored TX profiles are loaded",
                ProfileReporter.ComparisonStartSentence(ProfileReporter.MakeComparisonPlan(true, Lists(1, 2, 0))));
            Assert.Contains("Your stored global profiles are loaded",
                ProfileReporter.ComparisonStartSentence(ProfileReporter.MakeComparisonPlan(true, Lists(2, 1, 0))));
        }

        [Fact]
        public void When_nothing_will_be_loaded_the_start_sentence_promises_no_loading()
        {
            string nothing = ProfileReporter.ComparisonStartSentence(
                ProfileReporter.MakeComparisonPlan(true, Lists(1, 1, 6)));
            Assert.Equal("Writing the profile report. There is nothing to compare, so the radio is left as it is.", nothing);
            Assert.DoesNotContain("load", nothing, StringComparison.OrdinalIgnoreCase);

            // Not permitted here: the sentence says so, and that the
            // comparison is left out — it promises no change.
            string held = ProfileReporter.ComparisonStartSentence(
                ProfileReporter.MakeComparisonPlan(false, Lists(4, 4, 4)));
            Assert.Equal("Writing the profile report. Profiles are not loaded on this radio, so the comparison is left out, and the report says why.", held);
        }

        [Fact]
        public void An_offline_radio_is_planned_as_loading_nothing()
        {
            var plan = ProfileReporter.PlanComparison(MakeRig());
            Assert.False(plan.WillLoad);
            Assert.Equal(3, plan.Lists.Count);
            Assert.True(plan.Lists[ProfileTypes.mic].CouldNotAsk);
        }

        // ------------------------------------------------------------------
        // Repair round one, blocker 2: transmit refuses only a run that loads,
        // and its reason is what that run would load
        // ------------------------------------------------------------------

        [Fact]
        public void A_keyed_radio_that_would_load_nothing_gets_its_read_only_report()
        {
            // Red against 24154dab: the report refused for transmit although
            // nothing in it would have changed the radio.
            var rig = MakeRig();
            Key(rig, true);
            Assert.NotNull(ProfileReporter.GenerateReport(rig, null, out var why));
            Assert.Equal(ProfileReporter.WalkRefusal.None, why);
            Assert.False(ProfileReporter.WalkInProgress);
            Assert.Null(ProfileReporter.ComparisonTransmitRefusal(
                ProfileReporter.MakeComparisonPlan(false, Lists(4, 4, 0)), keyed: true));
            Assert.Null(ProfileReporter.ComparisonTransmitRefusal(
                ProfileReporter.MakeComparisonPlan(true, Lists(1, 1, 0)), keyed: true));
        }

        [Fact]
        public void A_keyed_radio_whose_run_would_load_is_refused_with_a_reason_that_names_what()
        {
            var rig = MakeRig();
            Key(rig, true);
            var plan = ProfileReporter.MakeComparisonPlan(true, Lists(3, 3, 0));

            Assert.Equal(
                "Not while transmitting. Unkey first — this report loads your global and TX profiles on the radio, "
                + "and profiles change transmit settings.",
                ProfileReporter.ComparisonTransmitRefusal(plan, keyed: true));
            Assert.Null(ProfileReporter.ComparisonTransmitRefusal(plan, keyed: false));

            // After the settle, under the press's reservation: refused before
            // the report is written, and the sentence is the same true one.
            using (var r = ProfileReporter.TryReserveWalk())
            {
                var report = ProfileReporter.GenerateReport(r, plan, rig, null);
                Assert.Equal(ProfileReporter.WalkRefusal.Transmitting, report.Refused);
                Assert.Null(report.Text);
                Assert.Equal(ProfileReporter.ComparisonTransmitRefusal(plan, keyed: true),
                    ProfileReporter.ComparisonRefusedSentence(report, plan));
                // The refusal does not release the caller's reservation.
                Assert.True(r.IsHeld);
                Assert.True(ProfileReporter.WalkInProgress);
            }
            Assert.False(ProfileReporter.WalkInProgress);
        }

        [Fact]
        public void The_restore_capture_asks_transmit_again_after_the_settle_but_runs_keyed_when_it_would_not_walk()
        {
            // Codex's ASK: the restore capture checked transmit only at the
            // press. Red against 24154dab, where a keyed offline capture ran.
            var rig = MakeRig();
            Key(rig, true);
            using (var r = ProfileReporter.TryReserveWalk())
            {
                var refused = ProfileReporter.GenerateRestoreGradeExport(r,
                    ProfileReporter.MakeRestorePlan(true, Lists(2, 1, 0)), rig, null);
                Assert.Equal(ProfileReporter.WalkRefusal.Transmitting, refused.Refused);
                Assert.Null(refused.Text);

                // A plan that loads nothing is read-only, so it runs keyed:
                // offline (no list can be read) and under the hold alike.
                var offline = ProfileReporter.GenerateRestoreGradeExport(r, rig, null);
                Assert.Equal(ProfileReporter.WalkRefusal.None, offline.Refused);
                Assert.NotNull(offline.Text);
                rig.SetChangeNothingActive(true);
                var readOnly = ProfileReporter.GenerateRestoreGradeExport(r, rig, null);
                Assert.Equal(ProfileReporter.WalkRefusal.None, readOnly.Refused);
                Assert.False(readOnly.WalkRan);
                Assert.NotNull(readOnly.Text);
            }
        }

        [Fact]
        public void A_radio_never_opted_in_is_not_walked_and_the_report_says_so()
        {
            // What this cannot reach: the TRUE policy answer needs a connected
            // radio with a serial the operator opted in, which no test here
            // has; the plan tests above construct that answer directly.
            var rig = MakeRig();
            Assert.False(ProfileReporter.ComparisonMayLoadProfiles(rig));
            string report = ProfileReporter.GenerateReport(rig);
            Assert.Contains("PROFILE COMPARISON SKIPPED", report);

            rig.SetChangeNothingActive(true);
            Assert.False(ProfileReporter.ComparisonMayLoadProfiles(rig));
            Assert.Contains("Change nothing is on for this radio", ProfileReporter.GenerateReport(rig));
        }

        // ------------------------------------------------------------------
        // Keying mid-walk (round two, Noel's ruling 2026-10-20: "I'd say what
        // profile radio's on now"): transmit is asked before every load and
        // before the put-back; keyed, the walk stops and puts NOTHING back,
        // and says which profile the radio is on and that the original was
        // not put back. Round one's wait-for-unkey tests were removed with
        // the wait.
        // ------------------------------------------------------------------

        /// <summary>A scripted radio for <see cref="ProfileReporter.RunWalk"/>:
        /// records every load request, fails the test if one is sent while
        /// keyed, and can be told to leave a load unconfirmed. It has no
        /// selection to read back on purpose: round three's rule is that the
        /// walk never asks where the radio is, so a fake that answered would
        /// pin the very claim Codex found false.</summary>
        private sealed class ScriptedRadio
        {
            public bool Keyed;
            public readonly List<string> Loads = new List<string>();
            public readonly List<string> Spoken = new List<string>();
            public Func<string, bool> KeyAfterLoading = _ => false;
            public Func<string, bool> Confirms = _ => true;
            /// <summary>Refused before dispatch (change nothing, ownership):
            /// recorded as asked for, never as sent.</summary>
            public Func<string, bool> Refuses = _ => false;
            public readonly List<string> Sent = new List<string>();

            public ProfileReporter.ProfileLoadResult Load(string name)
            {
                Assert.False(Keyed, "a profile load was sent while keyed: " + name);
                Loads.Add(name);
                if (Refuses(name)) return ProfileReporter.ProfileLoadResult.NotQueued;
                Sent.Add(name);
                if (KeyAfterLoading(name)) Keyed = true;
                return Confirms(name)
                    ? ProfileReporter.ProfileLoadResult.QueuedAndSettled
                    : ProfileReporter.ProfileLoadResult.QueuedNotSettled;
            }

            public ProfileReporter.ProfileWalkOutcome Walk(ProfileTypes type, string original, params string[] names)
            {
                var outcome = new ProfileReporter.ProfileWalkOutcome { ProfileType = type, OriginalSelection = original };
                ProfileReporter.RunWalk(outcome, names, Load, () => Keyed, null, Spoken.Add, null);
                return outcome;
            }
        }

        // Round four (Noel, 2026-10-20): after a transmit stop, nothing the
        // operator hears names a profile or claims where the radio was left.
        private const string StopLine =
            "Profile loading stopped because the radio is transmitting. Your profile may not be the one you "
            + "started with; load it again from Profiles, in the Radio menu, if needed.";

        /// <summary>Everything a stopped walk can put in front of the
        /// operator or into the comparison file, gathered so one assertion can
        /// hold all of it.</summary>
        private static string AllStopOutput(ScriptedRadio radio, ProfileReporter.ProfileWalkOutcome o)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var line in radio.Spoken) sb.AppendLine(line);
            sb.AppendLine(ProfileReporter.ComparisonFinishedSentence(
                new ProfileReporter.ComparisonReport { StoppedForTransmit = true }, "C:\\r.txt"));
            sb.AppendLine(ProfileReporter.RestoreFinishedSentence(
                new ProfileReporter.RestoreGradeExport { WalkRan = true, StoppedForTransmit = true }, "C:\\c.ini"));
            ProfileReporter.AppendComparisonStopped(sb, o);
            return sb.ToString();
        }

        [Fact]
        public void Keying_part_way_stops_the_walk_puts_nothing_back_and_names_no_profile()
        {
            // Red on 7023bd81, which said "The last global profile sent was A;
            // your original, Home, was not put back."
            var radio = new ScriptedRadio { KeyAfterLoading = n => n == "A" };

            var o = radio.Walk(ProfileTypes.global, "Home", "A", "B", "C");

            Assert.Equal(new[] { "A" }, radio.Loads);
            Assert.True(o.StoppedForTransmit);
            Assert.Equal("B", o.StoppedBefore);
            Assert.False(o.RestoreAttempted);
            Assert.Equal(StopLine, radio.Spoken.Last());
            foreach (string spoken in radio.Spoken.Skip(1))
            {
                Assert.DoesNotContain("Home", spoken);
                Assert.DoesNotContain("A;", spoken);
            }
        }

        [Fact]
        public void A_load_refused_before_dispatch_is_never_called_sent_anywhere()
        {
            // Codex's round-three blocker 1. SelectProfileGuarded can refuse a
            // load before it is enqueued (the change-nothing gate armed
            // mid-walk; the ownership gate); the walk sees only "not
            // confirmed". Nothing it produces may then say "sent".
            var radio = new ScriptedRadio { KeyAfterLoading = n => n == "A", Confirms = _ => false };
            var o = radio.Walk(ProfileTypes.tx, "Barefoot", "A", "B");

            string all = AllStopOutput(radio, o);
            Assert.DoesNotMatch(@"(?i)\bsent\b", all);
            // The file keeps the evidence, labelled for what it is.
            Assert.Contains("Last TX profile the walk requested: A (requested only; it may have been refused "
                + "before it reached the radio, or not applied).", all);
        }

        [Fact]
        public void The_app_s_recorded_selection_is_never_called_the_original()
        {
            // Codex's round-three blocker 2: the getter may hold the app's own
            // earlier optimistic request, so it is labelled as that, and the
            // spoken words do not use it at all.
            var radio = new ScriptedRadio { KeyAfterLoading = n => n == "A" };
            var o = radio.Walk(ProfileTypes.global, "Home", "A", "B");

            string all = AllStopOutput(radio, o);
            Assert.DoesNotMatch(@"(?i)\boriginal,", all);
            Assert.DoesNotContain("your original", all, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("App's last recorded global selection before the walk: Home (may be the app's own "
                + "earlier request, not the radio's state).", all);
            Assert.Contains("Which profile the radio is on now is not known.", all);
            Assert.DoesNotContain("Home", string.Join(" ", radio.Spoken));
        }

        [Fact]
        public void Keyed_after_the_LAST_profile_is_the_same_stop_and_the_same_words()
        {
            var radio = new ScriptedRadio { KeyAfterLoading = n => n == "B" };

            var o = radio.Walk(ProfileTypes.tx, "Barefoot", "A", "B");

            Assert.Equal(new[] { "A", "B" }, radio.Loads);
            Assert.True(o.StoppedForTransmit);
            Assert.Null(o.StoppedBefore);
            Assert.False(o.RestoreAttempted);
            Assert.Equal(StopLine, radio.Spoken.Last());

            var sb = new System.Text.StringBuilder();
            ProfileReporter.AppendComparisonStopped(sb, o);
            Assert.Contains("The radio was keyed after the last TX profile was requested, so nothing was put back.", sb.ToString());
        }

        [Fact]
        public void Keyed_before_the_first_load_gets_the_same_line()
        {
            var radio = new ScriptedRadio { Keyed = true };
            var o = radio.Walk(ProfileTypes.global, "Home", "A", "B");
            Assert.Empty(radio.Loads);
            Assert.True(o.StoppedForTransmit);
            Assert.False(o.RestoreAttempted);
            Assert.Equal(new[] { StopLine }, radio.Spoken);
            var sb = new System.Text.StringBuilder();
            ProfileReporter.AppendComparisonStopped(sb, o);
            Assert.Contains("Last global profile the walk requested: none — no load was requested.", sb.ToString());
        }

        [Fact]
        public void The_finished_sentences_after_a_stop_name_no_profile_and_claim_no_state()
        {
            Assert.Equal("Profile report saved to C:\\r.txt. Profile loading stopped because the radio was keyed, so the "
                + "comparison may be incomplete; the report says what was compared. Your profile may not be the one you "
                + "started with; load it again from Profiles, in the Radio menu, if needed.",
                ProfileReporter.ComparisonFinishedSentence(
                    new ProfileReporter.ComparisonReport { StoppedForTransmit = true, AnyLoadRequested = true }, "C:\\r.txt"));
            Assert.Equal("Restore capture written to C:\\c.ini. Profile loading stopped because the radio was keyed, so the "
                + "capture may be incomplete; the file says which profiles it holds. Your profile may not be the one you "
                + "started with; load it again from Profiles, in the Radio menu, if needed.",
                ProfileReporter.RestoreFinishedSentence(
                    new ProfileReporter.RestoreGradeExport { WalkRan = true, StoppedForTransmit = true, AnyLoadRequested = true },
                    "C:\\c.ini"));
            // Whatever the put-back flags say, a stopped run never speaks "back
            // on" or "not back on everything".
            foreach (bool back in new[] { true, false })
            {
                string c = ProfileReporter.ComparisonFinishedSentence(
                    new ProfileReporter.ComparisonReport { StoppedForTransmit = true, AnyLoadRequested = back }, "p");
                string r = ProfileReporter.RestoreFinishedSentence(
                    new ProfileReporter.RestoreGradeExport { WalkRan = true, StoppedForTransmit = true, AnyLoadRequested = back }, "p");
                foreach (string s in new[] { c, r })
                {
                    Assert.DoesNotContain("back on", s);
                    Assert.DoesNotContain("reconnect", s, StringComparison.OrdinalIgnoreCase);
                }
            }

            Assert.Equal("Profile report saved to C:\\r.txt",
                ProfileReporter.ComparisonFinishedSentence(new ProfileReporter.ComparisonReport(), "C:\\r.txt"));
        }

        // ------------------------------------------------------------------
        // Round six (Codex reverify5): "sent back" is unprovable too — Done
        // means queued, and FlexLib's setter can skip the send. A run that
        // requested any load ends with the manual route and claims nothing
        // about what reached the radio.
        // ------------------------------------------------------------------

        private const string Pointer = "Your profile may not be the one you started with; load it again from "
            + "Profiles, in the Radio menu, if needed.";

        [Fact]
        public void A_run_that_requested_any_load_ends_with_the_pointer_and_claims_nothing()
        {
            // Red on a74f6009, which said "The profiles recorded before it
            // began were sent back to the radio."
            Assert.Equal("Profile report saved to C:\\r.txt. " + Pointer,
                ProfileReporter.ComparisonFinishedSentence(
                    new ProfileReporter.ComparisonReport { AnyLoadRequested = true }, "C:\\r.txt"));
            Assert.Equal("Restore capture written to C:\\c.ini. " + Pointer,
                ProfileReporter.RestoreFinishedSentence(
                    new ProfileReporter.RestoreGradeExport { WalkRan = true, AnyLoadRequested = true }, "C:\\c.ini"));
        }

        [Fact]
        public void A_run_that_requested_no_load_says_only_the_saved_line()
        {
            Assert.Equal("Profile report saved to C:\\r.txt",
                ProfileReporter.ComparisonFinishedSentence(new ProfileReporter.ComparisonReport(), "C:\\r.txt"));
            // Provable only because no load was even attempted.
            Assert.Equal("Restore capture written to C:\\c.ini. No profile was sent to the radio, so it was not moved.",
                ProfileReporter.RestoreFinishedSentence(
                    new ProfileReporter.RestoreGradeExport { WalkRan = true, AnyLoadRequested = false }, "C:\\c.ini"));
        }

        [Fact]
        public void A_stopped_run_keeps_its_stop_sentences()
        {
            Assert.Equal("Profile report saved to C:\\r.txt. Profile loading stopped because the radio was keyed, so the "
                + "comparison may be incomplete; the report says what was compared. " + Pointer,
                ProfileReporter.ComparisonFinishedSentence(
                    new ProfileReporter.ComparisonReport { StoppedForTransmit = true, AnyLoadRequested = true }, "C:\\r.txt"));
            Assert.Equal("Restore capture written to C:\\c.ini. Profile loading stopped because the radio was keyed, so the "
                + "capture may be incomplete; the file says which profiles it holds. " + Pointer,
                ProfileReporter.RestoreFinishedSentence(
                    new ProfileReporter.RestoreGradeExport { WalkRan = true, StoppedForTransmit = true, AnyLoadRequested = true }, "C:\\c.ini"));
        }

        [Fact]
        public void The_file_says_a_put_back_was_requested_and_that_its_arrival_is_not_known()
        {
            var radio = new ScriptedRadio();
            var o = radio.Walk(ProfileTypes.tx, "Barefoot", "A", "B");
            Assert.True(o.RestoreAttempted);

            string note = "A put-back of Barefoot, the app's last recorded TX selection before the walk (it may be the "
                + "app's own earlier request), was requested; whether it reached the radio is not known.";
            Assert.Equal(note, ProfileReporter.PutBackNote(o));
            var sb = new System.Text.StringBuilder();
            ProfileReporter.AppendComparisonPutBack(sb, o);
            Assert.Contains(note + Environment.NewLine + Pointer, sb.ToString());
            Assert.Equal(note + " " + Pointer.TrimEnd('.'), ProfileReporter.AfterTheWalkValue(o));
        }

        [Fact]
        public void A_refused_put_back_reads_exactly_like_any_other_requested_one()
        {
            // Whether a request was refused, queued, skipped by FlexLib's
            // cache or applied, the file says only that it was requested.
            var refused = new ScriptedRadio { Refuses = n => n == "Home" };
            var queued = new ScriptedRadio();
            var a = refused.Walk(ProfileTypes.global, "Home", "A", "B");
            var b = queued.Walk(ProfileTypes.global, "Home", "A", "B");
            Assert.Equal(ProfileReporter.PutBackNote(b), ProfileReporter.PutBackNote(a));
            Assert.Equal(ProfileReporter.AfterTheWalkValue(b), ProfileReporter.AfterTheWalkValue(a));
        }

        [Fact]
        public void No_starting_selection_recorded_says_no_put_back_was_requested_and_gives_the_pointer()
        {
            // Codex's reverify5: round five's wording claimed a recorded
            // profile existed. It now says none was recorded.
            var radio = new ScriptedRadio();
            var o = radio.Walk(ProfileTypes.global, null, "A", "B");
            Assert.False(o.RestoreAttempted);
            Assert.Equal("No global selection was recorded before the walk, so no put-back was requested.",
                ProfileReporter.PutBackNote(o));
            var sb = new System.Text.StringBuilder();
            ProfileReporter.AppendComparisonPutBack(sb, o);
            Assert.Contains("No global selection was recorded before the walk, so no put-back was requested."
                + Environment.NewLine + Pointer, sb.ToString());

            var empty = new ScriptedRadio().Walk(ProfileTypes.mic, "", "A");
            Assert.Equal("The mic selection recorded before the walk was empty, so no put-back was requested.",
                ProfileReporter.PutBackNote(empty));
        }

        [Fact]
        public void A_walk_that_requested_no_load_writes_no_put_back_note()
        {
            var o = new ScriptedRadio { Keyed = false }.Walk(ProfileTypes.global, "Home");
            Assert.Equal(0, o.LoadAttempts);
            Assert.Null(ProfileReporter.PutBackNote(o));
            var sb = new System.Text.StringBuilder();
            ProfileReporter.AppendComparisonPutBack(sb, o);
            Assert.Equal("", sb.ToString());
        }

        [Fact]
        public void Sent_back_and_confirmed_appear_in_no_spoken_text_and_no_file()
        {
            string lexicon = Read("Radios/Lexicon/settings.json");
            string src = Read("Radios/ProfileReporter.cs");
            Assert.DoesNotContain("sent back", lexicon, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sent back", src, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("confirmed by the radio", src);
            Assert.DoesNotContain("RestoreSent", src);
            Assert.DoesNotContain("EverythingSentBack", src);

            // And in what a scripted walk actually produces.
            var radio = new ScriptedRadio();
            var o = radio.Walk(ProfileTypes.global, "Home", "A", "B");
            var sb = new System.Text.StringBuilder();
            ProfileReporter.AppendComparisonPutBack(sb, o);
            string all = string.Join("\n", radio.Spoken) + sb + ProfileReporter.AfterTheWalkValue(o);
            Assert.DoesNotContain("sent back", all, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("confirmed", all, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_queued_load_is_never_called_sent()
        {
            string src = Read("Radios/ProfileReporter.cs");
            Assert.Contains("var dispatched = rig.SelectProfileGuarded(prof);", src);
            Assert.DoesNotContain("ProfileLoadResult.NotSent", src);
            Assert.DoesNotContain("SentAndSettled", src);
        }

        [Fact]
        public void The_stop_path_carries_no_sent_or_original_claim_in_the_source_either()
        {
            // SOURCE PIN for the restore file's stop lines, which need a
            // connected radio to execute: they use the labelled evidence and
            // the fixed stop sentence, and nothing that names a "sent" profile.
            string src = Read("Radios/ProfileReporter.cs");
            Assert.DoesNotContain("LastSent", src);
            Assert.DoesNotContain("WhereTheRadioWasLeft", src);
            Assert.Contains("foreach (var (key, value) in StoppedWalkEvidence(outcome))", src);
            Assert.Contains("profile the radio is on now = not known", src);
            string lexicon = Read("Radios/Lexicon/settings.json");
            Assert.DoesNotContain("profile sent was", lexicon);
            Assert.DoesNotContain("your original", lexicon);
        }

        [Fact]
        public void Never_keyed_the_walk_is_unchanged()
        {
            var radio = new ScriptedRadio();
            var o = radio.Walk(ProfileTypes.global, "Home", "A", "B");
            Assert.Equal(new[] { "A", "B", "Home" }, radio.Loads);
            Assert.False(o.StoppedForTransmit);
            Assert.True(o.RestoreAttempted);
            Assert.Equal(new[]
            {
                "Loading global profile 1 of 2: A",
                "Loading global profile 2 of 2: B",
                "Asking the radio to load the global profile recorded before the walk: Home",
            }, radio.Spoken);
        }

        [Fact]
        public void The_walk_takes_its_start_as_FlexLib_holds_it_and_never_folds_unreported_into_none()
        {
            // Round three's fix, kept. SOURCE PIN: the production read needs a
            // FlexLib Radio, which no test here can build.
            string src = Read("Radios/ProfileReporter.cs");
            Assert.DoesNotContain("Selection ?? \"\"", src);
            Assert.Contains("outcome.OriginalSelection = StartingSelection(rig.theRadio, profileType);", src);
            Assert.Contains("case ProfileTypes.global: return radio.ProfileGlobalSelection;", src);
            string flex = Read("FlexLib_API/FlexLib/Radio.cs");
            Assert.Contains("UpdateProfileListSelection(profile_type, value);", flex);

            // And the file keeps the three states apart.
            var none = new ProfileReporter.ProfileWalkOutcome { ProfileType = ProfileTypes.mic, OriginalSelection = "" };
            var unknown = new ProfileReporter.ProfileWalkOutcome { ProfileType = ProfileTypes.mic, OriginalSelection = null };
            Assert.Contains(("app's last recorded mic selection before the walk", "recorded as empty"),
                ProfileReporter.StoppedWalkEvidence(none));
            Assert.Contains(("app's last recorded mic selection before the walk", "none recorded"),
                ProfileReporter.StoppedWalkEvidence(unknown));
        }

        [Fact]
        public void The_round_one_wait_and_its_sentences_are_gone()
        {
            Assert.Null(typeof(ProfileReporter).GetField("UnkeyWaitMs",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static));
            string src = Read("Radios/ProfileReporter.cs");
            Assert.DoesNotContain("waitUntilUnkeyed", src);
            Assert.DoesNotContain("PutBackHeldForTransmit", src);
            string lexicon = Read("Radios/Lexicon/settings.json");
            Assert.DoesNotContain("within a minute", lexicon);
            Assert.DoesNotContain("goes back on", lexicon);
        }

        // ------------------------------------------------------------------
        // Round two: an unreadable list is not an empty one, and the restore
        // refusal names only what will load
        // ------------------------------------------------------------------

        [Fact]
        public void An_unreadable_list_is_spoken_as_not_arriving_never_as_nothing_to_compare()
        {
            // Codex's round-one blocker 2. Red on 631072ae.
            var both = Lists(3, 3, 0);
            both[ProfileTypes.global].Reported = false;
            both[ProfileTypes.tx].Reported = false;
            Assert.Equal("Writing the profile report. The radio's global and TX profile lists did not arrive, "
                + "so nothing is compared and the radio is left as it is.",
                ProfileReporter.ComparisonStartSentence(ProfileReporter.MakeComparisonPlan(true, both)));

            var one = Lists(3, 1, 0);
            one[ProfileTypes.global].Reported = false;
            Assert.Equal("Writing the profile report. The radio's global profile list did not arrive, "
                + "so nothing is compared and the radio is left as it is.",
                ProfileReporter.ComparisonStartSentence(ProfileReporter.MakeComparisonPlan(true, one)));

            // An offline radio could not ask for either list: also not "nothing".
            string offline = ProfileReporter.ComparisonStartSentence(
                ProfileReporter.MakeComparisonPlan(true, ProfileReporter.PlanComparison(MakeRig()).Lists));
            Assert.DoesNotContain("nothing to compare", offline);
            Assert.Contains("did not arrive", offline);

            // Both arrived and neither holds two: then, and only then.
            Assert.Contains("There is nothing to compare",
                ProfileReporter.ComparisonStartSentence(ProfileReporter.MakeComparisonPlan(true, Lists(1, 1, 0))));
        }

        [Fact]
        public void The_restore_keyed_refusal_names_only_what_the_capture_would_load()
        {
            // Codex's round-one blocker 3. Red on 631072ae, which said "this
            // export loads every stored profile" although two lists had not
            // arrived.
            var partial = Lists(1, 0, 0);
            partial[ProfileTypes.tx].Reported = false;
            partial[ProfileTypes.mic].Reported = false;
            string refusal = ProfileReporter.RestoreTransmitRefusal(ProfileReporter.MakeRestorePlan(true, partial), keyed: true);
            Assert.Equal("Not while transmitting. Unkey first — this capture loads your stored global profiles on the radio, "
                + "and profiles change transmit settings.", refusal);
            Assert.DoesNotContain("every stored profile", refusal);
            Assert.Contains("global, TX and mic",
                ProfileReporter.RestoreTransmitRefusal(ProfileReporter.MakeRestorePlan(true, Lists(1, 1, 1)), keyed: true));
        }

        [Fact]
        public void Both_walkers_reach_the_radio_only_through_the_guarded_walk()
        {
            // SOURCE PIN for what the scripted radio cannot see: the production
            // WalkProfiles hands RunWalk the real transmit state and the real
            // load, and both reports stop starting new types once a walk
            // stopped. Red against 24154dab, whose walker never read transmit.
            string src = Read("Radios/ProfileReporter.cs");
            Assert.Contains("() => rig.Transmit,", src);
            // Round three: the walk is handed no way to ask where the radio is.
            Assert.DoesNotContain("ReadSelectionSafely", src);
            Assert.Contains("name => LoadProfileAndWait(rig, profileType, name),", src);
            Assert.Equal(1, CountOf(src, "LoadProfileAndWait(rig, profileType, name)"));
            // Both reports stop starting new types after a stopped walk: the
            // comparison's loop and the restore capture's loop each record it.
            Assert.Equal(2, CountOf(src, "result.StoppedForTransmit = true;"));
            Assert.Contains("foreach (var ptype in plan.TypesToLoad)", src);
        }

        private static int CountOf(string s, string token)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(token, i, StringComparison.Ordinal)) >= 0) { n++; i += token.Length; }
            return n;
        }

        // ------------------------------------------------------------------
        // The menu items: reserve at the press, plan before speaking, the walk
        // off the UI thread, and the path still spoken and the file opened
        // ------------------------------------------------------------------

        private const string ComparisonItem = "AddWired(tools, \"Profile comparison report, loads each one\"";
        private const string RestoreItem = "AddWired(tools, \"Full restore capture, loads every profile\"";

        /// <summary>The text of one menu item's lambda: from its AddWired to
        /// the next AddWired or separator.</summary>
        private static string MenuItemBody(string menu, string item)
        {
            int start = menu.IndexOf(item, StringComparison.Ordinal);
            Assert.True(start >= 0, "menu item not found: " + item);
            int next = menu.IndexOf("AddWired(tools,", start + item.Length, StringComparison.Ordinal);
            int sep = menu.IndexOf("AddSep(tools)", start + item.Length, StringComparison.Ordinal);
            int end = new[] { next, sep }.Where(i => i > 0).DefaultIfEmpty(menu.Length).Min();
            return menu.Substring(start, end - start);
        }

        private static int At(string body, string token)
        {
            int i = body.IndexOf(token, StringComparison.Ordinal);
            Assert.True(i >= 0, "expected '" + token + "' in the menu item's body");
            return i;
        }

        [Fact]
        public void The_comparison_item_reserves_at_the_press_plans_before_it_speaks_and_still_speaks_and_opens_the_file()
        {
            // A SOURCE PIN, said plainly: Radios.Tests cannot run a WPF menu,
            // so this reads the lambda's text. It proves the ORDER the code is
            // written in, not that NVDA says anything. The bench half does that.
            string body = MenuItemBody(Read("JJFlexWpf/NativeMenuBar.cs"), ComparisonItem);

            int reserve = At(body, "ProfileReporter.TryReserveWalk()");
            int task = At(body, "System.Threading.Tasks.Task.Run(");
            int owns = At(body, "using (reservation)");
            int plan = At(body, "ProfileReporter.PlanComparison(reportRig)");
            int refusal = At(body, "ProfileReporter.ComparisonTransmitRefusal(");
            int start = At(body, "ProfileReporter.ComparisonStartSentence(plan)");
            int settle = At(body, "System.Threading.Thread.Sleep(4000)");
            int walk = At(body, "ProfileReporter.GenerateReport(reservation, plan, reportRig,");
            int save = At(body, "ProfileReporter.SaveReport(report.Text)");
            int open = At(body, "System.Diagnostics.Process.Start(");
            int spoken = At(body, "ProfileReporter.ComparisonFinishedSentence(report, path)");

            Assert.True(reserve < task, "the walk is reserved at the press, before the task");
            Assert.True(task < owns && owns < plan, "the task owns the reservation");
            Assert.True(plan < refusal && refusal < start, "the plan decides both sentences, the refusal first");
            Assert.True(start < settle && settle < walk, "the walk runs inside the task, after the settle");
            Assert.True(walk < save && save < open && open < spoken,
                "save, then open the file, then speak the path, all after the walk");

            // Nothing is spoken at the press but busy: no start sentence before
            // the plan, and no advisory busy check that a second press could pass.
            Assert.DoesNotContain("ProfileReporter.WalkInProgress", body);
            Assert.Equal(walk, body.LastIndexOf("ProfileReporter.GenerateReport(", StringComparison.Ordinal));
            Assert.True(At(body, "reservation.Dispose();") > walk, "a task that never started releases the reservation");
            Assert.True(At(body, "catch (Exception ex)") > walk);
        }

        [Fact]
        public void The_restore_item_reserves_at_the_press_and_plans_before_it_speaks()
        {
            // Same reader, the other item — and the positive control for the
            // extractor: it must find the restore capture's own shape. Red on
            // 0cae6353, which spoke the fixed "every stored profile is loaded"
            // sentence at the press whatever would load.
            string body = MenuItemBody(Read("JJFlexWpf/NativeMenuBar.cs"), RestoreItem);
            int reserve = At(body, "ProfileReporter.TryReserveWalk()");
            int task = At(body, "System.Threading.Tasks.Task.Run(");
            int owns = At(body, "using (reservation)");
            int plan = At(body, "ProfileReporter.PlanRestoreCapture(restoreRig)");
            int refusal = At(body, "ProfileReporter.RestoreTransmitRefusal(");
            int start = At(body, "ProfileReporter.RestoreStartSentence(plan)");
            int settle = At(body, "System.Threading.Thread.Sleep(4000)");
            int walk = At(body, "ProfileReporter.GenerateRestoreGradeExport(reservation, plan, restoreRig,");
            int refused = At(body, "export.Refused == ProfileReporter.WalkRefusal.Transmitting");
            // Round three (Codex blocker 2): the post-settle refusal is the plan's
            // own, never the legacy "every stored profile" sentence. Red on 48433cfe.
            Assert.True(At(body, "ProfileReporter.RestoreRefusedSentence(export, plan)") > refused);
            Assert.DoesNotContain("\"settings.restore_export.not_while_transmitting\"", body);
            Assert.True(reserve < task && task < owns && owns < plan && plan < refusal && refusal < start
                && start < settle && settle < walk && walk < refused);
            Assert.DoesNotContain("\"settings.restore_export.start\"", body);
            Assert.DoesNotContain("ProfileReporter.WalkInProgress", body);
            Assert.DoesNotContain("ProfileReporter.GenerateReport(", body);
            Assert.True(At(body, "reservation.Dispose();") > walk);
        }

        [Fact]
        public void The_restore_capture_loads_every_kind_the_radio_holds_any_of_and_nothing_under_the_hold()
        {
            Assert.Equal(new[] { ProfileTypes.global, ProfileTypes.tx, ProfileTypes.mic },
                ProfileReporter.TypesTheRestoreCaptureLoads(true, Lists(1, 1, 1)));
            Assert.Equal(new[] { ProfileTypes.global, ProfileTypes.mic },
                ProfileReporter.TypesTheRestoreCaptureLoads(true, Lists(2, 0, 3)));
            Assert.Empty(ProfileReporter.TypesTheRestoreCaptureLoads(true, Lists(0, 0, 0)));
            Assert.Empty(ProfileReporter.TypesTheRestoreCaptureLoads(false, Lists(4, 4, 4)));
            var unreported = Lists(3, 3, 3);
            unreported[ProfileTypes.tx].Reported = false;
            Assert.Equal(new[] { ProfileTypes.global, ProfileTypes.mic },
                ProfileReporter.TypesTheRestoreCaptureLoads(true, unreported));
        }

        [Fact]
        public void The_restore_start_sentence_says_only_what_will_load()
        {
            Assert.Equal(
                "Capturing settings for restore. Your stored global, TX and mic profiles are loaded on the radio in turn, "
                + "and you will hear things change as it goes. This takes a minute or two; progress is spoken.",
                ProfileReporter.RestoreStartSentence(ProfileReporter.MakeRestorePlan(true, Lists(2, 1, 1))));
            Assert.Contains("Your stored global and mic profiles are loaded",
                ProfileReporter.RestoreStartSentence(ProfileReporter.MakeRestorePlan(true, Lists(1, 0, 4))));
            Assert.Contains("Your stored TX profiles are loaded",
                ProfileReporter.RestoreStartSentence(ProfileReporter.MakeRestorePlan(true, Lists(0, 1, 0))));
            Assert.DoesNotContain("Every stored profile",
                ProfileReporter.RestoreStartSentence(ProfileReporter.MakeRestorePlan(true, Lists(2, 2, 2))));
        }

        [Fact]
        public void When_the_restore_capture_will_load_nothing_its_start_sentence_promises_no_loading()
        {
            string none = ProfileReporter.RestoreStartSentence(ProfileReporter.MakeRestorePlan(true, Lists(0, 0, 0)));
            Assert.Equal("Capturing settings for restore. No stored profiles could be read from the radio, so it is left as it is.", none);
            string held = ProfileReporter.RestoreStartSentence(ProfileReporter.MakeRestorePlan(false, Lists(3, 3, 3)));
            Assert.Equal("Capturing settings for restore. Change nothing is on for this radio, so it is left as it is "
                + "and the file holds live settings only.", held);
            foreach (string sentence in new[] { none, held })
                Assert.DoesNotContain("loaded on the radio", sentence);

            // An offline radio reads no list, so it plans to load nothing and
            // is told so, never "every stored profile is loaded".
            var offline = ProfileReporter.PlanRestoreCapture(MakeRig());
            Assert.False(offline.WillLoad);
            Assert.Equal(none, ProfileReporter.RestoreStartSentence(offline));

            // A keyed radio whose capture would load nothing is not refused.
            Assert.Null(ProfileReporter.RestoreTransmitRefusal(offline, keyed: true));
            Assert.Equal(Radios.Lexicon.Get("settings.restore_export.not_while_transmitting_kinds", ("kinds", "global")),
                ProfileReporter.RestoreTransmitRefusal(ProfileReporter.MakeRestorePlan(true, Lists(1, 0, 0)), keyed: true));
        }

        // ------------------------------------------------------------------
        // The flag is released on every exit path
        // ------------------------------------------------------------------

        [Fact]
        public void The_flag_is_released_after_an_ordinary_report_so_the_next_one_runs()
        {
            var rig = MakeRig();
            Assert.NotNull(ProfileReporter.GenerateReport(rig));
            Assert.False(ProfileReporter.WalkInProgress);
            // A leaked flag would refuse every later walk until restart,
            // including the restore capture before a real reset.
            Assert.NotNull(ProfileReporter.GenerateReport(rig));
            Assert.NotNull(ProfileReporter.GenerateRestoreGradeExport(rig));
            Assert.False(ProfileReporter.WalkInProgress);
        }

        [Fact]
        public void The_flag_is_released_when_the_report_throws_mid_walk()
        {
            var rig = MakeRig();
            ProfileReporter.WalkHeldProbe = who =>
            {
                if (who == "comparison report")
                    throw new InvalidOperationException("the radio went away mid-walk");
            };
            try
            {
                Assert.Throws<InvalidOperationException>(() => ProfileReporter.GenerateReport(rig));
            }
            finally { ProfileReporter.WalkHeldProbe = null; }

            Assert.False(ProfileReporter.WalkInProgress);
            Assert.NotNull(ProfileReporter.GenerateReport(rig));
        }

        // ------------------------------------------------------------------
        // Every summary documents the member under it
        // ------------------------------------------------------------------

        [Fact]
        public void No_summary_in_the_reporter_is_orphaned_and_the_restore_capture_has_its_own()
        {
            // A summary that ends and is followed straight by another summary
            // documents nothing: the compiler attaches the LAST one, so the
            // first is lost. That is how GenerateRestoreGradeExport carried
            // CanShowScreensaver's summary while its own went nowhere (#469).
            string[] lines = Read("Radios/ProfileReporter.cs").Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                Assert.False(lines[i].Trim() == "/// </summary>" && lines[i + 1].Trim() == "/// <summary>",
                    "an orphaned <summary> ends at line " + (i + 1) + " of ProfileReporter.cs");
            }

            int method = Array.FindIndex(lines, l => l.Contains("public static RestoreGradeExport GenerateRestoreGradeExport("));
            Assert.True(method > 0, "GenerateRestoreGradeExport not found");
            int open = Array.FindLastIndex(lines, method, l => l.Trim() == "/// <summary>");
            Assert.Contains("Build the restore-grade capture", lines[open + 1]);
            for (int i = open; i < method; i++)
                Assert.StartsWith("///", lines[i].Trim());
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that "
                + "cannot find its subject proves nothing about it.");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
