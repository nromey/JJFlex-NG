#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #517 repair one (Codex and the independent second check, 2026-11-09):
    /// every choice on Settings' Accessibility tab is saved where the
    /// application loads it, so it survives the first connect and a restart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect: the dialog saved <see cref="AccessibilityConfig"/> into the
    /// per-radio folder it is handed (<c>Radios</c> under the settings root),
    /// and both loads read the settings root. The same-folder round trip in
    /// <see cref="SliceArrowOrderTests"/> passed throughout, because it saves
    /// and loads in one folder. These tests read which folder each real
    /// caller uses from the source first, and then run THOSE folders.
    /// </para>
    /// <para>
    /// No window, no radio: files in a temporary folder only.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class AccessibilityChoicesHomeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-a11y-home-" + Guid.NewGuid().ToString("N"));
        private readonly AccessibilityConfig _before = AccessibilityConfig.Current;
        private const string Op = "N0CALL_Test_Operator";

        public AccessibilityChoicesHomeTests()
        {
            Directory.CreateDirectory(_root);
            AccessibilityConfig.ForgetHomeForTests();
            // A fresh object, so nothing here edits the one other tests hold.
            SetCurrent(new AccessibilityConfig());
        }

        private static void SetCurrent(AccessibilityConfig c)
            => typeof(AccessibilityConfig).GetProperty(nameof(AccessibilityConfig.Current))!.SetValue(null, c);

        public void Dispose()
        {
            AccessibilityConfig.ForgetHomeForTests();
            SetCurrent(_before);
            try { Directory.Delete(_root, true); } catch { /* temp */ }
        }

        private string RadiosFolder => Path.Combine(_root, "Radios");

        private string FileIn(string dir) => Path.Combine(dir, Op + "_accessibilityConfig.xml");

        /// <summary>Every box on the tab set away from its default, as an operator would.</summary>
        private static void ChooseEverythingAwayFromTheDefaults(AccessibilityConfig c)
        {
            c.DoubleTapTolerance = DoubleTapTolerance.Leisurely;
            c.SliceArrowOrder = SliceArrowOrder.BottomToTop;
            c.SliceFieldArrowsTune = true;
            c.SliceOpsFieldArrowsTune = false;
            c.ReclaimStolenForeground = false;
        }

        private static void AssertEverythingAwayFromTheDefaults(AccessibilityConfig c)
        {
            Assert.Equal(DoubleTapTolerance.Leisurely, c.DoubleTapTolerance);
            Assert.Equal(SliceArrowOrder.BottomToTop, c.SliceArrowOrder);
            Assert.True(c.SliceFieldArrowsTune);
            Assert.False(c.SliceOpsFieldArrowsTune);
            Assert.False(c.ReclaimStolenForeground);
        }

        /// <summary>
        /// Which folder each real caller uses, read from the source: both loads
        /// read the settings root under <c>UniqueOpName</c>, and the dialog's
        /// commit saves through <see cref="AccessibilityConfig.SaveChoices"/>,
        /// with no folder of its own. Nothing in the application calls the
        /// folder-taking <see cref="AccessibilityConfig.Save"/> any more.
        /// </summary>
        [Fact]
        public void The_dialog_saves_through_the_load_and_both_loads_read_the_settings_root()
        {
            string repo = RepoRoot();
            string events = File.ReadAllText(Path.Combine(repo, "ApplicationEvents.vb"));
            string globals = File.ReadAllText(Path.Combine(repo, "globals.vb"));
            string dialog = File.ReadAllText(Path.Combine(repo, "JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"));

            Assert.Contains("Radios.AccessibilityConfig.Load(BaseConfigDir, opName)", events);
            Assert.Contains("Dim opName = PersonalData.UniqueOpName(CurrentOp)", events);
            Assert.Contains("Radios.AccessibilityConfig.Load(BaseConfigDir, PersonalData.UniqueOpName(CurrentOp))", globals);

            Assert.Contains("AccessibilityConfig.SaveChoices();", dialog);
            Assert.DoesNotMatch(new Regex(@"AccessibilityConfig\.Current\.Save\("), dialog);

            foreach (var file in Directory.EnumerateFiles(Path.Combine(repo, "JJFlexWpf"), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                Assert.DoesNotMatch(new Regex(@"AccessibilityConfig\.Current\.Save\("), File.ReadAllText(file));
            }
        }

        /// <summary>
        /// The ordinary path Codex reproduced: launch (the launch load), change
        /// every box, OK (the dialog's save), connect (the first connect's
        /// load), restart (the launch load again). Every choice is still there
        /// at each step, and nothing was written into the Radios folder.
        /// </summary>
        [Fact]
        public void Choices_made_in_Settings_survive_the_first_connect_and_a_restart()
        {
            AccessibilityConfig.Load(_root, Op);                        // launch
            ChooseEverythingAwayFromTheDefaults(AccessibilityConfig.Current);
            AccessibilityConfig.SaveChoices();                          // Settings, OK

            AssertEverythingAwayFromTheDefaults(AccessibilityConfig.Load(_root, Op));   // first connect

            AccessibilityConfig.ForgetHomeForTests();                   // restart
            AssertEverythingAwayFromTheDefaults(AccessibilityConfig.Load(_root, Op));
            AssertEverythingAwayFromTheDefaults(AccessibilityConfig.Current);

            // A second session changes its mind about one box: that too
            // survives the next restart.
            AccessibilityConfig.Current.SliceOpsFieldArrowsTune = true;
            AccessibilityConfig.SaveChoices();
            AccessibilityConfig.ForgetHomeForTests();
            Assert.True(AccessibilityConfig.Load(_root, Op).SliceOpsFieldArrowsTune);

            Assert.False(File.Exists(FileIn(RadiosFolder)), "the dialog's save went to the Radios folder again");
        }

        /// <summary>
        /// The second check's other path: Settings committed before the file
        /// has been read this session. The choices are held, win over the file
        /// at the load (the first connect), are written then, and survive a
        /// restart.
        /// </summary>
        [Fact]
        public void Choices_made_before_the_file_is_first_read_survive_that_load_and_a_restart()
        {
            new AccessibilityConfig { HoldsEverySetting = true }.Save(_root, Op);   // an earlier session's file

            ChooseEverythingAwayFromTheDefaults(AccessibilityConfig.Current);
            AccessibilityConfig.SaveChoices();                          // OK before any load
            Assert.False(File.Exists(FileIn(RadiosFolder)));

            AssertEverythingAwayFromTheDefaults(AccessibilityConfig.Load(_root, Op));
            AccessibilityConfig.ForgetHomeForTests();
            AssertEverythingAwayFromTheDefaults(AccessibilityConfig.Load(_root, Op));
        }

        /// <summary>
        /// An operator's choices saved by the old path live only in the Radios
        /// folder (the operator's own tree on 2026-11-09 held only that copy).
        /// The first load brings them home and writes the root, marked; from
        /// then on the root is read, and a later choice is not overridden by
        /// the stale copy.
        /// </summary>
        [Fact]
        public void Choices_the_old_save_left_in_the_Radios_folder_are_kept_and_brought_home_once()
        {
            var old = new AccessibilityConfig();
            ChooseEverythingAwayFromTheDefaults(old);
            old.Save(RadiosFolder, Op);

            AssertEverythingAwayFromTheDefaults(AccessibilityConfig.Load(_root, Op));
            Assert.True(File.Exists(FileIn(_root)), "the merged settings were not written to the root");
            Assert.True(File.Exists(FileIn(RadiosFolder)), "the old copy must be left where it was, not deleted");

            // A later choice, then a restart: the root wins over the stale copy.
            AccessibilityConfig.Current.SliceFieldArrowsTune = false;
            AccessibilityConfig.SaveChoices();
            AccessibilityConfig.ForgetHomeForTests();
            var next = AccessibilityConfig.Load(_root, Op);
            Assert.False(next.SliceFieldArrowsTune);
            Assert.Equal(SliceArrowOrder.BottomToTop, next.SliceArrowOrder);
        }

        /// <summary>
        /// Both places hold something from before the repair: the root has the
        /// mode cycle (chosen with no radio, which only ever reached the root)
        /// over defaults, and the Radios copy has the rest. The merge keeps
        /// both: the cycle from the root, everything else from the Radios copy.
        /// </summary>
        [Fact]
        public void An_old_root_keeps_its_mode_cycle_and_takes_everything_else_from_the_Radios_copy()
        {
            new AccessibilityConfig { ModeCycleModes = new[] { "CW", "USB" } }.Save(_root, Op);
            var old = new AccessibilityConfig { ModeCycleModes = new[] { "LSB" } };
            ChooseEverythingAwayFromTheDefaults(old);
            old.Save(RadiosFolder, Op);

            var loaded = AccessibilityConfig.Load(_root, Op);
            AssertEverythingAwayFromTheDefaults(loaded);
            Assert.Equal(new[] { "CW", "USB" }, loaded.ModeCycleModes);

            AccessibilityConfig.ForgetHomeForTests();
            var again = AccessibilityConfig.Load(_root, Op);
            AssertEverythingAwayFromTheDefaults(again);
            Assert.Equal(new[] { "CW", "USB" }, again.ModeCycleModes);
        }

        /// <summary>
        /// The mode cycle's own path still works beside the whole save: Settings
        /// commits the cycle, then the whole object, into the same file.
        /// </summary>
        [Fact]
        public void The_mode_cycle_and_the_whole_save_land_in_the_same_file()
        {
            AccessibilityConfig.Load(_root, Op);
            AccessibilityConfig.ChooseModeCycle(new[] { "AM", "FM" });
            AccessibilityConfig.Current.SliceFieldArrowsTune = true;
            AccessibilityConfig.SaveChoices();

            AccessibilityConfig.ForgetHomeForTests();
            var next = AccessibilityConfig.Load(_root, Op);
            Assert.Equal(new[] { "AM", "FM" }, next.ModeCycleModes);
            Assert.True(next.SliceFieldArrowsTune);
        }

        // ---- #517 repair two (Codex, 2026-11-10): switching operators ----

        private const string OpB = "N0CALL_Second_Operator";

        private string FileFor(string op) => Path.Combine(_root, op + "_accessibilityConfig.xml");

        /// <summary>
        /// Codex's trigger, followed exactly: start as operator A; Radio,
        /// Operators, select B; Settings, Accessibility, uncheck the Slice
        /// operations box; OK; no connect between. Settings must show B's
        /// choices and write B's file, and A's file must be left as A saved it.
        /// Then a restart as each operator reads that operator's own choices.
        /// </summary>
        /// <remarks>
        /// The operator change is <see cref="AccessibilityConfig.FollowOperator"/>
        /// with the settings root and the new operator's name, which is what the
        /// application's operator change handler calls (pinned from the source
        /// by <see cref="An_operator_switch_is_wired_to_follow_the_operator_with_or_without_a_radio"/>).
        /// The repair-one tests each used one operator and could not see this.
        /// </remarks>
        [Fact]
        public void After_an_operator_switch_Settings_reads_and_saves_the_new_operators_file_and_leaves_the_previous_one_alone()
        {
            // B saved choices of their own in an earlier session.
            new AccessibilityConfig
            {
                HoldsEverySetting = true,
                DoubleTapTolerance = DoubleTapTolerance.Relaxed,
                SliceArrowOrder = SliceArrowOrder.BottomToTop,
            }.Save(_root, OpB);

            // Launch as A; A makes and saves choices of their own.
            AccessibilityConfig.Load(_root, Op);
            AccessibilityConfig.Current.DoubleTapTolerance = DoubleTapTolerance.Quick;
            AccessibilityConfig.Current.SliceOpsFieldArrowsTune = true;
            AccessibilityConfig.SaveChoices();
            string aFileBefore = File.ReadAllText(FileFor(Op));

            // Radio, Operators: B.
            AccessibilityConfig.FollowOperator(_root, OpB);

            // Settings opens on B's choices, not A's.
            Assert.Equal(DoubleTapTolerance.Relaxed, AccessibilityConfig.Current.DoubleTapTolerance);
            Assert.Equal(SliceArrowOrder.BottomToTop, AccessibilityConfig.Current.SliceArrowOrder);
            Assert.True(AccessibilityConfig.Current.SliceOpsFieldArrowsTune);

            // B unchecks the Slice operations box; OK.
            AccessibilityConfig.Current.SliceOpsFieldArrowsTune = false;
            AccessibilityConfig.SaveChoices();

            Assert.Equal(aFileBefore, File.ReadAllText(FileFor(Op)));   // A's file untouched

            // Back to A in the same session: A's choices again.
            AccessibilityConfig.FollowOperator(_root, Op);
            Assert.Equal(DoubleTapTolerance.Quick, AccessibilityConfig.Current.DoubleTapTolerance);
            Assert.True(AccessibilityConfig.Current.SliceOpsFieldArrowsTune);

            // Restart as A, then as B: each reads their own.
            AccessibilityConfig.ForgetHomeForTests();
            var a = AccessibilityConfig.Load(_root, Op);
            Assert.Equal(DoubleTapTolerance.Quick, a.DoubleTapTolerance);
            Assert.Equal(SliceArrowOrder.TopToBottom, a.SliceArrowOrder);
            Assert.True(a.SliceOpsFieldArrowsTune);

            AccessibilityConfig.ForgetHomeForTests();
            var b = AccessibilityConfig.Load(_root, OpB);
            Assert.Equal(DoubleTapTolerance.Relaxed, b.DoubleTapTolerance);
            Assert.Equal(SliceArrowOrder.BottomToTop, b.SliceArrowOrder);
            Assert.False(b.SliceOpsFieldArrowsTune);
        }

        /// <summary>
        /// The same event also fires for an edit of the current operator. When
        /// that leaves the operator's name as it was, nothing is re-read: the
        /// object every reader holds is kept, choices and all.
        /// </summary>
        [Fact]
        public void The_operator_change_event_for_the_operator_already_in_use_changes_nothing()
        {
            var loaded = AccessibilityConfig.Load(_root, Op);
            loaded.SliceFieldArrowsTune = true;   // live, as Settings' Apply leaves it
            Assert.Same(loaded, AccessibilityConfig.FollowOperator(_root, Op));
            Assert.Same(loaded, AccessibilityConfig.Current);
            Assert.True(AccessibilityConfig.Current.SliceFieldArrowsTune);
        }

        /// <summary>
        /// A switch before any file has been read this session (no operator at
        /// launch): choices committed then belong to no operator yet, and the
        /// operator who becomes current gets them, written into their file, as
        /// the first connect would have done. They do not go on to a later one.
        /// </summary>
        [Fact]
        public void Choices_held_before_any_operator_go_to_the_first_operator_followed_and_no_further()
        {
            AccessibilityConfig.Current.SliceFieldArrowsTune = true;
            AccessibilityConfig.SaveChoices();                    // held: no file read yet

            Assert.True(AccessibilityConfig.FollowOperator(_root, Op).SliceFieldArrowsTune);
            Assert.True(File.Exists(FileFor(Op)));

            Assert.False(AccessibilityConfig.FollowOperator(_root, OpB).SliceFieldArrowsTune);
            Assert.False(File.Exists(FileFor(OpB)), "B's file was written though B chose nothing");
        }

        /// <summary>
        /// The application's operator change handler calls
        /// <see cref="AccessibilityConfig.FollowOperator"/> with the settings
        /// root and the new current operator's name, and it is wired at
        /// startup, after <c>InitializeApplication</c> and outside the
        /// connect-time wiring, so it works with no radio. Radio, Operators'
        /// switch raises that event. Read from the source: the VB application
        /// cannot be run here.
        /// </summary>
        [Fact]
        public void An_operator_switch_is_wired_to_follow_the_operator_with_or_without_a_radio()
        {
            string repo = RepoRoot();
            string events = File.ReadAllText(Path.Combine(repo, "ApplicationEvents.vb")).Replace("\r\n", "\n");
            string lister = File.ReadAllText(Path.Combine(repo, "Lister.vb")).Replace("\r\n", "\n");
            string personal = File.ReadAllText(Path.Combine(repo, "PersonalData.vb")).Replace("\r\n", "\n");

            int init = events.IndexOf("            InitializeApplication()\n", StringComparison.Ordinal);
            int follow = events.IndexOf(
                "Radios.AccessibilityConfig.FollowOperator(BaseConfigDir, PersonalData.UniqueOpName(CurrentOp))",
                StringComparison.Ordinal);
            int wire = events.LastIndexOf("AddHandler Operators.ConfigEvent,", follow, StringComparison.Ordinal);
            int connectWiring = events.IndexOf("WpfMainWindow.FreqOutHandlersWireCallback = Sub(handlers)", StringComparison.Ordinal);
            Assert.True(init > 0 && follow > init, "FollowOperator must be wired after InitializeApplication");
            Assert.True(wire > init && wire < follow && follow - wire < 300,
                "FollowOperator must run in an Operators.ConfigEvent handler");
            Assert.True(connectWiring > follow, "FollowOperator must be wired at startup, not in the connect-time wiring");
            Assert.Equal(1, Regex.Matches(events, Regex.Escape("AccessibilityConfig.FollowOperator(")).Count);

            // Radio, Operators' switch raises the event the handler hears.
            Assert.Matches(new Regex(@"SetCurrentOp\(Operators\(e\.Index\), e\.Index\)[\s\S]{0,200}Operators\.reportEvent\(\)"), lister);
            Assert.Matches(new Regex(@"Sub reportEvent\(\)\s*\n\s*RaiseEvent ConfigEvent\("), personal);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }
    }
}
