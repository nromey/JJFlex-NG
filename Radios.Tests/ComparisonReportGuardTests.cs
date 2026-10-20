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
                var refused = ProfileReporter.GenerateRestoreGradeExport(r, rig, null);
                Assert.Equal(ProfileReporter.WalkRefusal.Transmitting, refused.Refused);
                Assert.Null(refused.Text);

                // Under the change-nothing hold nothing is loaded, so it runs.
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
        // Repair round one, blocker 4: transmit is asked before every load,
        // and nothing is put back while keyed
        // ------------------------------------------------------------------

        /// <summary>A scripted radio for <see cref="ProfileReporter.RunWalk"/>:
        /// records every load, and fails the test if one is sent while keyed.</summary>
        private sealed class ScriptedRadio
        {
            public bool Keyed;
            public readonly List<string> Loads = new List<string>();
            public readonly List<string> Spoken = new List<string>();
            public int Waits;
            public Func<string, bool> KeyAfterLoading = _ => false;
            public Func<bool> Wait = () => false;

            public bool Load(string name)
            {
                Assert.False(Keyed, "a profile load was sent while keyed: " + name);
                Loads.Add(name);
                if (KeyAfterLoading(name)) Keyed = true;
                return true;
            }

            public ProfileReporter.ProfileWalkOutcome Walk(ProfileTypes type, string original, params string[] names)
            {
                var outcome = new ProfileReporter.ProfileWalkOutcome { ProfileType = type, OriginalSelection = original };
                ProfileReporter.RunWalk(outcome, names, Load, () => Keyed,
                    () => { Waits++; return Wait(); }, null, Spoken.Add, null);
                return outcome;
            }
        }

        [Fact]
        public void Keying_part_way_stops_the_walk_and_the_original_goes_back_only_after_unkey()
        {
            var radio = new ScriptedRadio { KeyAfterLoading = n => n == "A" };
            radio.Wait = () => { radio.Keyed = false; return true; };

            var o = radio.Walk(ProfileTypes.global, "Home", "A", "B", "C");

            Assert.Equal(new[] { "A", "Home" }, radio.Loads);
            Assert.True(o.StoppedForTransmit);
            Assert.Equal("B", o.StoppedBefore);
            Assert.True(o.PutBackWaitedForUnkey);
            Assert.True(o.RestoreAttempted && o.RestoreConfirmed);
            Assert.False(o.PutBackHeldForTransmit);
            Assert.Contains("Profile loading stopped because the radio is transmitting. "
                + "Your global profile goes back on if you unkey within a minute.", radio.Spoken);
        }

        [Fact]
        public void Still_keyed_after_the_wait_the_original_is_NOT_loaded_back_and_the_outcome_says_where_it_was_left()
        {
            var radio = new ScriptedRadio { KeyAfterLoading = n => n == "B", Wait = () => false };

            var o = radio.Walk(ProfileTypes.tx, "Barefoot", "A", "B", "C");

            Assert.Equal(new[] { "A", "B" }, radio.Loads);
            Assert.Equal(1, radio.Waits);
            Assert.True(o.PutBackHeldForTransmit);
            Assert.False(o.RestoreAttempted);
            Assert.Equal("B", o.LastLoaded);

            var sb = new System.Text.StringBuilder();
            ProfileReporter.AppendComparisonStopped(sb, o);
            string text = sb.ToString();
            Assert.Contains("Loading stopped before TX profile 'C'", text);
            Assert.Contains("TX profile 'Barefoot' was NOT loaded back while", text);
            Assert.Contains("The radio is still on 'B'", text);
        }

        [Fact]
        public void Keyed_again_the_moment_it_unkeys_still_puts_nothing_back()
        {
            var radio = new ScriptedRadio { KeyAfterLoading = n => n == "A" };
            radio.Wait = () => true;   // reports unkeyed, but the radio is keyed again
            var o = radio.Walk(ProfileTypes.global, "Home", "A", "B");
            Assert.Equal(new[] { "A" }, radio.Loads);
            Assert.True(o.PutBackHeldForTransmit);
        }

        [Fact]
        public void Keyed_before_the_first_load_moves_nothing_and_owes_nothing()
        {
            var radio = new ScriptedRadio { Keyed = true };
            var o = radio.Walk(ProfileTypes.global, "Home", "A", "B");
            Assert.Empty(radio.Loads);
            Assert.Equal(0, radio.Waits);
            Assert.True(o.StoppedForTransmit);
            Assert.False(o.RestoreAttempted);
            Assert.False(o.PutBackHeldForTransmit);
            Assert.Equal(new[] { "Profile loading stopped because the radio is transmitting." }, radio.Spoken);
        }

        [Fact]
        public void Keyed_only_at_the_end_waits_before_putting_back()
        {
            var radio = new ScriptedRadio { KeyAfterLoading = n => n == "B" };
            radio.Wait = () => { radio.Keyed = false; return true; };
            var o = radio.Walk(ProfileTypes.tx, "Barefoot", "A", "B");
            Assert.Equal(new[] { "A", "B", "Barefoot" }, radio.Loads);
            Assert.False(o.StoppedForTransmit);
            Assert.True(o.PutBackWaitedForUnkey);
            Assert.Contains("The radio is transmitting. Your TX profile goes back on if you unkey within a minute.", radio.Spoken);
        }

        [Fact]
        public void Never_keyed_the_walk_is_unchanged()
        {
            var radio = new ScriptedRadio();
            var o = radio.Walk(ProfileTypes.global, "Home", "A", "B");
            Assert.Equal(new[] { "A", "B", "Home" }, radio.Loads);
            Assert.Equal(0, radio.Waits);
            Assert.False(o.StoppedForTransmit || o.PutBackWaitedForUnkey || o.PutBackHeldForTransmit);
            Assert.True(o.RestoreConfirmed);
            Assert.Equal(new[]
            {
                "Loading global profile 1 of 2: A",
                "Loading global profile 2 of 2: B",
                "Putting back the global profile that was loaded: Home",
            }, radio.Spoken);
        }

        [Fact]
        public void The_wait_the_sentence_promises_is_the_wait_the_walk_takes()
        {
            Assert.Equal(60_000, ProfileReporter.UnkeyWaitMs);
            Assert.Contains("within a minute", Radios.Lexicon.Get("settings.profile_walk.put_back_waits_for_unkey"));
            Assert.Contains("within a minute", Radios.Lexicon.Get("settings.profile_walk.stopped_keyed_put_back_waits"));
        }

        [Fact]
        public void The_finished_sentences_say_when_loading_stopped_and_where_the_radio_was_left()
        {
            var ordinary = new ProfileReporter.ComparisonReport();
            Assert.Equal("Profile report saved to C:\\r.txt", ProfileReporter.ComparisonFinishedSentence(ordinary, "C:\\r.txt"));

            var stoppedBack = new ProfileReporter.ComparisonReport { StoppedForTransmit = true, EverythingPutBack = true };
            Assert.Equal("Profile report saved to C:\\r.txt. It stopped part-way because the radio was keyed, so the comparison "
                + "is incomplete. The radio is back on the profiles it started with.",
                ProfileReporter.ComparisonFinishedSentence(stoppedBack, "C:\\r.txt"));

            var stoppedLeft = new ProfileReporter.ComparisonReport { StoppedForTransmit = true, EverythingPutBack = false };
            Assert.Contains("Caution: the radio is not back on everything it started with",
                ProfileReporter.ComparisonFinishedSentence(stoppedLeft, "C:\\r.txt"));

            var export = new ProfileReporter.RestoreGradeExport { WalkRan = true, StoppedForTransmit = true, EverythingPutBack = false };
            Assert.Equal("Restore capture written to C:\\c.ini. It stopped part-way because the radio was keyed, so profiles "
                + "after that point are not in the file. Caution: the radio is not back on everything it started with — "
                + "the end of the file says what was left where.",
                ProfileReporter.RestoreFinishedSentence(export, "C:\\c.ini"));
            export.StoppedForTransmit = false; export.EverythingPutBack = true;
            Assert.Equal(Radios.Lexicon.Get("settings.restore_export.saved_put_back", ("path", "C:\\c.ini")),
                ProfileReporter.RestoreFinishedSentence(export, "C:\\c.ini"));
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
            Assert.Contains("() => FlexBase.await(() => !rig.Transmit, UnkeyWaitMs),", src);
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
        public void The_restore_item_reserves_at_the_press_too()
        {
            // Same reader, the other item — and the positive control for the
            // extractor: it must find the restore capture's own shape.
            string body = MenuItemBody(Read("JJFlexWpf/NativeMenuBar.cs"), RestoreItem);
            int transmit = At(body, "restoreRig.Transmit && ProfileReporter.RestoreCaptureWouldWalk(restoreRig)");
            int reserve = At(body, "ProfileReporter.TryReserveWalk()");
            int start = At(body, "\"settings.restore_export.start\"");
            int task = At(body, "System.Threading.Tasks.Task.Run(");
            int owns = At(body, "using (reservation)");
            int walk = At(body, "ProfileReporter.GenerateRestoreGradeExport(reservation, restoreRig,");
            int refused = At(body, "export.Refused == ProfileReporter.WalkRefusal.Transmitting");
            Assert.True(transmit < reserve && reserve < start && start < task && task < owns && owns < walk && walk < refused);
            Assert.DoesNotContain("ProfileReporter.WalkInProgress", body);
            Assert.DoesNotContain("ProfileReporter.GenerateReport(", body);
            Assert.True(At(body, "reservation.Dispose();") > walk);
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
