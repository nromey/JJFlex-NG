using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The one process-wide profile-walk flag, shared by every class that
    /// drives either walking entry point. xUnit runs classes in parallel, and
    /// a class holding the flag on purpose (below) would otherwise refuse a
    /// restore capture another class expected to run.
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class ProfileWalkFlagCollection
    {
        public const string Name = "Profile walk in-flight flag";
    }

    /// <summary>
    /// #469: the profile comparison report walks every stored profile the way
    /// the restore capture does, and now carries the same guards: it takes
    /// the in-flight flag for its whole run and releases it on every exit,
    /// and it refuses while the radio is keyed before anything is read or
    /// loaded.
    ///
    /// <para>No radio under test, so no profile is ever actually loaded here.
    /// What these tests drive is the guard itself, through the real entry
    /// points: each walk is started while the OTHER one genuinely holds the
    /// flag, via <see cref="ProfileReporter.WalkHeldProbe"/>, which production
    /// code calls the moment it has taken the flag.</para>
    /// </summary>
    [Collection(ProfileWalkFlagCollection.Name)]
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
        // Keyed transmit refuses before anything else
        // ------------------------------------------------------------------

        [Fact]
        public void Keyed_transmit_refuses_the_report_before_the_flag_is_taken()
        {
            // Red against 4d7cc805: a keyed radio got the whole report, walk
            // included, because nothing on this path asked.
            var rig = MakeRig();
            Key(rig, true);
            bool held = false;
            ProfileReporter.WalkHeldProbe = who => held = true;
            try
            {
                Assert.Null(ProfileReporter.GenerateReport(rig, null, out var why));
                Assert.Equal(ProfileReporter.WalkRefusal.Transmitting, why);
            }
            finally { ProfileReporter.WalkHeldProbe = null; }

            // Refused before the flag: no walk was ever held, so nothing ran
            // that could load a profile, and nothing was left to release.
            Assert.False(held);
            Assert.False(ProfileReporter.WalkInProgress);

            // Unkeyed, the same rig is reported on — the refusal is about the
            // key, not the rig.
            Key(rig, false);
            Assert.NotNull(ProfileReporter.GenerateReport(rig));
        }

        [Fact]
        public void Transmit_is_named_as_the_refusal_even_while_another_walk_holds_the_flag()
        {
            // Transmit is asked first, so the operator hears the reason that
            // needs their hands (unkey) rather than "wait for the other walk",
            // and the held flag is not touched.
            var rig = MakeRig();
            Key(rig, true);
            Flag.SetValue(null, 1);
            try
            {
                Assert.Null(ProfileReporter.GenerateReport(rig, null, out var why));
                Assert.Equal(ProfileReporter.WalkRefusal.Transmitting, why);
                Assert.True(ProfileReporter.WalkInProgress);
            }
            finally { Flag.SetValue(null, 0); }
        }

        // ------------------------------------------------------------------
        // One answer to "will this load profiles?"
        // ------------------------------------------------------------------

        [Fact]
        public void A_radio_never_opted_in_is_not_walked_and_the_report_says_so()
        {
            // The menu picks its start sentence from ComparisonWillLoadProfiles,
            // and the report decides its skip from the same call, so the
            // sentence cannot promise a walk the report then declines. What
            // this cannot reach: the TRUE answer needs a connected radio with a
            // serial the operator opted in, which no test here has.
            var rig = MakeRig();
            Assert.False(ProfileReporter.ComparisonWillLoadProfiles(rig));
            string report = ProfileReporter.GenerateReport(rig);
            Assert.Contains("PROFILE COMPARISON SKIPPED", report);
            Assert.DoesNotContain("PROFILE COMPARISON" + Environment.NewLine, report.Replace("PROFILE COMPARISON SKIPPED", ""));

            rig.SetChangeNothingActive(true);
            Assert.False(ProfileReporter.ComparisonWillLoadProfiles(rig));
            Assert.Contains("Change nothing is on for this radio", ProfileReporter.GenerateReport(rig));
        }

        // ------------------------------------------------------------------
        // The menu item: refusals at the press, the walk off the UI thread,
        // and the path still spoken and the file still opened
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
        public void The_menu_item_refuses_first_then_runs_the_walk_in_the_background_and_still_speaks_and_opens_the_file()
        {
            // A SOURCE PIN, said plainly: Radios.Tests cannot run a WPF menu,
            // so this reads the lambda's text. It proves the ORDER the code is
            // written in — refusals before the background task, the walk only
            // inside it, the save, the file opening and the spoken path after
            // the walk — not that NVDA says the path. The bench half does that.
            string body = MenuItemBody(Read("JJFlexWpf/NativeMenuBar.cs"), ComparisonItem);

            int transmit = At(body, "reportRig.Transmit");
            int busy = At(body, "ProfileReporter.WalkInProgress");
            int task = At(body, "System.Threading.Tasks.Task.Run(");
            int settle = At(body, "System.Threading.Thread.Sleep(4000)");
            int walk = At(body, "ProfileReporter.GenerateReport(");
            int save = At(body, "ProfileReporter.SaveReport(report)");
            int spoken = At(body, "\"settings.profile.report_saved\", (\"path\", path)");
            int open = At(body, "System.Diagnostics.Process.Start(");

            Assert.True(transmit < busy, "transmit must refuse before the busy check");
            Assert.True(busy < task, "both refusals come before anything starts");
            Assert.True(task < settle && settle < walk, "the walk runs inside the task, after the settle");
            // Open, THEN speak: the file's window arriving flushes anything
            // the screen reader was saying, so the path must come after it —
            // the order this item had before #469, when the open was
            // synchronous and the path came 500 ms later.
            Assert.True(walk < save && save < open && open < spoken,
                "save, then open the file, then speak the path, all after the walk");

            // Exactly one walk call, and it is the one inside the task: a
            // second, synchronous one would put the walk back on the UI thread.
            Assert.Equal(walk, body.LastIndexOf("ProfileReporter.GenerateReport(", StringComparison.Ordinal));
            Assert.Equal(save, body.LastIndexOf("ProfileReporter.SaveReport(", StringComparison.Ordinal));

            // A pool thread may not let an exception escape.
            Assert.True(At(body, "catch (Exception ex)") > walk);
        }

        [Fact]
        public void The_pin_reader_sees_the_restore_capture_built_the_same_way()
        {
            // Positive control for the extractor above: on the item this one
            // copies, the same reader finds the same shape. If the extractor
            // silently grabbed the wrong span, this would fail with it.
            string body = MenuItemBody(Read("JJFlexWpf/NativeMenuBar.cs"), RestoreItem);
            Assert.True(At(body, "restoreRig.Transmit") < At(body, "System.Threading.Tasks.Task.Run("));
            Assert.True(At(body, "System.Threading.Tasks.Task.Run(")
                < At(body, "ProfileReporter.GenerateRestoreGradeExport("));
            Assert.DoesNotContain("ProfileReporter.GenerateReport(", body);
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
